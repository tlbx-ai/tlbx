import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { $sessions } from '../../stores';

interface HeatElementMock {
  style: {
    setProperty: ReturnType<typeof vi.fn>;
  };
}

function createHeatElementMock(): HeatElementMock {
  return {
    style: {
      setProperty: vi.fn(),
    },
  };
}

vi.mock('../logging', () => ({
  createLogger: () => ({
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    verbose: vi.fn(),
  }),
}));

let destroyHeatIndicator: typeof import('./heatIndicator').destroyHeatIndicator;
let getDisplayedSessionHeat: typeof import('./heatIndicator').getDisplayedSessionHeat;
let getSessionHeat: typeof import('./heatIndicator').getSessionHeat;
let initHeatIndicator: typeof import('./heatIndicator').initHeatIndicator;
let pruneHeatSessions: typeof import('./heatIndicator').pruneHeatSessions;
let recordBytes: typeof import('./heatIndicator').recordBytes;
let registerHeatCanvas: typeof import('./heatIndicator').registerHeatCanvas;
let setSessionHeat: typeof import('./heatIndicator').setSessionHeat;
let unregisterHeatCanvas: typeof import('./heatIndicator').unregisterHeatCanvas;
const heatIndicatorModulePromise = import('./heatIndicator');

describe('heatIndicator', () => {
  let nowMs = Date.parse('2026-03-24T12:00:00.000Z');
  let timeoutCallbacks = new Map<number, () => void>();
  let nextTimeoutId = 1;
  let visibilityChangeListeners: Array<() => void> = [];
  let windowEventListeners = new Map<string, Array<() => void>>();
  let documentMock: { hidden: boolean; addEventListener: ReturnType<typeof vi.fn> };

  async function flushPromises(): Promise<void> {
    await Promise.resolve();
    await Promise.resolve();
  }

  function advanceTime(durationMs: number): void {
    nowMs += durationMs;
  }

  async function runTimeouts(): Promise<void> {
    const callbacks = [...timeoutCallbacks.entries()];
    timeoutCallbacks.clear();
    callbacks.forEach(([, callback]) => callback());
    await flushPromises();
  }

  async function advanceTimeout(durationMs: number): Promise<void> {
    advanceTime(durationMs);
    await runTimeouts();
  }

  function setDocumentHidden(hidden: boolean): void {
    documentMock.hidden = hidden;
    visibilityChangeListeners.forEach((listener) => listener());
  }

  function setStoreSession(
    sessionId: string,
    currentHeat: number,
    lastOutputAt?: string | null,
  ): void {
    $sessions.set({
      [sessionId]: {
        id: sessionId,
        _order: 0,
        cols: 120,
        rows: 30,
        supervisor: {
          currentHeat,
          lastOutputAt: lastOutputAt ?? null,
        },
      } as any,
    });
  }

  beforeAll(async () => {
    ({
      destroyHeatIndicator,
      getDisplayedSessionHeat,
      getSessionHeat,
      initHeatIndicator,
      pruneHeatSessions,
      recordBytes,
      registerHeatCanvas,
      setSessionHeat,
      unregisterHeatCanvas,
    } = await heatIndicatorModulePromise);
  });

  beforeEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();

    timeoutCallbacks = new Map<number, () => void>();
    nextTimeoutId = 1;
    visibilityChangeListeners = [];
    windowEventListeners = new Map<string, Array<() => void>>();
    nowMs = Date.parse('2026-03-24T12:00:00.000Z');
    vi.spyOn(Date, 'now').mockImplementation(() => nowMs);

    documentMock = {
      hidden: false,
      addEventListener: vi.fn((event: string, callback: () => void) => {
        if (event === 'visibilitychange') {
          visibilityChangeListeners.push(callback);
        }
      }),
    };

    vi.stubGlobal('window', {
      addEventListener: vi.fn((event: string, callback: () => void) => {
        const listeners = windowEventListeners.get(event) ?? [];
        listeners.push(callback);
        windowEventListeners.set(event, listeners);
      }),
      matchMedia: vi.fn(() => ({
        matches: false,
        addEventListener: vi.fn(),
      })),
      setTimeout: vi.fn((callback: () => void) => {
        const id = nextTimeoutId++;
        timeoutCallbacks.set(id, callback);
        return id;
      }),
      clearTimeout: vi.fn((id: number) => {
        timeoutCallbacks.delete(id);
      }),
      setInterval: vi.fn(() => {
        throw new Error('heat indicator must not use setInterval');
      }),
      clearInterval: vi.fn(),
    });
    vi.stubGlobal('document', documentMock);
    $sessions.set({});
    destroyHeatIndicator();
  });

  afterEach(() => {
    destroyHeatIndicator();
    $sessions.set({});
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('preserves heat state across sidebar rerenders', () => {
    const firstElement = createHeatElementMock();
    registerHeatCanvas('session-1', firstElement as unknown as HTMLElement);

    setSessionHeat('session-1', 0.8);
    advanceTime(300);
    const heatBeforeRerender = getSessionHeat('session-1');
    const displayedBeforeRerender = getDisplayedSessionHeat('session-1');

    expect(heatBeforeRerender).toBeGreaterThan(0);
    expect(displayedBeforeRerender).toBeGreaterThan(0.7);

    unregisterHeatCanvas('session-1');
    expect(getSessionHeat('session-1')).toBeCloseTo(heatBeforeRerender, 5);

    const secondElement = createHeatElementMock();
    registerHeatCanvas('session-1', secondElement as unknown as HTMLElement);

    expect(getSessionHeat('session-1')).toBeCloseTo(heatBeforeRerender, 5);
    expect(getDisplayedSessionHeat('session-1')).toBeCloseTo(displayedBeforeRerender, 3);
    expect(secondElement.style.setProperty).toHaveBeenCalled();
  });

  it('drops heat state only when the session is pruned', () => {
    const element = createHeatElementMock();
    registerHeatCanvas('session-1', element as unknown as HTMLElement);

    setSessionHeat('session-1', 0.6);
    advanceTime(300);
    expect(getSessionHeat('session-1')).toBeGreaterThan(0);

    pruneHeatSessions([]);
    expect(getSessionHeat('session-1')).toBe(0);
  });

  it('heats to red from mux output events', () => {
    const element = createHeatElementMock();
    registerHeatCanvas('session-1', element as unknown as HTMLElement);

    recordBytes('session-1', 24);

    expect(getSessionHeat('session-1')).toBe(1);
    expect(getDisplayedSessionHeat('session-1')).toBe(0);

    advanceTime(300);

    expect(getDisplayedSessionHeat('session-1')).toBeGreaterThan(0.9);
    expect(element.style.setProperty).toHaveBeenCalledWith(
      '--session-heat-transition-ms',
      '220ms',
    );
  });

  it('decays slowly enough to preserve a visible session hierarchy', async () => {
    const element = createHeatElementMock();
    registerHeatCanvas('session-1', element as unknown as HTMLElement);
    initHeatIndicator();

    recordBytes('session-1', 24);
    advanceTime(300);
    expect(getDisplayedSessionHeat('session-1')).toBeGreaterThan(0.9);

    setSessionHeat('session-1', 0);
    advanceTime(42_000);
    expect(getDisplayedSessionHeat('session-1')).toBeLessThan(1);
    expect(getDisplayedSessionHeat('session-1')).toBeGreaterThan(0.2);

    advanceTime(150_000);
    expect(getDisplayedSessionHeat('session-1')).toBe(0);
  });

  it('recomputes decayed heat from elapsed time when returning from the background', async () => {
    const element = createHeatElementMock();
    registerHeatCanvas('session-1', element as unknown as HTMLElement);
    initHeatIndicator();

    recordBytes('session-1', 24);
    advanceTime(300);
    expect(getDisplayedSessionHeat('session-1')).toBeGreaterThan(0.9);

    setDocumentHidden(true);
    advanceTime(42_000);
    setDocumentHidden(false);
    await flushPromises();

    expect(getDisplayedSessionHeat('session-1')).toBeCloseTo(0.25, 1);
    expect(element.style.setProperty).toHaveBeenCalled();
  });

  it('does not create heat from zero-heat session snapshots that only carry last output timestamps', async () => {
    const element = createHeatElementMock();
    registerHeatCanvas('session-1', element as unknown as HTMLElement);
    initHeatIndicator();

    setStoreSession('session-1', 0, new Date(nowMs).toISOString());
    await flushPromises();

    expect(getSessionHeat('session-1')).toBe(0);
    expect(getDisplayedSessionHeat('session-1')).toBe(0);
    expect(element.style.setProperty).toHaveBeenCalled();
  });

  it('syncs non-terminal supervisor heat from session state without polling', async () => {
    const element = createHeatElementMock();
    registerHeatCanvas('session-1', element as unknown as HTMLElement);
    initHeatIndicator();

    setStoreSession('session-1', 1, new Date(nowMs).toISOString());
    await flushPromises();

    expect(getSessionHeat('session-1')).toBe(1);
    expect(timeoutCallbacks.size).toBe(1);
  });

  it('does not arm the fall transition while hidden', () => {
    const element = createHeatElementMock();
    registerHeatCanvas('session-1', element as unknown as HTMLElement);
    initHeatIndicator();
    setDocumentHidden(true);

    recordBytes('session-1', 24);

    expect(getSessionHeat('session-1')).toBe(1);
    expect(timeoutCallbacks.size).toBe(0);
  });

  it('starts the smooth fall once output goes quiet', async () => {
    const element = createHeatElementMock();
    registerHeatCanvas('session-1', element as unknown as HTMLElement);

    recordBytes('session-1', 24);
    expect(timeoutCallbacks.size).toBe(1);

    await advanceTimeout(220);

    expect(timeoutCallbacks.size).toBe(0);
    expect(element.style.setProperty).toHaveBeenCalledWith(
      '--session-heat-transition-easing',
      'cubic-bezier(0.16, 1, 0.3, 1)',
    );
  });
});
