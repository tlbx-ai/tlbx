import type { HistoryJumpAlign, HistoryVirtualWindow, SessionAppServerControlViewState } from './types';
import { syncViewportScrollPosition } from '../../utils/virtualizer';

type JumpState = Pick<
  SessionAppServerControlViewState,
  'historyPendingJumpTargetIndex' | 'historyPendingJumpAlign' | 'historyJumpTargetEntryId'
>;

export function setHistoryJumpTarget(state: JumpState, index: number, align: HistoryJumpAlign): void {
  if (state.historyPendingJumpTargetIndex !== index) state.historyJumpTargetEntryId = null;
  state.historyPendingJumpTargetIndex = index;
  state.historyPendingJumpAlign = align;
}

export function clearHistoryJumpTarget(state: JumpState): void {
  state.historyPendingJumpTargetIndex = null;
  state.historyPendingJumpAlign = null;
  state.historyJumpTargetEntryId = null;
}

/** Keep an explicit seek attached to its row through passive hydration and reflow. */
export function alignHistoryJumpTarget(
  viewport: HTMLDivElement,
  state: SessionAppServerControlViewState,
): boolean | null {
  const target = state.historyPendingJumpTargetIndex;
  if (target == null || !state.snapshot) return null;
  const canonicalIds = new Set(state.snapshot.history.map((entry) => entry.entryId));
  const stableIndex = state.historyJumpTargetEntryId
    ? state.historyEntries.findIndex(
        (entry) => entry.id === state.historyJumpTargetEntryId && canonicalIds.has(entry.id),
      )
    : -1;
  const index =
    stableIndex >= 0
      ? stableIndex
      : resolveHistoryJumpEntryIndex(state.historyEntries, state.snapshot, target);
  const entry = state.historyEntries[index];
  const node =
    entry && canonicalIds.has(entry.id) ? state.historyRenderedNodes.get(entry.id)?.node : null;
  if (
    !entry ||
    !node ||
    typeof node.getBoundingClientRect !== 'function' ||
    typeof viewport.getBoundingClientRect !== 'function'
  ) {
    return null;
  }
  state.historyJumpTargetEntryId = entry.id;
  state.historyNavigatorAnchorIndex = Math.max(0, Math.round(entry.order) - 1);
  state.pendingHistoryPrependAnchor = null;
  state.pendingHistoryLayoutAnchor = null;
  const rect = node.getBoundingClientRect();
  const top = viewport.scrollTop + rect.top - viewport.getBoundingClientRect().top;
  const align = state.historyPendingJumpAlign ?? 'center';
  const targetTop =
    align === 'bottom'
      ? top + rect.height - viewport.clientHeight + 32
      : align === 'top'
        ? top - 18
        : top - Math.max(0, (viewport.clientHeight - rect.height) / 2);
  return syncViewportScrollPosition(viewport, targetTop);
}

/** Canonical indexes remain meaningful after the visual projection filters rows. */
export function resolveHistoryJumpEntryIndex(
  entries: readonly { order: number; id?: string }[],
  snapshot: {
    historyWindowStart: number;
    historyWindowEnd: number;
    history?: readonly { entryId: string }[];
  } | null,
  targetIndex: number | null | undefined,
): number {
  if (
    !snapshot ||
    typeof targetIndex !== 'number' ||
    !Number.isFinite(targetIndex) ||
    targetIndex < snapshot.historyWindowStart ||
    targetIndex >= snapshot.historyWindowEnd ||
    entries.length === 0
  ) {
    return -1;
  }

  const canonicalIds = snapshot.history ? new Set(snapshot.history.map((entry) => entry.entryId)) : null;
  let preceding = -1;
  for (let index = 0; index < entries.length; index += 1) {
    const entry = entries[index];
    if (!entry || (canonicalIds && (!entry.id || !canonicalIds.has(entry.id)))) continue;
    if (Math.round(entry.order) - 1 >= targetIndex) return index;
    preceding = index;
  }
  return preceding;
}

/** A jump replaces the mounted corridor rather than mounting the traversed span. */
export function buildHistoryJumpVirtualWindow<T>(
  entries: readonly T[],
  virtualWindow: HistoryVirtualWindow,
  targetEntryIndex: number,
  overscanItems: number,
  resolveEntryHeight: (entry: T) => number,
): HistoryVirtualWindow {
  const count = Math.min(
    entries.length,
    Math.max(overscanItems * 2 + 1, virtualWindow.end - virtualWindow.start),
  );
  const start = Math.max(
    0,
    Math.min(entries.length - count, targetEntryIndex - Math.floor(count / 2)),
  );
  const end = start + count;
  let topSpacerPx = 0;
  let bottomSpacerPx = 0;
  for (let index = 0; index < start; index += 1) {
    const entry = entries[index];
    if (entry !== undefined) topSpacerPx += resolveEntryHeight(entry);
  }
  for (let index = end; index < entries.length; index += 1) {
    const entry = entries[index];
    if (entry !== undefined) bottomSpacerPx += resolveEntryHeight(entry);
  }
  return { start, end, topSpacerPx, bottomSpacerPx };
}
