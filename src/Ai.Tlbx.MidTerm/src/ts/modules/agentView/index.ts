/* eslint-disable max-lines -- AppServerControl activation/runtime orchestration remains consolidated here until the active path is split into smaller modules. */
import { createLogger } from '../logging';
import {
  buildAppServerControlDebugScenario,
  type AppServerControlDebugScenarioName,
} from './debugScenario';
import {
  appendActivationTrace,
  classifyAppServerControlActivationIssue,
  describeError,
  ensureAppServerControlActivationIsCurrent,
  isStaleAppServerControlActivationError,
  setActivationState,
  shouldShowAppServerControlDevErrorDialog,
} from './activationHelpers';
import type {
  AssistantMarkdownCacheEntry,
  HistoryRenderedNode,
  AppServerControlHistoryEntry,
  SessionAppServerControlViewState,
} from './types';
import {
  applyOptimisticAppServerControlTurns,
  buildActivationHistoryEntries,
  buildAppServerControlHistoryEntries,
  buildAppServerControlRuntimeStats,
  cloneHistoryAttachments,
  preservePersistentCommandEntries,
  syncBusyIndicatorTicker,
  withActivationIssueNotice,
  withInlineAppServerControlStatus,
  withLiveAssistantState,
  withTrailingBusyIndicator,
  withTurnDurationNotes,
} from './historyProcessing';
import {
  applyCanonicalAppServerControlDelta,
  applyAppServerControlHistoryWindowState,
  collapseSnapshotToLatestWindow,
} from './snapshotState';
import {
  hasActiveAppServerControlSelectionInPanel,
  resolveHistoryScrollMode,
  setHistoryScrollMode,
  stabilizeHistoryEntryOrder,
} from './historyViewport';
import {
  resolveHistoryWindowViewportWidth,
  resolveRepresentativeHistoryEntryHeight,
} from './historyMeasurements';
import { createAgentHistoryDom } from './historyDom';
import { createAgentHistoryRender, resolveHistoryNavigatorTarget } from './historyRender';
import {
  DEFAULT_APP_SERVER_CONTROL_HISTORY_VIRTUALIZER_CONFIG,
  resolveAppServerControlHistoryFetchAheadItems,
  resolveAppServerControlHistoryFetchThresholdPx,
  resolveAppServerControlHistoryWindowTargetCount,
} from './historyVirtualizer';
import {
  applyFetchedAppServerControlHistoryWindow,
  hasRenderableAppServerControlHistory,
} from './historyWindowState';
import {
  resetAppServerControlHistoryTrace,
  traceAppServerControlHistoryCompact,
  traceAppServerControlHistoryFetch,
  traceAppServerControlHistoryPush,
  traceAppServerControlHistoryScroll,
} from './historyTrace';
import {
  prepareAppServerControlForForeground,
  syncAgentViewPresentation,
} from './viewPresentation';
import {
  ensureAgentViewSkeleton,
  APP_SERVER_CONTROL_DEBUG_SCENARIO_NAMES,
  normalizeAppServerControlDebugScenarioName,
} from './viewShell';
import {
  ensureSessionWrapper,
  getActiveTab,
  getTabPanel,
  onTabActivated,
  onTabDeactivated,
  setSessionAppServerControlAvailability,
  switchTab,
} from '../sessionTabs';
import {
  clearAppServerControlTurnSessionState,
  handleAppServerControlEscape,
  APP_SERVER_CONTROL_TURN_ACCEPTED_EVENT,
  APP_SERVER_CONTROL_TURN_FAILED_EVENT,
  APP_SERVER_CONTROL_TURN_SUBMITTED_EVENT,
  syncAppServerControlTurnExecutionState,
  type AppServerControlTurnAcceptedEventDetail,
  type AppServerControlTurnFailedEventDetail,
  type AppServerControlTurnSubmittedEventDetail,
} from '../appServerControl/input';
import {
  removeAppServerControlQuickSettingsSessionState,
  syncAppServerControlQuickSettingsFromSnapshot,
} from '../appServerControl/quickSettings';
import { showDevErrorDialog } from '../../utils/devErrorDialog';
import {
  attachSessionAppServerControl,
  detachSessionAppServerControl,
  type AppServerControlHistoryDelta,
  getAppServerControlHistoryWindow,
  openAppServerControlHistoryStream,
  updateAppServerControlHistoryStreamWindow,
  type AppServerControlHistorySnapshot,
} from '../../api/client';
import { t } from '../i18n';
import { $activeSessionId } from '../../stores';

const log = createLogger('agentView');
const viewStates = new Map<string, SessionAppServerControlViewState>();
const APP_SERVER_CONTROL_HISTORY_WINDOW_SIZE = 240;
const LIVE_HISTORY_RENDER_BATCH_MS = 250;
const USER_HISTORY_SCROLL_INTENT_WINDOW_MS = 900;
const HISTORY_FAST_WHEEL_DELTA_MIN_PX = 480;
const HISTORY_WHEEL_LINE_DELTA_PX = 40;
const HISTORY_NAVIGATOR_PREVIEW_COUNT = 5;
const HISTORY_NAVIGATOR_PREVIEW_THROTTLE_MS = 80;
const HISTORY_NAVIGATOR_HYDRATE_IDLE_MS = 120;
let appServerControlTurnLifecycleBound = false;
let appServerControlActiveSessionBound = false;
let appServerControlSelectionGuardBound = false;
let appServerControlForegroundRecoveryBound = false;
let appServerControlVisualViewportRecoveryBound = false;
let appServerControlSettingsRenderBound = false;
let appServerControlExistingPanelRecoveryBound = false;
let appServerControlForegroundRecoveryPending = false;

function createHistoryWindowRevision(sessionId: string): string {
  return `${sessionId}:${Date.now().toString(36)}:${Math.random().toString(36).slice(2, 10)}`;
}

function issueHistoryWindowRevision(
  sessionId: string,
  state: SessionAppServerControlViewState,
): string {
  const revision = createHistoryWindowRevision(sessionId);
  state.historyWindowRevision = revision;
  return revision;
}

function resolveRequestedHistoryViewportWidth(
  state: Pick<SessionAppServerControlViewState, 'historyViewport'> | null | undefined,
): number | undefined {
  return resolveHistoryWindowViewportWidth(state?.historyViewport);
}

function syncLiveHistoryWindowViewport(
  sessionId: string,
  state: SessionAppServerControlViewState,
): void {
  if (!state.disconnectStream) {
    return;
  }

  const viewportWidth = resolveRequestedHistoryViewportWidth(state);
  state.historyWindowViewportWidth = viewportWidth ?? state.historyWindowViewportWidth;
  if (state.historyWindowViewportWidth === null) {
    updateAppServerControlHistoryStreamWindow(
      sessionId,
      state.historyWindowStart,
      state.historyWindowCount,
      state.historyWindowRevision ?? undefined,
    );
    return;
  }

  updateAppServerControlHistoryStreamWindow(
    sessionId,
    state.historyWindowStart,
    state.historyWindowCount,
    state.historyWindowRevision ?? undefined,
    state.historyWindowViewportWidth,
  );
}

function requestAppServerControlHistoryWindow(
  sessionId: string,
  state: SessionAppServerControlViewState,
  startIndex: number | undefined,
  count: number | undefined,
  windowRevision: string,
): Promise<AppServerControlHistorySnapshot> {
  const viewportWidth = resolveRequestedHistoryViewportWidth(state);
  state.historyWindowViewportWidth = viewportWidth ?? state.historyWindowViewportWidth;
  return state.historyWindowViewportWidth === null
    ? getAppServerControlHistoryWindow(sessionId, startIndex, count, windowRevision)
    : getAppServerControlHistoryWindow(
        sessionId,
        startIndex,
        count,
        windowRevision,
        state.historyWindowViewportWidth,
      );
}

function connectAppServerControlHistoryStream(
  sessionId: string,
  state: SessionAppServerControlViewState,
  afterSequence: number,
  callbacks: Parameters<typeof openAppServerControlHistoryStream>[5],
): () => void {
  state.historyWindowViewportWidth =
    resolveRequestedHistoryViewportWidth(state) ?? state.historyWindowViewportWidth;
  return state.historyWindowViewportWidth === null
    ? openAppServerControlHistoryStream(
        sessionId,
        afterSequence,
        state.historyWindowStart,
        state.historyWindowCount,
        state.historyWindowRevision ?? issueHistoryWindowRevision(sessionId, state),
        callbacks,
      )
    : openAppServerControlHistoryStream(
        sessionId,
        afterSequence,
        state.historyWindowStart,
        state.historyWindowCount,
        state.historyWindowRevision ?? issueHistoryWindowRevision(sessionId, state),
        callbacks,
        state.historyWindowViewportWidth,
      );
}

const historyDom = createAgentHistoryDom({
  getState: (sessionId) => viewStates.get(sessionId),
  refreshAppServerControlSnapshot,
  renderCurrentAgentView: (sessionId) => {
    renderCurrentAgentView(sessionId);
  },
  retryAppServerControlActivation,
  logWarn: log.warn.bind(log),
});
const historyRender = createAgentHistoryRender({
  getState: (sessionId) => viewStates.get(sessionId),
  scheduleHistoryRender,
  syncAgentViewPresentation,
  createHistoryEntry: historyDom.createHistoryEntry,
  createHistoryPlaceholderBlock: historyDom.createHistoryPlaceholderBlock,
  syncBusyIndicatorEntry: historyDom.syncBusyIndicatorEntry,
  createRequestActionBlock: historyDom.createRequestActionBlock,
  pruneAssistantMarkdownCache: historyDom.pruneAssistantMarkdownCache,
  renderRuntimeStats: historyDom.renderRuntimeStats,
  syncViewportHistoryWindow: (sessionId) => {
    const state = viewStates.get(sessionId);
    if (!state || state.historyAutoScrollPinned) {
      return;
    }

    queueUrgentHistoryWindowViewportSync(sessionId, state);
  },
});

function appServerControlText(key: string, fallback: string): string {
  const translated = t(key);
  if (!translated || translated === key) {
    return fallback;
  }

  return translated;
}

function appServerControlFormat(
  key: string,
  fallback: string,
  replacements: Record<string, string | number>,
): string {
  return Object.entries(replacements).reduce(
    (text, [name, value]) => text.split(`{${name}}`).join(String(value)),
    appServerControlText(key, fallback),
  );
}

/**
 * Wires AppServerControl into the session-tab shell so supported agent sessions can open a
 * conversation-first surface without changing MidTerm's terminal-owned runtime
 * model underneath.
 */
export function initAgentView(): void {
  bindAppServerControlTurnLifecycle();
  bindActiveAppServerControlSessionRendering();
  bindAppServerControlSelectionGuard();
  bindAppServerControlSettingsRendering();
  bindAppServerControlForegroundRecovery();
  bindAppServerControlVisualViewportRecovery();

  const activateAgentPanel = (sessionId: string, panel: HTMLDivElement): void => {
    ensureAgentViewSkeleton(sessionId, panel, (targetSessionId) => {
      void handleAppServerControlEscape(targetSessionId);
    });
    const state = getOrCreateViewState(sessionId, panel);
    state.panel = panel;
    bindHistoryViewport(sessionId, state);
    prepareAppServerControlForForeground(state);
    void activateAgentView(sessionId).catch((error: unknown) => {
      if (!isStaleAppServerControlActivationError(error)) {
        throw error;
      }
    });
  };

  onTabActivated('agent', activateAgentPanel);

  const activateExistingAgentPanels = (): void => {
    if (typeof document === 'undefined' || typeof document.getElementsByClassName !== 'function') {
      return;
    }

    Array.from(document.getElementsByClassName('session-wrapper')).forEach((candidate) => {
      if (!(candidate instanceof HTMLElement) || candidate.dataset.activeTab !== 'agent') {
        return;
      }

      const sessionId = candidate.dataset.sessionId;
      const panel = candidate.querySelector<HTMLDivElement>(
        '.agent-tab-panel.active, [data-panel="agent"].active',
      );
      if (sessionId && panel) {
        activateAgentPanel(sessionId, panel);
      }
    });
  };

  $activeSessionId.subscribe((sessionId) => {
    if (!sessionId || getActiveTab(sessionId) !== 'agent') {
      return;
    }

    const panel = getTabPanel(sessionId, 'agent');
    if (panel) {
      activateAgentPanel(sessionId, panel);
    }
  });

  if (
    !appServerControlExistingPanelRecoveryBound &&
    typeof document !== 'undefined' &&
    typeof document.addEventListener === 'function'
  ) {
    document.addEventListener('click', () => {
      window.requestAnimationFrame(activateExistingAgentPanels);
    });
    appServerControlExistingPanelRecoveryBound = true;
  }

  window.requestAnimationFrame(() => {
    const sessionId = $activeSessionId.get();
    if (!sessionId || getActiveTab(sessionId) !== 'agent') {
      activateExistingAgentPanels();
      return;
    }

    const panel = getTabPanel(sessionId, 'agent');
    if (panel) {
      activateAgentPanel(sessionId, panel);
    }
    activateExistingAgentPanels();
  });

  onTabDeactivated('agent', (sessionId) => {
    const state = viewStates.get(sessionId);
    if (!state) {
      return;
    }

    releaseHiddenAppServerControlRenderState(state);
    void compactHiddenAppServerControlSessionHistory(sessionId, state);
  });

  log.info(() => 'Agent view initialized');
}

/**
 * Tears down per-session AppServerControl state when a session closes or loses the AppServerControl
 * surface so stale streams, timers, and attach state do not leak across turns.
 */
/* eslint-disable complexity -- teardown has to coordinate stream/timer/render cleanup in one place. */
export function destroyAgentView(sessionId: string): void {
  closeAppServerControlStream(sessionId);
  void detachSessionAppServerControl(sessionId).catch((error: unknown) => {
    log.warn(() => `Failed to detach AppServerControl for ${sessionId}: ${String(error)}`);
  });
  const state = viewStates.get(sessionId);
  clearPendingHistoryRenderBatch(state);
  if (state && state.historyRenderScheduled !== null) {
    window.cancelAnimationFrame(state.historyRenderScheduled);
  }
  if (state && state.busyIndicatorTickHandle !== null) {
    window.clearTimeout(state.busyIndicatorTickHandle);
  }
  if (state && state.historyNavigatorPreviewHandle !== null) {
    window.clearTimeout(state.historyNavigatorPreviewHandle);
  }
  if (state && state.historyNavigatorHydrateHandle !== null) {
    window.clearTimeout(state.historyNavigatorHydrateHandle);
  }
  state?.historyMeasurementObserver?.disconnect();
  state?.historyViewportResizeObserver?.disconnect();
  state?.historyRenderedNodes.clear();
  state?.historyObservedHeights.clear();
  if (state) {
    state.historyLeadingPlaceholders = [];
    state.historyTrailingPlaceholders = [];
    state.historyEmptyState = null;
    state.historyLastVoidSyncScrollTop = null;
    state.historyExpandedEntries.clear();
  }

  viewStates.delete(sessionId);
  resetAppServerControlHistoryTrace(sessionId);
  clearAppServerControlTurnSessionState(sessionId);
  removeAppServerControlQuickSettingsSessionState(sessionId);
}
/* eslint-enable complexity */

export function resetAgentViewRuntimeForTests(): void {
  for (const [sessionId, state] of viewStates) {
    clearPendingHistoryRenderBatch(state);
    if (state.historyRenderScheduled !== null) {
      window.cancelAnimationFrame(state.historyRenderScheduled);
    }
    if (state.busyIndicatorTickHandle !== null) {
      window.clearTimeout(state.busyIndicatorTickHandle);
    }
    if (state.historyNavigatorPreviewHandle !== null) {
      window.clearTimeout(state.historyNavigatorPreviewHandle);
    }
    if (state.historyNavigatorHydrateHandle !== null) {
      window.clearTimeout(state.historyNavigatorHydrateHandle);
    }
    state.disconnectStream?.();
    state.historyMeasurementObserver?.disconnect();
    state.historyViewportResizeObserver?.disconnect();
    clearAppServerControlTurnSessionState(sessionId);
    removeAppServerControlQuickSettingsSessionState(sessionId);
  }

  viewStates.clear();
  resetAppServerControlHistoryTrace();
  appServerControlTurnLifecycleBound = false;
  appServerControlActiveSessionBound = false;
  appServerControlSelectionGuardBound = false;
  appServerControlForegroundRecoveryBound = false;
  appServerControlVisualViewportRecoveryBound = false;
  appServerControlSettingsRenderBound = false;
  appServerControlExistingPanelRecoveryBound = false;
  appServerControlForegroundRecoveryPending = false;
}

/**
 * Exposes deterministic history fixtures so AppServerControl UI work can be iterated
 * and regression-tested without depending on a live agent runtime.
 */
export function getAppServerControlDebugScenarioNames(): readonly AppServerControlDebugScenarioName[] {
  return APP_SERVER_CONTROL_DEBUG_SCENARIO_NAMES;
}

/**
 * Loads representative AppServerControl history into an existing session panel to
 * speed up conversation UX and CSS tuning without depending on a live agent runtime.
 */
export function showAppServerControlDebugScenario(sessionId: string, scenario = 'mixed'): boolean {
  ensureSessionWrapper(sessionId);
  setSessionAppServerControlAvailability(sessionId, true);
  const panel = getTabPanel(sessionId, 'agent');
  if (!panel) {
    return false;
  }

  ensureAgentViewSkeleton(sessionId, panel, (targetSessionId) => {
    void handleAppServerControlEscape(targetSessionId);
  });
  const state = getOrCreateViewState(sessionId, panel);
  state.panel = panel;
  bindHistoryViewport(sessionId, state);

  const debugScenario = buildAppServerControlDebugScenario(
    sessionId,
    normalizeAppServerControlDebugScenarioName(scenario),
    window.location.origin,
  );
  state.snapshot = debugScenario.snapshot;
  state.debugScenarioActive = true;
  state.activationRunId += 1;
  state.streamConnected = true;
  state.refreshInFlight = false;
  state.historyViewportSyncPending = false;
  state.historyViewportSyncForcePending = false;
  state.activationState = 'ready';
  state.activationDetail = 'AppServerControl debug scenario loaded.';
  state.activationTrace = [];
  state.activationError = null;
  state.activationIssue = null;
  state.activationActionBusy = false;
  state.requestBusyIds.clear();
  state.historyWindowRevision = null;
  setHistoryScrollMode(state, 'browse');
  state.historyNavigatorMode = 'browse';
  state.historyNavigatorDragTargetIndex = null;
  switchTab(sessionId, 'agent');
  renderCurrentAgentView(sessionId, { immediate: true, force: true });
  return true;
}

async function activateAgentView(sessionId: string): Promise<void> {
  const state = viewStates.get(sessionId);
  if (!state) {
    return;
  }

  if (state.debugScenarioActive) {
    renderCurrentAgentView(sessionId);
    return;
  }

  if (state.snapshot && state.disconnectStream && state.streamConnected) {
    renderCurrentAgentView(sessionId, { immediate: true });
    if (state.historyAutoScrollPinned && state.snapshot.hasNewerHistory) {
      void refreshAppServerControlSnapshot(sessionId, { latestWindow: true });
    }
    return;
  }

  state.activationRunId += 1;
  const activationRunId = state.activationRunId;

  const hasExistingHistory = state.snapshot !== null;
  if (hasExistingHistory) {
    await resumeAppServerControlFromHistory(sessionId, state, activationRunId);
    return;
  }

  state.snapshot = null;
  state.streamConnected = false;
  state.activationTrace = [];
  state.activationError = null;
  state.activationIssue = null;
  state.activationActionBusy = false;

  setActivationState(
    state,
    'opening',
    appServerControlText(
      'appServerControl.activation.opening.detail',
      'AppServerControl pane opened. Preparing AppServerControl runtime attach.',
    ),
    appServerControlText(
      'appServerControl.activation.opening.summary',
      'AppServerControl pane opened.',
    ),
    appServerControlText(
      'appServerControl.activation.opening.body',
      'MidTerm is opening the AppServerControl conversation surface for this session.',
    ),
  );
  setActivationState(
    state,
    'attaching',
    appServerControlText(
      'appServerControl.activation.attaching.detail',
      'Requesting AppServerControl runtime attach.',
    ),
    appServerControlText(
      'appServerControl.activation.attaching.summary',
      'Attaching AppServerControl runtime.',
    ),
    appServerControlText(
      'appServerControl.activation.attaching.body',
      'Starting or reconnecting the backend-owned AppServerControl runtime for this session.',
    ),
  );
  renderCurrentAgentView(sessionId);

  const restoredReadonlyHistory = await tryLoadReadonlyAppServerControlHistory(
    sessionId,
    state,
    activationRunId,
  );
  ensureAppServerControlActivationIsCurrent(state, activationRunId);
  if (restoredReadonlyHistory) {
    renderCurrentAgentView(sessionId);
    await resumeAppServerControlFromHistory(sessionId, state, activationRunId);
    return;
  }

  try {
    await attachSessionAppServerControl(sessionId);
    ensureAppServerControlActivationIsCurrent(state, activationRunId);
    setActivationState(
      state,
      'waiting-history-window',
      appServerControlText(
        'appServerControl.activation.waitingSnapshot.detail',
        'AppServerControl runtime accepted the attach request.',
      ),
      appServerControlText(
        'appServerControl.activation.waitingSnapshot.summary',
        'AppServerControl runtime attached.',
      ),
      appServerControlText(
        'appServerControl.activation.waitingSnapshot.body',
        'Waiting for the first canonical AppServerControl history window from MidTerm.',
      ),
    );
    renderCurrentAgentView(sessionId);

    const snapshot = await waitForInitialAppServerControlSnapshot(
      sessionId,
      state,
      activationRunId,
    );
    ensureAppServerControlActivationIsCurrent(state, activationRunId);

    setActivationState(
      state,
      'connecting-stream',
      appServerControlText(
        'appServerControl.activation.connectingStream.detail',
        'AppServerControl history window is ready. Connecting the live stream.',
      ),
      appServerControlText(
        'appServerControl.activation.connectingStream.summary',
        'AppServerControl history window ready.',
      ),
      appServerControlText(
        'appServerControl.activation.connectingStream.body',
        'Opening the live AppServerControl stream so the history updates in real time.',
      ),
    );
    renderCurrentAgentView(sessionId);
    state.snapshot = snapshot;
    state.streamConnected = false;
    renderCurrentAgentView(sessionId, { immediate: true });
    openLiveAppServerControlStream(sessionId, snapshot.latestSequence);
  } catch (error) {
    if (isStaleAppServerControlActivationError(error)) {
      return;
    }

    log.warn(() => `Failed to activate AppServerControl for ${sessionId}: ${String(error)}`);
    const restoredFallbackHistory = await tryLoadReadonlyAppServerControlHistory(
      sessionId,
      state,
      activationRunId,
    );
    ensureAppServerControlActivationIsCurrent(state, activationRunId);
    if (restoredFallbackHistory) {
      log.warn(
        () =>
          `AppServerControl attach failed for ${sessionId}, but canonical history was restored.`,
      );
      appendActivationTrace(
        state,
        'warning',
        'history-restored',
        appServerControlText(
          'appServerControl.activation.historyRestored.summary',
          'Canonical AppServerControl history restored.',
        ),
        appServerControlText(
          'appServerControl.activation.historyRestored.body',
          'MidTerm recovered canonical AppServerControl history after the initial attach failed, so it is retrying the live attach automatically.',
        ),
      );
      await resumeAppServerControlFromHistory(sessionId, state, activationRunId);
      return;
    }

    state.activationError = describeError(error);
    state.activationIssue = classifyAppServerControlActivationIssue(error, false);
    setActivationState(
      state,
      'failed',
      appServerControlText(
        'appServerControl.activation.startupFailed.detail',
        'AppServerControl startup failed before the first stable history window became available.',
      ),
      appServerControlText(
        'appServerControl.activation.startupFailed.summary',
        'AppServerControl startup failed.',
      ),
      state.activationError,
      'attention',
    );
    if (shouldShowAppServerControlDevErrorDialog(state.activationIssue)) {
      showDevErrorDialog({
        title: appServerControlText(
          'appServerControl.error.openTitle',
          'AppServerControl failed to open',
        ),
        context: `AppServerControl activation failed for session ${sessionId}`,
        error,
      });
    }
    renderCurrentAgentView(sessionId);
  }
}

async function resumeAppServerControlFromHistory(
  sessionId: string,
  state: SessionAppServerControlViewState,
  activationRunId: number,
): Promise<void> {
  ensureAppServerControlActivationIsCurrent(state, activationRunId);
  state.streamConnected = false;
  renderCurrentAgentView(sessionId);

  try {
    await attachSessionAppServerControl(sessionId);
    ensureAppServerControlActivationIsCurrent(state, activationRunId);
    await refreshAppServerControlSnapshot(sessionId, {
      latestWindow: state.historyAutoScrollPinned,
    });
    ensureAppServerControlActivationIsCurrent(state, activationRunId);
    openLiveAppServerControlStream(sessionId, state.snapshot?.latestSequence ?? 0);
  } catch (error) {
    if (isStaleAppServerControlActivationError(error)) {
      return;
    }

    log.warn(() => `Failed to resume AppServerControl for ${sessionId}: ${String(error)}`);
    state.activationError = describeError(error);
    state.activationIssue = classifyAppServerControlActivationIssue(error, true);
    renderCurrentAgentView(sessionId);
  }
}

async function tryLoadReadonlyAppServerControlHistory(
  sessionId: string,
  state: SessionAppServerControlViewState,
  activationRunId: number,
): Promise<boolean> {
  try {
    state.historyWindowTargetCount = resolveAppServerControlHistoryWindowTargetCount(
      state.historyViewport,
      APP_SERVER_CONTROL_HISTORY_WINDOW_SIZE,
      state.historyObservedHeights.values(),
    );
    const windowRevision = issueHistoryWindowRevision(sessionId, state);
    const snapshot = await requestAppServerControlHistoryWindow(
      sessionId,
      state,
      undefined,
      state.historyWindowTargetCount,
      windowRevision,
    );
    ensureAppServerControlActivationIsCurrent(state, activationRunId);
    const hasSnapshotHistory = hasRenderableAppServerControlHistory(snapshot);
    if (!hasSnapshotHistory) {
      return false;
    }

    applyFetchedAppServerControlHistoryWindow(sessionId, state, snapshot);
    state.streamConnected = false;
    state.activationTrace = [];
    return true;
  } catch (error) {
    log.warn(
      () => `Failed to load AppServerControl history fallback for ${sessionId}: ${String(error)}`,
    );
    return false;
  }
}

function getOrCreateViewState(
  sessionId: string,
  panel: HTMLDivElement,
): SessionAppServerControlViewState {
  const existing = viewStates.get(sessionId);
  if (existing) {
    return existing;
  }

  const initialHistoryWindowCount = resolveAppServerControlHistoryWindowTargetCount(
    panel.querySelector<HTMLDivElement>('[data-agent-field="history"]'),
    APP_SERVER_CONTROL_HISTORY_WINDOW_SIZE,
  );

  const created: SessionAppServerControlViewState = {
    panel,
    snapshot: null,
    debugScenarioActive: false,
    activationRunId: 0,
    historyViewport: null,
    historyProgressNav: null,
    historyProgressThumb: null,
    historyEntries: [],
    historyWindowStart: 0,
    historyWindowCount: initialHistoryWindowCount,
    historyWindowTargetCount: initialHistoryWindowCount,
    historyViewportSyncPending: false,
    historyViewportSyncForcePending: false,
    historyViewportSyncQueuedDuringRefresh: false,
    historyViewportSyncSuppressUntil: 0,
    disconnectStream: null,
    streamConnected: false,
    refreshInFlight: false,
    requestBusyIds: new Set<string>(),
    requestDraftAnswersById: {},
    requestQuestionIndexById: {},
    historyScrollMode: 'follow',
    historyAutoScrollPinned: true,
    historyLastScrollMetrics: null,
    historyLastUserScrollIntentAt: 0,
    historyLastVoidSyncScrollTop: null,
    historyWindowRevision: null,
    historyWindowViewportWidth: null,
    historyNavigatorMode: 'follow-live',
    historyNavigatorAnchorIndex: null,
    historyNavigatorDragTargetIndex: null,
    historyNavigatorQueuedTargetIndex: null,
    historyNavigatorQueuedRequestKind: null,
    historyNavigatorPreviewHandle: null,
    historyNavigatorHydrateHandle: null,
    historyNavigatorLastPreviewRequestAt: 0,
    historyPendingJumpTargetIndex: null,
    historyPendingJumpAlign: null,
    historyRenderScheduled: null,
    historyRenderBatchHandle: null,
    activationState: 'idle',
    activationDetail: '',
    activationTrace: [],
    activationError: null,
    activationIssue: null,
    activationActionBusy: false,
    optimisticTurns: [],
    renderDirty: false,
    assistantMarkdownCache: new Map<string, AssistantMarkdownCacheEntry>(),
    historyRenderedNodes: new Map<string, HistoryRenderedNode>(),
    historyMeasuredHeights: new Map<string, number>(),
    historyObservedHeights: new Map<string, number>(),
    historyMeasuredHeightsByBucket: new Map<number, Map<string, number>>(),
    historyObservedHeightsByBucket: new Map<number, Map<string, number>>(),
    historyObservedHeightSamplesByBucket: new Map<number, Map<string, number[]>>(),
    historyMeasuredWidthBucket: 0,
    historyMeasurementObserver: null,
    historyViewportResizeObserver: null,
    historyViewportSize: null,
    historyLeadingPlaceholders: [],
    historyTrailingPlaceholders: [],
    historyEmptyState: null,
    pendingHistoryPrependAnchor: null,
    pendingHistoryLayoutAnchor: null,
    historyLastVirtualWindowKey: null,
    historyExpandedEntries: new Set<string>(),
    runtimeStats: null,
    busyIndicatorTickHandle: null,
    completedTurnDurationEntries: new Map<string, AppServerControlHistoryEntry>(),
  };

  viewStates.set(sessionId, created);
  return created;
}

function syncHistoryProgressNavigator(sessionId: string): void {
  historyRender.syncViewportOffset(sessionId);
}

function resolveHistoryKeyboardStepPx(state: SessionAppServerControlViewState): number {
  return Math.max(
    24,
    Math.round(
      resolveRepresentativeHistoryEntryHeight(state.historyObservedHeights.values()) * 0.5,
    ),
  );
}

function resolveHistoryWheelDeltaYPx(event: WheelEvent, viewport: HTMLDivElement): number {
  if (event.deltaMode === 1) {
    return event.deltaY * HISTORY_WHEEL_LINE_DELTA_PX;
  }

  if (event.deltaMode === 2) {
    return event.deltaY * Math.max(1, viewport.clientHeight);
  }

  return event.deltaY;
}

function clearHistoryNavigatorPreviewTimer(state: SessionAppServerControlViewState): void {
  if (state.historyNavigatorPreviewHandle === null) {
    return;
  }

  window.clearTimeout(state.historyNavigatorPreviewHandle);
  state.historyNavigatorPreviewHandle = null;
}

function clearHistoryNavigatorHydrateTimer(state: SessionAppServerControlViewState): void {
  if (state.historyNavigatorHydrateHandle === null) {
    return;
  }

  window.clearTimeout(state.historyNavigatorHydrateHandle);
  state.historyNavigatorHydrateHandle = null;
}

function clearQueuedHistoryNavigatorRequest(state: SessionAppServerControlViewState): void {
  state.historyNavigatorQueuedTargetIndex = null;
  state.historyNavigatorQueuedRequestKind = null;
}

function resolveHistoryJumpAlign(
  state: SessionAppServerControlViewState,
  absoluteIndex: number,
): 'top' | 'center' | 'bottom' {
  const historyCount = Math.max(state.snapshot?.historyCount ?? 0, state.historyEntries.length);
  if (absoluteIndex <= 0) {
    return 'top';
  }

  if (historyCount > 0 && absoluteIndex >= historyCount - 1) {
    return 'bottom';
  }

  return 'center';
}

function isHistoryIndexInsideCurrentWindow(
  state: SessionAppServerControlViewState,
  absoluteIndex: number,
): boolean {
  const snapshot = state.snapshot;
  if (!snapshot) {
    return false;
  }

  return absoluteIndex >= snapshot.historyWindowStart && absoluteIndex < snapshot.historyWindowEnd;
}

function queueHistoryNavigatorRequest(
  state: SessionAppServerControlViewState,
  targetIndex: number,
  kind: 'preview' | 'hydrate',
): void {
  state.historyNavigatorQueuedTargetIndex = targetIndex;
  state.historyNavigatorQueuedRequestKind =
    kind === 'hydrate' || state.historyNavigatorQueuedRequestKind === 'hydrate'
      ? 'hydrate'
      : 'preview';
}

function resolveCenteredHistoryWindowStart(
  historyCount: number,
  targetIndex: number,
  count: number,
): number {
  if (historyCount <= 0 || count <= 0) {
    return 0;
  }

  const clampedCount = Math.max(1, Math.min(historyCount, count));
  return Math.max(
    0,
    Math.min(targetIndex - Math.floor(clampedCount / 2), historyCount - clampedCount),
  );
}

/* eslint-disable complexity -- jump preview/hydration intentionally shares one queued request path. */
async function requestHistoryNavigatorWindow(
  sessionId: string,
  state: SessionAppServerControlViewState,
  targetIndex: number,
  kind: 'preview' | 'hydrate',
): Promise<void> {
  const snapshot = state.snapshot;
  if (!snapshot) {
    return;
  }

  const historyCount = Math.max(snapshot.historyCount, state.historyEntries.length);
  if (historyCount <= 0) {
    return;
  }

  const clampedTargetIndex = Math.max(0, Math.min(historyCount - 1, targetIndex));
  const desiredCount =
    kind === 'preview'
      ? Math.min(historyCount, HISTORY_NAVIGATOR_PREVIEW_COUNT)
      : resolveAppServerControlHistoryWindowTargetCount(
          state.historyViewport,
          Math.max(APP_SERVER_CONTROL_HISTORY_WINDOW_SIZE, state.historyWindowCount),
          state.historyObservedHeights.values(),
        );
  const requestCount = Math.max(1, Math.min(historyCount, desiredCount));
  const requestStart = resolveCenteredHistoryWindowStart(
    historyCount,
    clampedTargetIndex,
    requestCount,
  );
  const alreadyMaterialized =
    isHistoryIndexInsideCurrentWindow(state, clampedTargetIndex) &&
    state.historyWindowCount >= requestCount;

  state.historyPendingJumpTargetIndex = clampedTargetIndex;
  state.historyPendingJumpAlign = resolveHistoryJumpAlign(state, clampedTargetIndex);
  if (kind === 'hydrate') {
    state.historyWindowTargetCount = requestCount;
  }

  if (alreadyMaterialized) {
    if (kind === 'hydrate') {
      state.historyNavigatorMode = 'browse';
      state.historyNavigatorDragTargetIndex = null;
    }
    renderCurrentAgentView(sessionId, { immediate: true });
    return;
  }

  if (state.refreshInFlight) {
    queueHistoryNavigatorRequest(state, clampedTargetIndex, kind);
    return;
  }

  if (kind === 'preview') {
    state.historyNavigatorLastPreviewRequestAt = Date.now();
  }
  state.refreshInFlight = true;
  try {
    const windowRevision = issueHistoryWindowRevision(sessionId, state);
    const nextSnapshot = await requestAppServerControlHistoryWindow(
      sessionId,
      state,
      requestStart,
      requestCount,
      windowRevision,
    );
    traceAppServerControlHistoryFetch(
      sessionId,
      nextSnapshot,
      kind === 'preview' ? 'drag-preview' : 'jump',
    );
    if (applyFetchedAppServerControlHistoryWindow(sessionId, state, nextSnapshot)) {
      if (kind === 'hydrate') {
        state.historyNavigatorMode = 'browse';
        state.historyNavigatorDragTargetIndex = null;
      }
      renderCurrentAgentView(sessionId, { immediate: kind === 'preview' });
    }
  } catch (error) {
    log.warn(
      () =>
        `Failed to ${kind === 'preview' ? 'preview' : 'jump'} AppServerControl history for ${sessionId}: ${String(error)}`,
    );
    if (kind === 'hydrate' && !state.historyAutoScrollPinned) {
      state.historyNavigatorMode = 'browse';
      state.historyNavigatorDragTargetIndex = null;
    }
  } finally {
    state.refreshInFlight = false;
    if (!flushQueuedHistoryNavigatorRequest(sessionId, state)) {
      if (!flushQueuedRefreshViewportSync(sessionId, state)) {
        flushPendingHistoryWindowViewportSync(sessionId, state);
      }
    }
  }
}
/* eslint-enable complexity */

function flushQueuedHistoryNavigatorRequest(
  sessionId: string,
  state: SessionAppServerControlViewState,
): boolean {
  const targetIndex = state.historyNavigatorQueuedTargetIndex;
  const requestKind = state.historyNavigatorQueuedRequestKind;
  if (targetIndex === null || requestKind === null) {
    return false;
  }

  clearQueuedHistoryNavigatorRequest(state);
  void requestHistoryNavigatorWindow(sessionId, state, targetIndex, requestKind);
  return true;
}

function primeHistoryNavigatorPreview(
  sessionId: string,
  state: SessionAppServerControlViewState,
  targetIndex: number,
  flushNow = false,
): void {
  clearHistoryNavigatorHydrateTimer(state);
  state.historyPendingJumpTargetIndex = targetIndex;
  state.historyPendingJumpAlign = resolveHistoryJumpAlign(state, targetIndex);

  if (flushNow || isHistoryIndexInsideCurrentWindow(state, targetIndex)) {
    clearHistoryNavigatorPreviewTimer(state);
    void requestHistoryNavigatorWindow(sessionId, state, targetIndex, 'preview');
    return;
  }

  const now = Date.now();
  const remainingMs = Math.max(
    0,
    HISTORY_NAVIGATOR_PREVIEW_THROTTLE_MS - (now - state.historyNavigatorLastPreviewRequestAt),
  );
  if (remainingMs === 0 && state.historyNavigatorPreviewHandle === null) {
    state.historyNavigatorLastPreviewRequestAt = now;
    void requestHistoryNavigatorWindow(sessionId, state, targetIndex, 'preview');
    return;
  }

  if (state.historyNavigatorPreviewHandle !== null) {
    return;
  }

  state.historyNavigatorPreviewHandle = window.setTimeout(() => {
    const current = viewStates.get(sessionId);
    if (!current) {
      return;
    }

    current.historyNavigatorPreviewHandle = null;
    current.historyNavigatorLastPreviewRequestAt = Date.now();
    const latestTargetIndex =
      current.historyNavigatorDragTargetIndex ?? current.historyNavigatorAnchorIndex ?? targetIndex;
    void requestHistoryNavigatorWindow(sessionId, current, latestTargetIndex, 'preview');
  }, remainingMs);
}

function scheduleHistoryNavigatorHydration(
  sessionId: string,
  state: SessionAppServerControlViewState,
  targetIndex: number,
): void {
  clearHistoryNavigatorHydrateTimer(state);
  state.historyNavigatorHydrateHandle = window.setTimeout(() => {
    const current = viewStates.get(sessionId);
    if (!current || current.historyAutoScrollPinned) {
      return;
    }

    current.historyNavigatorHydrateHandle = null;
    void requestHistoryNavigatorWindow(sessionId, current, targetIndex, 'hydrate');
  }, HISTORY_NAVIGATOR_HYDRATE_IDLE_MS);
}

function enterHistoryFollowLive(sessionId: string, state: SessionAppServerControlViewState): void {
  clearHistoryNavigatorPreviewTimer(state);
  clearHistoryNavigatorHydrateTimer(state);
  clearQueuedHistoryNavigatorRequest(state);
  state.historyPendingJumpTargetIndex = null;
  state.historyPendingJumpAlign = null;
  state.historyViewportSyncPending = false;
  state.historyViewportSyncForcePending = false;
  state.historyViewportSyncQueuedDuringRefresh = false;
  state.historyViewportSyncSuppressUntil = 0;
  state.historyNavigatorMode = 'follow-live';
  state.historyNavigatorDragTargetIndex = null;
  setHistoryScrollMode(state, 'follow');
  syncHistoryProgressNavigator(sessionId);
  if (state.snapshot?.hasNewerHistory) {
    void loadLatestAppServerControlHistoryWindow(sessionId, state);
    return;
  }

  historyRender.scrollHistoryToBottom(sessionId, 'smooth');
}

function bindHistoryViewport(sessionId: string, state: SessionAppServerControlViewState): void {
  const viewport = state.panel.querySelector<HTMLDivElement>('[data-agent-field="history"]');
  state.historyViewport = viewport;
  state.historyProgressNav = state.panel.querySelector<HTMLDivElement>(
    '[data-agent-field="history-progress-nav"]',
  );
  state.historyProgressThumb = state.panel.querySelector<HTMLDivElement>(
    '[data-agent-field="history-progress-thumb"]',
  );
  if (state.historyProgressNav) {
    if (typeof state.historyProgressNav.removeAttribute === 'function') {
      state.historyProgressNav.removeAttribute('hidden');
    }
    state.historyProgressNav.hidden = false;
  }
  if (!viewport) {
    return;
  }

  viewport.style.overflow = 'hidden auto';
  syncHistoryProgressNavigator(sessionId);

  state.historyViewportSize = {
    width: viewport.clientWidth,
    height: viewport.clientHeight,
  };
  if (typeof ResizeObserver === 'function') {
    state.historyViewportResizeObserver ??= new ResizeObserver(() => {
      const current = viewStates.get(sessionId);
      const currentViewport = current?.historyViewport;
      if (!current || !currentViewport) {
        return;
      }

      const nextSize = {
        width: currentViewport.clientWidth,
        height: currentViewport.clientHeight,
      };
      const previousSize = current.historyViewportSize;
      current.historyViewportSize = nextSize;
      if (
        previousSize &&
        previousSize.width === nextSize.width &&
        previousSize.height === nextSize.height
      ) {
        return;
      }

      if (
        !current.historyAutoScrollPinned &&
        current.pendingHistoryPrependAnchor === null &&
        current.pendingHistoryLayoutAnchor === null
      ) {
        historyRender.captureHistoryViewportAnchor(current, 'pendingHistoryLayoutAnchor');
      }

      current.historyWindowTargetCount = resolveAppServerControlHistoryWindowTargetCount(
        currentViewport,
        Math.max(APP_SERVER_CONTROL_HISTORY_WINDOW_SIZE, current.historyWindowCount),
        current.historyObservedHeights.values(),
      );
      syncHistoryProgressNavigator(sessionId);
      syncLiveHistoryWindowViewport(sessionId, current);
      scheduleHistoryRender(sessionId);
    });
    state.historyViewportResizeObserver.observe(viewport);
  }

  if (viewport.dataset.appServerControlScrollBound === 'true') {
    return;
  }

  viewport.dataset.appServerControlScrollBound = 'true';
  let lastTouchClientY: number | null = null;
  const markUserScrollIntent = () => {
    const current = viewStates.get(sessionId);
    if (!current) {
      return;
    }

    current.historyLastUserScrollIntentAt = Date.now();
    current.historyViewportSyncSuppressUntil = 0;
  };
  const detachFollowForExplicitBrowseIntent = () => {
    const current = viewStates.get(sessionId);
    if (
      !current ||
      !current.historyAutoScrollPinned ||
      current.pendingHistoryPrependAnchor !== null ||
      current.pendingHistoryLayoutAnchor !== null
    ) {
      return;
    }

    setHistoryScrollMode(current, 'browse');
    current.historyNavigatorMode = 'browse';
    current.historyNavigatorDragTargetIndex = null;
    historyRender.renderScrollToBottomControl(current.panel, current);
  };
  const stepViewportScroll = (deltaPx: number) => {
    const current = viewStates.get(sessionId);
    const currentViewport = current?.historyViewport;
    if (!current || !currentViewport) {
      return;
    }

    currentViewport.scrollTop = Math.max(
      0,
      Math.min(
        currentViewport.scrollHeight - currentViewport.clientHeight,
        currentViewport.scrollTop + deltaPx,
      ),
    );
  };
  viewport.addEventListener(
    'wheel',
    (event) => {
      markUserScrollIntent();
      const deltaYPx = resolveHistoryWheelDeltaYPx(event, viewport);
      const fastWheelThresholdPx = Math.max(
        HISTORY_FAST_WHEEL_DELTA_MIN_PX,
        Math.round(Math.max(1, viewport.clientHeight) * 0.75),
      );
      if (Math.abs(deltaYPx) >= fastWheelThresholdPx) {
        const snapshot = viewStates.get(sessionId)?.snapshot;
        traceAppServerControlHistoryScroll({
          sessionId,
          reason: 'fast-wheel',
          scrollTop: viewport.scrollTop,
          clientHeight: viewport.clientHeight,
          scrollHeight: viewport.scrollHeight,
          deltaYPx,
          historyWindowStart: snapshot?.historyWindowStart,
          historyWindowEnd: snapshot?.historyWindowEnd,
          historyCount: snapshot?.historyCount,
        });
      }
      if (deltaYPx < 0) {
        detachFollowForExplicitBrowseIntent();
      }
    },
    { passive: true },
  );
  viewport.addEventListener(
    'touchstart',
    (event) => {
      markUserScrollIntent();
      lastTouchClientY = event.touches[0]?.clientY ?? null;
    },
    { passive: true },
  );
  viewport.addEventListener(
    'touchmove',
    (event) => {
      markUserScrollIntent();
      const nextTouchClientY = event.touches[0]?.clientY ?? null;
      if (
        typeof nextTouchClientY === 'number' &&
        typeof lastTouchClientY === 'number' &&
        nextTouchClientY > lastTouchClientY + 1
      ) {
        detachFollowForExplicitBrowseIntent();
      }
      lastTouchClientY = nextTouchClientY;
    },
    { passive: true },
  );
  viewport.addEventListener(
    'touchend',
    () => {
      lastTouchClientY = null;
    },
    { passive: true },
  );
  viewport.addEventListener(
    'touchcancel',
    () => {
      lastTouchClientY = null;
    },
    { passive: true },
  );
  viewport.addEventListener('pointerdown', markUserScrollIntent, { passive: true });
  /* eslint-disable complexity -- key-driven index scrolling intentionally handles the compact browse command set in one place. */
  viewport.addEventListener('keydown', (event) => {
    markUserScrollIntent();
    const current = viewStates.get(sessionId);
    const keyboardStepPx = current ? resolveHistoryKeyboardStepPx(current) : 40;
    const pageStepPx = Math.max(1, current?.historyViewport?.clientHeight ?? 0);
    if (
      event.key === 'ArrowUp' ||
      event.key === 'PageUp' ||
      event.key === 'Home' ||
      event.key === 'ArrowDown' ||
      event.key === 'PageDown' ||
      event.key === 'End'
    ) {
      event.preventDefault();
    }
    if (event.key === 'ArrowUp' || event.key === 'PageUp' || event.key === 'Home') {
      detachFollowForExplicitBrowseIntent();
    }
    if (event.key === 'ArrowUp') {
      stepViewportScroll(-keyboardStepPx);
    } else if (event.key === 'ArrowDown') {
      stepViewportScroll(keyboardStepPx);
    } else if (event.key === 'PageUp') {
      stepViewportScroll(-pageStepPx);
    } else if (event.key === 'PageDown') {
      stepViewportScroll(pageStepPx);
    } else if (event.key === 'Home') {
      stepViewportScroll(-viewport.scrollHeight);
    } else if (event.key === 'End') {
      stepViewportScroll(viewport.scrollHeight);
    }
  });
  /* eslint-enable complexity */
  /* eslint-disable complexity -- scroll/fetch coordination stays consolidated here while the progress navigator replaces the older host-scroller path. */
  let activeHistoryNavigatorPointerId: number | null = null;
  let activeHistoryNavigatorThumbOffsetPx: number | null = null;
  const handleViewportScroll = () => {
    const current = viewStates.get(sessionId);
    const currentViewport = current?.historyViewport;
    if (!current || !currentViewport) {
      return;
    }

    const viewportSyncSuppressed = Date.now() < current.historyViewportSyncSuppressUntil;
    const scrollMetrics = historyRender.readHistoryScrollMetrics(currentViewport, current);
    setHistoryScrollMode(
      current,
      resolveHistoryScrollMode({
        previousMode: current.historyScrollMode,
        previous: current.historyLastScrollMetrics,
        current: scrollMetrics,
        userInitiated:
          Date.now() - current.historyLastUserScrollIntentAt <=
          USER_HISTORY_SCROLL_INTENT_WINDOW_MS,
        pendingAnchorRestore:
          current.pendingHistoryPrependAnchor !== null ||
          current.pendingHistoryLayoutAnchor !== null,
      }),
    );
    if (current.historyAutoScrollPinned) {
      current.historyNavigatorMode = 'follow-live';
      current.historyNavigatorDragTargetIndex = null;
    } else if (
      current.historyNavigatorMode !== 'drag-preview' ||
      activeHistoryNavigatorPointerId === null
    ) {
      current.historyNavigatorMode = 'browse';
      current.historyNavigatorDragTargetIndex = null;
    }
    current.historyLastScrollMetrics = scrollMetrics;
    historyRender.syncViewportOffset(sessionId);
    historyRender.renderScrollToBottomControl(current.panel, current);
    if (current.historyNavigatorMode === 'drag-preview') {
      if (historyRender.shouldRenderForViewportScroll(current)) {
        scheduleHistoryRender(sessionId);
      }
      return;
    }
    if (current.refreshInFlight && !current.historyAutoScrollPinned && !viewportSyncSuppressed) {
      current.historyViewportSyncPending = true;
      current.historyViewportSyncQueuedDuringRefresh = true;
    }
    const fetchThresholdPx = Math.max(
      resolveAppServerControlHistoryFetchThresholdPx(current),
      Math.round(Math.max(1, currentViewport.clientHeight)),
    );
    const distanceFromBottom =
      currentViewport.scrollHeight - currentViewport.clientHeight - currentViewport.scrollTop;

    if (current.snapshot?.hasNewerHistory && distanceFromBottom <= fetchThresholdPx) {
      if (current.historyAutoScrollPinned) {
        void loadLatestAppServerControlHistoryWindow(sessionId, current);
      } else if (!viewportSyncSuppressed) {
        queueHistoryWindowViewportSync(sessionId, current);
      }
    } else if (!current.historyAutoScrollPinned && !viewportSyncSuppressed) {
      queueHistoryWindowViewportSync(sessionId, current);
    }

    if (
      current.historyLeadingPlaceholders.length > 0 ||
      current.historyTrailingPlaceholders.length > 0 ||
      historyRender.shouldRenderForViewportScroll(current)
    ) {
      scheduleHistoryRender(sessionId);
    }
  };
  /* eslint-enable complexity */
  viewport.addEventListener('scroll', handleViewportScroll);

  const progressNav = state.historyProgressNav;
  if (progressNav && progressNav.dataset.appServerControlProgressBound !== 'true') {
    progressNav.dataset.appServerControlProgressBound = 'true';
    const updateNavigatorTarget = (clientY: number, finalize = false) => {
      const current = viewStates.get(sessionId);
      if (!current) {
        return;
      }

      markUserScrollIntent();
      const target = resolveHistoryNavigatorTarget({
        state: current,
        clientY,
        thumbDragOffsetPx: activeHistoryNavigatorThumbOffsetPx,
      });
      if (!target) {
        return;
      }

      if (finalize && target.atLiveEdge) {
        enterHistoryFollowLive(sessionId, current);
        return;
      }

      setHistoryScrollMode(current, 'browse');
      current.historyNavigatorMode = 'drag-preview';
      current.historyNavigatorDragTargetIndex = target.targetIndex;
      syncHistoryProgressNavigator(sessionId);
      primeHistoryNavigatorPreview(sessionId, current, target.targetIndex, finalize);
      if (finalize) {
        scheduleHistoryNavigatorHydration(sessionId, current, target.targetIndex);
      }
    };

    progressNav.addEventListener('pointerdown', (event) => {
      activeHistoryNavigatorPointerId = event.pointerId;
      const current = viewStates.get(sessionId);
      const thumbRect = current?.historyProgressThumb?.getBoundingClientRect();
      activeHistoryNavigatorThumbOffsetPx =
        thumbRect && event.clientY >= thumbRect.top && event.clientY <= thumbRect.bottom
          ? event.clientY - thumbRect.top
          : null;
      progressNav.dataset.dragging = 'true';
      progressNav.setPointerCapture(event.pointerId);
      event.preventDefault();
      updateNavigatorTarget(event.clientY);
    });

    progressNav.addEventListener('pointermove', (event) => {
      if (activeHistoryNavigatorPointerId !== event.pointerId) {
        return;
      }

      event.preventDefault();
      updateNavigatorTarget(event.clientY);
    });

    const finishNavigatorDrag = (event: PointerEvent) => {
      if (activeHistoryNavigatorPointerId !== event.pointerId) {
        return;
      }

      Reflect.deleteProperty(progressNav.dataset, 'dragging');
      progressNav.releasePointerCapture(event.pointerId);
      updateNavigatorTarget(event.clientY, true);
      activeHistoryNavigatorPointerId = null;
      activeHistoryNavigatorThumbOffsetPx = null;
    };

    progressNav.addEventListener('pointerup', finishNavigatorDrag);
    progressNav.addEventListener('pointercancel', finishNavigatorDrag);
  }

  const scrollButton = state.panel.querySelector<HTMLButtonElement>(
    '[data-agent-field="scroll-to-bottom"]',
  );
  if (scrollButton && scrollButton.dataset.appServerControlScrollBound !== 'true') {
    scrollButton.dataset.appServerControlScrollBound = 'true';
    scrollButton.addEventListener('click', () => {
      historyRender.scrollHistoryToBottom(sessionId, 'smooth');
    });
  }
}

function bindAppServerControlForegroundRecovery(): void {
  if (
    appServerControlForegroundRecoveryBound ||
    typeof document === 'undefined' ||
    typeof document.addEventListener !== 'function' ||
    typeof window === 'undefined' ||
    typeof window.addEventListener !== 'function'
  ) {
    return;
  }

  const recoverForegroundAppServerControlState = () => {
    if (typeof document !== 'undefined' && document.visibilityState === 'hidden') {
      return;
    }

    if (!appServerControlForegroundRecoveryPending) {
      return;
    }

    appServerControlForegroundRecoveryPending = false;
    const sessionId = $activeSessionId.get();
    if (!sessionId || getActiveTab(sessionId) !== 'agent') {
      return;
    }

    const state = viewStates.get(sessionId);
    if (!state) {
      return;
    }

    prepareAppServerControlForForeground(state);
    renderCurrentAgentView(sessionId, { immediate: true });
    if (state.snapshot) {
      void refreshAppServerControlSnapshot(sessionId, {
        latestWindow: state.historyAutoScrollPinned,
      });
    }
  };

  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'hidden') {
      appServerControlForegroundRecoveryPending = true;
      return;
    }

    recoverForegroundAppServerControlState();
  });
  window.addEventListener('blur', () => {
    appServerControlForegroundRecoveryPending = true;
  });
  window.addEventListener('focus', recoverForegroundAppServerControlState);
  window.addEventListener('pageshow', recoverForegroundAppServerControlState);
  appServerControlForegroundRecoveryBound = true;
}

function bindAppServerControlVisualViewportRecovery(): void {
  if (
    appServerControlVisualViewportRecoveryBound ||
    typeof window === 'undefined' ||
    typeof window.addEventListener !== 'function'
  ) {
    return;
  }

  const recoverActiveAppServerControlViewport = () => {
    const sessionId = $activeSessionId.get();
    if (!sessionId || getActiveTab(sessionId) !== 'agent') {
      return;
    }

    const state = viewStates.get(sessionId);
    const viewport = state?.historyViewport;
    if (!state || !viewport) {
      return;
    }

    state.historyViewportSyncSuppressUntil = 0;
    state.historyLastVoidSyncScrollTop = null;
    state.historyWindowTargetCount = resolveAppServerControlHistoryWindowTargetCount(
      viewport,
      Math.max(APP_SERVER_CONTROL_HISTORY_WINDOW_SIZE, state.historyWindowCount),
      state.historyObservedHeights.values(),
    );
    syncHistoryProgressNavigator(sessionId);
    queueUrgentHistoryWindowViewportSync(sessionId, state);
    renderCurrentAgentView(sessionId, { immediate: true });
  };

  window.addEventListener('midterm:visual-viewport-changed', recoverActiveAppServerControlViewport);
  window.addEventListener('resize', recoverActiveAppServerControlViewport);
  appServerControlVisualViewportRecoveryBound = true;
}

function scheduleHistoryRender(sessionId: string): void {
  const state = viewStates.get(sessionId);
  renderCurrentAgentView(sessionId, { immediate: state?.debugScenarioActive === true });
}

function clearPendingHistoryRenderBatch(state: SessionAppServerControlViewState | undefined): void {
  if (!state || state.historyRenderBatchHandle === null) {
    return;
  }

  window.clearTimeout(state.historyRenderBatchHandle);
  state.historyRenderBatchHandle = null;
}

function shouldBatchLiveHistoryRender(delta: AppServerControlHistoryDelta): boolean {
  if (delta.requestUpserts.length > 0 || delta.requestRemovals.length > 0) {
    return false;
  }

  const currentTurnState = (delta.currentTurn.state || '').toLowerCase();
  if (currentTurnState !== 'running' && currentTurnState !== 'in_progress') {
    return false;
  }

  return (
    delta.historyUpserts.length > 0 ||
    delta.historyRemovals.length > 0 ||
    delta.itemUpserts.length > 0 ||
    delta.itemRemovals.length > 0 ||
    delta.noticeUpserts.length > 0
  );
}

function scheduleLiveHistoryRender(sessionId: string): void {
  const state = viewStates.get(sessionId);
  if (!state) {
    return;
  }

  state.renderDirty = true;
  if (!isAppServerControlViewVisible(sessionId, state) || state.historyRenderBatchHandle !== null) {
    return;
  }

  state.historyRenderBatchHandle = window.setTimeout(() => {
    const current = viewStates.get(sessionId);
    if (!current) {
      return;
    }

    current.historyRenderBatchHandle = null;
    renderCurrentAgentView(sessionId);
  }, LIVE_HISTORY_RENDER_BATCH_MS);
}

function openLiveAppServerControlStream(sessionId: string, afterSequence: number): void {
  const state = viewStates.get(sessionId);
  if (!state) {
    return;
  }

  closeAppServerControlStream(sessionId);
  state.disconnectStream = connectAppServerControlHistoryStream(sessionId, state, afterSequence, {
    onOpen: () => {
      const current = viewStates.get(sessionId);
      if (!current) {
        return;
      }

      current.streamConnected = true;
      current.activationIssue = null;
      current.activationError = null;
      setActivationState(
        current,
        'ready',
        appServerControlText(
          'appServerControl.activation.ready.detail',
          'AppServerControl live stream connected.',
        ),
        appServerControlText(
          'appServerControl.activation.ready.summary',
          'Live AppServerControl stream connected.',
        ),
        appServerControlText(
          'appServerControl.activation.ready.body',
          'Realtime canonical AppServerControl history patches are now flowing into the timeline.',
        ),
        'positive',
      );
      renderCurrentAgentView(sessionId);
    },
    onHistoryWindow: (snapshot) => {
      const current = viewStates.get(sessionId);
      if (!current) {
        return;
      }

      traceAppServerControlHistoryFetch(sessionId, snapshot, 'stream-window');
      if (applyFetchedAppServerControlHistoryWindow(sessionId, current, snapshot)) {
        scheduleHistoryRender(sessionId);
      }
    },
    onPatch: (delta) => {
      const current = viewStates.get(sessionId);
      if (!current || !current.snapshot) {
        return;
      }

      traceAppServerControlHistoryPush(sessionId, delta, current.snapshot);
      const requiresWindowRefresh = applyCanonicalAppServerControlDelta(current, delta);
      if (requiresWindowRefresh) {
        if (!current.historyAutoScrollPinned && current.historyNavigatorMode !== 'drag-preview') {
          queueUrgentHistoryWindowViewportSync(sessionId, current);
        } else {
          void refreshAppServerControlSnapshot(sessionId);
        }
      }
      if (shouldBatchLiveHistoryRender(delta)) {
        scheduleLiveHistoryRender(sessionId);
      } else {
        renderCurrentAgentView(sessionId);
      }
    },
    onError: () => {
      const current = viewStates.get(sessionId);
      if (!current) {
        return;
      }

      current.streamConnected = false;
      renderCurrentAgentView(sessionId);
    },
  });
}

function closeAppServerControlStream(sessionId: string): void {
  const state = viewStates.get(sessionId);
  if (!state) {
    return;
  }

  state.disconnectStream?.();
  state.disconnectStream = null;
  state.streamConnected = false;
}

function releaseHiddenAppServerControlRenderState(state: SessionAppServerControlViewState): void {
  clearPendingHistoryRenderBatch(state);
  clearHistoryNavigatorPreviewTimer(state);
  clearHistoryNavigatorHydrateTimer(state);
  clearQueuedHistoryNavigatorRequest(state);
  state.historyEntries = [];
  state.historyMeasurementObserver?.disconnect();
  state.historyRenderedNodes.clear();
  state.assistantMarkdownCache.clear();
  state.historyObservedHeights.clear();
  state.historyLeadingPlaceholders = [];
  state.historyTrailingPlaceholders = [];
  state.historyEmptyState = null;
  state.historyLastVoidSyncScrollTop = null;
  state.pendingHistoryPrependAnchor = null;
  state.pendingHistoryLayoutAnchor = null;
  state.historyPendingJumpTargetIndex = null;
  state.historyPendingJumpAlign = null;
  state.historyNavigatorDragTargetIndex = null;
  state.historyNavigatorMode = state.historyAutoScrollPinned ? 'follow-live' : 'browse';
  state.historyLastVirtualWindowKey = null;
  state.historyViewportSyncPending = false;
  state.historyViewportSyncForcePending = false;
  state.historyViewportSyncSuppressUntil = 0;
  state.renderDirty = true;

  const historyHost = state.panel.querySelector<HTMLElement>('[data-agent-field="history"]');
  historyHost?.replaceChildren();
}

async function compactHiddenAppServerControlSessionHistory(
  sessionId: string,
  state: SessionAppServerControlViewState,
): Promise<void> {
  if (state.debugScenarioActive || state.refreshInFlight) {
    return;
  }

  const snapshot = state.snapshot;
  if (!snapshot) {
    return;
  }

  if (!state.historyAutoScrollPinned) {
    return;
  }

  const shouldRefreshLatestWindow =
    snapshot.hasNewerHistory ||
    snapshot.historyWindowStart > 0 ||
    state.historyWindowCount > APP_SERVER_CONTROL_HISTORY_WINDOW_SIZE ||
    snapshot.history.length > APP_SERVER_CONTROL_HISTORY_WINDOW_SIZE;

  if (shouldRefreshLatestWindow) {
    try {
      const latestSnapshot = await getAppServerControlHistoryWindow(
        sessionId,
        undefined,
        resolveAppServerControlHistoryWindowTargetCount(
          state.historyViewport,
          APP_SERVER_CONTROL_HISTORY_WINDOW_SIZE,
          state.historyObservedHeights.values(),
        ),
        issueHistoryWindowRevision(sessionId, state),
      );
      traceAppServerControlHistoryFetch(sessionId, latestSnapshot, 'latest');
      const current = viewStates.get(sessionId);
      if (!current || current !== state) {
        return;
      }

      if (applyFetchedAppServerControlHistoryWindow(sessionId, current, latestSnapshot)) {
        if (isAppServerControlViewVisible(sessionId, current)) {
          renderCurrentAgentView(sessionId, { immediate: true });
        }
      }
      return;
    } catch (error) {
      log.warn(
        () =>
          `Failed to compact hidden AppServerControl history for ${sessionId}: ${String(error)}`,
      );
    }
  }

  const previousStart = snapshot.historyWindowStart;
  const previousEnd = snapshot.historyWindowEnd;
  const historyCount = snapshot.historyCount;
  collapseSnapshotToLatestWindow(state, APP_SERVER_CONTROL_HISTORY_WINDOW_SIZE);
  traceAppServerControlHistoryCompact(
    sessionId,
    previousStart,
    previousEnd,
    state.historyWindowStart,
    state.historyWindowStart + state.historyWindowCount,
    historyCount,
  );
}

async function refreshAppServerControlSnapshot(
  sessionId: string,
  options: { latestWindow?: boolean } = {},
): Promise<void> {
  const state = viewStates.get(sessionId);
  if (!state || state.refreshInFlight) {
    return;
  }

  state.refreshInFlight = true;
  try {
    const desiredLatestWindowCount = resolveAppServerControlHistoryWindowTargetCount(
      state.historyViewport,
      Math.max(APP_SERVER_CONTROL_HISTORY_WINDOW_SIZE, state.historyWindowCount),
      state.historyObservedHeights.values(),
    );
    if (options.latestWindow) {
      state.historyWindowTargetCount = desiredLatestWindowCount;
    }
    const windowRevision = issueHistoryWindowRevision(sessionId, state);
    const nextSnapshot = options.latestWindow
      ? await requestAppServerControlHistoryWindow(
          sessionId,
          state,
          undefined,
          desiredLatestWindowCount,
          windowRevision,
        )
      : await requestAppServerControlHistoryWindow(
          sessionId,
          state,
          state.historyWindowStart,
          state.historyWindowCount,
          windowRevision,
        );
    traceAppServerControlHistoryFetch(
      sessionId,
      nextSnapshot,
      options.latestWindow ? 'latest' : 'refresh',
    );
    if (applyFetchedAppServerControlHistoryWindow(sessionId, state, nextSnapshot)) {
      if (state.activationState !== 'ready') {
        setActivationState(
          state,
          'ready',
          'AppServerControl history window refreshed.',
          'AppServerControl history window refreshed.',
          'The AppServerControl read model is available and the history is rendering live data.',
          'positive',
        );
      }
      renderCurrentAgentView(sessionId);
    }
  } catch (error) {
    log.warn(
      () => `Failed to refresh AppServerControl history window for ${sessionId}: ${String(error)}`,
    );
    state.activationError = describeError(error);
    setActivationState(
      state,
      'failed',
      'AppServerControl history window refresh failed.',
      'AppServerControl refresh failed.',
      state.activationError,
      'attention',
    );
    showDevErrorDialog({
      title: appServerControlText(
        'appServerControl.error.refreshTitle',
        'AppServerControl refresh failed',
      ),
      context: `AppServerControl history window refresh failed for session ${sessionId}`,
      error,
    });
    renderCurrentAgentView(sessionId);
  } finally {
    state.refreshInFlight = false;
    if (!flushQueuedHistoryNavigatorRequest(sessionId, state)) {
      if (!flushQueuedRefreshViewportSync(sessionId, state)) {
        flushPendingHistoryWindowViewportSync(sessionId, state);
      }
    }
  }
}

async function loadLatestAppServerControlHistoryWindow(
  sessionId: string,
  state: SessionAppServerControlViewState,
): Promise<void> {
  if (state.refreshInFlight || !state.snapshot?.hasNewerHistory) {
    return;
  }

  state.refreshInFlight = true;
  try {
    state.historyWindowTargetCount = resolveAppServerControlHistoryWindowTargetCount(
      state.historyViewport,
      Math.max(APP_SERVER_CONTROL_HISTORY_WINDOW_SIZE, state.historyWindowCount),
      state.historyObservedHeights.values(),
    );
    const windowRevision = issueHistoryWindowRevision(sessionId, state);
    const nextSnapshot = await requestAppServerControlHistoryWindow(
      sessionId,
      state,
      undefined,
      state.historyWindowTargetCount,
      windowRevision,
    );
    traceAppServerControlHistoryFetch(sessionId, nextSnapshot, 'latest');
    if (applyFetchedAppServerControlHistoryWindow(sessionId, state, nextSnapshot)) {
      renderCurrentAgentView(sessionId);
    }
  } catch (error) {
    log.warn(
      () => `Failed to load latest AppServerControl history for ${sessionId}: ${String(error)}`,
    );
  } finally {
    state.refreshInFlight = false;
    if (!flushQueuedHistoryNavigatorRequest(sessionId, state)) {
      if (!flushQueuedRefreshViewportSync(sessionId, state)) {
        flushPendingHistoryWindowViewportSync(sessionId, state);
      }
    }
  }
}

function queueHistoryWindowViewportSync(
  sessionId: string,
  state: SessionAppServerControlViewState,
): void {
  queueHistoryWindowViewportSyncInternal(sessionId, state, false);
}

function queueUrgentHistoryWindowViewportSync(
  sessionId: string,
  state: SessionAppServerControlViewState,
): void {
  queueHistoryWindowViewportSyncInternal(sessionId, state, true);
}

function queueHistoryWindowViewportSyncInternal(
  sessionId: string,
  state: SessionAppServerControlViewState,
  forceRequest: boolean,
): void {
  if (state.historyNavigatorMode === 'drag-preview') {
    return;
  }

  state.historyViewportSyncForcePending ||= forceRequest;

  if (state.historyViewportSyncPending) {
    return;
  }

  state.historyViewportSyncPending = true;
  if (state.refreshInFlight) {
    return;
  }

  flushPendingHistoryWindowViewportSync(sessionId, state);
}

function flushQueuedRefreshViewportSync(
  sessionId: string,
  state: SessionAppServerControlViewState,
): boolean {
  if (
    state.historyViewportSyncQueuedDuringRefresh &&
    !state.historyAutoScrollPinned &&
    state.historyNavigatorMode !== 'drag-preview'
  ) {
    state.historyViewportSyncQueuedDuringRefresh = false;
    const forceRequest = state.historyViewportSyncForcePending;
    state.historyViewportSyncForcePending = false;
    void syncHistoryWindowToViewport(sessionId, state, forceRequest);
    return true;
  }

  return false;
}

function flushPendingHistoryWindowViewportSync(
  sessionId: string,
  state: SessionAppServerControlViewState,
): void {
  if (
    !state.historyViewportSyncPending ||
    state.refreshInFlight ||
    state.historyNavigatorMode === 'drag-preview'
  ) {
    return;
  }

  state.historyViewportSyncPending = false;
  const forceRequest = state.historyViewportSyncForcePending;
  state.historyViewportSyncForcePending = false;
  if (!state.historyAutoScrollPinned) {
    void syncHistoryWindowToViewport(sessionId, state, forceRequest);
  }
}

/* eslint-disable complexity -- viewport/window synchronization keeps both forced and anchored browse paths in one place while the index-scroll model settles. */
async function syncHistoryWindowToViewport(
  sessionId: string,
  state: SessionAppServerControlViewState,
  forceRequest = false,
): Promise<void> {
  if (state.refreshInFlight || !state.snapshot) {
    state.historyViewportSyncPending = true;
    state.historyViewportSyncForcePending ||= forceRequest;
    return;
  }

  const hasAnchor = historyRender.captureHistoryViewportAnchor(state);
  const anchorAbsoluteIndex = hasAnchor
    ? (state.pendingHistoryPrependAnchor?.absoluteIndex ?? null)
    : null;
  const requestedWindow = historyRender.getViewportCenteredHistoryWindowRequest(state, {
    fetchAheadItems: resolveAppServerControlHistoryFetchAheadItems(
      DEFAULT_APP_SERVER_CONTROL_HISTORY_VIRTUALIZER_CONFIG,
    ),
    anchorAbsoluteIndex,
  });
  if (!requestedWindow) {
    if (forceRequest) {
      state.refreshInFlight = true;
      try {
        const windowRevision = issueHistoryWindowRevision(sessionId, state);
        const nextSnapshot = await requestAppServerControlHistoryWindow(
          sessionId,
          state,
          state.historyWindowStart,
          state.historyWindowCount,
          windowRevision,
        );
        traceAppServerControlHistoryFetch(sessionId, nextSnapshot, 'scroll');
        if (applyFetchedAppServerControlHistoryWindow(sessionId, state, nextSnapshot)) {
          renderCurrentAgentView(sessionId);
        }
      } catch (error) {
        log.warn(
          () =>
            `Failed to force-refresh viewport-centered AppServerControl history for ${sessionId}: ${String(error)}`,
        );
      } finally {
        state.refreshInFlight = false;
        if (!flushQueuedHistoryNavigatorRequest(sessionId, state)) {
          if (!flushQueuedRefreshViewportSync(sessionId, state)) {
            flushPendingHistoryWindowViewportSync(sessionId, state);
          }
        }
      }
      return;
    }

    state.pendingHistoryPrependAnchor = null;
    return;
  }

  const isBackwardShift = requestedWindow.startIndex < state.historyWindowStart;
  if (isBackwardShift && hasAnchor && !state.historyAutoScrollPinned) {
    setHistoryScrollMode(state, 'restore-anchor');
    state.historyNavigatorMode = 'browse';
  }
  state.refreshInFlight = true;
  try {
    const windowRevision = issueHistoryWindowRevision(sessionId, state);
    const nextSnapshot = await requestAppServerControlHistoryWindow(
      sessionId,
      state,
      requestedWindow.startIndex,
      requestedWindow.count,
      windowRevision,
    );
    traceAppServerControlHistoryFetch(sessionId, nextSnapshot, 'scroll');
    if (applyFetchedAppServerControlHistoryWindow(sessionId, state, nextSnapshot)) {
      renderCurrentAgentView(sessionId);
    }
  } catch (error) {
    log.warn(
      () =>
        `Failed to sync viewport-centered AppServerControl history for ${sessionId} (${isBackwardShift ? 'backward' : 'forward'}): ${String(error)}`,
    );
  } finally {
    state.refreshInFlight = false;
    if (!flushQueuedHistoryNavigatorRequest(sessionId, state)) {
      if (!flushQueuedRefreshViewportSync(sessionId, state)) {
        flushPendingHistoryWindowViewportSync(sessionId, state);
      }
    }
  }
}
/* eslint-enable complexity */

function renderCurrentAgentView(
  sessionId: string,
  options: { immediate?: boolean; force?: boolean } = {},
): void {
  const state = viewStates.get(sessionId);
  if (!state) {
    return;
  }

  clearPendingHistoryRenderBatch(state);
  state.renderDirty = true;

  if (!options.force && !isAppServerControlViewVisible(sessionId, state)) {
    return;
  }

  if (!options.immediate) {
    if (state.historyRenderScheduled !== null) {
      return;
    }

    state.historyRenderScheduled = window.requestAnimationFrame(() => {
      const current = viewStates.get(sessionId);
      if (!current) {
        return;
      }

      current.historyRenderScheduled = null;
      commitAgentViewRender(sessionId, options.force === true);
    });
    return;
  }

  if (state.historyRenderScheduled !== null) {
    window.cancelAnimationFrame(state.historyRenderScheduled);
    state.historyRenderScheduled = null;
  }

  commitAgentViewRender(sessionId, options.force === true);
}

function commitAgentViewRender(sessionId: string, force = false): void {
  const state = viewStates.get(sessionId);
  if (!state) {
    return;
  }

  if (!force && !isAppServerControlViewVisible(sessionId, state)) {
    return;
  }

  if (!force && hasActiveAppServerControlSelectionInPanel(state.panel)) {
    state.renderDirty = true;
    return;
  }

  state.renderDirty = false;

  if (!state.snapshot) {
    historyRender.renderActivationView(
      sessionId,
      state.panel,
      state,
      withActivationIssueNotice(buildActivationHistoryEntries(state), state.activationIssue),
    );
    return;
  }

  renderAgentView(state.panel, state.snapshot, state.streamConnected, state);
}

function bindActiveAppServerControlSessionRendering(): void {
  if (appServerControlActiveSessionBound) {
    return;
  }

  $activeSessionId.subscribe((sessionId) => {
    if (!sessionId) {
      return;
    }

    const state = viewStates.get(sessionId);
    if (!state || !state.renderDirty) {
      return;
    }

    renderCurrentAgentView(sessionId, { immediate: true });
  });
  appServerControlActiveSessionBound = true;
}

function bindAppServerControlSelectionGuard(): void {
  if (
    appServerControlSelectionGuardBound ||
    typeof document === 'undefined' ||
    typeof document.addEventListener !== 'function'
  ) {
    return;
  }

  document.addEventListener('selectionchange', () => {
    for (const [sessionId, state] of viewStates) {
      if (!state.renderDirty || !isAppServerControlViewVisible(sessionId, state)) {
        continue;
      }

      if (hasActiveAppServerControlSelectionInPanel(state.panel)) {
        continue;
      }

      renderCurrentAgentView(sessionId, { immediate: true });
    }
  });
  appServerControlSelectionGuardBound = true;
}

function bindAppServerControlSettingsRendering(): void {
  if (
    appServerControlSettingsRenderBound ||
    typeof window === 'undefined' ||
    typeof window.addEventListener !== 'function'
  ) {
    return;
  }

  window.addEventListener('midterm:agent-view-settings-changed', () => {
    for (const [sessionId, state] of viewStates) {
      state.renderDirty = true;
      if (isAppServerControlViewVisible(sessionId, state)) {
        renderCurrentAgentView(sessionId, { immediate: true });
      }
    }
  });
  appServerControlSettingsRenderBound = true;
}

function isAppServerControlViewVisible(
  sessionId: string,
  state: SessionAppServerControlViewState,
): boolean {
  if (state.debugScenarioActive) {
    return true;
  }

  if (getActiveTab(sessionId) !== 'agent') {
    return false;
  }

  const activeSessionId = $activeSessionId.get();
  return !activeSessionId || activeSessionId === sessionId;
}

function renderAgentView(
  panel: HTMLDivElement,
  snapshot: AppServerControlHistorySnapshot,
  streamConnected: boolean,
  state: SessionAppServerControlViewState,
): void {
  syncAppServerControlQuickSettingsFromSnapshot(
    snapshot.sessionId,
    snapshot.provider,
    snapshot.quickSettings,
  );
  syncAgentViewPresentation(panel, snapshot.provider);
  panel.dataset.agentTurnId = snapshot.currentTurn.turnId || '';
  syncAppServerControlTurnExecutionState(snapshot.sessionId, snapshot.currentTurn);
  historyRender.syncRequestInteractionState(state, snapshot.requests);
  const historyEntries = preservePersistentCommandEntries(
    buildAppServerControlHistoryEntries(snapshot),
    state.historyEntries,
    snapshot,
  );
  const runtimeStats = buildAppServerControlRuntimeStats(snapshot);
  state.runtimeStats = runtimeStats;
  const visibleHistoryEntries = historyRender.suppressActiveComposerRequestEntries(
    historyEntries,
    snapshot.requests,
  );
  const optimistic = applyOptimisticAppServerControlTurns(
    snapshot,
    visibleHistoryEntries,
    state.optimisticTurns,
  );
  state.optimisticTurns = optimistic.optimisticTurns;
  const renderedEntries = stabilizeHistoryEntryOrder(
    withTurnDurationNotes(
      snapshot,
      withTrailingBusyIndicator(
        snapshot,
        withLiveAssistantState(
          snapshot,
          withActivationIssueNotice(
            withInlineAppServerControlStatus(snapshot, optimistic.entries, streamConnected),
            state.activationIssue,
          ),
        ),
        snapshot.requests,
      ),
      state,
    ),
  );
  syncBusyIndicatorTicker({
    snapshot,
    state,
    entries: renderedEntries,
    renderCurrentAgentView,
    updateBusyIndicatorElapsed: historyRender.updateBusyIndicatorElapsed,
  });
  historyDom.renderRuntimeStats(panel, runtimeStats);
  historyRender.renderHistory(panel, renderedEntries, snapshot.sessionId);
  historyRender.renderComposerInterruption(panel, snapshot.sessionId, snapshot.requests, state);
}

function bindAppServerControlTurnLifecycle(): void {
  if (appServerControlTurnLifecycleBound || typeof window === 'undefined') {
    return;
  }

  window.addEventListener(
    APP_SERVER_CONTROL_TURN_SUBMITTED_EVENT,
    handleAppServerControlTurnSubmitted as EventListener,
  );
  window.addEventListener(
    APP_SERVER_CONTROL_TURN_ACCEPTED_EVENT,
    handleAppServerControlTurnAccepted as EventListener,
  );
  window.addEventListener(
    APP_SERVER_CONTROL_TURN_FAILED_EVENT,
    handleAppServerControlTurnFailed as EventListener,
  );
  appServerControlTurnLifecycleBound = true;
}

function handleAppServerControlTurnSubmitted(event: Event): void {
  const detail = (event as CustomEvent<AppServerControlTurnSubmittedEventDetail>).detail;
  const state = viewStates.get(detail.sessionId);
  if (!state) {
    return;
  }

  state.optimisticTurns = [
    ...state.optimisticTurns.filter((turn) => turn.optimisticId !== detail.optimisticId),
    {
      optimisticId: detail.optimisticId,
      turnId: null,
      text: (detail.request.text ?? '').trim(),
      attachments: cloneHistoryAttachments(detail.request.attachments),
      submittedAt: new Date().toISOString(),
      status: 'submitted',
    },
  ];
  renderCurrentAgentView(detail.sessionId);
}

function handleAppServerControlTurnAccepted(event: Event): void {
  const detail = (event as CustomEvent<AppServerControlTurnAcceptedEventDetail>).detail;
  const state = viewStates.get(detail.sessionId);
  if (!state) {
    return;
  }

  state.optimisticTurns = state.optimisticTurns.map((turn) =>
    turn.optimisticId === detail.optimisticId
      ? {
          ...turn,
          turnId: detail.response.turnId || turn.turnId,
          status: 'accepted',
        }
      : turn,
  );
  state.activationIssue = null;
  state.activationError = null;

  if (!state.streamConnected) {
    openLiveAppServerControlStream(detail.sessionId, state.snapshot?.latestSequence ?? 0);
  }

  renderCurrentAgentView(detail.sessionId);
}

function handleAppServerControlTurnFailed(event: Event): void {
  const detail = (event as CustomEvent<AppServerControlTurnFailedEventDetail>).detail;
  const state = viewStates.get(detail.sessionId);
  if (!state) {
    return;
  }

  state.optimisticTurns = state.optimisticTurns.filter(
    (turn) => turn.optimisticId !== detail.optimisticId,
  );
  renderCurrentAgentView(detail.sessionId);
}

export {
  classifyAppServerControlActivationIssue,
  resolveHistoryBadgeLabel,
} from './activationHelpers';
export {
  buildRenderedDiffLines,
  estimateHistoryEntryHeight,
  resolveHistoryBodyPresentation,
  tokenizeCommandText,
} from './historyContent';
export {
  applyOptimisticAppServerControlTurns,
  buildActivationHistoryEntries,
  buildAppServerControlHistoryEntries,
  buildAppServerControlRuntimeStats,
  formatHistoryMeta,
  formatAppServerControlTurnDuration,
  preservePersistentCommandEntries,
  shouldHideStatusInMeta,
  withActivationIssueNotice,
  withLiveAssistantState,
  withTrailingBusyIndicator,
} from './historyProcessing';
export {
  computeHistoryVirtualWindow,
  hasActiveAppServerControlSelectionInPanel,
  isScrollContainerNearBottom,
  resolveHistoryScrollMode,
} from './historyViewport';
export { createAgentHistoryDom, resolveToolCallOutputLineLimit } from './historyDom';
export { suppressActiveComposerRequestEntries } from './historyRender';
export { applyCanonicalAppServerControlDelta } from './snapshotState';

async function waitForInitialAppServerControlSnapshot(
  sessionId: string,
  state: SessionAppServerControlViewState,
  activationRunId: number,
): Promise<AppServerControlHistorySnapshot> {
  let lastError: unknown = null;
  for (let attempt = 1; attempt <= 12; attempt += 1) {
    ensureAppServerControlActivationIsCurrent(state, activationRunId);
    try {
      const desiredWindowCount = resolveAppServerControlHistoryWindowTargetCount(
        state.historyViewport,
        state.historyWindowCount,
        state.historyObservedHeights.values(),
      );
      state.historyWindowTargetCount = desiredWindowCount;
      const windowRevision = issueHistoryWindowRevision(sessionId, state);
      const snapshot = state.snapshot
        ? await requestAppServerControlHistoryWindow(
            sessionId,
            state,
            state.historyWindowStart,
            state.historyWindowCount,
            windowRevision,
          )
        : await requestAppServerControlHistoryWindow(
            sessionId,
            state,
            undefined,
            desiredWindowCount,
            windowRevision,
          );
      traceAppServerControlHistoryFetch(sessionId, snapshot, 'initial');
      applyAppServerControlHistoryWindowState(state, snapshot);
      ensureAppServerControlActivationIsCurrent(state, activationRunId);
      if (attempt > 1) {
        appendActivationTrace(
          state,
          'positive',
          `history window retry ${attempt}`,
          appServerControlText(
            'appServerControl.activation.snapshotReady.summary',
            'AppServerControl history window became available.',
          ),
          appServerControlFormat(
            'appServerControl.activation.snapshotReady.body',
            'MidTerm produced the first canonical AppServerControl history window on retry {attempt}.',
            { attempt },
          ),
        );
      }
      return snapshot;
    } catch (error) {
      if (isStaleAppServerControlActivationError(error)) {
        throw error;
      }
      lastError = error;
      appendActivationTrace(
        state,
        attempt === 12 ? 'attention' : 'warning',
        `history window retry ${attempt}`,
        appServerControlText(
          'appServerControl.activation.snapshotPending',
          'AppServerControl history window not ready yet.',
        ),
        describeError(error),
      );
      renderCurrentAgentView(sessionId);
      if (attempt < 12) {
        await new Promise((resolve) => window.setTimeout(resolve, 200));
      }
    }
  }

  throw lastError instanceof Error ? lastError : new Error(String(lastError));
}

async function retryAppServerControlActivation(sessionId: string): Promise<void> {
  const state = viewStates.get(sessionId);
  if (!state || state.activationActionBusy) {
    return;
  }

  state.activationActionBusy = true;
  state.activationIssue = null;
  state.activationError = null;
  appendActivationTrace(
    state,
    'info',
    'retry',
    appServerControlText(
      'appServerControl.activation.retry.summary',
      'Retrying AppServerControl attach.',
    ),
    appServerControlText(
      'appServerControl.activation.retry.detail',
      'MidTerm is retrying the live AppServerControl attach for this session.',
    ),
  );
  renderCurrentAgentView(sessionId);

  try {
    if (state.snapshot) {
      state.activationRunId += 1;
      await resumeAppServerControlFromHistory(sessionId, state, state.activationRunId);
    } else {
      await activateAgentView(sessionId);
    }
  } finally {
    const current = viewStates.get(sessionId);
    if (current) {
      current.activationActionBusy = false;
      renderCurrentAgentView(sessionId);
    }
  }
}

/* eslint-enable max-lines */
