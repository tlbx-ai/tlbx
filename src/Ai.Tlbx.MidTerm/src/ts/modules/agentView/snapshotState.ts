import type {
  AppServerControlHistoryDelta,
  AppServerControlHistoryItem,
  AppServerControlHistorySnapshot,
} from '../../api/client';
import type { AppServerControlAttachmentReference } from '../../api/types';
import type { SessionAppServerControlViewState } from './types';

function cloneHistoryAttachments(
  attachments: readonly AppServerControlAttachmentReference[] | undefined,
): AppServerControlAttachmentReference[] {
  return attachments?.map((attachment) => ({ ...attachment })) ?? [];
}

export function applyAppServerControlHistoryWindowState(
  state: SessionAppServerControlViewState,
  snapshot: AppServerControlHistorySnapshot,
): void {
  const windowStart =
    typeof snapshot.historyWindowStart === 'number' ? snapshot.historyWindowStart : 0;
  const windowEnd =
    typeof snapshot.historyWindowEnd === 'number'
      ? snapshot.historyWindowEnd
      : windowStart + snapshot.history.length;
  let windowSize = Math.max(0, windowEnd - windowStart);
  if (windowSize === 0 && snapshot.historyCount > 0 && snapshot.history.length === 0) {
    windowSize = Math.max(1, state.historyWindowTargetCount, state.historyWindowCount);
  }
  state.historyWindowStart = windowStart;
  state.historyWindowCount = windowSize;
  state.historyWindowTargetCount = Math.max(1, state.historyWindowTargetCount || 0, windowSize);
}

export function collapseSnapshotToLatestWindow(
  state: SessionAppServerControlViewState,
  targetWindowCount: number,
): void {
  const snapshot = state.snapshot;
  if (!snapshot) {
    return;
  }

  const retainedEntries =
    snapshot.history.length > targetWindowCount
      ? snapshot.history.slice(-targetWindowCount)
      : snapshot.history.slice();
  const historyCount = Math.max(snapshot.historyCount, retainedEntries.length);

  snapshot.history = retainedEntries;
  snapshot.historyCount = historyCount;
  snapshot.historyWindowEnd = historyCount;
  snapshot.historyWindowStart = Math.max(0, historyCount - retainedEntries.length);
  snapshot.hasOlderHistory = snapshot.historyWindowStart > 0;
  snapshot.hasNewerHistory = false;
  state.historyWindowStart = snapshot.historyWindowStart;
  state.historyWindowCount = retainedEntries.length;
  state.historyWindowTargetCount = Math.max(1, targetWindowCount);
  retainWindowSummaries(snapshot);
}

export function applyCanonicalAppServerControlDelta(
  state: SessionAppServerControlViewState,
  delta: AppServerControlHistoryDelta,
): boolean {
  const snapshot = state.snapshot;
  if (!snapshot) {
    return false;
  }

  const previousHistoryCount = snapshot.historyCount;
  snapshot.provider = delta.provider || snapshot.provider;
  snapshot.generatedAt = delta.generatedAt;
  snapshot.latestSequence = Math.max(snapshot.latestSequence, delta.latestSequence);
  snapshot.historyCount = Math.max(delta.historyCount, snapshot.historyCount);
  snapshot.session = cloneSnapshotSessionSummary(delta.session);
  snapshot.thread = cloneSnapshotThreadSummary(delta.thread);
  snapshot.currentTurn = cloneSnapshotTurnSummary(delta.currentTurn);
  snapshot.quickSettings = cloneSnapshotQuickSettingsSummary(delta.quickSettings);
  snapshot.streams = cloneSnapshotStreamsSummary(delta.streams);
  snapshot.items = upsertSnapshotItems(snapshot.items, delta.itemUpserts, delta.itemRemovals);
  snapshot.requests = upsertSnapshotRequests(
    snapshot.requests,
    delta.requestUpserts,
    delta.requestRemovals,
  );
  snapshot.notices = upsertSnapshotNotices(snapshot.notices, delta.noticeUpserts);
  const requiresWindowRefresh = applyHistoryWindowDelta(
    state,
    snapshot,
    previousHistoryCount,
    delta.historyUpserts,
    delta.historyRemovals,
  );
  retainWindowSummaries(snapshot);
  return requiresWindowRefresh;
}

function retainWindowSummaries(snapshot: AppServerControlHistorySnapshot): void {
  const itemIds = new Set(
    snapshot.history.flatMap((entry) => (entry.itemId ? [entry.itemId] : [])),
  );
  const requestIds = new Set(
    snapshot.history.flatMap((entry) => (entry.requestId ? [entry.requestId] : [])),
  );
  const keepItem = (item: AppServerControlHistorySnapshot['items'][number]) =>
    itemIds.has(item.itemId) ||
    item.aliasItemIds?.some((alias) => itemIds.has(alias)) === true ||
    isCurrentActivityItem(snapshot, item);
  const keepRequest = (request: AppServerControlHistorySnapshot['requests'][number]) =>
    requestIds.has(request.requestId) || request.state.toLowerCase() === 'open';
  // A no-op patch keeps identity. Evict only browser-owned summaries, never canonical host data.
  if (!snapshot.items.every(keepItem)) {
    snapshot.items = snapshot.items.filter(keepItem);
  }
  if (!snapshot.requests.every(keepRequest)) {
    snapshot.requests = snapshot.requests.filter(keepRequest);
  }
}

function isCurrentActivityItem(
  snapshot: AppServerControlHistorySnapshot,
  item: AppServerControlHistorySnapshot['items'][number],
): boolean {
  if (
    ['completed', 'resolved', 'failed', 'stopped', 'error', 'cancelled'].includes(
      snapshot.currentTurn.state.toLowerCase(),
    ) ||
    (item.turnId && snapshot.currentTurn.turnId && item.turnId !== snapshot.currentTurn.turnId)
  ) {
    return false;
  }
  return [
    'active',
    'in progress',
    'inprogress',
    'in_progress',
    'open',
    'running',
    'starting',
    'started',
    'streaming',
  ].includes(item.status.trim().toLowerCase());
}

function applyHistoryWindowDelta(
  state: SessionAppServerControlViewState,
  snapshot: AppServerControlHistorySnapshot,
  previousHistoryCount: number,
  upserts: readonly AppServerControlHistoryItem[],
  removals: readonly string[],
): boolean {
  const currentWindowStart = snapshot.historyWindowStart;
  const currentWindowEnd = snapshot.historyWindowEnd;
  const wasLiveEdge = state.historyAutoScrollPinned && currentWindowEnd >= previousHistoryCount;
  const nextEntries = snapshot.history.map(cloneSnapshotHistoryEntry);
  const entryIndexById = new Map(nextEntries.map((entry, index) => [entry.entryId, index]));
  const requiresWindowRefresh = resolveHistoryWindowRefreshRequirement(
    wasLiveEdge,
    currentWindowStart,
    upserts,
  );

  applyHistoryEntryRemovals(nextEntries, entryIndexById, removals);
  applyHistoryEntryUpserts(
    nextEntries,
    entryIndexById,
    upserts,
    wasLiveEdge,
    currentWindowStart,
    currentWindowEnd,
  );

  nextEntries.sort((left, right) => left.order - right.order);
  const targetWindowCount = Math.max(
    1,
    state.historyWindowTargetCount || 0,
    state.historyWindowCount || 0,
    snapshot.history.length || 1,
  );
  applyHistoryWindowEntries(
    snapshot,
    nextEntries,
    wasLiveEdge,
    currentWindowStart,
    currentWindowEnd,
    targetWindowCount,
  );

  snapshot.hasOlderHistory = snapshot.historyWindowStart > 0;
  snapshot.hasNewerHistory = snapshot.historyWindowEnd < snapshot.historyCount;
  state.historyWindowStart = snapshot.historyWindowStart;
  state.historyWindowCount = snapshot.history.length;
  return requiresWindowRefresh;
}

function resolveHistoryWindowRefreshRequirement(
  wasLiveEdge: boolean,
  currentWindowStart: number,
  upserts: readonly AppServerControlHistoryItem[],
): boolean {
  if (wasLiveEdge) {
    return false;
  }

  const hasEarlierOffWindowUpsert = upserts.some((upsert) => {
    const absoluteIndex = Math.max(0, upsert.order - 1);
    return absoluteIndex < currentWindowStart;
  });
  // An unknown removal is normally a provisional live-tail entry that was canonicalized outside
  // the retained browse window. Refreshing the reader's window for it causes the viewport to jump.
  // Concrete removals inside the retained window are applied above; off-window changes reconcile
  // naturally on the next explicit kernel-edge fetch.
  return hasEarlierOffWindowUpsert;
}

function applyHistoryEntryRemovals(
  entries: AppServerControlHistoryItem[],
  entryIndexById: Map<string, number>,
  removals: readonly string[],
): void {
  for (const entryId of removals) {
    const index = entryIndexById.get(entryId);
    if (index === undefined) {
      continue;
    }

    entries.splice(index, 1);
    entryIndexById.delete(entryId);
    reindexHistoryEntryMap(entryIndexById, entries);
  }
}

function applyHistoryEntryUpserts(
  entries: AppServerControlHistoryItem[],
  entryIndexById: Map<string, number>,
  upserts: readonly AppServerControlHistoryItem[],
  wasLiveEdge: boolean,
  currentWindowStart: number,
  currentWindowEnd: number,
): void {
  for (const upsert of upserts) {
    const cloned = cloneSnapshotHistoryEntry(upsert);
    const existingIndex = entryIndexById.get(cloned.entryId);
    if (existingIndex !== undefined) {
      entries.splice(existingIndex, 1, cloned);
      continue;
    }

    if (wasLiveEdge) {
      entries.push(cloned);
      entryIndexById.set(cloned.entryId, entries.length - 1);
      continue;
    }

    const absoluteIndex = Math.max(0, cloned.order - 1);
    if (absoluteIndex >= currentWindowStart && absoluteIndex < currentWindowEnd) {
      entries.push(cloned);
      entryIndexById.set(cloned.entryId, entries.length - 1);
    }
  }
}

function applyHistoryWindowEntries(
  snapshot: AppServerControlHistorySnapshot,
  entries: readonly AppServerControlHistoryItem[],
  wasLiveEdge: boolean,
  currentWindowStart: number,
  currentWindowEnd: number,
  targetWindowCount: number,
): void {
  if (wasLiveEdge) {
    applyLiveEdgeHistoryWindow(snapshot, entries, targetWindowCount);
    return;
  }

  snapshot.history = entries.filter((entry) => {
    const absoluteIndex = Math.max(0, entry.order - 1);
    return absoluteIndex >= currentWindowStart && absoluteIndex < currentWindowEnd;
  });
  snapshot.historyWindowStart = currentWindowStart;
  snapshot.historyWindowEnd = snapshot.historyWindowStart + snapshot.history.length;
}

function applyLiveEdgeHistoryWindow(
  snapshot: AppServerControlHistorySnapshot,
  entries: readonly AppServerControlHistoryItem[],
  targetWindowCount: number,
): void {
  const trimmedEntries =
    entries.length > targetWindowCount ? entries.slice(-targetWindowCount) : entries.slice();
  snapshot.history = trimmedEntries;
  snapshot.historyWindowEnd = snapshot.historyCount;
  snapshot.historyWindowStart = Math.max(0, snapshot.historyCount - trimmedEntries.length);
}

function reindexHistoryEntryMap(
  entryIndexById: Map<string, number>,
  entries: readonly AppServerControlHistoryItem[],
): void {
  entryIndexById.clear();
  entries.forEach((entry, index) => {
    entryIndexById.set(entry.entryId, index);
  });
}

function upsertSnapshotItems(
  current: AppServerControlHistorySnapshot['items'],
  upserts: readonly AppServerControlHistorySnapshot['items'][number][],
  removals: readonly string[],
): AppServerControlHistorySnapshot['items'] {
  if (upserts.length === 0 && removals.length === 0) {
    return current;
  }

  const next = new Map(current.map((item) => [item.itemId, cloneSnapshotItemSummary(item)]));

  for (const itemId of removals) {
    next.delete(itemId);
  }

  for (const item of upserts) {
    next.set(item.itemId, cloneSnapshotItemSummary(item));
  }

  return Array.from(next.values()).sort(
    (left, right) => new Date(right.updatedAt).getTime() - new Date(left.updatedAt).getTime(),
  );
}

function upsertSnapshotRequests(
  current: AppServerControlHistorySnapshot['requests'],
  upserts: readonly AppServerControlHistorySnapshot['requests'][number][],
  removals: readonly string[],
): AppServerControlHistorySnapshot['requests'] {
  if (upserts.length === 0 && removals.length === 0) {
    return current;
  }

  const next = new Map(
    current.map((request) => [request.requestId, cloneSnapshotRequestSummary(request)]),
  );

  for (const requestId of removals) {
    next.delete(requestId);
  }

  for (const request of upserts) {
    next.set(request.requestId, cloneSnapshotRequestSummary(request));
  }

  return Array.from(next.values()).sort(
    (left, right) => new Date(right.updatedAt).getTime() - new Date(left.updatedAt).getTime(),
  );
}

function upsertSnapshotNotices(
  current: AppServerControlHistorySnapshot['notices'],
  upserts: readonly AppServerControlHistorySnapshot['notices'][number][],
): AppServerControlHistorySnapshot['notices'] {
  if (upserts.length === 0) {
    return current;
  }

  const next = new Map(
    current.map((notice) => [notice.eventId, cloneSnapshotRuntimeNotice(notice)]),
  );

  for (const notice of upserts) {
    next.set(notice.eventId, cloneSnapshotRuntimeNotice(notice));
  }

  return Array.from(next.values())
    .sort((left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime())
    .slice(0, 64);
}

function cloneSnapshotHistoryEntry(
  entry: AppServerControlHistoryItem,
): AppServerControlHistoryItem {
  return {
    ...entry,
    toolPresentation: entry.toolPresentation
      ? { ...entry.toolPresentation, paths: [...entry.toolPresentation.paths] }
      : null,
    attachments: cloneHistoryAttachments(entry.attachments),
    fileMentions: (entry.fileMentions ?? []).map((mention) => ({ ...mention })),
    imagePreviews: (entry.imagePreviews ?? []).map((preview) => ({ ...preview })),
  };
}

function cloneSnapshotItemSummary(
  item: AppServerControlHistorySnapshot['items'][number],
): AppServerControlHistorySnapshot['items'][number] {
  return {
    ...item,
    turnId: item.turnId ?? null,
    title: item.title ?? null,
    detail: item.detail ?? null,
    attachments: cloneHistoryAttachments(item.attachments),
  };
}

function cloneSnapshotRequestSummary(
  request: AppServerControlHistorySnapshot['requests'][number],
): AppServerControlHistorySnapshot['requests'][number] {
  return {
    ...request,
    turnId: request.turnId ?? null,
    detail: request.detail ?? null,
    decision: request.decision ?? null,
    questions: request.questions.map((question) => ({
      ...question,
      options: question.options.map((option) => ({ ...option })),
    })),
    answers: request.answers.map((answer) => ({
      questionId: answer.questionId,
      answers: [...answer.answers],
    })),
  };
}

function cloneSnapshotRuntimeNotice(
  notice: AppServerControlHistorySnapshot['notices'][number],
): AppServerControlHistorySnapshot['notices'][number] {
  return {
    ...notice,
    detail: notice.detail ?? null,
  };
}

function cloneSnapshotSessionSummary(
  session: AppServerControlHistorySnapshot['session'],
): AppServerControlHistorySnapshot['session'] {
  return {
    ...session,
    reason: session.reason ?? null,
    lastError: session.lastError ?? null,
    lastEventAt: session.lastEventAt ?? null,
  };
}

function cloneSnapshotThreadSummary(
  thread: AppServerControlHistorySnapshot['thread'],
): AppServerControlHistorySnapshot['thread'] {
  return structuredClone(thread);
}

function cloneSnapshotTurnSummary(
  turn: AppServerControlHistorySnapshot['currentTurn'],
): AppServerControlHistorySnapshot['currentTurn'] {
  return {
    ...turn,
    turnId: turn.turnId ?? null,
    model: turn.model ?? null,
    effort: turn.effort ?? null,
    startedAt: turn.startedAt ?? null,
    completedAt: turn.completedAt ?? null,
  };
}

function cloneSnapshotQuickSettingsSummary(
  quickSettings: AppServerControlHistorySnapshot['quickSettings'] | null | undefined,
): AppServerControlHistorySnapshot['quickSettings'] {
  return {
    ...structuredClone(quickSettings),
    model: quickSettings?.model ?? null,
    effort: quickSettings?.effort ?? null,
    planMode: quickSettings?.planMode ?? 'off',
    permissionMode: quickSettings?.permissionMode ?? 'manual',
    fastMode: quickSettings?.fastMode ?? 'off',
  };
}

function cloneSnapshotStreamsSummary(
  streams: AppServerControlHistorySnapshot['streams'],
): AppServerControlHistorySnapshot['streams'] {
  return {
    ...streams,
  };
}
