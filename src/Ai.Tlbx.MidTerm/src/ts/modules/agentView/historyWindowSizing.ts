import { resolveViewportDrivenWindowCount } from '../../utils/virtualizer';

// A sparse projection may need several canonical pages, but never an unbounded archive fetch.
export const MAX_HISTORY_FILL_ITEMS = 960;

export function resolveViewportDrivenHistoryWindowCount(
  viewport: HTMLDivElement | null | undefined,
  fetchAheadItems: number,
  fallbackCount: number,
  observedHeights?: Iterable<number> | null,
): number {
  return Math.max(
    Math.min(MAX_HISTORY_FILL_ITEMS, fallbackCount),
    resolveViewportDrivenWindowCount({
      viewport,
      fetchAheadItems,
      fallbackCount,
      observedSizes: observedHeights,
    }),
  );
}

export function resolveUnderfilledHistoryWindowRequest(args: {
  viewportHeight: number;
  renderedHeight: number;
  windowStart: number;
  windowEnd: number;
  historyCount: number;
}): { startIndex: number; count: number } | null {
  const currentCount = args.windowEnd - args.windowStart;
  if (
    args.viewportHeight <= 0 ||
    args.renderedHeight >= args.viewportHeight * 1.5 ||
    currentCount >= MAX_HISTORY_FILL_ITEMS ||
    (args.windowStart === 0 && args.windowEnd >= args.historyCount)
  ) {
    return null;
  }
  const count = Math.min(args.historyCount, MAX_HISTORY_FILL_ITEMS, Math.max(80, currentCount * 2));
  if (count <= currentCount) return null;
  const startIndex = Math.max(
    0,
    Math.min(args.historyCount - count, args.windowStart - Math.ceil((count - currentCount) / 2)),
  );
  return { startIndex, count };
}
