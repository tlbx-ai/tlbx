/**
 * Mobile Picture-in-Picture Module
 *
 * Provides a mobile-first miniature terminal preview using Document PiP.
 * When the PWA is backgrounded, we attempt to open a floating mini window
 * showing the active terminal and flash it when server-backed output heat
 * cools down. This intentionally follows the same PTY/AppServerControl signal used by the
 * sidebar heat strip so replayed browser bytes cannot re-arm PiP heat.
 */

import { ASSET_VERSION, MOBILE_BREAKPOINT, MOBILE_PIP_ACTIVE_CHANGED_EVENT } from '../../constants';
import { sessionTerminals } from '../../state';
import { $activeSessionId, $sessionList } from '../../stores';
import { getDisplayedSessionHeat, getSessionHeat } from '../sidebar/heatIndicator';
import { t } from '../i18n';
import { createLogger } from '../logging';

const log = createLogger('mobilePiP');

const PREVIEW_LINES = 16;
const PREVIEW_COLS = 88;
const PREVIEW_REFRESH_MS = 1000;
const LIVE_HEAT_THRESHOLD = 0.02;
const DISPLAY_HEAT_THRESHOLD = 0.02;
const FLASH_DURATION_MS = 600;

interface DocumentPictureInPictureWindowOptions {
  width?: number;
  height?: number;
  disallowReturnToOpener?: boolean;
  preferInitialWindowPlacement?: boolean;
}

interface DocumentPictureInPictureApi {
  window: Window | null;
  requestWindow(options?: DocumentPictureInPictureWindowOptions): Promise<Window>;
}

interface WindowWithDocumentPictureInPicture extends Window {
  documentPictureInPicture?: DocumentPictureInPictureApi;
}

type HeatTrend = 'idle' | 'up' | 'down' | 'steady';

interface SessionHeatReading {
  liveHeat: number;
  displayedHeat: number;
}

let initialized = false;
let autoPiPDisabled = false;
let pipWindow: Window | null = null;
let pipRoot: HTMLDivElement | null = null;
let pipTitleEl: HTMLDivElement | null = null;
let pipRateEl: HTMLDivElement | null = null;
let pipPreviewEl: HTMLPreElement | null = null;
let previewIntervalId: number | null = null;
let flashTimeoutId: number | null = null;

let trackedSessionId: string | null = null;
let currentHeatReading: SessionHeatReading = { liveHeat: 0, displayedHeat: 0 };
let flashedCurrentCooldown = false;
let heatTrend: HeatTrend = 'idle';

/**
 * Initialize mobile PiP behavior.
 * Safe to call multiple times.
 */
export function initMobilePiP(): void {
  if (initialized) return;
  initialized = true;
  if (!isMobileContext()) return;

  trackedSessionId = $activeSessionId.get();
  resetHeatTracking(trackedSessionId);

  if ('mediaSession' in navigator) {
    try {
      /* eslint-disable @typescript-eslint/no-explicit-any, @typescript-eslint/no-unsafe-call, @typescript-eslint/no-unsafe-member-access -- MediaSession PiP action typing is still incomplete, so this cast is isolated to the feature-detection call site. */
      (navigator.mediaSession as any).setActionHandler('enterpictureinpicture', () => {
        void openPiPIfEligibleAsync();
      });
      /* eslint-enable @typescript-eslint/no-explicit-any, @typescript-eslint/no-unsafe-call, @typescript-eslint/no-unsafe-member-access */
    } catch {
      // Browser doesn't support this action handler — PiP from visibilitychange won't work.
    }
  }

  document.addEventListener('visibilitychange', handleVisibilityChange);
  window.addEventListener('pageshow', handlePageShow);

  $activeSessionId.subscribe((sessionId) => {
    trackedSessionId = sessionId;
    resetHeatTracking(sessionId);
    updatePiPContent();
  });

  $sessionList.subscribe(() => {
    updatePiPContent();
  });
}

export function isMobilePiPActive(): boolean {
  return pipWindow !== null;
}

function handleVisibilityChange(): void {
  if (document.visibilityState === 'hidden') {
    void openPiPIfEligibleAsync();
  } else {
    autoPiPDisabled = false;
    closePiPWindow();
  }
}

function handlePageShow(): void {
  autoPiPDisabled = false;
  closePiPWindow();
}

function getDocumentPiPApi(): DocumentPictureInPictureApi | null {
  const w = window as WindowWithDocumentPictureInPicture;
  return w.documentPictureInPicture ?? null;
}

function isMobileContext(): boolean {
  return (
    window.matchMedia(`(max-width: ${MOBILE_BREAKPOINT}px)`).matches || navigator.maxTouchPoints > 0
  );
}

function shouldOpenPiP(): boolean {
  return isMobileContext();
}

async function openPiPIfEligibleAsync(): Promise<void> {
  if (pipWindow !== null) return;
  if (autoPiPDisabled) return;
  if (!shouldOpenPiP()) return;

  const pipApi = getDocumentPiPApi();
  if (pipApi === null) return;

  try {
    const win = await pipApi.requestWindow({
      width: 430,
      height: 260,
      preferInitialWindowPlacement: true,
    });

    if (document.visibilityState !== 'hidden') {
      win.close();
      return;
    }

    attachPiPWindow(win);
  } catch (error) {
    const name = error instanceof DOMException ? error.name : '';
    if (name === 'NotSupportedError') {
      autoPiPDisabled = true;
    }
    log.info(() => `Mobile PiP failed: ${String(error)}`);
  }
}

function attachPiPWindow(win: Window): void {
  pipWindow = win;
  window.dispatchEvent(new Event(MOBILE_PIP_ACTIVE_CHANGED_EVENT));
  buildPiPDocument(win.document);
  syncPiPTheme(win.document);
  win.addEventListener('pagehide', clearPiPReferences);
  resetHeatTracking($activeSessionId.get());
  startPreviewLoop();
  updatePiPContent();
  applyHeatUi();
}

function buildPiPDocument(doc: Document): void {
  doc.head.innerHTML = '';
  doc.body.innerHTML = '';

  const meta = doc.createElement('meta');
  meta.setAttribute('charset', 'utf-8');
  doc.head.appendChild(meta);

  const viewport = doc.createElement('meta');
  viewport.name = 'viewport';
  viewport.content = 'width=device-width,initial-scale=1';
  doc.head.appendChild(viewport);

  const themeColor = doc.createElement('meta');
  themeColor.name = 'theme-color';
  doc.head.appendChild(themeColor);

  const stylesheet = doc.createElement('link');
  stylesheet.rel = 'stylesheet';
  stylesheet.href = `/css/app.css?v=${ASSET_VERSION}`;
  doc.head.appendChild(stylesheet);

  const style = doc.createElement('style');
  style.textContent = `
    :root {
      color-scheme: light dark;
      font-family: var(--font-mono);
    }

    html, body {
      margin: 0;
      padding: 0;
      height: 100%;
      background: var(--bg-primary);
      color: var(--text-terminal);
    }

    body::before,
    body::after {
      content: none !important;
    }

    .mm-mobile-pip {
      box-sizing: border-box;
      height: 100%;
      width: 100%;
      display: flex;
      flex-direction: column;
      border: 2px solid color-mix(in srgb, var(--border-emphasis) 80%, transparent);
      border-radius: 12px;
      background: linear-gradient(
        180deg,
        color-mix(in srgb, var(--bg-elevated) 88%, var(--bg-terminal)) 0%,
        var(--bg-terminal) 100%
      );
      box-shadow: 0 16px 34px var(--shadow-color-md);
      overflow: hidden;
    }

    .mm-mobile-pip__header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
      padding: 8px 10px;
      border-bottom: 1px solid color-mix(in srgb, var(--border-emphasis) 70%, transparent);
      background: color-mix(in srgb, var(--bg-elevated) 92%, transparent);
      font-size: 12px;
    }

    .mm-mobile-pip__title {
      min-width: 0;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      color: var(--text-primary);
      font-weight: 600;
    }

    .mm-mobile-pip__rate {
      flex-shrink: 0;
      font-variant-numeric: tabular-nums;
      color: var(--text-muted);
    }

    .mm-mobile-pip__preview {
      margin: 0;
      flex: 1;
      padding: 10px;
      line-height: 1.25;
      font-size: 11px;
      overflow: hidden;
      white-space: pre;
      text-overflow: clip;
      color: var(--text-terminal);
    }

    .mm-mobile-pip.heat-up .mm-mobile-pip__rate {
      color: var(--accent-green);
    }

    .mm-mobile-pip.heat-down .mm-mobile-pip__rate {
      color: var(--accent-warning);
    }

    .mm-mobile-pip.heat-idle .mm-mobile-pip__rate {
      color: var(--text-secondary);
    }

    .mm-mobile-pip.cooling-flash {
      animation: mm-mobile-pip-flash 0.6s ease-out;
    }

    @keyframes mm-mobile-pip-flash {
      0% {
        box-shadow: 0 0 0 0 color-mix(in srgb, var(--accent-warning) 90%, transparent);
        border-color: var(--accent-warning);
      }
      100% {
        box-shadow: 0 0 0 12px transparent;
        border-color: color-mix(in srgb, var(--border-emphasis) 80%, transparent);
      }
    }
  `;
  doc.head.appendChild(style);

  const root = doc.createElement('div');
  root.className = 'mm-mobile-pip heat-idle';

  const header = doc.createElement('div');
  header.className = 'mm-mobile-pip__header';

  const title = doc.createElement('div');
  title.className = 'mm-mobile-pip__title';
  title.textContent = t('terminal.noTerminals');

  const rate = doc.createElement('div');
  rate.className = 'mm-mobile-pip__rate';
  rate.textContent = '. idle';

  header.appendChild(title);
  header.appendChild(rate);

  const preview = doc.createElement('pre');
  preview.className = 'mm-mobile-pip__preview';
  preview.textContent = '';

  root.appendChild(header);
  root.appendChild(preview);
  doc.body.appendChild(root);

  pipRoot = root;
  pipTitleEl = title;
  pipRateEl = rate;
  pipPreviewEl = preview;
}

function startPreviewLoop(): void {
  if (previewIntervalId !== null) return;
  previewIntervalId = window.setInterval(() => {
    updatePiPContent();
    onHeatWindowTick();
  }, PREVIEW_REFRESH_MS);
}

function closePiPWindow(): void {
  const win = pipWindow;
  clearPiPReferences();
  if (win !== null && !win.closed) {
    try {
      win.close();
    } catch {
      // Ignored: close can throw when window is already closing.
    }
  }
}

function clearPiPReferences(): void {
  const wasActive = pipWindow !== null;
  if (previewIntervalId !== null) {
    window.clearInterval(previewIntervalId);
    previewIntervalId = null;
  }
  if (flashTimeoutId !== null) {
    window.clearTimeout(flashTimeoutId);
    flashTimeoutId = null;
  }
  pipWindow = null;
  pipRoot = null;
  pipTitleEl = null;
  pipRateEl = null;
  pipPreviewEl = null;
  if (wasActive) {
    window.dispatchEvent(new Event(MOBILE_PIP_ACTIVE_CHANGED_EVENT));
  }
}

function updatePiPContent(): void {
  if (pipWindow !== null) {
    syncPiPTheme(pipWindow.document);
  }

  if (pipTitleEl === null || pipPreviewEl === null) return;

  const activeSessionId = $activeSessionId.get();
  if (activeSessionId === null) {
    pipTitleEl.textContent = t('terminal.noTerminals');
    pipPreviewEl.textContent = '';
    return;
  }

  const session = $sessionList.get().find((s) => s.id === activeSessionId);
  pipTitleEl.textContent =
    session?.name ?? session?.terminalTitle ?? session?.shellType ?? t('session.terminal');
  pipPreviewEl.textContent = buildTerminalPreview(activeSessionId);
}

function syncPiPTheme(doc: Document): void {
  doc.documentElement.style.cssText = document.documentElement.style.cssText;

  const themeColor = doc.querySelector<HTMLMetaElement>('meta[name="theme-color"]');
  if (themeColor !== null) {
    const currentColor = getComputedStyle(document.documentElement)
      .getPropertyValue('--bg-primary')
      .trim();
    if (currentColor.length > 0) {
      themeColor.content = currentColor;
    }
  }
}

function buildTerminalPreview(sessionId: string): string {
  const state = sessionTerminals.get(sessionId);
  if (!state?.terminal) {
    return t('terminal.noTerminals');
  }

  const terminal = state.terminal;
  const buffer = terminal.buffer.active;
  const endLine = buffer.baseY + terminal.rows;
  const startLine = Math.max(0, endLine - PREVIEW_LINES);
  const lines: string[] = [];

  for (let lineIndex = startLine; lineIndex < endLine; lineIndex++) {
    const line = buffer.getLine(lineIndex);
    if (!line) {
      lines.push('');
      continue;
    }
    let text = line.translateToString(true);
    if (text.length > PREVIEW_COLS) {
      text = text.slice(text.length - PREVIEW_COLS);
    }
    lines.push(text);
  }

  return lines.join('\n');
}

function resetHeatTracking(sessionId: string | null): void {
  trackedSessionId = sessionId;
  currentHeatReading = readTrackedSessionHeat(sessionId);
  flashedCurrentCooldown = false;
  heatTrend = resolveHeatTrend(currentHeatReading, currentHeatReading);
  applyHeatUi();
}

function onHeatWindowTick(): void {
  const previousHeat = currentHeatReading;
  currentHeatReading = readTrackedSessionHeat(trackedSessionId);
  heatTrend = resolveHeatTrend(previousHeat, currentHeatReading);

  if (
    heatTrend === 'down' &&
    isLiveHeat(previousHeat.liveHeat) &&
    !flashedCurrentCooldown &&
    pipWindow !== null
  ) {
    flashPiP();
    flashedCurrentCooldown = true;
  }

  if (isLiveHeat(currentHeatReading.liveHeat)) {
    flashedCurrentCooldown = false;
  } else if (!isDisplayedHeat(currentHeatReading.displayedHeat)) {
    flashedCurrentCooldown = false;
  }

  applyHeatUi();
}

function applyHeatUi(): void {
  if (pipRoot === null || pipRateEl === null) return;

  pipRoot.classList.remove('heat-idle', 'heat-up', 'heat-down', 'heat-steady');
  if (heatTrend === 'idle') {
    pipRoot.classList.add('heat-idle');
    pipRateEl.textContent = '. idle';
    return;
  }
  if (heatTrend === 'up') {
    pipRoot.classList.add('heat-up');
    pipRateEl.textContent = '^ live';
    return;
  }
  if (heatTrend === 'down') {
    pipRoot.classList.add('heat-down');
    pipRateEl.textContent = 'v cooling';
    return;
  }
  pipRoot.classList.add('heat-steady');
  pipRateEl.textContent = isLiveHeat(currentHeatReading.liveHeat) ? '- live' : '- warm';
}

function flashPiP(): void {
  if (pipRoot === null) return;

  pipRoot.classList.remove('cooling-flash');
  // eslint-disable-next-line @typescript-eslint/no-meaningless-void-operator -- Reading the layout property intentionally forces synchronous layout.
  void pipRoot.offsetWidth;
  pipRoot.classList.add('cooling-flash');

  if (flashTimeoutId !== null) {
    window.clearTimeout(flashTimeoutId);
  }
  flashTimeoutId = window.setTimeout(() => {
    pipRoot?.classList.remove('cooling-flash');
    flashTimeoutId = null;
  }, FLASH_DURATION_MS);
}

function readTrackedSessionHeat(sessionId: string | null): SessionHeatReading {
  if (!sessionId) {
    return { liveHeat: 0, displayedHeat: 0 };
  }

  return {
    liveHeat: getSessionHeat(sessionId),
    displayedHeat: getDisplayedSessionHeat(sessionId),
  };
}

function isLiveHeat(heat: number): boolean {
  return heat > LIVE_HEAT_THRESHOLD;
}

function isDisplayedHeat(heat: number): boolean {
  return heat > DISPLAY_HEAT_THRESHOLD;
}

function resolveHeatTrend(previous: SessionHeatReading, current: SessionHeatReading): HeatTrend {
  const wasLive = isLiveHeat(previous.liveHeat);
  const isLive = isLiveHeat(current.liveHeat);
  const hasDisplayedHeat = isDisplayedHeat(current.displayedHeat);

  if (isLive) {
    return wasLive ? 'steady' : 'up';
  }

  if (wasLive && hasDisplayedHeat) {
    return 'down';
  }

  if (hasDisplayedHeat) {
    return 'steady';
  }

  return 'idle';
}
