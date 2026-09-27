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
  it('does not rebuild the workspace tree for supervisor activity alone', () => {
    const previous = session({});
    const current = session({
      supervisor: {
        ...previous.supervisor,
        state: 'busy-turn',
        attentionScore: 20,
        needsAttention: true,
      } as Session['supervisor'],
    });
    expect(getSidebarFastPathSessionUpdates({ s1: previous }, { s1: current })).toEqual([]);
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
