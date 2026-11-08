import type { AppServerControlHistoryRequestSummary, MidTermSettingsPublic } from '../../api/types';
import { $currentSettings } from '../../stores';
import type { AppServerControlHistoryEntry } from './types';

export function areAgentTechnicalDetailsEnabled(
  settings: Pick<MidTermSettingsPublic, 'showAgentTechnicalDetails'> | null | undefined,
): boolean {
  return settings?.showAgentTechnicalDetails === true;
}

export function showAgentTechnicalDetails(): boolean {
  return areAgentTechnicalDetailsEnabled($currentSettings.get());
}

export function isFileChangeHistoryEntry(entry: AppServerControlHistoryEntry): boolean {
  return entry.kind === 'diff' || entry.toolPresentation?.category === 'edit';
}

export function isHistoryError(entry: AppServerControlHistoryEntry): boolean {
  return (
    /error|failed/i.test(entry.sourceStatus ?? '') ||
    /agent.?error/i.test(entry.sourceItemType ?? '') ||
    entry.toolPresentation?.evidenceKind === 'error' ||
    (entry.toolPresentation?.exitCode != null && entry.toolPresentation.exitCode !== 0) ||
    ((entry.kind === 'notice' || entry.kind === 'system') && entry.tone === 'attention')
  );
}

export function filterReadingHistory(
  entries: readonly AppServerControlHistoryEntry[],
  requests: readonly AppServerControlHistoryRequestSummary[],
  technicalDetails: boolean,
): AppServerControlHistoryEntry[] {
  if (technicalDetails) {
    return [...entries];
  }
  const requestById = new Map(requests.map((request) => [request.requestId, request]));
  return entries.filter((entry) => {
    if (entry.kind === 'request') {
      const request = entry.requestId ? requestById.get(entry.requestId) : undefined;
      return request ? request.state === 'open' : /^(open|pending)$/.test(entry.sourceStatus ?? '');
    }
    if (entry.kind === 'user' || entry.kind === 'assistant' || entry.busyIndicator) {
      return true;
    }
    if (isFileChangeHistoryEntry(entry) || entry.sourceItemType === 'proposed_plan') {
      return true;
    }
    return (
      entry.id === 'midterm-status' ||
      entry.id.startsWith('appServerControl-issue:') ||
      isHistoryError(entry)
    );
  });
}
