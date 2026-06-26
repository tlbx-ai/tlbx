import { estimateHistoryEntryHeight } from './historyContent';
import type { AppServerControlHistoryEntry, SessionAppServerControlViewState } from './types';
import {
  activateVirtualizerMeasurementBucket,
  recordMeasuredItemSize,
  resolveRepresentativeItemSize,
  resolveVirtualizerMeasurementWidthBucket,
  resolveVirtualizerViewportWidth,
  type VirtualizerMeasurementState,
} from '../../utils/virtualizer';

function ensureMeasurementBucketState(state: SessionAppServerControlViewState): void {
  const mutableState = state as {
    historyMeasuredHeightsByBucket?: Map<number, Map<string, number>>;
    historyObservedHeightsByBucket?: Map<number, Map<string, number>>;
    historyObservedHeightSamplesByBucket?: Map<number, Map<string, number[]>>;
    historyMeasuredHeights?: Map<string, number>;
    historyObservedHeights?: Map<string, number>;
  };
  mutableState.historyMeasuredHeightsByBucket ??= new Map<number, Map<string, number>>();
  mutableState.historyObservedHeightsByBucket ??= new Map<number, Map<string, number>>();
  mutableState.historyObservedHeightSamplesByBucket ??= new Map<number, Map<string, number[]>>();
  mutableState.historyMeasuredHeights ??= new Map<string, number>();
  mutableState.historyObservedHeights ??= new Map<string, number>();
  state.historyMeasuredHeightsByBucket = mutableState.historyMeasuredHeightsByBucket;
  state.historyObservedHeightsByBucket = mutableState.historyObservedHeightsByBucket;
  state.historyObservedHeightSamplesByBucket = mutableState.historyObservedHeightSamplesByBucket;
  state.historyMeasuredHeights = mutableState.historyMeasuredHeights;
  state.historyObservedHeights = mutableState.historyObservedHeights;
}

function createMeasurementStateAdapter(
  state: SessionAppServerControlViewState,
): VirtualizerMeasurementState {
  return {
    measuredSizes: state.historyMeasuredHeights,
    observedSizes: state.historyObservedHeights,
    measuredSizesByBucket: state.historyMeasuredHeightsByBucket,
    observedSizesByBucket: state.historyObservedHeightsByBucket,
    observedSizeSamplesByBucket: state.historyObservedHeightSamplesByBucket,
    measuredWidthBucket: state.historyMeasuredWidthBucket,
    lastWindowKey: state.historyLastVirtualWindowKey,
  };
}

function syncMeasurementStateAdapter(
  state: SessionAppServerControlViewState,
  adapter: VirtualizerMeasurementState,
): void {
  state.historyMeasuredHeights = adapter.measuredSizes;
  state.historyObservedHeights = adapter.observedSizes;
  state.historyMeasuredHeightsByBucket = adapter.measuredSizesByBucket;
  state.historyObservedHeightsByBucket = adapter.observedSizesByBucket;
  state.historyObservedHeightSamplesByBucket = adapter.observedSizeSamplesByBucket;
  state.historyMeasuredWidthBucket = adapter.measuredWidthBucket;
  state.historyLastVirtualWindowKey = adapter.lastWindowKey;
}

function normalizeEstimatedHeight(height: number | null | undefined): number | null {
  if (!Number.isFinite(height) || (height ?? 0) <= 0) {
    return null;
  }

  const normalizedHeight = height ?? 0;
  return Math.max(1, Math.round(normalizedHeight));
}

export function resolveHistoryMeasurementWidthBucket(clientWidth: number): number {
  return resolveVirtualizerMeasurementWidthBucket(clientWidth);
}

export function resolveHistoryWindowViewportWidth(
  viewport: Pick<HTMLDivElement, 'clientWidth'> | null | undefined,
): number | undefined {
  return resolveVirtualizerViewportWidth(viewport);
}

export function activateHistoryMeasurementBucket(
  state: SessionAppServerControlViewState,
  clientWidth: number,
): number {
  ensureMeasurementBucketState(state);
  const adapter = createMeasurementStateAdapter(state);
  const widthBucket = activateVirtualizerMeasurementBucket(adapter, clientWidth);
  syncMeasurementStateAdapter(state, adapter);
  return widthBucket;
}

export function recordHistoryMeasuredHeight(
  state: SessionAppServerControlViewState,
  entryId: string,
  measuredHeight: number,
  clientWidth: number,
): boolean {
  ensureMeasurementBucketState(state);
  const adapter = createMeasurementStateAdapter(state);
  const changed = recordMeasuredItemSize(adapter, entryId, measuredHeight, clientWidth);
  syncMeasurementStateAdapter(state, adapter);
  return changed;
}

export function pruneHistoryMeasurementCache(
  state: SessionAppServerControlViewState,
  entries: readonly AppServerControlHistoryEntry[],
): void {
  ensureMeasurementBucketState(state);
  const retainedIds = new Set(entries.map((entry) => entry.id));
  pruneMeasurementMap(state.historyMeasuredHeights, retainedIds);
  pruneMeasurementMap(state.historyObservedHeights, retainedIds);
  pruneBucketMap(state.historyMeasuredHeightsByBucket, retainedIds);
  pruneBucketMap(state.historyObservedHeightsByBucket, retainedIds);
  pruneBucketMap(state.historyObservedHeightSamplesByBucket, retainedIds);
}

function pruneMeasurementMap<T>(map: Map<string, T>, retainedIds: ReadonlySet<string>): void {
  for (const key of map.keys()) {
    if (!retainedIds.has(key)) {
      map.delete(key);
    }
  }
}

function pruneBucketMap<T>(
  buckets: Map<number, Map<string, T>>,
  retainedIds: ReadonlySet<string>,
): void {
  for (const [bucketKey, bucket] of buckets) {
    pruneMeasurementMap(bucket, retainedIds);
    if (bucket.size === 0) {
      buckets.delete(bucketKey);
    }
  }
}

export function resolveRepresentativeHistoryEntryHeight(
  observedHeights?: Iterable<number> | null,
): number {
  return resolveRepresentativeItemSize(observedHeights);
}

export function resolveHistoryEstimatedEntryHeight(
  entry: AppServerControlHistoryEntry,
  clientWidth: number,
): number {
  return (
    normalizeEstimatedHeight(entry.estimatedHeightPx) ??
    estimateHistoryEntryHeight(entry, clientWidth)
  );
}

export function resolveHistoryViewportEntryHeight(
  entry: AppServerControlHistoryEntry,
  state: SessionAppServerControlViewState | undefined,
  clientWidth: number,
): number {
  const estimatedHeight = resolveHistoryEstimatedEntryHeight(entry, clientWidth);
  if (!state) {
    return estimatedHeight;
  }

  activateHistoryMeasurementBucket(state, clientWidth);
  return (
    state.historyMeasuredHeights.get(entry.id) ??
    state.historyObservedHeights.get(entry.id) ??
    estimatedHeight
  );
}
