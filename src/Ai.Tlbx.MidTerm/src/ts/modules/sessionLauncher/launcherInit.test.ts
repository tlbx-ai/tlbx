import { describe, expect, it } from 'vitest';
import {
  applyLauncherBrowserDefaults,
  commitLauncherPathForAction,
  resolveLauncherProviderChoice,
  type LauncherBrowserPathState,
  type LauncherPathCommitContext,
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

function createCommitHarness(draft: string) {
  const launcher = { currentPath: '/home/owner', pathDraft: draft, targetId: 'local' };
  const errors: string[] = [];
  let finishLoad: (loaded: boolean) => void = () => {};
  const context: LauncherPathCommitContext = {
    getPathDraft: () => launcher.pathDraft,
    getCurrentPath: () => launcher.currentPath,
    getTargetId: () => launcher.targetId,
    loadDirectory: (path) =>
      new Promise<boolean>((resolve) => {
        finishLoad = (loaded) => {
          if (loaded) {
            launcher.currentPath = path;
            if (launcher.pathDraft === draft) launcher.pathDraft = path;
          }
          resolve(loaded);
        };
      }),
    setError: (message) => errors.push(message),
  };
  return { launcher, errors, context, finishLoad: (loaded: boolean) => finishLoad(loaded) };
}

describe('launcher path commit before create or resume', () => {
  it('waits for the typed path to validate instead of launching the stale home path', async () => {
    const harness = createCommitHarness('/repos/jpa');
    let result: string | null | undefined;
    const pending = commitLauncherPathForAction(harness.context, 'Path is required').then(
      (value) => {
        result = value;
      },
    );

    await Promise.resolve();
    expect(result).toBeUndefined();
    harness.finishLoad(true);
    await pending;
    expect(result).toBe('/repos/jpa');
  });

  it('does not launch an unreachable path and keeps the home path untouched', async () => {
    const harness = createCommitHarness('/missing');
    const pending = commitLauncherPathForAction(harness.context, 'Path is required');
    harness.finishLoad(false);

    expect(await pending).toBeNull();
    expect(harness.launcher.currentPath).toBe('/home/owner');
    expect(harness.launcher.pathDraft).toBe('/missing');
  });

  it('does not launch for the old target when the target changes during validation', async () => {
    const harness = createCommitHarness('/repos/jpa');
    const pending = commitLauncherPathForAction(harness.context, 'Path is required');
    harness.launcher.targetId = 'hub:other';
    harness.finishLoad(true);

    expect(await pending).toBeNull();
  });

  it('keeps newer typing and does not launch the path validated before it', async () => {
    const harness = createCommitHarness('/repos/jpa');
    const pending = commitLauncherPathForAction(harness.context, 'Path is required');
    harness.launcher.pathDraft = '/repos/jpa/agent';
    harness.finishLoad(true);

    expect(await pending).toBeNull();
    expect(harness.launcher.pathDraft).toBe('/repos/jpa/agent');
  });

  it('reports a missing path without loading anything', async () => {
    const harness = createCommitHarness('   ');

    expect(await commitLauncherPathForAction(harness.context, 'Path is required')).toBeNull();
    expect(harness.errors).toEqual(['Path is required']);
  });
});
