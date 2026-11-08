import { describe, expect, it } from 'vitest';
import {
  applyLauncherBrowserDefaults,
  resolveLauncherProviderChoice,
  type LauncherBrowserPathState,
} from './launcherInit';

const defaults = { path: 'C:\\Users\\owner', homePath: 'C:\\Users\\owner', startPath: '' };

function createState(
  overrides: Partial<LauncherBrowserPathState<string>> = {},
): LauncherBrowserPathState<string> {
  return {
    homePath: '',
    startPath: '',
    currentPath: '',
    pathDraft: '',
    pathDirty: false,
    pathHistory: [],
    parentPath: null,
    roots: [],
    entries: [],
    ...overrides,
  };
}

describe('launcher init defaults', () => {
  it('keeps a path the user typed before the async home default arrives', () => {
    const state = createState({ pathDraft: 'Q:\\repos\\Jpa', pathDirty: true });

    expect(applyLauncherBrowserDefaults(state, defaults, ['C:\\'])).toBe('Q:\\repos\\Jpa');
    expect(state.pathDraft).toBe('Q:\\repos\\Jpa');
    expect(state.homePath).toBe('C:\\Users\\owner');
    expect(state.roots).toEqual(['C:\\']);
  });

  it('prefers the typed path over an opening preset path', () => {
    const state = createState({ pathDraft: 'Q:\\repos\\Jpa', pathDirty: true });

    expect(applyLauncherBrowserDefaults(state, defaults, [], 'Q:\\repos\\Other')).toBe(
      'Q:\\repos\\Jpa',
    );
  });

  it('uses the preset, then the home default, when the user has not chosen a path', () => {
    expect(applyLauncherBrowserDefaults(createState(), defaults, [], 'Q:\\repos\\Other')).toBe(
      'Q:\\repos\\Other',
    );

    const state = createState({ currentPath: 'stale', pathDraft: 'stale' });
    expect(applyLauncherBrowserDefaults(state, defaults, [], undefined)).toBe('C:\\Users\\owner');
    expect(state.pathDraft).toBe('C:\\Users\\owner');
  });

  it('keeps the chosen provider when the select reports no valid option', () => {
    expect(resolveLauncherProviderChoice('codex', '')).toBe('codex');
    expect(resolveLauncherProviderChoice('codex', 'claude')).toBe('claude');
  });
});
