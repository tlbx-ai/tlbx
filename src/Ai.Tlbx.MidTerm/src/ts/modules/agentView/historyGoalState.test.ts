import { afterEach, describe, expect, it, vi } from 'vitest';
import type {
  AppServerControlHistorySnapshot,
  AppServerControlHistoryDelta,
} from '../../api/types';
import type { SessionAppServerControlViewState } from './types';

vi.mock('../../api/client', () => ({
  setAppServerControlGoal: vi.fn(),
  updateAppServerControlHistoryStreamWindow: vi.fn(),
}));
vi.mock('./historyProcessing', () => ({ buildAppServerControlHistoryEntries: () => [] }));
vi.mock('../appServerControl/quickSettings', () => ({
  syncAppServerControlQuickSettingsFromHistory: vi.fn(),
}));
vi.mock('../navigation/backButtonGuard', () => ({ registerBackButtonLayer: vi.fn() }));
vi.mock('../i18n', () => ({ t: (key: string) => key }));

import { setAppServerControlGoal } from '../../api/client';
import { controlAppServerControlGoal, goalControlTitle } from '../appServerControl/goals';
import {
  applyFetchedAppServerControlHistoryWindow,
  applyLiveAppServerControlHistoryDelta,
} from './historyWindowState';

const id = 'goal-window-sync';
function snapshot(): AppServerControlHistorySnapshot {
  return {
    sessionId: id,
    provider: 'claude',
    generatedAt: '',
    latestSequence: 5,
    historyCount: 0,
    historyWindowStart: 0,
    historyWindowEnd: 0,
    hasOlderHistory: false,
    hasNewerHistory: false,
    session: { state: 'running', stateLabel: '' },
    thread: {
      threadId: 'own',
      state: 'active',
      stateLabel: '',
      goal: {
        threadId: 'own',
        confirmation: 'confirmed',
        scope: 'thread',
        goal: { objective: 'Fetched', status: 'active' },
      },
    },
    currentTurn: { turnId: 'old-turn', state: 'running', stateLabel: '' },
    quickSettings: {
      planMode: 'off',
      permissionMode: 'auto',
      acknowledgedFields: ['permissionMode'],
    },
    streams: {
      assistantText: '',
      reasoningText: '',
      reasoningSummaryText: '',
      planText: '',
      commandOutput: '',
      fileChangeOutput: '',
      unifiedDiff: '',
    },
    history: [],
    items: [],
    requests: [],
    notices: [],
  };
}

describe('Goal canonical window/patch synchronization', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
  });
  it('uses the newest canonical turn guard without waiting for a render', async () => {
    vi.stubGlobal('window', new EventTarget());
    vi.stubGlobal(
      'CustomEvent',
      class extends Event {
        readonly detail: unknown;
        constructor(type: string, init: CustomEventInit) {
          super(type);
          this.detail = init.detail;
        }
      },
    );
    const fetched = snapshot();
    const state = {
      snapshot: null,
      streamConnected: true,
      historyWindowTargetCount: 80,
    } as unknown as SessionAppServerControlViewState;
    expect(applyFetchedAppServerControlHistoryWindow(id, state, fetched)).toBe(true);
    expect(goalControlTitle(id)).toContain('Fetched');
    const delta: AppServerControlHistoryDelta = {
      sessionId: id,
      provider: 'claude',
      generatedAt: '',
      latestSequence: 6,
      historyCount: 0,
      session: fetched.session,
      thread: {
        ...fetched.thread,
        goal: { ...fetched.thread.goal!, goal: { objective: 'Patched', status: 'active' } },
      },
      currentTurn: { turnId: 'new-turn', state: 'running', stateLabel: '' },
      quickSettings: fetched.quickSettings,
      streams: fetched.streams,
      historyUpserts: [],
      historyRemovals: [],
      itemUpserts: [],
      itemRemovals: [],
      requestUpserts: [],
      requestRemovals: [],
      noticeUpserts: [],
    };
    applyLiveAppServerControlHistoryDelta(state, delta);
    expect(goalControlTitle(id)).toContain('Patched');
    vi.mocked(setAppServerControlGoal).mockResolvedValue({
      sessionId: id,
      threadId: 'own',
      turnId: 'new-turn',
      status: 'pending',
    });
    await controlAppServerControlGoal(id, { operation: 'get' });
    expect(setAppServerControlGoal).toHaveBeenCalledWith(id, {
      operation: 'get',
      expectedThreadId: 'own',
      expectedTurnId: 'new-turn',
    });
    expect(state.snapshot!.quickSettings.acknowledgedFields).toEqual(['permissionMode']);
  });
});
