import { describe, expect, it, vi } from 'vitest';

vi.mock('./i18n', () => ({
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

describe('sessionSurface', () => {
  it('keeps regular shell sessions on the terminal surface', async () => {
    const { resolveSessionSurfaceMode, getPrimarySurfaceLabel } = await import('./sessionSurface');

    expect(
      resolveSessionSurfaceMode({
        appServerControlOnly: false,
        supervisor: { profile: 'shell' },
      }),
    ).toBe('terminal');
    expect(getPrimarySurfaceLabel({ supervisor: { profile: 'shell' } })).toBe('Terminal');
  });

  it('keeps terminal sessions in Terminal even when codex metadata is present', async () => {
    const { resolveSessionSurfaceMode } = await import('./sessionSurface');

    expect(
      resolveSessionSurfaceMode({
        appServerControlOnly: false,
        supervisor: { profile: 'codex' },
      }),
    ).toBe('terminal');
  });
});
