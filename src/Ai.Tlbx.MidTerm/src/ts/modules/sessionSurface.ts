import { t } from './i18n';

export type SessionSurfaceMode = 'terminal' | 'agent';
export type SessionAgentProfile = 'codex' | 'claude' | 'grok' | null;

export interface SessionSurfaceLike {
  appServerControlOnly?: boolean | null;
  profileHint?: string | null;
  supervisor?: {
    profile?: string | null;
  } | null;
}

export function normalizeAgentProfile(profile: string | null | undefined): SessionAgentProfile {
  return profile === 'codex' || profile === 'claude' || profile === 'grok' ? profile : null;
}

export function resolveSessionAgentProfile(
  session: SessionSurfaceLike | null | undefined,
): SessionAgentProfile {
  return normalizeAgentProfile(session?.profileHint ?? session?.supervisor?.profile);
}

export function isAgentSurfaceSession(session: SessionSurfaceLike | null | undefined): boolean {
  return session?.appServerControlOnly === true;
}

export function resolveSessionSurfaceMode(
  session: SessionSurfaceLike | null | undefined,
): SessionSurfaceMode {
  return isAgentSurfaceSession(session) ? 'agent' : 'terminal';
}

export function getAgentSurfaceLabel(session: SessionSurfaceLike | null | undefined): string {
  const profile = resolveSessionAgentProfile(session);
  if (profile === 'codex') {
    return t('sessionLauncher.codexTitle');
  }

  if (profile === 'claude') {
    return t('sessionLauncher.claudeTitle');
  }

  if (profile === 'grok') {
    return 'Grok';
  }

  return t('sessionTabs.agent');
}

export function getPrimarySurfaceLabel(session: SessionSurfaceLike | null | undefined): string {
  return resolveSessionSurfaceMode(session) === 'agent'
    ? getAgentSurfaceLabel(session)
    : t('session.terminal');
}
