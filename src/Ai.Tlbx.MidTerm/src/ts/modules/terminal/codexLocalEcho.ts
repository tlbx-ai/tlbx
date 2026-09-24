import type { Terminal } from '@xterm/xterm';

const MAX_PREVIEW_CHARS = 32;
const PREVIEW_TIMEOUT_MS = 120;

export interface CodexLocalEcho {
  onInput(data: string): void;
  clear(): void;
  dispose(): void;
}

/** A visual hint only. The PTY and xterm buffer remain authoritative. */
export function createCodexLocalEcho(
  terminal: Terminal,
  container: HTMLElement,
  canPreview: () => boolean,
): CodexLocalEcho {
  let overlay: HTMLDivElement | null = null;
  let text = '';
  let originX = -1;
  let originY = -1;
  let timeout: ReturnType<typeof setTimeout> | null = null;
  let outputParsed = false;
  let composing = false;
  let placeholderCovered = false;

  const clear = (): void => {
    if (timeout !== null) {
      clearTimeout(timeout);
      timeout = null;
    }
    overlay?.remove();
    overlay = null;
    text = '';
    originX = -1;
    originY = -1;
    outputParsed = false;
    placeholderCovered = false;
  };

  const cellIsEmpty = (x: number, y: number): boolean => {
    const buffer = terminal.buffer.active;
    const cell = buffer.getLine(buffer.baseY + y)?.getCell(x);
    if (!cell) return false;
    const chars = cell.getChars();
    return chars === '' || chars === ' ';
  };

  const isComposerCursor = (): boolean => {
    const buffer = terminal.buffer.active;
    if (buffer.cursorY < terminal.rows - 8) return false;
    const line = buffer.getLine(buffer.baseY + buffer.cursorY);
    return /^\s*› /.test(line?.translateToString(false, 0, Math.min(buffer.cursorX, 16)) ?? '');
  };

  const isEmptyComposerPlaceholder = (): boolean => {
    const buffer = terminal.buffer.active;
    const line = buffer.getLine(buffer.baseY + buffer.cursorY);
    return line?.translateToString(true).includes('Ask Codex to do anything') ?? false;
  };

  const canShowAtCursor = (): boolean => {
    const buffer = terminal.buffer.active;
    return (
      canPreview() &&
      !composing &&
      document.visibilityState === 'visible' &&
      buffer.viewportY === buffer.baseY &&
      buffer.cursorX >= 0 &&
      buffer.cursorX < terminal.cols &&
      buffer.cursorY >= 0 &&
      buffer.cursorY < terminal.rows &&
      terminal.modes.mouseTrackingMode === 'none' &&
      !terminal.hasSelection() &&
      isComposerCursor()
    );
  };

  const createOverlay = (x: number, y: number, coverPlaceholder: boolean): boolean => {
    const screen = container.querySelector<HTMLElement>('.xterm-screen');
    if (!screen || screen.offsetWidth < terminal.cols || screen.offsetHeight < terminal.rows) {
      return false;
    }
    originX = x;
    originY = y;
    placeholderCovered = coverPlaceholder;
    overlay = document.createElement('div');
    overlay.className = 'tlbx-codex-local-echo';
    overlay.setAttribute('aria-hidden', 'true');
    const cellWidth = screen.offsetWidth / terminal.cols;
    const cellHeight = screen.offsetHeight / terminal.rows;
    Object.assign(overlay.style, {
      position: 'absolute',
      left: `${x * cellWidth}px`,
      top: `${y * cellHeight}px`,
      height: `${cellHeight}px`,
      lineHeight: `${cellHeight}px`,
      maxWidth: `${(terminal.cols - x) * cellWidth}px`,
      minWidth: coverPlaceholder ? `${(terminal.cols - x) * cellWidth}px` : '0',
      overflow: 'hidden',
      whiteSpace: 'pre',
      pointerEvents: 'none',
      zIndex: '20',
      opacity: '1',
      color: terminal.options.theme?.foreground ?? 'var(--text-primary)',
      backgroundColor: coverPlaceholder
        ? (terminal.options.theme?.background ??
          'var(--terminal-canvas-background, var(--bg-terminal))')
        : 'transparent',
      fontFamily: terminal.options.fontFamily,
      fontSize: `${terminal.options.fontSize}px`,
      fontWeight: String(terminal.options.fontWeight),
      fontVariantLigatures: 'none',
    });
    screen.appendChild(overlay);
    return true;
  };

  const canAppendAt = (x: number, y: number, coverPlaceholder: boolean): boolean => {
    const buffer = terminal.buffer.active;
    const cursorMoved = text !== '' && (buffer.cursorX !== originX || buffer.cursorY !== originY);
    return (
      x < terminal.cols &&
      text.length < MAX_PREVIEW_CHARS &&
      !cursorMoved &&
      (cellIsEmpty(x, y) || coverPlaceholder || placeholderCovered)
    );
  };

  const onInput = (data: string): void => {
    const code = data.charCodeAt(0);
    if (data.length !== 1 || code < 0x20 || code > 0x7e || !canShowAtCursor()) {
      clear();
      return;
    }

    const buffer = terminal.buffer.active;
    const x = text ? originX + text.length : buffer.cursorX;
    const y = text ? originY : buffer.cursorY;
    const coverPlaceholder = text === '' && isEmptyComposerPlaceholder();
    if (!canAppendAt(x, y, coverPlaceholder)) {
      clear();
      return;
    }

    if (!overlay && !createOverlay(x, y, coverPlaceholder)) return;
    if (!overlay) return;

    text += data;
    overlay.textContent = text;
    if (timeout !== null) clearTimeout(timeout);
    timeout = setTimeout(clear, PREVIEW_TIMEOUT_MS);
  };

  const parsed = terminal.onWriteParsed(() => {
    if (overlay) outputParsed = true;
  });
  const rendered = terminal.onRender(() => {
    if (outputParsed) clear();
  });
  const resized = terminal.onResize(clear);
  const scrolled = terminal.onScroll(clear);
  const onPaste = (): void => {
    clear();
  };
  const onCompositionStart = (): void => {
    composing = true;
    clear();
  };
  const onCompositionEnd = (): void => {
    composing = false;
  };
  container.addEventListener('paste', onPaste, true);
  container.addEventListener('compositionstart', onCompositionStart, true);
  container.addEventListener('compositionend', onCompositionEnd, true);
  container.addEventListener('focusout', clear);
  window.addEventListener('blur', clear);
  document.addEventListener('visibilitychange', clear);

  return {
    onInput,
    clear,
    dispose: () => {
      clear();
      parsed.dispose();
      rendered.dispose();
      resized.dispose();
      scrolled.dispose();
      container.removeEventListener('paste', onPaste, true);
      container.removeEventListener('compositionstart', onCompositionStart, true);
      container.removeEventListener('compositionend', onCompositionEnd, true);
      container.removeEventListener('focusout', clear);
      window.removeEventListener('blur', clear);
      document.removeEventListener('visibilitychange', clear);
    },
  };
}
