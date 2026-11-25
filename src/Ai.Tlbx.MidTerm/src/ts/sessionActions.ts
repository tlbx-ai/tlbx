import { openInlineSessionRename } from './modules/sidebar/inlineRename';
import { isTerminalVisible } from './modules/terminal/presentationRefresh';
import { createLogger } from './modules/logging';
import { closeSettings } from './modules/settings';
import {
  createTerminalForSession,
  destroyTerminalForSession,
  fitSessionToScreen,
  isTerminalViewingScrollback,
  pasteToTerminal,
  revealTerminalPresentation,
  scrollToBottom,
  applyTerminalScalingSync,
  ensureTerminalStartupPaintRecovery,
} from './modules/terminal';
import { getInjectGuidancePromptKey } from './modules/midtermGuidance';
import {
  handleSessionClosed,
  isSessionInLayout,
  isLayoutActive,
  focusLayoutSession,
  getLayoutRoot,
} from './modules/layout';
import {
  ensureSessionWrapper,
  destroySessionWrapper,
  getActiveTab,
  reparentTerminalContainer,
  switchTab,
} from './modules/sessionTabs';
import {
  attachHubChannel,
  deleteRemoteSession,
  detachHubChannel,
  getFirstHubSessionId,
  getHubSession,
  getHubSessionRecord,
  isHubSessionId,
  refreshHubState,
  createRemoteHistoryEntry,
  renameRemoteSession,
  setRemoteSessionBookmark,
} from './modules/hub';
import {
  fetchHistory,
  refreshHistory,
  type LaunchEntry,
  createHistoryEntry,
} from './modules/history';
import {
  buildPlainShellBookmarkDedupeKey,
  buildTerminalBookmarkDedupeKey,
  doesSessionMatchTerminalBookmark,
  findEquivalentStarredBookmark,
  isPlainShellExecutable,
  resolveBookmarkTitle,
} from './modules/history/bookmarkSession';
import {
  isAdHocSession,
  resolveSessionLaunchOrigin,
} from './modules/sidebar/spacesTreeSidebarLogic';
import { resolveSessionHistoryMode } from './modules/history/launchMode';
import { getForegroundInfo } from './modules/process';
import { removeSessionDockState } from './modules/dockState';
import { removeSmartInputSessionState } from './modules/smartInput';
import { destroyAgentView } from './modules/agentView';
import { destroyFileBrowser } from './modules/fileBrowser';
import { destroyGitSession } from './modules/git';
import { destroyCommandsSession } from './modules/commands';
import { showAlert, showTextPrompt } from './utils/dialog';
import { dom, newlyCreatedSessions, sessionTerminals } from './state';
import {
  $activeSessionId,
  $currentSettings,
  $sessionList,
  clearPendingRename,
  markSessionClosing,
  cancelSessionClosing,
  getSession,
  removeSession,
  hasTerminalSizeControl,
  setPendingRename,
  setSession,
} from './stores';
import type { Session } from './types';
import {
  deleteSession as apiDeleteSession,
  patchHistoryEntry,
  renameSession as apiRenameSession,
  setSessionBookmark,
  setSessionControl as apiSetSessionControl,
} from './api/client';
import { sendActiveSessionHint, requestSizeControlForCurrentInput } from './modules/comms';
import { t } from './modules/i18n';
import { closeOperatorView } from './modules/operator';
import { closeActionGraphsView } from './modules/actionGraphs';
import { closeBoardsView } from './modules/boards';
import { repairTerminalDisplay } from './modules/terminal/displayRepair';
import { hasDirtySessionFiles, confirmSessionFileNavigation } from './modules/fileViewer/editState';

const log = createLogger('main');

export interface SessionSelectionOptions {
  userInitiated?: boolean;
  closeSettingsPanel?: boolean;
  focusTerminal?: boolean;
}

interface SessionActionsDeps {
  animateBookmarkSaveSuccess: (sessionId: string) => void;
  buildAppServerControlHistoryDedupeKey: (profile: string, workingDirectory: string) => string;
  closeMobileActionsMenu: () => void;
  getBookmarkSurfaceType: (session: Session, profile: string | null) => 'trm' | 'cdx' | 'acp';
  isAppServerControlOnlySession: (session: Session | null | undefined) => boolean;
}

interface ResolvedPinnedHistoryTarget {
  commandLine: string | null;
  dedupeKey: string;
  executable: string;
  fgInfo: ReturnType<typeof getForegroundInfo>;
  foregroundProcessCommandLine: string | null;
  foregroundProcessDisplayName: string | null;
  foregroundProcessIdentity: string | null;
  foregroundProcessName: string | null;
  historyMode: ReturnType<typeof resolveSessionHistoryMode>;
  isPlainShell: boolean;
  surfaceType: 'trm' | 'cdx' | 'acp';
  workingDirectory: string;
}

interface ResolvePinnedHistoryTargetResult {
  target: ResolvedPinnedHistoryTarget | null;
  reason?: string;
}

interface BookmarkSessionRef {
  machineId: string | null;
  remoteSessionId: string | null;
  session: Session;
}

/** Full-surface overlay views that must yield when a session takes focus. */
function closeOverlayViews(): void {
  closeOperatorView();
  closeActionGraphsView();
  closeBoardsView();
}

function revealStandaloneTerminal(
  sessionId: string,
  state: ReturnType<typeof createTerminalForSession>,
  isNewlyCreated: boolean,
): void {
  state.container.classList.remove('hidden');
  if (isNewlyCreated) ensureTerminalStartupPaintRecovery(sessionId, state);
}

function focusAlreadyVisibleSession(sessionId: string, focusTerminal: boolean): boolean {
  const existing = sessionTerminals.get(sessionId);
  if ($activeSessionId.get() !== sessionId || !existing?.opened || !isTerminalVisible(existing))
    return false;
  if (focusTerminal) existing.terminal.focus();
  return true;
}

function hideStandaloneTerminalContainers(): void {
  dom.terminalsArea?.querySelectorAll(':scope > .session-wrapper').forEach((wrapper) => {
    wrapper.classList.add('hidden');
  });
  sessionTerminals.forEach((state, id) => {
    if (!isSessionInLayout(id)) {
      state.container.classList.add('hidden');
    }
  });
}

function getBookmarkForegroundInfo(
  sessionId: string,
  session: Session,
): ReturnType<typeof getForegroundInfo> {
  const foreground = getForegroundInfo(sessionId);
  return {
    name: foreground.name ?? session.foregroundName ?? null,
    commandLine: foreground.commandLine ?? session.foregroundCommandLine ?? null,
    cwd: foreground.cwd ?? session.currentDirectory ?? null,
    displayName: foreground.displayName ?? session.foregroundDisplayName ?? null,
    processIdentity: foreground.processIdentity ?? session.foregroundProcessIdentity ?? null,
  };
}

function resolvePinnedHistoryTargetForSession(
  sessionId: string,
  session: Session,
  deps: Pick<
    SessionActionsDeps,
    'buildAppServerControlHistoryDedupeKey' | 'getBookmarkSurfaceType'
  >,
): ResolvePinnedHistoryTargetResult {
  const historyMode = resolveSessionHistoryMode(session);
  const fgInfo = getBookmarkForegroundInfo(sessionId, session);
  const surfaceType = deps.getBookmarkSurfaceType(session, historyMode.profile);
  const workingDirectory = (fgInfo.cwd ?? session.currentDirectory ?? '').trim();

  if (historyMode.launchMode === 'appServerControl') {
    if (!historyMode.profile) {
      log.info(
        () => `pinSessionToHistory: missing profile for appServerControl session ${sessionId}`,
      );
      return {
        target: null,
        reason:
          'Cannot bookmark this Agent Controller session because its provider profile is unknown.',
      };
    }
    if (!workingDirectory) {
      log.info(
        () =>
          `pinSessionToHistory: missing working directory for appServerControl session ${sessionId}`,
      );
      return {
        target: null,
        reason: 'Cannot bookmark this session because its working directory is unavailable.',
      };
    }

    return {
      target: {
        commandLine: null,
        dedupeKey: deps.buildAppServerControlHistoryDedupeKey(
          historyMode.profile,
          workingDirectory,
        ),
        executable: historyMode.profile,
        fgInfo,
        foregroundProcessCommandLine: null,
        foregroundProcessDisplayName: null,
        foregroundProcessIdentity: null,
        foregroundProcessName: null,
        historyMode,
        isPlainShell: false,
        surfaceType,
        workingDirectory,
      },
    };
  }

  if (!workingDirectory) {
    log.info(
      () => `pinSessionToHistory: missing working directory for terminal session ${sessionId}`,
    );
    return {
      target: null,
      reason: 'Cannot bookmark this terminal because its working directory is unavailable.',
    };
  }

  if (isPlainShellExecutable(fgInfo.name, session.shellType)) {
    const shellDedupeKey = buildPlainShellBookmarkDedupeKey(session.shellType, workingDirectory);
    if (!shellDedupeKey) {
      return {
        target: null,
        reason: 'Cannot bookmark this terminal because its shell type is unavailable.',
      };
    }
    return {
      target: {
        commandLine: null,
        dedupeKey: shellDedupeKey,
        executable: session.shellType,
        fgInfo,
        foregroundProcessCommandLine: null,
        foregroundProcessDisplayName: null,
        foregroundProcessIdentity: null,
        foregroundProcessName: null,
        historyMode,
        isPlainShell: true,
        surfaceType,
        workingDirectory,
      },
    };
  }

  const terminalDedupeKey = buildTerminalBookmarkDedupeKey(
    session.shellType,
    fgInfo.name,
    fgInfo.commandLine,
    workingDirectory,
    fgInfo.processIdentity,
  );
  if (!fgInfo.name || !terminalDedupeKey) {
    log.info(() => `pinSessionToHistory: missing process tuple for ${sessionId}`);
    return {
      target: null,
      reason: 'Cannot bookmark this terminal because its foreground process could not be resolved.',
    };
  }

  return {
    target: {
      commandLine: fgInfo.commandLine,
      dedupeKey: terminalDedupeKey,
      executable: fgInfo.name,
      fgInfo,
      foregroundProcessCommandLine: fgInfo.commandLine,
      foregroundProcessDisplayName: fgInfo.displayName,
      foregroundProcessIdentity: fgInfo.processIdentity,
      foregroundProcessName: fgInfo.name,
      historyMode,
      isPlainShell: false,
      surfaceType,
      workingDirectory,
    },
  };
}

async function resolvePinnedBookmarkLabel(
  session: Session,
  machineId: string | null,
  target: ResolvedPinnedHistoryTarget,
): Promise<string | null> {
  const { explicitLabel, defaultLabel } = resolveBookmarkTitle(session, {
    launchMode: target.historyMode.launchMode,
    profile: target.historyMode.profile,
    workingDirectory: target.workingDirectory,
    executable: target.executable,
    commandLine: target.commandLine,
    isPlainShell: target.isPlainShell,
  });

  if (explicitLabel !== null) {
    return explicitLabel;
  }

  if (!machineId) {
    try {
      const entries = await fetchHistory();
      const existing = findEquivalentStarredBookmark(entries, {
        bookmarkId: session.bookmarkId,
        launchMode: target.historyMode.launchMode,
        profile: target.historyMode.profile,
        shellType: session.shellType,
        executable: target.executable,
        commandLine: target.commandLine,
        workingDirectory: target.workingDirectory,
      });
      if (existing?.label?.trim()) {
        return null;
      }
    } catch {
      // Fall through to defaultLabel when history cannot be inspected.
    }
  }

  return defaultLabel;
}

function buildPinnedHistoryEntryInput(
  session: Session,
  label: string | null,
  target: ResolvedPinnedHistoryTarget,
) {
  return {
    shellType: session.shellType,
    executable: target.executable,
    commandLine: target.commandLine,
    workingDirectory: target.workingDirectory,
    isStarred: true,
    label,
    notes: session.notes ?? null,
    dedupeKey: target.dedupeKey,
    launchMode: target.historyMode.launchMode,
    profile: target.historyMode.profile,
    launchOrigin: resolveSessionLaunchOrigin(session),
    surfaceType: target.surfaceType,
    foregroundProcessName: target.foregroundProcessName,
    foregroundProcessCommandLine: target.foregroundProcessCommandLine,
    foregroundProcessDisplayName: target.foregroundProcessDisplayName,
    foregroundProcessIdentity: target.foregroundProcessIdentity,
  };
}

export function createSessionActionHandlers({
  animateBookmarkSaveSuccess,
  buildAppServerControlHistoryDedupeKey,
  closeMobileActionsMenu,
  getBookmarkSurfaceType,
  isAppServerControlOnlySession,
}: SessionActionsDeps) {
  function syncStandaloneSessionWrapper(sessionId: string): void {
    dom.terminalsArea?.querySelectorAll(':scope > .session-wrapper').forEach((wrapper) => {
      (wrapper as HTMLElement).classList.toggle(
        'hidden',
        wrapper.getAttribute('data-session-id') !== sessionId,
      );
    });
  }

  function selectHubSession(sessionId: string, focusTerminal: boolean): boolean {
    const sessionInfo = getHubSession(sessionId);
    if (!sessionInfo) {
      return true;
    }

    detachHubChannel();
    hideStandaloneTerminalContainers();
    $activeSessionId.set(sessionId);

    const state = createTerminalForSession(sessionId, sessionInfo);
    const tabState = ensureSessionWrapper(sessionId);
    reparentTerminalContainer(sessionId, state.container);
    if (dom.terminalsArea && !dom.terminalsArea.contains(tabState.wrapper)) {
      dom.terminalsArea.appendChild(tabState.wrapper);
    }

    syncStandaloneSessionWrapper(sessionId);
    state.container.classList.remove('hidden');
    if (isLayoutActive()) {
      getLayoutRoot()?.classList.add('hidden');
    }

    attachHubChannel(sessionId);
    requestAnimationFrame(() => {
      revealTerminalPresentation(sessionId, state);
      if (focusTerminal) {
        state.terminal.focus();
      }
      if (!isTerminalViewingScrollback(state)) {
        scrollToBottom(sessionId);
      }
    });

    dom.emptyState?.classList.add('hidden');
    return true;
  }

  function selectLayoutSession(sessionId: string, focusTerminal: boolean): boolean {
    if (!isSessionInLayout(sessionId)) {
      return false;
    }

    if (focusTerminal) {
      focusLayoutSession(sessionId);
    } else {
      $activeSessionId.set(sessionId);
    }
    sendActiveSessionHint(sessionId);
    const sessionInfo = getSession(sessionId);
    if (!isAppServerControlOnlySession(sessionInfo)) {
      createTerminalForSession(sessionId, sessionInfo);
    }
    getLayoutRoot()?.classList.remove('hidden');
    hideStandaloneTerminalContainers();
    return true;
  }

  function selectStandaloneSession(sessionId: string, focusTerminal: boolean): void {
    if (focusAlreadyVisibleSession(sessionId, focusTerminal)) return;
    hideStandaloneTerminalContainers();
    $activeSessionId.set(sessionId);
    sendActiveSessionHint(sessionId);

    const sessionInfo = getSession(sessionId);
    const appServerControlOnly = isAppServerControlOnlySession(sessionInfo);
    const state = appServerControlOnly ? null : createTerminalForSession(sessionId, sessionInfo);
    const isNewlyCreated = newlyCreatedSessions.has(sessionId);
    const activeTab = getActiveTab(sessionId);

    const tabState = ensureSessionWrapper(sessionId);
    if (state) {
      reparentTerminalContainer(sessionId, state.container);
    }
    if (dom.terminalsArea && !dom.terminalsArea.contains(tabState.wrapper)) {
      dom.terminalsArea.appendChild(tabState.wrapper);
    }

    syncStandaloneSessionWrapper(sessionId);
    if (state) {
      revealStandaloneTerminal(sessionId, state, isNewlyCreated);
    }
    if (isLayoutActive()) {
      getLayoutRoot()?.classList.add('hidden');
    }

    if (appServerControlOnly && activeTab === 'terminal') {
      switchTab(sessionId, 'agent');
    }

    requestAnimationFrame(() => {
      if (state) {
        revealTerminalPresentation(sessionId, state);
        if (activeTab === 'terminal') {
          if (hasTerminalSizeControl(sessionId)) {
            fitSessionToScreen(sessionId);
          } else {
            applyTerminalScalingSync(state);
          }
        }
        if (focusTerminal && activeTab !== 'agent') {
          state.terminal.focus();
        }
        if (isNewlyCreated || !isTerminalViewingScrollback(state)) {
          scrollToBottom(sessionId);
        }
      }

      if (isNewlyCreated) {
        newlyCreatedSessions.delete(sessionId);
      }
    });

    dom.emptyState?.classList.add('hidden');
  }

  function selectSession(sessionId: string, options?: SessionSelectionOptions): void {
    const previous = $activeSessionId.get();
    if (previous && previous !== sessionId && hasDirtySessionFiles(previous)) {
      void confirmSessionFileNavigation(previous).then((accepted) => {
        if (accepted && $activeSessionId.get() === previous) selectSession(sessionId, options);
      });
      return;
    }
    closeOverlayViews();
    closeMobileActionsMenu();
    if (options?.closeSettingsPanel !== false) {
      closeSettings();
    }
    const focusTerminal = options?.focusTerminal !== false;

    if (isHubSessionId(sessionId)) {
      selectHubSession(sessionId, focusTerminal);
      if (options?.userInitiated) requestSizeControlForCurrentInput(sessionId);
      return;
    }

    detachHubChannel();
    if (options?.userInitiated) requestSizeControlForCurrentInput(sessionId);
    if (selectLayoutSession(sessionId, focusTerminal)) {
      return;
    }

    selectStandaloneSession(sessionId, focusTerminal);
  }

  const closingSessions = new Set<string>();

  function deleteSession(sessionId: string): void {
    if (hasDirtySessionFiles(sessionId)) {
      void confirmSessionFileNavigation(sessionId).then((accepted) => {
        if (accepted) deleteSession(sessionId);
      });
      return;
    }
    if (isHubSessionId(sessionId)) {
      const record = getHubSessionRecord(sessionId);
      destroySessionWrapper(sessionId);
      destroyTerminalForSession(sessionId);
      if ($activeSessionId.get() === sessionId) {
        $activeSessionId.set(null);
      }
      detachHubChannel(sessionId);
      if (record) {
        deleteRemoteSession(record.machineId, record.remoteSessionId)
          .then(() => refreshHubState())
          .catch((e: unknown) => {
            log.error(() => `Failed to delete remote session ${sessionId}: ${String(e)}`);
          });
      }

      const nextLocal = $sessionList.get()[0]?.id ?? getFirstHubSessionId();
      if (nextLocal) {
        selectSession(nextLocal, { closeSettingsPanel: false });
      }
      return;
    }

    if (closingSessions.has(sessionId)) return;
    closingSessions.add(sessionId);
    const closedSession = getSession(sessionId);
    markSessionClosing(sessionId);
    handleSessionClosed(sessionId);
    removeSessionDockState(sessionId);
    removeSmartInputSessionState(sessionId);
    destroyAgentView(sessionId);
    destroyFileBrowser(sessionId);
    destroyGitSession(sessionId);
    destroyCommandsSession(sessionId);
    destroySessionWrapper(sessionId);
    destroyTerminalForSession(sessionId);
    removeSession(sessionId);

    if ($activeSessionId.get() === sessionId) {
      $activeSessionId.set(null);
      const firstSession = $sessionList.get()[0];
      if (firstSession?.id) selectSession(firstSession.id, { closeSettingsPanel: false });
    }
    void apiDeleteSession(sessionId)
      .catch(() => apiDeleteSession(sessionId))
      .catch(async (error: unknown) => {
        cancelSessionClosing(sessionId);
        if (closedSession) {
          setSession(closedSession);
          if (!closedSession.appServerControlOnly) {
            createTerminalForSession(sessionId, closedSession);
          }
        }
        log.error(() => `Failed to close session ${sessionId}: ${String(error)}`);
        await showAlert(error instanceof Error ? error.message : String(error), {
          title: t('session.close'),
        });
      })
      .finally(() => closingSessions.delete(sessionId));
  }

  async function enableMidtermFeatures(sessionId: string): Promise<void> {
    if (isHubSessionId(sessionId)) {
      return;
    }

    try {
      const fg = getForegroundInfo(sessionId);
      await pasteToTerminal(sessionId, t(getInjectGuidancePromptKey(fg.name)));
    } catch (e: unknown) {
      log.error(() => `Failed to enable tlbx features for ${sessionId}: ${String(e)}`);
    }
  }

  async function repairSessionDisplay(sessionId: string): Promise<void> {
    if (isHubSessionId(sessionId)) {
      return;
    }

    try {
      await repairTerminalDisplay(sessionId);
    } catch (error) {
      log.error(() => `Failed to repair terminal display for ${sessionId}: ${String(error)}`);
      await showAlert(error instanceof Error ? error.message : String(error), {
        title: t('session.repairDisplay'),
      });
    }
  }

  async function patchPinnedHistoryLabelIfMatchingTuple(
    sessionId: string,
    nameToSend: string,
  ): Promise<void> {
    const currentSession = getSession(sessionId);
    const bookmarkId = currentSession?.bookmarkId;
    if (!bookmarkId) return;

    if (currentSession.appServerControlOnly) {
      patchHistoryEntry(bookmarkId, { label: nameToSend || '' }).catch(() => {});
      return;
    }

    let entries: LaunchEntry[];
    try {
      entries = await fetchHistory();
    } catch {
      return;
    }

    const linkedEntry = entries.find((entry) => entry.id === bookmarkId);
    if (!linkedEntry) return;

    const fgInfo = getForegroundInfo(sessionId);
    if (!doesSessionMatchTerminalBookmark(currentSession, fgInfo, linkedEntry)) {
      log.verbose(() => `Skip bookmark label patch for ${sessionId}: tuple moved`);
      return;
    }

    patchHistoryEntry(bookmarkId, { label: nameToSend || '' }).catch(() => {});
  }

  function renameSession(sessionId: string, newName: string | null): void {
    void persistSessionName(sessionId, newName).catch((error: unknown) => {
      log.error(() => `Failed to rename session ${sessionId}: ${String(error)}`);
      void showAlert(String(error));
    });
  }

  async function persistSessionName(sessionId: string, newName: string | null): Promise<void> {
    const trimmedName = (newName || '').trim();
    if (isHubSessionId(sessionId)) {
      const record = getHubSessionRecord(sessionId);
      if (!record) throw new Error('Session no longer exists');
      await renameRemoteSession(record.machineId, record.remoteSessionId, { name: trimmedName });
      await refreshHubState();
      return;
    }
    const session = getSession(sessionId);
    if (!session) throw new Error('Session no longer exists');
    const nameToSend = trimmedName === '' || trimmedName === session.shellType ? '' : trimmedName;
    setPendingRename(sessionId, nameToSend);
    setSession({ ...session, name: nameToSend, manuallyNamed: true });
    try {
      const result = await apiRenameSession(sessionId, nameToSend);
      if (!result.response.ok) throw new Error(`Rename failed (${result.response.status})`);
      void patchPinnedHistoryLabelIfMatchingTuple(sessionId, nameToSend);
    } catch (error) {
      clearPendingRename(sessionId);
      const current = getSession(sessionId);
      if (current)
        setSession({ ...current, name: session.name, manuallyNamed: session.manuallyNamed });
      throw error;
    }
  }

  function getSessionFamilyIds(sessionId: string): string[] {
    const session = $sessionList.get().find((item) => item.id === sessionId);
    if (!session) {
      return [];
    }

    const rootSessionId = session.parentSessionId ?? session.id;
    return $sessionList
      .get()
      .filter((item) => item.id === rootSessionId || item.parentSessionId === rootSessionId)
      .map((item) => item.id)
      .filter((id): id is string => !!id);
  }

  function toggleAgentControl(sessionId: string): void {
    if (isHubSessionId(sessionId)) {
      return;
    }

    const session = getSession(sessionId);
    if (!session) return;

    const nextAgentControlled = !session.agentControlled;
    const previousSnapshots = getSessionFamilyIds(sessionId)
      .map((id) => getSession(id))
      .filter((item): item is Session => !!item)
      .map((item) => ({ ...item }));

    for (const snapshot of previousSnapshots) {
      setSession({
        ...snapshot,
        agentControlled: nextAgentControlled,
      });
    }

    apiSetSessionControl(sessionId, nextAgentControlled).catch((e: unknown) => {
      for (const snapshot of previousSnapshots) {
        setSession(snapshot);
      }
      log.error(() => `Failed to toggle agent control for ${sessionId}: ${String(e)}`);
    });
  }

  function startInlineRename(sessionId: string): void {
    const item = dom.sessionList?.querySelector(`[data-session-id="${CSS.escape(sessionId)}"]`);
    const session = getSession(sessionId);
    if (item && session)
      openInlineSessionRename(item, session.name || session.shellType, (name) =>
        persistSessionName(sessionId, name),
      );
  }

  async function promptRenameSession(sessionId: string): Promise<void> {
    const session = getSession(sessionId);
    if (!session) return;

    const currentName = session.name || session.shellType;
    const newName = await showTextPrompt({
      title: 'Rename terminal',
      initialValue: currentName,
    });
    if (newName !== null) {
      renameSession(sessionId, newName);
    }
  }

  function resolveBookmarkSessionRef(sessionId: string): BookmarkSessionRef | null {
    if (isHubSessionId(sessionId)) {
      const record = getHubSessionRecord(sessionId);
      if (!record) {
        return null;
      }

      return {
        machineId: record.machineId,
        remoteSessionId: record.remoteSessionId,
        session: {
          ...record.session,
          id: sessionId,
          _order: record.session.order,
        },
      };
    }

    const session = getSession(sessionId);
    return session
      ? {
          machineId: null,
          remoteSessionId: null,
          session,
        }
      : null;
  }

  function canManageAdHocBookmarks(session: Session, machineId: string | null): boolean {
    if (!isAdHocSession(session)) {
      return false;
    }

    if (machineId !== null && session.appServerControlOnly) {
      return false;
    }

    if ($currentSettings.get()?.showBookmarks === false) {
      return false;
    }

    return (
      $currentSettings.get()?.allowAdHocSessionBookmarks === true || !!session.bookmarkId?.trim()
    );
  }

  async function ensurePinnedSessionBookmark(
    sessionRef: BookmarkSessionRef,
    bookmarkId: string,
  ): Promise<void> {
    const { machineId, remoteSessionId, session } = sessionRef;
    if (machineId && remoteSessionId) {
      if (session.bookmarkId === bookmarkId) {
        return;
      }

      await setRemoteSessionBookmark(machineId, remoteSessionId, bookmarkId);
      await refreshHubState();
      return;
    }

    const sessionId = session.id;
    const current = getSession(sessionId) ?? session;
    if (current.bookmarkId === bookmarkId) {
      return;
    }

    const { response } = await setSessionBookmark(sessionId, bookmarkId);
    if (!response.ok) {
      throw new Error(`Bookmark link failed with status ${response.status}.`);
    }
    setSession({ ...current, bookmarkId });
  }

  function logPinnedHistoryOutcome(
    previousBookmarkId: string | null,
    bookmarkId: string,
    target: ResolvedPinnedHistoryTarget,
  ): void {
    const targetName = target.fgInfo.name ?? target.executable;
    if (previousBookmarkId && previousBookmarkId !== bookmarkId) {
      log.info(() => `Pinned to history (new tuple): ${targetName} (id=${bookmarkId})`);
      return;
    }
    if (previousBookmarkId === bookmarkId) {
      log.info(
        () => `Pinned to history (updated existing tuple): ${targetName} (id=${bookmarkId})`,
      );
      return;
    }

    log.info(() => `Pinned to history: ${targetName} (id=${bookmarkId})`);
  }

  async function pinSessionToHistory(sessionId: string): Promise<void> {
    const sessionRef = resolveBookmarkSessionRef(sessionId);
    if (!sessionRef) {
      log.warn(() => `pinSessionToHistory: session ${sessionId} not found`);
      return;
    }

    const { machineId, session } = sessionRef;
    if (!canManageAdHocBookmarks(session, machineId)) {
      log.info(() => `pinSessionToHistory: skipping ineligible session ${sessionId}`);
      return;
    }

    const previousBookmarkId = session.bookmarkId ?? null;
    const resolved = resolvePinnedHistoryTargetForSession(sessionId, session, {
      buildAppServerControlHistoryDedupeKey,
      getBookmarkSurfaceType,
    });
    if (!resolved.target) {
      if (resolved.reason) {
        await showAlert(resolved.reason, { title: 'Bookmark save failed' });
      }
      return;
    }
    const target = resolved.target;

    try {
      const label = await resolvePinnedBookmarkLabel(session, machineId, target);
      const input = buildPinnedHistoryEntryInput(session, label, target);
      const id = machineId
        ? (await createRemoteHistoryEntry(machineId, input)).id
        : await createHistoryEntry(input);

      if (!id) {
        throw new Error('The bookmark service did not return an id.');
      }

      await ensurePinnedSessionBookmark(sessionRef, id);
      if (machineId) {
        await refreshHubState();
      } else {
        refreshHistory();
      }
      animateBookmarkSaveSuccess(sessionId);
      logPinnedHistoryOutcome(previousBookmarkId, id, target);
    } catch (error) {
      log.error(() => `Failed to pin session ${sessionId} to history: ${String(error)}`);
      await showAlert(error instanceof Error ? error.message : String(error), {
        title: 'Bookmark save failed',
      });
    }
  }

  return {
    deleteSession,
    enableMidtermFeatures,
    pinSessionToHistory,
    promptRenameSession,
    repairSessionDisplay,
    renameSession,
    selectSession,
    startInlineRename,
    toggleAgentControl,
  };
}
