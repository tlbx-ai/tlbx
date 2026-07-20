import { icon } from '../../constants';
import { getTerminalSizeControl, hasTerminalSizeControl } from '../../stores';
import { requestTerminalSizeControl } from '../comms';
import { t } from '../i18n';
import { createLogger } from '../logging';

const log = createLogger('terminalSizeControl');
type FitOwnedTerminal = (sessionId: string, container: HTMLElement) => void;

export function createScalingOverlay(
  container: HTMLElement,
  ownsSize: boolean,
  fitOwnedTerminal: FitOwnedTerminal,
): HTMLButtonElement {
  const overlay = document.createElement('button');
  overlay.className = 'scaled-overlay';
  overlay.type = 'button';
  overlay.addEventListener('click', () => {
    void handleScalingOverlayClick(overlay, container, fitOwnedTerminal);
  });
  container.appendChild(overlay);
  positionScalingOverlay(overlay, ownsSize, overlay.innerText);
  return overlay;
}

async function handleScalingOverlayClick(
  overlay: HTMLButtonElement,
  container: HTMLElement,
  fitOwnedTerminal: FitOwnedTerminal,
): Promise<void> {
  const sessionId = getTerminalSessionId(container);
  if (!sessionId) return;
  if (hasTerminalSizeControl(sessionId)) {
    fitOwnedTerminal(sessionId, container);
    return;
  }

  overlay.classList.add('claiming');
  overlay.disabled = true;
  try {
    const result = await requestTerminalSizeControl(sessionId, true);
    if (result.status.isOwner) fitOwnedTerminal(sessionId, container);
  } catch (error: unknown) {
    log.warn(() => `Failed to claim terminal size control: ${String(error)}`);
  } finally {
    overlay.classList.remove('claiming');
    overlay.disabled = false;
  }
}

function getTerminalSessionId(container: HTMLElement): string | null {
  const prefix = 'terminal-';
  return container.id.startsWith(prefix) ? container.id.slice(prefix.length) || null : null;
}

export function positionScalingOverlay(
  overlay: HTMLButtonElement,
  ownsSize: boolean,
  label: string,
): void {
  const title = ownsSize ? t('terminal.resizeToThisViewport') : t('terminal.continueHere');
  const sessionId = overlay.parentElement ? getTerminalSessionId(overlay.parentElement) : null;
  const ownership = sessionId ? getTerminalSizeControl(sessionId) : undefined;
  const followerTitle = ownership?.hasOwner
    ? t('terminal.sizeControlledElsewhere')
    : t('terminal.sizeControlUnassigned');
  const ownerTransferHint = ownership?.ownerLabel
    ? `${t('terminal.takeControlFrom')} ${escapeOwnerLabel(ownership.ownerLabel)}`
    : t('terminal.continueHereHint');
  overlay.title = title;
  overlay.setAttribute('aria-label', title);
  overlay.innerHTML = ownsSize
    ? `${icon('resize')} <span>${label}</span>`
    : `${icon('resize')}<span class="scaled-overlay-copy"><strong>${followerTitle}</strong><span>${label}</span></span><span class="scaled-overlay-action"><strong>${t('terminal.continueHere')}</strong><span>${ownerTransferHint}</span></span>`;

  const connectionBadgeVisible = isConnectionBadgeVisible();
  overlay.style.bottom = connectionBadgeVisible ? '36px' : '8px';
  placeInAvailableGap(overlay, overlay.parentElement, connectionBadgeVisible);
}

function isConnectionBadgeVisible(): boolean {
  const badge = document.getElementById('connection-status');
  return ['disconnected', 'reconnecting', 'connecting'].some((name) =>
    badge?.classList.contains(name),
  );
}

function escapeOwnerLabel(label: string): string {
  return label
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function placeInAvailableGap(
  overlay: HTMLButtonElement,
  container: HTMLElement | null,
  connectionBadgeVisible: boolean,
): void {
  overlay.classList.remove('terminal-gap-right', 'terminal-gap-bottom');
  overlay.style.top = '';
  if (!container) return;

  const rightGap = readGap(container, '--terminal-gap-right-width');
  const bottomGap = readGap(container, '--terminal-gap-bottom-height');
  const requiredWidth = (overlay.offsetWidth || 300) + 16;
  const requiredHeight = (overlay.offsetHeight || 54) + 16 + (connectionBadgeVisible ? 28 : 0);

  if (rightGap >= requiredWidth) {
    overlay.classList.add('terminal-gap-right');
    overlay.style.top = '8px';
    overlay.style.bottom = '';
  } else if (bottomGap >= requiredHeight) {
    overlay.classList.add('terminal-gap-bottom');
  }
}

function readGap(container: HTMLElement, name: string): number {
  const style = container.style as CSSStyleDeclaration & Record<string, string>;
  const value =
    typeof style.getPropertyValue === 'function' ? style.getPropertyValue(name) : style[name];
  return Number.parseFloat(value || '0') || 0;
}
