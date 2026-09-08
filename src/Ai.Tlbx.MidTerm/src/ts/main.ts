/**
 * tlbx Terminal Client
 *
 * Web-based terminal multiplexer frontend using xterm.js.
 * Main entry point - wires together all modules.
 */

import { initLoginPage } from './modules/login';
import { initTrustPage } from './modules/trust';
import { initThemeFromBrowserCache } from './modules/theming';
import { initAuthSessionLifetime } from './modules/auth/sessionLifetime';
import { createLogger, initLogConcerns } from './modules/logging';
import {
  connectStateWebSocket,
  connectMuxWebSocket,
  connectSettingsWebSocket,
  handleStateUpdate,
  setSelectSessionCallback,
  setTerminalNotificationCallback,
  sendInput,
  requestBufferRefresh,
  updateTerminalVisibility,
  setupBrowserLifecycleRecovery,
  setSessionBytesCallback,
  setSuppressHeatCallback,
  reportBrowserActivity,
  getBrowserTransportSnapshot,
} from './modules/comms';
import { connectInitialSessionTransports } from './modules/comms/initialMuxConnection';
import { initBadges } from './modules/badges';
import {
  preloadTerminalFont,
  initCalibrationTerminal,
  setShowTerminalNotificationCallback,
  setupResizeObserver,
  setupVisualViewport,
  bindSearchEvents,
  focusActiveTerminal,
  setupGlobalFocusReclaim,
  handleClipboardPaste,
  initMobilePiP,
  isMobilePiPActive,
  initDevSoftKeyboardSimulator,
  resolveLaunchDimensions,
  scheduleForegroundResizeRecovery,
  syncWebglSessionPriority,
  initTerminalRecovery,
} from './modules/terminal';
import {
  getSessionDisplayName,
  setSessionListCallbacks,
  toggleSidebar,
  closeSidebar,
  collapseSidebar,
  expandSidebar,
  restoreSidebarState,
  setupSidebarResize,
  initShareAccessButton,
  initNetworkSection,
  initVoiceSection,
  initializeSessionList,
  initializeSidebarUpdater,
  initSessionDrag,
  initTrafficIndicator,
  initHeatIndicator,
  recordBytes,
  suppressAllHeat,
  renderSessionList,
  syncSidebarNavButtons,
  updateEmptyState,
  updateMobileTitle,
} from './modules/sidebar';
import { initI18n, t } from './modules/i18n';
import { initPwaInstall, syncAppModeClasses } from './modules/pwaInstall';
import { initTabTitle } from './modules/tabTitle';
import { bindVoiceEvents, initVoiceControls } from './modules/voice';
import { initChatPanel } from './modules/chat';
import { toggleSettings } from './modules/settings';
import { bindAuthEvents } from './modules/auth';
import { fetchBootstrap, getBootstrapData } from './modules/bootstrap';
import {
  checkForUpdates,
  applyFullUpdate,
  showChangelog,
  closeChangelog,
  disableChangelogAfterUpdate,
  showUpdateLog,
  dismissUpdateNotification,
  bindFooterUpdateLink,
  clearPendingAppRefreshMarker,
  handlePrimaryUpdateAction,
  initAppShellStatePersistence,
  initUpdateRuntime,
  initUpdateUi,
} from './modules/updating';
import { initDiagnosticsPanel } from './modules/diagnostics';
import {
  animateBookmarkSaveSuccess,
  closeHistoryDropdown,
  getBookmarkSurfaceType,
  initHistoryDropdown,
  initSessionInputHistoryMenus,
  type LaunchEntry,
} from './modules/history';
import { linkAndReplayRemoteBookmark } from './modules/history/remoteBookmarkLaunch';
import { buildLocalBookmarkLaunchRequest } from './modules/history/bookmarkLaunch';
import {
  isAppServerControlHistoryEntry,
  normalizeHistoryAppServerControlProfile,
} from './modules/history/launchMode';
import { getForegroundInfo, addProcessStateListener } from './modules/process';
import {
  initTouchController,
  dismissTouchController,
  restoreTouchController,
} from './modules/touchController';
import { initFileViewer } from './modules/fileViewer';
import { initManagerBar } from './modules/managerBar';
import {
  initLayoutRenderer,
  initDockOverlay,
  dockSession,
  getLayoutSessionIds,
  isSessionInLayout,
  isLayoutActive,
  focusLayoutSession,
  initLayoutPersistence,
  getLayoutRoot,
} from './modules/layout';
import {
  initSessionTabs,
  onTabActivated,
  setSessionAppServerControlAvailability,
  switchTab,
} from './modules/sessionTabs';
import {
  initAgentView,
  getAppServerControlDebugScenarioNames,
  recoverAppServerControlAfterBrowserResume,
  suspendAppServerControlForBrowserBackground,
  showAppServerControlDebugScenario,
} from './modules/agentView';
import {
  activateMobileTab,
  bindMobileActionsMenu,
  closeMobileActionsMenu,
  syncMobileTabActionState,
} from './modules/sessionTabs/mobileActions';
import { openSessionLauncher, type SessionLauncherSelection } from './modules/sessionLauncher';
import { initFileBrowser } from './modules/fileBrowser';
import * as gitModule from './modules/git';
import { initCommandsPanel } from './modules/commands';
import { initWebPreview, syncActiveWebPreview } from './modules/web';
import { initBackButtonGuard } from './modules/navigation/backButtonGuard';
import {
  bindHubSettings,
  createRemoteSession,
  getHubSidebarRenderSignature,
  initHubRuntime,
  isHubSessionId,
  refreshHubState,
  renderHubSettings,
  subscribeHubState,
  toHubCompositeId,
  suspendHubChannelForBrowserBackground,
  recoverHubChannelAfterBrowserResume,
} from './modules/hub';
import {
  initSessionShareButton,
  isSharedSessionRoute,
  claimSharedSessionAccess,
  fetchSharedBootstrap,
  applySharedSessionMode,
  showSharedSessionError,
} from './modules/share';
import { initDockState } from './modules/dockState';
import { initSmartInput, setAppServerControlResumeConversationHandler } from './modules/smartInput';
import { openProviderResumePicker, type ResumeProvider } from './modules/providerResume';
import { initSpacesDropdown, toggleSpacesDropdown } from './modules/spaces';
import { initSpacesRuntime, type SpaceSurface } from './modules/spaces/runtime';
import { initOperatorView } from './modules/operator';
import { initActionGraphsView } from './modules/actionGraphs';
import { createMidtermPerfDebugApi } from './modules/perf/midtermPerfDebug';
import {
  cacheDOMElements,
  sessionTerminals,
  hiddenSessionIds,
  dom,
  setFontsReadyPromise,
  newlyCreatedSessions,
  bellNotificationsSuppressed,
  activeNotifications,
} from './state';
import {
  $activeSessionId,
  $settingsOpen,
  $operatorOpen,
  $sessionList,
  $currentSettings,
  $layout,
  setSession,
  getSession,
  setProcessState,
} from './stores';
import type { Session } from './types';
import {
  bindClick,
  getOrCreateClientId,
  initializeTabIdentity,
  TAB_ID_COLLISION_EVENT,
} from './utils';
import { showAlert } from './utils/dialog';
import { createSessionActionHandlers } from './sessionActions';
import { getSessionLaunchErrorMessage, showSessionLaunchFailure } from './sessionLaunchErrors';
import { clearPendingSession, createPendingSession } from './pendingSession';
import {
  createSession as apiCreateSession,
  bootstrapWorker,
  waitForApiReachability,
  setSessionBookmark,
  setSessionNotes,
} from './api/client';
import type { ShellType } from './api/types';
import {
  buildTerminalNotificationBody,
  shouldShowDesktopTerminalNotification,
  type TerminalNotificationSignal,
} from './modules/terminal/terminalNotifications';

// Create logger for main module
const log = createLogger('main');
const bookmarkLaunchesInFlight = new Set<string>();

function attachBookmarkToSession(
  sessionId: string,
  bookmarkId: string | null,
  label: string | null,
  notes: string | null = null,
): void {
  if (!bookmarkId && !label && !notes) {
    return;
  }

  const applyBookmark = (): void => {
    const session = getSession(sessionId);
    if (!session) {
      window.setTimeout(applyBookmark, 100);
      return;
    }

    if (bookmarkId) {
      setSession({ ...session, bookmarkId });
      setSessionBookmark(sessionId, bookmarkId).catch(() => {});
    }

    if (notes) {
      setSession({ ...session, bookmarkId: bookmarkId ?? session.bookmarkId, notes });
      setSessionNotes(sessionId, notes).catch(() => {});
    }

    if (label) {
      renameSession(sessionId, label);
    }
  };

  applyBookmark();
}

// Debug export for console access (typed in types/xterm-extensions.d.ts)
window.mmDebug = {
  get terminals() {
    return sessionTerminals;
  },
  get activeId() {
    return $activeSessionId.get();
  },
  get settings() {
    return $currentSettings.get();
  },
  transport(sessionId: string) {
    return getBrowserTransportSnapshot(sessionId);
  },
  perf: createMidtermPerfDebugApi(),
  layout: {
    dock(
      targetSessionId: string,
      draggedSessionId: string,
      position: 'left' | 'right' | 'top' | 'bottom',
    ) {
      dockSession(targetSessionId, draggedSessionId, position);
    },
    focus(sessionId: string) {
      focusLayoutSession(sessionId);
    },
    get sessions() {
      return getLayoutSessionIds();
    },
    isSessionInLayout(sessionId: string) {
      return isSessionInLayout(sessionId);
    },
    get rootVisible() {
      return !getLayoutRoot()?.classList.contains('hidden');
    },
  },
  appServerControl: {
    get scenarios() {
      return [...getAppServerControlDebugScenarioNames()];
    },
    async showScenario(
      sessionId: string,
      scenario: 'mixed' | 'tables' | 'long' | 'massive' | 'workflow' = 'mixed',
    ): Promise<boolean> {
      setSessionAppServerControlAvailability(sessionId, true);
      switchTab(sessionId, 'agent');
      await Promise.resolve();
      return showAppServerControlDebugScenario(sessionId, scenario);
    },
  },
};

// =============================================================================
// Initialization
// =============================================================================

initThemeFromBrowserCache();
clearPendingAppRefreshMarker();

document.addEventListener('DOMContentLoaded', () => {
  const path = window.location.pathname;
  if (path === '/login' || path === '/login.html') {
    void initLoginPage();
  } else if (path === '/trust' || path === '/trust.html') {
    void initTrustPage();
  } else if (isSharedSessionRoute()) {
    void initShared();
  } else {
    void init();
  }
});

async function init(): Promise<void> {
  initAuthSessionLifetime();
  initLogConcerns();
  log.info(() => 'tlbx frontend initializing');
  initBackButtonGuard();

  cacheDOMElements();
  // Chrome can offer installation while translations or other startup work is
  // still loading. Capture that one-shot event before the first await.
  initPwaInstall();
  await initI18n();
  initUpdateUi();
  initUpdateRuntime();
  initAppShellStatePersistence();
  initTrafficIndicator();
  setSessionBytesCallback(recordBytes);
  initTerminalRecovery();
  setSuppressHeatCallback(suppressAllHeat);
  initHeatIndicator();
  initBadges();
  initFileViewer();
  restoreSidebarState();
  setupSidebarResize();
  initializeSessionList();
  initializeSidebarUpdater();
  initTabTitle();
  initSessionDrag();
  initLayoutRenderer();
  initLayoutPersistence();
  initDockOverlay();
  syncSidebarNavButtons($currentSettings.get());
  initHistoryDropdown(
    (entry) => {
      void spawnFromHistory(entry);
    },
    (entryId, newLabel) => {
      const session = $sessionList.get().find((candidate) => candidate.bookmarkId === entryId);
      if (session) {
        renameSession(session.id, newLabel || null);
      }
    },
  );
  initSessionInputHistoryMenus();
  initOperatorView({ onSelectSession: selectSession });
  initActionGraphsView({ onSelectSession: selectSession });
  const spacesRuntimeOptions = {
    resolveLaunchDimensions: resolveNewSessionDimensions,
    resolveShell: resolveLauncherShell,
    onOpenLocalSession: (session: Session, surface: SpaceSurface) => {
      setSession(session);
      newlyCreatedSessions.add(session.id);
      if (surface !== 'terminal') {
        setSessionAppServerControlAvailability(session.id, true);
      }

      selectSession(session.id);
      if (surface !== 'terminal') {
        requestAnimationFrame(() => {
          switchTab(session.id, 'agent');
        });
      }
    },
    onOpenRemoteSession: async (machineId: string, sessionId: string, surface: SpaceSurface) => {
      await refreshHubState();
      const compositeId = toHubCompositeId(machineId, sessionId);
      newlyCreatedSessions.add(compositeId);
      selectSession(compositeId);
      if (surface !== 'terminal') {
        requestAnimationFrame(() => {
          switchTab(compositeId, 'agent');
        });
      }
    },
    onSelectLocalSession: (sessionId: string) => {
      selectSession(sessionId);
    },
    onSelectRemoteSession: (machineId: string, sessionId: string) => {
      selectSession(toHubCompositeId(machineId, sessionId));
    },
    onLaunchRecent: (machineId: string | null, entry: LaunchEntry) => {
      void spawnFromHistory(entry, machineId);
    },
  };
  initSpacesRuntime(spacesRuntimeOptions);
  initSpacesDropdown(spacesRuntimeOptions);
  $currentSettings.subscribe((settings) => {
    syncSidebarNavButtons(settings);
  });

  const fontPromise = preloadTerminalFont();
  setFontsReadyPromise(fontPromise);

  // Initialize calibration terminal after fonts are ready for accurate measurements
  void fontPromise.then(() => initCalibrationTerminal());

  registerCallbacks();
  initSessionTabs();
  bindBrowserSurfaceActivityReporting();
  initAgentView();
  initFileBrowser();
  getOrCreateClientId(); // Ensure mt-client-id cookie exists before WS upgrade
  bindTabIdentityCollisionRecovery();
  await initializeTabIdentity();
  bindTerminalVisibilitySync();
  initMobilePiP();
  setupVisibilityChangeHandler(true);
  connectInitialSessionTransports();
  connectSettingsWebSocket();

  bindEvents();
  bindAuthEvents();
  bindSearchEvents();
  setupGlobalFocusReclaim();
  initShareAccessButton();
  initNetworkSection();
  initVoiceSection();
  bindVoiceEvents();
  await initVoiceControls();
  initChatPanel();
  syncAppModeClasses();
  setupResizeObserver();
  setupVisualViewport();
  initTouchController();
  initSmartInput();
  initDevSoftKeyboardSimulator();
  initManagerBar();
  gitModule.initGitPanel();
  gitModule.connectGitWebSocket();
  initCommandsPanel();
  initWebPreview();
  initSessionShareButton();
  initDockState();
  initHubRuntime();
  let previousHubSidebarSignature = getHubSidebarRenderSignature();
  subscribeHubState(() => {
    const nextHubSidebarSignature = getHubSidebarRenderSignature();
    if (previousHubSidebarSignature !== nextHubSidebarSignature) {
      previousHubSidebarSignature = nextHubSidebarSignature;
      renderSessionList();
      updateEmptyState();
      updateMobileTitle();
      syncMobileTabActionState();
    }
    renderHubSettings();
  });

  // Single bootstrap call replaces: fetchVersion, fetchNetworks, fetchSettings,
  // checkAuthStatus, checkUpdateResult, and checkSystemHealth
  void fetchBootstrap();
  bindNotificationPermissionRequest();
  initDiagnosticsPanel();
  bindHubSettings();

  let serviceWorker: ServiceWorkerContainer | undefined;
  try {
    serviceWorker = navigator.serviceWorker;
  } catch {
    serviceWorker = undefined;
  }

  if (serviceWorker?.register) {
    serviceWorker.register('/sw.js', { scope: '/' }).catch((error: unknown) => {
      log.warn(() => `PWA service worker registration failed: ${String(error)}`);
    });
  }

  log.info(() => 'tlbx frontend initialized');
}

async function initShared(): Promise<void> {
  initLogConcerns();
  log.info(() => 'tlbx shared frontend initializing');
  initBackButtonGuard();

  cacheDOMElements();
  await initI18n();
  initUpdateUi();
  initUpdateRuntime();
  initAppShellStatePersistence();

  const fontPromise = preloadTerminalFont();
  setFontsReadyPromise(fontPromise);
  void fontPromise.then(() => initCalibrationTerminal());
  bindTabIdentityCollisionRecovery();
  await initializeTabIdentity();

  setSelectSessionCallback(selectSession);
  setTerminalNotificationCallback(showTerminalNotification);
  setShowTerminalNotificationCallback((sessionId, signal) => {
    if (isHubSessionId(sessionId)) showTerminalNotification(sessionId, signal);
  });
  addProcessStateListener((sessionId, state) => {
    setProcessState(sessionId, { ...state });
  });

  initSessionTabs();
  bindBrowserSurfaceActivityReporting();
  bindTerminalVisibilitySync();
  bindSearchEvents();
  setupGlobalFocusReclaim();
  syncAppModeClasses();
  setupResizeObserver();
  setupVisualViewport();
  setupVisibilityChangeHandler(false);

  try {
    await claimSharedSessionAccess();
    const bootstrap = await fetchSharedBootstrap();
    applySharedSessionMode(bootstrap);
    handleStateUpdate(bootstrap.session ? [bootstrap.session] : []);
  } catch (error) {
    log.error(() => `Shared session bootstrap failed: ${String(error)}`);
    showSharedSessionError(t('share.shared.invalid'));
    return;
  }

  connectStateWebSocket();
  connectMuxWebSocket();

  log.info(() => 'tlbx shared frontend initialized');
}

function getVisibleTerminalSessionIds(): string[] {
  if ($settingsOpen.get() || $operatorOpen.get()) {
    return [];
  }

  if (!isLayoutActive() || getLayoutRoot()?.classList.contains('hidden')) {
    return [];
  }

  return getLayoutSessionIds().filter((sessionId) => !isHubSessionId(sessionId));
}

function syncMuxTerminalVisibility(): void {
  const activeSessionId = $activeSessionId.get();
  const visibleSessionIds = getVisibleTerminalSessionIds();
  const prioritySessionIds = new Set(visibleSessionIds);
  if (activeSessionId && !isHubSessionId(activeSessionId)) {
    prioritySessionIds.add(activeSessionId);
  }
  const backgroundSessionIds: string[] = [];
  sessionTerminals.forEach((_state, sessionId) => {
    if (
      !isHubSessionId(sessionId) &&
      !hiddenSessionIds.has(sessionId) &&
      !prioritySessionIds.has(sessionId)
    ) {
      backgroundSessionIds.push(sessionId);
    }
  });
  updateTerminalVisibility(activeSessionId, visibleSessionIds, backgroundSessionIds);

  syncWebglSessionPriority([...prioritySessionIds]);
}

function refreshHiddenSessionsForFullReplay(): void {
  const activeSessionId = $activeSessionId.get();
  const visibleSessionIds = new Set(getVisibleTerminalSessionIds());

  sessionTerminals.forEach((_state, sessionId) => {
    if (
      isHubSessionId(sessionId) ||
      sessionId === activeSessionId ||
      visibleSessionIds.has(sessionId)
    ) {
      return;
    }

    requestBufferRefresh(sessionId);
  });
}

function bindTerminalVisibilitySync(): void {
  syncMuxTerminalVisibility();

  $activeSessionId.subscribe(() => {
    syncMuxTerminalVisibility();
    reportBrowserActivity(undefined, true);
  });

  $layout.subscribe(() => {
    syncMuxTerminalVisibility();
  });

  $settingsOpen.subscribe(() => {
    syncMuxTerminalVisibility();
  });
  $operatorOpen.subscribe(syncMuxTerminalVisibility);

  let lastResumeMode = $currentSettings.get()?.resumeMode ?? null;
  $currentSettings.subscribe((settings) => {
    const nextResumeMode = settings?.resumeMode ?? null;
    if (lastResumeMode === 'quickResume' && nextResumeMode === 'fullReplay') {
      refreshHiddenSessionsForFullReplay();
    }
    lastResumeMode = nextResumeMode;
    syncMuxTerminalVisibility();
  });
}

function bindBrowserSurfaceActivityReporting(): void {
  const reportActiveSurface = (sessionId: string): void => {
    if ($activeSessionId.get() === sessionId) {
      reportBrowserActivity(undefined, true);
    }
  };

  onTabActivated('terminal', reportActiveSurface);
  onTabActivated('agent', reportActiveSurface);
  onTabActivated('files', reportActiveSurface);
}

function bindTabIdentityCollisionRecovery(): void {
  window.addEventListener(
    TAB_ID_COLLISION_EVENT,
    () => {
      window.location.reload();
    },
    { once: true },
  );
}

// =============================================================================
// Callback Registration
// =============================================================================

function registerCallbacks(): void {
  setSelectSessionCallback(selectSession);
  setTerminalNotificationCallback(showTerminalNotification);
  setShowTerminalNotificationCallback((sessionId, signal) => {
    if (isHubSessionId(sessionId)) showTerminalNotification(sessionId, signal);
  });

  addProcessStateListener((sessionId, state) => {
    setProcessState(sessionId, { ...state });
  });

  setSessionListCallbacks({
    onSelect: selectSession,
    onDelete: deleteSession,
    onRename: startInlineRename,
    onToggleAgentControl: toggleAgentControl,
    onPinToHistory: (sessionId: string) => {
      void pinSessionToHistory(sessionId);
    },
    onEnableMidtermFeatures: (sessionId: string) => {
      void enableMidtermFeatures(sessionId);
    },
    onLaunchRecent: (machineId, entry) => {
      void spawnFromHistory(entry, machineId);
    },
    onCloseSidebar: closeSidebar,
  });
}

// =============================================================================
// Visibility Change Handler
// =============================================================================

function applyScrollbackProtection(): void {
  if ($currentSettings.get()?.scrollbackProtection !== true) return;

  const activeId = $activeSessionId.get();
  const state = activeId ? sessionTerminals.get(activeId) : null;
  if (!state?.terminal) return;
  if (state.terminal.modes.synchronizedOutputMode) return;

  const bufferBefore = state.terminal.buffer.active;
  if (bufferBefore.viewportY >= bufferBefore.baseY) {
    return;
  }

  const scrollPosBefore = bufferBefore.viewportY;

  setTimeout(() => {
    if ($activeSessionId.get() !== activeId) return;
    if (!state.opened || state.container.classList.contains('hidden')) return;
    if (state.terminal.modes.synchronizedOutputMode) return;

    const scrollPosAfter = state.terminal.buffer.active.viewportY;
    const delta = Math.abs(scrollPosAfter - scrollPosBefore);
    if (delta > 50) {
      state.terminal.scrollToLine(scrollPosBefore);
    }
  }, 50);
}

function setupVisibilityChangeHandler(includeSettingsChannel: boolean): void {
  setupBrowserLifecycleRecovery({
    getVisibleTerminalSessionIds,
    syncMuxTerminalVisibility,
    focusActiveTerminal,
    applyScrollbackProtection,
    recoverTerminalPresentationAfterResume: scheduleForegroundResizeRecovery,
    keepTerminalOutputActiveWhileHidden: isMobilePiPActive,
    suspendAdditionalTerminalTransport: suspendHubChannelForBrowserBackground,
    recoverAdditionalTerminalTransport: recoverHubChannelAfterBrowserResume,
    suspendAppServerControlForBackground: suspendAppServerControlForBrowserBackground,
    suspendAncillaryTransportForBackground: gitModule.suspendGitWebSocketForBrowserBackground,
    recoverAncillaryTransportAfterResume: gitModule.recoverGitWebSocketAfterBrowserResume,
    ...(includeSettingsChannel
      ? { recoverAppServerControlAfterResume: recoverAppServerControlAfterBrowserResume }
      : {}),
    ...(includeSettingsChannel
      ? { reconnectSettingsAfterLongResume: connectSettingsWebSocket }
      : {}),
  });
}

// =============================================================================
// Session Management
// =============================================================================

async function resolveNewSessionDimensions(): Promise<{ cols: number; rows: number }> {
  return resolveLaunchDimensions($currentSettings.get(), 'launcher');
}

function resolveLauncherShell(): ShellType | null {
  const settings = $currentSettings.get();
  if (settings?.defaultShell) {
    return settings.defaultShell;
  }

  const platform = getBootstrapData()?.platform.toLowerCase();
  if (platform === 'windows') {
    return 'Pwsh';
  }

  if (platform === 'macos') {
    return 'Zsh';
  }

  return 'Bash';
}

function isAppServerControlOnlySession(session: Session | null | undefined): boolean {
  return session?.appServerControlOnly === true;
}

function activateNewAppServerControlSession(session: Session): void {
  setSession(session);
  newlyCreatedSessions.add(session.id);
  setSessionAppServerControlAvailability(session.id, true);
  selectSession(session.id);
  requestAnimationFrame(() => {
    switchTab(session.id, 'agent');
  });
}

const {
  deleteSession,
  enableMidtermFeatures,
  pinSessionToHistory,
  promptRenameSession,
  renameSession,
  selectSession,
  startInlineRename,
  toggleAgentControl,
} = createSessionActionHandlers({
  animateBookmarkSaveSuccess,
  buildAppServerControlHistoryDedupeKey,
  closeMobileActionsMenu,
  getBookmarkSurfaceType,
  isAppServerControlOnlySession,
});
setAppServerControlResumeConversationHandler((args) => {
  void resumeAppServerControlConversationFromCommandBay(args);
});

async function createSession(): Promise<void> {
  let selection: SessionLauncherSelection | null;
  try {
    selection = await openSessionLauncher();
  } catch (error) {
    void showAlert(getSessionLaunchErrorMessage(error), {
      title: t('sessionLauncher.loadFailed'),
    });
    return;
  }

  if (!selection) return;

  try {
    await waitForApiReachability();
  } catch (error: unknown) {
    showSessionLaunchFailure(error);
    return;
  }

  const { cols, rows } = await resolveNewSessionDimensions();
  const tempId = createPendingSession(cols, rows);
  const shell = resolveLauncherShell();
  const workingDirectory = selection.workingDirectory?.trim() || undefined;
  const createSessionRequest = {
    cols,
    rows,
    shell,
    ...(workingDirectory ? { workingDirectory } : {}),
  };
  closeSidebar();

  const target = selection.target;
  if (target.kind === 'hub') {
    if (selection.provider !== 'terminal') {
      clearPendingSession(tempId);
      void showAlert(t('sessionLauncher.remoteTerminalOnly'), {
        title: t('sessionLauncher.createFailed'),
      });
      return;
    }

    createRemoteSession(target.machineId, createSessionRequest)
      .then(async (session) => {
        await refreshHubState();
        clearPendingSession(tempId);
        const compositeId = toHubCompositeId(target.machineId, session.id);
        newlyCreatedSessions.add(compositeId);
        selectSession(compositeId);
      })
      .catch((e: unknown) => {
        clearPendingSession(tempId);
        log.error(() => `Failed to create remote session: ${String(e)}`);
        void showAlert(getSessionLaunchErrorMessage(e), {
          title: t('sessionLauncher.createFailed'),
        });
      });
    return;
  }

  if (selection.provider === 'terminal') {
    apiCreateSession(createSessionRequest)
      .then(({ data }) => {
        clearPendingSession(tempId);
        if (!data) return;

        setSession(data);
        newlyCreatedSessions.add(data.id);
        selectSession(data.id);
      })
      .catch((e: unknown) => {
        clearPendingSession(tempId);
        log.error(() => `Failed to create session: ${String(e)}`);
        showSessionLaunchFailure(e);
      });
    return;
  }

  bootstrapWorker({
    ...createSessionRequest,
    agentControlled: false,
    injectGuidance: true,
    profile: selection.provider,
    resumeThreadId: selection.resumeThreadId ?? null,
    appServerControlOnly: true,
    launchDelayMs: 0,
    slashCommands: [],
    slashCommandDelayMs: 350,
  })
    .then(({ data }) => {
      clearPendingSession(tempId);
      const session = data?.session;
      if (!session) {
        return;
      }

      activateNewAppServerControlSession(session);
    })
    .catch((e: unknown) => {
      clearPendingSession(tempId);
      log.error(() => `Failed to create worker session: ${String(e)}`);
      showSessionLaunchFailure(e);
    });
}

// eslint-disable-next-line complexity -- bookmark launch owns local, remote, and Agent Controller reconciliation in one guarded lifecycle.
async function spawnFromHistory(
  entry: LaunchEntry,
  machineId: string | null = null,
): Promise<void> {
  const launchKey = `${machineId ?? 'local'}:${entry.id}:${entry.commandLine}`;
  if (bookmarkLaunchesInFlight.has(launchKey)) {
    return;
  }
  bookmarkLaunchesInFlight.add(launchKey);

  let pendingSessionId: string | null = null;

  try {
    const { cols, rows } = await resolveLaunchDimensions($currentSettings.get(), 'history');
    const appServerControlBookmark = isAppServerControlHistoryEntry(entry);
    pendingSessionId = createPendingSession(cols, rows, {
      name: entry.label || entry.foregroundProcessDisplayName || entry.executable || 'Starting…',
      currentDirectory: entry.workingDirectory,
      shellType: entry.shellType,
      bookmarkId: entry.id,
      appServerControlOnly: appServerControlBookmark,
      profileHint: entry.profile,
    });
    closeSidebar();
    await waitForApiReachability();

    if (machineId) {
      if (appServerControlBookmark) {
        void showAlert(t('sessionLauncher.remoteTerminalOnly'), {
          title: t('sessionLauncher.createFailed'),
        });
        return;
      }

      const session = await createRemoteSession(machineId, {
        cols,
        rows,
        shell: entry.shellType || null,
        workingDirectory: entry.workingDirectory || null,
      });
      await refreshHubState();
      clearPendingSession(pendingSessionId);
      pendingSessionId = null;
      const compositeId = toHubCompositeId(machineId, session.id);
      newlyCreatedSessions.add(compositeId);
      selectSession(compositeId);
      linkAndReplayRemoteBookmark(machineId, session.id, compositeId, entry);
      return;
    }

    if (appServerControlBookmark) {
      const profile = normalizeHistoryAppServerControlProfile(entry.profile);
      if (profile) {
        const { data } = await bootstrapWorker({
          cols,
          rows,
          shell: resolveLauncherShell(),
          workingDirectory: entry.workingDirectory || null,
          agentControlled: false,
          injectGuidance: true,
          profile,
          appServerControlOnly: true,
          launchDelayMs: 0,
          slashCommands: [],
          slashCommandDelayMs: 350,
        });
        const session = data?.session;
        if (!session) {
          throw new Error('The Agent Controller launch returned no session.');
        }

        clearPendingSession(pendingSessionId);
        pendingSessionId = null;
        activateNewAppServerControlSession(session);
        attachBookmarkToSession(session.id, entry.id, entry.label ?? null, entry.notes ?? null);
        return;
      }
    }

    const { data } = await apiCreateSession(buildLocalBookmarkLaunchRequest(entry, cols, rows));
    if (!data) {
      throw new Error('The terminal launch returned no session.');
    }
    clearPendingSession(pendingSessionId);
    pendingSessionId = null;
    setSession(data);
    newlyCreatedSessions.add(data.id);
    selectSession(data.id);
    attachBookmarkToSession(data.id, entry.id, entry.label ?? null, entry.notes ?? null);
  } catch (e: unknown) {
    log.error(() => `Failed to spawn bookmark session: ${String(e)}`);
    showSessionLaunchFailure(e);
  } finally {
    if (pendingSessionId) {
      clearPendingSession(pendingSessionId);
    }
    bookmarkLaunchesInFlight.delete(launchKey);
  }
}

async function resumeAppServerControlConversationFromCommandBay(args: {
  sessionId: string;
  provider: ResumeProvider;
  workingDirectory: string;
}): Promise<void> {
  const sourceSession = getSession(args.sessionId);
  if (!sourceSession) return;

  const candidate = await openProviderResumePicker({
    provider: args.provider,
    workingDirectory: args.workingDirectory,
    initialScope: 'current',
  });
  if (!candidate) return;

  const { cols, rows } = await resolveLaunchDimensions($currentSettings.get(), 'history');
  const tempId = createPendingSession(cols, rows);

  bootstrapWorker({
    cols,
    rows,
    shell: resolveLauncherShell(),
    workingDirectory: args.workingDirectory,
    agentControlled: false,
    injectGuidance: true,
    profile: args.provider,
    resumeThreadId: candidate.sessionId,
    spaceId: sourceSession.spaceId ?? null,
    workspacePath: sourceSession.workspacePath ?? args.workingDirectory,
    surface: args.provider,
    appServerControlOnly: true,
    launchDelayMs: 0,
    slashCommands: [],
    slashCommandDelayMs: 350,
  })
    .then(({ data }) => {
      clearPendingSession(tempId);
      const session = data?.session;
      if (!session) {
        return;
      }

      activateNewAppServerControlSession(session);
      attachBookmarkToSession(session.id, sourceSession.bookmarkId ?? null, null);
    })
    .catch((e: unknown) => {
      clearPendingSession(tempId);
      log.error(() => `Failed to resume provider conversation from Command Bay: ${String(e)}`);
      showSessionLaunchFailure(e);
    });
}

function buildAppServerControlHistoryDedupeKey(profile: string, workingDirectory: string): string {
  const normalizedPath = workingDirectory
    .replace(/\\/g, '/')
    .trim()
    .replace(/\/+$/, '')
    .toLowerCase();
  return `appServerControl|${profile}|${normalizedPath}`;
}

// =============================================================================
// Notifications
// =============================================================================

function requestNotificationPermission(): void {
  if (!('Notification' in window)) return;
  if (Notification.permission === 'default') {
    Notification.requestPermission().catch(() => {});
  }
}

function bindNotificationPermissionRequest(): void {
  const bellStyle = document.getElementById('setting-bell-style');
  if (!(bellStyle instanceof HTMLSelectElement)) return;

  const requestForNotificationStyle = (): void => {
    if (bellStyle.value === 'notification' || bellStyle.value === 'both') {
      requestNotificationPermission();
    }
  };
  bellStyle.addEventListener('pointerdown', requestForNotificationStyle);
  bellStyle.addEventListener('change', requestForNotificationStyle);
}

function shouldCreateBrowserTerminalNotification(
  sessionId: string,
  bellStyle: string,
  signal: TerminalNotificationSignal,
): boolean {
  return (
    (bellStyle === 'notification' || bellStyle === 'both') &&
    !signal.nativeHandled &&
    'Notification' in window &&
    Notification.permission === 'granted' &&
    shouldShowDesktopTerminalNotification(
      {
        documentHidden: document.hidden,
        documentFocused: document.hasFocus(),
        sourceSessionActive: $activeSessionId.get() === sessionId,
      },
      signal.force === true,
    )
  );
}

function showTerminalNotification(sessionId: string, signal: TerminalNotificationSignal): void {
  const settings = $currentSettings.get();
  if (!settings) return;
  if (bellNotificationsSuppressed) return;

  const bellStyle = settings.bellStyle;
  const session = getSession(sessionId);
  const title = session ? getSessionDisplayName(session) : 'Terminal';

  if (shouldCreateBrowserTerminalNotification(sessionId, bellStyle, signal)) {
    // Close existing notification for this session (deduplication)
    const existing = activeNotifications.get(sessionId);
    if (existing) {
      existing.close();
    }

    let notification: Notification;
    try {
      notification = new Notification(title, {
        body: buildTerminalNotificationBody(signal),
        icon: '/favicon.ico',
        tag: `tlbx-terminal-${sessionId}`,
        requireInteraction: signal.priority === 'important',
      });
    } catch {
      return;
    }

    activeNotifications.set(sessionId, notification);

    notification.onclick = () => {
      window.focus();
      selectSession(sessionId);
      notification.close();
      if (activeNotifications.get(sessionId) === notification) {
        activeNotifications.delete(sessionId);
      }
    };

    if (signal.priority !== 'important') {
      // Normal notifications are transient. Important browser fallbacks remain
      // until the user interacts because native urgent delivery was unavailable.
      setTimeout(() => {
        notification.close();
        if (activeNotifications.get(sessionId) === notification) {
          activeNotifications.delete(sessionId);
        }
      }, 15000);
    }
  }

  if (bellStyle === 'visual' || bellStyle === 'both') {
    const state = sessionTerminals.get(sessionId);
    if (state) {
      state.container.classList.add('bell-flash');
      setTimeout(() => {
        state.container.classList.remove('bell-flash');
      }, 200);
    }
  }
}

function getActiveSessionTabBar(): HTMLDivElement | null {
  if (!dom.terminalsArea) return null;
  const activeSessionId = $activeSessionId.get();
  const activeSelector = activeSessionId
    ? `.session-wrapper[data-session-id="${CSS.escape(activeSessionId)}"] .session-tab-bar`
    : '.session-wrapper:not(.hidden) .session-tab-bar';
  return (
    dom.terminalsArea.querySelector<HTMLDivElement>(activeSelector) ??
    dom.terminalsArea.querySelector<HTMLDivElement>(
      '.session-wrapper:not(.hidden) .session-tab-bar',
    )
  );
}

function clickActiveSessionTabBarControl(selector: string): void {
  getActiveSessionTabBar()?.querySelector<HTMLButtonElement>(selector)?.click();
}

function bindEvents(): void {
  bindClick('btn-new-session', () => {
    void createSession();
  });
  bindClick('btn-new-session-mobile', () => {
    void createSession();
  });
  bindClick('btn-create-terminal', () => {
    void createSession();
  });

  bindClick('btn-dismiss-touchbar', dismissTouchController);
  bindClick('btn-show-touchbar', restoreTouchController);

  bindClick('btn-hamburger', toggleSidebar);
  bindClick('btn-collapse-sidebar', collapseSidebar);
  bindClick('btn-expand-sidebar', expandSidebar);
  bindMobileActionsMenu();

  if (dom.sidebarOverlay) {
    dom.sidebarOverlay.addEventListener('click', closeSidebar);
  }

  bindClick('btn-ctrlc-mobile', () => {
    const activeId = $activeSessionId.get();
    if (activeId) sendInput(activeId, '\x03');
  });
  bindClick('btn-paste-mobile', () => {
    const activeId = $activeSessionId.get();
    if (!activeId) return;
    const foreground = getForegroundInfo(activeId);
    void handleClipboardPaste(activeId, {
      foregroundName: foreground.name,
      foregroundCommandLine: foreground.commandLine,
    }).finally(() => {
      focusActiveTerminal();
    });
  });
  bindClick('btn-rename-mobile', () => {
    const activeId = $activeSessionId.get();
    if (activeId) void promptRenameSession(activeId);
  });
  bindClick('btn-rename-titlebar', () => {
    const activeId = $activeSessionId.get();
    if (activeId) void promptRenameSession(activeId);
  });
  bindClick('btn-close-mobile', () => {
    const activeId = $activeSessionId.get();
    if (activeId) deleteSession(activeId);
  });
  bindClick('btn-inject-mobile', () => {
    const activeId = $activeSessionId.get();
    if (activeId) void enableMidtermFeatures(activeId);
  });
  bindClick('btn-mobile-tab-terminal', () => {
    activateMobileTab('terminal');
  });
  bindClick('btn-mobile-tab-agent', () => {
    activateMobileTab('agent');
  });
  bindClick('btn-mobile-tab-files', () => {
    activateMobileTab('files');
  });
  bindClick('btn-mobile-strip-terminal', () => {
    activateMobileTab('terminal');
  });
  bindClick('btn-mobile-strip-agent', () => {
    activateMobileTab('agent');
  });
  bindClick('btn-mobile-strip-files', () => {
    activateMobileTab('files');
  });
  bindClick('btn-mobile-web', () => {
    // Sync the session's preview list first so opening the dock can reuse an
    // existing named preview target instead of the empty default.
    void syncActiveWebPreview().finally(() => {
      clickActiveSessionTabBarControl('[data-action="web"]');
    });
  });
  bindClick('btn-mobile-commands', () => {
    clickActiveSessionTabBarControl('[data-action="commands"]');
  });
  bindClick('btn-mobile-share', () => {
    clickActiveSessionTabBarControl('[data-action="share"]');
  });
  bindClick('btn-mobile-git', () => {
    clickActiveSessionTabBarControl('[data-action="git"]');
  });

  // Fullscreen toggle (mobile) - hide button if API not supported
  const fullscreenBtn = document.getElementById('btn-fullscreen-mobile');
  if (document.fullscreenEnabled) {
    bindClick('btn-fullscreen-mobile', () => {
      if (document.fullscreenElement) {
        void document.exitFullscreen();
      } else {
        document.documentElement.requestFullscreen().catch(() => {});
      }
    });

    document.addEventListener('fullscreenchange', () => {
      const iconEl = fullscreenBtn?.querySelector('.icon');
      if (iconEl) {
        iconEl.textContent = document.fullscreenElement ? '\ue920' : '\ue90c';
      }
    });
  } else if (fullscreenBtn) {
    fullscreenBtn.style.display = 'none';
  }

  if (dom.settingsBtn) {
    dom.settingsBtn.addEventListener('click', toggleSettings);
  }

  bindClick('update-btn', handlePrimaryUpdateAction);
  bindClick('btn-check-updates', checkForUpdates);
  bindClick('btn-full-update', () => void applyFullUpdate());
  bindClick('btn-apply-update', handlePrimaryUpdateAction);
  bindClick('btn-show-changelog', () => {
    showChangelog();
  });
  bindClick('btn-view-update-log', () => {
    void showUpdateLog();
  });
  bindClick('btn-close-changelog', closeChangelog);
  bindClick('btn-changelog-dont-show', disableChangelogAfterUpdate);
  bindClick('update-changelog-link', () => {
    showChangelog();
  });
  bindClick('update-dismiss-btn', dismissUpdateNotification);
  bindFooterUpdateLink();

  const changelogBackdrop = document.querySelector('#changelog-modal .modal-backdrop');
  if (changelogBackdrop) {
    changelogBackdrop.addEventListener('click', closeChangelog);
  }

  bindClick('btn-spaces', () => {
    closeHistoryDropdown();
    toggleSpacesDropdown();
  });
  // Global keyboard shortcut: Alt+T to create new terminal
  document.addEventListener('keydown', (e) => {
    if (e.altKey && !e.ctrlKey && !e.shiftKey && !e.metaKey && e.key.toLowerCase() === 't') {
      e.preventDefault();
      void createSession();
    }
  });
}
