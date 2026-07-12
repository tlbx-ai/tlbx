/**
 * State Channel Module
 *
 * Manages the state WebSocket connection for real-time session list updates.
 * Handles automatic reconnection on disconnect.
 * Also provides bidirectional command channel for session CRUD operations.
 */

import type {
  BrowserSessionStatus,
  DockPosition,
  LayoutNode,
  ManagerBarQueueEntry,
  Session,
  UpdateInfo,
  WsCommand,
  WsCommandAction,
  WsCommandPayload,
  WsCommandResponse,
} from '../../types';
import { ReconnectController, createWsUrl, closeWebSocket } from '../../utils';
import { handleAuthenticatedWebSocketClose } from '../auth/sessionLifetime';
import { createLogger } from '../logging';
import { initializeFromSession } from '../process';
import { destroyTerminalForSession, createTerminalForSession } from '../terminal/manager';
import { destroySessionWrapper } from '../sessionTabs';
import { applyTerminalScaling } from '../terminal/scaling';
import { handleSessionClosed } from '../layout';
import { updateEmptyState, updateMobileTitle } from '../sidebar/sessionList';
import { renderUpdatePanel } from '../updating/checker';
import { getRememberedActiveSessionId } from '../updating/appShellState';
import { handleHiddenSessionClosed } from '../commands/commandsPanel';
import { closeOverlay } from '../commands/outputPanel';
import {
  detachPreview,
  dockBack,
  isDetachedOpenForSession,
  setDetachedPreviewViewport,
} from '../web/webDetach';
import { setViewportSize, openWebPreviewDock } from '../web/webDock';
import { setWebPreviewTarget } from '../web/webApi';
import {
  getSessionPreview,
  getSessionSelectedPreviewName,
  setSessionMode,
  setSessionSelectedPreviewName,
  upsertSessionPreview,
} from '../web/webSessionState';
import { syncActiveWebPreview } from '../web';
import { isEmbeddedWebPreviewContext } from '../web/webContext';
import { isSharedSessionRoute } from '../share';
import { checkVersionAndReload } from '../../utils/versionCheck';
import type { MobileDeviceAction } from '../web/mobileDeviceBridge';

interface TmuxDockMessage {
  type: 'tmux-dock';
  newSessionId: string;
  relativeToSessionId: string;
  position: string;
}

interface TmuxFocusMessage {
  type: 'tmux-focus';
  sessionId: string;
}

interface TmuxSwapMessage {
  type: 'tmux-swap';
  sessionIdA: string;
  sessionIdB: string;
}

interface MainBrowserStatusMessage {
  type: 'main-browser-status';
  isMain: boolean;
  showButton: boolean;
  browsers?: BrowserSessionStatus[];
}

interface BrowserUiMessage {
  type: 'browser-ui';
  command: string;
  width?: number;
  height?: number;
  url?: string;
  sessionId?: string;
  previewName?: string;
  activateSession?: boolean;
  deviceAction?: string;
  deviceProfile?: string;
}

interface LayoutStateMessage {
  revision?: number;
  root: LayoutNode | null;
  focusedSessionId: string | null;
}

interface StateUpdateMessage {
  type?: undefined;
  sessions?: { sessions: Session[] };
  update?: UpdateInfo | null;
  layout?: LayoutStateMessage | null;
  managerBarQueue?: ManagerBarQueueEntry[];
}

interface CommandResponseMessage {
  type: 'response';
  id: string;
  success: boolean;
  data?: unknown;
  error?: string;
}

type StateWsMessage =
  | TmuxDockMessage
  | TmuxFocusMessage
  | TmuxSwapMessage
  | MainBrowserStatusMessage
  | BrowserUiMessage
  | StateUpdateMessage
  | CommandResponseMessage;

const log = createLogger('state');
const stateReconnect = new ReconnectController();
import {
  stateWs,
  sessionTerminals,
  newlyCreatedSessions,
  hiddenSessionIds,
  setStateWs,
} from '../../state';

const COMMAND_TIMEOUT_MS = 30000;
const pendingCommands = new Map<
  string,
  {
    resolve: (data: unknown) => void;
    reject: (error: Error) => void;
    timeout: number;
  }
>();
import {
  $settingsOpen,
  $stateWsConnected,
  $activeSessionId,
  $browserSessions,
  $sessionList,
  $updateInfo,
  $isMainBrowser,
  $showMainBrowserButton,
  $webPreviewUrl,
  getSession,
  setSessions,
  setManagerBarQueue,
  getParentSessionId,
} from '../../stores';
import {
  restoreLayoutFromStorage,
  applyServerLayoutState,
  dockSession,
  isSessionInLayout,
  markLayoutPersistenceReady,
  swapLayoutSessions,
} from '../layout/layoutStore';

// Track if we've hydrated layout state yet (server snapshot or fallback restore).
let layoutHydrated = false;
let stateWsHasConnected = false;
let lastUpdateInfoSignature = '';

// Pending dock instructions for sessions that haven't appeared in state yet
interface PendingDock {
  targetSessionId: string;
  newSessionId: string;
  position: string;
}
const pendingDocks: PendingDock[] = [];

let selectSession: (
  sessionId: string,
  options?: { closeSettingsPanel?: boolean; focusTerminal?: boolean },
) => void = () => {};

export function setSelectSessionCallback(
  cb: (
    sessionId: string,
    options?: { closeSettingsPanel?: boolean; focusTerminal?: boolean },
  ) => void,
): void {
  selectSession = cb;
}

export function requestSelectSession(
  sessionId: string,
  options?: { closeSettingsPanel?: boolean; focusTerminal?: boolean },
): void {
  selectSession(sessionId, options);
}

function handleTmuxDockMessage(data: TmuxDockMessage): void {
  log.verbose(
    () =>
      `Tmux dock: ${data.newSessionId} relative to ${data.relativeToSessionId} at ${data.position}`,
  );
  if (!sessionTerminals.has(data.newSessionId)) {
    pendingDocks.push({
      targetSessionId: data.relativeToSessionId,
      newSessionId: data.newSessionId,
      position: data.position,
    });
    return;
  }

  dockSession(data.relativeToSessionId, data.newSessionId, data.position as DockPosition, true);
}

function shouldFocusTmuxSession(sessionId: string): boolean {
  const activeId = $activeSessionId.get();
  const activeParent = activeId ? getParentSessionId(activeId) : null;
  const focusParent = getParentSessionId(sessionId);
  const activeInLayout = activeId ? isSessionInLayout(activeId) : false;
  const focusInLayout = isSessionInLayout(sessionId);
  const sameLayoutGroup = activeInLayout && focusInLayout;

  return (
    !activeId ||
    activeId === sessionId ||
    activeId === focusParent ||
    activeParent === sessionId ||
    (activeParent !== null && activeParent === focusParent) ||
    sameLayoutGroup
  );
}

function handleTmuxFocusMessage(data: TmuxFocusMessage): void {
  log.verbose(() => `Tmux focus: ${data.sessionId}`);
  if (shouldFocusTmuxSession(data.sessionId) && isSessionInLayout(data.sessionId)) {
    selectSession(data.sessionId, { closeSettingsPanel: false });
  }
}

function handleStateSocketMessage(data: StateWsMessage): void {
  if (data.type === 'response') {
    handleCommandResponse(data);
    return;
  }

  if (data.type === 'tmux-dock') {
    handleTmuxDockMessage(data);
    return;
  }

  if (data.type === 'tmux-focus') {
    handleTmuxFocusMessage(data);
    return;
  }

  if (data.type === 'tmux-swap') {
    log.verbose(() => `Tmux swap: ${data.sessionIdA} <-> ${data.sessionIdB}`);
    swapLayoutSessions(data.sessionIdA, data.sessionIdB);
    return;
  }

  if (data.type === 'main-browser-status') {
    $isMainBrowser.set(data.isMain);
    $showMainBrowserButton.set(data.showButton);
    $browserSessions.set(data.browsers ?? []);
    return;
  }

  if (data.type === 'browser-ui') {
    void handleBrowserUiCommand(data);
    return;
  }

  const sessionList = data.sessions?.sessions ?? [];
  handleStateUpdate(sessionList, data.layout);
  if (data.managerBarQueue !== undefined) {
    setManagerBarQueue(data.managerBarQueue);
  }
  handleUpdateInfo(data.update ?? null);
}

/**
 * Connect to the state WebSocket for real-time session updates.
 * Automatically reconnects with exponential backoff on disconnect.
 */
export function connectStateWebSocket(): void {
  closeWebSocket(stateWs, setStateWs);

  const wsPath = isSharedSessionRoute() ? '/ws/share/state' : '/ws/state';
  const ws = new WebSocket(createWsUrl(wsPath));
  setStateWs(ws);

  ws.onopen = () => {
    stateReconnect.reset();
    const isReconnect = stateWsHasConnected;
    stateWsHasConnected = true;
    $stateWsConnected.set(true);
    reportBrowserActivity(getCurrentBrowserActivity(), true);
    if (isReconnect) {
      void checkVersionAndReload();
    }
  };

  ws.onmessage = (event) => {
    try {
      const data = JSON.parse(event.data as string) as StateWsMessage;
      handleStateSocketMessage(data);
    } catch (e: unknown) {
      const message = e instanceof Error ? e.message : String(e);
      log.error(() => `Error parsing state: ${message}`);
    }
  };

  ws.onclose = (event) => {
    $stateWsConnected.set(false);

    // Reject all pending commands immediately (don't wait for timeout)
    pendingCommands.forEach((cmd, id) => {
      clearTimeout(cmd.timeout);
      cmd.reject(new Error('Connection lost'));
      pendingCommands.delete(id);
    });

    if (handleAuthenticatedWebSocketClose(event)) {
      return;
    }

    scheduleStateReconnect();
  };

  ws.onerror = (e) => {
    log.error(() => `WebSocket error: ${e.type}`);
  };
}

function removeClosedSessions(validSessions: readonly (Session & { id: string })[]): void {
  const newIds = new Set(validSessions.map((s) => s.id));
  sessionTerminals.forEach((_, id) => {
    if (!newIds.has(id) && !hiddenSessionIds.has(id)) {
      handleSessionClosed(id);
      destroySessionWrapper(id);
      destroyTerminalForSession(id);
      newlyCreatedSessions.delete(id);
    }
  });

  for (const hiddenId of hiddenSessionIds) {
    if (!newIds.has(hiddenId)) {
      handleHiddenSessionClosed(hiddenId);
      closeOverlay(hiddenId);
    }
  }
}

function syncSessionTerminalState(session: Session & { id: string }): void {
  initializeFromSession(
    session.id,
    session.foregroundPid,
    session.foregroundName,
    session.foregroundCommandLine,
    session.currentDirectory,
    session.foregroundDisplayName,
    session.foregroundProcessIdentity,
  );

  const state = sessionTerminals.get(session.id);
  if (state && state.opened) {
    const dimensionsChanged =
      state.serverCols !== session.cols || state.serverRows !== session.rows;
    if (dimensionsChanged) {
      if ($isMainBrowser.get() && !state.container.classList.contains('hidden')) {
        return;
      }

      state.serverCols = session.cols;
      state.serverRows = session.rows;
      state.terminal.resize(session.cols, session.rows);
      applyTerminalScaling(session.id, state);
    }
    return;
  }

  if (state) {
    state.serverCols = session.cols;
    state.serverRows = session.rows;
    return;
  }

  if (!session.appServerControlOnly) {
    createTerminalForSession(session.id, session);
  }
}

function applyPendingDocks(): void {
  for (let i = pendingDocks.length - 1; i >= 0; i--) {
    const dock = pendingDocks[i];
    if (!dock) continue;
    if (sessionTerminals.has(dock.newSessionId)) {
      pendingDocks.splice(i, 1);
      dockSession(dock.targetSessionId, dock.newSessionId, dock.position as DockPosition, true);
    }
  }
}

function hydrateLayoutState(
  layoutState: LayoutStateMessage | null | undefined,
  sessionCount: number,
): void {
  if (layoutState !== undefined) {
    applyServerLayoutState(layoutState ?? null);
    if (!layoutHydrated) {
      layoutHydrated = true;
      markLayoutPersistenceReady();
    }
    return;
  }

  if (!layoutHydrated && sessionCount >= 2) {
    restoreLayoutFromStorage();
    layoutHydrated = true;
    markLayoutPersistenceReady();
  }
}

function syncActiveSessionSelection(): void {
  const isSettingsOpen = $settingsOpen.get();
  const activeId = $activeSessionId.get();
  const sessionList = $sessionList.get();
  const firstSession = sessionList[0];

  if (!activeId && firstSession?.id && !isSettingsOpen) {
    const rememberedActiveId = getRememberedActiveSessionId();
    const rememberedSession =
      rememberedActiveId !== null
        ? sessionList.find((session) => session.id === rememberedActiveId)
        : undefined;
    selectSession((rememberedSession ?? firstSession).id, { closeSettingsPanel: false });
  }

  if (activeId && !sessionList.find((s) => s.id === activeId)) {
    $activeSessionId.set(null);
    const nextSession = sessionList[0];
    if (nextSession?.id && !isSettingsOpen) {
      selectSession(nextSession.id, { closeSettingsPanel: false });
    }
  }
}

/**
 * Handle session list updates from server.
 * Removes terminals for deleted sessions, updates dimensions, and manages selection.
 * Creates terminals proactively for all sessions so they receive data in the background.
 */
export function handleStateUpdate(
  newSessions: Session[],
  layoutState?: LayoutStateMessage | null,
): void {
  const validSessions = newSessions.filter((s): s is Session & { id: string } => !!s.id);
  removeClosedSessions(validSessions);
  validSessions.forEach(syncSessionTerminalState);
  const sessionsChanged = setSessions(validSessions);
  if (sessionsChanged) {
    updateEmptyState();
  }
  applyPendingDocks();
  hydrateLayoutState(layoutState, newSessions.length);
  if (sessionsChanged) {
    syncActiveSessionSelection();
    updateMobileTitle();
  }
}

/**
 * Handle update info from server.
 * Updates the stored update info and renders the update panel.
 */
export function handleUpdateInfo(update: UpdateInfo | null): void {
  const signature = JSON.stringify(update ?? null);
  if (signature === lastUpdateInfoSignature) {
    return;
  }

  lastUpdateInfoSignature = signature;
  $updateInfo.set(update);
  renderUpdatePanel();
}

/**
 * Schedule state WebSocket reconnection.
 */
export function scheduleStateReconnect(): void {
  stateReconnect.schedule(connectStateWebSocket);
}

// =============================================================================
// WebSocket Command API
// =============================================================================

/**
 * Handle command response from server.
 */
function handleCommandResponse(response: WsCommandResponse): void {
  const pending = pendingCommands.get(response.id);
  if (!pending) {
    log.verbose(() => `Received response for unknown command: ${response.id}`);
    return;
  }

  clearTimeout(pending.timeout);
  pendingCommands.delete(response.id);

  if (response.success) {
    pending.resolve(response.data);
  } else {
    pending.reject(new Error(response.error ?? 'Command failed'));
  }
}

/**
 * Send a command to the server over the state WebSocket.
 * Returns a promise that resolves with the response data or rejects on error.
 */
export function sendCommand<T = unknown>(
  action: 'browser.claimMain' | 'browser.releaseMain',
): Promise<T>;
export function sendCommand<T = unknown>(
  action: 'browser.setActivity',
  payload: WsCommandPayload<'browser.setActivity'>,
): Promise<T>;
export function sendCommand<T = unknown>(
  action: 'session.rename',
  payload: WsCommandPayload<'session.rename'>,
): Promise<T>;
export function sendCommand<T = unknown>(
  action: 'session.reorder',
  payload: WsCommandPayload<'session.reorder'>,
): Promise<T>;
export async function sendCommand<T = unknown>(
  action: WsCommandAction,
  payload?:
    | WsCommandPayload<'session.rename'>
    | WsCommandPayload<'session.reorder'>
    | WsCommandPayload<'browser.setActivity'>,
): Promise<T> {
  const ws = stateWs;
  if (!ws || ws.readyState !== WebSocket.OPEN) {
    throw new Error('WebSocket not connected');
  }

  const id = crypto.randomUUID();
  let command: WsCommand;
  switch (action) {
    case 'browser.claimMain':
    case 'browser.releaseMain':
      command = {
        type: 'command',
        id,
        action,
      };
      break;
    case 'browser.setActivity':
      command = {
        type: 'command',
        id,
        action,
        payload: payload as WsCommandPayload<'browser.setActivity'>,
      };
      break;
    case 'session.rename':
      command = {
        type: 'command',
        id,
        action,
        payload: payload as WsCommandPayload<'session.rename'>,
      };
      break;
    case 'session.reorder':
      command = {
        type: 'command',
        id,
        action,
        payload: payload as WsCommandPayload<'session.reorder'>,
      };
      break;
  }

  return new Promise<T>((resolve, reject) => {
    const timeout = window.setTimeout(() => {
      pendingCommands.delete(id);
      reject(new Error(`Command timed out: ${action}`));
    }, COMMAND_TIMEOUT_MS);

    pendingCommands.set(id, {
      resolve: resolve as (data: unknown) => void,
      reject,
      timeout,
    });

    try {
      ws.send(JSON.stringify(command));
    } catch (e) {
      clearTimeout(timeout);
      pendingCommands.delete(id);
      reject(new Error(e instanceof Error ? e.message : String(e)));
    }
  });
}

/**
 * Handle browser UI commands from the server (detach, dock, viewport).
 */
async function handleBrowserUiCommand(msg: BrowserUiMessage): Promise<void> {
  if (isEmbeddedWebPreviewContext() && msg.command === 'detach') {
    log.verbose(() => `Ignoring browser detach command inside embedded preview`);
    return;
  }

  const reloadRequested = await checkVersionAndReload({ forceReloadOnMismatch: true });
  if (reloadRequested) {
    log.info(() => `Browser UI command deferred until frontend reload: ${msg.command}`);
    return;
  }

  switch (msg.command) {
    case 'detach':
      handleDetachBrowserUiCommand(msg);
      break;
    case 'dock':
      handleDockBrowserUiCommand(msg);
      break;
    case 'viewport':
      handleViewportBrowserUiCommand(msg);
      break;
    case 'open':
      handleOpenBrowserUiCommand(msg);
      break;
    case 'mobile-device':
      if (msg.deviceAction) {
        void import('../web/mobileDeviceController')
          .then(({ controlMobileDevice }) =>
            controlMobileDevice(
              msg.deviceAction as MobileDeviceAction,
              msg.sessionId,
              msg.previewName,
              msg.deviceProfile,
            ),
          )
          .catch((error: unknown) => {
            log.warn(() => `Mobile device command failed: ${String(error)}`);
          });
      }
      break;
    default:
      log.warn(() => `Unknown browser-ui command: ${msg.command}`);
  }
}

function handleDetachBrowserUiCommand(msg: BrowserUiMessage): void {
  const target = resolveBrowserUiTarget(msg);
  if (!target) {
    return;
  }

  setSessionMode(target.sessionId, target.previewName, 'detached');
  void detachPreview(target.sessionId, target.previewName);
}

function handleDockBrowserUiCommand(msg: BrowserUiMessage): void {
  const target = resolveBrowserUiTarget(msg);
  if (!target) {
    return;
  }

  setSessionMode(target.sessionId, target.previewName, 'docked');
  dockBack(target.sessionId, target.previewName);
  if ($activeSessionId.get() === target.sessionId) {
    void syncActiveWebPreview();
  }
}

function handleViewportBrowserUiCommand(msg: BrowserUiMessage): void {
  const target = resolveBrowserUiTarget(msg);
  if (!target) {
    return;
  }

  if (
    applyDetachedPreviewViewport(
      target.sessionId,
      target.previewName,
      msg.width ?? 0,
      msg.height ?? 0,
    )
  ) {
    return;
  }

  setSessionMode(target.sessionId, target.previewName, 'docked');
  if ($activeSessionId.get() !== target.sessionId) {
    return;
  }

  openWebPreviewDock();
  void syncActiveWebPreview().finally(() => {
    setViewportSize(msg.width ?? 0, msg.height ?? 0);
  });
}

function applyDetachedPreviewViewport(
  sessionId: string,
  previewName: string,
  width: number,
  height: number,
): boolean {
  const preview = getSessionPreview(sessionId, previewName);
  return (
    preview?.mode === 'detached' &&
    isDetachedOpenForSession(sessionId, previewName) &&
    setDetachedPreviewViewport(sessionId, previewName, width, height)
  );
}

function handleOpenBrowserUiCommand(msg: BrowserUiMessage): void {
  const target = resolveBrowserUiTarget(msg);
  if (!target || !msg.url) {
    return;
  }

  setSessionMode(target.sessionId, target.previewName, 'docked');
  void handleBrowserOpen(
    target.sessionId,
    target.previewName,
    msg.url,
    msg.activateSession === true,
  );
}

function resolveBrowserUiTarget(
  msg: BrowserUiMessage,
): { sessionId: string; previewName: string } | null {
  const sessionId = msg.sessionId ?? $activeSessionId.get();
  if (!sessionId) {
    return null;
  }

  const previewName = setSessionSelectedPreviewName(
    sessionId,
    msg.previewName ?? getSessionSelectedPreviewName(sessionId),
  );

  return { sessionId, previewName };
}

async function handleBrowserOpen(
  sessionId: string,
  previewName: string,
  url: string,
  activateSession = false,
): Promise<void> {
  const result = await setWebPreviewTarget(sessionId, previewName, url);
  if (!result?.active) {
    return;
  }

  upsertSessionPreview(result);
  setSessionSelectedPreviewName(sessionId, previewName);
  setSessionMode(sessionId, previewName, 'docked');
  if (activateSession && $activeSessionId.get() !== sessionId) {
    selectSession(sessionId, { closeSettingsPanel: false });
  }
  if ($activeSessionId.get() !== sessionId) {
    return;
  }
  $webPreviewUrl.set(url);
  openWebPreviewDock();
  await syncActiveWebPreview();
}

/**
 * Check if the state WebSocket is connected and ready for commands.
 */
export function isStateConnected(): boolean {
  return stateWs !== null && stateWs.readyState === WebSocket.OPEN;
}

/**
 * Persist session order to server.
 * Fire-and-forget - failures are logged but not thrown.
 */
export function persistSessionOrder(sessionIds: string[]): void {
  if (!isStateConnected()) return;

  sendCommand('session.reorder', { sessionIds }).catch((e: unknown) => {
    log.warn(() => `Failed to persist session order: ${String(e)}`);
  });
}

/**
 * Claim main browser status from server.
 * Fire-and-forget - server will push status to all connections.
 */
export function claimMainBrowser(): void {
  if (!isStateConnected()) return;
  sendCommand('browser.claimMain').catch((e: unknown) => {
    log.warn(() => `Failed to claim main browser: ${String(e)}`);
  });
}

function getCurrentBrowserActivity(): boolean {
  if (typeof document === 'undefined') {
    return true;
  }

  const visible = document.visibilityState === 'visible' && !document.hidden;
  const focused = typeof document.hasFocus !== 'function' || document.hasFocus();
  return visible && focused;
}

function getCurrentActiveSurface(): string | null {
  const sessionId = $activeSessionId.get();
  if (!sessionId) {
    return null;
  }

  const session = getSession(sessionId);
  if (!session) {
    return null;
  }

  if (session.appServerControlOnly || session.surface === 'codex' || session.surface === 'claude') {
    return session.profileHint ? `agent:${session.profileHint}` : 'agent';
  }

  return session.surface ?? 'terminal';
}

let lastReportedBrowserActivity: boolean | undefined;

export function reportBrowserActivity(
  isActive: boolean = getCurrentBrowserActivity(),
  force: boolean = false,
): void {
  if (isSharedSessionRoute() || !isStateConnected()) return;
  if (!force && lastReportedBrowserActivity === isActive) return;

  sendCommand('browser.setActivity', {
    isActive,
    activeSessionId: $activeSessionId.get(),
    activeSurface: getCurrentActiveSurface(),
  })
    .then(() => {
      lastReportedBrowserActivity = isActive;
    })
    .catch((e: unknown) => {
      log.warn(() => `Failed to report browser activity: ${String(e)}`);
    });
}

/**
 * Release main browser status to server.
 * Fire-and-forget - server will push status to all connections.
 */
export function releaseMainBrowser(): void {
  if (!isStateConnected()) return;
  sendCommand('browser.releaseMain').catch((e: unknown) => {
    log.warn(() => `Failed to release main browser: ${String(e)}`);
  });
}

export function resetStateChannelRuntimeForTests(): void {
  pendingCommands.forEach((cmd) => {
    clearTimeout(cmd.timeout);
  });
  pendingCommands.clear();
  pendingDocks.length = 0;
  layoutHydrated = false;
  stateWsHasConnected = false;
  lastUpdateInfoSignature = '';
  selectSession = () => {};
  lastReportedBrowserActivity = undefined;
  closeWebSocket(stateWs, setStateWs);
}
