import { afterEach, describe, expect, it, vi } from 'vitest';

describe('Goal drafts', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.resetModules();
  });
  it('round trips objective, status and budget through reload, scoped by session AND native thread', async () => {
    const data = new Map<string, string>();
    vi.stubGlobal('localStorage', {
      getItem: (key: string) => data.get(key) ?? null,
      setItem: (key: string, value: string) => data.set(key, value),
      removeItem: (key: string) => data.delete(key),
    });
    const original = await import('./goalDrafts');
    const draft = { objective: 'Separate', status: 'paused', budget: '1024' };
    original.writeGoalDraft('session', 'native', draft);
    vi.resetModules();
    const loaded = await import('./goalDrafts');
    expect(loaded.readGoalDraft('session', 'native')).toMatchObject(draft);
    expect(loaded.readGoalDraft('session', 'replacement')).toBeNull();
    expect(loaded.readGoalDraft('other-session', 'native')).toBeNull();
    expect(loaded.readGoalDraft('session', 'session')).toBeNull();
    const submitted = loaded.readGoalDraft('session', 'native');
    loaded.writeGoalDraft('session', 'native', draft);
    expect(loaded.clearGoalDraft('session', 'native', submitted)).toBe(false);
    const newer = { ...draft, budget: '2048' };
    loaded.writeGoalDraft('session', 'native', newer);
    expect(loaded.clearGoalDraft('session', 'native', draft)).toBe(false);
    expect(loaded.readGoalDraft('session', 'native')).toMatchObject(newer);
    expect(
      loaded.clearGoalDraft('session', 'native', loaded.readGoalDraft('session', 'native')),
    ).toBe(true);
    vi.resetModules();
    expect((await import('./goalDrafts')).readGoalDraft('session', 'native')).toBeNull();
  });
});
