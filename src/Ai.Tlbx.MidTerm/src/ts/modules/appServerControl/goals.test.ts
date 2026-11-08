import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AppServerControlGoalResponse, AppServerControlGoalState, AppServerControlHistorySnapshot } from '../../api/types';

vi.mock('../../api/client', () => ({ setAppServerControlGoal: vi.fn() }));
vi.mock('../i18n', () => ({ t: (key: string) => key }));
vi.mock('../navigation/backButtonGuard', () => ({ registerBackButtonLayer: vi.fn() }));
import { setAppServerControlGoal } from '../../api/client';
import { controlAppServerControlGoal, goalControlLabel, goalControlTitle, goalGuard, syncAppServerControlGoal } from './goals';

const api = vi.mocked(setAppServerControlGoal);
function state(objective = 'Original', pendingOperation: string | null = null): AppServerControlGoalState {
  return { threadId: 'own', confirmation: 'confirmed', scope: 'thread', goal: { objective, status: 'active' }, pendingOperation };
}
function seed(id: string, goal = state(), turnId: string | null = 'turn'): void {
  syncAppServerControlGoal({ sessionId: id, provider: 'claude', thread: { threadId: 'own', goal },
    currentTurn: { turnId, state: turnId ? 'running' : 'completed' } } as AppServerControlHistorySnapshot, true);
}
function receipt(id: string, goal = state()): AppServerControlGoalResponse {
  return { sessionId: id, threadId: 'own', turnId: 'turn', status: 'pending', state: goal };
}

describe('native Goal state', () => {
  beforeEach(() => { api.mockReset(); vi.stubGlobal('window', new EventTarget()); });
  afterEach(() => { vi.unstubAllGlobals(); });

  it('guards the exact canonical turn including idle and refuses disconnected targets', () => {
    expect(goalGuard({ connected: true, threadId: 'own', turnId: null })).toEqual({ expectedThreadId: 'own', expectedTurnId: null });
    expect(() => goalGuard({ connected: false, threadId: 'own', turnId: 'turn' })).toThrow('goal.targetUnavailable');
  });

  it('keeps pending separate from actual goal and never borrows composer text', async () => {
    seed('pending'); api.mockResolvedValue(receipt('pending', state('Original', 'set')));
    await controlAppServerControlGoal('pending', { operation: 'set', objective: 'Separate goal draft' });
    expect(api).toHaveBeenCalledWith('pending', { operation: 'set', objective: 'Separate goal draft', expectedThreadId: 'own', expectedTurnId: 'turn' });
    expect(goalControlTitle('pending')).toContain('Original');
    expect(goalControlLabel('pending')).toContain('goal.pending');
  });

  it('preserves a newer canonical readback when an older queued receipt arrives', async () => {
    seed('race'); let resolve!: (value: AppServerControlGoalResponse) => void;
    api.mockImplementation(() => new Promise((done) => { resolve = done; }));
    const pending = controlAppServerControlGoal('race', { operation: 'set', objective: 'New' });
    seed('race', state('Actual new')); resolve(receipt('race', state('Original', 'set'))); await pending;
    expect(goalControlTitle('race')).toContain('Actual new');
    expect(goalControlLabel('race')).not.toContain('goal.pending');
  });

  it('accepts confirmed thread state across turn end without replay or fake turn success', async () => {
    seed('ended'); let resolve!: (value: AppServerControlGoalResponse) => void;
    api.mockImplementation(() => new Promise((done) => { resolve = done; }));
    const pending = controlAppServerControlGoal('ended', { operation: 'get' });
    seed('ended', state(), null);
    resolve({ ...receipt('ended', state('Readback')), status: 'applied' }); await pending;
    expect(goalControlTitle('ended')).toContain('Readback');
    expect(api).toHaveBeenCalledTimes(1);
  });

  it('retains the old confirmed value on real error or targetUnavailable', async () => {
    seed('error'); api.mockRejectedValue(new Error('native failure'));
    await controlAppServerControlGoal('error', { operation: 'clear', interruptActiveTurn: true });
    expect(goalControlTitle('error')).toContain('Original');
    expect(goalControlTitle('error')).toContain('native failure');
    api.mockResolvedValue({ ...receipt('error'), status: 'targetUnavailable' });
    await controlAppServerControlGoal('error', { operation: 'set', objective: 'Wrong' });
    expect(goalControlTitle('error')).toContain('Original');
    expect(goalControlTitle('error')).toContain('goal.targetUnavailable');
  });
});
