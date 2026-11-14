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
  const visible = entries.filter((entry) => {
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
  const visibleIds = new Set(visible.map((entry) => entry.id));
  const result: AppServerControlHistoryEntry[] = [];
  let summary: AppServerControlHistoryEntry | null = null;
  const counted = new Set<string>();
  for (const entry of entries) {
    if (visibleIds.has(entry.id)) {
      result.push(entry);
      if (!entry.busyIndicator) {
        summary = null;
        counted.clear();
      }
      continue;
    }
    if (entry.kind !== 'tool') continue;
    const identity = `${entry.sourceTurnId ?? ''}:${entry.sourceItemId || entry.id}`;
    if (counted.has(identity)) continue;
    counted.add(identity);
    if (!summary) {
      summary = {
        id: `tool-activity:${entry.id}`,
        order: entry.order,
        kind: 'notice',
        tone: 'info',
        label: '',
        title: '',
        body: '',
        meta: '',
        sourceTurnId: entry.sourceTurnId ?? null,
        sourceItemType: 'tool_activity_summary',
        toolActivityCount: 0,
      };
      result.push(summary);
    }
    summary.toolActivityCount = (summary.toolActivityCount ?? 0) + 1;
    summary.body = String(summary.toolActivityCount);
  }
  return result;
}
