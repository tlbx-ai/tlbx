/**
 * MidTerm Terminal Client
 *
 * Web-based terminal multiplexer frontend using xterm.js.
 * Main entry point - wires together all modules.
 */

import { initLoginPage } from './modules/login';
import { initTrustPage } from './modules/trust';
import { initThemeFromCookie } from './modules/theming';
import { createLogger, initLogConcerns } from './modules/logging';
import { ASSET_VERSION } from './constants';
import {
  connectStateWebSocket,
  connectMuxWebSocket,
  connectSettingsWebSocket,
  handleStateUpdate,
  setSelectSessionCallback,
  sendInput,
  requestBufferRefresh,
  updateTerminalVisibility,
  setSuppressHeatCallback,
  reportBrowserActivity,
} from './modules/comms';
import { initBadges } from './modules/badges';
import {
  preloadTerminalFont,
  initCalibrationTerminal,
  setShowBellCallback,
  setupResizeObserver,
  setupVisualViewport,
  bindSearchEvents,
  focusActiveTerminal,
  setupGlobalFocusReclaim,
  handleClipboardPaste,
  initMobilePiP,
  initDevSoftKeyboardSimulator,
  resolveLaunchDimensions,
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
  suppressAllHeat,
  renderSessionList,
  syncSidebarNavButtons,
  updateEmptyState,
  updateMobileTitle,
} from './modules/sidebar';
import { initI18n, t } from './modules/i18n';
import { initTabTitle } from './modules/tabTitle';
import { bindVoiceEvents, initVoiceControls } from './modules/voice';
import { initChatPanel } from './modules/chat';
import { toggleSettings } from './modules/settings';
import { bindAuthEvents } from './modules/auth';
import { fetchBootstrap, getBootstrapData } from './modules/bootstrap';
import {
  checkForUpdates,
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
  toggleHistoryDropdown,
  type LaunchEntry,
} from './modules/history';
import {
  isAppServerControlHistoryEntry,
  normalizeHistoryAppServerControlProfile,
} from './modules/history/launchMode';
import { getForegroundInfo, addProcessStateListener } from './modules/process';
import { buildReplayCommand } from './modules/sidebar/processDisplay';
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
  setSessionAppServerControlAvailability,
  switchTab,
} from './modules/sessionTabs';
import {
  initAgentView,
  getAppServerControlDebugScenarioNames,
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
import { initGitPanel, connectGitWebSocket } from './modules/git';
import { initCommandsPanel } from './modules/commands';
import { initWebPreview } from './modules/web';
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
import { closeSpacesDropdown, initSpacesDropdown, toggleSpacesDropdown } from './modules/spaces';
import { initSpacesRuntime, type SpaceSurface } from './modules/spaces/runtime';
import {
  cacheDOMElements,
  sessionTerminals,
  dom,
  setFontsReadyPromise,
  newlyCreatedSessions,
  pendingSessions,
  bellNotificationsSuppressed,
  activeNotifications,
} from './state';
import {
  $stateWsConnected,
  $muxWsConnected,
  $activeSessionId,
  $settingsOpen,
  $sessionList,
  $currentSettings,
  $layout,
  setSession,
  removeSession,
  getSession,
  setProcessState,
} from './stores';
import type { Session } from './types';
import { bindClick, getOrCreateClientId } from './utils';
import { showAlert } from './utils/dialog';
import { createSessionActionHandlers } from './sessionActions';
import { getSessionLaunchErrorMessage, showSessionLaunchFailure } from './sessionLaunchErrors';
import {
  createSession as apiCreateSession,
  bootstrapWorker,
  setSessionBookmark,
  setSessionNotes,
} from './api/client';
import type { ShellType } from './api/types';

// Create logger for main module
const log = createLogger('main');

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

initThemeFromCookie();
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
  initLogConcerns();
  log.info(() => 'MidTerm frontend initializing');
  initBackButtonGuard();

  cacheDOMElements();
  await initI18n();
  initUpdateUi();
  initUpdateRuntime();
  initAppShellStatePersistence();
  initTrafficIndicator();
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
  getOrCreateClientId(); // Ensure mt-client-id cookie exists before WS upgrade
  bindTerminalVisibilitySync();
  connectStateWebSocket();
  connectMuxWebSocket();
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
  initMobilePiP();
  initDevSoftKeyboardSimulator();
  initManagerBar();
  initSessionTabs();
  initAgentView();
  initFileBrowser();
  initGitPanel();
  connectGitWebSocket();
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
  requestNotificationPermission();
  initDiagnosticsPanel();
  bindHubSettings();

  setupVisibilityChangeHandler();
  initPwaInstall();

  let serviceWorker: ServiceWorkerContainer | undefined;
  try {
    serviceWorker = navigator.serviceWorker;
  } catch {
    serviceWorker = undefined;
  }

  if (serviceWorker?.register) {
    serviceWorker.register(`/js/sw.js?v=${encodeURIComponent(ASSET_VERSION)}`).catch(() => {});
  }

  log.info(() => 'MidTerm frontend initialized');
}

async function initShared(): Promise<void> {
  initLogConcerns();
  log.info(() => 'MidTerm shared frontend initializing');
  initBackButtonGuard();

  cacheDOMElements();
  await initI18n();
  initUpdateUi();
  initUpdateRuntime();
  initAppShellStatePersistence();

  const fontPromise = preloadTerminalFont();
  setFontsReadyPromise(fontPromise);
  void fontPromise.then(() => initCalibrationTerminal());

  setSelectSessionCallback(selectSession);
  setShowBellCallback(showBellNotification);
  addProcessStateListener((sessionId, state) => {
    setProcessState(sessionId, { ...state });
  });

  initSessionTabs();
  bindTerminalVisibilitySync();
  bindSearchEvents();
  setupGlobalFocusReclaim();
  syncAppModeClasses();
  setupResizeObserver();
  setupVisualViewport();
  setupVisibilityChangeHandler();

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

  log.info(() => 'MidTerm shared frontend initialized');
}

function getVisibleTerminalSessionIds(): string[] {
  if ($settingsOpen.get()) {
    return [];
  }

  if (!isLayoutActive() || getLayoutRoot()?.classList.contains('hidden')) {
    return [];
  }

  return getLayoutSessionIds().filter((sessionId) => !isHubSessionId(sessionId));
}

function syncMuxTerminalVisibility(): void {
  updateTerminalVisibility($activeSessionId.get(), getVisibleTerminalSessionIds());
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
  });

  $layout.subscribe(() => {
    syncMuxTerminalVisibility();
  });

  $settingsOpen.subscribe(() => {
    syncMuxTerminalVisibility();
  });

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

// =============================================================================
// Callback Registration
// =============================================================================

function registerCallbacks(): void {
  setSelectSessionCallback(selectSession);
  setShowBellCallback(showBellNotification);

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
  if (state.reconnectFreezeOverlay) return;
  if (state.terminal.modes.synchronizedOutputMode) return;

  const bufferBefore = state.terminal.buffer.active;
  if (bufferBefore.viewportY >= bufferBefore.baseY) {
    return;
  }

  const scrollPosBefore = bufferBefore.viewportY;

  setTimeout(() => {
    if ($activeSessionId.get() !== activeId) return;
    if (!state.opened || state.container.classList.contains('hidden')) return;
    if (state.reconnectFreezeOverlay) return;
    if (state.terminal.modes.synchronizedOutputMode) return;

    const scrollPosAfter = state.terminal.buffer.active.viewportY;
    const delta = Math.abs(scrollPosAfter - scrollPosBefore);
    if (delta > 50) {
      state.terminal.scrollToLine(scrollPosBefore);
    }
  }, 50);
}

function setupVisibilityChangeHandler(): void {
  document.addEventListener('visibilitychange', () => {
    reportBrowserActivity();

    if (document.visibilityState === 'visible') {
      // Reconnect WebSockets if they were dropped while in background
      // Buffer refresh is handled by muxChannel's reconnect handler if needed
      if (!$stateWsConnected.get()) {
        connectStateWebSocket();
      }
      if (!$muxWsConnected.get()) {
        connectMuxWebSocket();
      }

      // Refocus active terminal when page becomes visible
      focusActiveTerminal();

      // Claude Code scrollback glitch protection
      applyScrollbackProtection();
    }
  });

  // Also protect against focus from clicking into the browser window
  window.addEventListener('focus', () => {
    reportBrowserActivity(true);
    applyScrollbackProtection();
  });

  window.addEventListener('blur', () => {
    reportBrowserActivity(false);
  });

  window.addEventListener('pagehide', () => {
    reportBrowserActivity(false);
  });
}

// =============================================================================
// Session Management
// =============================================================================

async function resolveNewSessionDimensions(): Promise<{ cols: number; rows: number }> {
  return resolveLaunchDimensions($currentSettings.get(), 'launcher');
}

function createPendingSession(cols: number, rows: number): string {
  const tempId = 'pending-' + crypto.randomUUID();
  const tempSession: Session = {
    id: tempId,
    pid: 0,
    createdAt: new Date().toISOString(),
    isRunning: false,
    exitCode: null,
    name: '',
    terminalTitle: '',
    topic: null,
    currentDirectory: '',
    foregroundPid: null,
    foregroundName: null,
    foregroundCommandLine: null,
    foregroundDisplayName: null,
    foregroundProcessIdentity: null,
    shellType: 'Loading...',
    cols,
    rows,
    manuallyNamed: false,
    supervisor: {
      state: 'unknown',
      profile: 'unknown',
      needsAttention: false,
      attentionReason: null,
      attentionScore: 0,
      lastInputAt: null,
      lastOutputAt: null,
      lastBellAt: null,
      currentHeat: 0,
    },
    order: Date.now(),
    parentSessionId: null,
    bookmarkId: null,
    spaceId: null,
    workspacePath: null,
    surface: null,
    isAdHoc: true,
    agentControlled: false,
    appServerControlOnly: false,
    profileHint: null,
    appServerControlResumeThreadId: null,
    hasAppServerControlHistory: false,
    agentAttachPoint: null,
  };

  setSession(tempSession);
  pendingSessions.add(tempId);
  return tempId;
}

function clearPendingSession(tempId: string): void {
  pendingSessions.delete(tempId);
  removeSession(tempId);
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

async function spawnFromHistory(
  entry: LaunchEntry,
  machineId: string | null = null,
): Promise<void> {
  const { cols, rows } = await resolveLaunchDimensions($currentSettings.get(), 'history');

  closeSidebar();

  if (machineId) {
    if (isAppServerControlHistoryEntry(entry)) {
      void showAlert(t('sessionLauncher.remoteTerminalOnly'), {
        title: t('sessionLauncher.createFailed'),
      });
      return;
    }

    createRemoteSession(machineId, {
      cols,
      rows,
      shell: entry.shellType || null,
      workingDirectory: entry.workingDirectory || null,
    })
      .then(async (session) => {
        await refreshHubState();
        const compositeId = toHubCompositeId(machineId, session.id);
        newlyCreatedSessions.add(compositeId);
        selectSession(compositeId);
      })
      .catch((e: unknown) => {
        log.error(() => `Failed to spawn remote recent: ${String(e)}`);
        void showAlert(getSessionLaunchErrorMessage(e), {
          title: t('sessionLauncher.createFailed'),
        });
      });
    return;
  }

  if (isAppServerControlHistoryEntry(entry)) {
    const profile = normalizeHistoryAppServerControlProfile(entry.profile);
    if (profile) {
      bootstrapWorker({
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
      })
        .then(({ data }) => {
          const session = data?.session;
          if (!session) {
            return;
          }

          activateNewAppServerControlSession(session);
          attachBookmarkToSession(session.id, entry.id, entry.label ?? null, entry.notes ?? null);
        })
        .catch((e: unknown) => {
          log.error(() => `Failed to spawn appServerControl bookmark: ${String(e)}`);
          showSessionLaunchFailure(e);
        });
      return;
    }
  }

  apiCreateSession({
    cols,
    rows,
    shell: entry.shellType || null,
    workingDirectory: entry.workingDirectory || null,
  })
    .then(({ data }) => {
      if (!data) return;
      setSession(data);
      newlyCreatedSessions.add(data.id);
      selectSession(data.id);
      attachBookmarkToSession(data.id, entry.id, entry.label ?? null, entry.notes ?? null);

      if (entry.commandLine) {
        const replayCmd = buildReplayCommand(entry.executable, entry.commandLine);
        setTimeout(() => {
          sendInput(data.id, replayCmd + '\r');
        }, 100);
      }
    })
    .catch((e: unknown) => {
      log.error(() => `Failed to spawn from history: ${String(e)}`);
      showSessionLaunchFailure(e);
    });
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

function buildAppServerControlHistoryDedupeKey(
  profile: 'codex' | 'claude',
  workingDirectory: string,
): string {
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

function showBellNotification(sessionId: string): void {
  const settings = $currentSettings.get();
  if (!settings) return;
  if (bellNotificationsSuppressed) return;

  const bellStyle = settings.bellStyle;
  const session = getSession(sessionId);
  const title = session ? getSessionDisplayName(session) : 'Terminal';

  if (
    (bellStyle === 'notification' || bellStyle === 'both') &&
    Notification.permission === 'granted' &&
    document.hidden
  ) {
    // Close existing notification for this session (deduplication)
    const existing = activeNotifications.get(sessionId);
    if (existing) {
      existing.close();
    }

    const notification = new Notification(title, {
      body: 'Needs your attention',
      icon: '/favicon.ico',
      tag: `midterm-bell-${sessionId}`,
    });

    activeNotifications.set(sessionId, notification);

    notification.onclick = () => {
      window.focus();
      notification.close();
      activeNotifications.delete(sessionId);
    };

    // Auto-close after 15 seconds
    setTimeout(() => {
      notification.close();
      activeNotifications.delete(sessionId);
    }, 15000);
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

// =============================================================================
// PWA Install
// =============================================================================

interface BeforeInstallPromptEvent extends Event {
  prompt(): Promise<void>;
}

interface NavigatorWithStandalone extends Navigator {
  standalone?: boolean;
}

function initPwaInstall(): void {
  let deferredPrompt: BeforeInstallPromptEvent | null = null;
  const row = document.getElementById('pwa-install-row');
  const btn = document.getElementById('btn-install-pwa') as HTMLButtonElement | null;
  if (!row || !btn) return;

  const rowEl = row;
  const btnEl = btn;
  const isIos = isIosInstallableDevice();

  function showRow(): void {
    rowEl.classList.remove('hidden');
  }

  function hideRow(): void {
    rowEl.classList.add('hidden');
  }

  function setButtonLabel(key: string): void {
    btnEl.dataset.i18n = key;
    btnEl.textContent = t(key);
  }

  if (isRunningAsInstalledPwa()) {
    hideRow();
    return;
  }

  if (isIos) {
    showRow();
    setButtonLabel('settings.behavior.showInstallSteps');
  }

  window.addEventListener('beforeinstallprompt', (e) => {
    e.preventDefault();
    deferredPrompt = e as BeforeInstallPromptEvent;
    setButtonLabel('settings.behavior.install');
    showRow();
  });

  btn.addEventListener('click', () => {
    if (deferredPrompt) {
      void deferredPrompt.prompt().then(() => {
        deferredPrompt = null;
        hideRow();
      });
      return;
    }

    if (!isIos) return;

    void showAlert(t('settings.behavior.installIosMessage'), {
      title: t('settings.behavior.installIosTitle'),
    });
  });

  window.addEventListener('appinstalled', () => {
    hideRow();
    deferredPrompt = null;
    syncAppModeClasses();
  });
}

function isIosInstallableDevice(): boolean {
  const ua = navigator.userAgent.toLowerCase();
  return (
    /iphone|ipad|ipod/.test(ua) ||
    // eslint-disable-next-line @typescript-eslint/no-deprecated
    (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1)
  );
}

function isRunningAsInstalledPwa(): boolean {
  const standaloneNavigator = navigator as NavigatorWithStandalone;
  return (
    window.matchMedia('(display-mode: standalone)').matches ||
    window.matchMedia('(display-mode: fullscreen)').matches ||
    window.matchMedia('(display-mode: window-controls-overlay)').matches ||
    standaloneNavigator.standalone === true
  );
}

function syncAppModeClasses(): void {
  document.body.classList.toggle('installed-pwa', isRunningAsInstalledPwa());
  document.body.classList.toggle('ios-installable-device', isIosInstallableDevice());
}

function getActiveSessionTabBar(): HTMLDivElement | null {
  const activeSessionId = $activeSessionId.get();
  if (!activeSessionId || !dom.terminalsArea) return null;

  const wrappers = dom.terminalsArea.querySelectorAll<HTMLDivElement>('.session-wrapper');
  for (const wrapper of wrappers) {
    if (wrapper.dataset.sessionId === activeSessionId) {
      return wrapper.querySelector<HTMLDivElement>('.session-tab-bar');
    }
  }
  return null;
}

function clickActiveSessionTabBarControl(selector: string): void {
  const tabBar = getActiveSessionTabBar();
  if (!tabBar) return;
  const control = tabBar.querySelector<HTMLButtonElement>(selector);
  control?.click();
}

// =============================================================================
// Event Binding
// =============================================================================

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
    clickActiveSessionTabBarControl('[data-action="web"]');
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
  bindClick('btn-bookmarks', () => {
    closeSpacesDropdown();
    toggleHistoryDropdown();
  });

  // Global keyboard shortcut: Alt+T to create new terminal
  document.addEventListener('keydown', (e) => {
    if (e.altKey && !e.ctrlKey && !e.shiftKey && !e.metaKey && e.key.toLowerCase() === 't') {
      e.preventDefault();
      void createSession();
    }
  });
}
