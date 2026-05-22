import { t } from '../i18n';

export type HistoryLaunchMode = 'terminal' | 'appServerControl';
export type HistoryAppServerControlProfile = 'codex' | 'claude' | 'grok';

export interface HistoryModeEntry {
  launchMode?: string | null;
  profile?: string | null;
  surfaceType?: string | null;
}

export interface HistoryModeSessionLike {
  appServerControlOnly?: boolean | null;
  profileHint?: string | null;
  supervisor?: {
    profile?: string | null;
  } | null;
}

export function normalizeHistoryLaunchMode(mode: string | null | undefined): HistoryLaunchMode {
  return mode === 'appServerControl' ? 'appServerControl' : 'terminal';
}

export function normalizeHistoryAppServerControlProfile(
  profile: string | null | undefined,
): HistoryAppServerControlProfile | null {
  return profile === 'codex' || profile === 'claude' || profile === 'grok' ? profile : null;
}

export function isAppServerControlHistoryEntry(entry: HistoryModeEntry): boolean {
  return (
    normalizeHistoryLaunchMode(entry.launchMode) === 'appServerControl' &&
    normalizeHistoryAppServerControlProfile(entry.profile) !== null
  );
}

export function resolveSessionHistoryMode(session: HistoryModeSessionLike): {
  launchMode: HistoryLaunchMode;
  profile: HistoryAppServerControlProfile | null;
} {
  if (session.appServerControlOnly === true) {
    const profile = normalizeHistoryAppServerControlProfile(
      session.profileHint ?? session.supervisor?.profile,
    );
    if (profile) {
      return {
        launchMode: 'appServerControl',
        profile,
      };
    }
  }

  return {
    launchMode: 'terminal',
    profile: null,
  };
}

export function getHistoryModeDisplayText(entry: HistoryModeEntry): string {
  if ((entry.surfaceType ?? '').toLowerCase() === 'cld') {
    return `${t('sessionTabs.agent')} · ${t('sessionLauncher.claudeTitle')}`;
  }

  if ((entry.surfaceType ?? '').toLowerCase() === 'cdx') {
    return `${t('sessionTabs.agent')} · ${t('sessionLauncher.codexTitle')}`;
  }

  if ((entry.surfaceType ?? '').toLowerCase() === 'grk') {
    return `${t('sessionTabs.agent')} · Grok`;
  }

  if (!isAppServerControlHistoryEntry(entry)) {
    return t('session.terminal');
  }

  const profile = normalizeHistoryAppServerControlProfile(entry.profile);
  const providerText =
    profile === 'claude'
      ? t('sessionLauncher.claudeTitle')
      : profile === 'grok'
        ? 'Grok'
        : t('sessionLauncher.codexTitle');
  return `${t('sessionTabs.agent')} · ${providerText}`;
}

export function getHistoryModeBadgeText(entry: HistoryModeEntry): string {
  const normalizedSurfaceType = (entry.surfaceType ?? '').toLowerCase();
  if (normalizedSurfaceType === 'cld') {
    return 'CLD';
  }

  if (normalizedSurfaceType === 'cdx') {
    return 'CDX';
  }

  if (normalizedSurfaceType === 'grk') {
    return 'GRK';
  }

  if (!isAppServerControlHistoryEntry(entry)) {
    return 'TRM';
  }

  const profile = normalizeHistoryAppServerControlProfile(entry.profile);
  return profile === 'claude' ? 'CLD' : profile === 'grok' ? 'GRK' : 'CDX';
}
