export interface PerfFrameStats {
  count: number;
  p50Ms: number | null;
  p95Ms: number | null;
  p99Ms: number | null;
  maxMs: number | null;
  over50Ms: number;
  over100Ms: number;
  over250Ms: number;
  effectiveFpsP95: number | null;
}

export interface PerfLongTaskEntry {
  name: string;
  startTime: number;
  duration: number;
}

export interface PerfTerminalSummary {
  activeId: string | null;
  terminalCount: number;
  terminals: Array<{
    id: string;
    active: boolean;
    opened: boolean;
    hasWebgl: boolean;
    cols: number | null;
    rows: number | null;
    bufferLength: number | null;
    baseY: number | null;
    viewportY: number | null;
    visible: boolean;
    width: number;
    height: number;
  }>;
}

export interface PerfBrowserSummary {
  url: string;
  visibilityState: DocumentVisibilityState;
  documentHidden: boolean;
  focused: boolean;
  viewportWidth: number;
  viewportHeight: number;
  devicePixelRatio: number;
  hardwareConcurrency: number | null;
  deviceMemoryGb: number | null;
  usedJsHeapSizeMb: number | null;
  totalJsHeapSizeMb: number | null;
  jsHeapSizeLimitMb: number | null;
  canvasCount: number;
  xtermCanvasCount: number;
}

export interface PerfRecorderSnapshot {
  running: boolean;
  startedAt: string | null;
  now: string;
  frameStats: PerfFrameStats;
  recentFramesMs: number[];
  worstFramesMs: number[];
  longTaskCount: number;
  longTasks: PerfLongTaskEntry[];
  browserSummary: PerfBrowserSummary;
  terminalSummary: PerfTerminalSummary | null;
}

export interface PerfDebugApi {
  start: () => PerfRecorderSnapshot;
  stop: () => PerfRecorderSnapshot;
  reset: () => PerfRecorderSnapshot;
  snapshot: () => PerfRecorderSnapshot;
  copy: () => Promise<PerfRecorderSnapshot>;
}

const DEFAULT_MAX_FRAMES = 6000;
const DEFAULT_MAX_LONG_TASKS = 200;

interface PerfRecorderState {
  running: boolean;
  startedAt: string | null;
  frameRequestId: number | null;
  lastFrameAt: number | null;
  frames: number[];
  longTasks: PerfLongTaskEntry[];
  longTaskObserver: PerformanceObserver | null;
}

interface NavigatorWithDeviceMemory extends Navigator {
  deviceMemory?: number;
}

interface PerformanceWithMemory extends Performance {
  memory?: {
    usedJSHeapSize: number;
    totalJSHeapSize: number;
    jsHeapSizeLimit: number;
  };
}

function bytesToRoundedMb(bytes: number | undefined): number | null {
  return typeof bytes === 'number' ? Math.round((bytes / 1024 / 1024) * 10) / 10 : null;
}

export function calculatePerfFrameStats(frames: readonly number[]): PerfFrameStats {
  const sorted = [...frames].sort((a, b) => a - b);
  const percentile = (p: number): number | null => {
    if (!sorted.length) {
      return null;
    }

    const index = Math.min(sorted.length - 1, Math.floor(sorted.length * p));
    return sorted[index] ?? null;
  };
  const p95Ms = percentile(0.95);

  return {
    count: sorted.length,
    p50Ms: percentile(0.5),
    p95Ms,
    p99Ms: percentile(0.99),
    maxMs: sorted[sorted.length - 1] ?? null,
    over50Ms: sorted.filter((value) => value > 50).length,
    over100Ms: sorted.filter((value) => value > 100).length,
    over250Ms: sorted.filter((value) => value > 250).length,
    effectiveFpsP95: p95Ms && p95Ms > 0 ? Math.round((1000 / p95Ms) * 10) / 10 : null,
  };
}

function summarizeBrowser(): PerfBrowserSummary {
  const performanceWithMemory = window.performance as PerformanceWithMemory;
  const navigatorWithDeviceMemory = window.navigator as NavigatorWithDeviceMemory;

  return {
    url: window.location.href,
    visibilityState: document.visibilityState,
    documentHidden: document.hidden,
    focused: document.hasFocus(),
    viewportWidth: window.innerWidth,
    viewportHeight: window.innerHeight,
    devicePixelRatio: window.devicePixelRatio,
    hardwareConcurrency: window.navigator.hardwareConcurrency || null,
    deviceMemoryGb: navigatorWithDeviceMemory.deviceMemory ?? null,
    usedJsHeapSizeMb: bytesToRoundedMb(performanceWithMemory.memory?.usedJSHeapSize),
    totalJsHeapSizeMb: bytesToRoundedMb(performanceWithMemory.memory?.totalJSHeapSize),
    jsHeapSizeLimitMb: bytesToRoundedMb(performanceWithMemory.memory?.jsHeapSizeLimit),
    canvasCount: document.querySelectorAll('canvas').length,
    xtermCanvasCount: document.querySelectorAll('.xterm canvas').length,
  };
}

export function createPerfDebugApi(
  getTerminalSummary: () => PerfTerminalSummary | null,
  options: { maxFrames?: number; maxLongTasks?: number } = {},
): PerfDebugApi {
  const maxFrames = Math.max(60, options.maxFrames ?? DEFAULT_MAX_FRAMES);
  const maxLongTasks = Math.max(10, options.maxLongTasks ?? DEFAULT_MAX_LONG_TASKS);
  const state: PerfRecorderState = {
    running: false,
    startedAt: null,
    frameRequestId: null,
    lastFrameAt: null,
    frames: [],
    longTasks: [],
    longTaskObserver: null,
  };

  const trimFrameRing = (): void => {
    if (state.frames.length > maxFrames) {
      state.frames.splice(0, state.frames.length - maxFrames);
    }
  };

  const trimLongTaskRing = (): void => {
    if (state.longTasks.length > maxLongTasks) {
      state.longTasks.splice(0, state.longTasks.length - maxLongTasks);
    }
  };

  const recordFrame = (timestamp: number): void => {
    if (!state.running) {
      return;
    }

    if (state.lastFrameAt !== null) {
      state.frames.push(timestamp - state.lastFrameAt);
      trimFrameRing();
    }

    state.lastFrameAt = timestamp;
    state.frameRequestId = window.requestAnimationFrame(recordFrame);
  };

  const attachLongTaskObserver = (): void => {
    if (typeof PerformanceObserver === 'undefined') {
      return;
    }

    try {
      state.longTaskObserver = new PerformanceObserver((list) => {
        for (const entry of list.getEntries()) {
          state.longTasks.push({
            name: entry.name,
            startTime: entry.startTime,
            duration: entry.duration,
          });
        }
        trimLongTaskRing();
      });
      state.longTaskObserver.observe({ type: 'longtask', buffered: true });
    } catch {
      state.longTaskObserver = null;
    }
  };

  const detachLongTaskObserver = (): void => {
    state.longTaskObserver?.disconnect();
    state.longTaskObserver = null;
  };

  const resetData = (): void => {
    state.frames.length = 0;
    state.longTasks.length = 0;
    state.lastFrameAt = null;
  };

  const snapshot = (): PerfRecorderSnapshot => {
    const worstFramesMs = [...state.frames].sort((a, b) => b - a).slice(0, 20);
    return {
      running: state.running,
      startedAt: state.startedAt,
      now: new Date().toISOString(),
      frameStats: calculatePerfFrameStats(state.frames),
      recentFramesMs: state.frames.slice(-60),
      worstFramesMs,
      longTaskCount: state.longTasks.length,
      longTasks: state.longTasks.slice(-20),
      browserSummary: summarizeBrowser(),
      terminalSummary: getTerminalSummary(),
    };
  };

  const stop = (): PerfRecorderSnapshot => {
    state.running = false;
    if (state.frameRequestId !== null) {
      window.cancelAnimationFrame(state.frameRequestId);
      state.frameRequestId = null;
    }
    detachLongTaskObserver();
    return snapshot();
  };

  const start = (): PerfRecorderSnapshot => {
    if (state.running) {
      return snapshot();
    }

    resetData();
    state.running = true;
    state.startedAt = new Date().toISOString();
    attachLongTaskObserver();
    state.frameRequestId = window.requestAnimationFrame(recordFrame);
    return snapshot();
  };

  return {
    start,
    stop,
    reset: () => {
      const wasRunning = state.running;
      stop();
      resetData();
      state.startedAt = null;
      if (wasRunning) {
        return start();
      }
      return snapshot();
    },
    snapshot,
    copy: async () => {
      const current = snapshot();
      await navigator.clipboard.writeText(JSON.stringify(current, null, 2));
      return current;
    },
  };
}
