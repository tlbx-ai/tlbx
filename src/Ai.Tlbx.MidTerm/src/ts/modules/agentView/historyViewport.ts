import { estimateHistoryEntryHeight } from './historyContent';
import type {
  HistoryIndexRange,
  HistoryScrollMetrics,
  HistoryScrollMode,
  HistoryVirtualWindow,
  AppServerControlHistoryEntry,
  SessionAppServerControlViewState,
} from './types';
import { DEFAULT_APP_SERVER_CONTROL_HISTORY_VIRTUALIZER_CONFIG } from './historyVirtualizer';
import {
  buildVirtualizerWindowKey,
  computeVirtualWindow as computeVirtualizerWindow,
  computeVisibleRange as computeVirtualizerVisibleRange,
} from '../../utils/virtualizer';

const AUTO_SCROLL_BOTTOM_THRESHOLD_PX = 64;
export const HISTORY_VIRTUALIZE_AFTER = 50;
export const APP_SERVER_CONTROL_HISTORY_OVERSCAN_ITEMS =
  DEFAULT_APP_SERVER_CONTROL_HISTORY_VIRTUALIZER_CONFIG.overscanItems;
export const APP_SERVER_CONTROL_HISTORY_INDEX_SCROLL_STEP_PX = 4;

type HistoryHeightResolver = (entry: AppServerControlHistoryEntry, index: number) => number;

export function stabilizeHistoryEntryOrder(
  entries: readonly AppServerControlHistoryEntry[],
): AppServerControlHistoryEntry[] {
  return [...entries].sort(
    (left, right) => left.order - right.order || left.id.localeCompare(right.id),
  );
}

/**
 * Protects the user's reading position during streaming turns so AppServerControl only
 * auto-pins when they are effectively already following the live edge.
 */
export function isScrollContainerNearBottom(position: {
  scrollTop: number;
  clientHeight: number;
  scrollHeight: number;
}): boolean {
  const { scrollTop, clientHeight, scrollHeight } = position;
  if (![scrollTop, clientHeight, scrollHeight].every(Number.isFinite)) {
    return true;
  }

  return scrollHeight - clientHeight - scrollTop <= AUTO_SCROLL_BOTTOM_THRESHOLD_PX;
}

export function resolveHistoryScrollMode(args: {
  previousMode: HistoryScrollMode;
  previous: HistoryScrollMetrics | null;
  current: HistoryScrollMetrics;
  userInitiated: boolean;
  pendingAnchorRestore: boolean;
  hasNewerHistory?: boolean;
}): HistoryScrollMode {
  if (args.pendingAnchorRestore) {
    return 'restore-anchor';
  }

  const nearBottom = !args.hasNewerHistory && isScrollContainerNearBottom(args.current);
  const userScrolledUp =
    args.previous !== null &&
    Number.isFinite(args.previous.scrollTop) &&
    Number.isFinite(args.current.scrollTop) &&
    args.current.scrollTop < args.previous.scrollTop - 1;

  if (args.userInitiated) {
    if (userScrolledUp) {
      return 'browse';
    }

    return nearBottom ? 'follow' : 'browse';
  }

  if (args.previousMode === 'follow' && userScrolledUp) {
    return 'browse';
  }

  if (args.previousMode === 'follow') {
    return 'follow';
  }

  return 'browse';
}

export function isHistoryScrollModePinned(mode: HistoryScrollMode): boolean {
  return mode === 'follow';
}

export function setHistoryScrollMode(
  state: SessionAppServerControlViewState,
  mode: HistoryScrollMode,
): HistoryScrollMode {
  state.historyScrollMode = mode;
  state.historyAutoScrollPinned = isHistoryScrollModePinned(mode);
  if (state.historyAutoScrollPinned) state.historyReaderAnchor = null;
  return mode;
}

/**
 * Virtualizes long histories across viewport sizes so AppServerControl keeps a bounded
 * DOM even during extended agent runs.
 */
export function computeHistoryVirtualWindow(
  entries: ReadonlyArray<AppServerControlHistoryEntry>,
  scrollTop: number,
  clientHeight: number,
  clientWidth = typeof window === 'undefined' ? 960 : window.innerWidth,
  resolveEntryHeight?: HistoryHeightResolver,
): HistoryVirtualWindow {
  if (entries.length <= HISTORY_VIRTUALIZE_AFTER) {
    return {
      start: 0,
      end: entries.length,
      topSpacerPx: 0,
      bottomSpacerPx: 0,
    };
  }

  const resolveHeight =
    resolveEntryHeight ??
    ((entry: AppServerControlHistoryEntry) => estimateHistoryEntryHeight(entry, clientWidth));
  return computeVirtualizerWindow({
    items: entries,
    scrollTop,
    clientHeight,
    overscanItems: APP_SERVER_CONTROL_HISTORY_OVERSCAN_ITEMS,
    resolveItemSize: (entry, index) => resolveHeight(entry, index),
  });
}

export function computeHistoryVisibleRange(
  entries: ReadonlyArray<AppServerControlHistoryEntry>,
  scrollTop: number,
  clientHeight: number,
  clientWidth = typeof window === 'undefined' ? 960 : window.innerWidth,
  resolveEntryHeight?: HistoryHeightResolver,
): HistoryIndexRange {
  const resolveHeight =
    resolveEntryHeight ??
    ((entry: AppServerControlHistoryEntry) => estimateHistoryEntryHeight(entry, clientWidth));
  return computeVirtualizerVisibleRange({
    items: entries,
    scrollTop,
    clientHeight,
    overscanItems: 0,
    resolveItemSize: (entry, index) => resolveHeight(entry, index),
  });
}

export function buildHistoryVirtualWindowKey(window: HistoryVirtualWindow): string {
  return buildVirtualizerWindowKey(window);
}

export function hasActiveAppServerControlSelectionInPanel(
  panel: ParentNode | null | undefined,
  selection:
    | Pick<Selection, 'rangeCount' | 'isCollapsed' | 'getRangeAt'>
    | null
    | undefined = resolveCurrentSelection(),
): boolean {
  if (!panel || !selection || selection.isCollapsed || selection.rangeCount <= 0) {
    return false;
  }

  const range = selection.getRangeAt(0);
  const startNode = range.startContainer;
  const endNode = range.endContainer;
  return panel.contains(startNode) || panel.contains(endNode);
}

function resolveCurrentSelection(): Pick<
  Selection,
  'rangeCount' | 'isCollapsed' | 'getRangeAt'
> | null {
  if (typeof window === 'undefined' || typeof window.getSelection !== 'function') {
    return null;
  }

  return window.getSelection();
}
