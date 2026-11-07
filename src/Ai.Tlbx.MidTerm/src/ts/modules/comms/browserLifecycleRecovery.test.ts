import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { setupBrowserLifecycleRecovery } from './browserLifecycleRecovery';
import {
  $activeSessionId,
  $browserResuming,
  $muxWsConnected,
  $stateWsConnected,
} from '../../stores';
import { connectStateWebSocket, probeStateWebSocket } from './stateChannel';
import {
  probeMuxWebSocket,
  recoverVisibleTerminalsAfterBrowserResume,
  suspendMuxForBrowserBackground,
} from './muxChannel';
vi.mock('../../stores', async () => {
  const { atom } = await import('nanostores');
  return {
    $activeSessionId: atom<string | null>('sess1234'),
    $browserResuming: atom(false),
    $stateWsConnected: atom(true),
    $muxWsConnected: atom(true),
  };
});
vi.mock('./stateChannel', () => ({
  connectStateWebSocket: vi.fn(),
  reportBrowserActivity: vi.fn(),
  probeStateWebSocket: vi.fn(),
}));
vi.mock('./muxChannel', () => ({
  probeMuxWebSocket: vi.fn(),
  recoverVisibleTerminalsAfterBrowserResume: vi.fn(),
  suspendMuxForBrowserBackground: vi.fn(),
}));
describe('browser transport lifecycle protocol', () => {
  let page: EventTarget & { visibilityState: string };
  let viewport: EventTarget;
  let dispose: (() => void) | undefined;
  let present: ReturnType<typeof vi.fn>;
  let settings: ReturnType<typeof vi.fn>;
  const start = (prepareTerminalCapture?: () => Promise<void>) =>
    setupBrowserLifecycleRecovery({
      getVisibleTerminalSessionIds: () => ['sess1234'],
      syncMuxTerminalVisibility: vi.fn(),
      applyScrollbackProtection: vi.fn(),
      recoverTerminalPresentationAfterResume: present,
      keepTerminalOutputActiveWhileHidden: () => false,
      prepareTerminalCapture,
      recoverSettingsAfterResume: settings,
    });
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(0);
    page = Object.assign(new EventTarget(), { visibilityState: 'visible' });
    viewport = new EventTarget();
    vi.stubGlobal('document', page);
    vi.stubGlobal('window', viewport);
    vi.clearAllMocks();
    $activeSessionId.set('sess1234');
    $browserResuming.set(false);
    $stateWsConnected.set(true);
    $muxWsConnected.set(true);
    present = vi.fn().mockResolvedValue(undefined);
    settings = vi.fn();
    vi.mocked(probeStateWebSocket).mockResolvedValue(true);
    vi.mocked(probeMuxWebSocket).mockResolvedValue(true);
    vi.mocked(connectStateWebSocket).mockImplementation(() => $stateWsConnected.set(false));
    vi.mocked(recoverVisibleTerminalsAfterBrowserResume).mockImplementation(
      (_active, _visible, options) => {
        if (options?.forceReconnect) $muxWsConnected.set(false);
      },
    );
  });
  afterEach(() => {
    dispose?.();
    dispose = undefined;
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });
  it('borrows hidden terminal transport only through capture and restores suspension without focus or activity', async () => {
    page.visibilityState = 'hidden';
    const prepare = vi.fn().mockResolvedValue(undefined);
    const focused = { isConnected: true, focus: vi.fn() };
    Object.assign(page, { activeElement: focused });
    dispose = start(prepare);
    const hook = (viewport as unknown as { __tlbxBeforeCapture: () => Promise<() => void> })
      .__tlbxBeforeCapture;
    expect(suspendMuxForBrowserBackground).toHaveBeenCalledOnce();
    const release = await hook();
    expect(prepare).toHaveBeenCalledOnce();
    expect(recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledOnce();
    await vi.advanceTimersByTimeAsync(2000);
    expect(suspendMuxForBrowserBackground).toHaveBeenCalledOnce();
    expect(page.visibilityState).toBe('hidden');
    expect(present).not.toHaveBeenCalled();
    Object.assign(page, { activeElement: null });
    release();
    release();
    expect(focused.focus).toHaveBeenCalledExactlyOnceWith({ preventScroll: true });
    expect(suspendMuxForBrowserBackground).toHaveBeenCalledTimes(2);
  });
  it('releases failed and abandoned capture activity', async () => {
    page.visibilityState = 'hidden';
    dispose = start(vi.fn().mockRejectedValue(new Error('parser timeout')));
    const hook = () =>
      (
        viewport as unknown as { __tlbxBeforeCapture: () => Promise<() => void> }
      ).__tlbxBeforeCapture();
    await expect(hook()).rejects.toThrow('parser timeout');
    expect(suspendMuxForBrowserBackground).toHaveBeenCalledTimes(2);
    dispose();
    dispose = start(vi.fn().mockResolvedValue(undefined));
    await hook();
    const count = vi.mocked(suspendMuxForBrowserBackground).mock.calls.length;
    await vi.advanceTimersByTimeAsync(30_000);
    expect(suspendMuxForBrowserBackground).toHaveBeenCalledTimes(count + 1);
  });
  it('periodically verifies healthy visible transports without replacing them or covering the terminal', async () => {
    dispose = start();
    await vi.advanceTimersByTimeAsync(10_000);
    expect(probeStateWebSocket).toHaveBeenCalledTimes(2);
    expect(probeMuxWebSocket).toHaveBeenCalledTimes(2);
    expect(connectStateWebSocket).not.toHaveBeenCalled();
    expect(recoverVisibleTerminalsAfterBrowserResume).not.toHaveBeenCalled();
    expect($browserResuming.get()).toBe(false);
  });
  it('repairs a silent stale-open channel and waits for fresh state and presentation', async () => {
    vi.mocked(probeStateWebSocket).mockResolvedValue(false);
    dispose = start();
    await vi.advanceTimersByTimeAsync(5_000);
    expect(connectStateWebSocket).toHaveBeenCalledOnce();
    expect(recoverVisibleTerminalsAfterBrowserResume).not.toHaveBeenCalled();
    expect($browserResuming.get()).toBe(true);
    $stateWsConnected.set(true);
    await vi.advanceTimersByTimeAsync(0);
    expect(present).toHaveBeenCalledOnce();
    expect($browserResuming.get()).toBe(false);
  });
  it('coalesces restored Link and foreground events into one immediate socket replacement', async () => {
    dispose = start();
    viewport.dispatchEvent(new Event('tlbx-link-restored'));
    viewport.dispatchEvent(new Event('focus'));
    viewport.dispatchEvent(new Event('tlbx-link-restored'));
    await vi.advanceTimersByTimeAsync(0);
    expect(connectStateWebSocket).toHaveBeenCalledOnce();
    expect(recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledExactlyOnceWith(
      'sess1234',
      ['sess1234'],
      { forceReconnect: true },
    );
    expect(settings).toHaveBeenCalledExactlyOnceWith(true);
    expect($browserResuming.get()).toBe(true);
    $stateWsConnected.set(true);
    await vi.advanceTimersByTimeAsync(0);
    expect($browserResuming.get()).toBe(true);
    $muxWsConnected.set(true);
    await vi.advanceTimersByTimeAsync(0);
    expect(present).toHaveBeenCalledOnce();
    expect($browserResuming.get()).toBe(false);
  });
  it('ignores a health result from an old Link carrier after its replacement', async () => {
    let finishOldProbe!: (healthy: boolean) => void;
    vi.mocked(probeStateWebSocket).mockImplementation(
      () =>
        new Promise((resolve) => {
          finishOldProbe = resolve;
        }),
    );
    dispose = start();
    await vi.advanceTimersByTimeAsync(5_000);
    viewport.dispatchEvent(new Event('tlbx-link-restored'));
    await vi.advanceTimersByTimeAsync(0);
    $stateWsConnected.set(true);
    $muxWsConnected.set(true);
    await vi.advanceTimersByTimeAsync(0);
    finishOldProbe(false);
    await vi.advanceTimersByTimeAsync(0);
    expect(connectStateWebSocket).toHaveBeenCalledOnce();
    expect($stateWsConnected.get()).toBe(true);
    expect($muxWsConnected.get()).toBe(true);
    expect($browserResuming.get()).toBe(false);
  });
  it('immediately reconnects after a real freeze and visibility return', async () => {
    dispose = start();
    page.visibilityState = 'hidden';
    page.dispatchEvent(new Event('freeze'));
    await vi.advanceTimersByTimeAsync(60_000);
    expect(connectStateWebSocket).not.toHaveBeenCalled();
    page.visibilityState = 'visible';
    page.dispatchEvent(new Event('visibilitychange'));
    viewport.dispatchEvent(new Event('focus'));
    await vi.advanceTimersByTimeAsync(0);
    expect(connectStateWebSocket).toHaveBeenCalledOnce();
    expect(recoverVisibleTerminalsAfterBrowserResume).toHaveBeenCalledExactlyOnceWith(
      'sess1234',
      ['sess1234'],
      { forceReconnect: true },
    );
  });
  it('retries failed presentation instead of permanently leaving the recovery cover', async () => {
    present.mockRejectedValueOnce(new Error('renderer warming up'));
    dispose = start();
    viewport.dispatchEvent(new Event('tlbx-link-restored'));
    await vi.advanceTimersByTimeAsync(0);
    $stateWsConnected.set(true);
    $muxWsConnected.set(true);
    await vi.advanceTimersByTimeAsync(0);
    expect($browserResuming.get()).toBe(true);
    await vi.advanceTimersByTimeAsync(1_000);
    expect(present).toHaveBeenCalledTimes(2);
    expect($browserResuming.get()).toBe(false);
  });
});
