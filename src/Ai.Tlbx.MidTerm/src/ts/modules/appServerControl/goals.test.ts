import { readGoalDraft, writeGoalDraft } from './goalDrafts';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type {
  AppServerControlGoalResponse,
  AppServerControlGoalState,
  AppServerControlHistorySnapshot,
} from '../../api/types';

vi.mock('../../api/client', () => ({ setAppServerControlGoal: vi.fn() }));
vi.mock('../i18n', () => ({ t: (key: string) => key }));
vi.mock('../navigation/backButtonGuard', () => ({ registerBackButtonLayer: vi.fn(() => vi.fn()) }));
import { setAppServerControlGoal } from '../../api/client';
import {
  controlAppServerControlGoal,
  goalControlLabel,
  goalControlTitle,
  goalGuard,
  openAppServerControlGoalEditor,
  syncAppServerControlGoal,
} from './goals';

const api = vi.mocked(setAppServerControlGoal);
function state(
  objective = 'Original',
  pendingOperation: string | null = null,
): AppServerControlGoalState {
  return {
    threadId: 'own',
    confirmation: 'confirmed',
    scope: 'thread',
    goal: { objective, status: 'active' },
    pendingOperation,
  };
}
function seed(id: string, goal = state(), turnId: string | null = 'turn'): void {
  syncAppServerControlGoal(
    {
      sessionId: id,
      provider: 'claude',
      thread: { threadId: 'own', goal },
      currentTurn: { turnId, state: turnId ? 'running' : 'completed' },
    } as AppServerControlHistorySnapshot,
    true,
  );
}
function receipt(id: string, goal = state()): AppServerControlGoalResponse {
  return { sessionId: id, threadId: 'own', turnId: 'turn', status: 'pending', state: goal };
}

describe('native Goal state', () => {
  beforeEach(() => {
    api.mockReset();
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
  });
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('guards the exact canonical turn including idle and refuses disconnected targets', () => {
    expect(goalGuard({ connected: true, threadId: 'own', turnId: null })).toEqual({
      expectedThreadId: 'own',
      expectedTurnId: null,
    });
    expect(() => goalGuard({ connected: false, threadId: 'own', turnId: 'turn' })).toThrow(
      'goal.targetUnavailable',
    );
  });

  it('keeps pending separate from actual goal and never borrows composer text', async () => {
    seed('pending');
    api.mockResolvedValue(receipt('pending', state('Original', 'set')));
    await controlAppServerControlGoal('pending', {
      operation: 'set',
      objective: 'Separate goal draft',
    });
    expect(api).toHaveBeenCalledWith('pending', {
      operation: 'set',
      objective: 'Separate goal draft',
      expectedThreadId: 'own',
      expectedTurnId: 'turn',
    });
    expect(goalControlTitle('pending')).toContain('Original');
    expect(goalControlLabel('pending')).toContain('goal.pending');
  });

  it('preserves a newer canonical readback when an older queued receipt arrives', async () => {
    seed('race');
    let resolve!: (value: AppServerControlGoalResponse) => void;
    api.mockImplementation(
      () =>
        new Promise((done) => {
          resolve = done;
        }),
    );
    const pending = controlAppServerControlGoal('race', { operation: 'set', objective: 'New' });
    seed('race', state('Actual new'));
    resolve(receipt('race', state('Original', 'set')));
    await pending;
    expect(goalControlTitle('race')).toContain('Actual new');
    expect(goalControlLabel('race')).not.toContain('goal.pending');
  });

  it('accepts confirmed thread state across turn end without replay or fake turn success', async () => {
    seed('ended');
    let resolve!: (value: AppServerControlGoalResponse) => void;
    api.mockImplementation(
      () =>
        new Promise((done) => {
          resolve = done;
        }),
    );
    const pending = controlAppServerControlGoal('ended', { operation: 'get' });
    seed('ended', state(), null);
    resolve({ ...receipt('ended', state('Readback')), status: 'applied' });
    await pending;
    expect(goalControlTitle('ended')).toContain('Readback');
    expect(api).toHaveBeenCalledTimes(1);
  });

  it('retains the old confirmed value on real error or targetUnavailable', async () => {
    seed('error');
    api.mockRejectedValue(new Error('native failure'));
    await controlAppServerControlGoal('error', { operation: 'clear', interruptActiveTurn: true });
    expect(goalControlTitle('error')).toContain('Original');
    expect(goalControlTitle('error')).toContain('native failure');
    api.mockResolvedValue({ ...receipt('error'), status: 'targetUnavailable' });
    await controlAppServerControlGoal('error', { operation: 'set', objective: 'Wrong' });
    expect(goalControlTitle('error')).toContain('Original');
    expect(goalControlTitle('error')).toContain('goal.targetUnavailable');
  });
  it('ingests canonical state without a DOM', () => {
    vi.stubGlobal('window', undefined);
    expect(() => seed('no-dom')).not.toThrow();
    expect(goalControlTitle('no-dom')).toContain('Original');
  });

  it('clears only a confirmed mutation draft, preserving newer objective/status/budget edits', async () => {
    seed('draft-confirm');
    const draft = { objective: 'Sent', status: 'paused', budget: '123' };
    writeGoalDraft('draft-confirm', 'own', draft);
    api.mockResolvedValue(receipt('draft-confirm', state('Original', 'set')));
    await controlAppServerControlGoal('draft-confirm', { operation: 'set', objective: 'Sent' });
    expect(readGoalDraft('draft-confirm', 'own')).toMatchObject(draft);
    const newer = { ...draft, objective: 'Newer', status: 'blocked', budget: '456' };
    writeGoalDraft('draft-confirm', 'own', newer);
    seed('draft-confirm', state('Sent'));
    expect(readGoalDraft('draft-confirm', 'own')).toMatchObject(newer);
    api.mockResolvedValue({
      ...receipt('draft-confirm', { ...state(), goal: null }),
      status: 'applied',
    });
    await controlAppServerControlGoal('draft-confirm', {
      operation: 'clear',
      interruptActiveTurn: true,
    });
    expect(readGoalDraft('draft-confirm', 'own')).toBeNull();
  });

  it('keeps drafts on unconfirmed responses and rejects session fallback identity', async () => {
    seed('draft-unknown');
    const draft = { objective: 'Unknown', status: 'active', budget: '' };
    writeGoalDraft('draft-unknown', 'own', draft);
    api.mockResolvedValue({ ...receipt('draft-unknown'), status: 'unconfirmed' });
    await controlAppServerControlGoal('draft-unknown', { operation: 'set', objective: 'Unknown' });
    seed('draft-unknown', state('Unknown'));
    expect(readGoalDraft('draft-unknown', 'own')).toMatchObject(draft);
    syncAppServerControlGoal(
      {
        sessionId: 'fallback',
        provider: 'claude',
        thread: { threadId: 'fallback' },
        currentTurn: { state: 'completed' },
      } as AppServerControlHistorySnapshot,
      true,
    );
    const count = api.mock.calls.length;
    await controlAppServerControlGoal('fallback', { operation: 'get' });
    expect(api).toHaveBeenCalledTimes(count);
  });
  it('closes the open editor on canonical thread replacement without applying its draft to that thread', () => {
    class Element extends EventTarget {
      children: Element[] = [];
      className = '';
      textContent = '';
      value = '';
      removed = false;
      append(...children: Element[]): void {
        this.children.push(...children);
      }
      setAttribute(): void {
        /* Minimal editor DOM fixture. */
      }
      focus(): void {
        /* Focus is owned by the real browser acceptance. */
      }
      remove(): void {
        this.removed = true;
      }
    }
    const body = new Element();
    vi.stubGlobal('HTMLElement', Element);
    vi.stubGlobal('document', { body, activeElement: null, createElement: () => new Element() });
    seed('open-scope');
    api.mockResolvedValue(receipt('open-scope'));
    openAppServerControlGoalEditor('open-scope');
    const overlay = body.children[0]!;
    const objective = overlay.children[0]!.children[1]!.children[3]!;
    objective.value = 'Old thread only';
    objective.dispatchEvent(new Event('input'));
    expect(readGoalDraft('open-scope', 'own')?.objective).toBe('Old thread only');
    const calls = api.mock.calls.length;
    syncAppServerControlGoal(
      {
        sessionId: 'open-scope',
        provider: 'claude',
        thread: { threadId: 'replacement', goal: null },
        currentTurn: { state: 'completed' },
      } as AppServerControlHistorySnapshot,
      true,
    );
    expect(overlay.removed).toBe(true);
    expect(readGoalDraft('open-scope', 'replacement')).toBeNull();
    expect(api).toHaveBeenCalledTimes(calls);
  });
});
