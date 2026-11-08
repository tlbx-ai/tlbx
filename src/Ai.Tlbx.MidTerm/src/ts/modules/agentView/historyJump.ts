import type { HistoryVirtualWindow } from './types';

/** Canonical indexes remain meaningful after the visual projection filters rows. */
export function resolveHistoryJumpEntryIndex(
  entries: readonly { order: number }[],
  snapshot: { historyWindowStart: number; historyWindowEnd: number } | null,
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

  const index = entries.findIndex((entry) => Math.round(entry.order) - 1 >= targetIndex);
  return index >= 0 ? index : entries.length - 1;
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
