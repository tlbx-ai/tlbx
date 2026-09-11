import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
  connectStateWebSocket: vi.fn(),
  probeStateWebSocket: vi.fn(),
  probeMuxWebSocket: vi.fn(),
  reportBrowserActivity: vi.fn(),
  recoverVisibleTerminalsAfterBrowserResume: vi.fn(),
  suspendMuxForBrowserBackground: vi.fn(),
  stateWsConnected: true,
  connectionStatus: 'connected',
}));

vi.mock('../../stores', () => ({
  $connectionStatus: { get: () => mocks.connectionStatus },
  $activeSessionId: { get: () => 'sess1234' },
  $stateWsConnected: { get: () => mocks.stateWsConnected },
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
    mocks.connectionStatus = 'connected';
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
      reconnectSettingsAfterLongResume: vi.fn(),
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

  it('suspends terminal transport while a desktop document is hidden', () => {
    const options = setup();
    setVisibility('hidden');

    emitDocument('visibilitychange');

    expect(mocks.reportBrowserActivity).toHaveBeenCalledTimes(1);
    expect(mocks.suspendMuxForBrowserBackground).toHaveBeenCalledTimes(1);
    expect(options.suspendAdditionalTerminalTransport).toHaveBeenCalledTimes(1);
    expect(options.suspendAppServerControlForBackground).toHaveBeenCalledTimes(1);
    expect(options.suspendAncillaryTransportForBackground).toHaveBeenCalledTimes(1);
    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).not.toHaveBeenCalled();
    expect(options.syncMuxTerminalVisibility).not.toHaveBeenCalled();
  });

  it('restores visible terminal state without replacing healthy transports on ordinary focus', () => {
    const options = setup();
    setVisibility('visible');

    emitDocument('visibilitychange');
    vi.advanceTimersByTime(0);

    expect(mocks.connectStateWebSocket).not.toHaveBeenCalled();
    expect(options.reconnectSettingsAfterLongResume).not.toHaveBeenCalled();
    expect(options.recoverAppServerControlAfterResume).not.toHaveBeenCalled();
    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledWith(
      'sess1234',
      ['sess1234'],
      { forceReconnect: false },
    );
    expect(options.syncMuxTerminalVisibility).toHaveBeenCalledTimes(1);
    expect(options.focusActiveTerminal).toHaveBeenCalledTimes(1);
    expect(options.applyScrollbackProtection).toHaveBeenCalledTimes(1);
    expect(options.recoverTerminalPresentationAfterResume).toHaveBeenCalledTimes(1);
    expect(options.recoverAdditionalTerminalTransport).toHaveBeenCalledTimes(1);
  });

  it('replaces core transports once after an explicit browser freeze', () => {
    const options = setup();
    setVisibility('hidden');
    emitDocument('visibilitychange');
    emitDocument('freeze');
    vi.advanceTimersByTime(6000);

    setVisibility('visible');
    emitDocument('visibilitychange');
    emitWindow('focus');
    emitWindow('pageshow');
    emitDocument('resume');
    vi.advanceTimersByTime(0);

    expect(mocks.connectStateWebSocket).toHaveBeenCalledTimes(1);
    expect(options.reconnectSettingsAfterLongResume).toHaveBeenCalledTimes(1);
    expect(options.recoverAppServerControlAfterResume).toHaveBeenCalledTimes(1);
    expect(options.recoverAncillaryTransportAfterResume).toHaveBeenCalledTimes(1);
    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledTimes(1);
    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledWith(
      'sess1234',
      ['sess1234'],
      { forceReconnect: true },
    );
    expect(options.syncMuxTerminalVisibility).toHaveBeenCalledTimes(1);
    expect(options.focusActiveTerminal).toHaveBeenCalledTimes(1);
    expect(options.applyScrollbackProtection).toHaveBeenCalledTimes(1);
  });

  it('reuses healthy status and settings transports after a short background interval', () => {
    const options = setup();
    setVisibility('hidden');
    emitDocument('visibilitychange');
    vi.advanceTimersByTime(250);

    setVisibility('visible');
    emitDocument('visibilitychange');
    emitWindow('focus');
    vi.advanceTimersByTime(0);

    expect(mocks.connectStateWebSocket).not.toHaveBeenCalled();
    expect(options.reconnectSettingsAfterLongResume).not.toHaveBeenCalled();
    expect(options.recoverAppServerControlAfterResume).toHaveBeenCalledTimes(1);
    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledWith(
      'sess1234',
      ['sess1234'],
      { forceReconnect: false },
    );
  });

  it('applies backpressure immediately when initialized in an already hidden document', () => {
    setVisibility('hidden');
    const options = setup();

    expect(mocks.reportBrowserActivity).toHaveBeenCalledWith(false);
    expect(mocks.suspendMuxForBrowserBackground).toHaveBeenCalledTimes(1);
    expect(options.suspendAdditionalTerminalTransport).toHaveBeenCalledTimes(1);

    emitWindow('pagehide');
    emitDocument('freeze');
    vi.advanceTimersByTime(1000);
    expect(mocks.suspendMuxForBrowserBackground).toHaveBeenCalledTimes(2);

    setVisibility('visible');
    emitDocument('resume');
    vi.advanceTimersByTime(0);

    expect(mocks.connectStateWebSocket).toHaveBeenCalledTimes(1);
    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledWith(
      'sess1234',
      ['sess1234'],
      { forceReconnect: true },
    );
    expect(options.recoverTerminalPresentationAfterResume).toHaveBeenCalledTimes(1);
  });

  it('suspends on pagehide when visibilitychange is missing', () => {
    const options = setup();

    emitWindow('pagehide');

    expect(mocks.reportBrowserActivity).toHaveBeenCalledWith(false);
    expect(mocks.suspendMuxForBrowserBackground).toHaveBeenCalledTimes(1);

    emitWindow('pageshow');
    vi.advanceTimersByTime(0);

    expect(mocks.connectStateWebSocket).toHaveBeenCalledTimes(1);
    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledWith(
      'sess1234',
      ['sess1234'],
      { forceReconnect: true },
    );
    expect(options.recoverTerminalPresentationAfterResume).toHaveBeenCalledTimes(1);
  });

  it('coalesces duplicate foreground events without hiding the document first', () => {
    const options = setup();

    emitWindow('focus');
    emitWindow('pageshow');
    emitDocument('resume');
    vi.advanceTimersByTime(0);

    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledTimes(1);
    expect(options.focusActiveTerminal).toHaveBeenCalledTimes(1);
  });

  it('never coalesces away a new background resume within the foreground debounce window', () => {
    const options = setup();
    emitWindow('focus');
    vi.advanceTimersByTime(0);

    for (let cycle = 0; cycle < 3; cycle += 1) {
      vi.advanceTimersByTime(10);
      setVisibility('hidden');
      emitDocument('visibilitychange');
      vi.advanceTimersByTime(10);
      setVisibility('visible');
      emitDocument('visibilitychange');
      emitWindow('focus');
      vi.advanceTimersByTime(0);
    }

    expect(mocks.suspendMuxForBrowserBackground).toHaveBeenCalledTimes(3);
    expect(mocks.connectStateWebSocket).not.toHaveBeenCalled();
    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledTimes(4);
    expect(options.recoverTerminalPresentationAfterResume).toHaveBeenCalledTimes(4);
  });

  it('recovers a pending suspension on the foreground heartbeat when resume events are missing', () => {
    setup();
    setVisibility('hidden');
    emitDocument('visibilitychange');
    vi.advanceTimersByTime(10);
    setVisibility('visible');
    vi.advanceTimersByTime(1100);

    expect(mocks.connectStateWebSocket).not.toHaveBeenCalled();
    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledWith(
      'sess1234',
      ['sess1234'],
      { forceReconnect: false },
    );
  });

  it('does not classify a visible window blur as a browser suspension', () => {
    const options = setup();

    emitWindow('blur');
    vi.advanceTimersByTime(6000);
    emitWindow('focus');
    vi.advanceTimersByTime(0);

    expect(mocks.connectStateWebSocket).not.toHaveBeenCalled();
    expect(options.reconnectSettingsAfterLongResume).not.toHaveBeenCalled();
    expect(options.recoverAppServerControlAfterResume).not.toHaveBeenCalled();
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
    vi.advanceTimersByTime(60000);
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
    vi.advanceTimersByTime(60000);
    setVisibility('visible');
    emitDocument('visibilitychange');
    await vi.advanceTimersByTimeAsync(0);
    expect(mocks.connectStateWebSocket).not.toHaveBeenCalled();
  });

  it('suspends and recovers a real freeze even when stay-active is enabled', () => {
    const options = setup(false, true);
    setVisibility('hidden');
    emitDocument('visibilitychange');
    emitDocument('freeze');
    expect(mocks.suspendMuxForBrowserBackground).toHaveBeenCalledTimes(1);
    expect(options.suspendAppServerControlForBackground).toHaveBeenCalledTimes(1);
    setVisibility('visible');
    emitDocument('resume');
    vi.advanceTimersByTime(0);
    expect(mocks.connectStateWebSocket).toHaveBeenCalledTimes(1);
    expect(options.recoverAdditionalTerminalTransport).toHaveBeenCalledWith(true);
  });

  it('applies setting changes while already hidden and disposes the subscription', () => {
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
    expect(mocks.connectStateWebSocket).toHaveBeenCalledTimes(1);
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

  it('removes lifecycle listeners and timers when disposed', () => {
    const options = setup();
    options.dispose();

    setVisibility('hidden');
    emitDocument('visibilitychange');
    emitWindow('focus');
    vi.advanceTimersByTime(10000);

    expect(mocks.suspendMuxForBrowserBackground).not.toHaveBeenCalled();
    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);
  });

  it('keeps hidden terminal output active for mobile PiP', () => {
    const options = setup(true);
    setVisibility('hidden');

    emitDocument('visibilitychange');

    expect(mocks.suspendMuxForBrowserBackground).not.toHaveBeenCalled();
    expect(options.suspendAppServerControlForBackground).toHaveBeenCalledTimes(1);
  });

  it('keeps output active only while a real mobile PiP window exists', () => {
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

  it('reconnects state immediately when its store already reports a disconnect', () => {
    mocks.stateWsConnected = false;
    setup();

    emitWindow('focus');
    vi.advanceTimersByTime(0);

    expect(mocks.connectStateWebSocket).toHaveBeenCalledTimes(1);
  });

  it('replaces transports when the event loop resumes without lifecycle events', () => {
    expect(hasSuspendedForegroundEventLoop(1000, 7000)).toBe(true);
    expect(hasSuspendedForegroundEventLoop(1000, 5999)).toBe(false);
  });

  it('does not recover transports for an on-time foreground heartbeat', () => {
    setup();

    vi.advanceTimersByTime(1000);

    expect(mocks.connectStateWebSocket).not.toHaveBeenCalled();
    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).not.toHaveBeenCalled();
  });
  it('retries a stalled foreground handshake without renderer or focus churn', () => {
    mocks.connectionStatus = 'disconnected';
    const options = setup();
    vi.advanceTimersByTime(15000);
    expect(mocks.connectStateWebSocket).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1000);
    expect(mocks.connectStateWebSocket).toHaveBeenCalledTimes(1);
    expect(mocks.recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledWith(
      'sess1234',
      ['sess1234'],
      { forceReconnect: true },
    );
    expect(options.focusActiveTerminal).not.toHaveBeenCalled();
    expect(options.recoverTerminalPresentationAfterResume).not.toHaveBeenCalled();
    mocks.connectionStatus = 'connected';
    vi.advanceTimersByTime(30000);
    expect(mocks.connectStateWebSocket).toHaveBeenCalledTimes(1);
    options.dispose();
  });

  it('retries immediately when the network returns and removes the online listener', () => {
    const options = setup();
    emitWindow('online');
    vi.advanceTimersByTime(0);
    expect(mocks.connectStateWebSocket).toHaveBeenCalledTimes(1);
    options.dispose();
    emitWindow('online');
    vi.advanceTimersByTime(1000);
    expect(mocks.connectStateWebSocket).toHaveBeenCalledTimes(1);
  });
  it('bounds retries for a partially connected app and stays suspended while hidden', () => {
    mocks.connectionStatus = 'reconnecting';
    const options = setup();
    vi.advanceTimersByTime(31000);
    expect(mocks.connectStateWebSocket).toHaveBeenCalledTimes(2);
    setVisibility('hidden');
    emitDocument('visibilitychange');
    vi.advanceTimersByTime(60000);
    expect(mocks.connectStateWebSocket).toHaveBeenCalledTimes(2);
    options.dispose();
    vi.advanceTimersByTime(30000);
    expect(mocks.connectStateWebSocket).toHaveBeenCalledTimes(2);
  });
});
