import { describe, expect, it, vi } from 'vitest';

vi.mock('../i18n', () => ({
  t: (key: string) =>
    (
      ({
        'session.terminal': 'Terminal',
        'sessionTabs.agent': 'Agent',
        'sessionLauncher.codexTitle': 'Codex',
        'sessionLauncher.claudeTitle': 'Claude',
      }) as Record<string, string>
    )[key] ?? key,
}));

describe('history launch mode helpers', () => {
  it('keeps appServerControl entries provider-specific', async () => {
    const {
      isAppServerControlHistoryEntry,
      getHistoryModeDisplayText,
      getHistoryModeBadgeText,
      resolveSessionHistoryMode,
    } = await import('./launchMode');

    expect(
      isAppServerControlHistoryEntry({ launchMode: 'appServerControl', profile: 'opencode' }),
    ).toBe(true);
    expect(getHistoryModeDisplayText({ launchMode: 'appServerControl', profile: 'opencode' })).toBe(
      'Agent · OpenCode',
    );
    expect(getHistoryModeBadgeText({ launchMode: 'appServerControl', profile: 'opencode' })).toBe(
      'OPC',
    );
    expect(getHistoryModeDisplayText({ launchMode: 'appServerControl', profile: 'gemini' })).toBe(
      'Agent · Gemini CLI',
    );
    expect(getHistoryModeBadgeText({ launchMode: 'appServerControl', profile: 'copilot' })).toBe(
      'COP',
    );
    expect(
      isAppServerControlHistoryEntry({ launchMode: 'appServerControl', profile: 'claude' }),
    ).toBe(false);
    expect(getHistoryModeBadgeText({ launchMode: 'appServerControl', profile: 'codex' })).toBe(
      'CDX',
    );
    expect(
      resolveSessionHistoryMode({
        appServerControlOnly: true,
        profileHint: 'codex',
      }),
    ).toEqual({
      launchMode: 'appServerControl',
      profile: 'codex',
    });
  });

  it('keeps dynamically discovered ACP profiles bookmarkable', async () => {
    const { getBookmarkSurfaceType } = await import('./bookmarkSession');
    const { resolveSessionHistoryMode } = await import('./launchMode');
    const session = {
      id: 'session-opencode',
      appServerControlOnly: true,
      profileHint: 'opencode',
    } as never;

    const mode = resolveSessionHistoryMode(session);
    expect(mode).toEqual({ launchMode: 'appServerControl', profile: 'opencode' });
    expect(getBookmarkSurfaceType(session, mode.profile)).toBe('acp');
  });
});
