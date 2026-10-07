import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  emptyCampaignGoalState,
  emptyFocusContextState,
  loadPersistedCampaignGoalState,
  loadPersistedFocusContextState,
  persistCampaignGoalState,
  persistFocusContextState,
} from './voiceContextPersistence';

function installStorage(): Map<string, string> {
  const values = new Map<string, string>();
  vi.stubGlobal('window', {
    localStorage: {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => values.set(key, value),
    },
  });
  return values;
}

afterEach(() => vi.unstubAllGlobals());

describe('voice context persistence', () => {
  it('restores both existing v1 records including their target information', () => {
    const values = installStorage();
    const focus = { ...emptyFocusContextState(), active: true, sessionId: 'session-1' };
    const campaign = {
      ...emptyCampaignGoalState(),
      active: true,
      objective: 'Repair updates',
      phase: 'verify' as const,
      targetSessionIds: ['session-1'],
    };
    values.set('midterm.voice.focusContext.v1', JSON.stringify(focus));
    values.set('midterm.voice.campaignGoal.v1', JSON.stringify(campaign));

    expect(loadPersistedFocusContextState()).toEqual({ state: focus, persisted: true });
    expect(loadPersistedCampaignGoalState()).toEqual({ state: campaign, persisted: true });
    expect(persistFocusContextState(focus)).toBe(true);
    expect(persistCampaignGoalState(campaign)).toBe(true);
    expect(values.size).toBe(2);
    expect(JSON.parse(values.get('midterm.voice.focusContext.v1')!)).toEqual(focus);
    expect(JSON.parse(values.get('midterm.voice.campaignGoal.v1')!)).toEqual(campaign);
  });

  it('distinguishes empty usable storage from corrupted or invalid records', () => {
    const values = installStorage();
    expect(loadPersistedFocusContextState().persisted).toBe(true);
    expect(loadPersistedCampaignGoalState().persisted).toBe(true);

    values.set('midterm.voice.focusContext.v1', '{');
    values.set('midterm.voice.campaignGoal.v1', JSON.stringify({ active: true }));
    expect(loadPersistedFocusContextState()).toEqual({
      state: emptyFocusContextState(),
      persisted: false,
    });
    expect(loadPersistedCampaignGoalState()).toEqual({
      state: emptyCampaignGoalState(),
      persisted: false,
    });
  });

  it.each([{ phase: 'unknown' }, { targetSessionIds: ['session-1', 42] }])(
    'rejects invalid campaign fields %j',
    (invalidFields) => {
      const values = installStorage();
      values.set(
        'midterm.voice.campaignGoal.v1',
        JSON.stringify({ ...emptyCampaignGoalState(), ...invalidFields }),
      );
      expect(loadPersistedCampaignGoalState().persisted).toBe(false);
    },
  );

  it('rejects invalid focus target fields', () => {
    const values = installStorage();
    values.set(
      'midterm.voice.focusContext.v1',
      JSON.stringify({ ...emptyFocusContextState(), sessionExists: 'yes' }),
    );
    expect(loadPersistedFocusContextState().persisted).toBe(false);
  });

  it.each([
    undefined,
    {
      get localStorage(): never {
        throw new Error('Storage denied');
      },
    },
  ])('handles unavailable browser storage on both read and write', (browser) => {
    vi.stubGlobal('window', browser);
    expect(loadPersistedFocusContextState().persisted).toBe(false);
    expect(loadPersistedCampaignGoalState().persisted).toBe(false);
    expect(persistFocusContextState(emptyFocusContextState())).toBe(false);
    expect(persistCampaignGoalState(emptyCampaignGoalState())).toBe(false);
  });

  it('reports failed writes without losing the caller state', () => {
    vi.stubGlobal('window', {
      localStorage: {
        setItem: () => {
          throw new Error('Quota exceeded');
        },
      },
    });
    const focus = { ...emptyFocusContextState(), active: true, sessionId: 'session-1' };
    expect(persistFocusContextState(focus)).toBe(false);
    expect(focus.sessionId).toBe('session-1');
    expect(persistCampaignGoalState(emptyCampaignGoalState())).toBe(false);
  });
});
