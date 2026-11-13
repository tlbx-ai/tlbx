import type { SessionAppServerControlViewState } from './types';

/** Remember only the part the native scroller cannot consume at a retained-window edge. */
export function retainHistoryWheelOverflow(
  state: SessionAppServerControlViewState,
  deltaY: number,
): void {
  const viewport = state.historyViewport;
  if (!viewport || !Number.isFinite(deltaY) || deltaY === 0) return;
  if (Math.sign(state.historyPendingWheelDeltaPx) !== Math.sign(deltaY)) {
    state.historyPendingWheelDeltaPx = 0;
  }
  const hasMore = deltaY < 0 ? state.snapshot?.hasOlderHistory : state.snapshot?.hasNewerHistory;
  if (!hasMore) {
    state.historyPendingWheelDeltaPx = 0;
    return;
  }
  const available =
    deltaY < 0
      ? Math.max(0, viewport.scrollTop)
      : Math.max(0, viewport.scrollHeight - viewport.clientHeight - viewport.scrollTop);
  state.historyPendingWheelDeltaPx += Math.sign(deltaY) * Math.max(0, Math.abs(deltaY) - available);
}

/** Continue that same wheel gesture after the new window restores the current reader anchor. */
export function resumeHistoryWheelMotion(state: SessionAppServerControlViewState): boolean {
  const viewport = state.historyViewport;
  const deltaY = state.historyPendingWheelDeltaPx;
  if (!viewport || deltaY === 0 || state.historyAutoScrollPinned) return false;
  const before = viewport.scrollTop;
  viewport.scrollTop = Math.max(
    0,
    Math.min(viewport.scrollHeight - viewport.clientHeight, before + deltaY),
  );
  const moved = viewport.scrollTop - before;
  const hasMore = deltaY < 0 ? state.snapshot?.hasOlderHistory : state.snapshot?.hasNewerHistory;
  state.historyPendingWheelDeltaPx = hasMore ? deltaY - moved : 0;
  if (Math.abs(state.historyPendingWheelDeltaPx) < 1) state.historyPendingWheelDeltaPx = 0;
  state.historyViewportSyncSuppressUntil = 0;
  state.historyViewportSyncPending ||= state.historyPendingWheelDeltaPx !== 0;
  return moved !== 0;
}
