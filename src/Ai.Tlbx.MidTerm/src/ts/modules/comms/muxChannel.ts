/* eslint-disable max-lines -- Mux transport remains the protocol coordinator; hot-path helpers are split out as they become stable. */
/**
 * Mux WebSocket terminal I/O. Output ordering is strict per session, not global;
 * xterm's own WriteBuffer remains the user-visible parse boundary.
 */

import type { TerminalState } from '../../types';
import { handleAuthenticatedWebSocketClose } from '../auth/sessionLifetime';
import { createLogger } from '../logging';
import {
  MUX_HEADER_SIZE,
  MUX_PROTOCOL_VERSION,
  MUX_MIN_COMPATIBLE_VERSION,
  MUX_TYPE_OUTPUT,
  MUX_TYPE_INPUT,
  MUX_TYPE_RESIZE,
  MUX_TYPE_RESYNC,
  MUX_TYPE_BUFFER_REQUEST,
  MUX_TYPE_COMPRESSED_OUTPUT,
  MUX_TYPE_ACTIVE_HINT,
  MUX_TYPE_PING,
  MUX_TYPE_FOREGROUND_CHANGE,
  MUX_TYPE_DATA_LOSS,
  MUX_TYPE_PONG,
  MUX_TYPE_SYNC_COMPLETE,
  MUX_TYPE_VISIBLE_SESSIONS_HINT,
  MUX_TYPE_RECOVERY_BEGIN,
  MUX_TYPE_RECOVERY_END,
  WS_CLOSE_SERVER_SHUTDOWN,
} from '../../constants';
import type { ForegroundChangePayload } from '../../types';
import { handleForegroundChange } from '../process';
import { scanOutputForPaths } from '../terminal/fileLinks';
import {
  hideBurstCursor,
  processCursorVisibilityControls,
  reconcileSynchronizedOutputCursorState,
  scheduleBurstCursorShow,
  shouldHideCursorForOutput,
  showBurstCursor,
} from './cursorVisibility';
import { clearBracketedPasteScanState, scanBracketedPasteMode } from './bracketedPasteScan';
import {
  classifyTerminalFrameSequence,
  maxSequence,
  trimFrameToUnseenSuffix,
} from './terminalFrameTrim';
import * as muxSessionRouting from './muxSessionRouting';
import { resolveMuxDataLossReason } from './muxDataLoss';
import { createMuxInputFrame } from './muxInputFrame';
import { createPrintableInputBurstCoalescer } from './printableInputBurst';
import {
  appendTerminalWriteBatch,
  canAppendTerminalWriteBatch,
  combineTerminalWriteChunks,
  type TerminalOutputDelivery,
  type TerminalWriteBatch,
} from './muxOutputBatch';
import {
  buildResumeCursorQueryValue,
  countLocalTerminals,
  createBufferRequestFrame,
  getResumeSequence,
} from './muxResumeCursor';
import {
  armOutputRttMeasurement as armTrackedOutputRttMeasurement,
  consumeCompletedOutputRtt,
  createOutputRttTracker,
  recordOutputRttInput,
  resetOutputRttTracker,
} from './outputRttTracker';
import {
  clearInputLatencyTraceInFlight,
  handleMuxInputTraceResultFrame,
  maybeSendInputTraceMarker,
  recordInputTraceOutputParsed,
  recordInputTraceOutputReceived,
  resetInputLatencyTraceRuntime,
} from './inputLatencyTrace';
export {
  offInputLatencyTrace,
  onInputLatencyTrace,
  setInputLatencyTraceConsumerEnabled,
  setInputLatencyTracingEnabled,
} from './inputLatencyTrace';
export type { InputLatencyTraceSnapshot } from './inputLatencyTrace';
import {
  parseOutputFrame,
  parseCompressedOutputFrame,
  ReconnectController,
  checkVersionAndReload,
  createWsUrl,
  closeWebSocket,
} from '../../utils';
import { handleStateUpdate } from './stateChannel';
import { getSessions } from '../../api/client';
import { applyTerminalScaling } from '../terminal/scaling';
import { isSharedSessionRoute } from '../share';
import { isHubSessionId } from '../hub/runtime';
import { requestHubBufferRefresh, sendHubInput, sendHubResize } from '../hub/channel';
import { $currentSettings, $isMainBrowser } from '../../stores';
import {
  muxWs,
  sessionTerminals,
  pendingOutputFrames,
  sessionsNeedingResync,
  setMuxWs,
  setServerProtocolVersion,
  setBellNotificationsSuppressed,
  addWsRxBytes,
  addWsTxBytes,
} from '../../state';
import {
  $muxWsConnected,
  $muxHasConnected,
  $activeSessionId,
  $stateWsConnected,
  $dataLossDetected,
} from '../../stores';

const log = createLogger('mux');
const muxReconnect = new ReconnectController();
const textEncoder = new TextEncoder();

// Per-session byte activity callback (used by heat indicator)
type SessionBytesCallback = (sessionId: string, bytes: number) => void;
let _sessionBytesCallback: SessionBytesCallback | null = null;

export function setSessionBytesCallback(cb: SessionBytesCallback): void {
  _sessionBytesCallback = cb;
}

// Heat suppression callback (avoids circular sidebar↔comms dependency)
type SuppressHeatCallback = (durationMs: number) => void;
let _suppressHeatCallback: SuppressHeatCallback | null = null;

export function setSuppressHeatCallback(cb: SuppressHeatCallback): void {
  _suppressHeatCallback = cb;
}

let syncCompleteTimeout: number | null = null;
let syncCompletePending = false;
const REPLAY_HEAT_QUIET_MS = 750;
const RESYNC_HEAT_SUPPRESS_MS = 3000;
const ACTIVE_HINT_REPLAY_MAX_MS = 2500;
const BUFFER_REPLAY_MAX_MS = 12000;

interface BrowserTransportSnapshot {
  receivedSeq: bigint;
  renderedSeq: bigint;
  dataLossCount: number;
  lastDataLossReason: string | null;
  lastReplayReason: string | null;
  lastReplayBytes: number;
  recoveryRequested: number;
  recoveryCoalesced: number;
  recoveryCompleted: number;
  recoveryResetCount: number;
  recoveryGapCount: number;
  recoveryReplayBytes: number;
  lastRecoveryCause: string | null;
}

interface ActiveSessionRecovery {
  generation: number;
  sourceSequenceEndExclusive: bigint;
  endReceived: boolean;
  replayBytes: number;
  resetTerminal: boolean;
  prepared: boolean;
  ready: Promise<void> | null;
  releaseBarrier: () => void;
}

const replaySuppressedSessions = new Map<string, { quietUntilMs: number; hardUntilMs: number }>();
const browserTransportSnapshots = new Map<string, BrowserTransportSnapshot>();
const activeSessionRecoveries = new Map<string, ActiveSessionRecovery>();
const recoveryRequestsInFlight = new Set<string>();
const recoveryFollowupCauses = new Map<string, string>();

function discardSessionRecovery(sessionId: string): void {
  const recovery = activeSessionRecoveries.get(sessionId);
  activeSessionRecoveries.delete(sessionId);
  if (recovery?.ready) {
    recovery.ready = null;
    recovery.releaseBarrier();
  }
}

function discardAllSessionRecoveries(): void {
  [...activeSessionRecoveries.keys()].forEach(discardSessionRecovery);
}
function forEachLocalTerminal(callback: (state: TerminalState, sessionId: string) => void): void {
  sessionTerminals.forEach((state, sessionId) => {
    if (isHubSessionId(sessionId)) {
      return;
    }

    callback(state, sessionId);
  });
}

function beginReplayHeatSuppression(sessionId: string, maxDurationMs = BUFFER_REPLAY_MAX_MS): void {
  const now = Date.now();
  replaySuppressedSessions.set(sessionId, {
    quietUntilMs: now + REPLAY_HEAT_QUIET_MS,
    hardUntilMs: now + maxDurationMs,
  });
}

function removeReconnectFreeze(state: TerminalState): void {
  state.reconnectFreezeOverlay?.remove();
  state.reconnectFreezeOverlay = null;
}

function freezeTerminalDuringReconnect(state: TerminalState): void {
  removeReconnectFreeze(state);

  if (!state.opened || state.container.classList.contains('hidden')) {
    return;
  }

  const containerRect = state.container.getBoundingClientRect();
  if (containerRect.width < 2 || containerRect.height < 2) {
    return;
  }

  const overlay = document.createElement('div');
  overlay.className = 'terminal-reconnect-freeze';
  overlay.setAttribute('aria-hidden', 'true');
  overlay.style.position = 'absolute';
  overlay.style.inset = '0';
  overlay.style.zIndex = '40';
  overlay.style.pointerEvents = 'none';
  overlay.style.overflow = 'hidden';

  const viewport =
    state.container.querySelector<HTMLElement>('.xterm-viewport') ??
    state.container.querySelector<HTMLElement>('.xterm');
  const backgroundColor =
    viewport !== null
      ? getComputedStyle(viewport).backgroundColor
      : getComputedStyle(state.container).backgroundColor;
  overlay.style.background = backgroundColor;
  state.container.appendChild(overlay);
  state.reconnectFreezeOverlay = overlay;
}

function freezeVisibleTerminalsDuringReconnect(): void {
  forEachLocalTerminal((state) => {
    freezeTerminalDuringReconnect(state);
  });
}

function thawReconnectFreeze(): void {
  forEachLocalTerminal((state) => {
    removeReconnectFreeze(state);
  });
}

function shouldRecordHeat(sessionId: string, bytes: number): boolean {
  if (bytes <= 0) return false;

  const now = Date.now();
  const suppression = replaySuppressedSessions.get(sessionId);
  if (suppression !== undefined) {
    if (now <= suppression.quietUntilMs && now <= suppression.hardUntilMs) {
      replaySuppressedSessions.set(sessionId, {
        quietUntilMs: Math.min(now + REPLAY_HEAT_QUIET_MS, suppression.hardUntilMs),
        hardUntilMs: suppression.hardUntilMs,
      });
      return false;
    }
    replaySuppressedSessions.delete(sessionId);
  }

  return true;
}

// \x1b[?2004 as bytes: [0x1b, 0x5b, 0x3f, 0x32, 0x30, 0x30, 0x34]
// Followed by 0x68 ('h') = enable, 0x6c ('l') = disable.
function scanBracketedPaste(data: Uint8Array, sessionId: string): void {
  const mode = scanBracketedPasteMode(data, sessionId);
  if (mode !== null) bracketedPasteState.set(sessionId, mode);
}

const pendingInputQueue: Array<{ sessionId: string; data: string }> = [];

async function refreshSessionList(): Promise<void> {
  try {
    const { data, response } = await getSessions();
    if (!response.ok || !data) return;

    handleStateUpdate(data.sessions);
    log.info(() => `Refreshed session list: ${data.sessions.length} sessions`);
  } catch (e) {
    log.warn(() => `Failed to refresh session list: ${String(e)}`);
  }
}

let pongCallback: ((mode: number, rtt: number) => void) | null = null;

export function sendPing(sessionId: string, mode: 0 | 1): Promise<number> {
  return new Promise((resolve, reject) => {
    if (!muxWs || muxWs.readyState !== WebSocket.OPEN) {
      reject(new Error('WebSocket not connected'));
      return;
    }

    const timestamp = performance.now();
    const timestampBytes = new Float64Array([timestamp]);
    const timestampBuffer = new Uint8Array(timestampBytes.buffer);

    // Frame: [type:1][sessionId:8][mode:1][timestamp:8]
    const frame = new Uint8Array(MUX_HEADER_SIZE + 1 + 8);
    frame[0] = MUX_TYPE_PING;
    encodeSessionId(frame, 1, sessionId);
    frame[MUX_HEADER_SIZE] = mode;
    frame.set(timestampBuffer, MUX_HEADER_SIZE + 1);
    sendFrame(frame);

    const timeout = setTimeout(() => {
      pongCallback = null;
      reject(new Error('Ping timeout'));
    }, 5000);

    pongCallback = (pongMode, rtt) => {
      if (pongMode === mode) {
        clearTimeout(timeout);
        pongCallback = null;
        resolve(rtt);
      }
    };
  });
}

/**
 * Measure both server and mthost RTT for a session.
 */
export async function measureLatency(
  sessionId: string,
): Promise<{ serverRtt: number | null; mthostRtt: number | null }> {
  const result = { serverRtt: null as number | null, mthostRtt: null as number | null };

  try {
    result.serverRtt = await sendPing(sessionId, 0);
  } catch {
    /* timeout or disconnect */
  }

  try {
    result.mthostRtt = await sendPing(sessionId, 1);
  } catch {
    /* timeout or disconnect */
  }

  return result;
}

// =============================================================================
// Input→Output RTT Tracking
// =============================================================================

let lastOutputRtt: number | null = null;
type OutputRttListener = (sessionId: string, rtt: number) => void;
const outputRttListeners = new Set<OutputRttListener>();
// WebSocket receipt is too early to reflect what the user sees. This tracker
// holds the timestamp until xterm finishes parsing the first post-input chunk.
const outputRttTracker = createOutputRttTracker();

let lastFlushDelayMs: number | null = null;
let lastServerIoRttMs: number | null = null;

export function getLastOutputRtt(): number | null {
  return lastOutputRtt;
}

export function getLastFlushDelay(): number | null {
  return lastFlushDelayMs;
}

export function getLastServerIoRtt(): number | null {
  return lastServerIoRttMs;
}

export function onOutputRtt(cb: OutputRttListener | null): void {
  if (cb) {
    outputRttListeners.add(cb);
  }
}

export function offOutputRtt(cb: OutputRttListener): void {
  outputRttListeners.delete(cb);
}

function recordInputTimestamp(sessionId: string): void {
  recordOutputRttInput(outputRttTracker, sessionId, performance.now());
}

function armOutputRttMeasurement(sessionId: string): void {
  // We deliberately move the timestamp only when the first post-input output
  // frame actually arrives. The final RTT is then reported when xterm finishes
  // parsing that first frame, which matches visible terminal latency much more
  // closely than measuring at WebSocket receipt time.
  armTrackedOutputRttMeasurement(outputRttTracker, sessionId);
}

function measureCompletedOutputRtt(sessionId: string): void {
  const rtt = consumeCompletedOutputRtt(outputRttTracker, sessionId, performance.now());
  if (rtt === null) {
    return;
  }

  lastOutputRtt = rtt;
  for (const listener of outputRttListeners) {
    listener(sessionId, lastOutputRtt);
  }
}

// Track last hinted session to avoid redundant hints
let lastHintedSessionId: string | null = null;
let currentVisibleSessionIds: string[] = [];

// =============================================================================
// Per-Session Output Delivery
// =============================================================================
//
// Each session keeps a small owned queue so we can preserve strict in-order
// delivery across async decompression and xterm's async write callback.
//
// We intentionally keep MidTerm's own queue shallow. xterm already preserves
// write order internally, so we only use its async callback for "parsed and
// visible" notifications while periodically yielding the main thread to keep
// input responsive during flood output.

interface OutputFrameItem {
  sessionId: string;
  payload: Uint8Array;
  compressed: boolean;
}

interface SessionOutputQueue {
  items: OutputFrameItem[];
  index: number;
  bytes: number;
  processing: boolean;
}

const MAX_QUEUED_FRAMES_PER_SESSION = 2000;
const MAX_QUEUED_BYTES_PER_SESSION = 4 * 1024 * 1024;
const MAX_PENDING_FRAMES_PER_SESSION = 1000;
const QUEUE_COMPACT_THRESHOLD = 1000;
const OUTPUT_DRAIN_BUDGET_MS = 8;
const MAX_TERMINAL_WRITE_BATCH_BYTES = 64 * 1024;
const MAX_PRINTABLE_INPUT_COALESCING_MS = 200;

const sessionOutputQueues = new Map<string, SessionOutputQueue>();
const scheduledOutputQueues = new Set<string>();
const sessionOutputGenerations = new Map<string, number>();
let nextOutputGeneration = 1;
let yieldToMainChannel: MessageChannel | null = null;
const pendingYieldToMainResolves: Array<() => void> = [];
const printableInputCoalescer = createPrintableInputBurstCoalescer(
  getPrintableInputCoalescingMs,
  sendInputNow,
);

function getPrintableInputCoalescingMs(): number {
  const value = $currentSettings.get()?.terminalInputCoalescingMs;
  return typeof value === 'number' && Number.isFinite(value)
    ? Math.max(0, Math.min(MAX_PRINTABLE_INPUT_COALESCING_MS, value))
    : 0;
}

function compactSessionQueue(queue: SessionOutputQueue): void {
  if (queue.index > 0) {
    queue.items.splice(0, queue.index);
    queue.index = 0;
  }
}

function yieldToMain(): Promise<void> {
  const scheduler = (
    globalThis as typeof globalThis & {
      scheduler?: { yield?: () => Promise<void> };
    }
  ).scheduler;
  if (typeof scheduler?.yield === 'function') {
    return scheduler.yield();
  }

  if (typeof MessageChannel !== 'undefined') {
    if (!yieldToMainChannel) {
      yieldToMainChannel = new MessageChannel();
      yieldToMainChannel.port1.onmessage = () => {
        pendingYieldToMainResolves.shift()?.();
      };
    }

    return new Promise((resolve) => {
      pendingYieldToMainResolves.push(resolve);
      yieldToMainChannel?.port2.postMessage(0);
    });
  }

  return new Promise((resolve) => setTimeout(resolve, 0));
}

function getOrCreateBrowserTransportSnapshot(sessionId: string): BrowserTransportSnapshot {
  let snapshot = browserTransportSnapshots.get(sessionId);
  if (!snapshot) {
    snapshot = {
      receivedSeq: 0n,
      renderedSeq: 0n,
      dataLossCount: 0,
      lastDataLossReason: null,
      lastReplayReason: null,
      lastReplayBytes: 0,
      recoveryRequested: 0,
      recoveryCoalesced: 0,
      recoveryCompleted: 0,
      recoveryResetCount: 0,
      recoveryGapCount: 0,
      recoveryReplayBytes: 0,
      lastRecoveryCause: null,
    };
    browserTransportSnapshots.set(sessionId, snapshot);
  }

  return snapshot;
}

export function getBrowserTransportSnapshot(sessionId: string): BrowserTransportSnapshot | null {
  return browserTransportSnapshots.get(sessionId) ?? null;
}

/** Releases every per-session transport owner when a terminal is destroyed. */
export function forgetMuxSession(sessionId: string): void {
  clearQueuedOutput(sessionId);
  pendingOutputFrames.delete(sessionId);
  sessionsNeedingResync.delete(sessionId);
  replaySuppressedSessions.delete(sessionId);
  browserTransportSnapshots.delete(sessionId);
  discardSessionRecovery(sessionId);
  recoveryRequestsInFlight.delete(sessionId);
  recoveryFollowupCauses.delete(sessionId);
  bracketedPasteState.delete(sessionId);
  clearBracketedPasteScanState(sessionId);
  sessionOutputGenerations.delete(sessionId);
}

function getPendingFrameCount(queue: SessionOutputQueue): number {
  return queue.items.length - queue.index;
}

function getOrCreateSessionQueue(sessionId: string): SessionOutputQueue {
  let queue = sessionOutputQueues.get(sessionId);
  if (!queue) {
    queue = { items: [], index: 0, bytes: 0, processing: false };
    sessionOutputQueues.set(sessionId, queue);
  }
  return queue;
}

function getOutputGeneration(sessionId: string): number {
  let generation = sessionOutputGenerations.get(sessionId);
  if (generation === undefined) {
    generation = nextOutputGeneration++;
    sessionOutputGenerations.set(sessionId, generation);
  }
  return generation;
}

function isOutputGenerationCurrent(sessionId: string, generation: number): boolean {
  return sessionOutputGenerations.get(sessionId) === generation;
}

function clearQueuedOutput(sessionId?: string): void {
  if (sessionId !== undefined) {
    sessionOutputGenerations.set(sessionId, nextOutputGeneration++);
    sessionOutputQueues.delete(sessionId);
    scheduledOutputQueues.delete(sessionId);
    outputRttTracker.inputTimestamps.delete(sessionId);
    outputRttTracker.pendingOutputTimestamps.delete(sessionId);
    return;
  }

  const knownSessionIds = new Set([
    ...sessionOutputGenerations.keys(),
    ...sessionOutputQueues.keys(),
  ]);
  knownSessionIds.forEach((knownSessionId) => {
    sessionOutputGenerations.set(knownSessionId, nextOutputGeneration++);
  });
  sessionOutputQueues.clear();
  scheduledOutputQueues.clear();
  resetOutputRttTracker(outputRttTracker);
}

function noteQueueOverflow(sessionId: string): void {
  log.warn(() => `Output queue full for ${sessionId}, dropping oldest queued frame`);
  $dataLossDetected.set({ sessionId, timestamp: Date.now() });
  const snapshot = getOrCreateBrowserTransportSnapshot(sessionId);
  snapshot.dataLossCount += 1;
  snapshot.lastDataLossReason = 'browser_pending_overflow';
}

function dequeueOutputFrame(sessionId: string): OutputFrameItem | null {
  const queue = sessionOutputQueues.get(sessionId);
  if (!queue) {
    return null;
  }

  const item = queue.items[queue.index];
  if (!item) {
    queue.items.length = 0;
    queue.index = 0;
    queue.bytes = 0;
    sessionOutputQueues.delete(sessionId);
    return null;
  }

  queue.index++;
  queue.bytes = Math.max(0, queue.bytes - item.payload.byteLength);

  if (getPendingFrameCount(queue) === 0) {
    // Do not remove the queue object yet. The current worker may still be
    // waiting on async decompression or a timed yield, and deleting it early
    // would let a second worker start for the same session and break strict
    // per-session ordering.
    queue.items.length = 0;
    queue.index = 0;
    queue.bytes = 0;
  } else if (queue.index >= QUEUE_COMPACT_THRESHOLD) {
    compactSessionQueue(queue);
  }

  return item;
}

function queueOutputFrame(sessionId: string, payload: Uint8Array, compressed: boolean): void {
  const queue = getOrCreateSessionQueue(sessionId);
  const pendingCount = getPendingFrameCount(queue);
  if (
    pendingCount >= MAX_QUEUED_FRAMES_PER_SESSION ||
    queue.bytes + payload.byteLength > MAX_QUEUED_BYTES_PER_SESSION
  ) {
    noteQueueOverflow(sessionId);
    queue.items.length = 0;
    queue.index = 0;
    queue.bytes = 0;
    sessionOutputQueues.delete(sessionId);
    sessionsNeedingResync.add(sessionId);
    requestSessionRecovery(sessionId, 'browser_output_queue_overflow');
    return;
  }

  queue.items.push({ sessionId, payload, compressed });
  queue.bytes += payload.byteLength;
  scheduleSessionOutputQueue(sessionId, getOutputGeneration(sessionId));
}

function scheduleSessionOutputQueue(sessionId: string, generation: number): void {
  if (scheduledOutputQueues.has(sessionId)) {
    return;
  }

  scheduledOutputQueues.add(sessionId);
  void Promise.resolve().then(() => {
    scheduledOutputQueues.delete(sessionId);
    if (!isOutputGenerationCurrent(sessionId, generation)) {
      return;
    }
    void processSessionOutputQueue(sessionId, generation);
  });
}

async function processSessionOutputQueue(sessionId: string, generation: number): Promise<void> {
  const queue = sessionOutputQueues.get(sessionId);
  if (!queue || queue.processing) {
    return;
  }

  queue.processing = true;
  let writeBatch: TerminalWriteBatch | null = null;
  const flushWriteBatch = (): void => {
    if (!writeBatch) {
      return;
    }

    deliverTerminalWriteBatch(writeBatch, generation);
    writeBatch = null;
  };

  try {
    let sliceStartMs = performance.now();

    while (isOutputGenerationCurrent(sessionId, generation)) {
      const item = dequeueOutputFrame(sessionId);
      if (!item) {
        break;
      }

      const processedFrame = processOneFrame(item);
      const delivery = processedFrame instanceof Promise ? await processedFrame : processedFrame;
      if (delivery) {
        if (!canAppendTerminalWriteBatch(writeBatch, delivery)) {
          flushWriteBatch();
        }
        writeBatch = appendTerminalWriteBatch(writeBatch, delivery);
        if (writeBatch.bytes >= MAX_TERMINAL_WRITE_BATCH_BYTES) {
          flushWriteBatch();
        }
      }

      if (!isOutputGenerationCurrent(sessionId, generation)) {
        break;
      }

      // Heavy output must periodically yield so keyboard interrupts like Ctrl+C
      // can be processed promptly instead of waiting behind a long browser-side drain.
      if (performance.now() - sliceStartMs >= OUTPUT_DRAIN_BUDGET_MS) {
        flushWriteBatch();
        await yieldToMain();
        sliceStartMs = performance.now();
      }
    }
  } finally {
    flushWriteBatch();
    queue.processing = false;

    // If new frames landed after the current drain finished, restart the same
    // per-session worker. We keep sequencing at the session boundary so async
    // decompression and timed yields cannot reorder a terminal's output.
    if (isOutputGenerationCurrent(sessionId, generation) && getPendingFrameCount(queue) > 0) {
      void processSessionOutputQueue(sessionId, generation);
    } else if (sessionOutputQueues.get(sessionId) === queue && getPendingFrameCount(queue) === 0) {
      sessionOutputQueues.delete(sessionId);
    }
  }
}

function deliverTerminalWriteBatch(batch: TerminalWriteBatch, generation: number): void {
  writeToTerminal(
    batch.sessionId,
    batch.state,
    batch.sequenceEnd,
    batch.cols,
    batch.rows,
    combineTerminalWriteChunks(batch.chunks, batch.bytes),
    generation,
  );
}

/**
 * Process a single frame - decompress if needed, then write to terminal.
 */
function bufferPendingFrame(
  sessionId: string,
  sequenceEnd: bigint,
  cols: number,
  rows: number,
  data: Uint8Array,
): void {
  if (data.length <= 0) {
    return;
  }

  const bufferedPayload = new Uint8Array(12 + data.length);
  const view = new DataView(bufferedPayload.buffer);
  view.setBigUint64(0, sequenceEnd, true);
  bufferedPayload[8] = cols & 0xff;
  bufferedPayload[9] = (cols >> 8) & 0xff;
  bufferedPayload[10] = rows & 0xff;
  bufferedPayload[11] = (rows >> 8) & 0xff;
  bufferedPayload.set(data, 12);

  let frames = pendingOutputFrames.get(sessionId);
  if (!frames) {
    frames = [];
    pendingOutputFrames.set(sessionId, frames);
  }
  if (frames.length >= MAX_PENDING_FRAMES_PER_SESSION) {
    log.warn(() => `Pending frames overflow for ${sessionId}, requesting buffer refresh`);
    sessionsNeedingResync.add(sessionId);
    pendingOutputFrames.delete(sessionId);
    const snapshot = getOrCreateBrowserTransportSnapshot(sessionId);
    snapshot.dataLossCount += 1;
    snapshot.lastDataLossReason = 'browser_pending_overflow';
    requestSessionRecovery(sessionId, 'browser_pending_output_overflow');
    return;
  }

  frames.push(bufferedPayload);
}

function processOneFrame(
  item: OutputFrameItem,
): TerminalOutputDelivery | null | Promise<TerminalOutputDelivery | null> {
  const recoveryReady = activeSessionRecoveries.get(item.sessionId)?.ready;
  return recoveryReady
    ? recoveryReady.then(() => processOneFrameAfterRecoveryBarrier(item))
    : processOneFrameAfterRecoveryBarrier(item);
}

function processOneFrameAfterRecoveryBarrier(
  item: OutputFrameItem,
): TerminalOutputDelivery | null | Promise<TerminalOutputDelivery | null> {
  try {
    if (item.compressed) {
      return processCompressedFrame(item);
    }

    const frame = parseOutputFrame(item.payload);
    return processParsedOutputFrame(item, frame.sequenceEnd, frame.cols, frame.rows, frame.data);
  } catch (e) {
    log.error(() => `Failed to process frame: ${String(e)}`);
    return null;
  }
}

async function processCompressedFrame(
  item: OutputFrameItem,
): Promise<TerminalOutputDelivery | null> {
  try {
    const frame = await parseCompressedOutputFrame(item.payload);
    return processParsedOutputFrame(item, frame.sequenceEnd, frame.cols, frame.rows, frame.data);
  } catch (e) {
    log.error(() => `Failed to process frame: ${String(e)}`);
    return null;
  }
}

function processParsedOutputFrame(
  item: OutputFrameItem,
  sequenceEnd: bigint,
  cols: number,
  rows: number,
  data: Uint8Array,
): TerminalOutputDelivery | null {
  try {
    const snapshot = getOrCreateBrowserTransportSnapshot(item.sessionId);
    const sequence = classifyTerminalFrameSequence(data.length, sequenceEnd, snapshot.receivedSeq);
    if (sequence.kind === 'gap') {
      log.warn(
        () =>
          `Forward sequence gap for ${item.sessionId}: expected ${snapshot.receivedSeq.toString()}, got ${sequence.sequenceStart.toString()}`,
      );
      snapshot.dataLossCount += 1;
      snapshot.recoveryGapCount += 1;
      snapshot.lastDataLossReason = 'browser_forward_sequence_gap';
      $dataLossDetected.set({ sessionId: item.sessionId, timestamp: Date.now() });
      sessionsNeedingResync.add(item.sessionId);
      pendingOutputFrames.delete(item.sessionId);
      clearQueuedOutput(item.sessionId);
      if (activeSessionRecoveries.has(item.sessionId)) {
        // A gap inside an ordered snapshot means this socket can no longer prove
        // its transaction boundary. Reconnect is safer than waiting on an end
        // cursor that the cleared queue can never reach.
        muxWs?.close(4400, 'sequence gap during recovery');
        return null;
      }
      requestSessionRecovery(item.sessionId, 'browser_forward_sequence_gap');
      return null;
    }

    const trimmedData = trimFrameToUnseenSuffix(data, sequenceEnd, snapshot.receivedSeq);
    snapshot.receivedSeq = maxSequence(snapshot.receivedSeq, sequenceEnd);
    tryCompleteSessionRecovery(item.sessionId);

    const state = sessionTerminals.get(item.sessionId);

    if (state && state.opened) {
      if (trimmedData.length > 0) {
        return {
          sessionId: item.sessionId,
          state,
          sequenceEnd,
          cols,
          rows,
          data: trimmedData,
        };
      }
    } else if (trimmedData.length > 0) {
      bufferPendingFrame(item.sessionId, sequenceEnd, cols, rows, trimmedData);
    }

    return null;
  } catch (e) {
    log.error(() => `Failed to process frame: ${String(e)}`);
    return null;
  }
}

// Track bracketed paste mode per session
const bracketedPasteState = new Map<string, boolean>();

/** Check if session has bracketed paste mode enabled */
export function isBracketedPasteEnabled(sessionId: string): boolean {
  return bracketedPasteState.get(sessionId) ?? false;
}

export function reconcileSynchronizedOutputCursor(sessionId: string): void {
  const state = sessionTerminals.get(sessionId);
  if (!state?.opened) {
    return;
  }

  // Codex and similar TUIs wrap redraws in DEC synchronized output. xterm buffers row
  // paints for that mode, but the visible cursor can still appear to jump around unless we
  // suppress it until the synchronized update completes.
  reconcileSynchronizedOutputCursorState(state);
}

/**
 * Write data to terminal, resizing if dimensions changed.
 */
function writeTerminalData(
  sessionId: string,
  state: TerminalState,
  sequenceEnd: bigint,
  data: Uint8Array,
  generation: number,
  onParsed?: () => void,
): void {
  // xterm already preserves write order internally. We use the callback for
  // "parsed and visible" notifications only, not as a per-frame flow-control gate.
  state.terminal.write(data, () => {
    if (!isOutputGenerationCurrent(sessionId, generation)) {
      return;
    }

    const snapshot = getOrCreateBrowserTransportSnapshot(sessionId);
    snapshot.renderedSeq = maxSequence(snapshot.renderedSeq, sequenceEnd);
    measureCompletedOutputRtt(sessionId);
    recordInputTraceOutputParsed(sessionId, sequenceEnd);
    onParsed?.();
  });
}

function updateReconnectFreezeForOutput(
  state: TerminalState,
  data: Uint8Array,
  cols: number,
  rows: number,
): void {
  if (state.reconnectFreezeOverlay && (data.length > 0 || (cols > 0 && rows > 0))) {
    removeReconnectFreeze(state);
  }
}

function processTerminalOutputCursorState(
  state: TerminalState,
  sessionId: string,
  data: Uint8Array,
): {
  data: Uint8Array;
} & ReturnType<typeof processCursorVisibilityControls> {
  scanBracketedPaste(data, sessionId);

  const shouldHideCursor = shouldHideCursorForOutput(state, data);
  const cursorVisibility = processCursorVisibilityControls(
    data,
    shouldHideCursor || state.burstCursorHidden === true,
  );
  if (cursorVisibility.remoteCursorVisible !== null) {
    state.remoteCursorVisible = cursorVisibility.remoteCursorVisible;
  }
  if (shouldHideCursor) {
    hideBurstCursor(state);
  }
  if (data.length > 0) {
    scheduleBurstCursorShow(state);
  }
  return cursorVisibility;
}

function applyTerminalResizeIfNeeded(
  sessionId: string,
  state: TerminalState,
  cols: number,
  rows: number,
): void {
  if (!(cols > 0 && rows > 0 && cols <= 500 && rows <= 500 && state.opened)) {
    return;
  }

  const currentCols = state.terminal.cols;
  const currentRows = state.terminal.rows;
  if (currentCols === cols && currentRows === rows) {
    return;
  }

  if ($isMainBrowser.get() && !state.container.classList.contains('hidden')) {
    state.serverCols = cols;
    state.serverRows = rows;
    return;
  }

  try {
    state.terminal.resize(cols, rows);
    state.serverCols = cols;
    state.serverRows = rows;
    applyTerminalScaling(sessionId, state);
  } catch (e: unknown) {
    const message = e instanceof Error ? e.message : String(e);
    log.warn(() => `Terminal resize deferred: ${message}`);
  }
}

function writeOutputDataWithPathScan(
  sessionId: string,
  state: TerminalState,
  sequenceEnd: bigint,
  data: Uint8Array,
  generation: number,
): void {
  if (data.length === 0) {
    return;
  }

  writeTerminalData(sessionId, state, sequenceEnd, data, generation, () => {
    if (sessionId === $activeSessionId.get()) {
      void yieldToMain().then(() => {
        if (
          isOutputGenerationCurrent(sessionId, generation) &&
          sessionId === $activeSessionId.get()
        ) {
          scanOutputForPaths(sessionId, data);
        }
      });
    }
  });
}

function writeToTerminal(
  sessionId: string,
  state: TerminalState,
  sequenceEnd: bigint,
  cols: number,
  rows: number,
  data: Uint8Array,
  generation: number,
): void {
  updateReconnectFreezeForOutput(state, data, cols, rows);
  const cursorVisibility = processTerminalOutputCursorState(state, sessionId, data);
  applyTerminalResizeIfNeeded(sessionId, state, cols, rows);
  writeOutputDataWithPathScan(sessionId, state, sequenceEnd, cursorVisibility.data, generation);
}

export function applyOutputFrameToTerminal(
  sessionId: string,
  state: TerminalState,
  sequenceEnd: bigint,
  cols: number,
  rows: number,
  data: Uint8Array,
): void {
  writeToTerminal(sessionId, state, sequenceEnd, cols, rows, data, getOutputGeneration(sessionId));
}

function handleMuxInitFrame(type: number, data: Uint8Array): boolean {
  if (type !== 0xff) {
    return false;
  }

  if (data.length >= MUX_HEADER_SIZE + 2) {
    const dv = new DataView(data.buffer, data.byteOffset, data.byteLength);
    const serverVersion = dv.getUint16(MUX_HEADER_SIZE, true);
    setServerProtocolVersion(serverVersion);
    log.info(
      () => `Server protocol version: ${serverVersion}, client version: ${MUX_PROTOCOL_VERSION}`,
    );

    if (serverVersion < MUX_MIN_COMPATIBLE_VERSION) {
      log.error(
        () =>
          `Server protocol version ${serverVersion} is below minimum ${MUX_MIN_COMPATIBLE_VERSION}`,
      );
    } else if (serverVersion > MUX_PROTOCOL_VERSION) {
      log.warn(
        () => `Server uses newer protocol (v${serverVersion}), client is v${MUX_PROTOCOL_VERSION}`,
      );
    }
  }

  return true;
}

function handleMuxSyncCompleteFrame(type: number): boolean {
  if (type !== MUX_TYPE_SYNC_COMPLETE) {
    return false;
  }

  syncCompletePending = true;
  finishInitialSyncWhenRecoveriesAreParsed();
  return true;
}

function finishInitialSyncWhenRecoveriesAreParsed(): void {
  if (!syncCompletePending || activeSessionRecoveries.size > 0) {
    return;
  }

  syncCompletePending = false;
  if (syncCompleteTimeout !== null) {
    clearTimeout(syncCompleteTimeout);
    syncCompleteTimeout = null;
  }
  thawReconnectFreeze();
  _suppressHeatCallback?.(0);
  setBellNotificationsSuppressed(false);
}

function handleMuxRecoveryBeginFrame(
  type: number,
  sessionId: string,
  payload: Uint8Array,
): boolean {
  if (type !== MUX_TYPE_RECOVERY_BEGIN) {
    return false;
  }
  if (payload.length < 22) {
    log.warn(() => `Ignoring malformed recovery-begin frame for ${sessionId}`);
    return true;
  }

  const view = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
  const generation = view.getUint32(0, true);
  const resetTerminal = view.getUint8(4) !== 0;
  const reasonCode = view.getUint8(5);
  const sequenceStart = view.getBigUint64(6, true);
  const sourceSequenceEndExclusive = view.getBigUint64(14, true);

  // Invalidate only this session. Async decompression and xterm callbacks from
  // other terminals must remain live while this transaction re-establishes one cursor.
  clearQueuedOutput(sessionId);
  pendingOutputFrames.delete(sessionId);
  sessionsNeedingResync.delete(sessionId);
  clearBracketedPasteScanState(sessionId);
  bracketedPasteState.delete(sessionId);
  beginReplayHeatSuppression(sessionId, BUFFER_REPLAY_MAX_MS);
  _suppressHeatCallback?.(RESYNC_HEAT_SUPPRESS_MS);

  const snapshot = getOrCreateBrowserTransportSnapshot(sessionId);
  snapshot.lastReplayReason = resolveMuxDataLossReason(reasonCode);
  let releaseRecoveryBarrier: () => void = () => {};
  const recoveryReady = new Promise<void>((resolve) => {
    releaseRecoveryBarrier = resolve;
  });
  const recovery: ActiveSessionRecovery = {
    generation,
    sourceSequenceEndExclusive,
    endReceived: false,
    replayBytes: 0,
    resetTerminal,
    prepared: false,
    ready: recoveryReady,
    releaseBarrier: releaseRecoveryBarrier,
  };
  activeSessionRecoveries.set(sessionId, recovery);

  const prepareRecovery = (): void => {
    if (activeSessionRecoveries.get(sessionId) !== recovery) {
      releaseRecoveryBarrier();
      return;
    }

    if (resetTerminal) {
      const state = sessionTerminals.get(sessionId);
      if (state?.opened) {
        // Missing terminal bytes may end inside UTF-8 or ANSI state. reset() is the
        // only operation that repairs parser state; clear() only erases the viewport.
        state.terminal.reset();
      }
    }
    snapshot.receivedSeq = sequenceStart;
    snapshot.renderedSeq = sequenceStart;
    recovery.prepared = true;
    recovery.ready = null;
    releaseRecoveryBarrier();
    tryCompleteSessionRecovery(sessionId);
  };

  const state = sessionTerminals.get(sessionId);
  if (state?.opened) {
    // xterm owns writes once terminal.write returns. This empty write waits for
    // that internal queue before reset/replay without serializing normal output.
    try {
      state.terminal.write('', prepareRecovery);
    } catch {
      prepareRecovery();
    }
  } else {
    prepareRecovery();
  }
  return true;
}

function handleMuxRecoveryEndFrame(type: number, sessionId: string, payload: Uint8Array): boolean {
  if (type !== MUX_TYPE_RECOVERY_END) {
    return false;
  }
  if (payload.length < 16) {
    log.warn(() => `Ignoring malformed recovery-end frame for ${sessionId}`);
    return true;
  }

  const view = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
  const generation = view.getUint32(0, true);
  const recovery = activeSessionRecoveries.get(sessionId);
  if (!recovery || recovery.generation !== generation) {
    return true;
  }

  recovery.sourceSequenceEndExclusive = view.getBigUint64(4, true);
  recovery.replayBytes = Math.max(0, view.getInt32(12, true));
  recovery.endReceived = true;
  tryCompleteSessionRecovery(sessionId);
  return true;
}

function tryCompleteSessionRecovery(sessionId: string): void {
  const recovery = activeSessionRecoveries.get(sessionId);
  const snapshot = browserTransportSnapshots.get(sessionId);
  if (
    !recovery?.prepared ||
    !recovery.endReceived ||
    !snapshot ||
    snapshot.receivedSeq !== recovery.sourceSequenceEndExclusive
  ) {
    return;
  }

  discardSessionRecovery(sessionId);
  recoveryRequestsInFlight.delete(sessionId);
  sessionsNeedingResync.delete(sessionId);
  snapshot.recoveryCompleted += 1;
  snapshot.recoveryReplayBytes += recovery.replayBytes;
  snapshot.lastReplayBytes = recovery.replayBytes;
  if (recovery.resetTerminal) {
    snapshot.recoveryResetCount += 1;
  }
  const followupCause = recoveryFollowupCauses.get(sessionId);
  recoveryFollowupCauses.delete(sessionId);
  if (followupCause !== undefined) {
    requestSessionRecovery(sessionId, followupCause);
  }
  finishInitialSyncWhenRecoveriesAreParsed();
}

function handleMuxResyncFrame(type: number, sessionId: string): boolean {
  if (type !== MUX_TYPE_RESYNC) {
    return false;
  }

  log.info(() =>
    sessionId
      ? `Resync: clearing terminal ${sessionId}`
      : 'Resync: clearing terminals for buffer refresh',
  );
  _suppressHeatCallback?.(RESYNC_HEAT_SUPPRESS_MS);
  if (sessionId) {
    beginReplayHeatSuppression(sessionId, BUFFER_REPLAY_MAX_MS);
  } else {
    forEachLocalTerminal((_, localSessionId) => {
      beginReplayHeatSuppression(localSessionId, BUFFER_REPLAY_MAX_MS);
    });
  }
  forEachLocalTerminal((state, localSessionId) => {
    if (sessionId && localSessionId !== sessionId) {
      return;
    }

    if (state.opened) {
      // A resync follows transport data loss. The byte hole may have poisoned
      // parser state (charset shifts, pending escape sequences), so only a full
      // reset guarantees the replay renders cleanly; clear() + SGR reset cannot
      // repair a stuck charset.
      state.terminal.reset();
    }
  });
  if (sessionId) {
    pendingOutputFrames.delete(sessionId);
    sessionsNeedingResync.delete(sessionId);
    const snapshot = getOrCreateBrowserTransportSnapshot(sessionId);
    snapshot.receivedSeq = 0n;
    snapshot.renderedSeq = 0n;
    discardSessionRecovery(sessionId);
    recoveryRequestsInFlight.delete(sessionId);
    recoveryFollowupCauses.delete(sessionId);
    clearBracketedPasteScanState(sessionId);
    bracketedPasteState.delete(sessionId);
    clearQueuedOutput(sessionId);
  } else {
    pendingOutputFrames.clear();
    sessionsNeedingResync.clear();
    browserTransportSnapshots.forEach((snapshot) => {
      snapshot.receivedSeq = 0n;
      snapshot.renderedSeq = 0n;
    });
    discardAllSessionRecoveries();
    recoveryRequestsInFlight.clear();
    recoveryFollowupCauses.clear();
    clearBracketedPasteScanState();
    bracketedPasteState.clear();
    clearQueuedOutput();
  }
  return true;
}

function handleMuxOutputFrame(type: number, sessionId: string, payload: Uint8Array): boolean {
  if (type !== MUX_TYPE_OUTPUT && type !== MUX_TYPE_COMPRESSED_OUTPUT) {
    return false;
  }

  armOutputRttMeasurement(sessionId);
  recordInputTraceOutputReceived(sessionId, payload);
  const hdrBytes = type === MUX_TYPE_COMPRESSED_OUTPUT ? 16 : 12;
  const termDataBytes = Math.max(0, payload.length - hdrBytes);
  if (shouldRecordHeat(sessionId, termDataBytes)) {
    _sessionBytesCallback?.(sessionId, termDataBytes);
  }
  if (payload.length >= 4) {
    queueOutputFrame(sessionId, payload, type === MUX_TYPE_COMPRESSED_OUTPUT);
  }
  return true;
}

function handleMuxForegroundChangeFrame(
  type: number,
  sessionId: string,
  payload: Uint8Array,
): boolean {
  if (type !== MUX_TYPE_FOREGROUND_CHANGE) {
    return false;
  }

  try {
    const jsonStr = new TextDecoder().decode(payload);
    const changePayload = JSON.parse(jsonStr) as ForegroundChangePayload;
    handleForegroundChange(sessionId, changePayload);
  } catch (e) {
    log.error(() => `Failed to parse foreground change: ${String(e)}`);
  }

  return true;
}

function handleMuxPongFrame(type: number, payload: Uint8Array): boolean {
  if (type !== MUX_TYPE_PONG) {
    return false;
  }

  if (payload.length >= 9 && pongCallback) {
    const pdv = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
    const pongMode = pdv.getUint8(0);
    const timestampBytes = payload.slice(1, 9);
    const timestamp = new DataView(timestampBytes.buffer).getFloat64(0, true);
    const rtt = performance.now() - timestamp;
    if (pongMode === 0 && payload.length >= 13) {
      lastFlushDelayMs = pdv.getUint16(9, true);
      lastServerIoRttMs = pdv.getUint16(11, true);
    }
    pongCallback(pongMode, rtt);
  }

  return true;
}

function handleMuxDataLossFrame(type: number, sessionId: string, payload: Uint8Array): boolean {
  if (type !== MUX_TYPE_DATA_LOSS) {
    return false;
  }

  const dataView = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
  const droppedBytes = payload.length >= 5 ? dataView.getUint32(1, true) : 0;
  const missingSequenceStart = payload.length >= 21 ? dataView.getBigUint64(5, true) : null;
  const missingSequenceEndExclusive = payload.length >= 21 ? dataView.getBigUint64(13, true) : null;
  log.warn(
    () =>
      `Data loss: session ${sessionId} dropped ${droppedBytes} bytes` +
      (missingSequenceStart !== null && missingSequenceEndExclusive !== null
        ? ` (${missingSequenceStart.toString()}..${missingSequenceEndExclusive.toString()})`
        : '') +
      ', requesting cursor recovery',
  );
  const reasonCode = payload.length >= 1 ? dataView.getUint8(0) : 0;
  const snapshot = getOrCreateBrowserTransportSnapshot(sessionId);
  snapshot.dataLossCount += 1;
  snapshot.lastDataLossReason = resolveMuxDataLossReason(reasonCode);
  sessionsNeedingResync.add(sessionId);
  requestSessionRecovery(sessionId, snapshot.lastDataLossReason);
  return true;
}

/**
 * Connect to the mux WebSocket for terminal I/O.
 * Uses a binary protocol with 9-byte header.
 */
export function connectMuxWebSocket(): void {
  closeWebSocket(muxWs, setMuxWs);

  const activeId = $activeSessionId.get();
  const query = new URLSearchParams();
  if (activeId) {
    query.set('activeSessionId', activeId);
  }
  if (currentVisibleSessionIds.length > 0) {
    query.set('visibleSessionIds', currentVisibleSessionIds.join(','));
  }
  const resumeCursors = buildResumeCursorQueryValue(
    sessionTerminals,
    (sessionId) => browserTransportSnapshots.get(sessionId),
    isHubSessionId,
  );
  if (resumeCursors !== null) {
    query.set('resumeCursors', resumeCursors);
  }
  const replayRows = muxSessionRouting.isQuickResumeEnabled()
    ? muxSessionRouting.getReplayRows(activeId)
    : null;
  if (replayRows !== null) {
    query.set('replayRows', String(replayRows));
  }
  const wsPathBase = isSharedSessionRoute() ? '/ws/share/mux' : '/ws/mux';
  const wsPath = query.size > 0 ? `${wsPathBase}?${query.toString()}` : wsPathBase;
  const ws = new WebSocket(createWsUrl(wsPath));
  ws.binaryType = 'arraybuffer';
  setMuxWs(ws);

  ws.onopen = () => {
    muxReconnect.reset();
    syncCompletePending = false;

    // Suppress bell and heat until server sends SyncComplete (10s safety timeout)
    setBellNotificationsSuppressed(true);
    _suppressHeatCallback?.(Number.MAX_SAFE_INTEGER);
    if (syncCompleteTimeout !== null) clearTimeout(syncCompleteTimeout);
    syncCompleteTimeout = window.setTimeout(() => {
      syncCompletePending = false;
      thawReconnectFreeze();
      _suppressHeatCallback?.(0);
      setBellNotificationsSuppressed(false);
      syncCompleteTimeout = null;
    }, 10000);

    // Detect reconnect: we've connected before AND have terminals to refresh
    const localTerminalCount = countLocalTerminals(sessionTerminals, isHubSessionId);
    const isReconnect = $muxHasConnected.get() && localTerminalCount > 0;

    $muxWsConnected.set(true);
    $muxHasConnected.set(true);

    // On reconnect, check if server version changed (update applied) and reload
    if (isReconnect) {
      void checkVersionAndReload();
      log.info(() => `Reconnected - refreshing ${localTerminalCount} terminals`);
      freezeVisibleTerminalsDuringReconnect();
      pendingOutputFrames.clear();
      sessionsNeedingResync.clear();
      replaySuppressedSessions.clear();
      clearQueuedOutput();
      forEachLocalTerminal((_, sessionId) => {
        const snapshot = getOrCreateBrowserTransportSnapshot(sessionId);
        snapshot.lastReplayReason = 'reconnect_tail_replay';
        // Server pushes all buffers on connect via SendInitialBuffersAsync
      });

      // If state WS is connected, fetch fresh session list to ensure consistency
      // (state WS may have missed updates while mux was disconnected)
      if ($stateWsConnected.get()) {
        void refreshSessionList();
      }
    } else {
      log.info(() => 'Connected (first connection)');
    }

    // Send active session hint so server knows which session to prioritize
    const activeSessionId = $activeSessionId.get();
    if (activeSessionId) {
      sendActiveSessionHint(activeSessionId);
    }
    sendVisibleSessionsHint(currentVisibleSessionIds);

    // Flush any input buffered during disconnection
    flushPendingInput();
  };

  ws.onmessage = (event) => {
    if (!(event.data instanceof ArrayBuffer)) return;
    addWsRxBytes(event.data.byteLength);

    const data = new Uint8Array(event.data);
    if (data.length < MUX_HEADER_SIZE) return;

    const type = data[0];
    if (type === undefined) return;

    if (handleMuxInitFrame(type, data)) {
      return;
    }

    if (handleMuxSyncCompleteFrame(type)) {
      return;
    }

    const sessionId = decodeSessionId(data, 1);
    const payload = data.subarray(MUX_HEADER_SIZE); // zero-copy view

    if (handleMuxRecoveryBeginFrame(type, sessionId, payload)) {
      return;
    }
    if (handleMuxRecoveryEndFrame(type, sessionId, payload)) {
      return;
    }
    if (handleMuxResyncFrame(type, sessionId)) {
      return;
    }

    if (handleMuxOutputFrame(type, sessionId, payload)) return;
    if (handleMuxForegroundChangeFrame(type, sessionId, payload)) return;
    if (handleMuxPongFrame(type, payload)) return;
    if (handleMuxInputTraceResultFrame(type, sessionId, payload)) return;
    void handleMuxDataLossFrame(type, sessionId, payload);
  };

  ws.onclose = (event) => {
    $muxWsConnected.set(false);
    lastHintedSessionId = null;
    syncCompletePending = false;
    replaySuppressedSessions.clear();
    discardAllSessionRecoveries();
    recoveryRequestsInFlight.clear();
    recoveryFollowupCauses.clear();
    clearInputLatencyTraceInFlight();
    thawReconnectFreeze();

    // Log close reason
    if (event.code === WS_CLOSE_SERVER_SHUTDOWN) {
      log.info(() => 'Server shutting down, will reconnect');
    } else if (event.code !== 1000 && event.code !== 1001) {
      log.warn(() => `WebSocket closed: code=${event.code}, reason=${event.reason || 'none'}`);
    }

    if (handleAuthenticatedWebSocketClose(event)) {
      return;
    }

    scheduleMuxReconnect();
  };

  ws.onerror = (e) => {
    log.error(() => `WebSocket error: ${e.type}`);
  };
}

/**
 * Send a frame to the mux WebSocket with traffic tracking.
 */
function sendFrame(frame: Uint8Array): void {
  if (!muxWs || muxWs.readyState !== WebSocket.OPEN) return;
  addWsTxBytes(frame.byteLength);
  muxWs.send(frame);
}

export function sendInput(sessionId: string, data: string): void {
  if (isHubSessionId(sessionId)) {
    sendHubInput(sessionId, data);
    return;
  }

  const inputAtMs = performance.now();
  const state = sessionTerminals.get(sessionId);
  if (state) {
    state.lastLocalInputAtMs = inputAtMs;
    showBurstCursor(state);
    scheduleBurstCursorShow(state);
  }

  if (printableInputCoalescer.enqueue(sessionId, data, inputAtMs)) {
    return;
  }

  sendInputNow(sessionId, data, inputAtMs);
}

function sendInputNow(sessionId: string, data: string, inputAtMs: number): void {
  if (!muxWs || muxWs.readyState !== WebSocket.OPEN) {
    // Buffer input during disconnection (prevents lost keystrokes during reconnect)
    if (pendingInputQueue.length < 100) {
      pendingInputQueue.push({ sessionId, data });
    }
    return;
  }

  const shouldSendActiveHint = sessionId !== lastHintedSessionId;

  maybeSendInputTraceMarker(sessionId, inputAtMs, sendFrame, encodeSessionId);
  recordInputTimestamp(sessionId);

  sendFrame(
    createMuxInputFrame(
      MUX_HEADER_SIZE,
      MUX_TYPE_INPUT,
      sessionId,
      data,
      encodeSessionId,
      textEncoder,
    ),
  );

  if (shouldSendActiveHint) {
    _suppressHeatCallback?.(1500);
    sendActiveSessionHint(sessionId);
    lastHintedSessionId = sessionId;
  }
}

function flushPendingInput(): void {
  printableInputCoalescer.flushAll();
  while (pendingInputQueue.length > 0) {
    const item = pendingInputQueue.shift();
    if (!item) break;
    sendInput(item.sessionId, item.data);
  }
}

export function sendResize(sessionId: string, cols: number, rows: number): void {
  if (!$isMainBrowser.get()) {
    return;
  }

  if (isHubSessionId(sessionId)) {
    sendHubResize(sessionId, cols, rows);
    return;
  }

  printableInputCoalescer.flush(sessionId);

  if (!muxWs || muxWs.readyState !== WebSocket.OPEN) return;

  const frame = new Uint8Array(MUX_HEADER_SIZE + 4);
  frame[0] = MUX_TYPE_RESIZE;
  encodeSessionId(frame, 1, sessionId);
  frame[MUX_HEADER_SIZE] = cols & 0xff;
  frame[MUX_HEADER_SIZE + 1] = (cols >> 8) & 0xff;
  frame[MUX_HEADER_SIZE + 2] = rows & 0xff;
  frame[MUX_HEADER_SIZE + 3] = (rows >> 8) & 0xff;
  sendFrame(frame);

  const state = sessionTerminals.get(sessionId);
  if (state) {
    state.serverCols = cols;
    state.serverRows = rows;
  }
}

/**
 * Request buffer refresh for a session via WebSocket.
 */
export function requestBufferRefresh(
  sessionId: string,
  mode: 'fullReplay' | 'quickResume' = 'fullReplay',
  recoveryCause = mode === 'quickResume' ? 'quick_resume' : 'manual_full_replay',
): void {
  if (isHubSessionId(sessionId)) {
    requestHubBufferRefresh(sessionId);
    return;
  }

  beginReplayHeatSuppression(sessionId, BUFFER_REPLAY_MAX_MS);
  _suppressHeatCallback?.(RESYNC_HEAT_SUPPRESS_MS);
  const snapshot = getOrCreateBrowserTransportSnapshot(sessionId);
  if (recoveryRequestsInFlight.has(sessionId) || activeSessionRecoveries.has(sessionId)) {
    snapshot.recoveryCoalesced += 1;
    return;
  }
  snapshot.lastReplayReason =
    mode === 'quickResume' ? 'quick_resume_tail_replay' : 'buffer_refresh_tail_replay';
  if (!muxWs || muxWs.readyState !== WebSocket.OPEN) return;

  recoveryRequestsInFlight.add(sessionId);
  snapshot.recoveryRequested += 1;
  snapshot.lastRecoveryCause = recoveryCause;

  const replayRows = mode === 'quickResume' ? muxSessionRouting.getReplayRows(sessionId) : null;
  const resumeSequence =
    mode === 'quickResume' ? getResumeSequence(browserTransportSnapshots.get(sessionId)) : null;
  const frame = createBufferRequestFrame(
    MUX_HEADER_SIZE,
    MUX_TYPE_BUFFER_REQUEST,
    encodeSessionId,
    sessionId,
    mode === 'quickResume',
    replayRows,
    resumeSequence,
  );
  sendFrame(frame);
}

function requestSessionRecovery(sessionId: string, cause: string): void {
  if (activeSessionRecoveries.has(sessionId)) {
    const snapshot = getOrCreateBrowserTransportSnapshot(sessionId);
    snapshot.recoveryCoalesced += 1;
    recoveryFollowupCauses.set(sessionId, cause);
    return;
  }
  requestBufferRefresh(sessionId, 'quickResume', cause);
}

/**
 * Send active session hint to server for priority delivery.
 */
export function sendActiveSessionHint(sessionId: string | null): void {
  if (sessionId && isHubSessionId(sessionId)) {
    return;
  }

  if (!muxWs || muxWs.readyState !== WebSocket.OPEN) return;

  if (sessionId) {
    printableInputCoalescer.flush(sessionId);
  }

  if (sessionId) {
    beginReplayHeatSuppression(sessionId, ACTIVE_HINT_REPLAY_MAX_MS);
  }

  const frame = new Uint8Array(MUX_HEADER_SIZE);
  frame[0] = MUX_TYPE_ACTIVE_HINT;
  if (sessionId) {
    encodeSessionId(frame, 1, sessionId);
  }
  sendFrame(frame);
}

export function sendVisibleSessionsHint(sessionIds: readonly string[]): void {
  if (!muxWs || muxWs.readyState !== WebSocket.OPEN) return;

  const normalizedSessionIds = muxSessionRouting.normalizeSessionIds(sessionIds);
  const frame = new Uint8Array(MUX_HEADER_SIZE + normalizedSessionIds.length * 8);
  frame[0] = MUX_TYPE_VISIBLE_SESSIONS_HINT;
  let offset = MUX_HEADER_SIZE;
  normalizedSessionIds.forEach((sessionId) => {
    encodeSessionId(frame, offset, sessionId);
    offset += 8;
  });
  sendFrame(frame);
}

export function updateTerminalVisibility(
  _activeSessionId: string | null,
  visibleSessionIds: readonly string[],
): void {
  const normalizedVisibleSessionIds = muxSessionRouting.normalizeSessionIds(visibleSessionIds);

  currentVisibleSessionIds = normalizedVisibleSessionIds;

  if (muxWs && muxWs.readyState === WebSocket.OPEN) {
    sendVisibleSessionsHint(normalizedVisibleSessionIds);
  }
}

export function recoverVisibleTerminalsAfterBrowserResume(
  activeSessionId: string | null,
  visibleSessionIds: readonly string[],
): void {
  const normalizedVisibleSessionIds = muxSessionRouting.normalizeSessionIds(visibleSessionIds);
  currentVisibleSessionIds = normalizedVisibleSessionIds;

  if (!muxWs || muxWs.readyState !== WebSocket.OPEN) {
    connectMuxWebSocket();
    return;
  }

  sendVisibleSessionsHint(normalizedVisibleSessionIds);
  sendActiveSessionHint(activeSessionId);
}

/**
 * Encode 8-character session ID into buffer at offset.
 */
export function encodeSessionId(buffer: Uint8Array, offset: number, sessionId: string): void {
  for (let i = 0; i < 8; i++) {
    buffer[offset + i] = i < sessionId.length ? sessionId.charCodeAt(i) : 0;
  }
}

/**
 * Decode 8-character session ID from buffer at offset.
 */
export function decodeSessionId(buffer: Uint8Array, offset: number): string {
  const chars: string[] = [];
  for (let i = 0; i < 8; i++) {
    const byte = buffer[offset + i];
    if (byte !== undefined && byte !== 0) {
      chars.push(String.fromCharCode(byte));
    }
  }
  return chars.join('');
}

/**
 * Schedule mux WebSocket reconnection.
 */
export function scheduleMuxReconnect(): void {
  muxReconnect.schedule(connectMuxWebSocket);
}

/**
 * Write output frame to terminal (used by manager.ts for replay).
 */
export function writeOutputFrame(
  sessionId: string,
  state: TerminalState,
  payload: Uint8Array,
): void {
  const frame = parseOutputFrame(payload);
  writeToTerminal(
    sessionId,
    state,
    frame.sequenceEnd,
    frame.cols,
    frame.rows,
    frame.data,
    getOutputGeneration(sessionId),
  );
}

export function resetMuxChannelRuntimeForTests(): void {
  if (syncCompleteTimeout !== null) {
    clearTimeout(syncCompleteTimeout);
    syncCompleteTimeout = null;
  }
  syncCompletePending = false;

  _sessionBytesCallback = null;
  _suppressHeatCallback = null;
  pongCallback = null;
  lastOutputRtt = null;
  lastFlushDelayMs = null;
  lastServerIoRttMs = null;
  lastHintedSessionId = null;
  currentVisibleSessionIds = [];
  replaySuppressedSessions.clear();
  browserTransportSnapshots.clear();
  discardAllSessionRecoveries();
  recoveryRequestsInFlight.clear();
  recoveryFollowupCauses.clear();
  bracketedPasteState.clear();
  clearBracketedPasteScanState();
  outputRttListeners.clear();
  resetInputLatencyTraceRuntime(true);
  pendingInputQueue.length = 0;
  pendingYieldToMainResolves.length = 0;
  yieldToMainChannel?.port1.close();
  yieldToMainChannel?.port2.close();
  yieldToMainChannel = null;

  clearQueuedOutput();
  sessionOutputGenerations.clear();
  pendingOutputFrames.clear();
  sessionsNeedingResync.clear();
  closeWebSocket(muxWs, setMuxWs);
}

/* eslint-enable max-lines */
