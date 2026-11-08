import { t } from '../i18n';
import { resolveUnderfilledHistoryWindowRequest } from './historyWindowSizing';
import {
  alignHistoryJumpTarget,
  buildHistoryJumpVirtualWindow,
  clearHistoryJumpTarget,
  resolveHistoryJumpEntryIndex,
} from './historyJump';
import {
  HISTORY_PROGRESS_THUMB_INSET_PX,
  resolveCanonicalHistoryCount,
  measureHistoryNavigatorRange,
  measureHistoryNavigatorAnchor,
  resolveHistoryNavigatorGeometry,
  resolveHistoryNavigatorPointerIndex,
  type HistoryNavigatorMapping,
} from './historyNavigator';
import { resolveHistoryBadgeLabel } from './activationHelpers';
import {
  retainHistoryMeasurementsAcrossWindowShift,
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
import { isProposedPlanActionDisabled, resolveToolCallOutputLineLimit } from './historyDom';
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
  resolveKernelWindowRequest,
  resolveResizeObserverBorderBoxSize,
  resolveScrollCompensationDelta,
  restoreViewportAnchor,
  syncViewportScrollPosition,
  type VirtualizerAnchor,
  type VirtualizerMeasuredItemChange,
} from '../../utils/virtualizer';
import type {
  ArtifactClusterInfo,
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
  syncHistoryEntry?: (
    node: HTMLElement,
    entry: AppServerControlHistoryEntry,
    sessionId: string,
    options?: {
      artifactCluster?: ArtifactClusterInfo | null;
      showAssistantBadge?: boolean;
    },
  ) => void;
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
  pruneHistoryMarkdownCache: (
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

export function resolveHistoryRetainedWindowDescriptor(
  _entries: readonly AppServerControlHistoryEntry[],
  state: SessionAppServerControlViewState | undefined,
): {
  windowStart: number;
  windowEnd: number;
  totalCount: number;
} {
  const snapshot = state?.snapshot;
  const totalCount = resolveCanonicalHistoryCount(snapshot);
  const windowStart = Math.max(0, Math.min(totalCount, snapshot?.historyWindowStart ?? 0));
  return {
    windowStart,
    windowEnd: Math.max(
      windowStart,
      Math.min(totalCount, snapshot?.historyWindowEnd ?? windowStart),
    ),
    totalCount,
  };
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

function readHistoryReaderAnchor(state: SessionAppServerControlViewState) {
  const viewport = state.historyViewport;
  if (!viewport || viewport.clientWidth <= 0 || viewport.clientHeight <= 0) return null;
  return toHistoryViewportAnchor(
    captureViewportAnchor({
      viewport,
      renderedNodes: Array.from(state.historyRenderedNodes, ([entryId, rendered]) => ({
        key: entryId,
        node: rendered.node,
        absoluteIndex: resolveAnchorAbsoluteIndex(state, entryId),
      })),
    }),
  );
}

/** Call only after a settled render/restore or a scroll at the confirmed size. */
export function confirmHistoryReaderAnchor(state: SessionAppServerControlViewState): void {
  if (state.historyAutoScrollPinned || state.historyPendingJumpTargetIndex != null) {
    state.historyReaderAnchor = null;
    return;
  }
  if (state.pendingHistoryPrependAnchor || state.pendingHistoryLayoutAnchor) return;
  const viewport = state.historyViewport;
  const anchor = readHistoryReaderAnchor(state);
  if (!viewport || !anchor) return;
  state.historyReaderAnchor = {
    ...anchor,
    scrollTop: viewport.scrollTop,
    width: viewport.clientWidth,
    height: viewport.clientHeight,
  };
}

/** ResizeObserver is post-reflow: never capture its already shifted DOM. */
export function prepareHistoryReaderResize(state: SessionAppServerControlViewState): boolean {
  const viewport = state.historyViewport;
  const anchor = state.historyReaderAnchor;
  if (
    !viewport ||
    !anchor ||
    viewport.clientWidth <= 0 ||
    viewport.clientHeight <= 0 ||
    (anchor.width === viewport.clientWidth && anchor.height === viewport.clientHeight) ||
    state.historyAutoScrollPinned ||
    state.historyPendingJumpTargetIndex != null ||
    state.pendingHistoryPrependAnchor ||
    state.pendingHistoryLayoutAnchor
  )
    return false;
  state.pendingHistoryLayoutAnchor = {
    entryId: anchor.entryId,
    topOffsetPx: anchor.topOffsetPx,
    absoluteIndex: anchor.absoluteIndex,
  };
  return true;
}

export function prepareHistoryViewportResize(state: SessionAppServerControlViewState): boolean {
  const viewport = state.historyViewport;
  if (!viewport) return false;
  const next = { width: viewport.clientWidth, height: viewport.clientHeight };
  const previous = state.historyViewportSize;
  if (previous && previous.width === next.width && previous.height === next.height) return false;
  prepareHistoryReaderResize(state);
  restoreHistoryReaderBeforePaint(state);
  state.historyViewportSize = next;
  return true;
}

export function restoreHistoryReaderBeforePaint(state: SessionAppServerControlViewState): void {
  const viewport = state.historyViewport;
  const anchor = state.historyReaderAnchor;
  if (
    !viewport ||
    !anchor ||
    state.historyAutoScrollPinned ||
    state.historyPendingJumpTargetIndex != null ||
    state.pendingHistoryPrependAnchor ||
    !state.pendingHistoryLayoutAnchor ||
    (anchor.width === viewport.clientWidth && anchor.height === viewport.clientHeight)
  )
    return;
  const node = state.historyRenderedNodes.get(state.pendingHistoryLayoutAnchor.entryId)?.node;
  if (!node || !node.isConnected) return;
  // The other RO can still carry old-width measurements. Keep recording them,
  // but do not sum a second scroll correction before the render remeasures all rows.
  state.historyReaderResizeMeasurementPending = true;
  restorePendingHistoryAnchor(viewport, state, 'pendingHistoryLayoutAnchor');
}

export function completeHistoryReaderMeasurements(state: SessionAppServerControlViewState): void {
  state.historyReaderResizeMeasurementPending = false;
  confirmHistoryReaderAnchor(state);
}

export function recordHistoryReaderScroll(
  state: SessionAppServerControlViewState,
  userInitiated: boolean,
): boolean {
  const viewport = state.historyViewport;
  if (!viewport) return false;
  if (state.historyAutoScrollPinned || state.historyPendingJumpTargetIndex != null) {
    state.historyReaderAnchor = null;
    return false;
  }
  if (state.pendingHistoryPrependAnchor) return false;
  const anchor = state.historyReaderAnchor;
  if (anchor && userInitiated) {
    // Reflow may clamp scrollTop before its queued scroll event, even while a
    // recent input timestamp is still active. Only movement beyond that clamp
    // belongs to wheel/touch/key momentum.
    const baseline = Math.max(
      0,
      Math.min(anchor.scrollTop, viewport.scrollHeight - viewport.clientHeight),
    );
    const delta = viewport.scrollTop - baseline;
    anchor.topOffsetPx -= delta;
    anchor.scrollTop = viewport.scrollTop;
    if (state.pendingHistoryLayoutAnchor) state.pendingHistoryLayoutAnchor.topOffsetPx -= delta;
  }
  if (prepareHistoryReaderResize(state)) return true;
  // Scroll can precede the viewport RO. Do not confirm post-reflow geometry.
  if (anchor && (anchor.width !== viewport.clientWidth || anchor.height !== viewport.clientHeight))
    return false;
  confirmHistoryReaderAnchor(state);
  return false;
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

export function restorePendingHistoryAnchor(
  viewport: HTMLDivElement,
  state: SessionAppServerControlViewState,
  key: 'pendingHistoryPrependAnchor' | 'pendingHistoryLayoutAnchor',
): boolean {
  const anchor = state[key];
  if (!anchor) {
    return false;
  }
  const node = state.historyRenderedNodes.get(anchor.entryId)?.node;
  if (!node || !node.isConnected || typeof node.getBoundingClientRect !== 'function') return false;

  state[key] = null;
  if (key === 'pendingHistoryPrependAnchor') state.pendingHistoryLayoutAnchor = null;
  setHistoryScrollMode(state, 'browse');
  suppressViewportWindowSync(state);
  const adjusted = restoreViewportAnchor({
    viewport,
    anchor: {
      key: anchor.entryId,
      topOffsetPx: anchor.topOffsetPx,
      absoluteIndex: anchor.absoluteIndex,
    },
    resolveNode: (entryId) => state.historyRenderedNodes.get(entryId)?.node,
  });
  state.historyLastScrollMetrics = readHistoryScrollMetrics(viewport, state);
  confirmHistoryReaderAnchor(state);
  return adjusted;
}

export function resolveMeasurementBrowseAnchorIndex(
  state: SessionAppServerControlViewState,
  viewport: HTMLDivElement,
): number | null {
  if (
    state.historyAutoScrollPinned ||
    state.historyReaderResizeMeasurementPending ||
    state.pendingHistoryPrependAnchor !== null ||
    state.pendingHistoryLayoutAnchor !== null
  ) {
    return null;
  }

  // ResizeObserver runs after reflow; a fresh DOM anchor may already be the expanded row.
  const stableAnchor = state.historyNavigatorAnchorIndex;
  if (stableAnchor !== null && Number.isFinite(stableAnchor)) {
    return stableAnchor;
  }
  return (
    captureViewportAnchor({
      viewport,
      renderedNodes: Array.from(state.historyRenderedNodes, ([entryId, rendered]) => ({
        key: entryId,
        node: rendered.node,
        absoluteIndex: resolveAnchorAbsoluteIndex(state, entryId),
      })),
    })?.absoluteIndex ?? null
  );
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
      resolveResizeObserverBorderBoxSize(record),
      viewport.clientWidth,
    );
    if (!sizeChanged || entry === null || previousSize === null) {
      continue;
    }

    const nextSize = resolveHistoryViewportEntryHeight(entry, state, viewport.clientWidth);
    changes.push({
      absoluteIndex: Math.max(0, Math.round(entry.order) - 1),
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

export function syncHistoryMeasurementObserver(args: {
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

    prepareHistoryReaderResize(current);
    restoreHistoryReaderBeforePaint(current);
    const browseAnchorIndex = resolveMeasurementBrowseAnchorIndex(current, viewport);
    const measurementChanges = collectHistoryMeasurementChanges({
      state: current,
      viewport,
      records,
    });
    if (measurementChanges.length === 0) {
      return;
    }

    if (current.historyPendingJumpTargetIndex != null) {
      suppressViewportWindowSync(current);
      alignHistoryJumpTarget(viewport, current);
      current.historyLastScrollMetrics = readHistoryScrollMetrics(viewport, current);
      args.scheduleHistoryRender(args.sessionId);
      return;
    }

    const compensationDelta = resolveScrollCompensationDelta({
      changes: measurementChanges,
      anchorAbsoluteIndex: browseAnchorIndex ?? undefined,
    });
    const activelyScrollingEarlier =
      Date.now() - current.historyLastUserScrollIntentAt <= USER_HISTORY_GAP_RECOVERY_GRACE_MS &&
      current.historyLastUserScrollDirection < 0;
    if (!current.historyAutoScrollPinned && !activelyScrollingEarlier && compensationDelta !== 0) {
      suppressViewportWindowSync(current);
      syncViewportScrollPosition(viewport, viewport.scrollTop + compensationDelta);
      current.historyLastScrollMetrics = readHistoryScrollMetrics(viewport, current);
      confirmHistoryReaderAnchor(current);
    }

    args.scheduleHistoryRender(args.sessionId);
  });

  args.state.historyMeasurementObserver.disconnect();
  for (const visibleEntry of args.visibleEntries) {
    const node = args.state.historyRenderedNodes.get(visibleEntry.key)?.node;
    if (node) {
      args.state.historyMeasurementObserver.observe(node, { box: 'border-box' });
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
  const entryOrder = relativeIndex >= 0 ? state.historyEntries[relativeIndex]?.order : undefined;
  if (typeof entryOrder === 'number' && Number.isFinite(entryOrder) && entryOrder > 0) {
    return clampHistoryAbsoluteIndex(
      Math.floor(entryOrder) - 1,
      resolveCanonicalHistoryCount(state.snapshot),
    );
  }

  const historyWindowStart = resolveHistoryRetainedWindowDescriptor(
    state.historyEntries,
    state,
  ).windowStart;
  return historyWindowStart;
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
  _state: SessionAppServerControlViewState | undefined,
): string {
  return (entry.imagePreviews ?? []).map((preview) => preview.resolvedPath).join('|');
}

function buildHistoryActionToken(entry: AppServerControlHistoryEntry): string {
  return (entry.actions ?? [])
    .map((action) => [action.id, action.label, action.style, action.busyLabel ?? ''].join(':'))
    .join('|');
}

function readHistoryNavigatorRows(state: SessionAppServerControlViewState) {
  const canonicalOrders = new Map(
    (state.snapshot?.history ?? []).map((entry) => [entry.entryId, entry.order]),
  );
  return Array.from(state.historyRenderedNodes)
    .filter(([entryId]) => canonicalOrders.has(entryId))
    .map(([entryId, rendered]) => {
      const rect = rendered.node.getBoundingClientRect();
      return {
        absoluteIndex: Math.max(0, (canonicalOrders.get(entryId) ?? 1) - 1),
        top: rect.top,
        bottom: rect.bottom,
      };
    });
}

function readHistoryNavigatorVisibleRange(state: SessionAppServerControlViewState) {
  const viewport = state.historyViewport;
  return viewport && viewport.clientHeight > 0
    ? measureHistoryNavigatorRange(
        viewport.getBoundingClientRect().top,
        viewport.clientHeight,
        readHistoryNavigatorRows(state),
      )
    : null;
}

function clampHistoryAbsoluteIndex(index: number, historyCount: number): number {
  if (historyCount <= 0) {
    return 0;
  }

  return Math.max(0, Math.min(historyCount - 1, Math.round(index)));
}

function readHistoryViewportMetrics(
  container: HTMLDivElement,
  _state?: SessionAppServerControlViewState,
): HistoryViewportMetrics {
  return {
    scrollTop: container.scrollTop,
    clientHeight: container.clientHeight,
    clientWidth: container.clientWidth,
  };
}

function readHistoryScrollMetrics(
  container: HTMLDivElement,
  _state?: SessionAppServerControlViewState,
): HistoryScrollMetrics {
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

  const canonicalIds = new Set((state.snapshot?.history ?? []).map((entry) => entry.entryId));
  const visibleCanonicalEntries = state.historyEntries
    .slice(visibleRange.start, visibleRange.end)
    .filter((entry) => canonicalIds.has(entry.id));
  const firstVisibleEntry = visibleCanonicalEntries[0];
  const lastVisibleEntry = visibleCanonicalEntries[visibleCanonicalEntries.length - 1];
  if (!firstVisibleEntry || !lastVisibleEntry) {
    return null;
  }

  const visibleStart = Math.max(0, firstVisibleEntry.order - 1);
  const visibleEnd = Math.max(visibleStart, lastVisibleEntry.order - 1);

  const retainedWindow = resolveHistoryRetainedWindowDescriptor(state.historyEntries, state);
  if (
    visibleRange.end >= state.historyEntries.length &&
    visibleEnd >= Math.max(0, totalCount - 1)
  ) {
    return Math.max(0, totalCount - 1);
  }

  if (
    metrics.scrollTop <= HISTORY_PROGRESS_TOP_ALIGN_THRESHOLD_PX &&
    retainedWindow.windowStart === 0
  ) {
    return 0;
  }

  let top = -metrics.scrollTop;
  const rows = state.historyEntries.flatMap((entry) => {
    const height = resolveHistoryEntryHeight(entry, state, metrics.clientWidth);
    const row = { absoluteIndex: entry.order - 1, top, bottom: top + height };
    top += height;
    return canonicalIds.has(entry.id) ? [row] : [];
  });
  return measureHistoryNavigatorAnchor(0, metrics.clientHeight, rows);
}

function resolveHistoryNavigatorConcreteAnchorIndex(
  state: SessionAppServerControlViewState,
  viewport: HTMLDivElement,
  totalCount: number,
): number | null {
  if (typeof viewport.getBoundingClientRect !== 'function') {
    return null;
  }

  const metrics = readHistoryScrollMetrics(viewport, state);
  const retainedWindow = resolveHistoryRetainedWindowDescriptor(state.historyEntries, state);
  const distanceFromBottom = metrics.scrollHeight - metrics.clientHeight - metrics.scrollTop;
  if (
    distanceFromBottom <= HISTORY_PROGRESS_TOP_ALIGN_THRESHOLD_PX &&
    retainedWindow.windowEnd >= totalCount
  ) {
    return Math.max(0, totalCount - 1);
  }

  if (
    metrics.scrollTop <= HISTORY_PROGRESS_TOP_ALIGN_THRESHOLD_PX &&
    retainedWindow.windowStart === 0
  ) {
    return 0;
  }

  return measureHistoryNavigatorAnchor(
    viewport.getBoundingClientRect().top,
    viewport.clientHeight,
    readHistoryNavigatorRows(state),
  );
}

function resolveHistoryNavigatorAnchorIndex(
  state: SessionAppServerControlViewState,
  viewport: HTMLDivElement,
  options: {
    refreshFromViewport?: boolean;
  } = {},
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
  if (concreteAnchorIndex !== null) {
    return clampHistoryAbsoluteIndex(concreteAnchorIndex, retainedWindow.totalCount);
  }

  if (options.refreshFromViewport) {
    const estimatedAnchorIndex = resolveHistoryNavigatorEstimatedAnchorIndex(
      state,
      viewport,
      retainedWindow.totalCount,
    );
    if (estimatedAnchorIndex !== null && Number.isFinite(estimatedAnchorIndex)) {
      return clampHistoryAbsoluteIndex(estimatedAnchorIndex, retainedWindow.totalCount);
    }
  }

  const cachedAnchorIndex = resolveCachedHistoryNavigatorAnchorIndex(
    state,
    retainedWindow.totalCount,
  );
  if (cachedAnchorIndex !== null) {
    return cachedAnchorIndex;
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
    (retainedWindow.windowStart + retainedWindow.windowEnd - 1) / 2,
    retainedWindow.totalCount,
  );
}

function resolveCachedHistoryNavigatorAnchorIndex(
  state: SessionAppServerControlViewState,
  totalCount: number,
): number | null {
  if (state.historyPendingJumpTargetIndex != null) {
    const pinned = state.historyJumpTargetEntryId
      ? state.snapshot?.history.find((entry) => entry.entryId === state.historyJumpTargetEntryId)
      : null;
    return clampHistoryAbsoluteIndex(
      pinned ? pinned.order - 1 : state.historyPendingJumpTargetIndex,
      totalCount,
    );
  }
  const anchorIndex = state.historyNavigatorAnchorIndex;
  return anchorIndex !== null && Number.isFinite(anchorIndex)
    ? clampHistoryAbsoluteIndex(anchorIndex, totalCount)
    : null;
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
  visibleCount: number;
  mode: SessionAppServerControlViewState['historyNavigatorMode'];
  mapping?: HistoryNavigatorMapping | null;
}): void {
  const { host, thumb, anchorIndex, visibleCount, mode } = args;
  const historyCount = args.mapping ? args.mapping.geometry.maximumIndex + 1 : args.historyCount;
  const geometry = resolveHistoryNavigatorGeometry({
    trackHeightPx: args.mapping
      ? args.mapping.geometry.travelPx + args.mapping.geometry.thumbHeightPx
      : Math.max(0, host.clientHeight - HISTORY_PROGRESS_THUMB_INSET_PX * 2),
    historyCount,
    visibleCount,
    anchorIndex: anchorIndex ?? historyCount - 1,
    ...(args.mapping ? { renderedThumbHeightPx: args.mapping.geometry.thumbHeightPx } : {}),
  });

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
  thumb.style.height = `${geometry.thumbHeightPx}px`;
  thumb.style.top = `${HISTORY_PROGRESS_THUMB_INSET_PX + geometry.topPx}px`;
}

function syncHistoryProgressNavigatorUi(
  state: SessionAppServerControlViewState | undefined,
  options: { refreshAnchorFromViewport?: boolean; refreshVisibleRange?: boolean } = {},
): void {
  if (!state?.historyProgressNav || !state.historyProgressThumb) {
    return;
  }
  const host = state.historyProgressNav;
  const thumb = state.historyProgressThumb;

  const snapshot = state.snapshot;
  const historyCount = resolveCanonicalHistoryCount(snapshot);
  if (prepareHistoryProgressNavigator(host, thumb, historyCount)) {
    state.historyNavigatorAnchorIndex = null;
    state.historyNavigatorVisibleCount = null;
    return;
  }

  const viewport = state.historyViewport;
  const refreshFromViewport =
    options.refreshAnchorFromViewport === true &&
    state.historyNavigatorMode !== 'drag-preview' &&
    state.historyPendingJumpTargetIndex == null;
  const visibleRange =
    state.historyNavigatorMode !== 'drag-preview' &&
    (refreshFromViewport ||
      options.refreshVisibleRange === true ||
      state.historyNavigatorVisibleCount === null)
      ? readHistoryNavigatorVisibleRange(state)
      : null;
  if (visibleRange) {
    state.historyNavigatorVisibleCount = visibleRange.end - visibleRange.start;
  }
  const anchorIndex =
    viewport === null
      ? historyCount - 1
      : resolveHistoryNavigatorAnchorIndex(state, viewport, {
          refreshFromViewport,
        });
  state.historyNavigatorAnchorIndex = anchorIndex;
  applyHistoryProgressNavigatorPosition({
    host,
    thumb,
    historyCount,
    anchorIndex,
    visibleCount: state.historyNavigatorVisibleCount ?? 1,
    mode: state.historyNavigatorMode,
    mapping: state.historyNavigatorDragMapping,
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
  const historyCount = resolveCanonicalHistoryCount(snapshot);
  if (!state || !host || !snapshot || historyCount <= 0) {
    return null;
  }

  const { trackTopPx, geometry } =
    state.historyNavigatorDragMapping ?? readHistoryNavigatorMapping(state);
  const targetIndex = resolveHistoryNavigatorPointerIndex(
    geometry,
    clientY - trackTopPx,
    typeof thumbDragOffsetPx === 'number' && Number.isFinite(thumbDragOffsetPx)
      ? thumbDragOffsetPx
      : undefined,
  );
  return {
    targetIndex,
    atLiveEdge: targetIndex >= historyCount - 1,
  };
}

export function readHistoryNavigatorMapping(
  state: SessionAppServerControlViewState,
): HistoryNavigatorMapping {
  const host = state.historyProgressNav;
  const paintedCount = Number(host?.getAttribute('aria-valuemax'));
  const count =
    Number.isFinite(paintedCount) && paintedCount > 0
      ? paintedCount
      : resolveCanonicalHistoryCount(state.snapshot);
  const height = state.historyProgressThumb?.getBoundingClientRect().height;
  return {
    trackTopPx: (host?.getBoundingClientRect().top ?? 0) + HISTORY_PROGRESS_THUMB_INSET_PX,
    geometry: resolveHistoryNavigatorGeometry({
      trackHeightPx: Math.max(0, (host?.clientHeight ?? 0) - HISTORY_PROGRESS_THUMB_INSET_PX * 2),
      historyCount: count,
      visibleCount: state.historyNavigatorVisibleCount ?? 1,
      anchorIndex: state.historyNavigatorAnchorIndex ?? count - 1,
      ...(typeof height === 'number' && Number.isFinite(height) && height > 0
        ? { renderedThumbHeightPx: height }
        : {}),
    }),
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
  const adjusted = syncViewportScrollPosition(
    viewport,
    viewport.scrollTop + nearestOffsetTopPx - 24,
  );
  state.historyLastScrollMetrics = readHistoryScrollMetrics(viewport, state);
  confirmHistoryReaderAnchor(state);
  return adjusted;
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
  if (!state) return virtualWindow;
  const jumpEntryIndex = state.historyJumpTargetEntryId
    ? entries.findIndex((entry) => entry.id === state.historyJumpTargetEntryId)
    : resolveHistoryJumpEntryIndex(
        entries,
        state.snapshot ?? null,
        state.historyPendingJumpTargetIndex,
      );
  if (jumpEntryIndex >= 0) {
    return buildHistoryJumpVirtualWindow(
      entries,
      virtualWindow,
      jumpEntryIndex,
      APP_SERVER_CONTROL_HISTORY_OVERSCAN_ITEMS,
      resolveEntryHeight,
    );
  }
  const anchorEntryId =
    state.pendingHistoryPrependAnchor?.entryId ?? state.pendingHistoryLayoutAnchor?.entryId;
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
    // The server window already pages off-kernel canonical history. Row virtualization must be
    // driven by the concrete retained rows only; using the global history count here made a small,
    // filtered kernel re-render on every wheel tick and restore its anchor back to the local edge.
    return (
      entryCount > HISTORY_VIRTUALIZE_AFTER ||
      (state?.historyLeadingPlaceholders.length ?? 0) > 0 ||
      (state?.historyTrailingPlaceholders.length ?? 0) > 0
    );
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
    const viewport = container as HTMLDivElement;
    if (state) prepareHistoryReaderResize(state);
    const renderAnchor =
      state &&
      !state.historyAutoScrollPinned &&
      state.historyPendingJumpTargetIndex === null &&
      state.historyRenderedNodes.size > 0 &&
      typeof viewport.getBoundingClientRect === 'function' &&
      state.pendingHistoryPrependAnchor === null &&
      state.pendingHistoryLayoutAnchor === null
        ? readHistoryReaderAnchor(state)
        : null;
    if (state) {
      state.historyViewport = viewport;
      state.historyEntries = entries;
      state.historyLastScrollMetrics ??= readHistoryScrollMetrics(viewport, state);
      retainHistoryMeasurementsAcrossWindowShift(state);
      deps.pruneHistoryMarkdownCache(state, entries);
      renderScrollToBottomControl(panel, state);
      if (renderAnchor) {
        state.pendingHistoryLayoutAnchor = renderAnchor;
      }
    }

    let metrics = readHistoryViewportMetrics(viewport, state);
    let renderPlan = buildHistoryRenderPlan(entries, metrics, state);
    reconcileHistoryRenderPlan(sessionId, viewport, renderPlan);
    const measurementChanged = state
      ? measureRenderedHistoryHeights(
          sessionId,
          state,
          renderPlan.visibleEntries,
          metrics.clientWidth,
        )
      : false;
    if (measurementChanged && isVirtualizedHistoryContext(state, entries.length)) {
      metrics = readHistoryViewportMetrics(viewport, state);
      renderPlan = buildHistoryRenderPlan(entries, metrics, state);
      reconcileHistoryRenderPlan(sessionId, viewport, renderPlan);
    }
    if (state?.snapshot) {
      traceRenderedAppServerControlHistoryWindow({
        sessionId,
        entries,
        metrics,
        state,
        resolveEntryHeight: (entry) => resolveHistoryEntryHeight(entry, state, metrics.clientWidth),
      });
    }
    finalizeRenderedHistoryState(sessionId, panel, viewport, entries, state);
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
    const virtualWindow = computeHistoryVirtualWindow(
      entries,
      metrics.scrollTop,
      metrics.clientHeight,
      metrics.clientWidth,
      resolveEntryHeight,
    );
    const renderedWindow = expandHistoryVirtualWindowForPendingAnchor({
      entries,
      virtualWindow,
      state,
      resolveEntryHeight,
    });
    const retainedWindow = resolveHistoryRetainedWindowDescriptor(entries, state);
    const retainedWindowStart = retainedWindow.windowStart;
    const leadingPlaceholders = [
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
  ): boolean | null {
    if (state.historyPendingJumpTargetIndex == null) return null;
    suppressViewportWindowSync(state);
    const adjusted = alignHistoryJumpTarget(viewport, state);
    state.historyLastScrollMetrics = readHistoryScrollMetrics(viewport, state);
    return adjusted;
  }

  function finalizeRenderedHistoryState(
    sessionId: string,
    panel: HTMLDivElement,
    viewport: HTMLDivElement,
    entries: readonly AppServerControlHistoryEntry[],
    state: SessionAppServerControlViewState | undefined,
  ): void {
    const jumpResult = state ? restorePendingHistoryJumpTarget(viewport, state) : null;
    const jumpViewportAdjusted = jumpResult === true;
    const browseViewportAdjusted =
      jumpResult !== null ? false : adjustBrowseViewportIfNeeded(sessionId, viewport, state);
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
      completeHistoryReaderMeasurements(state);
    }

    const userScrollInProgress =
      state !== undefined &&
      Date.now() - state.historyLastUserScrollIntentAt <= USER_HISTORY_GAP_RECOVERY_GRACE_MS;
    if (
      state &&
      !state.historyAutoScrollPinned &&
      entries.length > 0 &&
      !viewportHasConcreteRows &&
      !userScrollInProgress
    ) {
      const roundedScrollTop = Math.round(viewport.scrollTop);
      if (state.historyLastVoidSyncScrollTop !== roundedScrollTop) {
        state.historyLastVoidSyncScrollTop = roundedScrollTop;
        deps.syncViewportHistoryWindow?.(sessionId);
      }
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
      resolveHistoryBadgeLabel(entry.kind, state?.snapshot?.session.agentName),
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
      entry.sourceItemType === 'proposed_plan' && state?.snapshot
        ? [
            state.snapshot.currentTurn.state,
            state.streamConnected,
            isProposedPlanActionDisabled(state.snapshot.sessionId, state),
          ].join(':')
        : '',
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
    const interactionToken = [
      state?.requestBusyIds.has(entry.requestId) ? 'busy' : 'idle',
      state?.requestQuestionIndexById[entry.requestId] ?? 0,
    ].join(':');
    if (!request) {
      return `embedded:${interactionToken}`;
    }

    return [
      request.kind,
      request.state,
      request.decision ?? '',
      request.detail ?? '',
      request.updatedAt,
      request.answers.map((answer) => `${answer.questionId}:${answer.answers.join(',')}`).join('|'),
      interactionToken,
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

    if (existing && deps.syncHistoryEntry) {
      deps.syncHistoryEntry(existing.node, visibleEntry.entry, sessionId, {
        artifactCluster: visibleEntry.cluster,
        showAssistantBadge: visibleEntry.showAssistantBadge,
      });
      existing.node.dataset.appServerControlEntryId = visibleEntry.key;
      existing.signature = visibleEntry.signature;
      existing.entry = visibleEntry.entry;
      existing.cluster = visibleEntry.cluster;
      existing.lastMeasuredWidthBucket = null;
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

  function captureHistoryViewportAnchor(
    state: SessionAppServerControlViewState,
    key:
      'pendingHistoryPrependAnchor' | 'pendingHistoryLayoutAnchor' = 'pendingHistoryPrependAnchor',
  ): boolean {
    const viewport = state.historyViewport;
    if (!viewport) {
      state[key] = null;
      return false;
    }

    prepareHistoryReaderResize(state);
    if (state.pendingHistoryLayoutAnchor) {
      if (key === 'pendingHistoryPrependAnchor') {
        state.pendingHistoryPrependAnchor = { ...state.pendingHistoryLayoutAnchor };
        state.pendingHistoryLayoutAnchor = null;
      }
      return true;
    }
    const anchor = readHistoryReaderAnchor(state);
    state[key] = anchor;
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

    clearHistoryJumpTarget(state);
    state.historyNavigatorDragMapping = null;

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
    const virtualWindow = computeHistoryVirtualWindow(
      state.historyEntries,
      metrics.scrollTop,
      metrics.clientHeight,
      metrics.clientWidth,
      (entry) => resolveHistoryEntryHeight(entry, state, metrics.clientWidth),
    );
    return buildHistoryVirtualWindowKey(virtualWindow) !== state.historyLastVirtualWindowKey;
  }

  function getViewportCenteredHistoryWindowRequest(
    state: SessionAppServerControlViewState,
    options: {
      fetchAheadItems: number;
      anchorAbsoluteIndex?: number | null;
      navigationDirection?: 'earlier' | 'later' | null | undefined;
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
    const distanceFromBottom = viewport.scrollHeight - viewport.clientHeight - viewport.scrollTop;
    const edgeDirection =
      viewport.scrollTop <= 1 && snapshot.hasOlderHistory
        ? 'earlier'
        : distanceFromBottom <= 1 && snapshot.hasNewerHistory
          ? 'later'
          : null;
    const request = resolveKernelWindowRequest({
      items: state.historyEntries,
      viewportMetrics: metrics,
      retainedWindow,
      fetchAheadItems: options.fetchAheadItems,
      resolveItemSize: (entry) => resolveEntryHeight(entry),
      resolveAbsoluteIndex: (entry) =>
        clampHistoryAbsoluteIndex(Math.floor(entry.order) - 1, retainedWindow.totalCount),
      edgeDirection,
      navigationDirection: options.navigationDirection,
      anchorAbsoluteIndex: options.anchorAbsoluteIndex,
    });
    return request;
  }

  function syncViewportOffset(
    sessionId: string,
    options: { refreshAnchorFromViewport?: boolean; refreshVisibleRange?: boolean } = {},
  ): void {
    const state = deps.getState(sessionId);
    if (!state) {
      return;
    }

    syncHistoryProgressNavigatorUi(state, options);
  }

  function getUnderfilledHistoryWindowRequest(state: SessionAppServerControlViewState) {
    const viewport = state.historyViewport;
    const snapshot = state.snapshot;
    if (!viewport || !snapshot || state.historyNavigatorMode === 'drag-preview') return null;
    const rows = readHistoryNavigatorRows(state);
    const top = Math.min(...rows.map((row) => row.top));
    const bottom = Math.max(...rows.map((row) => row.bottom));
    return resolveUnderfilledHistoryWindowRequest({
      viewportHeight: viewport.clientHeight,
      renderedHeight: rows.length > 0 ? bottom - top : 0,
      windowStart: snapshot.historyWindowStart,
      windowEnd: snapshot.historyWindowEnd,
      historyCount: snapshot.historyCount,
    });
  }

  return {
    captureHistoryViewportAnchor,
    getViewportCenteredHistoryWindowRequest,
    getUnderfilledHistoryWindowRequest,
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
  if (!entry || !isArtifactClusterEntry(entry)) {
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

function isArtifactClusterEntry(entry: AppServerControlHistoryEntry): boolean {
  return (
    entry.sourceItemType !== 'proposed_plan' &&
    ['tool', 'reasoning', 'plan', 'diff'].includes(entry.kind)
  );
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
    if (!nextEntry || !isArtifactClusterEntry(nextEntry)) {
      return boundary;
    }
    boundary = nextIndex;
  }
}
/* eslint-enable max-lines-per-function, complexity */
/* eslint-enable max-lines */
