import { describe, expect, it } from 'vitest';
import type { Session } from '../../types';
import { getSidebarFastPathSessionUpdates } from './sidebarSessionDiff';

function session(overrides: Partial<Session>): Session {
  return {
    id: 's1',
    name: null,
    terminalTitle: null,
    manuallyNamed: false,
    shellType: 'pwsh',
    currentDirectory: 'Q:/repos/MidTerm',
    workspacePath: 'Q:/repos/MidTerm',
    spaceId: null,
    isAdHoc: true,
    bookmarkId: null,
    parentSessionId: null,
    order: 1,
    _order: 1,
    agentControlled: false,
    appServerControlOnly: false,
    profileHint: null,
    hasAppServerControlHistory: false,
    foregroundPid: null,
    foregroundName: null,
    foregroundCommandLine: null,
    foregroundDisplayName: null,
    foregroundProcessIdentity: null,
    supervisor: {
      profile: null,
      state: 'unknown',
      needsAttention: false,
      attentionScore: 0,
      attentionReason: null,
    },
    cols: 120,
    rows: 30,
    pid: 123,
    ...overrides,
  } as Session;
}

describe('sidebar session diff', () => {
  it('allows terminal-title-only updates to patch existing sidebar rows', () => {
    const previous = session({ terminalTitle: 'build' });
    const current = session({
      terminalTitle: 'build ⠋',
      supervisor: previous.supervisor ? { ...previous.supervisor } : null,
    });

    expect(getSidebarFastPathSessionUpdates({ s1: previous }, { s1: current })).toEqual([current]);
  });

  it('ignores terminal-size-only updates because they do not affect sidebar rows', () => {
    const previous = session({ cols: 120, rows: 30 });
    const current = session({ cols: 100, rows: 24 });

    expect(getSidebarFastPathSessionUpdates({ s1: previous }, { s1: current })).toEqual([]);
  });

  it('allows foreground-process-only updates to patch existing sidebar rows', () => {
    const previous = session({ foregroundName: 'pwsh', foregroundCommandLine: 'pwsh' });
    const current = session({
      foregroundName: 'codex',
      foregroundCommandLine: 'codex --model gpt-5.4',
      foregroundDisplayName: 'codex',
    });

    expect(getSidebarFastPathSessionUpdates({ s1: previous }, { s1: current })).toEqual([current]);
  });

  it('allows notes-only updates to patch existing sidebar rows', () => {
    const previous = session({ notes: null });
    const current = session({ notes: 'investigate resize' });

    expect(getSidebarFastPathSessionUpdates({ s1: previous }, { s1: current })).toEqual([current]);
  });

  it('allows topic-only updates to patch existing sidebar rows', () => {
    const previous = session({ topic: null });
    const current = session({ topic: 'DAI test worker' });

    expect(getSidebarFastPathSessionUpdates({ s1: previous }, { s1: current })).toEqual([current]);
  });

  it('allows cwd display updates when workspace placement is stable', () => {
    const previous = session({
      currentDirectory: 'Q:/repos/MidTerm',
      workspacePath: 'Q:/repos/MidTerm',
    });
    const current = session({
      currentDirectory: 'Q:/repos/MidTerm/src',
      workspacePath: 'Q:/repos/MidTerm',
    });

    expect(getSidebarFastPathSessionUpdates({ s1: previous }, { s1: current })).toEqual([current]);
  });

  it('requires a full render when cwd controls sidebar placement', () => {
    const previous = session({
      currentDirectory: 'Q:/repos/MidTerm',
      workspacePath: null,
    });
    const current = session({
      currentDirectory: 'Q:/repos/MidTerm/src',
      workspacePath: null,
    });

    expect(getSidebarFastPathSessionUpdates({ s1: previous }, { s1: current })).toBeNull();
  });

  it('requires a full render when a sidebar structural field changes', () => {
    const previous = session({ terminalTitle: 'build' });
    const current = session({ terminalTitle: 'build ⠋', name: 'worker' });

    expect(getSidebarFastPathSessionUpdates({ s1: previous }, { s1: current })).toBeNull();
  });

  it('requires a full render when sessions are added or removed', () => {
    const previous = session({ id: 's1' });
    const current = session({ id: 's2' });

    expect(getSidebarFastPathSessionUpdates({ s1: previous }, { s2: current })).toBeNull();
  });
});
