import type {
  AppServerControlHistoryDelta,
  AppServerControlHistorySnapshot,
} from '../../api/client';
import { computeHistoryVisibleRange } from './historyViewport';
import type {
  HistoryViewportMetrics,
  AppServerControlHistoryEntry,
  SessionAppServerControlViewState,
} from './types';

type AppServerControlHistoryFetchReason =
  | 'initial'
  | 'refresh'
  | 'latest'
  | 'scroll'
  | 'jump'
  | 'drag-preview'
  | 'stream-window'
  | 'hidden-compact';

type AppServerControlHistoryScrollReason = 'fast-wheel' | 'gap-wait' | 'gap-snap';

type TraceSessionState = {
  lastShowKey: string | null;
  lastWindowStart: number | null;
  lastWindowEnd: number | null;
};

type TraceShowArgs = {
  sessionId: string;
  historyWindowStart: number;
  historyWindowEnd: number;
  historyCount: number;
  visibleStart: number;
  visibleEnd: number;
  pinnedToBottom: boolean;
};

type TraceScrollArgs = {
  sessionId: string;
  reason: AppServerControlHistoryScrollReason;
  scrollTop: number;
  clientHeight: number;
  scrollHeight: number;
  deltaYPx?: number | undefined;
  historyWindowStart?: number | undefined;
  historyWindowEnd?: number | undefined;
  historyCount?: number | undefined;
};

const traceSessionState = new Map<string, TraceSessionState>();

function getTraceSessionState(sessionId: string): TraceSessionState {
  let state = traceSessionState.get(sessionId);
  if (!state) {
    state = {
      lastShowKey: null,
      lastWindowStart: null,
      lastWindowEnd: null,
    };
    traceSessionState.set(sessionId, state);
  }

  return state;
}

function readAppServerControlTraceFlag(): string | null {
  if (typeof window === 'undefined' || !('localStorage' in window)) {
    return null;
  }

  try {
    return window.localStorage.getItem('midterm.appServerControlTrace');
  } catch {
    return null;
  }
}

export function shouldTraceAppServerControlHistory(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }

  const explicitFlag =
    (window as typeof window & { __MIDTERM_APP_SERVER_CONTROL_TRACE__?: boolean })
      .__MIDTERM_APP_SERVER_CONTROL_TRACE__ === true;
  if (explicitFlag) {
    return true;
  }

  const storedFlag = readAppServerControlTraceFlag();
  if (storedFlag === '1' || storedFlag === 'true') {
    return true;
  }

  // Installed local hosts are production use too. Per-frame formatting and
  // console delivery remain available only when explicitly requested.
  return false;
}

function trace(sessionId: string, message: string): void {
  if (!shouldTraceAppServerControlHistory()) {
    return;
  }

  // eslint-disable-next-line no-console
  console.debug(`[AppServerControlHistory ${sessionId.slice(0, 8)}] ${message}`);
}

function formatRange(startIndex: number, endIndexExclusive: number): string {
  if (endIndexExclusive <= startIndex) {
    return 'empty';
  }

  const start = startIndex + 1;
  const end = endIndexExclusive;
  return start === end ? `#${start}` : `#${start}-#${end}`;
}

function formatDiscreteRangeNumbers(values: readonly number[]): string {
  if (values.length === 0) {
    return 'none';
  }

  const ordered = [...new Set(values.filter((value) => Number.isFinite(value) && value > 0))].sort(
    (left, right) => left - right,
  );
  if (ordered.length === 0) {
    return 'none';
  }

  const ranges: string[] = [];
  const first = ordered[0];
  if (first === undefined) {
    return 'none';
  }
  let rangeStart = first;
  let previous = first;

  for (let index = 1; index < ordered.length; index += 1) {
    const current = ordered[index];
    if (current === undefined) {
      continue;
    }

    if (current === previous + 1) {
      previous = current;
      continue;
    }

    ranges.push(rangeStart === previous ? `#${rangeStart}` : `#${rangeStart}-#${previous}`);
    rangeStart = current;
    previous = current;
  }

  ranges.push(rangeStart === previous ? `#${rangeStart}` : `#${rangeStart}-#${previous}`);
  return ranges.join(', ');
}

function formatFetchReason(reason: AppServerControlHistoryFetchReason): string {
  switch (reason) {
    case 'initial':
      return 'initial';
    case 'refresh':
      return 'refresh';
    case 'latest':
      return 'bottom';
    case 'scroll':
      return 'scroll';
    case 'jump':
      return 'jump';
    case 'drag-preview':
      return 'drag';
    case 'stream-window':
      return 'stream';
    case 'hidden-compact':
      return 'compact';
  }
}

function formatDiscardRanges(
  previousStart: number,
  previousEnd: number,
  nextStart: number,
  nextEnd: number,
): string | null {
  const ranges: string[] = [];
  if (previousStart < nextStart) {
    ranges.push(formatRange(previousStart, Math.min(previousEnd, nextStart)));
  }
  if (previousEnd > nextEnd) {
    ranges.push(formatRange(Math.max(previousStart, nextEnd), previousEnd));
  }

  return ranges.length > 0 ? ranges.join(', ') : null;
}

function shouldTracePush(delta: AppServerControlHistoryDelta): boolean {
  return (
    delta.historyUpserts.length > 0 ||
    delta.historyRemovals.length > 0 ||
    delta.requestUpserts.length > 0 ||
    delta.requestRemovals.length > 0 ||
    delta.noticeUpserts.length > 0
  );
}

function resolveHistoryPushRanges(
  delta: AppServerControlHistoryDelta,
  currentSnapshot: AppServerControlHistorySnapshot | null | undefined,
): {
  insertedOrders: number[];
  updatedOrders: number[];
  removalOrders: number[];
} {
  const previousHistoryCount = currentSnapshot?.historyCount ?? 0;
  const visibleEntryIds = new Set(currentSnapshot?.history.map((entry) => entry.entryId) ?? []);
  const insertedOrders = delta.historyUpserts
    .filter((entry) => entry.order > previousHistoryCount && !visibleEntryIds.has(entry.entryId))
    .map((entry) => entry.order);
  const insertedOrderSet = new Set(insertedOrders);
  const updatedOrders = delta.historyUpserts
    .filter((entry) => !insertedOrderSet.has(entry.order))
    .map((entry) => entry.order);
  const removalOrders =
    currentSnapshot?.history
      .filter((entry) => delta.historyRemovals.includes(entry.entryId))
      .map((entry) => entry.order) ?? [];

  return {
    insertedOrders,
    updatedOrders,
    removalOrders,
  };
}

function appendHistoryPushRanges(
  parts: string[],
  delta: AppServerControlHistoryDelta,
  ranges: {
    insertedOrders: readonly number[];
    updatedOrders: readonly number[];
    removalOrders: readonly number[];
  },
): void {
  if (ranges.insertedOrders.length > 0) {
    parts.push(`+${formatDiscreteRangeNumbers(ranges.insertedOrders)}`);
  }
  if (ranges.updatedOrders.length > 0) {
    parts.push(`~${formatDiscreteRangeNumbers(ranges.updatedOrders)}`);
  }
  if (ranges.removalOrders.length > 0) {
    parts.push(`-${formatDiscreteRangeNumbers(ranges.removalOrders)}`);
  } else if (delta.historyRemovals.length > 0) {
    parts.push(`-x${delta.historyRemovals.length}`);
  }
}

function appendAncillaryPushChanges(parts: string[], delta: AppServerControlHistoryDelta): void {
  if (delta.requestUpserts.length > 0 || delta.requestRemovals.length > 0) {
    const requestDelta = delta.requestUpserts.length - delta.requestRemovals.length;
    parts.push(`req ${requestDelta >= 0 ? '+' : ''}${requestDelta}`);
  }
  if (delta.noticeUpserts.length > 0) {
    parts.push(`notice +${delta.noticeUpserts.length}`);
  }
}

export function traceAppServerControlHistoryFetch(
  sessionId: string,
  snapshot: AppServerControlHistorySnapshot,
  reason: AppServerControlHistoryFetchReason,
): void {
  trace(
    sessionId,
    `fetch ${formatRange(snapshot.historyWindowStart, snapshot.historyWindowEnd)} ${formatFetchReason(reason)} total ${snapshot.historyCount}`,
  );
}

export function traceAppServerControlHistoryPush(
  sessionId: string,
  delta: AppServerControlHistoryDelta,
  currentSnapshot: AppServerControlHistorySnapshot | null | undefined,
): void {
  if (!shouldTracePush(delta) || !shouldTraceAppServerControlHistory()) {
    return;
  }

  const ranges = resolveHistoryPushRanges(delta, currentSnapshot);
  const parts = ['push'];
  appendHistoryPushRanges(parts, delta, ranges);
  appendAncillaryPushChanges(parts, delta);
  parts.push(`seq ${delta.latestSequence}`);
  parts.push(`total ${delta.historyCount}`);
  trace(sessionId, parts.join(' '));
}

export function traceAppServerControlHistoryScroll(args: TraceScrollArgs): void {
  if (!shouldTraceAppServerControlHistory()) {
    return;
  }

  const parts = [
    'scroll',
    args.reason,
    `top ${Math.round(args.scrollTop)}`,
    `height ${Math.round(args.clientHeight)}/${Math.round(args.scrollHeight)}`,
  ];
  if (typeof args.deltaYPx === 'number' && Number.isFinite(args.deltaYPx)) {
    parts.push(`dy ${Math.round(args.deltaYPx)}`);
  }
  if (typeof args.historyWindowStart === 'number' && typeof args.historyWindowEnd === 'number') {
    parts.push(formatRange(args.historyWindowStart, args.historyWindowEnd));
  }
  if (typeof args.historyCount === 'number') {
    parts.push(`total ${args.historyCount}`);
  }

  trace(args.sessionId, parts.join(' '));
}

export function traceAppServerControlHistoryShow(args: TraceShowArgs): void {
  if (!shouldTraceAppServerControlHistory()) {
    return;
  }

  const state = getTraceSessionState(args.sessionId);
  const showKey = [
    args.historyWindowStart,
    args.historyWindowEnd,
    args.visibleStart,
    args.visibleEnd,
    args.pinnedToBottom ? 'bottom' : 'custom',
  ].join(':');
  if (state.lastShowKey === showKey) {
    return;
  }

  const discard =
    state.lastWindowStart !== null && state.lastWindowEnd !== null
      ? formatDiscardRanges(
          state.lastWindowStart,
          state.lastWindowEnd,
          args.historyWindowStart,
          args.historyWindowEnd,
        )
      : null;

  const parts = [
    'show',
    formatRange(args.historyWindowStart, args.historyWindowEnd),
    'view',
    formatRange(args.visibleStart, args.visibleEnd),
    args.pinnedToBottom ? 'bottom' : 'custom',
  ];
  if (discard) {
    parts.push(`discard ${discard}`);
  }
  if (args.historyWindowEnd < args.historyCount) {
    parts.push(`total ${args.historyCount}`);
  }

  trace(args.sessionId, parts.join(' '));
  state.lastShowKey = showKey;
  state.lastWindowStart = args.historyWindowStart;
  state.lastWindowEnd = args.historyWindowEnd;
}

export function traceRenderedAppServerControlHistoryWindow(args: {
  sessionId: string;
  entries: readonly AppServerControlHistoryEntry[];
  metrics: HistoryViewportMetrics;
  state: SessionAppServerControlViewState;
  resolveEntryHeight: (entry: AppServerControlHistoryEntry) => number;
}): void {
  if (!shouldTraceAppServerControlHistory() || !args.state.snapshot) {
    return;
  }

  const visibleRange = computeHistoryVisibleRange(
    args.entries,
    args.metrics.scrollTop,
    args.metrics.clientHeight,
    args.metrics.clientWidth,
    (entry) => args.resolveEntryHeight(entry),
  );
  traceAppServerControlHistoryShow({
    sessionId: args.sessionId,
    historyWindowStart: args.state.snapshot.historyWindowStart,
    historyWindowEnd: args.state.snapshot.historyWindowEnd,
    historyCount: args.state.snapshot.historyCount,
    visibleStart: args.state.snapshot.historyWindowStart + visibleRange.start,
    visibleEnd: args.state.snapshot.historyWindowStart + visibleRange.end,
    pinnedToBottom: args.state.historyAutoScrollPinned,
  });
}

export function traceAppServerControlHistoryCompact(
  sessionId: string,
  previousStart: number,
  previousEnd: number,
  nextStart: number,
  nextEnd: number,
  historyCount: number,
): void {
  if (!shouldTraceAppServerControlHistory()) {
    return;
  }

  const discard = formatDiscardRanges(previousStart, previousEnd, nextStart, nextEnd);
  const parts = [
    'show',
    formatRange(nextStart, nextEnd),
    formatFetchReason('hidden-compact'),
    'view',
    formatRange(nextStart, nextEnd),
    'custom',
  ];
  if (discard) {
    parts.push(`discard ${discard}`);
  }
  if (nextEnd < historyCount) {
    parts.push(`total ${historyCount}`);
  }

  trace(sessionId, parts.join(' '));
  const state = getTraceSessionState(sessionId);
  state.lastShowKey = null;
  state.lastWindowStart = nextStart;
  state.lastWindowEnd = nextEnd;
}

export function resetAppServerControlHistoryTrace(sessionId?: string): void {
  if (sessionId) {
    traceSessionState.delete(sessionId);
    return;
  }

  traceSessionState.clear();
}
