import { beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
  activeSessionId: 'session-1' as string | null,
  activeTab: 'terminal',
  redrawSession: vi.fn<(_sessionId: string) => Promise<void>>(),
  refreshTerminalPresentation: vi.fn(),
  sessionTerminals: new Map<string, unknown>(),
}));

vi.mock('../../api/client', () => ({
  redrawSession: mocks.redrawSession,
}));

vi.mock('../../state', () => ({
  sessionTerminals: mocks.sessionTerminals,
}));

vi.mock('../../stores', () => ({
  $activeSessionId: {
    get: () => mocks.activeSessionId,
  },
}));

vi.mock('../sessionTabs', () => ({
  getActiveTab: () => mocks.activeTab,
}));

vi.mock('./scaling', () => ({
  refreshTerminalPresentation: mocks.refreshTerminalPresentation,
}));

import { repairTerminalDisplay } from './displayRepair';

describe('repairTerminalDisplay', () => {
  let scheduledFrame: FrameRequestCallback | null;

  beforeEach(() => {
    mocks.activeSessionId = 'session-1';
    mocks.activeTab = 'terminal';
    mocks.redrawSession.mockReset().mockResolvedValue(undefined);
    mocks.refreshTerminalPresentation.mockReset();
    mocks.sessionTerminals.clear();
    scheduledFrame = null;
    vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => {
      scheduledFrame = callback;
      return 1;
    });
  });

  it('requests a shell redraw before refreshing only the existing browser renderer', async () => {
    const focus = vi.fn();
    const state = { terminal: { focus } };
    mocks.sessionTerminals.set('session-1', state);

    await repairTerminalDisplay('session-1');

    expect(mocks.redrawSession).toHaveBeenCalledWith('session-1');
    expect(mocks.refreshTerminalPresentation).toHaveBeenCalledWith('session-1', state);
    expect(mocks.redrawSession.mock.invocationCallOrder[0]).toBeLessThan(
      mocks.refreshTerminalPresentation.mock.invocationCallOrder[0] ?? Number.MAX_SAFE_INTEGER,
    );
    expect(focus).not.toHaveBeenCalled();

    scheduledFrame?.(0);
    expect(focus).toHaveBeenCalledOnce();
  });

  it('does not steal focus when the user leaves the terminal before the refresh frame', async () => {
    const focus = vi.fn();
    mocks.sessionTerminals.set('session-1', { terminal: { focus } });

    await repairTerminalDisplay('session-1');
    mocks.activeSessionId = 'session-2';
    scheduledFrame?.(0);

    expect(focus).not.toHaveBeenCalled();
  });
});
