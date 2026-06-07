/**
 * Terminal Scaling Module
 *
 * Handles terminal scaling, fitting to screen, and viewport resize handling.
 * Terminals maintain server-side dimensions and are scaled to fit the viewport.
 */

import type { TerminalState } from '../../types';
import {
  TERMINAL_PADDING,
  SCROLLBAR_WIDTH,
  MIN_TERMINAL_COLS,
  MIN_TERMINAL_ROWS,
  MAX_TERMINAL_COLS,
  MAX_TERMINAL_ROWS,
  icon,
} from '../../constants';
import { sessionTerminals, fontsReadyPromise, dom } from '../../state';
import {
  $activeSessionId,
  $currentSettings,
  $isMainBrowser,
  $sessions,
  getSession,
} from '../../stores';
import { throttle } from '../../utils';
import { getCalibrationMeasurement, getCalibrationPromise, focusActiveTerminal } from './manager';
import {
  isTerminalVisible,
  remeasureTerminalCells,
  refreshTerminalRenderer,
} from './presentationRefresh';
import { ADAPTIVE_FOOTER_RESERVED_HEIGHT_CHANGED_EVENT } from '../smartInput/layout';
import { isTerminalViewingScrollback } from './scrollback';
import {
  buildTerminalFontStack,
  DEFAULT_TERMINAL_FONT_WEIGHT,
  DEFAULT_TERMINAL_FONT_WEIGHT_BOLD,
  DEFAULT_TERMINAL_LETTER_SPACING,
  DEFAULT_TERMINAL_LINE_HEIGHT,
  ensureTerminalFontLoaded,
  getConfiguredTerminalFontFamily,
  normalizeTerminalFontWeight,
  normalizeTerminalLetterSpacing,
} from './fontConfig';
import { claimMainBrowser, sendResize } from '../comms';
import { t } from '../i18n';
import { isDevMode } from '../sidebar/voiceSection';
import { getTabBarHeight } from '../sessionTabs';
import { clearTerminalGapFillers, updateTerminalGapFillers } from './terminalGapFillers';
import {
  isMobileTerminalViewport,
  observeMobileVerticalViewportChange,
  rememberCurrentMobileViewportSnapshot,
  setMobileVerticalStability,
  shouldPreserveMobileTerminalRows,
  syncMobileVerticalStableTerminals,
} from './mobileVerticalStability';

const SCALE_TOLERANCE = 0.97;
const MAX_TRANSIENT_FIT_RETRIES = 2;
const MOBILE_DENSE_MIN_COLS = 100;
type MeasurementSource = 'existing-terminal' | 'calibration' | 'font-probe' | 'xterm-internal';
export { isTerminalViewingScrollback } from './scrollback';

function isMobileDenseTerminalModeEnabled(): boolean {
  return (
    typeof window !== 'undefined' &&
    isMobileTerminalViewport() &&
    (document.body.classList.contains('mobile-dense-terminal-mode') ||
      ($currentSettings.get() as { mobileDenseTerminalMode?: boolean } | null)
        ?.mobileDenseTerminalMode === true)
  );
}

export function refreshTerminalPresentation(
  _sessionId: string,
  providedState?: TerminalState,
): void {
  const state = providedState ?? sessionTerminals.get(_sessionId);
  if (!state) return;

  if (!state.opened || !isTerminalVisible(state)) {
    state.pendingVisualRefresh = true;
    return;
  }

  state.pendingVisualRefresh = false;

  requestAnimationFrame(() => {
    const currentState = providedState ?? sessionTerminals.get(_sessionId);
    if (!currentState?.opened) return;

    if (!isTerminalVisible(currentState)) {
      currentState.pendingVisualRefresh = true;
      return;
    }

    refreshTerminalRenderer(currentState);
  });
}

/**
 * Get the total width of all visible dock panels.
 * Web preview can coexist with one other dock (commands, git, or file viewer).
 */
function getDockPanelWidth(): number {
  let total = 0;
  for (const id of ['git-dock', 'commands-dock', 'file-viewer-dock', 'web-preview-dock']) {
    const el = document.getElementById(id);
    if (el && !el.classList.contains('hidden')) total += el.offsetWidth;
  }
  return total;
}

/**
 * Get cell dimensions from xterm.js internal render service.
 * These are the true cell sizes unaffected by CSS layout constraints,
 * avoiding circular measurements when the terminal overflows its container.
 */
function getXtermCellDimensions(
  terminal: TerminalState['terminal'],
): { cellWidth: number; cellHeight: number } | null {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const core = terminal as Record<string, any>;
  // eslint-disable-next-line @typescript-eslint/no-unsafe-member-access
  const dims = core._core?._renderService?.dimensions?.css?.cell as
    | { width: number; height: number }
    | undefined;
  if (!dims || dims.width < 1 || dims.height < 1) return null;
  return { cellWidth: dims.width, cellHeight: dims.height };
}

function measureTerminalCellDimensions(
  state: Pick<TerminalState, 'terminal' | 'container'>,
): { cellWidth: number; cellHeight: number } | null {
  const xtermDims = getXtermCellDimensions(state.terminal);
  if (xtermDims) return xtermDims;

  const screen = state.container.querySelector<HTMLElement>('.xterm-screen');
  const terminalCols = state.terminal.cols;
  const terminalRows = state.terminal.rows;
  if (!screen || terminalCols <= 0 || terminalRows <= 0) {
    return null;
  }

  const cellWidth = screen.offsetWidth / terminalCols;
  const cellHeight = screen.offsetHeight / terminalRows;
  if (cellWidth < 1 || cellHeight < 1) {
    return null;
  }

  return { cellWidth, cellHeight };
}

function calculateOptimalDimensionsForViewport(
  state: Pick<TerminalState, 'terminal' | 'container'>,
  container: HTMLElement,
  isLayoutPane: boolean,
): { cols: number; rows: number } | null {
  const cellDims = measureTerminalCellDimensions(state);
  if (!cellDims) return null;

  const rect = container.getBoundingClientRect();
  const tabBarH = isLayoutPane ? 0 : getTabBarHeight();
  const dockWidth = isLayoutPane ? 0 : getDockPanelWidth();
  const availWidth = rect.width - TERMINAL_PADDING - SCROLLBAR_WIDTH - dockWidth;
  const availHeight = rect.height - TERMINAL_PADDING - tabBarH;

  if (availWidth <= 0 || availHeight <= 0) {
    return null;
  }

  let cols = Math.floor(availWidth / cellDims.cellWidth);
  let rows = Math.floor(availHeight / cellDims.cellHeight);
  const minCols = isMobileDenseTerminalModeEnabled() ? MOBILE_DENSE_MIN_COLS : MIN_TERMINAL_COLS;
  cols = Math.max(minCols, Math.min(cols, MAX_TERMINAL_COLS));
  rows = Math.max(MIN_TERMINAL_ROWS, Math.min(rows, MAX_TERMINAL_ROWS));
  return { cols, rows };
}

export function getTerminalViewportMismatch(
  state: Pick<TerminalState, 'terminal' | 'container'>,
): { optimalCols: number; optimalRows: number; isTooLarge: boolean; isTooSmall: boolean } | null {
  const layoutPane = state.container.closest<HTMLElement>('.layout-leaf');
  const viewportContainer = layoutPane ?? dom.terminalsArea;
  if (!viewportContainer) return null;

  const optimal = calculateOptimalDimensionsForViewport(state, viewportContainer, !!layoutPane);
  if (!optimal) {
    return null;
  }

  return {
    optimalCols: optimal.cols,
    optimalRows: optimal.rows,
    isTooLarge: state.terminal.cols > optimal.cols || state.terminal.rows > optimal.rows,
    isTooSmall: state.terminal.cols < optimal.cols || state.terminal.rows < optimal.rows,
  };
}

function logResizeDiagnostics(
  operation: 'create' | 'manual-resize',
  sessionId: string,
  container: HTMLElement,
  fontFamily: string,
  fontSize: number,
  cellWidth: number,
  cellHeight: number,
  measurementSource: MeasurementSource,
  cols: number,
  rows: number,
  state?: TerminalState,
): void {
  const session = getSession(sessionId);
  const containerRect = container.getBoundingClientRect();

  const assumedWidth = cols * cellWidth;
  const assumedHeight = rows * cellHeight;

  let actualWidth = 0;
  let actualHeight = 0;
  let scaleFactor = 1;

  if (state?.opened) {
    const xterm = state.container.querySelector<HTMLElement>('.xterm');
    const screen = state.container.querySelector<HTMLElement>('.xterm-screen');
    if (xterm && screen) {
      actualWidth = screen.offsetWidth;
      actualHeight = screen.offsetHeight;
      const availW = state.container.clientWidth - TERMINAL_PADDING;
      const availH = state.container.clientHeight - TERMINAL_PADDING;
      const scaleX = availW / xterm.offsetWidth;
      const scaleY = availH / xterm.offsetHeight;
      scaleFactor = Math.min(scaleX, scaleY, 1);
    }
  }

  if (localStorage.getItem('latency-overlay-enabled') === 'true') {
    // eslint-disable-next-line no-console
    console.log(
      `[RESIZE DIAG] ${operation}\n` +
        `  Session: "${session?.name ?? sessionId}" (${session?.terminalTitle ?? 'no title'})\n` +
        `  Container: ${containerRect.width.toFixed(0)}×${containerRect.height.toFixed(0)} px\n` +
        `  Font: ${fontFamily}, ${fontSize}px\n` +
        `  Cell size: ${cellWidth.toFixed(2)}×${cellHeight.toFixed(2)} px (from: ${measurementSource})\n` +
        `  Calculated fit: ${cols}×${rows}\n` +
        `  Assumed size: ${assumedWidth.toFixed(0)}×${assumedHeight.toFixed(0)} px\n` +
        `  Actual size: ${actualWidth.toFixed(0)}×${actualHeight.toFixed(0)} px\n` +
        `  Scale factor: ${scaleFactor.toFixed(3)}`,
    );
  }
}

function terminalMatchesMeasurementConfig(
  state: TerminalState,
  expectedFontStack: string,
  fontSize: number,
  lineHeight: number,
  letterSpacing: number,
  fontWeight: string,
  fontWeightBold: string,
  fontFamily: string,
): boolean {
  if (!state.opened || state.terminal.options.fontSize !== fontSize) {
    return false;
  }
  if ((state.terminal.options.lineHeight ?? DEFAULT_TERMINAL_LINE_HEIGHT) !== lineHeight) {
    return false;
  }
  if ((state.terminal.options.letterSpacing ?? DEFAULT_TERMINAL_LETTER_SPACING) !== letterSpacing) {
    return false;
  }
  if (String(state.terminal.options.fontWeight ?? DEFAULT_TERMINAL_FONT_WEIGHT) !== fontWeight) {
    return false;
  }
  if (
    String(state.terminal.options.fontWeightBold ?? DEFAULT_TERMINAL_FONT_WEIGHT_BOLD) !==
    fontWeightBold
  ) {
    return false;
  }

  const terminalFontFamily = state.terminal.options.fontFamily ?? '';
  return terminalFontFamily === expectedFontStack || terminalFontFamily.includes(fontFamily);
}

function measureTerminalDomCellDimensions(
  state: TerminalState,
): { cellWidth: number; cellHeight: number } | null {
  const screen = state.container.querySelector<HTMLElement>('.xterm-screen');
  const cols = state.terminal.cols;
  const rows = state.terminal.rows;
  if (!screen || cols <= 0 || rows <= 0) {
    return null;
  }

  const cellWidth = screen.offsetWidth / cols;
  const cellHeight = screen.offsetHeight / rows;
  return cellWidth >= 1 && cellHeight >= 1 ? { cellWidth, cellHeight } : null;
}

/**
 * Measure actual cell dimensions from an existing terminal.
 * Returns null if no terminal is available or measurements are invalid.
 */
function measureFromExistingTerminal(
  fontSize: number,
  fontFamily: string,
  lineHeight: number,
  letterSpacing: number,
  fontWeight: string,
  fontWeightBold: string,
): { cellWidth: number; cellHeight: number } | null {
  const expectedFontStack = buildTerminalFontStack(fontFamily);

  for (const state of sessionTerminals.values()) {
    if (
      !terminalMatchesMeasurementConfig(
        state,
        expectedFontStack,
        fontSize,
        lineHeight,
        letterSpacing,
        fontWeight,
        fontWeightBold,
        fontFamily,
      )
    ) {
      continue;
    }

    const xtermDims = getXtermCellDimensions(state.terminal);
    if (xtermDims) return xtermDims;
    const domDims = measureTerminalDomCellDimensions(state);
    if (domDims) return domDims;
  }
  return null;
}

/**
 * Measure cell dimensions by creating a temporary element with the terminal font.
 * Used when no existing terminal is available to measure from.
 */
function measureFromFont(
  fontSize: number,
  fontFamily: string,
  lineHeight: number,
  letterSpacing: number,
  fontWeight: string,
): { cellWidth: number; cellHeight: number } {
  const measureEl = document.createElement('span');
  measureEl.style.cssText = `
    position: absolute;
    visibility: hidden;
    font-family: ${buildTerminalFontStack(fontFamily)};
    font-size: ${fontSize}px;
    line-height: ${lineHeight};
    letter-spacing: ${letterSpacing}px;
    font-weight: ${fontWeight};
    white-space: pre;
  `;
  measureEl.textContent = 'WWWWWWWWWW';
  document.body.appendChild(measureEl);

  const cellWidth = measureEl.offsetWidth / 10;
  const cellHeight = measureEl.offsetHeight;

  document.body.removeChild(measureEl);

  return { cellWidth, cellHeight };
}

async function resolveMeasurementSource(
  fontSize: number,
  fontFamily: string,
  lineHeight: number,
  letterSpacing: number,
  fontWeight: string,
  fontWeightBold: string,
): Promise<{ source: MeasurementSource; cellWidth: number; cellHeight: number }> {
  const existingMeasurement = measureFromExistingTerminal(
    fontSize,
    fontFamily,
    lineHeight,
    letterSpacing,
    fontWeight,
    fontWeightBold,
  );
  if (existingMeasurement) {
    return { source: 'existing-terminal', ...existingMeasurement };
  }

  const calibrationPromise = getCalibrationPromise();
  if (calibrationPromise) {
    await calibrationPromise;
  }

  const calibration = getCalibrationMeasurement();
  if (
    calibration &&
    calibration.fontSize === fontSize &&
    calibration.lineHeight === lineHeight &&
    calibration.letterSpacing === letterSpacing &&
    calibration.fontWeight === fontWeight &&
    calibration.fontWeightBold === fontWeightBold &&
    (calibration.fontFamily === buildTerminalFontStack(fontFamily) ||
      calibration.fontFamily.includes(fontFamily))
  ) {
    return {
      source: 'calibration',
      cellWidth: calibration.cellWidth,
      cellHeight: calibration.cellHeight,
    };
  }

  if (fontsReadyPromise) {
    await fontsReadyPromise;
  }
  await ensureTerminalFontLoaded(fontFamily, fontSize);
  const fontMeasurement = measureFromFont(
    fontSize,
    fontFamily,
    lineHeight,
    letterSpacing,
    fontWeight,
  );
  return { source: 'font-probe', ...fontMeasurement };
}

/**
 * Calculate optimal terminal dimensions (cols/rows) for the given container.
 * Uses actual font measurements - either from existing terminal or by measuring the font directly.
 *
 * This is the SINGLE source of truth for size calculations used by:
 * - Session creation (main.ts)
 * - Fit-to-screen (scaling.ts)
 */
export async function calculateOptimalDimensions(
  container: HTMLElement,
  fontSize: number,
  fontFamily: string,
  lineHeight: number = DEFAULT_TERMINAL_LINE_HEIGHT,
  letterSpacing: number = DEFAULT_TERMINAL_LETTER_SPACING,
  fontWeight: string = DEFAULT_TERMINAL_FONT_WEIGHT,
  fontWeightBold: string = DEFAULT_TERMINAL_FONT_WEIGHT_BOLD,
  sessionIdForLog?: string,
): Promise<{ cols: number; rows: number } | null> {
  letterSpacing = normalizeTerminalLetterSpacing(letterSpacing);
  fontWeight = normalizeTerminalFontWeight(fontWeight, DEFAULT_TERMINAL_FONT_WEIGHT);
  fontWeightBold = normalizeTerminalFontWeight(fontWeightBold, DEFAULT_TERMINAL_FONT_WEIGHT_BOLD);

  // Allow layout to settle for very small containers before giving up
  let rect = container.getBoundingClientRect();
  if (rect.width < 100 || rect.height < 100) {
    await new Promise((resolve) => requestAnimationFrame(resolve));
    rect = container.getBoundingClientRect();
    if (rect.width < 100 || rect.height < 100) {
      return null;
    }
  }

  const measurement = await resolveMeasurementSource(
    fontSize,
    fontFamily,
    lineHeight,
    letterSpacing,
    fontWeight,
    fontWeightBold,
  );
  const measurementSource = measurement.source;
  const { cellWidth, cellHeight } = measurement;

  // Account for padding, scrollbar width, session tab bar, and dock panels
  const tabBarH = getTabBarHeight();
  const dockWidth = getDockPanelWidth();
  const availWidth = rect.width - TERMINAL_PADDING - SCROLLBAR_WIDTH - dockWidth;
  const availHeight = rect.height - TERMINAL_PADDING - tabBarH;

  const cols = Math.floor(availWidth / cellWidth);
  const rows = Math.floor(availHeight / cellHeight);

  // Clamp to valid range
  const clampedCols = Math.max(MIN_TERMINAL_COLS, Math.min(cols, MAX_TERMINAL_COLS));
  const clampedRows = Math.max(MIN_TERMINAL_ROWS, Math.min(rows, MAX_TERMINAL_ROWS));

  // Reject only if we cannot reach the minimum, not when we exactly equal it.
  if (clampedCols < MIN_TERMINAL_COLS || clampedRows < MIN_TERMINAL_ROWS) {
    return null;
  }

  if (sessionIdForLog) {
    logResizeDiagnostics(
      'create',
      sessionIdForLog,
      container,
      fontFamily,
      fontSize,
      cellWidth,
      cellHeight,
      measurementSource,
      clampedCols,
      clampedRows,
    );
  }

  return { cols: clampedCols, rows: clampedRows };
}

function refreshRendererForMeasurement(
  state: Pick<TerminalState, 'terminal' | 'container' | 'opened'>,
): boolean {
  if (!state.opened || !isTerminalVisible(state)) {
    return false;
  }

  remeasureTerminalCells(state);
  return true;
}

function calculateViewportFitWithMeasurementRecovery(
  state: Pick<TerminalState, 'terminal' | 'container' | 'opened'>,
  container: HTMLElement,
  isLayoutPane: boolean,
): { cols: number; rows: number; cellWidth: number; cellHeight: number } | null {
  const initialFit = calculateViewportFit(state, container, isLayoutPane);
  if (initialFit) {
    return initialFit;
  }

  if (!refreshRendererForMeasurement(state)) {
    return null;
  }

  return calculateViewportFit(state, container, isLayoutPane);
}

function calculateOptimalDimensionsForViewportWithMeasurementRecovery(
  state: Pick<TerminalState, 'terminal' | 'container' | 'opened'>,
  container: HTMLElement,
  isLayoutPane: boolean,
): { cols: number; rows: number } | null {
  const initialOptimal = calculateOptimalDimensionsForViewport(state, container, isLayoutPane);
  if (initialOptimal) {
    return initialOptimal;
  }

  if (!refreshRendererForMeasurement(state)) {
    return null;
  }

  return calculateOptimalDimensionsForViewport(state, container, isLayoutPane);
}

function clearTerminalScaling(state: Pick<TerminalState, 'container'>): void {
  const xterm = state.container.querySelector<HTMLElement>('.xterm');
  if (!xterm) {
    clearTerminalGapFillers(state.container);
    return;
  }

  xterm.style.transform = '';
  xterm.style.transformOrigin = '';
  state.container.classList.remove('scaled');
  clearTerminalGapFillers(state.container);
}

function calculateViewportFit(
  state: Pick<TerminalState, 'terminal' | 'container'>,
  container: HTMLElement,
  isLayoutPane: boolean,
): { cols: number; rows: number; cellWidth: number; cellHeight: number } | null {
  const rect = container.getBoundingClientRect();
  if (rect.width < 100 || rect.height < 100) {
    return null;
  }

  const measuredCellDims = measureTerminalCellDimensions(state);
  const cellWidth = measuredCellDims?.cellWidth ?? null;
  const cellHeight = measuredCellDims?.cellHeight ?? null;
  if (!cellWidth || !cellHeight || cellWidth < 1 || cellHeight < 1) {
    return null;
  }

  const tabBarH = isLayoutPane ? 0 : getTabBarHeight();
  const dockWidth = isLayoutPane ? 0 : getDockPanelWidth();
  const availWidth = rect.width - TERMINAL_PADDING - SCROLLBAR_WIDTH - dockWidth;
  const availHeight = rect.height - TERMINAL_PADDING - tabBarH;
  if (availWidth <= 0 || availHeight <= 0) {
    return null;
  }

  let cols = Math.floor(availWidth / cellWidth);
  let rows = Math.floor(availHeight / cellHeight);
  cols = Math.max(MIN_TERMINAL_COLS, Math.min(cols, MAX_TERMINAL_COLS));
  rows = Math.max(MIN_TERMINAL_ROWS, Math.min(rows, MAX_TERMINAL_ROWS));

  return { cols, rows, cellWidth, cellHeight };
}

function scheduleFitRetry(sessionId: string, retriesRemaining: number): void {
  if (retriesRemaining <= 0) {
    return;
  }

  requestAnimationFrame(() => {
    const state = sessionTerminals.get(sessionId);
    if (!state) {
      return;
    }

    const layoutPane = state.container.closest<HTMLElement>('.layout-leaf');
    if (layoutPane) {
      fitTerminalToContainerInternal(sessionId, layoutPane, retriesRemaining - 1);
    } else {
      fitSessionToScreenInternal(sessionId, retriesRemaining - 1);
    }
  });
}

function removeScalingOverlay(container: HTMLElement): void {
  const overlay = container.querySelector<HTMLElement>('.scaled-overlay');
  if (overlay) overlay.remove();
}

function buildScaledOverlayLabel(
  container: HTMLElement,
  state: TerminalState,
  scale: number,
): string {
  const pct = Math.round(scale * 100);
  const screen = container.querySelector<HTMLElement>('.xterm-screen');
  let diagHtml = '';
  if (isDevMode() && screen) {
    const cols = state.terminal.cols;
    const rows = state.terminal.rows;
    const cellW = (screen.offsetWidth / cols).toFixed(2);
    const cellH = (screen.offsetHeight / rows).toFixed(2);
    const termPx = `${screen.offsetWidth}×${screen.offsetHeight}`;
    const containerPx = `${container.clientWidth}×${container.clientHeight}`;
    const scaleTxt = scale.toPrecision(5);
    diagHtml = `<br><span style="font-size:9pt">Cell: ${cellW}×${cellH}  Term: ${cols}×${rows}  Px: ${termPx}  Container: ${containerPx}  Scale: ${scaleTxt}</span>`;
  }
  const overlayLabel = $isMainBrowser.get()
    ? `${t('terminal.scaledTo')} ${pct}%`
    : `${t('terminal.scaledContent')} (${pct}%) - ${t('terminal.makeReferenceScaleBrowser')}`;
  return `${overlayLabel}${diagHtml}`;
}

/**
 * Fit a session's terminal to the current screen size.
 * This sends a resize request to the server.
 *
 * Uses direct measurement of terminalsArea via getBoundingClientRect() rather than
 * FitAddon's measurement of the terminal container. This avoids timing issues where
 * clearing zoom/scale causes layout to be in flux when measurements occur.
 */
export function fitSessionToScreen(sessionId: string): void {
  fitSessionToScreenInternal(sessionId, MAX_TRANSIENT_FIT_RETRIES);
}

function withTemporarilyVisibleTerminal<T>(state: TerminalState, work: () => T): T {
  const wasHidden = state.container.classList.contains('hidden');
  if (wasHidden) {
    state.container.classList.remove('hidden');
  }

  try {
    return work();
  } finally {
    if (wasHidden) {
      state.container.classList.add('hidden');
    }
  }
}

function resizeTerminalToFit(
  state: TerminalState,
  sessionId: string,
  cols: number,
  rows: number,
): void {
  try {
    if (state.terminal.cols !== cols || state.terminal.rows !== rows) {
      state.terminal.resize(cols, rows);
      sendResize(sessionId, state.terminal.cols, state.terminal.rows);
    }
  } catch {
    // Resize may fail if terminal is disposed
  }
}

function isSoftKeyboardVisible(): boolean {
  return document.body.classList.contains('keyboard-visible');
}

function fitSessionToScreenInternal(sessionId: string, retriesRemaining: number): void {
  const state = sessionTerminals.get(sessionId);
  if (!state) return;

  if (!$isMainBrowser.get()) {
    applyTerminalScaling(sessionId, state);
    return;
  }

  // Capture fontSize for diagnostics
  const fontSize = $currentSettings.get()?.fontSize ?? 14;
  const fontFamily = getConfiguredTerminalFontFamily();

  // Wait for terminal to be opened before fitting
  if (!state.opened) {
    void (fontsReadyPromise ?? Promise.resolve()).then(() => {
      fitSessionToScreen(sessionId);
    });
    return;
  }

  // Clear any existing scaling first
  clearTerminalScaling(state);

  if (!dom.terminalsArea) {
    return;
  }

  const terminalsArea = dom.terminalsArea;
  const fit = withTemporarilyVisibleTerminal(state, () =>
    calculateViewportFitWithMeasurementRecovery(state, terminalsArea, false),
  );
  if (!fit) {
    scheduleFitRetry(sessionId, retriesRemaining);
    return;
  }

  const { cols, rows, cellWidth, cellHeight } = fit;
  const rowsToApply = shouldPreserveMobileTerminalRows(state, cols) ? state.terminal.rows : rows;
  resizeTerminalToFit(state, sessionId, cols, rowsToApply);
  applyTerminalScalingSync(state);
  syncMobileVerticalStableTerminals();

  logResizeDiagnostics(
    'manual-resize',
    sessionId,
    dom.terminalsArea,
    fontFamily,
    fontSize,
    cellWidth,
    cellHeight,
    'existing-terminal',
    cols,
    rowsToApply,
    state,
  );
  if (!isSoftKeyboardVisible()) {
    focusActiveTerminal();
  }
}

/**
 * Fit a terminal to a specific container (e.g., layout pane).
 * Resizes the terminal (cols/rows) and notifies the server.
 * Used when docking terminals into a layout.
 */
export function fitTerminalToContainer(sessionId: string, container: HTMLElement): void {
  fitTerminalToContainerInternal(sessionId, container, MAX_TRANSIENT_FIT_RETRIES);
}

function fitTerminalToContainerInternal(
  sessionId: string,
  container: HTMLElement,
  retriesRemaining: number,
): void {
  const state = sessionTerminals.get(sessionId);
  if (!state || !state.opened) return;

  if (!$isMainBrowser.get()) {
    applyTerminalScaling(sessionId, state);
    return;
  }

  const fit = calculateViewportFitWithMeasurementRecovery(state, container, true);
  if (!fit) {
    scheduleFitRetry(sessionId, retriesRemaining);
    return;
  }

  const { cols, rows } = fit;
  const rowsToApply = shouldPreserveMobileTerminalRows(state, cols) ? state.terminal.rows : rows;

  // Resize terminal and notify server
  try {
    if (state.terminal.cols !== cols || state.terminal.rows !== rowsToApply) {
      state.terminal.resize(cols, rowsToApply);
      state.serverCols = cols;
      state.serverRows = rowsToApply;
      sendResize(sessionId, state.terminal.cols, state.terminal.rows);
    }
  } catch {
    // Resize may fail if terminal is disposed
  }

  if (isMobileDenseTerminalModeEnabled()) {
    applyTerminalScalingSync(state);
  } else {
    // Clear any scaling since we just resized to fit
    clearTerminalScaling(state);
    state.container.querySelector<HTMLElement>('.scaled-overlay')?.remove();
  }
  syncMobileVerticalStableTerminals();
}

/**
 * Apply CSS scaling to a terminal synchronously.
 * Use this when already inside a requestAnimationFrame callback.
 */
export function applyTerminalScalingSync(state: TerminalState): void {
  const context = createTerminalScalingContext(state);
  if (!context) return;
  const { container, xterm, isMainBrowser, scale, hasOptimalSizeMismatch } = context;

  let overlay = container.querySelector<HTMLButtonElement>('.scaled-overlay');
  const ensureOverlay = (): HTMLButtonElement => {
    if (overlay) return overlay;
    overlay = createScalingOverlay(container, isMainBrowser);
    return overlay;
  };
  const resetScaleState = (): void => {
    resetTerminalScaleState(container, xterm);
  };
  const showOverlay = (label: string): void => {
    const el = ensureOverlay();
    positionScalingOverlay(el, isMainBrowser, label);
  };

  if (scale < 1) {
    applyScaledDownTerminalState({
      container,
      xterm,
      state,
      scale,
      isMainBrowser,
      hasOptimalSizeMismatch,
      showOverlay,
      resetScaleState,
    });
    return;
  }

  if (context.shouldApplyUndersizedState) {
    applyUndersizedTerminalState({
      container,
      xterm,
      isMainBrowser,
      viewportMismatchTooSmall: context.viewportMismatchTooSmall,
      showOverlay,
      resetScaleState,
      overlay,
      clearOverlay: () => {
        overlay?.remove();
        overlay = null;
      },
    });
    return;
  }

  applyNaturalFitTerminalState({
    isMainBrowser,
    showOverlay,
    resetScaleState,
    clearOverlay: () => {
      overlay?.remove();
      overlay = null;
    },
  });
  if (isMainBrowser) updateTerminalGapFillers(container, xterm, 1);
}

interface TerminalScalingContext {
  container: HTMLElement;
  xterm: HTMLElement;
  state: TerminalState;
  scale: number;
  isMainBrowser: boolean;
  hasOptimalSizeMismatch: boolean;
  viewportMismatchTooSmall: boolean;
  shouldApplyUndersizedState: boolean;
}

function createTerminalScalingContext(state: TerminalState): TerminalScalingContext | null {
  const container = state.container;
  const xterm = container.querySelector<HTMLElement>('.xterm');
  if (!xterm) {
    return null;
  }

  const viewportMismatch = getTerminalViewportMismatch(state);
  const hasOptimalSizeMismatch = hasTerminalViewportMismatch(viewportMismatch);
  const measurements = measureTerminalScalingGeometry(container, xterm);
  if (!measurements) {
    return null;
  }

  return {
    container,
    xterm,
    state,
    scale: normalizeTerminalScalingFactor(
      measurements.availWidth,
      measurements.availHeight,
      measurements.termWidth,
      measurements.termHeight,
      hasOptimalSizeMismatch,
    ),
    isMainBrowser: $isMainBrowser.get(),
    hasOptimalSizeMismatch,
    viewportMismatchTooSmall: Boolean(viewportMismatch?.isTooSmall),
    shouldApplyUndersizedState: shouldApplyUndersizedTerminalState(
      viewportMismatch,
      measurements.termWidth,
      measurements.termHeight,
      measurements.availWidth,
      measurements.availHeight,
    ),
  };
}

function hasTerminalViewportMismatch(
  mismatch: ReturnType<typeof getTerminalViewportMismatch>,
): boolean {
  return Boolean(mismatch?.isTooLarge || mismatch?.isTooSmall);
}

function measureTerminalScalingGeometry(
  container: HTMLElement,
  xterm: HTMLElement,
): { availWidth: number; availHeight: number; termWidth: number; termHeight: number } | null {
  const availWidth = container.clientWidth - TERMINAL_PADDING;
  const availHeight = container.clientHeight - TERMINAL_PADDING;
  const termWidth = xterm.offsetWidth;
  const termHeight = xterm.offsetHeight;
  if (availWidth <= 0 || availHeight <= 0 || termWidth <= 0 || termHeight <= 0) {
    return null;
  }

  return {
    availWidth,
    availHeight,
    termWidth,
    termHeight,
  };
}

function normalizeTerminalScalingFactor(
  availWidth: number,
  availHeight: number,
  termWidth: number,
  termHeight: number,
  hasOptimalSizeMismatch: boolean,
): number {
  const scale = Math.min(availWidth / termWidth, availHeight / termHeight, 1);
  if (!hasOptimalSizeMismatch && scale > SCALE_TOLERANCE) {
    return 1;
  }

  return scale;
}

function shouldApplyUndersizedTerminalState(
  viewportMismatch: ReturnType<typeof getTerminalViewportMismatch>,
  termWidth: number,
  termHeight: number,
  availWidth: number,
  availHeight: number,
): boolean {
  return (
    Boolean(viewportMismatch?.isTooSmall) ||
    termWidth < availWidth - 2 ||
    termHeight < availHeight - 2
  );
}

function applyScaledDownTerminalState(args: {
  container: HTMLElement;
  xterm: HTMLElement;
  state: TerminalState;
  scale: number;
  isMainBrowser: boolean;
  hasOptimalSizeMismatch: boolean;
  showOverlay: (label: string) => void;
  resetScaleState: () => void;
}): void {
  const { container, xterm, state, scale, isMainBrowser, showOverlay, resetScaleState } = args;
  const scaleMainBrowser = isMainBrowser && isMobileDenseTerminalModeEnabled();
  if (isMainBrowser && !scaleMainBrowser) {
    resetScaleState();
    removeScalingOverlay(container);
    return;
  }

  xterm.style.transform = `scale(${scale})`;
  xterm.style.transformOrigin = 'top left';
  container.classList.add('scaled');
  updateTerminalGapFillers(container, xterm, scale);
  if (!scaleMainBrowser) {
    showOverlay(buildScaledOverlayLabel(container, state, scale));
  } else {
    removeScalingOverlay(container);
  }
}

function applyUndersizedTerminalState(args: {
  container: HTMLElement;
  xterm: HTMLElement;
  isMainBrowser: boolean;
  viewportMismatchTooSmall: boolean;
  showOverlay: (label: string) => void;
  resetScaleState: () => void;
  overlay: HTMLButtonElement | null;
  clearOverlay: () => void;
}): void {
  const {
    container,
    xterm,
    isMainBrowser,
    viewportMismatchTooSmall,
    showOverlay,
    resetScaleState,
    overlay,
    clearOverlay,
  } = args;
  resetScaleState();
  updateTerminalGapFillers(container, xterm, 1);

  if (viewportMismatchTooSmall) {
    if (isMainBrowser) {
      removeScalingOverlay(container);
      return;
    }
    const overlayLabel = $isMainBrowser.get()
      ? t('terminal.sizedForSmallerScreen')
      : `${t('terminal.sizedForSmallerScreen')} - ${t('terminal.makeReferenceScaleBrowser')}`;
    showOverlay(overlayLabel);
    return;
  }

  if (!isMainBrowser) {
    showOverlay(t('terminal.makeReferenceScaleBrowser'));
  } else if (overlay) {
    clearOverlay();
  }
}

function applyNaturalFitTerminalState(args: {
  isMainBrowser: boolean;
  showOverlay: (label: string) => void;
  resetScaleState: () => void;
  clearOverlay: () => void;
}): void {
  const { isMainBrowser, showOverlay, resetScaleState, clearOverlay } = args;
  resetScaleState();

  if (!isMainBrowser) {
    showOverlay(t('terminal.makeReferenceScaleBrowser'));
    return;
  }

  clearOverlay();
}

function createScalingOverlay(container: HTMLElement, isMainBrowser: boolean): HTMLButtonElement {
  const overlay = document.createElement('button');
  overlay.className = 'scaled-overlay';
  overlay.type = 'button';
  overlay.addEventListener('click', () => {
    if (!$isMainBrowser.get()) {
      claimMainBrowser();
      return;
    }
    const sessionId = container.id.replace('terminal-', '');
    if (!sessionId) return;
    const layoutPane = container.closest<HTMLElement>('.layout-leaf');
    if (layoutPane) {
      fitTerminalToContainer(sessionId, layoutPane);
    } else {
      fitSessionToScreen(sessionId);
    }
  });
  container.appendChild(overlay);
  positionScalingOverlay(overlay, isMainBrowser, overlay.innerText);
  return overlay;
}

function positionScalingOverlay(
  overlay: HTMLButtonElement,
  isMainBrowser: boolean,
  label: string,
): void {
  const title = isMainBrowser
    ? t('terminal.resizeToThisViewport')
    : t('terminal.makeReferenceScaleBrowser');
  overlay.title = title;
  overlay.setAttribute('aria-label', title);
  overlay.innerHTML = `${icon('resize')} ${label}`;

  const connBadge = document.getElementById('connection-status');
  const connVisible =
    connBadge &&
    (connBadge.classList.contains('disconnected') ||
      connBadge.classList.contains('reconnecting') ||
      connBadge.classList.contains('connecting'));
  overlay.style.bottom = connVisible ? '36px' : '8px';
}

function resetTerminalScaleState(container: HTMLElement, xterm: HTMLElement): void {
  xterm.style.transform = '';
  xterm.style.transformOrigin = '';
  container.classList.remove('scaled');
  clearTerminalGapFillers(container);
}

/**
 * Apply CSS scaling to a terminal to fit within its container.
 * Scales down terminals that are larger than the available space.
 */
export function applyTerminalScaling(_sessionId: string, state: TerminalState): void {
  if (pendingTerminalScaleStates.has(state)) {
    return;
  }

  pendingTerminalScaleStates.add(state);
  requestAnimationFrame(() => {
    pendingTerminalScaleStates.delete(state);
    if (!state.opened) {
      return;
    }
    applyTerminalScalingSync(state);
  });
}

/**
 * Recalculate scaling for all open terminals (internal, non-debounced)
 */
function rescaleAllTerminalsInternal(): void {
  sessionTerminals.forEach((state, sessionId) => {
    if (state.opened) {
      applyTerminalScaling(sessionId, state);
    }
  });
}

/**
 * Recalculate scaling for all open terminals (throttled for smooth live updates during resize)
 */
export const rescaleAllTerminals = throttle(rescaleAllTerminalsInternal, 16);

/**
 * Rescale terminals immediately (for sidebar collapse/expand)
 */
export function rescaleAllTerminalsImmediate(): void {
  rescaleAllTerminalsInternal();
}

/**
 * Auto-resize all visible terminals to fit their containers.
 * For layout panes, resizes to pane size. For standalone, resizes to screen.
 */
function autoResizeAllTerminalsInternal(): void {
  syncMobileVerticalStableTerminals();

  sessionTerminals.forEach((state, sessionId) => {
    if (!state.opened) return;

    // Never auto-resize while the user is reading scrollback. Keep the server-side
    // size stable and only refresh CSS scaling for the current container.
    if (isTerminalViewingScrollback(state)) {
      applyTerminalScaling(sessionId, state);
      return;
    }

    const layoutPane = state.container.closest<HTMLElement>('.layout-leaf');
    if (layoutPane) {
      fitTerminalToContainer(sessionId, layoutPane);
    } else if (!state.container.classList.contains('hidden')) {
      fitSessionToScreen(sessionId);
    }
  });
}

let autoResizeTimer: number | undefined;
let mainBrowserContainerResizeObserver: ResizeObserver | null = null;
let observedMainBrowserContainer: HTMLElement | null = null;
let footerReserveResizeQueued = false;
const pendingTerminalScaleStates = new WeakSet<TerminalState>();

/**
 * Auto-resize all terminals (debounced 300ms, for window resize events).
 * Only active when $isMainBrowser is true.
 */
export function autoResizeAllTerminals(): void {
  if (autoResizeTimer !== undefined) {
    clearTimeout(autoResizeTimer);
  }
  autoResizeTimer = window.setTimeout(() => {
    autoResizeTimer = undefined;
    autoResizeAllTerminalsInternal();
  }, 300);
}

/**
 * Auto-resize all terminals immediately (for sidebar/layout changes).
 * Only active when $isMainBrowser is true.
 */
export function autoResizeAllTerminalsImmediate(): void {
  autoResizeAllTerminalsInternal();
}

function ensureMainBrowserContainerResizeObserver(): void {
  if (typeof ResizeObserver === 'undefined') {
    return;
  }

  const container = dom.terminalsArea;
  if (!container) {
    return;
  }

  if (!mainBrowserContainerResizeObserver) {
    mainBrowserContainerResizeObserver = new ResizeObserver(() => {
      if ($isMainBrowser.get()) {
        scheduleMainBrowserResize();
      }
    });
  }

  if (observedMainBrowserContainer === container) {
    return;
  }

  mainBrowserContainerResizeObserver.disconnect();
  mainBrowserContainerResizeObserver.observe(container);
  observedMainBrowserContainer = container;
}

function disconnectMainBrowserContainerResizeObserver(): void {
  mainBrowserContainerResizeObserver?.disconnect();
  observedMainBrowserContainer = null;
}

let _mainResizeScheduled = false;

function scheduleMainBrowserResize(): void {
  if (_mainResizeScheduled) return;
  _mainResizeScheduled = true;
  requestAnimationFrame(() => {
    _mainResizeScheduled = false;
    if (!$isMainBrowser.get()) return;
    if (observeMobileVerticalViewportChange()) {
      syncMobileVerticalStableTerminals();
      return;
    }
    autoResizeAllTerminalsImmediate();
  });
}

function scheduleFooterReserveResize(): void {
  if (footerReserveResizeQueued) {
    return;
  }

  footerReserveResizeQueued = true;
  requestAnimationFrame(() => {
    footerReserveResizeQueued = false;
    if (isMobileTerminalViewport()) {
      setMobileVerticalStability(true);
      return;
    }

    if ($isMainBrowser.get()) {
      autoResizeAllTerminalsImmediate();
      return;
    }

    rescaleAllTerminalsImmediate();
  });
}

let foregroundResizeRecoveryScheduled = false;

export function scheduleForegroundResizeRecovery(): void {
  if (foregroundResizeRecoveryScheduled) return;
  foregroundResizeRecoveryScheduled = true;
  requestAnimationFrame(() => {
    requestAnimationFrame(() => {
      foregroundResizeRecoveryScheduled = false;
      if (!$isMainBrowser.get()) return;
      ensureMainBrowserContainerResizeObserver();
      sessionTerminals.forEach((state) => {
        if (!state.opened || !isTerminalVisible(state)) return;
        refreshTerminalRenderer(state);
      });
      periodicResizeCheck();
    });
  });
}

/**
 * Central dock layout change handler.
 * All dock modules call this after opening/closing/resizing a dock panel.
 * Uses rAF coalescing so close+open in the same synchronous block (e.g., dock
 * state restore on session switch) produces only a single resize pass.
 */
let dockChangeScheduled = false;

export function handleDockLayoutChange(): void {
  if (dockChangeScheduled) return;
  dockChangeScheduled = true;
  requestAnimationFrame(() => {
    dockChangeScheduled = false;
    if (isMobileTerminalViewport()) {
      setMobileVerticalStability(true);
      return;
    }

    if ($isMainBrowser.get()) {
      autoResizeAllTerminalsImmediate();
    } else {
      rescaleAllTerminalsImmediate();
    }
  });
}

/** Last periodic resize check result for diagnostics overlay */
let lastPeriodicCheckResult = 'idle';

export function getLastPeriodicCheckResult(): string {
  return lastPeriodicCheckResult;
}

/**
 * Mismatch check: compare current terminal dimensions against what they should be.
 * Only resizes when a real mismatch is found. Does NOT touch focus, transforms,
 * overlays, or any other DOM state — just terminal.resize() + sendResize().
 */
function periodicResizeCheck(): void {
  const sessions = $sessions.get();
  const details: string[] = [];

  const activeId = $activeSessionId.get();

  sessionTerminals.forEach((state, sessionId) => {
    const detail = applyPeriodicTerminalResizeCheck(state, sessionId, activeId, sessions);
    if (detail) details.push(detail);
  });

  lastPeriodicCheckResult = details.length > 0 ? details.join('; ') : 'no change';
}

function applyPeriodicTerminalResizeCheck(
  state: TerminalState,
  sessionId: string,
  activeId: string | null,
  sessions: ReturnType<typeof $sessions.get>,
): string | null {
  if (!state.opened) return null;

  if (isTerminalViewingScrollback(state)) {
    applyTerminalScaling(sessionId, state);
    return null;
  }

  const layoutPane = state.container.closest<HTMLElement>('.layout-leaf');
  if (!layoutPane && sessionId !== activeId) return null;

  const container = layoutPane ?? dom.terminalsArea;
  if (!container) return null;

  const termCols = state.terminal.cols;
  const termRows = state.terminal.rows;
  if (termCols <= 0 || termRows <= 0) return null;

  const optimal = calculateOptimalDimensionsForViewportWithMeasurementRecovery(
    state,
    container,
    !!layoutPane,
  );
  if (!optimal) return null;

  return applyPeriodicTerminalResizeDiff(state, sessionId, sessions, optimal);
}

function applyPeriodicTerminalResizeDiff(
  state: TerminalState,
  sessionId: string,
  sessions: ReturnType<typeof $sessions.get>,
  optimal: { cols: number; rows: number },
): string | null {
  const termCols = state.terminal.cols;
  const termRows = state.terminal.rows;
  const optimalCols = optimal.cols;
  const optimalRows = optimal.rows;

  if (termCols === optimalCols && shouldPreserveMobileTerminalRows(state, optimalCols)) {
    syncMobileVerticalStableTerminals();
    return null;
  }

  if (termCols === optimalCols && termRows === optimalRows) {
    return null;
  }

  const rowsToApply = shouldPreserveMobileTerminalRows(state, optimalCols) ? termRows : optimalRows;
  try {
    state.terminal.resize(optimalCols, rowsToApply);
    state.serverCols = optimalCols;
    state.serverRows = rowsToApply;
    sendResize(sessionId, optimalCols, rowsToApply);
  } catch {
    // terminal may be disposed
  }
  requestAnimationFrame(() => {
    applyTerminalScalingSync(state);
  });

  const session = sessions[sessionId];
  const name = session?.name ?? sessionId.substring(0, 8);
  return `${name}: ${termCols}×${termRows} → ${optimalCols}×${rowsToApply}`;
}

/**
 * Set up resize observer to recalculate scaling when window resizes.
 * Main browser: auto-resize terminals. Follower: CSS scale only.
 */
export function setupResizeObserver(): void {
  window.addEventListener('resize', () => {
    if (observeMobileVerticalViewportChange()) {
      return;
    }

    if ($isMainBrowser.get()) {
      autoResizeAllTerminals();
    } else {
      rescaleAllTerminals();
    }
  });

  window.addEventListener(ADAPTIVE_FOOTER_RESERVED_HEIGHT_CHANGED_EVENT, () => {
    scheduleFooterReserveResize();
  });

  const handleForegroundRecovery = () => {
    if (typeof document !== 'undefined' && document.visibilityState === 'hidden') {
      return;
    }
    scheduleForegroundResizeRecovery();
  };

  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') {
      handleForegroundRecovery();
    }
  });
  window.addEventListener('focus', handleForegroundRecovery);
  window.addEventListener('pageshow', handleForegroundRecovery);

  $isMainBrowser.subscribe((isMain) => {
    if (isMain) {
      requestAnimationFrame(() => {
        rememberCurrentMobileViewportSnapshot();
        ensureMainBrowserContainerResizeObserver();
        autoResizeAllTerminalsImmediate();
      });
    } else {
      disconnectMainBrowserContainerResizeObserver();
      requestAnimationFrame(rescaleAllTerminalsImmediate);
    }
  });
}
