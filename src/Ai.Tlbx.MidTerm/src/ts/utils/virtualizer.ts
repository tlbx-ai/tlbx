const VIRTUALIZER_MEASUREMENT_WIDTH_BUCKET_SIZE_PX = 40;
const VIRTUALIZER_MEASUREMENT_MIN_WIDTH_PX = 240;
const VIRTUALIZER_HEIGHT_SAMPLE_LIMIT = 6;
const VIRTUALIZER_MEASUREMENT_BUCKET_LIMIT = 3;
const VIRTUALIZER_MEASUREMENT_ENTRY_LIMIT = 12000;
const DEFAULT_REPRESENTATIVE_ITEM_SIZE_PX = 72;

export interface VirtualizerIndexRange {
  start: number;
  end: number;
}

export interface VirtualizerViewportMetrics {
  scrollTop: number;
  clientHeight: number;
  clientWidth: number;
}

export interface VirtualizerWindow {
  start: number;
  end: number;
  topSpacerPx: number;
  bottomSpacerPx: number;
}

export interface VirtualizerRetainedWindowDescriptor {
  windowStart: number;
  windowEnd: number;
  totalCount: number;
}

export interface VirtualizerAnchor<TKey extends string = string> {
  key: TKey;
  topOffsetPx: number;
  absoluteIndex: number;
}

export interface VirtualizerMeasurementState<TKey extends string = string> {
  measuredSizes: Map<TKey, number>;
  observedSizes: Map<TKey, number>;
  measuredSizesByBucket: Map<number, Map<TKey, number>>;
  observedSizesByBucket: Map<number, Map<TKey, number>>;
  observedSizeSamplesByBucket: Map<number, Map<TKey, number[]>>;
  measuredWidthBucket: number;
  lastWindowKey: string | null;
}

export interface VirtualizerRenderedNode<TKey extends string = string> {
  key: TKey;
  node: HTMLElement;
  absoluteIndex: number;
}

export interface VirtualizerMeasuredItemChange {
  absoluteIndex: number;
  previousSize: number;
  nextSize: number;
}

type SizeResolver<TItem> = (item: TItem, index: number) => number;

interface LayoutModel {
  prefixSizes: number[];
  totalSize: number;
}

function ensureBucket<T>(
  buckets: Map<number, Map<string, T>>,
  widthBucket: number,
): Map<string, T> {
  let bucket = buckets.get(widthBucket);
  if (!bucket) {
    bucket = new Map<string, T>();
    buckets.set(widthBucket, bucket);
  }

  return bucket;
}

function touchBucket<T>(buckets: Map<number, Map<string, T>>, widthBucket: number): Map<string, T> {
  const bucket = ensureBucket(buckets, widthBucket);
  buckets.delete(widthBucket);
  buckets.set(widthBucket, bucket);
  while (buckets.size > VIRTUALIZER_MEASUREMENT_BUCKET_LIMIT) {
    const oldestBucket = buckets.keys().next().value;
    if (oldestBucket === undefined) break;
    buckets.delete(oldestBucket);
  }
  return bucket;
}

function trimMeasurementEntries<T>(bucket: Map<string, T>): void {
  while (bucket.size > VIRTUALIZER_MEASUREMENT_ENTRY_LIMIT) {
    const oldestKey = bucket.keys().next().value;
    if (oldestKey === undefined) break;
    bucket.delete(oldestKey);
  }
}

function resolveMedian(sample: readonly number[], fallback: number): number {
  const numericSample = sample.filter((value) => Number.isFinite(value) && value > 0);
  if (numericSample.length === 0) {
    return fallback;
  }

  numericSample.sort((left, right) => left - right);
  return numericSample[Math.floor(numericSample.length / 2)] ?? fallback;
}

function buildLayoutModel<TItem>(
  items: ReadonlyArray<TItem>,
  resolveItemSize: SizeResolver<TItem>,
): LayoutModel {
  const prefixSizes = new Array<number>(items.length + 1);
  prefixSizes[0] = 0;
  let cumulativeSize = 0;
  for (let index = 0; index < items.length; index += 1) {
    const item = items[index];
    cumulativeSize += item ? resolveItemSize(item, index) : 0;
    prefixSizes[index + 1] = cumulativeSize;
  }

  return {
    prefixSizes,
    totalSize: cumulativeSize,
  };
}

function findFirstIntersectingIndex(prefixSizes: readonly number[], targetTop: number): number {
  let low = 1;
  let high = prefixSizes.length - 1;
  while (low < high) {
    const middle = Math.floor((low + high) / 2);
    if ((prefixSizes[middle] ?? 0) > targetTop) {
      high = middle;
    } else {
      low = middle + 1;
    }
  }

  return Math.max(0, Math.min(prefixSizes.length - 2, low - 1));
}

function findFirstEndAtOrAfter(prefixSizes: readonly number[], targetBottom: number): number {
  let low = 1;
  let high = prefixSizes.length - 1;
  while (low < high) {
    const middle = Math.floor((low + high) / 2);
    if ((prefixSizes[middle] ?? 0) >= targetBottom) {
      high = middle;
    } else {
      low = middle + 1;
    }
  }

  return Math.max(1, Math.min(prefixSizes.length - 1, low));
}

export function resolveVirtualizerMeasurementWidthBucket(clientWidth: number): number {
  return Math.max(
    VIRTUALIZER_MEASUREMENT_MIN_WIDTH_PX,
    Math.round(clientWidth / VIRTUALIZER_MEASUREMENT_WIDTH_BUCKET_SIZE_PX) *
      VIRTUALIZER_MEASUREMENT_WIDTH_BUCKET_SIZE_PX,
  );
}

export function resolveVirtualizerViewportWidth(
  viewport: Pick<HTMLDivElement, 'clientWidth'> | null | undefined,
): number | undefined {
  const clientWidth = Math.max(0, viewport?.clientWidth ?? 0);
  return clientWidth > 0 ? resolveVirtualizerMeasurementWidthBucket(clientWidth) : undefined;
}

export function resolveResizeObserverBorderBoxSize(record: ResizeObserverEntry): number {
  const borderBoxBlockSize = record.borderBoxSize[0]?.blockSize;
  if (
    borderBoxBlockSize !== undefined &&
    Number.isFinite(borderBoxBlockSize) &&
    borderBoxBlockSize > 0
  ) {
    return borderBoxBlockSize;
  }

  const measuredHeight = (record.target as HTMLElement).getBoundingClientRect().height;
  if (Number.isFinite(measuredHeight) && measuredHeight > 0) {
    return measuredHeight;
  }

  return record.contentRect.height;
}

export function activateVirtualizerMeasurementBucket(
  state: VirtualizerMeasurementState,
  clientWidth: number,
): number {
  const widthBucket = resolveVirtualizerMeasurementWidthBucket(clientWidth);
  const measuredSizes = touchBucket(state.measuredSizesByBucket, widthBucket);
  const observedSizes = touchBucket(state.observedSizesByBucket, widthBucket);
  touchBucket(state.observedSizeSamplesByBucket, widthBucket);
  const changed =
    state.measuredWidthBucket !== widthBucket ||
    state.measuredSizes !== measuredSizes ||
    state.observedSizes !== observedSizes;
  state.measuredWidthBucket = widthBucket;
  state.measuredSizes = measuredSizes;
  state.observedSizes = observedSizes;
  if (changed) {
    state.lastWindowKey = null;
  }

  return widthBucket;
}

export function recordMeasuredItemSize(
  state: VirtualizerMeasurementState,
  key: string,
  measuredSize: number,
  clientWidth: number,
): boolean {
  const widthBucket = activateVirtualizerMeasurementBucket(state, clientWidth);
  const normalizedSize = Math.max(1, Math.round(measuredSize));
  const previousMeasuredSize = state.measuredSizes.get(key);
  const sampleBuckets = ensureBucket(state.observedSizeSamplesByBucket, widthBucket);
  const samples = [...(sampleBuckets.get(key) ?? [])];
  if (samples[samples.length - 1] !== normalizedSize) {
    samples.push(normalizedSize);
    while (samples.length > VIRTUALIZER_HEIGHT_SAMPLE_LIMIT) {
      samples.shift();
    }
    sampleBuckets.set(key, samples);
  }

  const nextObservedSize = resolveMedian(samples, normalizedSize);
  const previousObservedSize = state.observedSizes.get(key);
  if (previousMeasuredSize === normalizedSize && previousObservedSize === nextObservedSize) {
    return false;
  }

  state.measuredSizes.set(key, normalizedSize);
  state.observedSizes.set(key, nextObservedSize);
  trimMeasurementEntries(state.measuredSizes);
  trimMeasurementEntries(state.observedSizes);
  trimMeasurementEntries(sampleBuckets);
  state.lastWindowKey = null;
  return true;
}

export function resolveRepresentativeItemSize(observedSizes?: Iterable<number> | null): number {
  const sample: number[] = [];
  if (observedSizes) {
    for (const value of observedSizes) {
      if (!Number.isFinite(value) || value <= 0) {
        continue;
      }

      sample.push(value);
    }
  }

  return resolveMedian(sample, DEFAULT_REPRESENTATIVE_ITEM_SIZE_PX);
}

export function computeVisibleRange<TItem>(args: {
  items: ReadonlyArray<TItem>;
  scrollTop: number;
  clientHeight: number;
  overscanItems?: number;
  resolveItemSize: SizeResolver<TItem>;
}): VirtualizerIndexRange {
  const { items, scrollTop, clientHeight, overscanItems = 0, resolveItemSize } = args;
  if (items.length === 0) {
    return { start: 0, end: 0 };
  }

  const layout = buildLayoutModel(items, resolveItemSize);
  const visibleStart = findFirstIntersectingIndex(layout.prefixSizes, scrollTop);
  const visibleEnd = Math.max(
    visibleStart + 1,
    Math.min(items.length, findFirstEndAtOrAfter(layout.prefixSizes, scrollTop + clientHeight)),
  );

  return {
    start: Math.max(0, visibleStart - Math.max(0, overscanItems)),
    end: Math.min(items.length, visibleEnd + Math.max(0, overscanItems)),
  };
}

export function computeVirtualWindow<TItem>(args: {
  items: ReadonlyArray<TItem>;
  scrollTop: number;
  clientHeight: number;
  overscanItems?: number;
  overscanPx?: number;
  resolveItemSize: SizeResolver<TItem>;
}): VirtualizerWindow {
  const { items, resolveItemSize, scrollTop, clientHeight } = args;
  if (items.length === 0) return { start: 0, end: 0, topSpacerPx: 0, bottomSpacerPx: 0 };
  // Build once: range lookup and spacer geometry share the same prefix sums.
  const layout = buildLayoutModel(items, resolveItemSize);
  const visibleStart = findFirstIntersectingIndex(layout.prefixSizes, scrollTop);
  const visibleEnd = Math.max(
    visibleStart + 1,
    Math.min(items.length, findFirstEndAtOrAfter(layout.prefixSizes, scrollTop + clientHeight)),
  );
  const overscan = Math.max(0, args.overscanItems ?? 0);
  let start = Math.max(0, visibleStart - overscan);
  let end = Math.min(items.length, visibleEnd + overscan);
  if (args.overscanPx !== undefined) {
    const margin = Math.max(0, args.overscanPx);
    start = Math.max(
      start,
      findFirstIntersectingIndex(layout.prefixSizes, Math.max(0, scrollTop - margin)),
    );
    end = Math.min(
      end,
      Math.max(
        visibleEnd,
        findFirstEndAtOrAfter(layout.prefixSizes, scrollTop + clientHeight + margin),
      ),
    );
  }
  const topSpacerPx = layout.prefixSizes[start] ?? 0;
  return {
    start,
    end,
    topSpacerPx,
    bottomSpacerPx: Math.max(0, layout.totalSize - (layout.prefixSizes[end] ?? layout.totalSize)),
  };
}

export function buildVirtualizerWindowKey(window: VirtualizerWindow): string {
  return `${window.start}:${window.end}`;
}

export function resolveViewportDrivenWindowCount(args: {
  viewport: Pick<HTMLDivElement, 'clientHeight'> | null | undefined;
  fetchAheadItems: number;
  fallbackCount: number;
  observedSizes?: Iterable<number> | null | undefined;
}): number {
  const clientHeight = Math.max(0, args.viewport?.clientHeight ?? 0);
  if (clientHeight <= 0) {
    return args.fallbackCount;
  }

  const representativeItemSize = resolveRepresentativeItemSize(args.observedSizes);
  const estimatedVisibleCount = Math.max(1, Math.ceil(clientHeight / representativeItemSize));
  return Math.max(
    estimatedVisibleCount + Math.max(0, args.fetchAheadItems) * 2,
    estimatedVisibleCount + 1,
  );
}

function resolveKernelAbsoluteIndex<TItem>(args: {
  items: readonly TItem[];
  relativeIndex: number;
  retainedWindow: VirtualizerRetainedWindowDescriptor;
  resolveAbsoluteIndex: ((item: TItem, relativeIndex: number) => number) | undefined;
}): number {
  const item = args.items[args.relativeIndex];
  const fallback = args.retainedWindow.windowStart + args.relativeIndex;
  const resolved =
    item === undefined || !args.resolveAbsoluteIndex
      ? fallback
      : args.resolveAbsoluteIndex(item, args.relativeIndex);
  return Number.isFinite(resolved)
    ? Math.max(0, Math.min(args.retainedWindow.totalCount - 1, Math.round(resolved)))
    : fallback;
}

function resolveKernelWindowEdges(args: {
  retainedWindow: VirtualizerRetainedWindowDescriptor;
  edgeDirection: 'earlier' | 'later' | null | undefined;
  absoluteVisibleStart: number;
  absoluteVisibleEnd: number;
  marginItems: number;
  navigationDirection?: 'earlier' | 'later' | null | undefined;
}): { needsEarlierWindow: boolean; needsLaterWindow: boolean } {
  const allowEarlier = args.navigationDirection !== 'later';
  const allowLater = args.navigationDirection !== 'earlier';
  return {
    needsEarlierWindow:
      allowEarlier &&
      args.retainedWindow.windowStart > 0 &&
      (args.edgeDirection === 'earlier' ||
        args.absoluteVisibleStart < args.retainedWindow.windowStart + args.marginItems),
    needsLaterWindow:
      allowLater &&
      args.retainedWindow.windowEnd < args.retainedWindow.totalCount &&
      (args.edgeDirection === 'later' ||
        args.absoluteVisibleEnd > args.retainedWindow.windowEnd - args.marginItems),
  };
}

function resolveKernelWindowShiftDirection(args: {
  edgeDirection: 'earlier' | 'later' | null | undefined;
  navigationDirection?: 'earlier' | 'later' | null | undefined;
  needsEarlierWindow: boolean;
  needsLaterWindow: boolean;
}): 'earlier' | 'later' | null | undefined {
  if (args.navigationDirection === 'earlier' && args.needsEarlierWindow) {
    return 'earlier';
  }
  if (args.navigationDirection === 'later' && args.needsLaterWindow) {
    return 'later';
  }
  return args.edgeDirection;
}

/**
 * Resolves the next retained item window for a native, kernel-local pixel scroller.
 *
 * This function never maps local scroll pixels onto estimated off-window history.
 * The browser scrolls only the
 * currently retained kernel; crossing either kernel margin requests an overlapping
 * canonical index window and the caller restores a concrete visible item anchor.
 */
export function resolveKernelWindowRequest<TItem>(args: {
  items: readonly TItem[];
  viewportMetrics: VirtualizerViewportMetrics;
  retainedWindow: VirtualizerRetainedWindowDescriptor;
  fetchAheadItems: number;
  resolveItemSize: SizeResolver<TItem>;
  resolveAbsoluteIndex?: (item: TItem, relativeIndex: number) => number;
  edgeDirection?: 'earlier' | 'later' | null;
  navigationDirection?: 'earlier' | 'later' | null | undefined;
  anchorAbsoluteIndex?: number | null | undefined;
}): { startIndex: number; count: number } | null {
  const { items, viewportMetrics, retainedWindow, resolveItemSize } = args;
  if (items.length === 0 || retainedWindow.totalCount <= 0) {
    return null;
  }

  const visibleRange = computeVisibleRange({
    items,
    scrollTop: Math.max(0, viewportMetrics.scrollTop),
    clientHeight: Math.max(1, viewportMetrics.clientHeight),
    overscanItems: 0,
    resolveItemSize,
  });
  const resolveAbsoluteIndex = (relativeIndex: number): number =>
    resolveKernelAbsoluteIndex({
      items,
      relativeIndex,
      retainedWindow,
      resolveAbsoluteIndex: args.resolveAbsoluteIndex,
    });
  const absoluteVisibleStart = resolveAbsoluteIndex(visibleRange.start);
  const absoluteVisibleEnd =
    resolveAbsoluteIndex(Math.max(visibleRange.start, visibleRange.end - 1)) + 1;
  const marginItems = Math.max(0, Math.floor(args.fetchAheadItems));
  const { needsEarlierWindow, needsLaterWindow } = resolveKernelWindowEdges({
    retainedWindow,
    edgeDirection: args.edgeDirection,
    absoluteVisibleStart,
    absoluteVisibleEnd,
    marginItems,
    navigationDirection: args.navigationDirection,
  });
  if (!needsEarlierWindow && !needsLaterWindow) {
    return null;
  }

  const currentCount = Math.max(1, retainedWindow.windowEnd - retainedWindow.windowStart);
  const visibleCount = Math.max(1, absoluteVisibleEnd - absoluteVisibleStart);
  const requestedCount = Math.min(
    retainedWindow.totalCount,
    Math.max(currentCount, visibleCount + marginItems * 2),
  );
  const visibleCenter = (absoluteVisibleStart + absoluteVisibleEnd) / 2;
  let startIndex = Math.round(visibleCenter - requestedCount / 2);
  startIndex = Math.max(0, Math.min(retainedWindow.totalCount - requestedCount, startIndex));

  const overlapItems = Math.max(marginItems, Math.ceil(requestedCount / 3));
  const edgeShiftItems = Math.max(1, requestedCount - overlapItems);
  const shiftDirection = resolveKernelWindowShiftDirection({
    edgeDirection: args.edgeDirection,
    navigationDirection: args.navigationDirection,
    needsEarlierWindow,
    needsLaterWindow,
  });
  if (shiftDirection === 'earlier') {
    startIndex = Math.min(startIndex, retainedWindow.windowStart - edgeShiftItems);
  } else if (shiftDirection === 'later') {
    startIndex = Math.max(startIndex, retainedWindow.windowStart + edgeShiftItems);
  }
  startIndex = Math.max(0, Math.min(retainedWindow.totalCount - requestedCount, startIndex));

  const anchorAbsoluteIndex =
    typeof args.anchorAbsoluteIndex === 'number' && Number.isFinite(args.anchorAbsoluteIndex)
      ? Math.max(0, Math.min(retainedWindow.totalCount - 1, args.anchorAbsoluteIndex))
      : null;
  if (anchorAbsoluteIndex !== null) {
    if (anchorAbsoluteIndex < startIndex) {
      startIndex = anchorAbsoluteIndex;
    } else if (anchorAbsoluteIndex >= startIndex + requestedCount) {
      startIndex = anchorAbsoluteIndex - requestedCount + 1;
    }
    startIndex = Math.max(0, Math.min(retainedWindow.totalCount - requestedCount, startIndex));
  }

  if (
    startIndex === retainedWindow.windowStart &&
    requestedCount === retainedWindow.windowEnd - retainedWindow.windowStart
  ) {
    return null;
  }

  return { startIndex, count: requestedCount };
}

export function syncViewportScrollPosition(
  viewport: HTMLDivElement,
  targetScrollTop: number,
): boolean {
  const maxScrollTop = Math.max(0, viewport.scrollHeight - viewport.clientHeight);
  const nextScrollTop = Math.max(0, Math.min(targetScrollTop, maxScrollTop));
  if (Math.abs(nextScrollTop - viewport.scrollTop) <= 1) {
    return false;
  }

  viewport.scrollTop = nextScrollTop;
  return Math.abs(viewport.scrollTop - nextScrollTop) <= 1;
}

export function captureViewportAnchor<TKey extends string = string>(args: {
  viewport: HTMLDivElement;
  renderedNodes: Iterable<VirtualizerRenderedNode<TKey>>;
}): VirtualizerAnchor<TKey> | null {
  const viewportRect = args.viewport.getBoundingClientRect();
  let bestAnchor: VirtualizerAnchor<TKey> | null = null;
  for (const rendered of args.renderedNodes) {
    if (typeof rendered.node.getBoundingClientRect !== 'function') {
      continue;
    }

    const nodeRect = rendered.node.getBoundingClientRect();
    if (nodeRect.bottom < viewportRect.top || nodeRect.top > viewportRect.bottom) {
      continue;
    }

    const topOffsetPx = nodeRect.top - viewportRect.top;
    if (!bestAnchor || topOffsetPx < bestAnchor.topOffsetPx) {
      bestAnchor = {
        key: rendered.key,
        topOffsetPx,
        absoluteIndex: rendered.absoluteIndex,
      };
    }
  }

  return bestAnchor;
}

export function restoreViewportAnchor<TKey extends string = string>(args: {
  viewport: HTMLDivElement;
  anchor: VirtualizerAnchor<TKey>;
  resolveNode: (key: TKey) => HTMLElement | null | undefined;
}): boolean {
  const anchorNode = args.resolveNode(args.anchor.key);
  if (!anchorNode || typeof anchorNode.getBoundingClientRect !== 'function') {
    return false;
  }

  const viewportRect = args.viewport.getBoundingClientRect();
  const anchorRect = anchorNode.getBoundingClientRect();
  return syncViewportScrollPosition(
    args.viewport,
    args.viewport.scrollTop + (anchorRect.top - viewportRect.top - args.anchor.topOffsetPx),
  );
}

export function resolveScrollCompensationDelta(args: {
  changes: readonly VirtualizerMeasuredItemChange[];
  anchorAbsoluteIndex: number | null | undefined;
}): number {
  if (args.anchorAbsoluteIndex === null || args.anchorAbsoluteIndex === undefined) {
    return 0;
  }

  let delta = 0;
  for (const change of args.changes) {
    if (change.absoluteIndex >= args.anchorAbsoluteIndex) {
      continue;
    }

    delta += change.nextSize - change.previousSize;
  }

  return delta;
}
