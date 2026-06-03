type TerminalGapFillerPlacement = 'right' | 'bottom' | 'corner';

const TERMINAL_GAP_FILLERS: TerminalGapFillerPlacement[] = ['right', 'bottom', 'corner'];
const gapFillerState = new WeakMap<
  HTMLElement,
  {
    background: string | null;
    contentWidth: string;
    contentHeight: string;
    rightWidth: string;
    bottomHeight: string;
  }
>();

export function updateTerminalGapFillers(
  container: HTMLElement,
  xterm: HTMLElement,
  scale: number,
): void {
  const content = getTerminalGapContentElement(xterm);
  const containerWidth = container.clientWidth;
  const containerHeight = container.clientHeight;
  const { width: measuredContentWidth, height: measuredContentHeight } =
    measureTerminalGapContentSize(content, scale);
  const contentWidth = Math.min(containerWidth, measuredContentWidth);
  const contentHeight = Math.min(containerHeight, measuredContentHeight);
  const rightWidth = Math.max(0, containerWidth - contentWidth);
  const bottomHeight = Math.max(0, containerHeight - contentHeight);
  const previousState = gapFillerState.get(container);
  const nextState = {
    background: resolveTerminalGapBackground(container, xterm),
    contentWidth: formatCssPixelValue(contentWidth),
    contentHeight: formatCssPixelValue(contentHeight),
    rightWidth: formatCssPixelValue(rightWidth),
    bottomHeight: formatCssPixelValue(bottomHeight),
  };

  if (previousState?.background !== nextState.background && nextState.background) {
    setTerminalGapBackgroundValue(container, nextState.background);
  }

  setTerminalGapVariable(container, '--terminal-gap-content-width', nextState.contentWidth);
  setTerminalGapVariable(container, '--terminal-gap-content-height', nextState.contentHeight);
  setTerminalGapVariable(container, '--terminal-gap-right-width', nextState.rightWidth);
  setTerminalGapVariable(container, '--terminal-gap-bottom-height', nextState.bottomHeight);
  gapFillerState.set(container, nextState);

  if (rightWidth > 0 || bottomHeight > 0) {
    ensureTerminalGapFillers(container);
  }
}

function getTerminalGapContentElement(xterm: HTMLElement): HTMLElement {
  const host = xterm as {
    getElementsByClassName?: (className: string) => { item: (index: number) => unknown };
  };
  if (typeof host.getElementsByClassName !== 'function') {
    return xterm;
  }

  const screen = host.getElementsByClassName('xterm-screen').item(0);
  return isTerminalGapContentElement(screen) ? screen : xterm;
}

function isTerminalGapContentElement(value: unknown): value is HTMLElement {
  const candidate = value as Partial<HTMLElement> | null;
  return (
    typeof candidate?.offsetWidth === 'number' &&
    typeof candidate.offsetHeight === 'number' &&
    candidate.offsetWidth > 0 &&
    candidate.offsetHeight > 0
  );
}

function measureTerminalGapContentSize(
  content: HTMLElement,
  scale: number,
): { width: number; height: number } {
  if (typeof content.getBoundingClientRect === 'function') {
    const rect = content.getBoundingClientRect();
    if (rect.width > 0 && rect.height > 0) {
      return {
        width: Math.max(0, rect.width),
        height: Math.max(0, rect.height),
      };
    }
  }

  return {
    width: Math.max(0, content.offsetWidth * scale),
    height: Math.max(0, content.offsetHeight * scale),
  };
}

export function clearTerminalGapFillers(container: HTMLElement): void {
  gapFillerState.delete(container);
  setTerminalGapVariable(container, '--terminal-gap-content-width', '0px');
  setTerminalGapVariable(container, '--terminal-gap-content-height', '0px');
  setTerminalGapVariable(container, '--terminal-gap-right-width', '0px');
  setTerminalGapVariable(container, '--terminal-gap-bottom-height', '0px');
}

function resolveTerminalGapBackground(container: HTMLElement, xterm: HTMLElement): string | null {
  if (typeof getComputedStyle !== 'function') {
    return null;
  }

  const terminalCanvasStack = getTerminalCanvasBackgroundStack(container);
  if (terminalCanvasStack) {
    return terminalCanvasStack;
  }

  const xtermBackground = getElementBackgroundColor(xterm);
  const viewportBackground =
    getElementBackgroundColor(getFirstElementByClassName(xterm, 'xterm-viewport')) ??
    xtermBackground;
  const renderedBackground = getElementBackgroundColor(
    getFirstElementByClassName(xterm, 'xterm-scrollable-element'),
  );
  const layers = [
    renderedBackground,
    renderedBackground,
    viewportBackground,
    xtermBackground,
  ].filter(isPaintedBackground);

  if (layers.length === 0) {
    return null;
  }

  return buildTerminalGapBackground(layers);
}

function getTerminalCanvasBackgroundStack(container: HTMLElement): string | null {
  const value = getComputedStyle(container).getPropertyValue('--terminal-canvas-background-stack');
  const normalized = value.trim();
  return normalized.length > 0 ? normalized : null;
}

function getFirstElementByClassName(host: HTMLElement, className: string): HTMLElement | null {
  const elements = host.getElementsByClassName(className);
  const element = elements.item(0);
  return element instanceof HTMLElement ? element : null;
}

function getElementBackgroundColor(element: HTMLElement | null): string | null {
  if (!element) {
    return null;
  }

  return getComputedStyle(element).backgroundColor;
}

function isPaintedBackground(value: string | null): value is string {
  return Boolean(value && !isTransparentBackgroundColor(value));
}

function isTransparentBackgroundColor(value: string): boolean {
  const normalized = value.trim().toLowerCase();
  if (normalized === 'transparent') {
    return true;
  }

  if (!normalized.startsWith('rgb')) {
    return false;
  }

  const openIndex = normalized.indexOf('(');
  const closeIndex = normalized.lastIndexOf(')');
  if (openIndex < 0 || closeIndex <= openIndex) {
    return false;
  }

  const channels = normalized
    .slice(openIndex + 1, closeIndex)
    .split(',')
    .map((part) => Number.parseFloat(part.trim()));
  if (channels.length < 4) {
    return false;
  }

  return channels[3] === 0;
}

function buildTerminalGapBackground(layers: string[]): string {
  return layers
    .map((layer, index) =>
      index < layers.length - 1 ? `linear-gradient(${layer}, ${layer})` : layer,
    )
    .join(', ');
}

function setTerminalGapBackgroundValue(container: HTMLElement, background: string): void {
  if (getTerminalGapStyleValue(container, '--terminal-gap-background') === background) {
    return;
  }

  if (typeof container.style.setProperty === 'function') {
    container.style.setProperty('--terminal-gap-background', background);
    return;
  }

  (container.style as CSSStyleDeclaration & Record<string, string>)['--terminal-gap-background'] =
    background;
}

function ensureTerminalGapFillers(container: HTMLElement): void {
  if (
    typeof document === 'undefined' ||
    !('createElement' in document) ||
    typeof container.appendChild !== 'function'
  ) {
    return;
  }

  for (const placement of TERMINAL_GAP_FILLERS) {
    const selector = `.terminal-gap-fill-${placement}`;
    if (container.querySelector(selector)) {
      continue;
    }

    const filler = document.createElement('div');
    filler.className = `terminal-gap-fill terminal-gap-fill-${placement}`;
    filler.setAttribute('aria-hidden', 'true');
    container.appendChild(filler);
  }
}

function setTerminalGapVariable(container: HTMLElement, name: string, px: string): void {
  if (getTerminalGapStyleValue(container, name) === px) {
    return;
  }

  if (typeof container.style.setProperty === 'function') {
    container.style.setProperty(name, px);
    return;
  }

  (container.style as CSSStyleDeclaration & Record<string, string>)[name] = px;
}

function getTerminalGapStyleValue(container: HTMLElement, name: string): string {
  if (typeof container.style.getPropertyValue === 'function') {
    return container.style.getPropertyValue(name);
  }

  return (container.style as CSSStyleDeclaration & Record<string, string>)[name] ?? '';
}

function formatCssPixelValue(value: number): string {
  const normalized = Math.max(0, value);
  const rounded = Math.round(normalized);
  if (Math.abs(normalized - rounded) < 0.001) {
    return `${rounded}px`;
  }

  return `${normalized.toFixed(3)}px`;
}
