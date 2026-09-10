import { recordTextActivity } from '../sidebar/heatIndicator';
/**
 * State Channel Module
 *
 * Manages the state WebSocket connection for real-time session list updates.
 * Handles automatic reconnection on disconnect.
 * Also provides bidirectional command channel for session CRUD operations.
 */

import type {
  BrowserSessionStatus,
  TerminalSizeControlCommandResult,
  TerminalSizeControlStatus,
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
import { destroySessionWrapper, getActiveTab } from '../sessionTabs';
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
import { getWebPreviewTarget } from '../web/webApi';
import { applySessionViewportToFrame } from '../web/webViewport';
import {
  getSessionPreview,
  getSessionSelectedPreviewName,
  setSessionMode,
  setSessionSelectedPreviewName,
  setSessionViewport,
  upsertSessionPreview,
} from '../web/webSessionState';
import { closePreviewFromServer, syncActiveWebPreview, syncBackgroundWebPreview } from '../web';
import { isEmbeddedWebPreviewContext } from '../web/webContext';
import { isSharedSessionRoute } from '../share';
import { checkVersionAndReload } from '../../utils/versionCheck';
import type { MobileDeviceAction } from '../web/mobileDeviceBridge';
import type { TerminalNotificationSignal } from '../terminal/terminalNotifications';

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
  revision?: number;
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
  requestId?: string;
  targetRevision?: number;
  deltaY?: number;
  steps?: number;
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
  terminalSizeControls?: TerminalSizeControlStatus[];
}

interface CommandResponseMessage {
  type: 'response';
  id: string;
  success: boolean;
  data?: unknown;
  error?: string;
}

interface TerminalNotificationMessage extends TerminalNotificationSignal {
  type: 'terminal-notification';
  sessionId: string;
}

interface TerminalTextActivityMessage {
  type: 'terminal-text-activity';
  sessionId: string;
  lastTextOutputAt: string | null;
  textActivityAgeMs: number | null;
}
type DirectStateMessage =
  BrowserUiMessage | TerminalNotificationMessage | TerminalTextActivityMessage;

type StateWsMessage =
  | TmuxDockMessage
  | TmuxFocusMessage
  | TmuxSwapMessage
  | MainBrowserStatusMessage
  | BrowserUiMessage
  | TerminalNotificationMessage
  | TerminalTextActivityMessage
  | StateUpdateMessage
  | CommandResponseMessage;

const log = createLogger('state');
const stateReconnect = new ReconnectController();
import {
  stateWs,
  sessionTerminals,
  newlyCreatedSessions,
  hiddenSessionIds,
  pendingSessions,
  setStateWs,
} from '../../state';

const COMMAND_TIMEOUT_MS = 30000;
let lastMainBrowserRevision = -1;
const pendingCommands = new Map<
  string,
  {
    resolve: (data: unknown) => void;
    reject: (error: Error) => void;
    timeout: number;
  }
>();
const browserUiCommandQueues = new Map<string, Promise<void>>();

function rejectPendingCommands(reason: string): void {
  pendingCommands.forEach((command, id) => {
    clearTimeout(command.timeout);
    command.reject(new Error(reason));
    pendingCommands.delete(id);
  });
}
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
  getTerminalSizeControl,
  getSession,
  setTerminalSizeControl,
  setTerminalSizeControls,
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
import { isHubSessionId } from '../hub/runtime';
import {
  requestHubTerminalSizeControl,
  resizeHubTerminalWithControl,
} from '../hub/sizeControlChannel';

// Track if we've hydrated layout state yet (server snapshot or fallback restore).
let layoutHydrated = false;
let stateWsHasConnected = false;
let lastUpdateInfoSignature = '';
let initialStateHydrated = false;
let handleInitialStateHydrated: (() => void) | null = null;

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

let handleTerminalNotification: (
  sessionId: string,
  signal: TerminalNotificationSignal,
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

export function setTerminalNotificationCallback(
  callback: (sessionId: string, signal: TerminalNotificationSignal) => void,
): void {
  handleTerminalNotification = callback;
}

export function setInitialStateHydratedCallback(callback: (() => void) | null): void {
  handleInitialStateHydrated = callback;
  if (callback && initialStateHydrated) {
    callback();
  }
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

function handleDirectStateMessage(data: StateWsMessage): data is DirectStateMessage {
  if (data.type === 'browser-ui') {
    enqueueBrowserUiCommand(data);
    return true;
  }

  if (data.type === 'terminal-text-activity') {
    recordTextActivity(data.sessionId, data.lastTextOutputAt, data.textActivityAgeMs);
    return true;
  }
  if (data.type === 'terminal-notification') {
    handleTerminalNotification(data.sessionId, {
      protocol: data.protocol,
      ...(data.title ? { title: data.title } : {}),
      ...(data.body ? { body: data.body } : {}),
      ...(data.force ? { force: true } : {}),
      ...(data.priority ? { priority: data.priority } : {}),
      ...(data.nativeHandled ? { nativeHandled: true } : {}),
    });
    return true;
  }

  return false;
}

function enqueueBrowserUiCommand(msg: BrowserUiMessage): void {
  const key = `${msg.sessionId ?? 'active'}::${msg.previewName ?? 'default'}`;
  const previous = browserUiCommandQueues.get(key) ?? Promise.resolve();
  const current = previous.catch(() => undefined).then(() => handleBrowserUiCommand(msg));
  browserUiCommandQueues.set(key, current);
  void current.finally(() => {
    if (browserUiCommandQueues.get(key) === current) {
      browserUiCommandQueues.delete(key);
    }
  });
}

function handleMainBrowserStatus(data: MainBrowserStatusMessage): void {
  if (data.revision !== undefined && data.revision < lastMainBrowserRevision) {
    return;
  }
  if (data.revision !== undefined) {
    lastMainBrowserRevision = data.revision;
  }
  $isMainBrowser.set(data.isMain);
  $showMainBrowserButton.set(data.showButton);
  $browserSessions.set(data.browsers ?? []);
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
    handleMainBrowserStatus(data);
    return;
  }

  if (handleDirectStateMessage(data)) {
    return;
  }

  const sessionList = data.sessions?.sessions ?? [];
  if (data.terminalSizeControls !== undefined) {
    setTerminalSizeControls(data.terminalSizeControls);
  }
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
  stateReconnect.cancel();
  rejectPendingCommands('Connection replaced');
  closeWebSocket(stateWs, setStateWs);
  $stateWsConnected.set(false);
  lastMainBrowserRevision = -1;

  const wsPath = isSharedSessionRoute() ? '/ws/share/state' : '/ws/state';
  const ws = new WebSocket(createWsUrl(wsPath));
  setStateWs(ws);

  ws.onopen = () => {
    if (stateWs !== ws) return;
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
    if (stateWs !== ws) return;
    try {
      const data = JSON.parse(event.data as string) as StateWsMessage;
      handleStateSocketMessage(data);
    } catch (e: unknown) {
      const message = e instanceof Error ? e.message : String(e);
      log.error(() => `Error parsing state: ${message}`);
    }
  };

  ws.onclose = (event) => {
    if (stateWs !== ws) return;
    $stateWsConnected.set(false);

    // Reject all pending commands immediately (don't wait for timeout)
    rejectPendingCommands('Connection lost');

    if (handleAuthenticatedWebSocketClose(event)) {
      return;
    }

    scheduleStateReconnect();
  };

  ws.onerror = (e) => {
    if (stateWs !== ws) return;
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
    const bookmarkedSession = sessionList.find((session) => !!session.bookmarkId?.trim());
    const terminalSession = sessionList.find((session) => !session.appServerControlOnly);
    selectSession((rememberedSession ?? bookmarkedSession ?? terminalSession ?? firstSession).id, {
      closeSettingsPanel: false,
    });
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
  const serverSessionIds = new Set(newSessions.map((session) => session.id));
  const optimisticSessions = [...pendingSessions]
    .filter((sessionId) => !serverSessionIds.has(sessionId))
    .map((sessionId) => getSession(sessionId))
    .filter((session): session is Session => session !== undefined);
  const validSessions = [...newSessions, ...optimisticSessions].filter(
    (s): s is Session & { id: string } => !!s.id,
  );
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
  if (!initialStateHydrated) {
    initialStateHydrated = true;
    handleInitialStateHydrated?.();
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
  action: 'terminal.requestSizeControl',
  payload: WsCommandPayload<'terminal.requestSizeControl'>,
): Promise<T>;
export function sendCommand<T = unknown>(
  action: 'terminal.resize',
  payload: WsCommandPayload<'terminal.resize'>,
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
    | WsCommandPayload<'browser.setActivity'>
    | WsCommandPayload<'terminal.requestSizeControl'>
    | WsCommandPayload<'terminal.resize'>,
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
    case 'terminal.requestSizeControl':
      command = {
        type: 'command',
        id,
        action,
        payload: payload as WsCommandPayload<'terminal.requestSizeControl'>,
      };
      break;
    case 'terminal.resize':
      command = {
        type: 'command',
        id,
        action,
        payload: payload as WsCommandPayload<'terminal.resize'>,
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
    await reportBrowserUiCommandResult(
      msg,
      false,
      'Detach must be handled by the top-level tlbx window.',
    );
    return;
  }

  const reloadRequested = await checkVersionAndReload({ forceReloadOnMismatch: true });
  if (reloadRequested) {
    log.info(
      () => `Browser UI command rejected because a frontend reload is required: ${msg.command}`,
    );
    await reportBrowserUiCommandResult(
      msg,
      false,
      'The tlbx frontend is reloading to match the server. Retry the browser command.',
    );
    return;
  }

  try {
    let result: { success: boolean; error?: string } = { success: true };
    switch (msg.command) {
      case 'detach':
        result = await handleDetachBrowserUiCommand(msg);
        break;
      case 'dock':
        result = handleDockBrowserUiCommand(msg);
        break;
      case 'viewport':
        result = await handleViewportBrowserUiCommand(msg);
        break;
      case 'open':
        result = await handleOpenBrowserUiCommand(msg);
        break;
      case 'close':
        if (msg.sessionId) {
          await closePreviewFromServer(msg.sessionId, msg.previewName);
        }
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
      case 'agent-wheel':
        await handleAgentWheelBrowserUiCommand(msg);
        return;
      default:
        result = { success: false, error: `Unknown browser-ui command: ${msg.command}` };
        log.warn(() => result.error ?? 'Unknown browser-ui command');
    }

    await reportBrowserUiCommandResult(msg, result.success, result.error);
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    log.warn(() => `Browser UI command failed: ${message}`);
    await reportBrowserUiCommandResult(msg, false, message);
  }
}

async function reportBrowserUiCommandResult(
  msg: BrowserUiMessage,
  success: boolean,
  error?: string,
): Promise<void> {
  if (!msg.requestId) {
    return;
  }

  const response = await fetch('/api/browser/ui-result', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ requestId: msg.requestId, command: msg.command, success, error }),
  });
  if (!response.ok) {
    log.warn(() => `Browser UI result delivery failed: HTTP ${response.status}`);
  }
}

async function handleAgentWheelBrowserUiCommand(msg: BrowserUiMessage): Promise<void> {
  if (!msg.requestId || !msg.sessionId) {
    return;
  }

  let result: unknown;
  try {
    const { wheelAgentHistory } = await import('../agentView');
    result = await wheelAgentHistory({
      requestId: msg.requestId,
      sessionId: msg.sessionId,
      deltaY: msg.deltaY ?? 120,
      steps: msg.steps ?? 1,
    });
  } catch (error) {
    result = {
      requestId: msg.requestId,
      success: false,
      error: error instanceof Error ? error.message : String(error),
      sessionId: msg.sessionId,
      cancelledSteps: 0,
      samples: [],
    };
  }

  const response = await fetch('/api/browser/agent-wheel/result', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(result),
  });
  if (!response.ok) {
    log.warn(() => `ACP wheel result delivery failed: HTTP ${response.status}`);
  }
}

async function handleDetachBrowserUiCommand(
  msg: BrowserUiMessage,
): Promise<{ success: boolean; error?: string }> {
  const target = resolveBrowserUiTarget(msg);
  if (!target) {
    return { success: false, error: 'No browser preview target could be resolved.' };
  }

  return detachPreview(target.sessionId, target.previewName, { suppressFocus: true });
}

function handleDockBrowserUiCommand(msg: BrowserUiMessage): { success: boolean; error?: string } {
  const target = resolveBrowserUiTarget(msg);
  if (!target) {
    return { success: false, error: 'No browser preview target could be resolved.' };
  }

  setSessionMode(target.sessionId, target.previewName, 'docked');
  dockBack(target.sessionId, target.previewName);
  if ($activeSessionId.get() === target.sessionId) {
    void syncActiveWebPreview();
  }
  return { success: true };
}

async function handleViewportBrowserUiCommand(
  msg: BrowserUiMessage,
): Promise<{ success: boolean; error?: string }> {
  const target = resolveBrowserUiTarget(msg);
  if (!target) {
    return { success: false, error: 'No browser preview target could be resolved.' };
  }

  const width = msg.width ?? 0;
  const height = msg.height ?? 0;
  setSessionViewport(target.sessionId, target.previewName, width, height);

  if (applyDetachedPreviewViewport(target.sessionId, target.previewName, width, height)) {
    return { success: true };
  }

  setSessionMode(target.sessionId, target.previewName, 'docked');
  if ($activeSessionId.get() !== target.sessionId) {
    await syncBackgroundWebPreview(target.sessionId, target.previewName);
    applySessionViewportToFrame(target.sessionId, target.previewName);
    return { success: true };
  }

  openWebPreviewDock();
  await syncActiveWebPreview();
  setViewportSize(width, height);
  return { success: true };
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

async function handleOpenBrowserUiCommand(
  msg: BrowserUiMessage,
): Promise<{ success: boolean; error?: string }> {
  const target = resolveBrowserUiTarget(msg);
  if (!target || !msg.url) {
    return { success: false, error: 'The browser open command is missing its target or URL.' };
  }

  return handleBrowserOpen(
    target.sessionId,
    target.previewName,
    msg.activateSession === true,
    msg.targetRevision,
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
  activateSession = false,
  expectedTargetRevision?: number,
): Promise<{ success: boolean; error?: string }> {
  const result = await getWebPreviewTarget(sessionId, previewName);
  if (!result?.active) {
    return {
      success: false,
      error: 'The browser target was closed before the open command completed.',
    };
  }
  if (expectedTargetRevision !== undefined && result.targetRevision !== expectedTargetRevision) {
    return {
      success: false,
      error: 'A newer browser target replaced this open command before it completed.',
    };
  }

  upsertSessionPreview(result);
  setSessionSelectedPreviewName(sessionId, previewName);
  setSessionMode(sessionId, previewName, 'docked');
  if (activateSession && $activeSessionId.get() !== sessionId) {
    selectSession(sessionId, { closeSettingsPanel: false });
  }
  if ($activeSessionId.get() !== sessionId) {
    await syncBackgroundWebPreview(sessionId, previewName);
    return { success: true };
  }
  $webPreviewUrl.set(result.url ?? '');
  openWebPreviewDock();
  await syncActiveWebPreview();
  return { success: true };
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

const terminalInteractionReportAt = new Map<string, number>();
const OWNER_INTERACTION_REPORT_INTERVAL_MS = 15000;
const FOLLOWER_INTERACTION_REPORT_INTERVAL_MS = 1000;

function applyTerminalSizeControlResult(result: TerminalSizeControlCommandResult): void {
  setTerminalSizeControl(result.status);
}

export async function requestTerminalSizeControl(
  sessionId: string,
  force: boolean,
  expectedEpoch?: number,
): Promise<TerminalSizeControlCommandResult> {
  if (isHubSessionId(sessionId)) {
    return requestHubTerminalSizeControl(sessionId, force, expectedEpoch);
  }

  const result = await sendCommand<TerminalSizeControlCommandResult>(
    'terminal.requestSizeControl',
    { sessionId, force, ...(expectedEpoch === undefined ? {} : { expectedEpoch }) },
  );
  applyTerminalSizeControlResult(result);
  return result;
}

export function reportTerminalSizeInteraction(sessionId: string): void {
  if (!sessionId || isSharedSessionRoute() || !isStateConnected()) return;
  const now = performance.now();
  const status = getTerminalSizeControl(sessionId);
  const interval = status?.isOwner
    ? OWNER_INTERACTION_REPORT_INTERVAL_MS
    : FOLLOWER_INTERACTION_REPORT_INTERVAL_MS;
  const last = terminalInteractionReportAt.get(sessionId) ?? Number.NEGATIVE_INFINITY;
  if (now - last < interval) return;
  terminalInteractionReportAt.set(sessionId, now);

  requestTerminalSizeControl(sessionId, false).catch((e: unknown) => {
    terminalInteractionReportAt.delete(sessionId);
    log.warn(() => `Failed to report terminal size activity: ${String(e)}`);
  });
}

export async function resizeTerminalWithControl(
  sessionId: string,
  cols: number,
  rows: number,
  expectedEpoch: number,
): Promise<TerminalSizeControlCommandResult> {
  if (isHubSessionId(sessionId)) {
    return resizeHubTerminalWithControl(sessionId, cols, rows, expectedEpoch);
  }

  const result = await sendCommand<TerminalSizeControlCommandResult>('terminal.resize', {
    sessionId,
    cols,
    rows,
    expectedEpoch,
  });
  applyTerminalSizeControlResult(result);
  return result;
}

function getCurrentBrowserActivity(): boolean {
  if (typeof document === 'undefined') {
    return true;
  }

  const visible = document.visibilityState === 'visible' && !document.hidden;
  const focused = typeof document.hasFocus !== 'function' || document.hasFocus();
  return visible && focused;
}

function getCurrentBrowserVisibility(): boolean {
  if (typeof document === 'undefined') {
    return true;
  }

  return document.visibilityState === 'visible' && !document.hidden;
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

  const activeTab = getActiveTab(sessionId);
  if (activeTab === 'files') {
    return 'files';
  }

  if (
    activeTab === 'agent' ||
    session.appServerControlOnly ||
    session.surface === 'codex' ||
    session.surface === 'claude'
  ) {
    return session.profileHint ? `agent:${session.profileHint}` : 'agent';
  }

  return session.surface ?? 'terminal';
}

let lastReportedBrowserActivity:
  | {
      isActive: boolean;
      isVisible: boolean;
      activeSessionId: string | null;
      activeSurface: string | null;
    }
  | undefined;

export function reportBrowserActivity(
  isActive: boolean = getCurrentBrowserActivity(),
  force: boolean = false,
): void {
  if (isSharedSessionRoute() || !isStateConnected()) return;
  const report = {
    isActive,
    isVisible: getCurrentBrowserVisibility(),
    activeSessionId: $activeSessionId.get(),
    activeSurface: getCurrentActiveSurface(),
  };
  if (
    !force &&
    lastReportedBrowserActivity?.isActive === report.isActive &&
    lastReportedBrowserActivity.isVisible === report.isVisible &&
    lastReportedBrowserActivity.activeSessionId === report.activeSessionId &&
    lastReportedBrowserActivity.activeSurface === report.activeSurface
  ) {
    return;
  }

  const previousReport = lastReportedBrowserActivity;
  lastReportedBrowserActivity = report;

  sendCommand('browser.setActivity', {
    ...report,
    activeSessionId: report.activeSessionId,
    activeSurface: report.activeSurface,
  }).catch((e: unknown) => {
    if (lastReportedBrowserActivity === report) {
      lastReportedBrowserActivity = previousReport;
    }
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
  browserUiCommandQueues.clear();
  pendingDocks.length = 0;
  layoutHydrated = false;
  stateWsHasConnected = false;
  lastMainBrowserRevision = -1;
  lastUpdateInfoSignature = '';
  initialStateHydrated = false;
  handleInitialStateHydrated = null;
  selectSession = () => {};
  handleTerminalNotification = () => {};
  lastReportedBrowserActivity = undefined;
  terminalInteractionReportAt.clear();
  closeWebSocket(stateWs, setStateWs);
}
