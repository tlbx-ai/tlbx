import { normalizeAppServerControlProvider } from './activationHelpers';
import { setHistoryScrollMode } from './historyViewport';
import type { SessionAppServerControlViewState } from './types';

export function syncAgentViewPresentation(
  panel: HTMLDivElement,
  provider: string | null | undefined = null,
): void {
  panel.dataset.appServerControlProvider = normalizeAppServerControlProvider(provider);
}

export function prepareAppServerControlForForeground(
  state: SessionAppServerControlViewState,
): void {
  if (
    state.historyScrollMode === 'restore-anchor' &&
    state.pendingHistoryPrependAnchor === null &&
    state.pendingHistoryLayoutAnchor === null
  ) {
    setHistoryScrollMode(state, state.historyAutoScrollPinned ? 'follow' : 'browse');
  }

  if (state.historyAutoScrollPinned) {
    state.historyNavigatorMode = 'follow-live';
    state.historyNavigatorDragTargetIndex = null;
  } else if (state.historyNavigatorMode !== 'drag-preview') {
    state.historyNavigatorMode = 'browse';
  }
}
