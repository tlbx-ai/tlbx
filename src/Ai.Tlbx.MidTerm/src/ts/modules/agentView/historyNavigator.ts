import type { HistoryScrollMetrics } from './types';
import type { AppServerControlHistorySnapshot } from '../../api/types';

export const HISTORY_PROGRESS_THUMB_INSET_PX = 6;

export function resolveCanonicalHistoryCount(
  snapshot: Pick<AppServerControlHistorySnapshot, 'historyCount'> | null | undefined,
): number {
  const count = snapshot?.historyCount ?? 0;
  return Number.isFinite(count) ? Math.max(0, Math.floor(count)) : 0;
}

export function resolveHistoryNavigatorGeometry(args: {
  trackHeightPx: number;
  historyCount: number;
  visibleCount: number;
  anchorIndex: number;
}) {
  const trackHeightPx = Math.max(0, args.trackHeightPx);
  const historyCount = Math.max(1, args.historyCount);
  const visibleFraction = Math.max(0, Math.min(1, args.visibleCount / historyCount));
  const thumbHeightPx = Math.min(trackHeightPx, Math.max(18, trackHeightPx * visibleFraction));
  const travelPx = trackHeightPx - thumbHeightPx;
  const maximumIndex = historyCount - 1;
  const anchorIndex = Math.max(0, Math.min(maximumIndex, args.anchorIndex));
  const progress = maximumIndex > 0 ? anchorIndex / maximumIndex : 0;
  return { thumbHeightPx, travelPx, topPx: travelPx * progress, maximumIndex, anchorIndex };
}

export function resolveHistoryNavigatorPointerIndex(
  geometry: ReturnType<typeof resolveHistoryNavigatorGeometry>,
  trackOffsetPx: number,
  grabOffsetPx = geometry.thumbHeightPx / 2,
): number {
  if (geometry.travelPx <= 0) {
    return Math.round(geometry.anchorIndex);
  }
  const progress = Math.max(0, Math.min(1, (trackOffsetPx - grabOffsetPx) / geometry.travelPx));
  return Math.round(progress * geometry.maximumIndex);
}

export function measureHistoryNavigatorRange(
  viewportTop: number,
  viewportHeight: number,
  rows: Iterable<{ absoluteIndex: number; top: number; bottom: number }>,
): { start: number; end: number } | null {
  let start = Number.POSITIVE_INFINITY;
  let end = Number.NEGATIVE_INFINITY;
  const viewportBottom = viewportTop + viewportHeight;
  for (const row of rows) {
    const height = row.bottom - row.top;
    if (
      ![row.absoluteIndex, row.top, row.bottom].every(Number.isFinite) ||
      height <= 0 ||
      row.bottom <= viewportTop ||
      row.top >= viewportBottom
    ) {
      continue;
    }
    start = Math.min(start, row.absoluteIndex + Math.max(0, (viewportTop - row.top) / height));
    end = Math.max(end, row.absoluteIndex + Math.min(1, (viewportBottom - row.top) / height));
  }
  return end > start ? { start, end } : null;
}

export function shouldRefreshHistoryNavigatorFromScroll(args: {
  now: number;
  lastUserIntentAt: number;
  intentWindowMs: number;
  previous: HistoryScrollMetrics | null;
  current: HistoryScrollMetrics;
}): boolean {
  // Correction callers record the resulting metrics synchronously. Their queued scroll event
  // has no movement left; subsequent user movement (including momentum) must still take effect.
  return (
    args.lastUserIntentAt > 0 &&
    args.now - args.lastUserIntentAt <= args.intentWindowMs &&
    args.previous !== null &&
    args.previous.scrollTop !== args.current.scrollTop
  );
}
