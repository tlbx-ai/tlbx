/**
 * Latency Overlay Module
 *
 * Floating overlay on the terminal showing real-time diagnostics:
 * input-to-xterm output RTT, server ping, mthost ping, flush delay, and
 * scrollback buffer fill.
 * Toggled via Settings > Diagnostics.
 */

import {
  onOutputRtt,
  offOutputRtt,
  onInputLatencyTrace,
  offInputLatencyTrace,
  measureLatency,
  getLastFlushDelay,
  getLastServerIoRtt,
  setInputLatencyTraceConsumerEnabled,
} from '../comms/muxChannel';
import type { InputLatencyTraceSnapshot } from '../comms/muxChannel';
import { $activeSessionId, hasTerminalSizeControl } from '../../stores';
import { sessionTerminals } from '../../state';
import { TERMINAL_PADDING } from '../../constants';
import { getLastPeriodicCheckResult } from '../terminal/scaling';

let overlayEl: HTMLDivElement | null = null;
let enabled = false;
let currentSessionId: string | null = null;
let unsubscribeSession: (() => void) | null = null;
let pingInterval: ReturnType<typeof setInterval> | null = null;

interface MetricElements {
  outputRtt: HTMLSpanElement;
  inputTrace: HTMLSpanElement;
  inputServer: HTMLSpanElement;
  inputHost: HTMLSpanElement;
  inputPtyOut: HTMLSpanElement;
  inputIpcOut: HTMLSpanElement;
  inputMux: HTMLSpanElement;
  serverRtt: HTMLSpanElement;
  mthostRtt: HTMLSpanElement;
  serverIo: HTMLSpanElement;
  flushDelay: HTMLSpanElement;
  scrollback: HTMLSpanElement;
  cursorVisible: HTMLSpanElement;
  termFocus: HTMLSpanElement;
  cursorPos: HTMLSpanElement;
  scale: HTMLSpanElement;
  containerPx: HTMLSpanElement;
  termColsRows: HTMLSpanElement;
  cellPx: HTMLSpanElement;
  xtermPx: HTMLSpanElement;
  resizeTimer: HTMLSpanElement;
  sizeOwner: HTMLSpanElement;
}

let metricEls: MetricElements | null = null;

export function enableLatencyOverlay(): void {
  if (enabled) return;
  enabled = true;
  onOutputRtt(handleOutputRtt);
  onInputLatencyTrace(handleInputLatencyTrace);
  setInputLatencyTraceConsumerEnabled('overlay', true);
  ensureOverlay();
  attachToActiveSession();
  startPingLoop();
  unsubscribeSession = $activeSessionId.subscribe(() => {
    attachToActiveSession();
    void runPingAndScrollback();
  });
}

export function disableLatencyOverlay(): void {
  if (!enabled) return;
  enabled = false;
  offOutputRtt(handleOutputRtt);
  offInputLatencyTrace(handleInputLatencyTrace);
  setInputLatencyTraceConsumerEnabled('overlay', false);
  stopPingLoop();
  removeOverlay();
  if (unsubscribeSession) {
    unsubscribeSession();
    unsubscribeSession = null;
  }
}

export function isLatencyOverlayEnabled(): boolean {
  return enabled;
}

export function reattachOverlay(): void {
  if (!enabled) return;
  attachToActiveSession();
}

function ensureOverlay(): void {
  if (overlayEl) return;
  overlayEl = document.createElement('div');
  overlayEl.className = 'latency-overlay';

  const rows = [
    { label: 'Out', id: 'outputRtt' },
    { label: 'Trace', id: 'inputTrace' },
    { label: 'SrvIn', id: 'inputServer' },
    { label: 'TtyIn', id: 'inputHost' },
    { label: 'PtyOut', id: 'inputPtyOut' },
    { label: 'IpcOut', id: 'inputIpcOut' },
    { label: 'MuxOut', id: 'inputMux' },
    { label: 'Srv', id: 'serverRtt' },
    { label: 'Host', id: 'mthostRtt' },
    { label: 'I/O', id: 'serverIo' },
    { label: 'Flush', id: 'flushDelay' },
    { label: 'Buf', id: 'scrollback' },
    { label: 'Cur', id: 'cursorVisible' },
    { label: 'Foc', id: 'termFocus' },
    { label: 'CPos', id: 'cursorPos' },
    { label: 'Scale', id: 'scale' },
    { label: 'Cntr', id: 'containerPx' },
    { label: 'Dim', id: 'termColsRows' },
    { label: 'Cell', id: 'cellPx' },
    { label: 'XTrm', id: 'xtermPx' },
    { label: 'RTmr', id: 'resizeTimer' },
    { label: 'Own', id: 'sizeOwner' },
  ] as const;

  const els: Partial<MetricElements> = {};
  for (const row of rows) {
    const line = document.createElement('div');
    line.className = 'latency-overlay-row';
    const label = document.createElement('span');
    label.className = 'latency-overlay-label';
    label.textContent = row.label;
    const value = document.createElement('span');
    value.className = 'latency-overlay-value';
    value.textContent = '—';
    line.appendChild(label);
    line.appendChild(value);
    overlayEl.appendChild(line);
    els[row.id] = value;
  }
  metricEls = els as MetricElements;
}

function removeOverlay(): void {
  if (overlayEl) {
    overlayEl.remove();
    overlayEl = null;
  }
  metricEls = null;
  currentSessionId = null;
}

function attachToActiveSession(): void {
  if (!overlayEl) return;
  const sessionId = $activeSessionId.get();
  if (!sessionId) return;

  if (currentSessionId === sessionId && overlayEl.parentElement) return;

  const state = sessionTerminals.get(sessionId);
  if (!state) return;

  overlayEl.remove();
  state.container.appendChild(overlayEl);
  currentSessionId = sessionId;
}

function startPingLoop(): void {
  stopPingLoop();
  void runPingAndScrollback();
  pingInterval = setInterval(() => {
    void runPingAndScrollback();
  }, 3000);
}

function stopPingLoop(): void {
  if (pingInterval !== null) {
    clearInterval(pingInterval);
    pingInterval = null;
  }
}

async function runPingAndScrollback(): Promise<void> {
  if (!enabled || !metricEls) return;
  const sessionId = $activeSessionId.get();
  if (!sessionId) return;

  updateScrollback(sessionId);
  updateCursorState(sessionId);
  updateScalingMetrics(sessionId);
  updateResizeTimerStatus();

  const result = await measureLatency(sessionId);

  if (result.serverRtt !== null) {
    setMetric(metricEls.serverRtt, result.serverRtt);
  }
  if (result.mthostRtt !== null) {
    setMetric(metricEls.mthostRtt, result.mthostRtt);
  }

  const flushDelay = getLastFlushDelay();
  if (flushDelay !== null) {
    metricEls.flushDelay.textContent = `${flushDelay} ms`;
    applyColor(metricEls.flushDelay, flushDelay < 5 ? 'good' : flushDelay < 50 ? 'warn' : 'bad');
  }

  const serverIo = getLastServerIoRtt();
  if (serverIo !== null && serverIo >= 0) {
    metricEls.serverIo.textContent = `${serverIo} ms`;
    applyColor(metricEls.serverIo, serverIo < 30 ? 'good' : serverIo < 100 ? 'warn' : 'bad');
  }
}

function updateScrollback(sessionId: string): void {
  if (!metricEls) return;
  const state = sessionTerminals.get(sessionId);
  if (!state?.terminal) return;

  const used = state.terminal.buffer.active.length;
  const max = (state.terminal.options.scrollback ?? 10000) + state.terminal.rows;
  const pct = Math.round((used / max) * 100);
  metricEls.scrollback.textContent = `${used}/${max} (${pct}%)`;
  applyColor(metricEls.scrollback, pct < 50 ? 'good' : pct < 80 ? 'warn' : 'bad');
}

function resolveCellMetrics(
  state: typeof sessionTerminals extends Map<string, infer TValue> ? TValue : never,
  screenEl: HTMLElement | null,
  cols: number,
  rows: number,
): string | null {
  const xtermCore = ((state.terminal as unknown as Record<string, unknown>)._core ?? undefined) as
    | { _renderService?: { dimensions?: { css?: { cell?: { width: number; height: number } } } } }
    | undefined;
  const xtermDims = xtermCore?._renderService?.dimensions?.css?.cell;
  if (xtermDims) {
    let cellText = `${xtermDims.width.toFixed(2)}x${xtermDims.height.toFixed(2)}`;
    if (screenEl && cols > 0 && rows > 0) {
      const domW = screenEl.offsetWidth / cols;
      if (Math.abs(domW - xtermDims.width) > 0.1) {
        cellText += ` (DOM: ${domW.toFixed(2)})`;
      }
    }
    return cellText;
  }

  if (screenEl && cols > 0 && rows > 0) {
    const cellW = screenEl.offsetWidth / cols;
    const cellH = screenEl.offsetHeight / rows;
    return `${cellW.toFixed(2)}x${cellH.toFixed(2)}`;
  }

  return null;
}

function updateScaleMetric(
  container: HTMLElement,
  xtermEl: HTMLElement | null,
  containerW: number,
  containerH: number,
): void {
  if (!metricEls || !xtermEl) {
    return;
  }

  const availW = containerW - TERMINAL_PADDING;
  const availH = containerH - TERMINAL_PADDING;
  const scaleX = availW / xtermEl.offsetWidth;
  const scaleY = availH / xtermEl.offsetHeight;
  const scale = Math.min(scaleX, scaleY, 1);
  const isScaled = container.classList.contains('scaled');
  const transform = xtermEl.style.transform;
  metricEls.scale.textContent = `${scale.toFixed(4)} ${isScaled ? '(SCALED)' : '(1:1)'}`;
  if (transform) {
    metricEls.scale.title = transform;
  }
  applyColor(metricEls.scale, scale >= 1 ? 'good' : scale > 0.95 ? 'warn' : 'bad');
}

function updateScalingMetrics(sessionId: string): void {
  if (!metricEls) return;
  const state = sessionTerminals.get(sessionId);
  if (!state?.terminal || !state.opened) return;

  const container = state.container;
  const xtermEl = container.querySelector<HTMLElement>('.xterm');
  const screenEl = container.querySelector<HTMLElement>('.xterm-screen');

  const cols = state.terminal.cols;
  const rows = state.terminal.rows;

  // Container available size (minus padding)
  const containerW = container.clientWidth;
  const containerH = container.clientHeight;
  metricEls.containerPx.textContent = `${containerW}×${containerH}`;

  // Terminal cols×rows and server dims
  const srvCols = state.serverCols;
  const srvRows = state.serverRows;
  metricEls.termColsRows.textContent = `${cols}×${rows} (srv ${srvCols}×${srvRows})`;
  applyColor(metricEls.termColsRows, cols === srvCols && rows === srvRows ? 'good' : 'warn');

  const cellMetrics = resolveCellMetrics(state, screenEl, cols, rows);
  if (cellMetrics) {
    metricEls.cellPx.textContent = cellMetrics;
  }

  // Xterm actual rendered size
  if (xtermEl) {
    metricEls.xtermPx.textContent = `${xtermEl.offsetWidth}×${xtermEl.offsetHeight}`;
  }

  updateScaleMetric(container, xtermEl, containerW, containerH);
}

function updateCursorState(sessionId: string): void {
  if (!metricEls) return;
  const state = sessionTerminals.get(sessionId);
  if (!state?.terminal) return;

  // DECTCEM cursor visibility — read directly from xterm.js internal state
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const core = (state.terminal as Record<string, any>)._core as
    | { coreService?: { decPrivateModes?: { cursorHidden?: boolean } } }
    | undefined;
  const cursorHidden = core?.coreService?.decPrivateModes?.cursorHidden ?? false;
  metricEls.cursorVisible.textContent = cursorHidden ? 'HIDDEN' : 'visible';
  applyColor(metricEls.cursorVisible, cursorHidden ? 'bad' : 'good');

  // Terminal DOM focus
  const xtermEl = state.container.querySelector('.xterm textarea');
  const hasFocus = xtermEl !== null && document.activeElement === xtermEl;
  metricEls.termFocus.textContent = hasFocus ? 'yes' : 'no';
  applyColor(metricEls.termFocus, hasFocus ? 'good' : 'warn');

  // Cursor position from active buffer
  const buf = state.terminal.buffer.active;
  metricEls.cursorPos.textContent = `${buf.cursorX},${buf.cursorY}`;
  applyColor(metricEls.cursorPos, 'good');
}

function updateResizeTimerStatus(): void {
  if (!metricEls) return;
  const result = getLastPeriodicCheckResult();
  metricEls.resizeTimer.textContent = result;
  const isNoChange = result === 'no change' || result === 'idle';
  const isSkipped = result.startsWith('skipped');
  applyColor(metricEls.resizeTimer, isNoChange ? 'good' : isSkipped ? 'warn' : 'bad');

  const activeSessionId = $activeSessionId.get();
  const isMain = activeSessionId ? hasTerminalSizeControl(activeSessionId) : false;
  metricEls.sizeOwner.textContent = isMain ? 'yes' : 'no';
  applyColor(metricEls.sizeOwner, isMain ? 'good' : 'warn');
}

function handleOutputRtt(sessionId: string, rtt: number): void {
  if (!metricEls || !enabled) return;

  const activeId = $activeSessionId.get();
  if (sessionId !== activeId) return;

  if (currentSessionId !== sessionId) {
    attachToActiveSession();
  }

  setMetric(metricEls.outputRtt, rtt);
}

function formatTraceMs(value: number | null): string {
  if (value === null || value < 0 || !Number.isFinite(value)) {
    return '—';
  }

  return value >= 100 ? value.toFixed(0) : value.toFixed(1);
}

function setTraceMetric(el: HTMLSpanElement, text: string, levelMs: number | null): void {
  el.textContent = text;
  if (levelMs === null || levelMs < 0 || !Number.isFinite(levelMs)) {
    el.classList.remove('latency-good', 'latency-warn', 'latency-bad');
    return;
  }

  applyColor(el, levelMs < 30 ? 'good' : levelMs < 100 ? 'warn' : 'bad');
}

function handleInputLatencyTrace(sessionId: string, trace: InputLatencyTraceSnapshot): void {
  if (!metricEls || !enabled) return;

  const activeId = $activeSessionId.get();
  if (sessionId !== activeId) return;

  if (currentSessionId !== sessionId) {
    attachToActiveSession();
  }

  setTraceMetric(
    metricEls.inputTrace,
    `${formatTraceMs(trace.totalToXtermParsedMs)} ms`,
    trace.totalToXtermParsedMs,
  );
  metricEls.inputTrace.title =
    `trace=${trace.traceId} recv=${formatTraceMs(trace.browserToOutputReceiveMs)}ms ` +
    `parse=${formatTraceMs(trace.browserReceiveToXtermParseMs)}ms`;

  const serverText = `${formatTraceMs(trace.serverReceiveToIpcStartMs)}/${formatTraceMs(
    trace.ipcWriteMs,
  )} ms`;
  setTraceMetric(metricEls.inputServer, serverText, trace.serverReceiveToIpcStartMs);
  metricEls.inputServer.title = 'server receive to IPC start / IPC write';

  const hostText = `${formatTraceMs(trace.serverReceiveToMthostReceiveMs)}/${formatTraceMs(
    trace.serverReceiveToPtyWriteDoneMs,
  )} ms`;
  setTraceMetric(metricEls.inputHost, hostText, trace.serverReceiveToPtyWriteDoneMs);
  metricEls.inputHost.title = 'server receive to mthost input receive / PTY write done';

  const ptyOutText = `${formatTraceMs(trace.ptyWriteDoneToPtyOutputReadMs)}/${formatTraceMs(
    trace.ptyOutputReadToMthostIpcEnqueuedMs,
  )} ms`;
  setTraceMetric(metricEls.inputPtyOut, ptyOutText, trace.ptyWriteDoneToPtyOutputReadMs);
  metricEls.inputPtyOut.title =
    'PTY write done to PTY output read / PTY output read to mthost IPC enqueue';

  const ipcOutText = `${formatTraceMs(trace.mthostIpcEnqueuedToWriteDoneMs)}/${formatTraceMs(
    trace.mthostIpcWriteDoneToFlushDoneMs,
  )}/${formatTraceMs(trace.mthostIpcEnqueuedToServerOutputObservedMs)} ms`;
  setTraceMetric(
    metricEls.inputIpcOut,
    ipcOutText,
    trace.mthostIpcEnqueuedToServerOutputObservedMs,
  );
  metricEls.inputIpcOut.title =
    'mthost IPC enqueue to write done / write done to flush done / enqueue to server output observed';

  const muxText = `${formatTraceMs(trace.outputObservedToMuxQueuedMs)}/${formatTraceMs(
    trace.muxQueuedToClientQueuedMs,
  )}/${formatTraceMs(trace.clientQueuedToWsFlushMs)} ms`;
  setTraceMetric(metricEls.inputMux, muxText, trace.clientQueuedToWsFlushMs);
  metricEls.inputMux.title =
    `server output observed->mux queue / mux queue->client queue / client queue->ws flush; ` +
    `server->output=${formatTraceMs(trace.serverReceiveToOutputObservedMs)}ms ` +
    `server->flush=${formatTraceMs(trace.serverReceiveToWsFlushMs)}ms`;
}

function setMetric(el: HTMLSpanElement, ms: number): void {
  el.textContent = `${ms.toFixed(1)} ms`;
  applyColor(el, ms < 30 ? 'good' : ms < 100 ? 'warn' : 'bad');
}

function applyColor(el: HTMLSpanElement, level: 'good' | 'warn' | 'bad'): void {
  el.classList.remove('latency-good', 'latency-warn', 'latency-bad');
  el.classList.add(`latency-${level}`);
}
