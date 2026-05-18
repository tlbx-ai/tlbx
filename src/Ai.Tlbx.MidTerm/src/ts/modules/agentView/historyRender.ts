import { t } from '../i18n';
import { resolveHistoryBadgeLabel } from './activationHelpers';
import {
  resolveHistoryEstimatedEntryHeight,
  recordHistoryMeasuredHeight,
  resolveHistoryViewportEntryHeight,
} from './historyMeasurements';
import {
  buildHistoryVirtualWindowKey,
  computeHistoryVirtualWindow,
  computeHistoryVisibleRange,
  HISTORY_VIRTUALIZE_AFTER,
  APP_SERVER_CONTROL_HISTORY_OVERSCAN_ITEMS,
  setHistoryScrollMode,
} from './historyViewport';
import { resolveToolCallOutputLineLimit } from './historyDom';
import {
  traceAppServerControlHistoryScroll,
  traceRenderedAppServerControlHistoryWindow,
} from './historyTrace';
import type {
  AppServerControlHistoryRequestSummary,
  AppServerControlHistorySnapshot,
} from '../../api/client';
import {
  captureViewportAnchor,
  resolveRetainedWindowViewportMetrics,
  resolveScrollCompensationDelta,
  resolveViewportCenteredWindowRequest,
  restoreViewportAnchor,
  syncViewportScrollPosition,
  type VirtualizerAnchor,
  type VirtualizerMeasuredItemChange,
  type VirtualizerWindowViewportMetrics,
} from '../../utils/virtualizer';
import type {
  ArtifactClusterInfo,
  HistoryJumpAlign,
  HistoryPlaceholderBlock,
  HistoryRenderPlan,
  HistoryScrollMetrics,
  HistoryVirtualWindow,
  HistoryViewportMetrics,
  HistoryVisibleEntry,
  AppServerControlHistoryEntry,
  SessionAppServerControlViewState,
} from './types';
/* eslint-disable max-lines -- AppServerControl history rendering/virtualization remains intentionally consolidated while the browser-side virtualizer is being hardened. */

function appServerControlText(key: string, fallback: string): string {
  const translated = t(key);
  return !translated || translated === key ? fallback : translated;
}

const HISTORY_PROGRESS_THUMB_INSET_PX = 6;
const HISTORY_PROGRESS_THUMB_DESKTOP_MIN_PX = 40;
const HISTORY_PROGRESS_THUMB_DESKTOP_MAX_PX = 72;
const HISTORY_PROGRESS_THUMB_TOUCH_MIN_PX = 44;
const HISTORY_PROGRESS_THUMB_TOUCH_MAX_PX = 84;
const HISTORY_PROGRESS_TOP_ALIGN_THRESHOLD_PX = 1;

function resolveHistoryEntryHeight(
  entry: AppServerControlHistoryEntry,
  state: SessionAppServerControlViewState | undefined,
  clientWidth: number,
): number {
  return resolveHistoryViewportEntryHeight(entry, state, clientWidth);
}

type HistoryRenderDeps = {
  getState: (sessionId: string) => SessionAppServerControlViewState | undefined;
  scheduleHistoryRender: (sessionId: string) => void;
  syncAgentViewPresentation: (
    panel: HTMLDivElement,
    provider: AppServerControlHistorySnapshot['provider'] | null | undefined,
  ) => void;
  createHistoryEntry: (
    entry: AppServerControlHistoryEntry,
    sessionId: string,
    options?: {
      artifactCluster?: ArtifactClusterInfo | null;
      showAssistantBadge?: boolean;
    },
  ) => HTMLElement;
  syncBusyIndicatorEntry?: (node: HTMLElement, entry: AppServerControlHistoryEntry) => void;
  createHistoryPlaceholderBlock?: (args: {
    heightPx: number;
    itemCount: number;
    direction: 'earlier' | 'later';
    label: string;
    rangeLabel: string;
  }) => HTMLElement;
  createRequestActionBlock: (
    sessionId: string,
    request: AppServerControlHistoryRequestSummary,
    busy: boolean,
    state: SessionAppServerControlViewState,
    surface: 'composer' | 'history',
  ) => HTMLElement;
  pruneAssistantMarkdownCache: (
    state: SessionAppServerControlViewState,
    entries: readonly AppServerControlHistoryEntry[],
  ) => void;
  renderRuntimeStats: (
    panel: HTMLDivElement,
    stats: SessionAppServerControlViewState['runtimeStats'],
  ) => void;
  syncViewportHistoryWindow?: (sessionId: string) => void;
};

const HISTORY_PLACEHOLDER_TARGET_BLOCK_HEIGHT_PX = 960;
const HISTORY_PLACEHOLDER_MAX_BLOCKS = 10;
const PROGRAMMATIC_HISTORY_VIEWPORT_SYNC_SUPPRESS_MS = 200;
const USER_HISTORY_GAP_RECOVERY_GRACE_MS = 900;

export type HistoryWindowViewportMetrics = VirtualizerWindowViewportMetrics;

function resolveContiguousHistoryEntryWindow(entries: readonly AppServerControlHistoryEntry[]): {
  windowStart: number;
  windowEnd: number;
} | null {
  if (entries.length === 0) {
    return null;
  }

  const firstOrder = Math.max(1, entries[0]?.order ?? 1);
  const lastOrder = Math.max(firstOrder, entries[entries.length - 1]?.order ?? firstOrder);
  const inferredWindowStart = firstOrder - 1;
  const inferredWindowEnd = lastOrder;
  const inferredCount = Math.max(0, inferredWindowEnd - inferredWindowStart);
  if (inferredCount !== entries.length) {
    return null;
  }

  return {
    windowStart: inferredWindowStart,
    windowEnd: inferredWindowEnd,
  };
}

export function resolveHistoryRetainedWindowDescriptor(
  entries: readonly AppServerControlHistoryEntry[],
  state: SessionAppServerControlViewState | undefined,
): {
  windowStart: number;
  windowEnd: number;
  totalCount: number;
} {
  const snapshotWindowStart = Math.max(0, state?.snapshot?.historyWindowStart ?? 0);
  const snapshotWindowEnd = Math.max(
    snapshotWindowStart,
    state?.snapshot?.historyWindowEnd ?? snapshotWindowStart + entries.length,
  );
  const snapshotTotalCount = Math.max(snapshotWindowEnd, state?.snapshot?.historyCount ?? 0);
  const contiguousWindow = resolveContiguousHistoryEntryWindow(entries);
  if (!contiguousWindow) {
    return {
      windowStart: snapshotWindowStart,
      windowEnd: snapshotWindowEnd,
      totalCount: snapshotTotalCount,
    };
  }

  const snapshotWindowSize = Math.max(0, snapshotWindowEnd - snapshotWindowStart);
  if (snapshotWindowSize > entries.length) {
    return {
      windowStart: snapshotWindowStart,
      windowEnd: snapshotWindowEnd,
      totalCount: snapshotTotalCount,
    };
  }

  return {
    windowStart: contiguousWindow.windowStart,
    windowEnd: contiguousWindow.windowEnd,
    totalCount: Math.max(snapshotTotalCount, contiguousWindow.windowEnd),
  };
}

export function resolveHistoryWindowViewportMetrics(
  entries: readonly AppServerControlHistoryEntry[],
  state: SessionAppServerControlViewState | undefined,
  metrics: HistoryViewportMetrics,
  resolveEntryHeight: (entry: AppServerControlHistoryEntry) => number,
): HistoryWindowViewportMetrics {
  const retainedWindow = resolveHistoryRetainedWindowDescriptor(entries, state);
  return resolveRetainedWindowViewportMetrics({
    items: entries,
    viewportMetrics: metrics,
    retainedWindow,
    observedSizes: state?.historyObservedHeights.values(),
    resolveItemSize: (entry) => resolveEntryHeight(entry),
    resolveEstimatedItemSize: (entry) =>
      resolveHistoryEstimatedEntryHeight(entry, metrics.clientWidth),
  });
}

function toHistoryViewportAnchor(
  anchor: VirtualizerAnchor | null,
): SessionAppServerControlViewState['pendingHistoryLayoutAnchor'] {
  if (!anchor) {
    return null;
  }

  return {
    entryId: anchor.key,
    topOffsetPx: anchor.topOffsetPx,
    absoluteIndex: anchor.absoluteIndex,
  };
}

function updateBusyIndicatorElapsedInState(
  state: SessionAppServerControlViewState | undefined,
  elapsedText: string,
): boolean {
  if (!state) {
    return false;
  }

  for (const rendered of state.historyRenderedNodes.values()) {
    if (!rendered.entry.busyIndicator) {
      continue;
    }

    const elapsed = rendered.node.querySelector<HTMLElement>('.agent-history-busy-elapsed');
    if (!elapsed) {
      return false;
    }

    elapsed.textContent = elapsedText;
    rendered.entry.busyElapsedText = elapsedText;
    return true;
  }

  return false;
}

function resolveMeasurementBrowseAnchor(
  state: SessionAppServerControlViewState,
  viewport: HTMLDivElement,
): VirtualizerAnchor | null {
  if (state.historyAutoScrollPinned || state.pendingHistoryPrependAnchor !== null) {
    return null;
  }

  return captureViewportAnchor({
    viewport,
    renderedNodes: Array.from(state.historyRenderedNodes, ([entryId, rendered]) => ({
      key: entryId,
      node: rendered.node,
      absoluteIndex: resolveAnchorAbsoluteIndex(state, entryId),
    })),
  });
}

function collectHistoryMeasurementChanges(args: {
  state: SessionAppServerControlViewState;
  viewport: HTMLDivElement;
  records: readonly ResizeObserverEntry[];
}): VirtualizerMeasuredItemChange[] {
  const { state, viewport, records } = args;
  const changes: VirtualizerMeasuredItemChange[] = [];

  for (const record of records) {
    const target = record.target as HTMLElement;
    const entryId = target.dataset.appServerControlEntryId;
    if (!entryId) {
      continue;
    }

    const relativeIndex = state.historyEntries.findIndex((entry) => entry.id === entryId);
    const entry = relativeIndex >= 0 ? (state.historyEntries[relativeIndex] ?? null) : null;
    const previousSize =
      entry === null ? null : resolveHistoryViewportEntryHeight(entry, state, viewport.clientWidth);
    const sizeChanged = recordHistoryMeasuredHeight(
      state,
      entryId,
      record.contentRect.height,
      viewport.clientWidth,
    );
    if (!sizeChanged || entry === null || previousSize === null) {
      continue;
    }

    const nextSize = resolveHistoryViewportEntryHeight(entry, state, viewport.clientWidth);
    changes.push({
      absoluteIndex: (state.snapshot?.historyWindowStart ?? 0) + relativeIndex,
      previousSize,
      nextSize,
    });
    const rendered = state.historyRenderedNodes.get(entryId);
    if (rendered) {
      rendered.lastMeasuredWidthBucket = state.historyMeasuredWidthBucket;
    }
  }

  return changes;
}

function syncHistoryMeasurementObserver(args: {
  sessionId: string;
  state: SessionAppServerControlViewState;
  visibleEntries: readonly HistoryVisibleEntry[];
  getState: (sessionId: string) => SessionAppServerControlViewState | undefined;
  scheduleHistoryRender: (sessionId: string) => void;
}): void {
  if (typeof ResizeObserver !== 'function') {
    return;
  }

  args.state.historyMeasurementObserver ??= new ResizeObserver((records) => {
    const current = args.getState(args.sessionId);
    const viewport = current?.historyViewport;
    if (!current || !viewport) {
      return;
    }

    const browseAnchor = resolveMeasurementBrowseAnchor(current, viewport);
    const measurementChanges = collectHistoryMeasurementChanges({
      state: current,
      viewport,
      records,
    });
    if (measurementChanges.length === 0) {
      return;
    }

    const compensationDelta = resolveScrollCompensationDelta({
      changes: measurementChanges,
      anchorAbsoluteIndex: browseAnchor?.absoluteIndex,
    });
    if (!current.historyAutoScrollPinned && compensationDelta !== 0) {
      syncViewportScrollPosition(viewport, viewport.scrollTop + compensationDelta);
      current.historyLastScrollMetrics = readHistoryScrollMetrics(viewport, current);
    }

    if (!current.historyAutoScrollPinned && current.pendingHistoryPrependAnchor === null) {
      current.pendingHistoryLayoutAnchor = toHistoryViewportAnchor(browseAnchor);
    }

    args.scheduleHistoryRender(args.sessionId);
  });

  args.state.historyMeasurementObserver.disconnect();
  for (const visibleEntry of args.visibleEntries) {
    const node = args.state.historyRenderedNodes.get(visibleEntry.key)?.node;
    if (node) {
      args.state.historyMeasurementObserver.observe(node);
    }
  }
}

function buildVisibleHistoryEntries(args: {
  entries: readonly AppServerControlHistoryEntry[];
  visibleStart: number;
  visibleEnd: number;
  state: SessionAppServerControlViewState | undefined;
  resolveCluster: (
    entries: readonly AppServerControlHistoryEntry[],
    absoluteIndex: number,
  ) => ArtifactClusterInfo | null;
  buildSignature: (
    entry: AppServerControlHistoryEntry,
    cluster: ArtifactClusterInfo | null,
    state: SessionAppServerControlViewState | undefined,
    showAssistantBadge: boolean,
  ) => string;
}): HistoryVisibleEntry[] {
  const { entries, visibleStart, visibleEnd, state, resolveCluster, buildSignature } = args;
  return entries.slice(visibleStart, visibleEnd).map((entry, visibleIndex) => {
    const absoluteIndex = visibleStart + visibleIndex;
    const cluster = resolveCluster(entries, absoluteIndex);
    const showAssistantBadge = shouldShowAssistantBadge(entries, absoluteIndex);
    return {
      key: entry.id,
      entry,
      cluster,
      showAssistantBadge,
      signature: buildSignature(entry, cluster, state, showAssistantBadge),
    };
  });
}

function hasEarlierAssistantInTurn(
  entries: readonly AppServerControlHistoryEntry[],
  absoluteIndex: number,
  sourceTurnId: string,
): boolean {
  for (let index = absoluteIndex - 1; index >= 0; index -= 1) {
    const previous = entries[index];
    if (previous?.kind === 'assistant' && (previous.sourceTurnId?.trim() ?? '') === sourceTurnId) {
      return true;
    }
  }

  return false;
}

function didUserStartMostRecentUntaggedRun(
  entries: readonly AppServerControlHistoryEntry[],
  absoluteIndex: number,
): boolean {
  for (let index = absoluteIndex - 1; index >= 0; index -= 1) {
    const previous = entries[index];
    if (!previous) {
      continue;
    }

    if (previous.kind === 'assistant') {
      return false;
    }

    if (previous.kind === 'user') {
      return true;
    }
  }

  return true;
}

function shouldShowAssistantBadge(
  entries: readonly AppServerControlHistoryEntry[],
  absoluteIndex: number,
): boolean {
  const entry = entries[absoluteIndex];
  if (!entry || entry.kind !== 'assistant') {
    return false;
  }

  const sourceTurnId = entry.sourceTurnId?.trim() ?? '';
  return sourceTurnId
    ? !hasEarlierAssistantInTurn(entries, absoluteIndex, sourceTurnId)
    : didUserStartMostRecentUntaggedRun(entries, absoluteIndex);
}

function resolveAnchorAbsoluteIndex(
  state: SessionAppServerControlViewState,
  entryId: string,
): number {
  const relativeIndex = state.historyEntries.findIndex((entry) => entry.id === entryId);
  const historyWindowStart = resolveHistoryRetainedWindowDescriptor(
    state.historyEntries,
    state,
  ).windowStart;
  return relativeIndex >= 0 ? historyWindowStart + relativeIndex : historyWindowStart;
}

function buildHistoryAttachmentToken(entry: AppServerControlHistoryEntry): string {
  return (entry.attachments ?? [])
    .map((attachment) =>
      [attachment.kind, attachment.displayName, attachment.path, attachment.mimeType ?? ''].join(
        ':',
      ),
    )
    .join('|');
}

function buildHistoryClusterToken(cluster: ArtifactClusterInfo | null): string {
  return cluster
    ? [cluster.position, cluster.label ?? '', cluster.count, cluster.onlyTools ? '1' : '0'].join(
        ':',
      )
    : '';
}

function buildAssistantPreviewToken(
  entry: AppServerControlHistoryEntry,
  state: SessionAppServerControlViewState | undefined,
): string {
  void state;
  return (entry.imagePreviews ?? []).map((preview) => preview.resolvedPath).join('|');
}

function buildHistoryActionToken(entry: AppServerControlHistoryEntry): string {
  return (entry.actions ?? [])
    .map((action) => [action.id, action.label, action.style, action.busyLabel ?? ''].join(':'))
    .join('|');
}

function resolveHistoryProgressThumbHeightPx(host: HTMLDivElement): number {
  const coarsePointer =
    typeof window !== 'undefined' &&
    typeof window.matchMedia === 'function' &&
    window.matchMedia('(pointer: coarse)').matches;
  const minPx = coarsePointer
    ? HISTORY_PROGRESS_THUMB_TOUCH_MIN_PX
    : HISTORY_PROGRESS_THUMB_DESKTOP_MIN_PX;
  const maxPx = coarsePointer
    ? HISTORY_PROGRESS_THUMB_TOUCH_MAX_PX
    : HISTORY_PROGRESS_THUMB_DESKTOP_MAX_PX;
  return Math.max(
    minPx,
    Math.min(
      maxPx,
      Math.round(Math.max(1, host.clientHeight - HISTORY_PROGRESS_THUMB_INSET_PX * 2) * 0.18),
    ),
  );
}

function clampHistoryAbsoluteIndex(index: number, historyCount: number): number {
  if (historyCount <= 0) {
    return 0;
  }

  return Math.max(0, Math.min(historyCount - 1, Math.round(index)));
}

function readHistoryViewportMetrics(
  container: HTMLDivElement,
  state?: SessionAppServerControlViewState,
): HistoryViewportMetrics {
  void state;

  return {
    scrollTop: container.scrollTop,
    clientHeight: container.clientHeight,
    clientWidth: container.clientWidth,
  };
}

function readHistoryScrollMetrics(
  container: HTMLDivElement,
  state?: SessionAppServerControlViewState,
): HistoryScrollMetrics {
  void state;

  return {
    scrollTop: container.scrollTop,
    clientHeight: container.clientHeight,
    scrollHeight: container.scrollHeight,
  };
}

function resolveHistoryNavigatorEstimatedAnchorIndex(
  state: SessionAppServerControlViewState,
  viewport: HTMLDivElement,
  totalCount: number,
): number | null {
  if (state.historyEntries.length === 0) {
    return null;
  }

  const metrics = readHistoryViewportMetrics(viewport, state);
  const visibleRange = computeHistoryVisibleRange(
    state.historyEntries,
    metrics.scrollTop,
    metrics.clientHeight,
    metrics.clientWidth,
    (entry) => resolveHistoryEntryHeight(entry, state, metrics.clientWidth),
  );
  if (visibleRange.end <= visibleRange.start) {
    return null;
  }

  const firstVisibleEntry = state.historyEntries[visibleRange.start];
  const lastVisibleEntry = state.historyEntries[Math.max(visibleRange.start, visibleRange.end - 1)];
  if (!firstVisibleEntry || !lastVisibleEntry) {
    return null;
  }

  const visibleStart = Math.max(0, firstVisibleEntry.order - 1);
  const visibleEnd = Math.max(visibleStart, lastVisibleEntry.order - 1);

  if (metrics.scrollTop <= HISTORY_PROGRESS_TOP_ALIGN_THRESHOLD_PX) {
    return 0;
  }

  if (
    visibleRange.end >= state.historyEntries.length &&
    visibleEnd >= Math.max(0, totalCount - 1)
  ) {
    return Math.max(0, totalCount - 1);
  }

  return (visibleStart + visibleEnd) / 2;
}

function resolveHistoryNavigatorConcreteAnchorIndex(
  state: SessionAppServerControlViewState,
  viewport: HTMLDivElement,
  totalCount: number,
): number | null {
  if (state.historyEntries.length === 0 || typeof viewport.getBoundingClientRect !== 'function') {
    return null;
  }

  const metrics = readHistoryScrollMetrics(viewport, state);
  if (metrics.scrollTop <= HISTORY_PROGRESS_TOP_ALIGN_THRESHOLD_PX) {
    return 0;
  }

  const distanceFromBottom = metrics.scrollHeight - metrics.clientHeight - metrics.scrollTop;
  const retainedWindow = resolveHistoryRetainedWindowDescriptor(state.historyEntries, state);
  if (
    distanceFromBottom <= HISTORY_PROGRESS_TOP_ALIGN_THRESHOLD_PX &&
    retainedWindow.windowEnd >= totalCount
  ) {
    return Math.max(0, totalCount - 1);
  }

  const anchor = captureViewportAnchor({
    viewport,
    renderedNodes: Array.from(state.historyRenderedNodes, ([entryId, rendered]) => ({
      key: entryId,
      node: rendered.node,
      absoluteIndex: resolveAnchorAbsoluteIndex(state, entryId),
    })),
  });

  return typeof anchor?.absoluteIndex === 'number' && Number.isFinite(anchor.absoluteIndex)
    ? anchor.absoluteIndex
    : null;
}

function resolveHistoryNavigatorAnchorIndex(
  state: SessionAppServerControlViewState,
  viewport: HTMLDivElement,
  options: { refreshFromViewport?: boolean } = {},
): number | null {
  const retainedWindow = resolveHistoryRetainedWindowDescriptor(state.historyEntries, state);
  if (retainedWindow.totalCount <= 0) {
    return null;
  }

  if (
    state.historyNavigatorMode === 'drag-preview' &&
    typeof state.historyNavigatorDragTargetIndex === 'number'
  ) {
    return clampHistoryAbsoluteIndex(
      state.historyNavigatorDragTargetIndex,
      retainedWindow.totalCount,
    );
  }

  if (state.historyAutoScrollPinned) {
    return retainedWindow.totalCount - 1;
  }

  const concreteAnchorIndex = options.refreshFromViewport
    ? resolveHistoryNavigatorConcreteAnchorIndex(state, viewport, retainedWindow.totalCount)
    : null;
  if (concreteAnchorIndex !== null && Number.isFinite(concreteAnchorIndex)) {
    return clampHistoryAbsoluteIndex(concreteAnchorIndex, retainedWindow.totalCount);
  }

  if (
    state.historyNavigatorAnchorIndex !== null &&
    Number.isFinite(state.historyNavigatorAnchorIndex)
  ) {
    return clampHistoryAbsoluteIndex(state.historyNavigatorAnchorIndex, retainedWindow.totalCount);
  }

  const estimatedAnchorIndex = resolveHistoryNavigatorEstimatedAnchorIndex(
    state,
    viewport,
    retainedWindow.totalCount,
  );
  if (estimatedAnchorIndex !== null && Number.isFinite(estimatedAnchorIndex)) {
    return clampHistoryAbsoluteIndex(estimatedAnchorIndex, retainedWindow.totalCount);
  }

  return clampHistoryAbsoluteIndex(
    retainedWindow.windowStart + Math.floor(state.historyEntries.length / 2),
    retainedWindow.totalCount,
  );
}

function prepareHistoryProgressNavigator(
  host: HTMLDivElement,
  thumb: HTMLDivElement,
  historyCount: number,
): boolean {
  if (typeof host.removeAttribute === 'function') {
    host.removeAttribute('hidden');
  }
  host.hidden = false;

  const ready = historyCount > 0;
  host.dataset.ready = ready ? 'true' : 'false';
  host.tabIndex = ready ? 0 : -1;
  host.setAttribute('aria-disabled', ready ? 'false' : 'true');
  if (ready) {
    return false;
  }

  thumb.style.height = '';
  thumb.style.top = `${HISTORY_PROGRESS_THUMB_INSET_PX}px`;
  host.dataset.mode = 'browse';
  host.setAttribute('aria-valuemin', '0');
  host.setAttribute('aria-valuemax', '0');
  host.setAttribute('aria-valuenow', '0');
  host.setAttribute(
    'aria-valuetext',
    appServerControlText('appServerControl.history.navigator.empty', 'No history'),
  );
  return true;
}

function applyHistoryProgressNavigatorPosition(args: {
  host: HTMLDivElement;
  thumb: HTMLDivElement;
  historyCount: number;
  anchorIndex: number | null;
  mode: SessionAppServerControlViewState['historyNavigatorMode'];
}): void {
  const { host, thumb, historyCount, anchorIndex, mode } = args;
  const thumbHeightPx = resolveHistoryProgressThumbHeightPx(host);
  const trackHeightPx = Math.max(1, host.clientHeight - HISTORY_PROGRESS_THUMB_INSET_PX * 2);
  const trackTravelPx = Math.max(0, trackHeightPx - thumbHeightPx);
  const progress =
    historyCount <= 1 || anchorIndex === null ? 1 : anchorIndex / Math.max(1, historyCount - 1);
  const thumbTopPx = HISTORY_PROGRESS_THUMB_INSET_PX + Math.round(trackTravelPx * progress);

  host.dataset.mode = mode;
  host.setAttribute('aria-valuemin', '1');
  host.setAttribute('aria-valuemax', String(Math.max(1, historyCount)));
  host.setAttribute(
    'aria-valuenow',
    String(anchorIndex === null ? Math.max(1, historyCount) : anchorIndex + 1),
  );
  host.setAttribute(
    'aria-valuetext',
    anchorIndex === null
      ? `${historyCount}`
      : `${anchorIndex + 1} ${appServerControlText('appServerControl.history.navigator.of', 'of')} ${historyCount}`,
  );
  thumb.style.height = `${thumbHeightPx}px`;
  thumb.style.top = `${thumbTopPx}px`;
}

function syncHistoryProgressNavigatorUi(
  state: SessionAppServerControlViewState | undefined,
  options: { refreshAnchorFromViewport?: boolean } = {},
): void {
  const host = state?.historyProgressNav;
  const thumb = state?.historyProgressThumb;
  if (!state || !host || !thumb) {
    return;
  }

  const snapshot = state.snapshot;
  const historyCount = Math.max(snapshot?.historyCount ?? 0, state.historyEntries.length);
  if (prepareHistoryProgressNavigator(host, thumb, historyCount)) {
    return;
  }

  const viewport = state.historyViewport;
  const anchorIndex =
    viewport === null
      ? historyCount - 1
      : resolveHistoryNavigatorAnchorIndex(state, viewport, {
          refreshFromViewport: options.refreshAnchorFromViewport === true,
        });
  state.historyNavigatorAnchorIndex = anchorIndex;
  applyHistoryProgressNavigatorPosition({
    host,
    thumb,
    historyCount,
    anchorIndex,
    mode: state.historyNavigatorMode,
  });
}

export function resolveHistoryNavigatorTarget(args: {
  state: SessionAppServerControlViewState | undefined;
  clientY: number;
  thumbDragOffsetPx?: number | null;
}): { targetIndex: number; atLiveEdge: boolean } | null {
  const { state, clientY, thumbDragOffsetPx } = args;
  const host = state?.historyProgressNav;
  const snapshot = state?.snapshot;
  const historyCount = Math.max(snapshot?.historyCount ?? 0, state?.historyEntries.length ?? 0);
  if (!state || !host || !snapshot || historyCount <= 0) {
    return null;
  }

  const rect = host.getBoundingClientRect();
  const trackTopPx = rect.top + HISTORY_PROGRESS_THUMB_INSET_PX;
  const trackHeightPx = Math.max(1, rect.height - HISTORY_PROGRESS_THUMB_INSET_PX * 2);
  const thumbHeightPx = resolveHistoryProgressThumbHeightPx(host);
  const trackTravelPx = Math.max(0, trackHeightPx - thumbHeightPx);
  const normalizedProgress =
    typeof thumbDragOffsetPx === 'number' && Number.isFinite(thumbDragOffsetPx) && trackTravelPx > 0
      ? Math.max(0, Math.min(1, (clientY - thumbDragOffsetPx - trackTopPx) / trackTravelPx))
      : Math.max(0, Math.min(1, (clientY - trackTopPx) / trackHeightPx));
  const targetIndex = clampHistoryAbsoluteIndex(
    normalizedProgress * (historyCount - 1),
    historyCount,
  );
  return {
    targetIndex,
    atLiveEdge: normalizedProgress >= 1 || targetIndex >= historyCount - 1,
  };
}
function hasIntersectingRenderedHistoryEntry(
  viewport: HTMLDivElement,
  state: SessionAppServerControlViewState,
): boolean {
  if (typeof viewport.getBoundingClientRect !== 'function') {
    return true;
  }

  const viewportRect = viewport.getBoundingClientRect();
  for (const rendered of state.historyRenderedNodes.values()) {
    if (typeof rendered.node.getBoundingClientRect !== 'function') {
      continue;
    }

    const rect = rendered.node.getBoundingClientRect();
    const offsetTopPx = rect.top - viewportRect.top;
    const offsetBottomPx = rect.bottom - viewportRect.top;
    if (offsetBottomPx >= 0 && offsetTopPx <= viewport.clientHeight) {
      return true;
    }
  }

  return false;
}

function recoverViewportFromRenderedHistoryGap(
  sessionId: string,
  viewport: HTMLDivElement,
  state: SessionAppServerControlViewState,
): boolean {
  if (typeof viewport.getBoundingClientRect !== 'function') {
    return false;
  }

  const viewportRect = viewport.getBoundingClientRect();
  let nearestOffsetTopPx: number | null = null;
  let nearestDistancePx: number | null = null;
  for (const rendered of state.historyRenderedNodes.values()) {
    if (typeof rendered.node.getBoundingClientRect !== 'function') {
      continue;
    }

    const rect = rendered.node.getBoundingClientRect();
    const offsetTopPx = rect.top - viewportRect.top;
    const offsetBottomPx = rect.bottom - viewportRect.top;
    if (offsetBottomPx >= 0 && offsetTopPx <= viewport.clientHeight) {
      return false;
    }

    const distancePx =
      offsetTopPx > viewport.clientHeight
        ? offsetTopPx - viewport.clientHeight
        : Math.max(0, -offsetBottomPx);
    if (nearestDistancePx === null || distancePx < nearestDistancePx) {
      nearestDistancePx = distancePx;
      nearestOffsetTopPx = offsetTopPx;
    }
  }

  if (nearestOffsetTopPx === null) {
    return false;
  }

  traceAppServerControlHistoryScroll({
    sessionId,
    reason: 'gap-snap',
    scrollTop: viewport.scrollTop,
    clientHeight: viewport.clientHeight,
    scrollHeight: viewport.scrollHeight,
    historyWindowStart: state.snapshot?.historyWindowStart,
    historyWindowEnd: state.snapshot?.historyWindowEnd,
    historyCount: state.snapshot?.historyCount,
  });
  suppressViewportWindowSync(state);
  return syncViewportScrollPosition(viewport, viewport.scrollTop + nearestOffsetTopPx - 24);
}

function suppressViewportWindowSync(state: SessionAppServerControlViewState | undefined): void {
  if (!state) {
    return;
  }

  state.historyViewportSyncSuppressUntil =
    Date.now() + PROGRAMMATIC_HISTORY_VIEWPORT_SYNC_SUPPRESS_MS;
}

function sumHistoryEntryHeights(
  entries: readonly AppServerControlHistoryEntry[],
  start: number,
  end: number,
  resolveEntryHeight: (entry: AppServerControlHistoryEntry) => number,
): number {
  let total = 0;
  for (let index = Math.max(0, start); index < Math.min(entries.length, end); index += 1) {
    const entry = entries[index];
    if (entry) {
      total += resolveEntryHeight(entry);
    }
  }

  return total;
}

function createFallbackHistoryPlaceholderBlock(args: {
  heightPx: number;
  itemCount: number;
  direction: 'earlier' | 'later';
  label: string;
  rangeLabel: string;
}): HTMLDivElement {
  const block = document.createElement('div');
  block.className = 'agent-history-placeholder';
  block.dataset.direction = args.direction;
  block.style.height = `${Math.max(0, Math.round(args.heightPx))}px`;
  return block;
}

function formatHistoryPlaceholderRange(startIndex: number, endIndex: number): string {
  const start = startIndex + 1;
  const end = endIndex;
  if (end <= start) {
    return `${Math.max(1, start)}`;
  }

  return `${Math.max(1, start)}-${Math.max(start, end)}`;
}

function buildHistoryPlaceholderBlocks(args: {
  keyPrefix: string;
  heightPx: number;
  itemCount: number;
  direction: 'earlier' | 'later';
  label: string;
  absoluteStart: number;
}): HistoryPlaceholderBlock[] {
  const roundedHeightPx = Math.max(0, Math.round(args.heightPx));
  if (roundedHeightPx <= 0 || args.itemCount <= 0) {
    return [];
  }

  const blockCount = Math.max(
    1,
    Math.min(
      HISTORY_PLACEHOLDER_MAX_BLOCKS,
      Math.ceil(roundedHeightPx / HISTORY_PLACEHOLDER_TARGET_BLOCK_HEIGHT_PX),
      args.itemCount,
    ),
  );
  const blocks: HistoryPlaceholderBlock[] = [];
  let remainingHeightPx = roundedHeightPx;
  let remainingItemCount = args.itemCount;
  let nextAbsoluteIndex = args.absoluteStart;

  for (let index = 0; index < blockCount; index += 1) {
    const slotsLeft = blockCount - index;
    const blockItemCount = Math.max(1, Math.round(remainingItemCount / slotsLeft));
    const blockHeightPx =
      index === blockCount - 1
        ? remainingHeightPx
        : Math.max(blockItemCount, Math.round(remainingHeightPx / slotsLeft));
    const nextAbsoluteEnd = Math.min(
      args.absoluteStart + args.itemCount,
      nextAbsoluteIndex + blockItemCount,
    );
    blocks.push({
      key: `${args.keyPrefix}-${index}`,
      heightPx: blockHeightPx,
      itemCount: blockItemCount,
      direction: args.direction,
      label: args.label,
      rangeLabel: formatHistoryPlaceholderRange(nextAbsoluteIndex, nextAbsoluteEnd),
    });
    remainingHeightPx = Math.max(0, remainingHeightPx - blockHeightPx);
    remainingItemCount = Math.max(0, remainingItemCount - blockItemCount);
    nextAbsoluteIndex = nextAbsoluteEnd;
  }

  return blocks;
}

function expandHistoryVirtualWindowForPendingAnchor(args: {
  entries: readonly AppServerControlHistoryEntry[];
  virtualWindow: HistoryVirtualWindow;
  state?: SessionAppServerControlViewState | undefined;
  resolveEntryHeight: (entry: AppServerControlHistoryEntry) => number;
}): HistoryVirtualWindow {
  const { entries, virtualWindow, state, resolveEntryHeight } = args;
  const anchorEntryId =
    state?.pendingHistoryPrependAnchor?.entryId ?? state?.pendingHistoryLayoutAnchor?.entryId;
  if (!anchorEntryId) {
    return virtualWindow;
  }

  const anchorIndex = entries.findIndex((entry) => entry.id === anchorEntryId);
  if (anchorIndex < 0) {
    return virtualWindow;
  }

  const corridorStart = Math.max(0, anchorIndex - APP_SERVER_CONTROL_HISTORY_OVERSCAN_ITEMS);
  const corridorEnd = Math.min(
    entries.length,
    anchorIndex + APP_SERVER_CONTROL_HISTORY_OVERSCAN_ITEMS + 1,
  );
  const start = Math.min(virtualWindow.start, corridorStart);
  const end = Math.max(virtualWindow.end, corridorEnd);
  if (start === virtualWindow.start && end === virtualWindow.end) {
    return virtualWindow;
  }

  const extraTopHeight = sumHistoryEntryHeights(
    entries,
    start,
    virtualWindow.start,
    resolveEntryHeight,
  );
  const extraBottomHeight = sumHistoryEntryHeights(
    entries,
    virtualWindow.end,
    end,
    resolveEntryHeight,
  );

  return {
    start,
    end,
    topSpacerPx: Math.max(0, virtualWindow.topSpacerPx - extraTopHeight),
    bottomSpacerPx: Math.max(0, virtualWindow.bottomSpacerPx - extraBottomHeight),
  };
}

/* eslint-disable max-lines-per-function, complexity -- AppServerControl history render orchestration is intentionally centralized while the virtualizer contract is still moving. */
export function createAgentHistoryRender(deps: HistoryRenderDeps) {
  function isVirtualizedHistoryContext(
    state: SessionAppServerControlViewState | undefined,
    entryCount: number,
  ): boolean {
    if (!state) {
      return entryCount > HISTORY_VIRTUALIZE_AFTER;
    }

    const snapshot = state.snapshot;
    const historyCount = snapshot?.historyCount ?? entryCount;
    if (historyCount > HISTORY_VIRTUALIZE_AFTER) {
      return true;
    }

    if (
      state.historyLeadingPlaceholders.length > 0 ||
      state.historyTrailingPlaceholders.length > 0
    ) {
      return true;
    }

    if (!snapshot) {
      return false;
    }

    return snapshot.historyWindowStart > 0 || snapshot.historyWindowEnd < snapshot.historyCount;
  }

  function renderActivationView(
    sessionId: string,
    panel: HTMLDivElement,
    state: SessionAppServerControlViewState,
    entries: AppServerControlHistoryEntry[],
  ): void {
    deps.syncAgentViewPresentation(panel, state.snapshot?.provider ?? null);
    panel.dataset.agentTurnId = '';
    deps.renderRuntimeStats(panel, state.runtimeStats);
    renderComposerInterruption(panel, sessionId, [], state);
    renderHistory(panel, entries, sessionId);
  }
  function renderHistory(
    panel: HTMLDivElement,
    entries: AppServerControlHistoryEntry[],
    sessionId: string,
  ): void {
    const container = panel.querySelector<HTMLElement>('[data-agent-field="history"]');
    if (!container) {
      return;
    }

    const state = deps.getState(sessionId);
    if (state) {
      state.historyViewport = container as HTMLDivElement;
      state.historyEntries = entries;
      state.historyLastScrollMetrics ??= readHistoryScrollMetrics(
        container as HTMLDivElement,
        state,
      );
      deps.pruneAssistantMarkdownCache(state, entries);
      renderScrollToBottomControl(panel, state);
    }

    const viewport = container as HTMLDivElement;
    const metrics = readHistoryViewportMetrics(viewport, state);
    const renderPlan = buildHistoryRenderPlan(entries, metrics, state);
    reconcileHistoryRenderPlan(sessionId, viewport, renderPlan);
    const measurementChanged = state
      ? measureRenderedHistoryHeights(
          sessionId,
          state,
          renderPlan.visibleEntries,
          metrics.clientWidth,
        )
      : false;
    if (state?.snapshot) {
      traceRenderedAppServerControlHistoryWindow({
        sessionId,
        entries,
        metrics,
        state,
        resolveEntryHeight: (entry) => resolveHistoryEntryHeight(entry, state, metrics.clientWidth),
      });
    }
    finalizeRenderedHistoryState(sessionId, panel, viewport, entries, state, measurementChanged);
    syncHistoryProgressNavigatorUi(state);
  }

  function buildHistoryRenderPlan(
    entries: readonly AppServerControlHistoryEntry[],
    metrics: HistoryViewportMetrics,
    state: SessionAppServerControlViewState | undefined,
  ): HistoryRenderPlan {
    if (entries.length === 0) {
      return {
        emptyStateText: appServerControlText(
          'appServerControl.emptyHistory',
          'No history entries yet.',
        ),
        virtualWindowKey: null,
        leadingPlaceholders: [],
        trailingPlaceholders: [],
        visibleEntries: [],
      };
    }

    const resolveEntryHeight = (entry: AppServerControlHistoryEntry) =>
      resolveHistoryEntryHeight(entry, state, metrics.clientWidth);
    const windowMetrics = resolveHistoryWindowViewportMetrics(
      entries,
      state,
      metrics,
      resolveEntryHeight,
    );
    const virtualWindow = computeHistoryVirtualWindow(
      entries,
      windowMetrics.scrollTop,
      windowMetrics.clientHeight,
      windowMetrics.clientWidth,
      resolveEntryHeight,
    );
    const renderedWindow = expandHistoryVirtualWindowForPendingAnchor({
      entries,
      virtualWindow,
      state,
      resolveEntryHeight,
    });
    const retainedWindowStart = windowMetrics.retainedWindowStart;
    const retainedWindowEnd = windowMetrics.retainedWindowEnd;
    const leadingPlaceholders = [
      ...buildHistoryPlaceholderBlocks({
        keyPrefix: 'history-off-window-earlier',
        heightPx: windowMetrics.effectiveOffWindowTopSpacerPx,
        itemCount: retainedWindowStart,
        direction: 'earlier',
        label: appServerControlText(
          'appServerControl.history.placeholderEarlier',
          'Earlier history',
        ),
        absoluteStart: 0,
      }),
      ...buildHistoryPlaceholderBlocks({
        keyPrefix: 'history-retained-earlier',
        heightPx: renderedWindow.topSpacerPx,
        itemCount: renderedWindow.start,
        direction: 'earlier',
        label: appServerControlText(
          'appServerControl.history.placeholderNearbyEarlier',
          'Buffered earlier rows',
        ),
        absoluteStart: retainedWindowStart,
      }),
    ];
    const trailingPlaceholders = [
      ...buildHistoryPlaceholderBlocks({
        keyPrefix: 'history-retained-later',
        heightPx: renderedWindow.bottomSpacerPx,
        itemCount: Math.max(0, entries.length - renderedWindow.end),
        direction: 'later',
        label: appServerControlText(
          'appServerControl.history.placeholderNearbyLater',
          'Buffered later rows',
        ),
        absoluteStart: retainedWindowStart + renderedWindow.end,
      }),
      ...buildHistoryPlaceholderBlocks({
        keyPrefix: 'history-off-window-later',
        heightPx: windowMetrics.offWindowBottomSpacerPx,
        itemCount: Math.max(0, windowMetrics.totalCount - retainedWindowEnd),
        direction: 'later',
        label: appServerControlText('appServerControl.history.placeholderLater', 'Later history'),
        absoluteStart: retainedWindowEnd,
      }),
    ];

    return {
      emptyStateText: null,
      virtualWindowKey: buildHistoryVirtualWindowKey(renderedWindow),
      leadingPlaceholders,
      trailingPlaceholders,
      visibleEntries: buildVisibleHistoryEntries({
        entries,
        visibleStart: renderedWindow.start,
        visibleEnd: renderedWindow.end,
        state,
        resolveCluster: resolveArtifactCluster,
        buildSignature: buildHistoryEntrySignature,
      }),
    };
  }

  function adjustBrowseViewportIfNeeded(
    sessionId: string,
    viewport: HTMLDivElement,
    state: SessionAppServerControlViewState | undefined,
  ): boolean {
    if (!state || state.historyAutoScrollPinned) {
      return false;
    }

    const restoredAnchor =
      restorePendingHistoryAnchor(viewport, state, 'pendingHistoryPrependAnchor') ||
      restorePendingHistoryAnchor(viewport, state, 'pendingHistoryLayoutAnchor');
    if (restoredAnchor) {
      return true;
    }

    const leadingPlaceholders = state.historyLeadingPlaceholders;
    const trailingPlaceholders = state.historyTrailingPlaceholders;
    if (leadingPlaceholders.length === 0 && trailingPlaceholders.length === 0) {
      return false;
    }

    const userScrollInProgress =
      Date.now() - state.historyLastUserScrollIntentAt <= USER_HISTORY_GAP_RECOVERY_GRACE_MS;
    if (userScrollInProgress && !hasIntersectingRenderedHistoryEntry(viewport, state)) {
      traceAppServerControlHistoryScroll({
        sessionId,
        reason: 'gap-wait',
        scrollTop: viewport.scrollTop,
        clientHeight: viewport.clientHeight,
        scrollHeight: viewport.scrollHeight,
        historyWindowStart: state.snapshot?.historyWindowStart,
        historyWindowEnd: state.snapshot?.historyWindowEnd,
        historyCount: state.snapshot?.historyCount,
      });
      return false;
    }

    if (state.historyRenderedNodes.size === 0) {
      return false;
    }

    if (hasIntersectingRenderedHistoryEntry(viewport, state)) {
      return false;
    }

    return recoverViewportFromRenderedHistoryGap(sessionId, viewport, state);
  }

  function restorePendingHistoryJumpTarget(
    viewport: HTMLDivElement,
    state: SessionAppServerControlViewState,
  ): boolean {
    const snapshot = state.snapshot;
    const targetIndex = state.historyPendingJumpTargetIndex;
    if (
      snapshot === null ||
      targetIndex === null ||
      typeof viewport.getBoundingClientRect !== 'function'
    ) {
      return false;
    }

    const relativeIndex = targetIndex - snapshot.historyWindowStart;
    if (relativeIndex < 0 || relativeIndex >= state.historyEntries.length) {
      return false;
    }

    const entry = state.historyEntries[relativeIndex];
    const node = entry ? state.historyRenderedNodes.get(entry.id)?.node : null;
    if (!node || typeof node.getBoundingClientRect !== 'function') {
      return false;
    }

    const viewportRect = viewport.getBoundingClientRect();
    const rect = node.getBoundingClientRect();
    const absoluteTopPx = viewport.scrollTop + rect.top - viewportRect.top;
    const absoluteBottomPx = viewport.scrollTop + rect.bottom - viewportRect.top;
    const align: HistoryJumpAlign = state.historyPendingJumpAlign ?? 'center';
    state.historyPendingJumpTargetIndex = null;
    state.historyPendingJumpAlign = null;

    let targetScrollTop = absoluteTopPx - 18;
    if (align === 'bottom') {
      targetScrollTop = absoluteBottomPx - viewport.clientHeight + 32;
    } else if (align === 'center') {
      targetScrollTop = absoluteTopPx - Math.max(0, (viewport.clientHeight - rect.height) * 0.5);
    }

    suppressViewportWindowSync(state);
    return syncViewportScrollPosition(viewport, targetScrollTop);
  }

  function finalizeRenderedHistoryState(
    sessionId: string,
    panel: HTMLDivElement,
    viewport: HTMLDivElement,
    entries: readonly AppServerControlHistoryEntry[],
    state: SessionAppServerControlViewState | undefined,
    measurementChanged: boolean,
  ): void {
    const jumpViewportAdjusted = state ? restorePendingHistoryJumpTarget(viewport, state) : false;
    const browseViewportAdjusted = jumpViewportAdjusted
      ? false
      : adjustBrowseViewportIfNeeded(sessionId, viewport, state);
    const hasPlaceholderRanges =
      state !== undefined &&
      (state.historyLeadingPlaceholders.length > 0 || state.historyTrailingPlaceholders.length > 0);
    const viewportHasConcreteRows =
      !state || !hasPlaceholderRanges ? true : hasIntersectingRenderedHistoryEntry(viewport, state);

    if (state?.historyAutoScrollPinned) {
      state.historyLastVoidSyncScrollTop = null;
      syncPinnedHistoryViewport(sessionId, panel, viewport, entries.length);
    } else if (state && (jumpViewportAdjusted || browseViewportAdjusted)) {
      state.historyLastVoidSyncScrollTop = null;
      state.historyLastScrollMetrics = readHistoryScrollMetrics(viewport, state);
      renderScrollToBottomControl(panel, state);
      if (isVirtualizedHistoryContext(state, entries.length)) {
        deps.scheduleHistoryRender(sessionId);
      }
    }

    if (state && viewportHasConcreteRows) {
      state.historyLastVoidSyncScrollTop = null;
    }

    if (state && !state.historyAutoScrollPinned && entries.length > 0 && !viewportHasConcreteRows) {
      const roundedScrollTop = Math.round(viewport.scrollTop);
      if (state.historyLastVoidSyncScrollTop !== roundedScrollTop) {
        state.historyLastVoidSyncScrollTop = roundedScrollTop;
        deps.syncViewportHistoryWindow?.(sessionId);
      }
    }

    if (measurementChanged && isVirtualizedHistoryContext(state, entries.length)) {
      deps.scheduleHistoryRender(sessionId);
    }
  }

  function syncPinnedHistoryViewport(
    sessionId: string,
    panel: HTMLDivElement,
    viewport: HTMLDivElement,
    entryCount: number,
  ): void {
    const didAutoScroll = syncViewportScrollPosition(
      viewport,
      viewport.scrollHeight - viewport.clientHeight,
    );
    const currentAfterScroll = deps.getState(sessionId);
    if (didAutoScroll && isVirtualizedHistoryContext(currentAfterScroll, entryCount)) {
      deps.scheduleHistoryRender(sessionId);
    }

    if (!currentAfterScroll) {
      return;
    }

    setHistoryScrollMode(currentAfterScroll, 'follow');
    currentAfterScroll.historyNavigatorMode = 'follow-live';
    currentAfterScroll.historyNavigatorDragTargetIndex = null;
    currentAfterScroll.historyLastScrollMetrics = readHistoryScrollMetrics(
      viewport,
      currentAfterScroll,
    );
    renderScrollToBottomControl(panel, currentAfterScroll);
  }

  function buildHistoryEntrySignature(
    entry: AppServerControlHistoryEntry,
    cluster: ArtifactClusterInfo | null,
    state: SessionAppServerControlViewState | undefined,
    showAssistantBadge: boolean,
  ): string {
    return [
      entry.kind,
      entry.tone,
      resolveHistoryBadgeLabel(entry.kind, state?.snapshot?.provider),
      entry.title,
      entry.body,
      entry.meta,
      entry.pending ? '1' : '0',
      entry.live ? '1' : '0',
      entry.busyIndicator ? '1' : '0',
      entry.busyElapsedText ?? '',
      entry.turnDurationNote ? '1' : '0',
      showAssistantBadge ? '1' : '0',
      entry.sourceItemType ?? '',
      entry.commandText ?? '',
      (entry.commandOutputTail ?? []).join('\n'),
      entry.kind === 'tool' ? String(resolveToolCallOutputLineLimit()) : '',
      buildHistoryAttachmentToken(entry),
      buildAssistantPreviewToken(entry, state),
      buildHistoryActionToken(entry),
      buildHistoryClusterToken(cluster),
      resolveHistoryEntryBusyToken(entry, state),
      buildHistoryRequestToken(entry, state),
    ].join('||');
  }

  function resolveHistoryEntryBusyToken(
    entry: AppServerControlHistoryEntry,
    state: SessionAppServerControlViewState | undefined,
  ): string {
    return state?.activationActionBusy === true && (entry.actions?.length ?? 0) > 0
      ? 'busy'
      : 'idle';
  }

  function buildHistoryRequestToken(
    entry: AppServerControlHistoryEntry,
    state: SessionAppServerControlViewState | undefined,
  ): string {
    if (entry.kind !== 'request' || !entry.requestId) {
      return '';
    }

    const request = state?.snapshot?.requests.find(
      (candidate) => candidate.requestId === entry.requestId,
    );
    if (!request) {
      return 'missing';
    }

    return [
      request.kind,
      request.state,
      request.decision ?? '',
      request.detail ?? '',
      request.updatedAt,
      request.answers.map((answer) => `${answer.questionId}:${answer.answers.join(',')}`).join('|'),
    ].join('::');
  }

  function reconcileHistoryRenderPlan(
    sessionId: string,
    container: HTMLDivElement,
    plan: HistoryRenderPlan,
  ): void {
    const state = deps.getState(sessionId);
    if (!state) {
      return;
    }

    if (plan.emptyStateText) {
      const emptyNode = ensureEmptyHistoryNode(state, plan.emptyStateText);
      syncOrderedChildren(container, [emptyNode]);
      state.historyMeasurementObserver?.disconnect();
      state.historyRenderedNodes.clear();
      state.historyLeadingPlaceholders = [];
      state.historyTrailingPlaceholders = [];
      return;
    }

    state.historyEmptyState = null;
    const nextChildren: HTMLElement[] = [];
    nextChildren.push(
      ...resolveHistoryPlaceholderNodes(state, 'leading', plan.leadingPlaceholders),
    );

    const visibleKeys = new Set<string>();
    for (const visibleEntry of plan.visibleEntries) {
      visibleKeys.add(visibleEntry.key);
      nextChildren.push(resolveRenderedHistoryNode(sessionId, state, visibleEntry));
    }

    for (const cacheKey of state.historyRenderedNodes.keys()) {
      if (!visibleKeys.has(cacheKey)) {
        state.historyRenderedNodes.delete(cacheKey);
      }
    }

    nextChildren.push(
      ...resolveHistoryPlaceholderNodes(state, 'trailing', plan.trailingPlaceholders),
    );

    syncOrderedChildren(container, nextChildren);
    state.historyLastVirtualWindowKey = plan.virtualWindowKey;
  }

  function ensureEmptyHistoryNode(
    state: SessionAppServerControlViewState,
    text: string,
  ): HTMLDivElement {
    if (!state.historyEmptyState) {
      const empty = document.createElement('div');
      empty.className = 'agent-history-empty';
      state.historyEmptyState = empty;
    }
    state.historyEmptyState.textContent = text;
    return state.historyEmptyState;
  }

  function ensureHistoryPlaceholderNode(
    state: SessionAppServerControlViewState,
    position: 'leading' | 'trailing',
    blockIndex: number,
    block: HistoryPlaceholderBlock,
  ): HTMLDivElement {
    const store =
      position === 'leading' ? state.historyLeadingPlaceholders : state.historyTrailingPlaceholders;
    const existing = store[blockIndex];
    const placeholderFactory =
      deps.createHistoryPlaceholderBlock ?? createFallbackHistoryPlaceholderBlock;
    const node =
      existing ??
      (placeholderFactory({
        heightPx: block.heightPx,
        itemCount: block.itemCount,
        direction: block.direction,
        label: block.label,
        rangeLabel: block.rangeLabel,
      }) as HTMLDivElement);
    node.className = 'agent-history-placeholder';
    node.dataset.direction = block.direction;
    node.dataset.placeholderKey = block.key;
    node.style.height = `${Math.max(0, Math.round(block.heightPx))}px`;
    const selectorRoot = node as unknown as Record<string, unknown>;
    if (typeof selectorRoot['querySelector'] !== 'function') {
      store[blockIndex] = node;
      return node;
    }

    // Existing placeholder nodes are reused, so refresh their visible labels.
    const title = node.querySelector<HTMLElement>('.agent-history-placeholder-title');
    if (title) {
      title.textContent = block.label;
    }

    const meta = node.querySelector<HTMLElement>('.agent-history-placeholder-meta');
    if (meta) {
      meta.textContent = `${block.itemCount} items${block.rangeLabel ? ` • ${block.rangeLabel}` : ''}`;
    }
    node.setAttribute(
      'aria-label',
      `${block.label}: ${block.itemCount} items represented by an estimated placeholder block`,
    );
    store[blockIndex] = node;
    return node;
  }

  function resolveHistoryPlaceholderNodes(
    state: SessionAppServerControlViewState,
    position: 'leading' | 'trailing',
    blocks: readonly HistoryPlaceholderBlock[],
  ): HTMLDivElement[] {
    const store =
      position === 'leading' ? state.historyLeadingPlaceholders : state.historyTrailingPlaceholders;
    const nodes = blocks.map((block, index) =>
      ensureHistoryPlaceholderNode(state, position, index, block),
    );
    store.length = blocks.length;
    return nodes;
  }

  function resolveRenderedHistoryNode(
    sessionId: string,
    state: SessionAppServerControlViewState,
    visibleEntry: HistoryVisibleEntry,
  ): HTMLElement {
    const existing = state.historyRenderedNodes.get(visibleEntry.key);
    if (existing && existing.entry.busyIndicator && visibleEntry.entry.busyIndicator) {
      deps.syncBusyIndicatorEntry?.(existing.node, visibleEntry.entry);
      existing.node.dataset.appServerControlEntryId = visibleEntry.key;
      existing.signature = visibleEntry.signature;
      existing.entry = visibleEntry.entry;
      existing.cluster = visibleEntry.cluster;
      existing.lastMeasuredWidthBucket = null;
      return existing.node;
    }

    if (existing && existing.signature === visibleEntry.signature) {
      existing.node.dataset.appServerControlEntryId = visibleEntry.key;
      return existing.node;
    }

    const node = deps.createHistoryEntry(visibleEntry.entry, sessionId, {
      artifactCluster: visibleEntry.cluster,
      showAssistantBadge: visibleEntry.showAssistantBadge,
    });
    node.dataset.appServerControlEntryId = visibleEntry.key;
    state.historyRenderedNodes.set(visibleEntry.key, {
      node,
      signature: visibleEntry.signature,
      entry: visibleEntry.entry,
      cluster: visibleEntry.cluster,
      lastMeasuredWidthBucket: null,
    });
    return node;
  }

  function measureRenderedHistoryHeights(
    sessionId: string,
    state: SessionAppServerControlViewState,
    visibleEntries: readonly HistoryVisibleEntry[],
    clientWidth: number,
  ): boolean {
    let changed = false;
    const widthBucket = state.historyMeasuredWidthBucket;
    for (const visibleEntry of visibleEntries) {
      const rendered = state.historyRenderedNodes.get(visibleEntry.key);
      if (!rendered?.node || typeof rendered.node.getBoundingClientRect !== 'function') {
        continue;
      }

      if (rendered.lastMeasuredWidthBucket === widthBucket) {
        continue;
      }

      changed =
        recordHistoryMeasuredHeight(
          state,
          visibleEntry.key,
          rendered.node.getBoundingClientRect().height,
          clientWidth,
        ) || changed;
      rendered.lastMeasuredWidthBucket = state.historyMeasuredWidthBucket;
    }

    syncHistoryMeasurementObserver({
      sessionId,
      state,
      visibleEntries,
      getState: deps.getState,
      scheduleHistoryRender: deps.scheduleHistoryRender,
    });
    return changed;
  }

  function restorePendingHistoryAnchor(
    viewport: HTMLDivElement,
    state: SessionAppServerControlViewState,
    key: 'pendingHistoryPrependAnchor' | 'pendingHistoryLayoutAnchor',
  ): boolean {
    const anchor = state[key];
    if (!anchor) {
      return false;
    }

    state[key] = null;
    setHistoryScrollMode(state, 'browse');
    suppressViewportWindowSync(state);
    return restoreViewportAnchor({
      viewport,
      anchor: {
        key: anchor.entryId,
        topOffsetPx: anchor.topOffsetPx,
        absoluteIndex: anchor.absoluteIndex,
      },
      resolveNode: (entryId) => state.historyRenderedNodes.get(entryId)?.node,
    });
  }

  function captureHistoryViewportAnchor(
    state: SessionAppServerControlViewState,
    key:
      | 'pendingHistoryPrependAnchor'
      | 'pendingHistoryLayoutAnchor' = 'pendingHistoryPrependAnchor',
  ): boolean {
    const viewport = state.historyViewport;
    if (!viewport) {
      state[key] = null;
      return false;
    }

    const anchor = captureViewportAnchor({
      viewport,
      renderedNodes: Array.from(state.historyRenderedNodes, ([entryId, rendered]) => ({
        key: entryId,
        node: rendered.node,
        absoluteIndex: resolveAnchorAbsoluteIndex(state, entryId),
      })),
    });
    state[key] = anchor
      ? {
          entryId: anchor.key,
          topOffsetPx: anchor.topOffsetPx,
          absoluteIndex: anchor.absoluteIndex,
        }
      : null;
    return anchor !== null;
  }

  function syncOrderedChildren(container: HTMLElement, nodes: readonly HTMLElement[]): void {
    let anchor = container.firstChild;
    for (const node of nodes) {
      if (anchor !== node) {
        container.insertBefore(node, anchor);
      } else {
        anchor = anchor.nextSibling;
        continue;
      }

      anchor = node.nextSibling;
    }

    while (container.childNodes.length > nodes.length) {
      container.removeChild(container.lastChild as ChildNode);
    }
  }

  function renderScrollToBottomControl(
    panel: HTMLDivElement,
    state: SessionAppServerControlViewState,
  ): void {
    const button = panel.querySelector<HTMLButtonElement>('[data-agent-field="scroll-to-bottom"]');
    if (!button) {
      return;
    }

    button.textContent = appServerControlText('appServerControl.scrollToBottom', 'Back to bottom');
    button.hidden =
      state.historyAutoScrollPinned ||
      state.historyEntries.length === 0 ||
      state.activationState === 'failed';
  }

  function renderComposerInterruption(
    panel: HTMLDivElement,
    sessionId: string,
    requests: readonly AppServerControlHistoryRequestSummary[],
    state: SessionAppServerControlViewState,
  ): void {
    const shell = panel.querySelector<HTMLElement>('[data-agent-field="composer-shell"]');
    const host = panel.querySelector<HTMLElement>('[data-agent-field="composer-interruption"]');
    if (!shell || !host) {
      return;
    }

    const activeRequest = findActiveComposerRequest(requests);
    if (!activeRequest) {
      shell.hidden = true;
      host.hidden = true;
      host.replaceChildren();
      return;
    }

    shell.hidden = false;
    host.hidden = false;
    host.replaceChildren(
      deps.createRequestActionBlock(
        sessionId,
        activeRequest,
        state.requestBusyIds.has(activeRequest.requestId),
        state,
        'composer',
      ),
    );
  }

  function syncRequestInteractionState(
    state: SessionAppServerControlViewState,
    requests: readonly AppServerControlHistoryRequestSummary[],
  ): void {
    const activeRequestIds = new Set(
      requests.filter((request) => request.state === 'open').map((request) => request.requestId),
    );

    for (const requestId of Object.keys(state.requestDraftAnswersById)) {
      if (!activeRequestIds.has(requestId)) {
        Reflect.deleteProperty(state.requestDraftAnswersById, requestId);
      }
    }

    for (const requestId of Object.keys(state.requestQuestionIndexById)) {
      if (!activeRequestIds.has(requestId)) {
        Reflect.deleteProperty(state.requestQuestionIndexById, requestId);
      }
    }
  }

  function scrollHistoryToBottom(sessionId: string, behavior: ScrollBehavior = 'auto'): void {
    const state = deps.getState(sessionId);
    const viewport = state?.historyViewport;
    if (!state || !viewport) {
      return;
    }

    setHistoryScrollMode(state, 'follow');
    state.historyNavigatorMode = 'follow-live';
    state.historyNavigatorDragTargetIndex = null;
    state.historyLastUserScrollIntentAt = 0;
    state.historyViewportSyncPending = false;
    state.historyViewportSyncForcePending = false;
    state.historyViewportSyncQueuedDuringRefresh = false;
    state.historyViewportSyncSuppressUntil = 0;
    state.pendingHistoryPrependAnchor = null;
    state.pendingHistoryLayoutAnchor = null;
    state.historyLastVoidSyncScrollTop = null;
    viewport.scrollTo({
      top: viewport.scrollHeight,
      behavior,
    });
    state.historyLastScrollMetrics = readHistoryScrollMetrics(viewport, state);
    renderScrollToBottomControl(state.panel, state);
  }

  function shouldRenderForViewportScroll(state: SessionAppServerControlViewState): boolean {
    const viewport = state.historyViewport;
    if (!viewport || !isVirtualizedHistoryContext(state, state.historyEntries.length)) {
      return false;
    }

    if (
      state.historyRenderedNodes.size > 0 &&
      (state.historyLeadingPlaceholders.length > 0 ||
        state.historyTrailingPlaceholders.length > 0) &&
      !hasIntersectingRenderedHistoryEntry(viewport, state)
    ) {
      return true;
    }

    const metrics = readHistoryViewportMetrics(viewport, state);
    const windowMetrics = resolveHistoryWindowViewportMetrics(
      state.historyEntries,
      state,
      metrics,
      (entry) => resolveHistoryEntryHeight(entry, state, metrics.clientWidth),
    );
    const virtualWindow = computeHistoryVirtualWindow(
      state.historyEntries,
      windowMetrics.scrollTop,
      windowMetrics.clientHeight,
      windowMetrics.clientWidth,
      (entry) => resolveHistoryEntryHeight(entry, state, windowMetrics.clientWidth),
    );
    return buildHistoryVirtualWindowKey(virtualWindow) !== state.historyLastVirtualWindowKey;
  }

  function getViewportCenteredHistoryWindowRequest(
    state: SessionAppServerControlViewState,
    options: {
      fetchAheadItems: number;
      anchorAbsoluteIndex?: number | null;
    },
  ): { startIndex: number; count: number } | null {
    const viewport = state.historyViewport;
    const snapshot = state.snapshot;
    if (!viewport || !snapshot || state.historyEntries.length === 0) {
      return null;
    }

    const metrics = readHistoryViewportMetrics(viewport, state);
    const resolveEntryHeight = (entry: AppServerControlHistoryEntry) =>
      resolveHistoryEntryHeight(entry, state, metrics.clientWidth);
    const retainedWindow = resolveHistoryRetainedWindowDescriptor(state.historyEntries, state);
    const request = resolveViewportCenteredWindowRequest({
      items: state.historyEntries,
      viewportMetrics: metrics,
      retainedWindow,
      fetchAheadItems: options.fetchAheadItems,
      resolveItemSize: (entry) => resolveEntryHeight(entry),
      observedSizes: state.historyObservedHeights.values(),
      anchorAbsoluteIndex: options.anchorAbsoluteIndex,
      resolveEstimatedItemSize: (entry) =>
        resolveHistoryEstimatedEntryHeight(entry, metrics.clientWidth),
    });
    return request;
  }

  function syncViewportOffset(sessionId: string): void {
    const state = deps.getState(sessionId);
    if (!state) {
      return;
    }

    syncHistoryProgressNavigatorUi(state, { refreshAnchorFromViewport: true });
  }

  return {
    captureHistoryViewportAnchor,
    getViewportCenteredHistoryWindowRequest,
    renderActivationView,
    renderComposerInterruption,
    renderHistory,
    renderScrollToBottomControl,
    readHistoryScrollMetrics: (
      container: HTMLDivElement,
      stateArg?: SessionAppServerControlViewState,
    ) => readHistoryScrollMetrics(container, stateArg),
    scrollHistoryToBottom,
    shouldRenderForViewportScroll,
    suppressActiveComposerRequestEntries,
    syncViewportOffset,
    syncRequestInteractionState,
    updateBusyIndicatorElapsed: (sessionId: string, elapsedText: string) =>
      updateBusyIndicatorElapsedInState(deps.getState(sessionId), elapsedText),
  };
}

export function suppressActiveComposerRequestEntries(
  entries: readonly AppServerControlHistoryEntry[],
  requests: readonly AppServerControlHistoryRequestSummary[],
): AppServerControlHistoryEntry[] {
  const activeRequest = findActiveComposerRequest(requests);
  if (!activeRequest || activeRequest.state !== 'open') {
    return [...entries];
  }

  return entries.filter(
    (entry) => entry.kind !== 'request' || entry.requestId !== activeRequest.requestId,
  );
}

function findActiveComposerRequest(
  requests: readonly AppServerControlHistoryRequestSummary[],
): AppServerControlHistoryRequestSummary | null {
  const openRequests = requests.filter(
    (request) => request.state === 'open' && request.kind !== 'interview',
  );
  if (openRequests.length === 0) {
    return null;
  }

  return (
    openRequests
      .slice()
      .sort(
        (left, right) => new Date(right.updatedAt).getTime() - new Date(left.updatedAt).getTime(),
      )[0] ?? null
  );
}

function resolveArtifactCluster(
  entries: readonly AppServerControlHistoryEntry[],
  index: number,
): ArtifactClusterInfo | null {
  const entry = entries[index];
  if (!entry || !isArtifactClusterKind(entry.kind)) {
    return null;
  }

  const start = findArtifactClusterBoundary(entries, index, -1);
  const end = findArtifactClusterBoundary(entries, index, 1);

  const count = end - start + 1;
  const position =
    count === 1 ? 'single' : index === start ? 'start' : index === end ? 'end' : 'middle';
  const clusterEntries = entries.slice(start, end + 1);
  const onlyTools = clusterEntries.every((candidate) => candidate.kind === 'tool');
  return {
    position,
    label:
      position === 'start' && !onlyTools
        ? appServerControlText('appServerControl.cluster.workLog', 'Work log')
        : null,
    count,
    onlyTools,
  };
}

function isArtifactClusterKind(kind: AppServerControlHistoryEntry['kind']): boolean {
  return ['tool', 'reasoning', 'plan', 'diff'].includes(kind);
}

function findArtifactClusterBoundary(
  entries: readonly AppServerControlHistoryEntry[],
  index: number,
  direction: -1 | 1,
): number {
  let boundary = index;
  for (;;) {
    const nextIndex = boundary + direction;
    if (nextIndex < 0 || nextIndex >= entries.length) {
      return boundary;
    }
    const nextEntry = entries[nextIndex];
    if (!nextEntry || !isArtifactClusterKind(nextEntry.kind)) {
      return boundary;
    }
    boundary = nextIndex;
  }
}
/* eslint-enable max-lines-per-function, complexity */
/* eslint-enable max-lines */
