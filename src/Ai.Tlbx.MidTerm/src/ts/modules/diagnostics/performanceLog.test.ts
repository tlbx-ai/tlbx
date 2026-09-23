import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  getPerformanceLogState,
  startPerformanceLog,
  stopPerformanceLog,
} from './performanceLog';

const mocks = vi.hoisted(() => ({
  traceListener: null as ((sessionId: string, trace: Record<string, number>) => void) | null,
  consumerEnabled: false,
}));

vi.mock('../../stores', () => ({ $activeSessionId: { get: () => 'session1' } }));
vi.mock('../comms', () => ({
  getBrowserTransportSnapshot: () => ({
    receivedSeq: 12n,
    submittedSeq: 12n,
    renderedSeq: 10n,
    dataLossCount: 0,
    recoveryCompleted: 0,
    recoveryGapCount: 0,
    lastReplayReason: null,
  }),
  onInputLatencyTrace: (listener: typeof mocks.traceListener) => {
    mocks.traceListener = listener;
  },
  offInputLatencyTrace: () => {
    mocks.traceListener = null;
  },
  setInputLatencyTraceConsumerEnabled: (_name: string, enabled: boolean) => {
    mocks.consumerEnabled = enabled;
  },
}));

describe('performance log', () => {
  let clockMs: number;
  let frames: Map<number, FrameRequestCallback>;
  let nextFrameId: number;
  let page: EventTarget & { visibilityState: DocumentVisibilityState };
  let observedOptions: PerformanceObserverInit[];

  function renderFrame(atMs: number): void {
    clockMs = atMs;
    const callbacks = [...frames.values()];
    frames.clear();
    callbacks.forEach((callback) => callback(atMs));
  }

  beforeEach(() => {
    clockMs = 0;
    frames = new Map();
    nextFrameId = 0;
    page = Object.assign(new EventTarget(), {
      visibilityState: 'visible' as DocumentVisibilityState,
    });
    observedOptions = [];
    vi.spyOn(performance, 'now').mockImplementation(() => clockMs);
    vi.stubGlobal('document', page);
    vi.stubGlobal(
      'window',
      Object.assign(new EventTarget(), {
        location: { origin: 'https://tlbx.test', pathname: '/' },
        devicePixelRatio: 1,
        setInterval,
        setTimeout,
      }),
    );
    vi.stubGlobal('navigator', { userAgent: 'test browser', hardwareConcurrency: 8 });
    vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => {
      const id = ++nextFrameId;
      frames.set(id, callback);
      return id;
    });
    vi.stubGlobal('cancelAnimationFrame', (id: number) => {
      frames.delete(id);
    });
    vi.stubGlobal(
      'PerformanceObserver',
      class {
        observe(options: PerformanceObserverInit): void {
          observedOptions.push(options);
        }
        disconnect(): void {}
      },
    );
  });

  afterEach(() => {
    stopPerformanceLog();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('pauses in a hidden tab without treating background time as a frame gap', () => {
    startPerformanceLog();
    expect(mocks.consumerEnabled).toBe(true);
    expect(observedOptions).toEqual([{ type: 'longtask' }]);
    renderFrame(10);
    renderFrame(90);
    clockMs = 100;
    page.visibilityState = 'hidden';
    page.dispatchEvent(new Event('visibilitychange'));
    expect(getPerformanceLogState()).toBe('paused');
    expect(mocks.consumerEnabled).toBe(false);
    renderFrame(5000);
    clockMs = 5010;
    page.visibilityState = 'visible';
    page.dispatchEvent(new Event('visibilitychange'));
    renderFrame(5020);
    renderFrame(5040);
    const report = stopPerformanceLog();

    expect(
      report?.events.filter((event) => event.type === 'frame-gap').map((event) => event.durationMs),
    ).toEqual([80]);
    expect(report?.events.map((event) => event.type)).toContain('paused');
    expect(report?.events.map((event) => event.type)).toContain('resumed');
    expect(report?.activeDurationMs).toBe(130);
    expect(report?.pausedDurationMs).toBe(4910);
    expect(mocks.consumerEnabled).toBe(false);
  });

  it('records existing input traces and transport counters', () => {
    startPerformanceLog();
    mocks.traceListener?.('session1', { totalToXtermParsedMs: 123 });
    const report = stopPerformanceLog();
    expect(report?.events.find((event) => event.type === 'input-trace')?.trace).toEqual({
      totalToXtermParsedMs: 123,
    });
    expect(
      report?.events.find((event) => event.type === 'transport')?.transport?.renderBacklogBytes,
    ).toBe('2');
  });
});
