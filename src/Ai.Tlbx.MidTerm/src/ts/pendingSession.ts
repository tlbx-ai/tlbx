import { pendingSessions } from './state';
import { removeSession, setSession } from './stores';
import type { Session } from './types';

export interface PendingSessionDetails {
  name?: string | null;
  currentDirectory?: string | null;
  shellType?: string | null;
  bookmarkId?: string | null;
  appServerControlOnly?: boolean;
  profileHint?: string | null;
}

export function createPendingSession(
  cols: number,
  rows: number,
  details: PendingSessionDetails = {},
): string {
  const tempId = 'pending-' + crypto.randomUUID();
  const tempSession: Session = {
    id: tempId,
    pid: 0,
    createdAt: new Date().toISOString(),
    isRunning: false,
    exitCode: null,
    name: details.name?.trim() || '',
    terminalTitle: '',
    topic: null,
    currentDirectory: details.currentDirectory?.trim() || '',
    foregroundPid: null,
    foregroundName: null,
    foregroundCommandLine: null,
    foregroundDisplayName: null,
    foregroundProcessIdentity: null,
    shellType: details.shellType?.trim() || 'Loading...',
    cols,
    rows,
    manuallyNamed: false,
    supervisor: {
      state: 'unknown',
      profile: 'unknown',
      needsAttention: false,
      attentionReason: null,
      attentionScore: 0,
      lastInputAt: null,
      lastOutputAt: null,
      lastBellAt: null,
      currentHeat: 0,
      lastTextOutputAt: null,
      textActivityAgeMs: null,
    },
    order: Date.now(),
    parentSessionId: null,
    bookmarkId: details.bookmarkId ?? null,
    spaceId: null,
    workspacePath: null,
    surface: null,
    isAdHoc: true,
    agentControlled: false,
    appServerControlOnly: details.appServerControlOnly === true,
    profileHint: details.profileHint ?? null,
    appServerControlResumeThreadId: null,
    hasAppServerControlHistory: false,
    agentAttachPoint: null,
  };

  setSession(tempSession);
  pendingSessions.add(tempId);
  return tempId;
}

export function clearPendingSession(tempId: string): void {
  pendingSessions.delete(tempId);
  removeSession(tempId);
}
