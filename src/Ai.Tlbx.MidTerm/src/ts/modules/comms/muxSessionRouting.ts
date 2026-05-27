import { sessionTerminals } from '../../state';
import { $activeSessionId, $currentSettings } from '../../stores';
import { isHubSessionId } from '../hub/runtime';
import { resolveReplayRowsFromTerminals } from './muxReplayRows';

export function isQuickResumeEnabled(): boolean {
  return $currentSettings.get()?.resumeMode === 'quickResume';
}

export function normalizeSessionIds(sessionIds: readonly string[]): string[] {
  return [...new Set(sessionIds.filter((sessionId) => !isHubSessionId(sessionId)))].sort();
}

export function getStreamableSessionIds(
  activeSessionId: string | null,
  visibleSessionIds: readonly string[],
): Set<string> {
  const streamable = new Set<string>(visibleSessionIds);
  if (activeSessionId && !isHubSessionId(activeSessionId)) {
    streamable.add(activeSessionId);
  }
  return streamable;
}

export function getReplayRows(sessionId?: string | null): number | null {
  return resolveReplayRowsFromTerminals(
    sessionId,
    $activeSessionId.get(),
    sessionTerminals,
    isHubSessionId,
  );
}
