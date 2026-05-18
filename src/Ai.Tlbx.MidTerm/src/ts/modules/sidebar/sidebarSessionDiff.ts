import type { Session } from '../../types';

type SessionMap = Record<string, Session>;

const FIELD_SEPARATOR = '\u001f';

function normalizeSignatureValue(value: unknown): string {
  if (value === null || value === undefined) {
    return '';
  }

  if (typeof value === 'string') {
    return value;
  }

  if (typeof value === 'boolean' || typeof value === 'number' || typeof value === 'bigint') {
    return value.toString();
  }

  return '';
}

function getSupervisorSignature(session: Session): string {
  const supervisor = session.supervisor;
  if (!supervisor) {
    return '';
  }

  return [
    supervisor.profile,
    supervisor.state,
    supervisor.needsAttention,
    supervisor.attentionScore,
    supervisor.attentionReason,
  ]
    .map(normalizeSignatureValue)
    .join(FIELD_SEPARATOR);
}

function getAgentAttachPointSignature(session: Session): string {
  const attachPoint = session.agentAttachPoint;
  if (!attachPoint) {
    return '';
  }

  return [
    attachPoint.provider,
    attachPoint.transportKind,
    attachPoint.endpoint,
    attachPoint.sharedRuntime,
    attachPoint.source,
    attachPoint.preferredThreadId,
  ]
    .map(normalizeSignatureValue)
    .join(FIELD_SEPARATOR);
}

function getSidebarStructuralSignature(session: Session): string {
  return [
    session.id,
    session.pid,
    session.createdAt,
    session.isRunning,
    session.exitCode,
    session.name,
    session.manuallyNamed,
    session.shellType,
    session.workspacePath,
    session.workspacePath ? '' : session.currentDirectory,
    session.spaceId,
    session.isAdHoc,
    session.bookmarkId,
    session.parentSessionId,
    session.order,
    session._order,
    session.agentControlled,
    session.appServerControlOnly,
    session.profileHint,
    session.surface,
    session.appServerControlResumeThreadId,
    session.hasAppServerControlHistory,
    getAgentAttachPointSignature(session),
    getSupervisorSignature(session),
  ]
    .map(normalizeSignatureValue)
    .join(FIELD_SEPARATOR);
}

function getSidebarPatchableContentSignature(session: Session): string {
  return [
    session.terminalTitle,
    session.currentDirectory,
    session.foregroundPid,
    session.foregroundName,
    session.foregroundCommandLine,
    session.foregroundDisplayName,
    session.foregroundProcessIdentity,
    session.topic,
    session.notes,
  ]
    .map(normalizeSignatureValue)
    .join(FIELD_SEPARATOR);
}

/**
 * Returns null when the sidebar tree needs a full render.
 * Otherwise returns sessions whose volatile display content can be patched in place.
 */
export function getSidebarFastPathSessionUpdates(
  previous: SessionMap,
  current: SessionMap,
): Session[] | null {
  const previousIds = Object.keys(previous).sort();
  const currentIds = Object.keys(current).sort();
  if (previousIds.length !== currentIds.length) {
    return null;
  }

  const contentChangedSessions: Session[] = [];
  for (let index = 0; index < currentIds.length; index += 1) {
    const sessionId = currentIds[index];
    const previousId = previousIds[index];
    if (!sessionId || !previousId || sessionId !== previousId) {
      return null;
    }

    const previousSession = previous[sessionId];
    const currentSession = current[sessionId];
    if (!previousSession || !currentSession) {
      return null;
    }

    if (
      getSidebarStructuralSignature(previousSession) !==
      getSidebarStructuralSignature(currentSession)
    ) {
      return null;
    }

    if (
      getSidebarPatchableContentSignature(previousSession) !==
      getSidebarPatchableContentSignature(currentSession)
    ) {
      contentChangedSessions.push(currentSession);
    }
  }

  return contentChangedSessions;
}
