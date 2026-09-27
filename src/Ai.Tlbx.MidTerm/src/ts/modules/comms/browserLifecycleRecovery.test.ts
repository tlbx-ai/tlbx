import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
  connectStateWebSocket: vi.fn(),
  probeStateWebSocket: vi.fn(),
  probeMuxWebSocket: vi.fn(),
  reportBrowserActivity: vi.fn(),
  recoverVisibleTerminalsAfterBrowserResume: vi.fn(),
  suspendMuxForBrowserBackground: vi.fn(),
  stateWsConnected: true,
  stateListeners: new Set<() => void>(),
  resuming: false,
}));

vi.mock('../../stores', () => ({
  $browserResuming: {
    get: () => mocks.resuming,
    set: (value: boolean) => {
      mocks.resuming = value;
    },
  },
  $activeSessionId: { get: () => 'sess1234' },
  $stateWsConnected: {
    get: () => mocks.stateWsConnected,
    listen: (listener: () => void) => {
      mocks.stateListeners.add(listener);
      return () => mocks.stateListeners.delete(listener);
    },
  },
  $muxWsConnected: { get: () => true, listen: () => () => {} },
}));

vi.mock('./stateChannel', () => ({
  connectStateWebSocket: mocks.connectStateWebSocket,
  probeStateWebSocket: mocks.probeStateWebSocket,
  reportBrowserActivity: mocks.reportBrowserActivity,
}));

vi.mock('./muxChannel', () => ({
  recoverVisibleTerminalsAfterBrowserResume: mocks.recoverVisibleTerminalsAfterBrowserResume,
  suspendMuxForBrowserBackground: mocks.suspendMuxForBrowserBackground,
  probeMuxWebSocket: mocks.probeMuxWebSocket,
}));

import {
  hasSuspendedForegroundEventLoop,
  setupBrowserLifecycleRecovery,
} from './browserLifecycleRecovery';

describe('browserLifecycleRecovery', () => {
  let fakeDocument: EventTarget & { visibilityState: DocumentVisibilityState };
  let fakeWindow: EventTarget;

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-08-10T00:00:00Z'));
    vi.clearAllMocks();
    mocks.probeStateWebSocket.mockResolvedValue(true);
    mocks.probeMuxWebSocket.mockResolvedValue(true);
    mocks.stateWsConnected = true;
    mocks.resuming = false;
    mocks.stateListeners.clear();
    fakeDocument = Object.assign(new EventTarget(), {
      visibilityState: 'visible' as DocumentVisibilityState,
    });
    fakeWindow = new EventTarget();
    vi.stubGlobal('document', fakeDocument);
    vi.stubGlobal('window', fakeWindow);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  function setVisibility(value: DocumentVisibilityState): void {
    Object.defineProperty(fakeDocument, 'visibilityState', {
      value,
      configurable: true,
    });
  }

  function setup(
    keepTerminalOutputActiveWhileHidden: boolean | (() => boolean) = false,
    stayActiveInBackground = false,
  ) {
    const options = {
      stayActiveInBackground: vi.fn(() => stayActiveInBackground),
      subscribeBackgroundActivity: vi.fn((_listener: () => void) => vi.fn()),
      getVisibleTerminalSessionIds: vi.fn(() => ['sess1234']),
      syncMuxTerminalVisibility: vi.fn(),
      focusActiveTerminal: vi.fn(),
      applyScrollbackProtection: vi.fn(),
      recoverTerminalPresentationAfterResume: vi.fn(),
      keepTerminalOutputActiveWhileHidden: vi.fn(() =>
        typeof keepTerminalOutputActiveWhileHidden === 'function'
          ? keepTerminalOutputActiveWhileHidden()
          : keepTerminalOutputActiveWhileHidden,
      ),
      suspendAdditionalTerminalTransport: vi.fn(),
      recoverAdditionalTerminalTransport: vi.fn(),
      suspendAppServerControlForBackground: vi.fn(),
      suspendAncillaryTransportForBackground: vi.fn(),
      recoverAncillaryTransportAfterResume: vi.fn(),
      recoverSettingsAfterResume: vi.fn(),
      recoverAppServerControlAfterResume: vi.fn(),
    };
    const dispose = setupBrowserLifecycleRecovery(options);
    return { ...options, dispose };
  }

  function emitDocument(type: string): void {
    fakeDocument.dispatchEvent(new Event(type));
  }

  function emitWindow(type: string): void {
    fakeWindow.dispatchEvent(new Event(type));
  }

  it('keeps resume pending until fresh state and the presentation paint complete', async () => {
    const options = setup(false, true);
    let painted!: () => void;
    options.recoverTerminalPresentationAfterResume.mockImplementation(
      () =>
        new Promise<void>((resolve) => {
          painted = resolve;
        }),
    );
    setVisibility('hidden');
    emitDocument('visibilitychange');
    mocks.stateWsConnected = false;
    setVisibility('visible');
    emitDocument('visibilitychange');
    await vi.advanceTimersByTimeAsync(0);
    expect(mocks.resuming).toBe(true);
    expect(options.recoverTerminalPresentationAfterResume).not.toHaveBeenCalled();
    mocks.stateWsConnected = true;
    mocks.stateListeners.forEach((listener) => listener());
    expect(mocks.resuming).toBe(true);
    painted();
    await Promise.resolve();
    expect(mocks.resuming).toBe(false);
    expect(options.focusActiveTerminal).not.toHaveBeenCalled();
    options.dispose();
  });

  it('restores visible terminal state without replacing healthy transports on ordinary focus', async () => {
    const options = setup();
    setVisibility('visible');

    emitDocument('visibilitychange');
    await vi.advanceTimersByTimeAsync(0);

    expect(mocks.connectStateWebSocket).not.toHaveBeenCalled();
    expect(options.recoverSettingsAfterResume).not.toHaveBeenCalled();
    expect(options.recoverAppServerControlAfterResume).not.toHaveBeenCalled();
    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledWith(
      'sess1234',
      ['sess1234'],
      { forceReconnect: false },
    );
    expect(options.syncMuxTerminalVisibility).toHaveBeenCalledTimes(1);
    expect(options.focusActiveTerminal).not.toHaveBeenCalled();
    expect(options.applyScrollbackProtection).toHaveBeenCalledTimes(1);
    expect(options.recoverTerminalPresentationAfterResume).toHaveBeenCalledTimes(1);
    expect(options.recoverAdditionalTerminalTransport).toHaveBeenCalledTimes(1);
  });

  it('replaces core transports once after an explicit browser freeze', async () => {
    const options = setup();
    setVisibility('hidden');
    emitDocument('visibilitychange');
    emitDocument('freeze');
    await vi.advanceTimersByTimeAsync(6000);

    setVisibility('visible');
    emitDocument('visibilitychange');
    emitWindow('focus');
    emitWindow('pageshow');
    emitDocument('resume');
    await vi.advanceTimersByTimeAsync(0);

    expect(mocks.connectStateWebSocket).toHaveBeenCalledTimes(1);
    expect(options.recoverSettingsAfterResume).toHaveBeenCalledTimes(1);
    expect(options.recoverAppServerControlAfterResume).toHaveBeenCalledTimes(1);
    expect(options.recoverAncillaryTransportAfterResume).toHaveBeenCalledTimes(1);
    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledTimes(1);
    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledWith(
      'sess1234',
      ['sess1234'],
      { forceReconnect: true },
    );
    expect(options.syncMuxTerminalVisibility).toHaveBeenCalledTimes(1);
    expect(options.focusActiveTerminal).not.toHaveBeenCalled();
    expect(options.applyScrollbackProtection).toHaveBeenCalledTimes(1);
  });

  it('applies backpressure immediately when initialized in an already hidden document', async () => {
    setVisibility('hidden');
    const options = setup();

    expect(mocks.reportBrowserActivity).toHaveBeenCalledWith(false);
    expect(mocks.suspendMuxForBrowserBackground).toHaveBeenCalledTimes(1);
    expect(options.suspendAdditionalTerminalTransport).toHaveBeenCalledTimes(1);

    emitWindow('pagehide');
    emitDocument('freeze');
    await vi.advanceTimersByTimeAsync(1000);
    expect(mocks.suspendMuxForBrowserBackground).toHaveBeenCalledTimes(2);

    setVisibility('visible');
    emitDocument('resume');
    await vi.advanceTimersByTimeAsync(0);

    expect(mocks.connectStateWebSocket).toHaveBeenCalledTimes(1);
    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledWith(
      'sess1234',
      ['sess1234'],
      { forceReconnect: true },
    );
    expect(options.recoverTerminalPresentationAfterResume).toHaveBeenCalledTimes(1);
  });

  it('suspends on pagehide when visibilitychange is missing', async () => {
    const options = setup();

    emitWindow('pagehide');

    expect(mocks.reportBrowserActivity).toHaveBeenCalledWith(false);
    expect(mocks.suspendMuxForBrowserBackground).toHaveBeenCalledTimes(1);

    emitWindow('pageshow');
    await vi.advanceTimersByTimeAsync(0);

    expect(mocks.connectStateWebSocket).toHaveBeenCalledTimes(1);
    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledWith(
      'sess1234',
      ['sess1234'],
      { forceReconnect: true },
    );
    expect(options.recoverTerminalPresentationAfterResume).toHaveBeenCalledTimes(1);
  });

  it('coalesces duplicate foreground events without hiding the document first', async () => {
    const options = setup();

    emitWindow('focus');
    emitWindow('pageshow');
    emitDocument('resume');
    await vi.advanceTimersByTimeAsync(0);

    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledTimes(1);
    expect(options.focusActiveTerminal).not.toHaveBeenCalled();
  });

  it('never coalesces away a new background resume within the foreground debounce window', async () => {
    const options = setup();
    emitWindow('focus');
    await vi.advanceTimersByTimeAsync(0);

    for (let cycle = 0; cycle < 3; cycle += 1) {
      await vi.advanceTimersByTimeAsync(10);
      setVisibility('hidden');
      emitDocument('visibilitychange');
      await vi.advanceTimersByTimeAsync(10);
      setVisibility('visible');
      emitDocument('visibilitychange');
      emitWindow('focus');
      await vi.advanceTimersByTimeAsync(0);
    }

    expect(mocks.suspendMuxForBrowserBackground).toHaveBeenCalledTimes(3);
    expect(mocks.connectStateWebSocket).not.toHaveBeenCalled();
    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledTimes(4);
    expect(options.recoverTerminalPresentationAfterResume).toHaveBeenCalledTimes(4);
  });

  it('recovers a pending suspension on the foreground heartbeat when resume events are missing', async () => {
    setup();
    setVisibility('hidden');
    emitDocument('visibilitychange');
    await vi.advanceTimersByTimeAsync(10);
    setVisibility('visible');
    await vi.advanceTimersByTimeAsync(1100);

    expect(mocks.connectStateWebSocket).not.toHaveBeenCalled();
    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledWith(
      'sess1234',
      ['sess1234'],
      { forceReconnect: false },
    );
  });

  it('keeps all transports active for a long hidden interval when enabled', async () => {
    const options = setup(false, true);
    setVisibility('hidden');
    emitDocument('visibilitychange');
    await vi.advanceTimersByTimeAsync(60000);
    expect(mocks.suspendMuxForBrowserBackground).not.toHaveBeenCalled();
    expect(options.suspendAppServerControlForBackground).not.toHaveBeenCalled();
    expect(options.suspendAncillaryTransportForBackground).not.toHaveBeenCalled();
    setVisibility('visible');
    emitDocument('visibilitychange');
    await vi.advanceTimersByTimeAsync(0);
    expect(mocks.connectStateWebSocket).not.toHaveBeenCalled();
    expect(mocks.probeStateWebSocket).toHaveBeenCalledTimes(1);
    expect(mocks.probeMuxWebSocket).toHaveBeenCalledTimes(1);
  });

  it('preserves healthy state connections after long hidden intervals with the setting off', async () => {
    setup();
    setVisibility('hidden');
    emitDocument('visibilitychange');
    await vi.advanceTimersByTimeAsync(60000);
    setVisibility('visible');
    emitDocument('visibilitychange');
    await vi.advanceTimersByTimeAsync(0);
    expect(mocks.connectStateWebSocket).not.toHaveBeenCalled();
  });

  it('suspends and recovers a real freeze even when stay-active is enabled', async () => {
    const options = setup(false, true);
    setVisibility('hidden');
    emitDocument('visibilitychange');
    emitDocument('freeze');
    expect(mocks.suspendMuxForBrowserBackground).toHaveBeenCalledTimes(1);
    expect(options.suspendAppServerControlForBackground).toHaveBeenCalledTimes(1);
    setVisibility('visible');
    emitDocument('resume');
    await vi.advanceTimersByTimeAsync(0);
    expect(mocks.connectStateWebSocket).toHaveBeenCalledTimes(1);
    expect(options.recoverAdditionalTerminalTransport).toHaveBeenCalledWith(true);
  });

  it('applies setting changes while already hidden and disposes the subscription', async () => {
    const options = setup();
    setVisibility('hidden');
    emitDocument('visibilitychange');
    options.stayActiveInBackground.mockReturnValue(true);
    const changed = options.subscribeBackgroundActivity.mock.calls[0]![0];
    changed();
    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledWith(
      'sess1234',
      ['sess1234'],
      { forceReconnect: false },
    );
    expect(options.recoverAncillaryTransportAfterResume).toHaveBeenCalledTimes(1);
    options.stayActiveInBackground.mockReturnValue(false);
    changed();
    expect(mocks.suspendMuxForBrowserBackground).toHaveBeenCalledTimes(2);
    options.dispose();
    expect(options.subscribeBackgroundActivity.mock.results[0]!.value).toHaveBeenCalledTimes(1);
  });

  it('replaces stale-open sockets only after a failed foreground probe', async () => {
    mocks.probeMuxWebSocket.mockResolvedValue(false);
    setup(false, true);
    setVisibility('hidden');
    emitDocument('visibilitychange');
    setVisibility('visible');
    emitDocument('visibilitychange');
    await vi.advanceTimersByTimeAsync(10);
    expect(mocks.connectStateWebSocket).not.toHaveBeenCalled();
    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).toHaveBeenLastCalledWith(
      'sess1234',
      ['sess1234'],
      { forceReconnect: true },
    );
  });

  it('ignores failed probes after disposal or a newer background cycle', async () => {
    let finish!: (healthy: boolean) => void;
    mocks.probeMuxWebSocket.mockImplementation(
      () =>
        new Promise<boolean>((resolve) => {
          finish = resolve;
        }),
    );
    const options = setup(false, true);
    setVisibility('hidden');
    emitDocument('visibilitychange');
    setVisibility('visible');
    emitDocument('visibilitychange');
    await vi.advanceTimersByTimeAsync(0);
    options.dispose();
    finish(false);
    await vi.advanceTimersByTimeAsync(10);
    expect(mocks.connectStateWebSocket).not.toHaveBeenCalled();
  });

  it('removes lifecycle listeners and timers when disposed', async () => {
    const options = setup();
    options.dispose();

    setVisibility('hidden');
    emitDocument('visibilitychange');
    emitWindow('focus');
    await vi.advanceTimersByTimeAsync(10000);

    expect(mocks.suspendMuxForBrowserBackground).not.toHaveBeenCalled();
    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);
  });

  it('keeps output active only while a real mobile PiP window exists', async () => {
    let pipActive = false;
    const options = setup(() => pipActive);
    setVisibility('hidden');
    emitDocument('visibilitychange');

    expect(mocks.suspendMuxForBrowserBackground).toHaveBeenCalledTimes(1);

    pipActive = true;
    emitWindow('tlbx:mobile-pip-active-changed');

    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledWith(
      'sess1234',
      ['sess1234'],
      { forceReconnect: false },
    );

    pipActive = false;
    emitWindow('tlbx:mobile-pip-active-changed');
    expect(mocks.suspendMuxForBrowserBackground).toHaveBeenCalledTimes(2);
    expect(options.recoverAdditionalTerminalTransport).toHaveBeenCalledTimes(1);
    expect(options.suspendAdditionalTerminalTransport).toHaveBeenCalledTimes(2);
  });

  it('reconnects state immediately when its store already reports a disconnect', async () => {
    mocks.stateWsConnected = false;
    setup();

    emitWindow('focus');
    await vi.advanceTimersByTimeAsync(0);

    expect(mocks.connectStateWebSocket).toHaveBeenCalledTimes(1);
  });

  it('replaces transports when the event loop resumes without lifecycle events', async () => {
    expect(hasSuspendedForegroundEventLoop(1000, 7000)).toBe(true);
    expect(hasSuspendedForegroundEventLoop(1000, 5999)).toBe(false);
  });

  it('retries immediately when the network returns and removes the online listener', async () => {
    const options = setup();
    emitWindow('online');
    await vi.advanceTimersByTimeAsync(0);
    expect(mocks.connectStateWebSocket).toHaveBeenCalledTimes(1);
    options.dispose();
    emitWindow('online');
    await vi.advanceTimersByTimeAsync(1000);
    expect(mocks.connectStateWebSocket).toHaveBeenCalledTimes(1);
  });
});
