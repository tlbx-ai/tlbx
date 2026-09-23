import { $activeSessionId } from '../../stores';
import {
  getBrowserTransportSnapshot,
  offInputLatencyTrace,
  onInputLatencyTrace,
  setInputLatencyTraceConsumerEnabled,
} from '../comms';
import type { InputLatencyTraceSnapshot } from '../comms';
import { calculatePerfFrameStats } from '../perf/frameRecorder';
import type { PerfFrameStats } from '../perf/frameRecorder';

type PerformanceLogEvent = {
  atMs: number;
  type:
    | 'started'
    | 'paused'
    | 'resumed'
    | 'stopped'
    | 'frame-gap'
    | 'long-task'
    | 'input-trace'
    | 'transport'
    | 'focus-change';
  sessionId?: string;
  durationMs?: number;
  reason?: string;
  trace?: InputLatencyTraceSnapshot;
  transport?: Record<string, string | number | null>;
};

export interface PerformanceLogReport {
  schemaVersion: 1;
  startedAt: string;
  stoppedAt: string;
  page: string;
  browser: {
    userAgent: string;
    hardwareConcurrency: number;
    devicePixelRatio: number;
  };
  activeDurationMs: number;
  pausedDurationMs: number;
  droppedEvents: number;
  frameStats: PerfFrameStats;
  events: PerformanceLogEvent[];
}

export type PerformanceLogState = 'idle' | 'recording' | 'paused' | 'stopped';

const MAX_EVENTS = 10_000;
const MAX_FRAME_SAMPLES = 6000;
const FRAME_GAP_THRESHOLD_MS = 50;
const TRANSPORT_SAMPLE_MS = 1000;
const TRACE_CONSUMER = 'performance-log';

let state: PerformanceLogState = 'idle';
let startedAt = '';
let stoppedAt = '';
let startedMs = 0;
let activeSinceMs = 0;
let pausedSinceMs = 0;
let activeDurationMs = 0;
let pausedDurationMs = 0;
let droppedEvents = 0;
let events: PerformanceLogEvent[] = [];
let frameSamples: number[] = [];
let frameId: number | null = null;
let transportTimer: number | null = null;
let lastFrameMs: number | null = null;
let longTaskObserver: PerformanceObserver | null = null;
let stateListener: (() => void) | null = null;

function addEvent(event: Omit<PerformanceLogEvent, 'atMs'>, atMs = performance.now()): void {
  events.push({ atMs: Math.round(atMs - startedMs), ...event });
  if (events.length > MAX_EVENTS) {
    events.splice(0, 1000);
    droppedEvents += 1000;
  }
}

function notifyState(): void {
  stateListener?.();
}

function recordFrame(now: number): void {
  if (state !== 'recording') return;
  if (lastFrameMs !== null) {
    const gap = now - lastFrameMs;
    frameSamples.push(gap);
    if (frameSamples.length > MAX_FRAME_SAMPLES) frameSamples.splice(0, 1000);
    if (gap > FRAME_GAP_THRESHOLD_MS) {
      addEvent({ type: 'frame-gap', durationMs: Math.round(gap) }, now);
    }
  }
  lastFrameMs = now;
  frameId = requestAnimationFrame(recordFrame);
}

function recordTransport(): void {
  const sessionId = $activeSessionId.get();
  if (!sessionId) return;
  const snapshot = getBrowserTransportSnapshot(sessionId);
  if (!snapshot) return;
  addEvent({
    type: 'transport',
    sessionId,
    transport: {
      receivedSeq: snapshot.receivedSeq.toString(),
      submittedSeq: snapshot.submittedSeq.toString(),
      renderedSeq: snapshot.renderedSeq.toString(),
      renderBacklogBytes: (snapshot.receivedSeq - snapshot.renderedSeq).toString(),
      dataLossCount: snapshot.dataLossCount,
      recoveryCompleted: snapshot.recoveryCompleted,
      recoveryGapCount: snapshot.recoveryGapCount,
      lastReplayReason: snapshot.lastReplayReason,
    },
  });
}

function recordInputTrace(sessionId: string, trace: InputLatencyTraceSnapshot): void {
  if (state === 'recording') addEvent({ type: 'input-trace', sessionId, trace });
}

function startActiveSampling(): void {
  if (state !== 'recording') return;
  activeSinceMs = performance.now();
  lastFrameMs = null;
  setInputLatencyTraceConsumerEnabled(TRACE_CONSUMER, true);
  onInputLatencyTrace(recordInputTrace);
  frameId = requestAnimationFrame(recordFrame);
  recordTransport();
  transportTimer = window.setInterval(recordTransport, TRANSPORT_SAMPLE_MS);
  if (typeof PerformanceObserver !== 'undefined') {
    try {
      longTaskObserver = new PerformanceObserver((list) => {
        for (const entry of list.getEntries()) {
          if (state === 'recording' && entry.startTime >= activeSinceMs) {
            addEvent(
              { type: 'long-task', durationMs: Math.round(entry.duration) },
              entry.startTime,
            );
          }
        }
      });
      // Do not request buffered entries: they can predate this recording.
      longTaskObserver.observe({ type: 'longtask' });
    } catch {
      longTaskObserver = null;
    }
  }
}

function stopActiveSampling(): void {
  if (frameId !== null) cancelAnimationFrame(frameId);
  frameId = null;
  if (transportTimer !== null) clearInterval(transportTimer);
  transportTimer = null;
  longTaskObserver?.disconnect();
  longTaskObserver = null;
  offInputLatencyTrace(recordInputTrace);
  setInputLatencyTraceConsumerEnabled(TRACE_CONSUMER, false);
  lastFrameMs = null;
}

function pause(reason: string): void {
  if (state !== 'recording') return;
  recordTransport();
  activeDurationMs += performance.now() - activeSinceMs;
  stopActiveSampling();
  state = 'paused';
  pausedSinceMs = performance.now();
  addEvent({ type: 'paused', reason });
  notifyState();
}

function resume(): void {
  if (state !== 'paused' || document.visibilityState === 'hidden') return;
  pausedDurationMs += performance.now() - pausedSinceMs;
  state = 'recording';
  addEvent({ type: 'resumed' });
  startActiveSampling();
  notifyState();
}

function handleVisibilityChange(): void {
  if (document.visibilityState === 'hidden') pause('tab-hidden');
  else resume();
}

export function setPerformanceLogStateListener(listener: (() => void) | null): void {
  stateListener = listener;
}

export function getPerformanceLogState(): PerformanceLogState {
  return state;
}

export function getPerformanceLogEventCount(): number {
  return events.length;
}

export function startPerformanceLog(): void {
  if (state === 'recording' || state === 'paused') return;
  stopActiveSampling();
  events = [];
  frameSamples = [];
  droppedEvents = 0;
  activeDurationMs = 0;
  pausedDurationMs = 0;
  startedAt = new Date().toISOString();
  stoppedAt = '';
  startedMs = performance.now();
  addEvent({ type: 'started' });
  document.addEventListener('visibilitychange', handleVisibilityChange);
  window.addEventListener('pagehide', handlePageHide);
  window.addEventListener('pageshow', handlePageShow);
  window.addEventListener('focus', handleFocus);
  window.addEventListener('blur', handleBlur);
  state = document.visibilityState === 'hidden' ? 'paused' : 'recording';
  if (state === 'paused') {
    pausedSinceMs = performance.now();
    addEvent({ type: 'paused', reason: 'tab-hidden' });
  } else {
    startActiveSampling();
  }
  notifyState();
}

function handlePageHide(): void {
  pause('pagehide');
}

function handlePageShow(): void {
  resume();
}

function handleFocus(): void {
  if (state === 'recording') addEvent({ type: 'focus-change', reason: 'focused' });
}

function handleBlur(): void {
  if (state === 'recording') addEvent({ type: 'focus-change', reason: 'blurred' });
}

export function stopPerformanceLog(): PerformanceLogReport | null {
  if (state === 'idle') return null;
  if (state === 'recording') {
    recordTransport();
    activeDurationMs += performance.now() - activeSinceMs;
  } else if (state === 'paused') {
    pausedDurationMs += performance.now() - pausedSinceMs;
  }
  stopActiveSampling();
  document.removeEventListener('visibilitychange', handleVisibilityChange);
  window.removeEventListener('pagehide', handlePageHide);
  window.removeEventListener('pageshow', handlePageShow);
  window.removeEventListener('focus', handleFocus);
  window.removeEventListener('blur', handleBlur);
  if (state !== 'stopped') {
    state = 'stopped';
    stoppedAt = new Date().toISOString();
    addEvent({ type: 'stopped' });
    notifyState();
  }
  return getPerformanceLogReport();
}

export function getPerformanceLogReport(): PerformanceLogReport | null {
  if (state === 'idle') return null;
  const now = performance.now();
  return {
    schemaVersion: 1,
    startedAt,
    stoppedAt: stoppedAt || new Date().toISOString(),
    page: window.location.origin + window.location.pathname,
    browser: {
      userAgent: navigator.userAgent,
      hardwareConcurrency: navigator.hardwareConcurrency,
      devicePixelRatio: window.devicePixelRatio,
    },
    activeDurationMs: Math.round(
      activeDurationMs + (state === 'recording' ? now - activeSinceMs : 0),
    ),
    pausedDurationMs: Math.round(pausedDurationMs + (state === 'paused' ? now - pausedSinceMs : 0)),
    droppedEvents,
    frameStats: calculatePerfFrameStats(frameSamples),
    events: [...events],
  };
}

export function downloadPerformanceLog(): string | null {
  const report = getPerformanceLogReport();
  if (!report) return null;
  const filename = `tlbx-performance-${report.startedAt.replace(/[:.]/g, '-')}.json`;
  const url = URL.createObjectURL(
    new Blob([JSON.stringify(report, null, 2)], { type: 'application/json' }),
  );
  const link = document.createElement('a');
  link.href = url;
  link.download = filename;
  link.click();
  window.setTimeout(() => {
    URL.revokeObjectURL(url);
  }, 1000);
  return filename;
}
