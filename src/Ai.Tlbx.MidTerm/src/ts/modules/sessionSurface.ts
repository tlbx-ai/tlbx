import { t } from './i18n';

export type SessionSurfaceMode = 'terminal' | 'agent';
export type SessionAgentProfile =
  'codex' | 'claude' | 'grok' | 'antigravity' | 'copilot' | 'gemini' | 'opencode' | null;

export interface SessionSurfaceLike {
  appServerControlOnly?: boolean | null;
  profileHint?: string | null;
  supervisor?: {
    profile?: string | null;
  } | null;
}

export function normalizeAgentProfile(profile: string | null | undefined): SessionAgentProfile {
  const normalized = profile?.trim().toLowerCase();
  if (
    normalized === 'codex' ||
    normalized === 'claude' ||
    normalized === 'grok' ||
    normalized === 'antigravity' ||
    normalized === 'copilot' ||
    normalized === 'gemini' ||
    normalized === 'opencode'
  ) {
    return normalized;
  }
  return null;
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

  if (profile === 'antigravity') {
    return 'Antigravity';
  }

  if (profile === 'copilot') {
    return 'Copilot';
  }

  if (profile === 'gemini') {
    return 'Gemini';
  }

  if (profile === 'opencode') {
    return 'OpenCode';
  }

  return t('sessionTabs.agent');
}

export function getPrimarySurfaceLabel(session: SessionSurfaceLike | null | undefined): string {
  return resolveSessionSurfaceMode(session) === 'agent'
    ? getAgentSurfaceLabel(session)
    : t('session.terminal');
}
