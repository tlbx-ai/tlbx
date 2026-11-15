import { resolveRepresentativeHistoryEntryHeight } from './historyMeasurements';
import { resolveViewportDrivenHistoryWindowCount } from './historyWindowSizing';
import type { SessionAppServerControlViewState } from './types';

export interface AppServerControlHistoryVirtualizerConfig {
  overscanItems: number;
  fetchAheadItems: number;
}

export const APP_SERVER_CONTROL_HISTORY_MIN_FETCH_AHEAD_ITEMS = 20;

export const DEFAULT_APP_SERVER_CONTROL_HISTORY_VIRTUALIZER_CONFIG: AppServerControlHistoryVirtualizerConfig =
  {
    overscanItems: 8,
    fetchAheadItems: 30,
  };

export function resolveAppServerControlHistoryFetchAheadItems(
  config: AppServerControlHistoryVirtualizerConfig = DEFAULT_APP_SERVER_CONTROL_HISTORY_VIRTUALIZER_CONFIG,
): number {
  return Math.max(APP_SERVER_CONTROL_HISTORY_MIN_FETCH_AHEAD_ITEMS, config.fetchAheadItems);
}

export function resolveAppServerControlHistoryFetchThresholdPx(
  state: SessionAppServerControlViewState,
  config: AppServerControlHistoryVirtualizerConfig = DEFAULT_APP_SERVER_CONTROL_HISTORY_VIRTUALIZER_CONFIG,
): number {
  const fetchAheadItems = resolveAppServerControlHistoryFetchAheadItems(config);
  return Math.max(
    1,
    Math.round(
      resolveRepresentativeHistoryEntryHeight(state.historyObservedHeights.values()) *
        fetchAheadItems,
    ),
  );
}

export function resolveAppServerControlHistoryWindowTargetCount(
  viewport: HTMLDivElement | null | undefined,
  fallbackCount: number,
  observedHeights?: Iterable<number> | null,
  config: AppServerControlHistoryVirtualizerConfig = DEFAULT_APP_SERVER_CONTROL_HISTORY_VIRTUALIZER_CONFIG,
): number {
  const fetchAheadItems = resolveAppServerControlHistoryFetchAheadItems(config);
  return resolveViewportDrivenHistoryWindowCount(
    viewport,
    fetchAheadItems,
    fallbackCount,
    observedHeights,
  );
}
