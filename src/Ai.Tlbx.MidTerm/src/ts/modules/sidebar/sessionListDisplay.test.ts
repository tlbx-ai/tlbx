import { describe, expect, it } from 'vitest';
import type { Session } from '../../types';
import { getSessionDisplayInfo } from './sessionList';

function makeSession(overrides: Partial<Session> = {}): Session {
  return {
    id: 's1',
    pid: 1,
    createdAt: '',
    isRunning: true,
    exitCode: null,
    cols: 120,
    rows: 30,
    shellType: 'Pwsh',
    name: null,
    terminalTitle: null,
    topic: null,
    notes: null,
    manuallyNamed: false,
    currentDirectory: 'Q:\\repos\\Jpa',
    foregroundPid: null,
    foregroundName: null,
    foregroundCommandLine: null,
    foregroundDisplayName: null,
    foregroundProcessIdentity: null,
    order: 0,
    parentSessionId: null,
    bookmarkId: null,
    spaceId: null,
    workspacePath: null,
    surface: null,
    isAdHoc: true,
    agentControlled: false,
    appServerControlOnly: false,
    profileHint: null,
    ...overrides,
  };
}

describe('getSessionDisplayInfo', () => {
  it('omits redundant shell/cmd.exe subtitle for named terminal sessions', () => {
    expect(
      getSessionDisplayInfo(
        makeSession({
          name: 'JPA',
          terminalTitle: 'C:\\WINDOWS\\system32\\cmd.exe',
        }),
      ),
    ).toEqual({ primary: 'JPA', secondary: null });

    expect(
      getSessionDisplayInfo(
        makeSession({
          name: 'JPA',
          terminalTitle: null,
        }),
      ),
    ).toEqual({ primary: 'JPA', secondary: null });
  });

  it('keeps surface subtitle for named Agent Controller sessions', () => {
    const info = getSessionDisplayInfo(
      makeSession({
        name: 'Review',
        appServerControlOnly: true,
        profileHint: 'codex',
      }),
    );
    expect(info.primary).toBe('Review');
    expect(info.secondary).toBeTruthy();
  });

  it('ignores bare cmd.exe wrapper titles on unnamed terminal sessions and keeps real app titles', () => {
    expect(
      getSessionDisplayInfo(
        makeSession({
          name: null,
          terminalTitle: 'C:\\WINDOWS\\system32\\cmd.exe',
        }),
      ).useProcessAsTitle,
    ).toBe(true);

    expect(
      getSessionDisplayInfo(
        makeSession({
          name: null,
          terminalTitle: 'Claude Code',
        }),
      ),
    ).toEqual({ primary: 'Claude Code', secondary: null });
  });
});
