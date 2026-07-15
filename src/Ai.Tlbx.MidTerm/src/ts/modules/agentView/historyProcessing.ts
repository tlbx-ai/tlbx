import { t } from '../i18n';
import type {
  AppServerControlHistoryRequestSummary,
  AppServerControlHistoryRuntimeNotice,
  AppServerControlHistorySnapshot,
  AppServerControlHistoryItem,
} from '../../api/client';
import type {
  AppServerControlAttachmentReference,
  AppServerControlInlineFileReference,
  AppServerControlInlineImagePreview,
} from '../../api/types';
import { $currentSettings } from '../../stores';
import {
  formatAbsoluteTime,
  historyLabel,
  normalizeSnapshotHistoryKind,
  prettify,
  toneFromState,
} from './activationHelpers';
import {
  extractCommandOutputTail,
  hasInlineCommandPresentation,
  isCommandExecutionHistoryEntry,
  isCommandOutputHistoryEntry,
  normalizeHistoryItemType,
  parseCommandOutputBody,
} from './historyContent';
import type {
  HistoryKind,
  AppServerControlActivationIssue,
  AppServerControlHistoryEntry,
  AppServerControlRuntimeStatsSummary,
  PendingAppServerControlTurn,
  SessionAppServerControlViewState,
} from './types';

function appServerControlText(key: string, fallback: string): string {
  const translated = t(key);
  return !translated || translated === key ? fallback : translated;
}

function normalizeComparableHistoryText(value: string | null | undefined): string {
  return (value ?? '').trim().replace(/\s+/g, ' ').toLowerCase();
}

const BUSY_INDICATOR_ITEM_STATUSES = [
  'active',
  'in progress',
  'inprogress',
  'in_progress',
  'open',
  'running',
  'starting',
] as const;

const BUSY_INDICATOR_EXCLUDED_ITEM_TYPES = new Set([
  'assistant_message',
  'assistant_text',
  'assistantmessage',
  'assistanttext',
  'command',
  'command_call',
  'command_execution',
  'command_output',
  'commandcall',
  'commandexecution',
  'file_change_output',
  'file_change',
  'filechange',
  'interview',
  'request',
  'user_message',
  'usermessage',
]);

const COMMAND_HISTORY_ITEM_TYPES = new Set([
  'command',
  'commandcall',
  'commandexecution',
  'commandoutput',
  'commandrun',
]);

export function cloneHistoryAttachments(
  attachments: readonly AppServerControlAttachmentReference[] | undefined,
): AppServerControlAttachmentReference[] {
  return attachments?.map((attachment) => ({ ...attachment })) ?? [];
}

function cloneFileMentions(
  fileMentions: readonly AppServerControlInlineFileReference[] | undefined,
): AppServerControlInlineFileReference[] {
  return fileMentions?.map((mention) => ({ ...mention })) ?? [];
}

function cloneImagePreviews(
  imagePreviews: readonly AppServerControlInlineImagePreview[] | undefined,
): AppServerControlInlineImagePreview[] {
  return imagePreviews?.map((preview) => ({ ...preview })) ?? [];
}

export function buildAppServerControlHistoryEntries(
  snapshot: AppServerControlHistorySnapshot,
): AppServerControlHistoryEntry[] {
  const historyEntries = Array.isArray(snapshot.history) ? snapshot.history : [];
  if (historyEntries.length === 0) {
    return [];
  }

  return historyEntries
    .map((entry) => {
      const kind = normalizeSnapshotHistoryKind(entry.kind);
      const statusLabel = entry.streaming
        ? appServerControlText('appServerControl.status.streaming', 'Streaming')
        : prettify(entry.status || kind);
      const mapped: AppServerControlHistoryEntry = {
        id: entry.entryId,
        order: entry.order,
        kind,
        tone: toneFromState(entry.status),
        label: resolveHistoryEntryLabel(kind, entry.itemType),
        title: entry.title || '',
        body: entry.body || '',
        commandText: entry.commandText ?? null,
        meta:
          kind === 'diff' || isCommandExecutionSnapshotEntry(entry)
            ? ''
            : formatHistoryMeta(kind, statusLabel, entry.updatedAt),
        attachments: cloneHistoryAttachments(entry.attachments),
        fileMentions: cloneFileMentions(entry.fileMentions),
        imagePreviews: cloneImagePreviews(entry.imagePreviews),
        live: entry.streaming,
        sourceItemId: entry.itemId ?? null,
        sourceTurnId: entry.turnId ?? null,
        sourceItemType: entry.itemType ?? null,
      };
      if (typeof entry.estimatedHeightPx === 'number' && entry.estimatedHeightPx > 0) {
        mapped.estimatedHeightPx = entry.estimatedHeightPx;
      }
      if (entry.requestId) {
        mapped.requestId = entry.requestId;
      }
      applyDirectCommandPresentation(mapped);
      if (hasInlineCommandPresentation(mapped)) {
        mapped.meta = '';
      }
      return mapped;
    })
    .filter((entry) => shouldShowUnknownAgentMessages() || !isUnknownAgentFallbackEntry(entry))
    .filter((entry) => !isSuppressedAppServerControlRuntimeNoticeEntry(entry))
    .sort((left, right) => left.order - right.order)
    .reduce<AppServerControlHistoryEntry[]>(mergeCommandOutputHistoryEntries, [])
    .filter(
      (entry) =>
        entry.body.trim() ||
        (entry.commandText?.trim() ?? '').length > 0 ||
        (entry.attachments?.length ?? 0) > 0 ||
        entry.kind === 'request' ||
        entry.kind === 'system' ||
        entry.kind === 'notice',
    );
}

function resolveHistoryEntryLabel(kind: HistoryKind, itemType: string | null | undefined): string {
  switch (normalizeHistoryItemType(itemType)) {
    case 'agentstate':
      return appServerControlText('appServerControl.label.agentState', 'Agent State');
    case 'agenterror':
      return appServerControlText('appServerControl.label.agentError', 'Agent Error');
    default:
      return historyLabel(kind);
  }
}

export function preservePersistentCommandEntries(
  entries: readonly AppServerControlHistoryEntry[],
  previousEntries: readonly AppServerControlHistoryEntry[],
  snapshot:
    | Pick<AppServerControlHistorySnapshot, 'historyWindowEnd' | 'historyWindowStart'>
    | null
    | undefined,
): AppServerControlHistoryEntry[] {
  if (entries.length === 0 && previousEntries.length === 0) {
    return [];
  }

  const rememberedEntries = previousEntries.filter(isPersistentCommandEntry);
  if (rememberedEntries.length === 0) {
    return [...entries];
  }

  const rememberedByKey = new Map<string, AppServerControlHistoryEntry>();
  for (const entry of rememberedEntries) {
    for (const key of buildPersistentCommandKeys(entry)) {
      if (!rememberedByKey.has(key)) {
        rememberedByKey.set(key, entry);
      }
    }
  }

  const seenKeys = new Set<string>();
  const nextEntries = entries.map((entry) => {
    const remembered = resolveRememberedCommandEntry(entry, rememberedByKey);
    const merged = mergePersistentCommandEntry(entry, remembered);
    for (const key of buildPersistentCommandKeys(merged)) {
      seenKeys.add(key);
    }
    return merged;
  });

  for (const remembered of rememberedEntries) {
    const keys = buildPersistentCommandKeys(remembered);
    if (keys.some((key) => seenKeys.has(key))) {
      continue;
    }

    if (!shouldRetainMissingCommandEntry(remembered, snapshot)) {
      continue;
    }

    nextEntries.push(cloneAppServerControlHistoryEntry(remembered));
    for (const key of keys) {
      seenKeys.add(key);
    }
  }

  return nextEntries.sort(
    (left, right) => left.order - right.order || left.id.localeCompare(right.id),
  );
}

function isCommandExecutionSnapshotEntry(
  entry: Pick<AppServerControlHistoryItem, 'kind' | 'itemType'>,
): boolean {
  return (
    normalizeSnapshotHistoryKind(entry.kind) === 'tool' &&
    normalizeComparableHistoryText(entry.itemType ?? '').replace(/[_-]+/g, ' ') ===
      'command execution'
  );
}

function applyDirectCommandPresentation(entry: AppServerControlHistoryEntry): void {
  const commandPresentation = resolveCommandPresentation(entry);
  if (!commandPresentation) {
    return;
  }

  entry.commandText = commandPresentation.commandText;
  entry.commandOutputTail = commandPresentation.commandOutputTail;
  entry.body = '';
}

function resolveCommandPresentation(
  entry: Pick<
    AppServerControlHistoryEntry,
    'body' | 'commandOutputTail' | 'commandText' | 'sourceItemType'
  >,
): { commandText: string; commandOutputTail: string[] } | null {
  const normalizedType = normalizeHistoryItemType(entry.sourceItemType);
  if (normalizedType === 'commandexecution') {
    const commandText = (entry.commandText ?? entry.body).trim();
    return commandText ? { commandText, commandOutputTail: entry.commandOutputTail ?? [] } : null;
  }

  if (normalizedType !== 'commandoutput') {
    return null;
  }

  if ((entry.commandText?.trim() ?? '').length > 0) {
    const commandText = entry.commandText?.trim() ?? '';
    const parsedBody = parseCommandOutputBody(entry.body);
    return {
      commandText,
      commandOutputTail:
        entry.commandOutputTail ??
        (parsedBody &&
        normalizeComparableHistoryText(parsedBody.commandText) ===
          normalizeComparableHistoryText(commandText)
          ? parsedBody.commandOutputTail
          : extractCommandOutputTail(entry.body)),
    };
  }

  return parseCommandOutputBody(entry.body);
}

function isPersistentCommandEntry(entry: AppServerControlHistoryEntry): boolean {
  if (entry.kind !== 'tool') {
    return false;
  }

  if (hasInlineCommandPresentation(entry)) {
    return true;
  }

  return COMMAND_HISTORY_ITEM_TYPES.has(normalizeHistoryItemType(entry.sourceItemType));
}

function shouldShowUnknownAgentMessages(): boolean {
  return $currentSettings.get()?.showUnknownAgentMessages !== false;
}

function isUnknownAgentFallbackEntry(
  entry: Pick<AppServerControlHistoryEntry, 'sourceItemType'>,
): boolean {
  return normalizeHistoryItemType(entry.sourceItemType) === 'unknownagentmessage';
}

function buildPersistentCommandKeys(entry: AppServerControlHistoryEntry): string[] {
  if (!isPersistentCommandEntry(entry)) {
    return [];
  }

  return buildCommandLookupKeys(entry);
}

function buildCommandLookupKeys(entry: AppServerControlHistoryEntry): string[] {
  const keys = new Set<string>();
  const commandText = (entry.commandText ?? '').trim();
  const normalizedCommandText = normalizeComparableHistoryText(commandText);
  if (entry.id.trim()) {
    keys.add(`id:${entry.id}`);
  }
  if ((entry.sourceItemId ?? '').trim()) {
    keys.add(`item:${entry.sourceItemId}`);
  }
  if ((entry.sourceTurnId ?? '').trim() && normalizedCommandText) {
    keys.add(`turncmd:${entry.sourceTurnId}:${normalizedCommandText}`);
  }
  if (normalizedCommandText) {
    keys.add(`ordercmd:${Math.floor(entry.order)}:${normalizedCommandText}`);
  }
  return [...keys];
}

function resolveRememberedCommandEntry(
  entry: AppServerControlHistoryEntry,
  rememberedByKey: ReadonlyMap<string, AppServerControlHistoryEntry>,
): AppServerControlHistoryEntry | null {
  if (entry.kind !== 'tool') {
    return null;
  }

  for (const key of buildCommandLookupKeys(entry)) {
    const remembered = rememberedByKey.get(key);
    if (remembered) {
      return remembered;
    }
  }

  return null;
}

function mergePersistentCommandEntry(
  entry: AppServerControlHistoryEntry,
  remembered: AppServerControlHistoryEntry | null,
): AppServerControlHistoryEntry {
  if (!remembered || entry.kind !== 'tool') {
    return entry;
  }

  const rememberedCommandText = (remembered.commandText ?? '').trim();
  const nextCommandText = (entry.commandText ?? '').trim() || rememberedCommandText;
  const nextCommandOutputTail = resolveMergedCommandOutputTail(entry, remembered);
  const shouldForceCommandPresentation = shouldForcePersistentCommandPresentation(
    entry,
    remembered,
    nextCommandText,
    nextCommandOutputTail,
  );

  if (!shouldForceCommandPresentation) {
    return entry;
  }

  return {
    ...entry,
    body: '',
    meta: '',
    commandText: nextCommandText || null,
    commandOutputTail: nextCommandOutputTail,
  };
}

function resolveMergedCommandOutputTail(
  entry: AppServerControlHistoryEntry,
  remembered: AppServerControlHistoryEntry,
): string[] {
  return (entry.commandOutputTail?.length ?? 0) > 0
    ? [...(entry.commandOutputTail ?? [])]
    : [...(remembered.commandOutputTail ?? [])];
}

function shouldForcePersistentCommandPresentation(
  entry: AppServerControlHistoryEntry,
  remembered: AppServerControlHistoryEntry,
  nextCommandText: string,
  nextCommandOutputTail: readonly string[],
): boolean {
  if (nextCommandText.length > 0 || nextCommandOutputTail.length > 0) {
    return true;
  }

  return isPersistentCommandEntry(entry) && isPersistentCommandEntry(remembered);
}

function shouldRetainMissingCommandEntry(
  entry: AppServerControlHistoryEntry,
  snapshot:
    | Pick<AppServerControlHistorySnapshot, 'historyWindowEnd' | 'historyWindowStart'>
    | null
    | undefined,
): boolean {
  if (!snapshot) {
    return true;
  }

  const absoluteIndex = Math.max(0, Math.floor(entry.order) - 1);
  return absoluteIndex >= snapshot.historyWindowStart && absoluteIndex < snapshot.historyWindowEnd;
}

function cloneAppServerControlHistoryEntry(
  entry: AppServerControlHistoryEntry,
): AppServerControlHistoryEntry {
  const cloned: AppServerControlHistoryEntry = {
    ...entry,
    attachments: cloneHistoryAttachments(entry.attachments),
    commandOutputTail: [...(entry.commandOutputTail ?? [])],
    fileMentions: cloneFileMentions(entry.fileMentions),
    imagePreviews: cloneImagePreviews(entry.imagePreviews),
  };
  if (entry.actions) {
    cloned.actions = entry.actions.map((action) => ({ ...action }));
  }
  return cloned;
}

function findMatchingCommandExecutionIndex(
  mergedEntries: readonly AppServerControlHistoryEntry[],
  entry: AppServerControlHistoryEntry,
): number {
  for (let index = mergedEntries.length - 1; index >= 0; index -= 1) {
    const candidate = mergedEntries[index];
    if (!candidate || !isCommandExecutionHistoryEntry(candidate)) {
      continue;
    }

    const sameSourceItem =
      candidate.sourceItemId && entry.sourceItemId && candidate.sourceItemId === entry.sourceItemId;
    if (sameSourceItem || candidate.id === entry.id) {
      return index;
    }
  }

  const previousEntry = mergedEntries[mergedEntries.length - 1];
  return previousEntry && isCommandExecutionHistoryEntry(previousEntry)
    ? mergedEntries.length - 1
    : -1;
}

function mergeCommandOutputHistoryEntries(
  mergedEntries: AppServerControlHistoryEntry[],
  entry: AppServerControlHistoryEntry,
): AppServerControlHistoryEntry[] {
  if (!isCommandOutputHistoryEntry(entry)) {
    mergedEntries.push(entry);
    return mergedEntries;
  }

  const targetIndex = findMatchingCommandExecutionIndex(mergedEntries, entry);
  if (targetIndex < 0) {
    mergedEntries.push(entry);
    return mergedEntries;
  }

  const targetEntry = mergedEntries[targetIndex];
  const commandPresentation = resolveCommandPresentation(entry);
  if (!targetEntry || !commandPresentation) {
    return mergedEntries;
  }

  mergedEntries[targetIndex] = {
    ...targetEntry,
    body: '',
    commandText: targetEntry.commandText ?? commandPresentation.commandText,
    commandOutputTail: commandPresentation.commandOutputTail,
  };
  return mergedEntries;
}

export function isSuppressedAppServerControlRuntimeNoticeEntry(
  entry: Pick<AppServerControlHistoryEntry, 'kind' | 'title' | 'body'>,
): boolean {
  if (!['system', 'notice'].includes(normalizeSnapshotHistoryKind(entry.kind))) {
    return false;
  }

  const title = normalizeComparableHistoryText(entry.title);
  const body = normalizeComparableHistoryText(entry.body);
  const contextMarker = normalizeComparableHistoryText('Codex context window updated.');
  const rateLimitMarker = normalizeComparableHistoryText('Codex rate limits updated.');
  const skillsChangedMarker = normalizeComparableHistoryText('Codex skills changed.');
  return (
    title === contextMarker ||
    body === contextMarker ||
    title === rateLimitMarker ||
    body === rateLimitMarker ||
    title === skillsChangedMarker ||
    body === skillsChangedMarker ||
    body === `${skillsChangedMarker} {}` ||
    body.startsWith(normalizeComparableHistoryText('Codex remote-control status:')) ||
    isAppServerControlProviderLifecycleNotice(body) ||
    body.includes(normalizeComparableHistoryText('last turn in/out')) ||
    body.includes('"ratelimits"') ||
    body.includes('"usedpercent"')
  );
}

function isAppServerControlProviderLifecycleNotice(normalizedBody: string): boolean {
  return /^[a-z][a-z0-9_.-]* (?:starting|ready)\.$/i.test(normalizedBody);
}

export function buildAppServerControlRuntimeStats(
  snapshot: AppServerControlHistorySnapshot,
): AppServerControlRuntimeStatsSummary | null {
  const stats: AppServerControlRuntimeStatsSummary = {
    windowUsedTokens: null,
    windowTokenLimit: null,
    accumulatedInputTokens: 0,
    accumulatedOutputTokens: 0,
    primaryRateLimitUsedPercent: null,
    secondaryRateLimitUsedPercent: null,
  };
  let hasStats = false;
  const sources =
    snapshot.notices.length > 0
      ? snapshot.notices
      : snapshot.history
          .filter((entry) =>
            isSuppressedAppServerControlRuntimeNoticeEntry({
              kind: normalizeSnapshotHistoryKind(entry.kind),
              title: entry.title ?? '',
              body: entry.body,
            }),
          )
          .map<AppServerControlHistoryRuntimeNotice>((entry) => ({
            eventId: entry.entryId,
            type: normalizeSnapshotHistoryKind(entry.kind),
            message: entry.title ?? '',
            detail: entry.body,
            createdAt: entry.updatedAt,
          }));

  for (const notice of sources) {
    const contextWindow = parseCodexContextWindowNotice(notice);
    if (contextWindow) {
      stats.windowUsedTokens = contextWindow.usedTokens;
      stats.windowTokenLimit = contextWindow.windowTokens;
      stats.accumulatedInputTokens += contextWindow.lastTurnInputTokens;
      stats.accumulatedOutputTokens += contextWindow.lastTurnOutputTokens;
      hasStats = true;
      continue;
    }

    const rateLimits = parseCodexRateLimitNotice(notice);
    if (rateLimits) {
      stats.primaryRateLimitUsedPercent = rateLimits.primaryUsedPercent;
      stats.secondaryRateLimitUsedPercent = rateLimits.secondaryUsedPercent;
      hasStats = true;
    }
  }

  return hasStats ? stats : null;
}

function parseCodexContextWindowNotice(
  notice: Pick<AppServerControlHistoryRuntimeNotice, 'message' | 'detail'>,
): {
  usedTokens: number | null;
  windowTokens: number;
  lastTurnInputTokens: number;
  lastTurnOutputTokens: number;
} | null {
  const message = normalizeComparableHistoryText(notice.message);
  const detail = notice.detail ?? '';
  if (
    message !== normalizeComparableHistoryText('Codex context window updated.') &&
    !normalizeComparableHistoryText(detail).includes(
      normalizeComparableHistoryText('last turn in/out'),
    )
  ) {
    return null;
  }

  const contextMatch = detail.match(
    /Used\s+(\d+)\s+tokens,\s+window\s+(\d+),\s+last turn in\/out\s+(\d+)\/(\d+)/i,
  );
  if (contextMatch) {
    const [, usedTokensText, windowTokensText, inputTokensText, outputTokensText] = contextMatch;
    if (!usedTokensText || !windowTokensText || !inputTokensText || !outputTokensText) {
      return null;
    }

    const usedTokens = Number.parseInt(usedTokensText, 10);
    const windowTokens = Number.parseInt(windowTokensText, 10);
    return {
      usedTokens: usedTokens <= windowTokens ? usedTokens : null,
      windowTokens,
      lastTurnInputTokens: Number.parseInt(inputTokensText, 10),
      lastTurnOutputTokens: Number.parseInt(outputTokensText, 10),
    };
  }

  const totalOnlyMatch = detail.match(
    /(?:Session\s+total|Total)\s+(\d+)\s+tokens,\s+window\s+(\d+),\s+last turn in\/out\s+(\d+)\/(\d+)/i,
  );
  if (!totalOnlyMatch) {
    return null;
  }

  const [, , windowTokensText, inputTokensText, outputTokensText] = totalOnlyMatch;
  if (!windowTokensText || !inputTokensText || !outputTokensText) {
    return null;
  }

  return {
    usedTokens: null,
    windowTokens: Number.parseInt(windowTokensText, 10),
    lastTurnInputTokens: Number.parseInt(inputTokensText, 10),
    lastTurnOutputTokens: Number.parseInt(outputTokensText, 10),
  };
}

function parseCodexRateLimitNotice(
  notice: Pick<AppServerControlHistoryRuntimeNotice, 'message' | 'detail'>,
): { primaryUsedPercent: number | null; secondaryUsedPercent: number | null } | null {
  if (
    normalizeComparableHistoryText(notice.message) !==
      normalizeComparableHistoryText('Codex rate limits updated.') ||
    !notice.detail
  ) {
    return null;
  }

  try {
    const parsed = JSON.parse(notice.detail) as {
      rateLimits?: {
        primary?: { usedPercent?: number | null };
        secondary?: { usedPercent?: number | null };
      };
    };
    return {
      primaryUsedPercent:
        typeof parsed.rateLimits?.primary?.usedPercent === 'number'
          ? parsed.rateLimits.primary.usedPercent
          : null,
      secondaryUsedPercent:
        typeof parsed.rateLimits?.secondary?.usedPercent === 'number'
          ? parsed.rateLimits.secondary.usedPercent
          : null,
    };
  } catch {
    return null;
  }
}

export function applyOptimisticAppServerControlTurns(
  snapshot: AppServerControlHistorySnapshot,
  entries: readonly AppServerControlHistoryEntry[],
  optimisticTurns: readonly PendingAppServerControlTurn[],
): { entries: AppServerControlHistoryEntry[]; optimisticTurns: PendingAppServerControlTurn[] } {
  if (optimisticTurns.length === 0) {
    return { entries: [...entries], optimisticTurns: [] };
  }

  const optimisticEntries = [...entries];
  const remainingTurns: PendingAppServerControlTurn[] = [];
  let nextOrder =
    optimisticEntries.reduce((maxOrder, entry) => Math.max(maxOrder, entry.order), 0) + 1;

  for (const turn of optimisticTurns) {
    const userCommitted =
      turn.turnId !== null && optimisticEntries.some((entry) => entry.id === `user:${turn.turnId}`);
    const assistantCommitted =
      (turn.turnId !== null &&
        optimisticEntries.some((entry) => entry.id === `assistant:${turn.turnId}`)) ||
      (turn.turnId !== null &&
        snapshot.currentTurn.turnId === turn.turnId &&
        Boolean(snapshot.streams.assistantText.trim()));

    if (!userCommitted) {
      optimisticEntries.push({
        id: `optimistic-user:${turn.optimisticId}`,
        order: nextOrder++,
        kind: 'user',
        tone: 'info',
        label: historyLabel('user'),
        title: '',
        body: turn.text,
        meta: formatHistoryMeta(
          'user',
          turn.status === 'submitted' ? 'Sending' : 'Sent',
          turn.submittedAt,
        ),
        attachments: cloneHistoryAttachments(turn.attachments),
        pending: turn.status === 'submitted',
      });
    }

    if (!assistantCommitted) {
      optimisticEntries.push({
        id: `optimistic-assistant:${turn.optimisticId}`,
        order: nextOrder++,
        kind: 'assistant',
        tone: 'info',
        label: historyLabel('assistant'),
        title: '',
        body: turn.status === 'submitted' ? 'Starting…' : 'Thinking…',
        meta: formatHistoryMeta(
          'assistant',
          turn.status === 'submitted' ? 'Starting' : 'Running',
          turn.submittedAt,
        ),
        live: true,
        pending: turn.status === 'submitted',
      });
    }

    if (!userCommitted || !assistantCommitted) {
      remainingTurns.push(turn);
    }
  }

  return {
    entries: optimisticEntries.sort((left, right) => left.order - right.order),
    optimisticTurns: remainingTurns,
  };
}

export function withInlineAppServerControlStatus(
  snapshot: AppServerControlHistorySnapshot,
  entries: AppServerControlHistoryEntry[],
  streamConnected: boolean,
): AppServerControlHistoryEntry[] {
  const hasConversation = entries.some((entry) =>
    ['user', 'assistant', 'tool', 'request', 'plan', 'diff'].includes(entry.kind),
  );
  const statusBody =
    snapshot.session.lastError?.trim() ||
    snapshot.session.reason?.trim() ||
    (streamConnected
      ? appServerControlText(
          'appServerControl.status.connectedWaiting',
          'AppServerControl is connected to tlbx and waiting for history content.',
        )
      : appServerControlText(
          'appServerControl.status.reconnecting',
          'AppServerControl is reconnecting to tlbx.',
        ));

  if ((!statusBody || hasConversation) && !snapshot.session.lastError) {
    return entries;
  }

  return [
    {
      id: 'midterm-status',
      order: Number.MIN_SAFE_INTEGER,
      kind: snapshot.session.lastError ? 'notice' : 'system',
      tone: snapshot.session.lastError ? 'attention' : streamConnected ? 'positive' : 'warning',
      label: appServerControlText('appServerControl.label.midterm', 'tlbx'),
      title: '',
      body: statusBody,
      meta: streamConnected
        ? ''
        : appServerControlText('appServerControl.status.connecting', 'Connecting'),
    },
    ...entries,
  ];
}

export function withLiveAssistantState(
  snapshot: AppServerControlHistorySnapshot,
  entries: AppServerControlHistoryEntry[],
): AppServerControlHistoryEntry[] {
  if (snapshot.currentTurn.state !== 'running' && snapshot.currentTurn.state !== 'in_progress') {
    return entries;
  }

  const activeTurnId = snapshot.currentTurn.turnId?.trim() ?? '';
  if (!activeTurnId) {
    return entries;
  }

  for (let index = entries.length - 1; index >= 0; index -= 1) {
    const entry = entries[index];
    if (!entry || entry.kind !== 'assistant' || entry.sourceTurnId !== activeTurnId) {
      continue;
    }

    return entries.map((candidate, candidateIndex) =>
      candidate.kind === 'assistant'
        ? candidateIndex === index
          ? { ...candidate, live: true }
          : candidate.live
            ? { ...candidate, live: false }
            : candidate
        : candidate,
    );
  }

  return entries;
}

export function withTrailingBusyIndicator(
  snapshot: AppServerControlHistorySnapshot,
  entries: AppServerControlHistoryEntry[],
  requests: readonly AppServerControlHistoryRequestSummary[],
): AppServerControlHistoryEntry[] {
  const currentTurnState = (snapshot.currentTurn.state || '').toLowerCase();
  const sessionState = (snapshot.session.state || '').toLowerCase();
  if (
    requests.some((request) => request.state === 'open') ||
    !(
      currentTurnState === 'running' ||
      currentTurnState === 'in_progress' ||
      (currentTurnState.length === 0 && (sessionState === 'starting' || sessionState === 'running'))
    )
  ) {
    return entries.filter((entry) => !entry.busyIndicator);
  }

  const nextEntries = entries.filter((entry) => !entry.busyIndicator);
  const lastOrder = nextEntries.reduce((maxOrder, entry) => Math.max(maxOrder, entry.order), 0);
  nextEntries.push({
    id: `busy-indicator:${snapshot.currentTurn.turnId ?? snapshot.session.lastEventAt ?? 'current'}`,
    order: lastOrder + 1,
    kind: 'assistant',
    tone: 'info',
    label: historyLabel('assistant'),
    title: '',
    body: resolveBusyIndicatorLabelFromSnapshotItems(snapshot),
    meta: '',
    busyIndicator: true,
    busyElapsedText: formatAppServerControlTurnDuration(resolveBusyIndicatorElapsedMs(snapshot)),
  });
  return nextEntries;
}

function resolveBusyIndicatorLabelFromSnapshotItems(
  snapshot: AppServerControlHistorySnapshot,
): string {
  const currentTurnId = snapshot.currentTurn.turnId ?? null;
  const items = Array.isArray(snapshot.items) ? snapshot.items : [];

  for (let index = items.length - 1; index >= 0; index -= 1) {
    const item = items[index];
    if (!item) {
      continue;
    }

    if (!isBusyIndicatorItemCandidate(item, currentTurnId)) {
      continue;
    }

    const label = resolveBusyIndicatorLabelFromItem(snapshot, item, currentTurnId);
    if (label) {
      return label;
    }
  }

  return appServerControlText('appServerControl.status.working', 'Working');
}

function isBusyIndicatorItemCandidate(item: unknown, currentTurnId: string | null): boolean {
  if (typeof item !== 'object' || item === null) {
    return false;
  }

  const candidate = item as {
    itemType?: unknown;
    turnId?: unknown;
    status?: unknown;
  };
  const itemTurnId = typeof candidate.turnId === 'string' ? candidate.turnId : null;
  if (currentTurnId && itemTurnId && itemTurnId !== currentTurnId) {
    return false;
  }

  const normalizedItemType = normalizeBusyIndicatorItemType(candidate.itemType);
  if (BUSY_INDICATOR_EXCLUDED_ITEM_TYPES.has(normalizedItemType)) {
    return false;
  }

  const status = normalizeComparableHistoryText(
    typeof candidate.status === 'string' ? candidate.status : '',
  );
  return BUSY_INDICATOR_ITEM_STATUSES.some((busyStatus) => status.includes(busyStatus));
}

function normalizeBusyIndicatorItemType(itemType: unknown): string {
  return typeof itemType === 'string'
    ? itemType
        .trim()
        .toLowerCase()
        .replace(/[\s-]+/g, '_')
    : '';
}

function resolveBusyIndicatorLabelFromItem(
  snapshot: AppServerControlHistorySnapshot,
  item: unknown,
  currentTurnId: string | null,
): string {
  if (typeof item !== 'object' || item === null) {
    return '';
  }

  const candidate = item as {
    detail?: unknown;
  };
  const detail = typeof candidate.detail === 'string' ? candidate.detail.trim() : '';
  if (
    detail &&
    !isBusyIndicatorCommandLikeText(detail) &&
    !matchesBusyIndicatorSuppressedTurnText(snapshot, detail, currentTurnId)
  ) {
    return detail;
  }

  return '';
}

function isBusyIndicatorCommandLikeText(value: string): boolean {
  const text = value.trim();
  if (!text) {
    return false;
  }

  if (text.includes('\n')) {
    return true;
  }

  if (/^[>$]/.test(text) || /^[A-Za-z]:[\\/]/.test(text) || /^\.\.?[\\/]/.test(text)) {
    return true;
  }

  if (/[|&;<>]/.test(text)) {
    return true;
  }

  return /^(?:pwsh|powershell|cmd|bash|sh|zsh|git|npm|pnpm|yarn|node|dotnet|python|python3|pip|rg|grep|find|ls|dir|cat|type|get-childitem|set-content|select-object|copy-item|move-item|remove-item)\b/i.test(
    text,
  );
}

function matchesBusyIndicatorSuppressedTurnText(
  snapshot: AppServerControlHistorySnapshot,
  label: string,
  currentTurnId: string | null,
): boolean {
  const normalizedLabel = normalizeComparableHistoryText(label);
  if (!normalizedLabel) {
    return false;
  }

  const currentAssistantText = normalizeComparableHistoryText(snapshot.streams.assistantText);
  if (
    currentAssistantText.length >= 16 &&
    (currentAssistantText.includes(normalizedLabel) ||
      normalizedLabel.includes(currentAssistantText))
  ) {
    return true;
  }

  const historyEntries =
    'history' in snapshot && Array.isArray(snapshot.history) ? snapshot.history : [];
  return historyEntries.some((entry) => {
    if (currentTurnId && entry.turnId && entry.turnId !== currentTurnId) {
      return false;
    }

    const normalizedKind = normalizeSnapshotHistoryKind(entry.kind);
    if (!['user', 'assistant'].includes(normalizedKind)) {
      return false;
    }

    const normalizedBody = normalizeComparableHistoryText(entry.body);
    if (normalizedBody.length < 16) {
      return false;
    }

    return normalizedBody.includes(normalizedLabel) || normalizedLabel.includes(normalizedBody);
  });
}

function resolveBusyIndicatorElapsedMs(snapshot: AppServerControlHistorySnapshot): number | null {
  const startedAt = snapshot.currentTurn.startedAt ?? null;
  if (!startedAt) {
    return null;
  }

  const startMs = Date.parse(startedAt);
  return Number.isFinite(startMs) ? Math.max(0, Date.now() - startMs) : null;
}

function maybeRememberCompletedTurnDuration(
  snapshot: AppServerControlHistorySnapshot,
  state: SessionAppServerControlViewState,
): void {
  const turnId = snapshot.currentTurn.turnId ?? null;
  const startedAt = snapshot.currentTurn.startedAt ?? null;
  const completedAt = snapshot.currentTurn.completedAt || snapshot.generatedAt;
  const currentTurnState = normalizeComparableHistoryText(snapshot.currentTurn.state || '');
  if (
    !turnId ||
    !startedAt ||
    !completedAt ||
    state.completedTurnDurationEntries.has(turnId) ||
    ['running', 'in progress'].includes(currentTurnState)
  ) {
    return;
  }

  const durationMs = Date.parse(completedAt) - Date.parse(startedAt);
  if (!Number.isFinite(durationMs) || durationMs < 0) {
    return;
  }

  state.completedTurnDurationEntries.set(turnId, {
    id: `turn-duration:${turnId}`,
    order: Number.MAX_SAFE_INTEGER,
    kind: 'system',
    tone: 'info',
    label: '',
    title: '',
    body: `(Turn took ${formatAppServerControlTurnDuration(durationMs)})`,
    meta: '',
    sourceTurnId: turnId,
    turnDurationNote: true,
  });
}

function pruneCompletedTurnDurationEntries(state: SessionAppServerControlViewState): void {
  const turnIds = [...state.completedTurnDurationEntries.keys()];
  for (const staleTurnId of turnIds.slice(0, Math.max(0, turnIds.length - 64))) {
    state.completedTurnDurationEntries.delete(staleTurnId);
  }
}

function appendTurnDurationEntries(
  entries: readonly AppServerControlHistoryEntry[],
  state: SessionAppServerControlViewState,
): AppServerControlHistoryEntry[] {
  if (state.completedTurnDurationEntries.size === 0) {
    return [...entries];
  }

  const nextEntries = [...entries];
  for (const durationEntry of state.completedTurnDurationEntries.values()) {
    const matchingEntries = entries.filter(
      (entry) => !durationEntry.sourceTurnId || entry.sourceTurnId === durationEntry.sourceTurnId,
    );
    if (matchingEntries.length === 0) {
      continue;
    }

    nextEntries.push({
      ...durationEntry,
      order:
        matchingEntries.reduce(
          (maxOrder, entry) => Math.max(maxOrder, entry.order),
          Number.MIN_SAFE_INTEGER,
        ) + 0.01,
    });
  }

  return nextEntries.sort((left, right) => left.order - right.order);
}

export function withTurnDurationNotes(
  snapshot: AppServerControlHistorySnapshot,
  entries: AppServerControlHistoryEntry[],
  state: SessionAppServerControlViewState,
): AppServerControlHistoryEntry[] {
  maybeRememberCompletedTurnDuration(snapshot, state);
  pruneCompletedTurnDurationEntries(state);
  return appendTurnDurationEntries(entries, state);
}

export function syncBusyIndicatorTicker(args: {
  snapshot: AppServerControlHistorySnapshot;
  state: SessionAppServerControlViewState;
  entries: readonly AppServerControlHistoryEntry[];
  renderCurrentAgentView: (sessionId: string, options?: { immediate?: boolean }) => void;
  updateBusyIndicatorElapsed: (sessionId: string, elapsedText: string) => boolean;
}): void {
  const { snapshot, state, entries, renderCurrentAgentView, updateBusyIndicatorElapsed } = args;
  if (!entries.some((entry) => entry.busyIndicator)) {
    if (state.busyIndicatorTickHandle !== null) {
      window.clearTimeout(state.busyIndicatorTickHandle);
      state.busyIndicatorTickHandle = null;
    }
    return;
  }

  if (state.busyIndicatorTickHandle !== null) {
    return;
  }

  state.busyIndicatorTickHandle = window.setTimeout(() => {
    state.busyIndicatorTickHandle = null;
    const elapsedText = formatAppServerControlTurnDuration(resolveBusyIndicatorElapsedMs(snapshot));
    if (!updateBusyIndicatorElapsed(snapshot.sessionId, elapsedText)) {
      renderCurrentAgentView(snapshot.sessionId, { immediate: true });
      return;
    }

    syncBusyIndicatorTicker(args);
  }, 1000);
}

export function withActivationIssueNotice(
  entries: AppServerControlHistoryEntry[],
  issue: AppServerControlActivationIssue | null,
): AppServerControlHistoryEntry[] {
  if (!issue) {
    return entries;
  }

  return [
    {
      id: `appServerControl-issue:${issue.kind}`,
      order: Number.MIN_SAFE_INTEGER,
      kind: issue.tone === 'attention' ? 'notice' : 'system',
      tone: issue.tone,
      label: appServerControlText('appServerControl.label.midterm', 'tlbx'),
      title: issue.title,
      body: issue.body,
      meta: issue.meta,
      actions: issue.actions,
    },
    ...entries,
  ];
}

export function buildActivationHistoryEntries(
  state: SessionAppServerControlViewState,
): AppServerControlHistoryEntry[] {
  if (state.activationTrace.length === 0) {
    return [
      {
        id: 'activation:pending',
        order: 0,
        kind: 'system',
        tone: state.activationState === 'failed' ? 'attention' : 'warning',
        label: appServerControlText('appServerControl.label.midterm', 'tlbx'),
        title: '',
        body: state.activationDetail || 'Waiting for AppServerControl boot steps…',
        meta:
          state.activationState === 'failed'
            ? appServerControlText('appServerControl.status.failed', 'Failed')
            : appServerControlText('appServerControl.status.connecting', 'Connecting'),
      },
    ];
  }

  const traceEntries =
    state.activationIssue?.kind === 'busy-terminal-turn' ||
    state.activationIssue?.kind === 'missing-resume-id' ||
    state.activationIssue?.kind === 'shell-recovery-failed' ||
    state.activationIssue?.kind === 'native-runtime-unavailable'
      ? state.activationTrace.filter((entry) => entry.tone !== 'attention').slice(-2)
      : state.activationTrace;

  return traceEntries.map((entry, index) => ({
    id: `activation:${index}`,
    order: index,
    kind: entry.tone === 'attention' ? ('notice' as const) : ('system' as const),
    tone: entry.tone,
    label: appServerControlText('appServerControl.label.midterm', 'tlbx'),
    title: '',
    body: entry.detail,
    meta: entry.meta,
  }));
}

export function formatHistoryMeta(kind: HistoryKind, statusLabel: string, value: string): string {
  void kind;
  void statusLabel;
  return formatAbsoluteTime(value);
}

export function shouldHideStatusInMeta(kind: HistoryKind, statusLabel: string): boolean {
  void kind;
  void statusLabel;
  return true;
}

export function formatAppServerControlTurnDuration(durationMs: number | null | undefined): string {
  if (durationMs === null || durationMs === undefined || !Number.isFinite(durationMs)) {
    return '0s';
  }

  const totalSeconds = Math.max(0, Math.floor(durationMs / 1000));
  const seconds = totalSeconds % 60;
  const totalMinutes = Math.floor(totalSeconds / 60);
  const minutes = totalMinutes % 60;
  const hours = Math.floor(totalMinutes / 60);

  if (hours > 0) {
    return `${hours}h ${minutes}m ${seconds}s`;
  }

  if (totalMinutes > 0) {
    return `${totalMinutes}m ${seconds}s`;
  }

  return `${seconds}s`;
}
