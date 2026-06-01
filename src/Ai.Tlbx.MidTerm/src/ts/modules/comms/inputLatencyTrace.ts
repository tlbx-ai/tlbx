import {
  MUX_HEADER_SIZE,
  MUX_TYPE_INPUT_TRACE_MARKER,
  MUX_TYPE_INPUT_TRACE_RESULT,
} from '../../constants';

export interface InputLatencyTraceSnapshot {
  traceId: number;
  totalToXtermParsedMs: number | null;
  browserToOutputReceiveMs: number | null;
  browserReceiveToXtermParseMs: number | null;
  serverReceiveToIpcStartMs: number;
  ipcWriteMs: number;
  serverReceiveToMthostReceiveMs: number;
  serverReceiveToPtyWriteDoneMs: number;
  ptyWriteDoneToPtyOutputReadMs: number;
  ptyOutputReadToMthostIpcEnqueuedMs: number;
  mthostIpcEnqueuedToWriteDoneMs: number;
  mthostIpcWriteDoneToFlushDoneMs: number;
  mthostIpcEnqueuedToServerOutputObservedMs: number;
  serverReceiveToOutputObservedMs: number;
  outputObservedToMuxQueuedMs: number;
  muxQueuedToClientQueuedMs: number;
  clientQueuedToWsFlushMs: number;
  serverReceiveToWsFlushMs: number;
}

interface InputLatencyServerTrace {
  firstOutputSequenceEnd: bigint;
  serverReceiveToIpcStartMs: number;
  ipcWriteMs: number;
  serverReceiveToMthostReceiveMs: number;
  serverReceiveToPtyWriteDoneMs: number;
  ptyWriteDoneToPtyOutputReadMs: number;
  ptyOutputReadToMthostIpcEnqueuedMs: number;
  mthostIpcEnqueuedToWriteDoneMs: number;
  mthostIpcWriteDoneToFlushDoneMs: number;
  mthostIpcEnqueuedToServerOutputObservedMs: number;
  serverReceiveToOutputObservedMs: number;
  outputObservedToMuxQueuedMs: number;
  muxQueuedToClientQueuedMs: number;
  clientQueuedToWsFlushMs: number;
  serverReceiveToWsFlushMs: number;
}

interface PendingInputLatencyTrace {
  traceId: number;
  sessionId: string;
  browserInputAtMs: number;
  server?: InputLatencyServerTrace;
}

type InputLatencyTraceListener = (sessionId: string, trace: InputLatencyTraceSnapshot) => void;

const INPUT_TRACE_MIN_INTERVAL_MS = 500;
const INPUT_TRACE_TIMEOUT_MS = 5000;
const SEQUENCE_MODULUS = 1n << 64n;
const HALF_SEQUENCE_RANGE = 1n << 63n;

const inputLatencyTraceConsumers = new Set<string>();
let inputLatencyTracingEnabled = false;
let nextInputTraceId = (Math.random() * 0xffffffff) >>> 0 || 1;
const inputTraceListeners = new Set<InputLatencyTraceListener>();
const pendingInputTraces = new Map<number, PendingInputLatencyTrace>();
const inputTraceSessionState = new Map<
  string,
  { lastStartedAtMs: number; inFlightTraceId: number | null }
>();
const lastOutputReceivedBySession = new Map<string, { sequenceEnd: bigint; atMs: number }>();
const lastOutputParsedBySession = new Map<string, { sequenceEnd: bigint; atMs: number }>();

function syncInputLatencyTracingEnabled(): void {
  const enabledValue = inputLatencyTraceConsumers.size > 0;
  if (inputLatencyTracingEnabled === enabledValue) {
    return;
  }

  inputLatencyTracingEnabled = enabledValue;
  if (!inputLatencyTracingEnabled) {
    pendingInputTraces.clear();
    inputTraceSessionState.clear();
  }
}

export function setInputLatencyTraceConsumerEnabled(consumer: string, enabledValue: boolean): void {
  if (enabledValue) {
    inputLatencyTraceConsumers.add(consumer);
  } else {
    inputLatencyTraceConsumers.delete(consumer);
  }
  syncInputLatencyTracingEnabled();
}

export function setInputLatencyTracingEnabled(enabledValue: boolean): void {
  setInputLatencyTraceConsumerEnabled('legacy', enabledValue);
}

export function onInputLatencyTrace(cb: InputLatencyTraceListener): void {
  inputTraceListeners.add(cb);
}

export function offInputLatencyTrace(cb: InputLatencyTraceListener): void {
  inputTraceListeners.delete(cb);
}

export function resetInputLatencyTraceRuntime(clearListeners = false): void {
  inputLatencyTraceConsumers.clear();
  inputLatencyTracingEnabled = false;
  pendingInputTraces.clear();
  inputTraceSessionState.clear();
  lastOutputReceivedBySession.clear();
  lastOutputParsedBySession.clear();
  if (clearListeners) {
    inputTraceListeners.clear();
  }
}

export function clearInputLatencyTraceInFlight(): void {
  pendingInputTraces.clear();
  inputTraceSessionState.clear();
}

function allocateInputTraceId(): number {
  nextInputTraceId = (nextInputTraceId + 1) >>> 0;
  if (nextInputTraceId === 0) {
    nextInputTraceId = 1;
  }
  return nextInputTraceId;
}

function getInputTraceSessionState(sessionId: string): {
  lastStartedAtMs: number;
  inFlightTraceId: number | null;
} {
  let state = inputTraceSessionState.get(sessionId);
  if (!state) {
    state = { lastStartedAtMs: -Infinity, inFlightTraceId: null };
    inputTraceSessionState.set(sessionId, state);
  }
  return state;
}

export function maybeSendInputTraceMarker(
  sessionId: string,
  nowMs: number,
  sendFrame: (frame: Uint8Array) => void,
  encodeSessionId: (buffer: Uint8Array, offset: number, sessionId: string) => void,
): void {
  if (!inputLatencyTracingEnabled) {
    return;
  }

  const state = getInputTraceSessionState(sessionId);
  if (state.inFlightTraceId !== null) {
    const inFlight = pendingInputTraces.get(state.inFlightTraceId);
    if (inFlight && nowMs - inFlight.browserInputAtMs < INPUT_TRACE_TIMEOUT_MS) {
      return;
    }
    if (inFlight) {
      pendingInputTraces.delete(inFlight.traceId);
    }
    state.inFlightTraceId = null;
  }

  if (nowMs - state.lastStartedAtMs < INPUT_TRACE_MIN_INTERVAL_MS) {
    return;
  }

  const traceId = allocateInputTraceId();
  const frame = new Uint8Array(MUX_HEADER_SIZE + 4);
  const view = new DataView(frame.buffer);
  frame[0] = MUX_TYPE_INPUT_TRACE_MARKER;
  encodeSessionId(frame, 1, sessionId);
  view.setUint32(MUX_HEADER_SIZE, traceId, true);
  pendingInputTraces.set(traceId, { traceId, sessionId, browserInputAtMs: nowMs });
  state.inFlightTraceId = traceId;
  state.lastStartedAtMs = nowMs;
  sendFrame(frame);
}

function isSequenceNewer(candidate: bigint, current: bigint): boolean {
  const delta = (candidate - current + SEQUENCE_MODULUS) % SEQUENCE_MODULUS;
  return delta !== 0n && delta < HALF_SEQUENCE_RANGE;
}

function hasSequenceReached(observed: bigint, target: bigint): boolean {
  return observed === target || isSequenceNewer(observed, target);
}

function readOutputSequenceEnd(payload: Uint8Array): bigint | null {
  if (payload.length < 8) {
    return null;
  }

  const view = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
  return view.getBigUint64(0, true);
}

export function recordInputTraceOutputReceived(sessionId: string, payload: Uint8Array): void {
  if (pendingInputTraces.size === 0) {
    return;
  }

  const sequenceEnd = readOutputSequenceEnd(payload);
  if (sequenceEnd === null) {
    return;
  }

  lastOutputReceivedBySession.set(sessionId, {
    sequenceEnd,
    atMs: performance.now(),
  });
  tryCompleteInputTracesForSession(sessionId);
}

export function recordInputTraceOutputParsed(sessionId: string, sequenceEnd: bigint): void {
  if (pendingInputTraces.size === 0) {
    return;
  }

  lastOutputParsedBySession.set(sessionId, {
    sequenceEnd,
    atMs: performance.now(),
  });
  tryCompleteInputTracesForSession(sessionId);
}

function completeInputTrace(
  trace: PendingInputLatencyTrace,
  server: InputLatencyServerTrace,
): void {
  const state = inputTraceSessionState.get(trace.sessionId);
  if (state?.inFlightTraceId === trace.traceId) {
    state.inFlightTraceId = null;
  }
  pendingInputTraces.delete(trace.traceId);

  const received = lastOutputReceivedBySession.get(trace.sessionId);
  const parsed = lastOutputParsedBySession.get(trace.sessionId);
  const browserToOutputReceiveMs =
    received && hasSequenceReached(received.sequenceEnd, server.firstOutputSequenceEnd)
      ? received.atMs - trace.browserInputAtMs
      : null;
  const totalToXtermParsedMs =
    parsed && hasSequenceReached(parsed.sequenceEnd, server.firstOutputSequenceEnd)
      ? parsed.atMs - trace.browserInputAtMs
      : null;

  const snapshot: InputLatencyTraceSnapshot = {
    traceId: trace.traceId,
    totalToXtermParsedMs,
    browserToOutputReceiveMs,
    browserReceiveToXtermParseMs:
      browserToOutputReceiveMs !== null && totalToXtermParsedMs !== null
        ? totalToXtermParsedMs - browserToOutputReceiveMs
        : null,
    serverReceiveToIpcStartMs: server.serverReceiveToIpcStartMs,
    ipcWriteMs: server.ipcWriteMs,
    serverReceiveToMthostReceiveMs: server.serverReceiveToMthostReceiveMs,
    serverReceiveToPtyWriteDoneMs: server.serverReceiveToPtyWriteDoneMs,
    ptyWriteDoneToPtyOutputReadMs: server.ptyWriteDoneToPtyOutputReadMs,
    ptyOutputReadToMthostIpcEnqueuedMs: server.ptyOutputReadToMthostIpcEnqueuedMs,
    mthostIpcEnqueuedToWriteDoneMs: server.mthostIpcEnqueuedToWriteDoneMs,
    mthostIpcWriteDoneToFlushDoneMs: server.mthostIpcWriteDoneToFlushDoneMs,
    mthostIpcEnqueuedToServerOutputObservedMs: server.mthostIpcEnqueuedToServerOutputObservedMs,
    serverReceiveToOutputObservedMs: server.serverReceiveToOutputObservedMs,
    outputObservedToMuxQueuedMs: server.outputObservedToMuxQueuedMs,
    muxQueuedToClientQueuedMs: server.muxQueuedToClientQueuedMs,
    clientQueuedToWsFlushMs: server.clientQueuedToWsFlushMs,
    serverReceiveToWsFlushMs: server.serverReceiveToWsFlushMs,
  };

  for (const listener of inputTraceListeners) {
    listener(trace.sessionId, snapshot);
  }
}

function tryCompleteInputTracesForSession(sessionId: string): void {
  const parsed = lastOutputParsedBySession.get(sessionId);
  if (!parsed) {
    return;
  }

  for (const trace of [...pendingInputTraces.values()]) {
    const server = trace.server;
    if (
      trace.sessionId === sessionId &&
      server &&
      hasSequenceReached(parsed.sequenceEnd, server.firstOutputSequenceEnd)
    ) {
      completeInputTrace(trace, server);
    }
  }
}

export function handleMuxInputTraceResultFrame(
  type: number,
  sessionId: string,
  payload: Uint8Array,
): boolean {
  if (type !== MUX_TYPE_INPUT_TRACE_RESULT) {
    return false;
  }

  if (payload.length < 68) {
    return true;
  }

  const view = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
  const traceId = view.getUint32(0, true);
  const trace = pendingInputTraces.get(traceId);
  if (!trace || trace.sessionId !== sessionId) {
    return true;
  }

  trace.server = {
    firstOutputSequenceEnd: view.getBigUint64(4, true),
    serverReceiveToIpcStartMs: view.getInt32(12, true),
    ipcWriteMs: view.getInt32(16, true),
    serverReceiveToMthostReceiveMs: view.getInt32(20, true),
    serverReceiveToPtyWriteDoneMs: view.getInt32(24, true),
    ptyWriteDoneToPtyOutputReadMs: view.getInt32(28, true),
    ptyOutputReadToMthostIpcEnqueuedMs: view.getInt32(32, true),
    mthostIpcEnqueuedToWriteDoneMs: view.getInt32(36, true),
    mthostIpcWriteDoneToFlushDoneMs: view.getInt32(40, true),
    mthostIpcEnqueuedToServerOutputObservedMs: view.getInt32(44, true),
    serverReceiveToOutputObservedMs: view.getInt32(48, true),
    outputObservedToMuxQueuedMs: view.getInt32(52, true),
    muxQueuedToClientQueuedMs: view.getInt32(56, true),
    clientQueuedToWsFlushMs: view.getInt32(60, true),
    serverReceiveToWsFlushMs: view.getInt32(64, true),
  };
  tryCompleteInputTracesForSession(sessionId);
  return true;
}
