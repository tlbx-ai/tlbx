import { t } from '../i18n';

export type HistoryLaunchMode = 'terminal' | 'appServerControl';
export type HistoryAppServerControlProfile = string;

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
  const normalized = (profile ?? '').trim().toLowerCase();
  return normalized && /^[a-z0-9][a-z0-9._-]*$/.test(normalized) ? normalized : null;
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

  if ((entry.surfaceType ?? '').toLowerCase() === 'acp') {
    const profile = normalizeHistoryAppServerControlProfile(entry.profile);
    return `${t('sessionTabs.agent')} · ${getAgentDisplayName(profile)}`;
  }

  if (!isAppServerControlHistoryEntry(entry)) {
    return t('session.terminal');
  }

  const profile = normalizeHistoryAppServerControlProfile(entry.profile);
  const providerText = getAgentDisplayName(profile);
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

  if (normalizedSurfaceType === 'acp') {
    return getAgentBadge(normalizeHistoryAppServerControlProfile(entry.profile));
  }

  if (!isAppServerControlHistoryEntry(entry)) {
    return 'TRM';
  }

  const profile = normalizeHistoryAppServerControlProfile(entry.profile);
  return getAgentBadge(profile);
}

function getAgentDisplayName(profile: HistoryAppServerControlProfile | null): string {
  if (profile === 'codex') return t('sessionLauncher.codexTitle');
  if (profile === 'grok') return 'Grok Build';
  if (profile === 'opencode') return 'OpenCode';
  if (profile === 'gemini') return 'Gemini CLI';
  if (profile === 'copilot') return 'GitHub Copilot CLI';
  return profile || t('sessionTabs.agent');
}

function getAgentBadge(profile: HistoryAppServerControlProfile | null): string {
  if (profile === 'codex') return 'CDX';
  if (profile === 'grok') return 'GRK';
  if (profile === 'opencode') return 'OPC';
  if (profile === 'gemini') return 'GEM';
  if (profile === 'copilot') return 'COP';
  return (profile || 'ACP').slice(0, 3).toUpperCase();
}
