import type { TerminalState } from '../../types';
import { sessionTerminals } from '../../state';
import { $activeSessionId } from '../../stores';
import { isHubSessionId } from '../hub/runtime';

const BACKGROUND_REPLAY_GATE_TIMEOUT_MS = 5000;

type BackgroundReplayGatePhase = 'awaitingReset' | 'awaitingReplayFrame';

export interface OutputFrameEnvelope {
  sequenceEnd: bigint;
  cols: number;
  rows: number;
  dataBytes: number;
}

const backgroundSkippedSessions = new Set<string>();
const backgroundReplayGates = new Map<string, BackgroundReplayGatePhase>();
const backgroundReplayGateTimeouts = new Map<string, number>();

export function parseOutputFrameEnvelope(
  payload: Uint8Array,
  compressed: boolean,
): OutputFrameEnvelope {
  const view = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
  return {
    sequenceEnd: payload.length >= 8 ? view.getBigUint64(0, true) : 0n,
    cols: (payload[8] ?? 0) | ((payload[9] ?? 0) << 8),
    rows: (payload[10] ?? 0) | ((payload[11] ?? 0) << 8),
    dataBytes: Math.max(0, payload.length - (compressed ? 16 : 12)),
  };
}

export function isSessionStreamable(
  sessionId: string,
  currentStreamableSessionIds: ReadonlySet<string>,
  currentVisibleSessionIds: readonly string[],
): boolean {
  if (isHubSessionId(sessionId)) {
    return true;
  }

  const activeSessionId = $activeSessionId.get();
  return (
    sessionId === activeSessionId ||
    currentStreamableSessionIds.has(sessionId) ||
    currentVisibleSessionIds.includes(sessionId)
  );
}

function updateServerDimensionsFromEnvelope(
  state: TerminalState | undefined,
  envelope: OutputFrameEnvelope,
): void {
  if (!state || envelope.cols <= 0 || envelope.rows <= 0) {
    return;
  }

  state.serverCols = envelope.cols;
  state.serverRows = envelope.rows;
}

export function noteBackgroundFrameDeferred(
  sessionId: string,
  envelope: OutputFrameEnvelope,
): void {
  updateServerDimensionsFromEnvelope(sessionTerminals.get(sessionId), envelope);
  if (envelope.dataBytes > 0 || envelope.sequenceEnd > 0n) {
    backgroundSkippedSessions.add(sessionId);
  }
}

export function getBackgroundReplayGatePhase(
  sessionId: string,
): BackgroundReplayGatePhase | undefined {
  return backgroundReplayGates.get(sessionId);
}

export function markBackgroundReplayAwaitingFrame(sessionId: string): void {
  backgroundReplayGates.set(sessionId, 'awaitingReplayFrame');
}

export function clearBackgroundReplayGate(sessionId: string): void {
  const timeout = backgroundReplayGateTimeouts.get(sessionId);
  if (timeout !== undefined) {
    clearTimeout(timeout);
    backgroundReplayGateTimeouts.delete(sessionId);
  }
  backgroundReplayGates.delete(sessionId);
}

export function clearCompletedBackgroundReplay(sessionId: string): void {
  clearBackgroundReplayGate(sessionId);
  backgroundSkippedSessions.delete(sessionId);
}

export function clearAllBackgroundReplayState(): void {
  backgroundSkippedSessions.clear();
  backgroundReplayGates.clear();
  backgroundReplayGateTimeouts.forEach((timeout) => {
    clearTimeout(timeout);
  });
  backgroundReplayGateTimeouts.clear();
}

export function armBackgroundReplayGate(sessionId: string): void {
  if (!backgroundSkippedSessions.has(sessionId)) {
    return;
  }

  clearBackgroundReplayGate(sessionId);
  backgroundReplayGates.set(sessionId, 'awaitingReset');
  const timeout = window.setTimeout(() => {
    backgroundReplayGates.delete(sessionId);
    backgroundReplayGateTimeouts.delete(sessionId);
    backgroundSkippedSessions.delete(sessionId);
  }, BACKGROUND_REPLAY_GATE_TIMEOUT_MS);
  backgroundReplayGateTimeouts.set(sessionId, timeout);
}

export function prepareBackgroundOutputDelivery(
  sessionId: string,
  payload: Uint8Array,
  compressed: boolean,
  currentStreamableSessionIds: ReadonlySet<string>,
  currentVisibleSessionIds: readonly string[],
): boolean | null {
  const envelope = parseOutputFrameEnvelope(payload, compressed);
  if (!isSessionStreamable(sessionId, currentStreamableSessionIds, currentVisibleSessionIds)) {
    noteBackgroundFrameDeferred(sessionId, envelope);
    return null;
  }

  const replayGate = getBackgroundReplayGatePhase(sessionId);
  if (replayGate === 'awaitingReset' && envelope.sequenceEnd !== 0n) {
    noteBackgroundFrameDeferred(sessionId, envelope);
    return null;
  }
  if (replayGate === 'awaitingReset' && envelope.sequenceEnd === 0n) {
    markBackgroundReplayAwaitingFrame(sessionId);
  }

  return replayGate === 'awaitingReplayFrame' && envelope.sequenceEnd !== 0n;
}

export function finishBackgroundOutputDelivery(
  sessionId: string,
  clearReplayGateAfterFrame: boolean,
): void {
  if (clearReplayGateAfterFrame) {
    clearCompletedBackgroundReplay(sessionId);
  }
}
