import { describe, expect, it } from 'vitest';
import type { LaunchEntry } from '../../api/types';
import { shouldShowAdHocBookmarkAction } from '../sidebar/spacesTreeSidebarLogic';
import { buildLocalBookmarkLaunchRequest } from './bookmarkLaunch';
import {
  buildPlainShellBookmarkDedupeKey,
  buildTerminalBookmarkDedupeKey,
  doesSessionMatchTerminalBookmark,
  extractBookmarkDirectoryTitle,
  findEquivalentStarredBookmark,
  isPlainShellExecutable,
  resolveBookmarkTitle,
} from './bookmarkSession';
import {
  getAgentBadge,
  getAgentDisplayName,
  getHistoryModeBadgeText,
  resolveSessionHistoryMode,
} from './launchMode';

describe('bookmark title and dedupe consistency', () => {
  it('uses explicit non-shell session names first and falls back to folder basename for unnamed sessions', () => {
    expect(
      resolveBookmarkTitle(
        { name: 'JPA', shellType: 'Pwsh' },
        {
          launchMode: 'terminal',
          profile: null,
          workingDirectory: 'Q:\\repos\\Jpa',
          executable: 'agy',
          commandLine: 'agy --dangerously-skip-permissions',
        },
      ),
    ).toEqual({ explicitLabel: 'JPA', defaultLabel: 'JPA' });

    expect(
      resolveBookmarkTitle(
        { name: 'Pwsh', shellType: 'Pwsh' },
        {
          launchMode: 'terminal',
          profile: null,
          workingDirectory: 'Q:\\repos\\tlbx-1\\',
          executable: 'pwsh',
          commandLine: null,
          isPlainShell: true,
        },
      ),
    ).toEqual({ explicitLabel: null, defaultLabel: 'tlbx-1' });

    expect(
      resolveBookmarkTitle(
        { name: '', shellType: 'Pwsh' },
        {
          launchMode: 'appServerControl',
          profile: 'antigravity',
          workingDirectory: 'C:\\',
          executable: 'antigravity',
          commandLine: null,
        },
      ),
    ).toEqual({ explicitLabel: null, defaultLabel: 'Antigravity' });
  });

  it('extracts directory titles cleanly across Windows and POSIX paths', () => {
    expect(extractBookmarkDirectoryTitle('Q:\\repos\\Jpa\\')).toBe('Jpa');
    expect(extractBookmarkDirectoryTitle('/home/dev/connect.tlbx.ai/')).toBe('connect.tlbx.ai');
    expect(extractBookmarkDirectoryTitle('C:\\')).toBeNull();
    expect(extractBookmarkDirectoryTitle('/')).toBeNull();
  });

  it('distinguishes plain shells, shell types, and command flags in terminal dedupe keys', () => {
    expect(isPlainShellExecutable('pwsh.exe', 'Pwsh')).toBe(true);
    expect(isPlainShellExecutable(null, 'Pwsh')).toBe(true);
    expect(isPlainShellExecutable('codex', 'Pwsh')).toBe(false);

    const pwshShell = buildPlainShellBookmarkDedupeKey('Pwsh', 'Q:\\repos\\Jpa');
    const bashShell = buildPlainShellBookmarkDedupeKey('bash', 'Q:\\repos\\Jpa');
    expect(pwshShell).toBe('shell|pwsh|q:/repos/jpa');
    expect(bashShell).toBe('shell|bash|q:/repos/jpa');

    const codexDefault = buildTerminalBookmarkDedupeKey('Pwsh', 'codex', 'codex', 'Q:\\repos\\Jpa');
    const codexAuto = buildTerminalBookmarkDedupeKey(
      'Pwsh',
      'codex',
      'codex --full-auto',
      'Q:\\repos\\Jpa',
    );
    const codexBash = buildTerminalBookmarkDedupeKey('bash', 'codex', 'codex', 'Q:\\repos\\Jpa');
    expect(codexDefault).not.toBe(codexAuto);
    expect(codexDefault).not.toBe(codexBash);
  });

  it('builds consistent local launch requests for plain shells and cross-platform command lines', () => {
    const plainShellEntry: LaunchEntry = {
      id: '1',
      shellType: 'Pwsh',
      executable: 'pwsh',
      commandLine: null,
      workingDirectory: 'Q:\\repos\\Jpa',
      isStarred: true,
      label: 'Jpa',
      notes: null,
      lastUsed: '',
      order: 0,
      launchMode: 'terminal',
      profile: null,
      launchOrigin: 'adhoc',
      surfaceType: 'trm',
    };
    expect(buildLocalBookmarkLaunchRequest(plainShellEntry, 120, 40)).toEqual({
      cols: 120,
      rows: 40,
      shell: 'Pwsh',
      workingDirectory: 'Q:\\repos\\Jpa',
      launchCommand: null,
    });

    const linuxReplayEntry: LaunchEntry = {
      ...plainShellEntry,
      shellType: 'bash',
      executable: 'python3',
      commandLine: '-m http.server 8000',
    };
    expect(buildLocalBookmarkLaunchRequest(linuxReplayEntry, 120, 40).launchCommand).toBe(
      'python3 -m http.server 8000',
    );
  });

  it('maps claude and antigravity badges cleanly and keeps profile-less ACP sessions in appServerControl mode', () => {
    expect(getAgentBadge('claude')).toBe('CLD');
    expect(getAgentBadge('antigravity')).toBe('AGY');
    expect(getAgentBadge('agy')).toBe('AGY');
    expect(getAgentDisplayName('antigravity')).toBe('Antigravity');
    expect(getHistoryModeBadgeText({ surfaceType: 'acp', profile: 'claude' })).toBe('CLD');
    expect(getHistoryModeBadgeText({ surfaceType: 'acp', profile: 'antigravity' })).toBe('AGY');

    expect(resolveSessionHistoryMode({ appServerControlOnly: true, profileHint: null })).toEqual({
      launchMode: 'appServerControl',
      profile: null,
    });
  });

  it('hides bookmark action for remote ACP sessions while allowing local ACP and remote terminals', () => {
    expect(
      shouldShowAdHocBookmarkAction(
        { isAdHoc: true, spaceId: null, bookmarkId: null, appServerControlOnly: true },
        'machine-1',
        true,
        true,
      ),
    ).toBe(false);

    expect(
      shouldShowAdHocBookmarkAction(
        { isAdHoc: true, spaceId: null, bookmarkId: null, appServerControlOnly: true },
        null,
        true,
        true,
      ),
    ).toBe(true);

    expect(
      shouldShowAdHocBookmarkAction(
        { isAdHoc: true, spaceId: null, bookmarkId: null, appServerControlOnly: false },
        'machine-1',
        true,
        true,
      ),
    ).toBe(true);
  });

  it('matches plain-shell and foregroundProcessIdentity terminal bookmarks on rename and overwrite lookup', () => {
    const plainShellEntry: LaunchEntry = {
      id: 'shell-1',
      shellType: 'Pwsh',
      executable: 'pwsh',
      commandLine: null,
      workingDirectory: 'Q:\\repos\\Jpa',
      isStarred: true,
      label: 'JPA Shell',
      notes: null,
      lastUsed: '',
      order: 0,
      launchMode: 'terminal',
      profile: null,
      launchOrigin: 'adhoc',
      surfaceType: 'trm',
    };

    expect(
      doesSessionMatchTerminalBookmark(
        { shellType: 'Pwsh', currentDirectory: 'Q:\\repos\\Jpa' },
        { name: null, commandLine: null, cwd: 'Q:\\repos\\Jpa', processIdentity: null },
        plainShellEntry,
      ),
    ).toBe(true);

    expect(
      doesSessionMatchTerminalBookmark(
        { shellType: 'Pwsh', currentDirectory: 'Q:\\repos\\Other' },
        { name: null, commandLine: null, cwd: 'Q:\\repos\\Other', processIdentity: null },
        plainShellEntry,
      ),
    ).toBe(false);

    const processIdentityEntry: LaunchEntry = {
      ...plainShellEntry,
      id: 'proc-1',
      executable: 'node',
      commandLine: 'node wrapper.js',
      foregroundProcessIdentity: 'custom-agent',
      label: 'Agent',
    };
    expect(
      doesSessionMatchTerminalBookmark(
        { shellType: 'Pwsh', currentDirectory: 'Q:\\repos\\Jpa' },
        {
          name: 'node',
          commandLine: 'node wrapper.js',
          cwd: 'Q:\\repos\\Jpa',
          processIdentity: 'custom-agent',
        },
        processIdentityEntry,
      ),
    ).toBe(true);

    expect(
      findEquivalentStarredBookmark([plainShellEntry, processIdentityEntry], {
        bookmarkId: null,
        launchMode: 'terminal',
        profile: null,
        shellType: 'pwsh',
        executable: 'node.exe',
        commandLine: 'node  wrapper.js',
        workingDirectory: 'q:/repos/jpa/',
      })?.id,
    ).toBe('proc-1');
  });
});
