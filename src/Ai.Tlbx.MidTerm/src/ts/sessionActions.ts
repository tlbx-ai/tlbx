import { createLogger } from './modules/logging';
import { suppressAllHeat } from './modules/sidebar';
import { closeSettings } from './modules/settings';
import {
  createTerminalForSession,
  destroyTerminalForSession,
  fitSessionToScreen,
  isTerminalViewingScrollback,
  pasteToTerminal,
  refreshTerminalPresentation,
  scrollToBottom,
  applyTerminalScalingSync,
} from './modules/terminal';
import { getInjectGuidancePromptKey } from './modules/midtermGuidance';
import { buildProcessCwdTuple } from './modules/sidebar/processDisplay';
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
import { sendActiveSessionHint } from './modules/comms';
import { t } from './modules/i18n';
import { closeOperatorView } from './modules/operator';
import { repairTerminalDisplay } from './modules/terminal/displayRepair';

const log = createLogger('main');

export interface SessionSelectionOptions {
  closeSettingsPanel?: boolean;
  focusTerminal?: boolean;
}

interface SessionActionsDeps {
  animateBookmarkSaveSuccess: (sessionId: string) => void;
  buildAppServerControlHistoryDedupeKey: (
    profile: 'codex' | 'claude' | 'grok',
    workingDirectory: string,
  ) => string;
  closeMobileActionsMenu: () => void;
  getBookmarkSurfaceType: (
    session: Session,
    profile: 'codex' | 'claude' | 'grok' | null,
  ) => 'trm' | 'cdx' | 'cld' | 'grk';
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
  surfaceType: 'trm' | 'cdx' | 'cld' | 'grk';
  workingDirectory: string;
}

interface BookmarkSessionRef {
  machineId: string | null;
  remoteSessionId: string | null;
  session: Session;
}

export function createSessionActionHandlers({
  animateBookmarkSaveSuccess,
  buildAppServerControlHistoryDedupeKey,
  closeMobileActionsMenu,
  getBookmarkSurfaceType,
  isAppServerControlOnlySession,
}: SessionActionsDeps) {
  function hideStandaloneTerminalContainers(): void {
    sessionTerminals.forEach((state, id) => {
      if (!isSessionInLayout(id)) {
        state.container.classList.add('hidden');
      }
    });
  }

  function syncStandaloneSessionWrapper(sessionId: string): void {
    dom.terminalsArea?.querySelectorAll('.session-wrapper').forEach((wrapper) => {
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
    suppressAllHeat(1500);

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
      refreshTerminalPresentation(sessionId, state);
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

    suppressAllHeat(1500);
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
    hideStandaloneTerminalContainers();
    $activeSessionId.set(sessionId);
    suppressAllHeat(1500);
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
      state.container.classList.remove('hidden');
    }
    if (isLayoutActive()) {
      getLayoutRoot()?.classList.add('hidden');
    }

    if (appServerControlOnly && activeTab === 'terminal') {
      switchTab(sessionId, 'agent');
    }

    requestAnimationFrame(() => {
      if (state) {
        refreshTerminalPresentation(sessionId, state);
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
    closeOperatorView();
    closeMobileActionsMenu();
    if (options?.closeSettingsPanel !== false) {
      closeSettings();
    }
    const focusTerminal = options?.focusTerminal !== false;

    if (isHubSessionId(sessionId)) {
      void selectHubSession(sessionId, focusTerminal);
      return;
    }

    detachHubChannel();
    if (selectLayoutSession(sessionId, focusTerminal)) {
      return;
    }

    selectStandaloneSession(sessionId, focusTerminal);
  }

  function deleteSession(sessionId: string): void {
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
      if (firstSession?.id) {
        selectSession(firstSession.id, { closeSettingsPanel: false });
      }
    }

    apiDeleteSession(sessionId).catch((e: unknown) => {
      log.error(() => `Failed to delete session ${sessionId}: ${String(e)}`);
    });
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

    const fgInfo = getForegroundInfo(sessionId);
    const currentTuple = buildProcessCwdTuple(
      fgInfo.name,
      fgInfo.commandLine,
      fgInfo.cwd,
      fgInfo.processIdentity,
    );
    if (!currentTuple) return;

    let entries: LaunchEntry[];
    try {
      entries = await fetchHistory();
    } catch {
      return;
    }

    const linkedEntry = entries.find((entry) => entry.id === bookmarkId);
    if (!linkedEntry) return;

    const linkedTuple = buildProcessCwdTuple(
      linkedEntry.executable,
      linkedEntry.commandLine ?? null,
      linkedEntry.workingDirectory,
    );
    if (!linkedTuple || linkedTuple !== currentTuple) {
      log.verbose(() => `Skip bookmark label patch for ${sessionId}: tuple moved`);
      return;
    }

    patchHistoryEntry(bookmarkId, { label: nameToSend || '' }).catch(() => {});
  }

  function renameSession(sessionId: string, newName: string | null): void {
    if (isHubSessionId(sessionId)) {
      const record = getHubSessionRecord(sessionId);
      if (!record) return;

      const trimmedName = (newName || '').trim();
      renameRemoteSession(record.machineId, record.remoteSessionId, { name: trimmedName })
        .then(() => refreshHubState())
        .catch((e: unknown) => {
          log.error(() => `Failed to rename remote session ${sessionId}: ${String(e)}`);
        });
      return;
    }

    const session = getSession(sessionId);
    if (!session) return;

    const trimmedName = (newName || '').trim();
    const nameToSend = trimmedName === '' || trimmedName === session.shellType ? '' : trimmedName;
    const previousName = session.name;
    const wasManuallyNamed = session.manuallyNamed;

    setPendingRename(sessionId, nameToSend);
    setSession({ ...session, name: nameToSend, manuallyNamed: true });

    apiRenameSession(sessionId, nameToSend)
      .then(() => {
        void patchPinnedHistoryLabelIfMatchingTuple(sessionId, nameToSend);
      })
      .catch((e: unknown) => {
        clearPendingRename(sessionId);
        const currentSession = getSession(sessionId);
        if (currentSession) {
          setSession({ ...currentSession, name: previousName, manuallyNamed: wasManuallyNamed });
        }
        log.error(() => `Failed to rename session ${sessionId}: ${String(e)}`);
      });
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
    const item = dom.sessionList?.querySelector(`[data-session-id="${sessionId}"]`);
    if (!item) return;

    const renameAnchor =
      item.querySelector('.session-title') ||
      item.querySelector('.process-title') ||
      item.querySelector('.session-title-row');
    if (!renameAnchor) return;

    const session = getSession(sessionId);
    const currentName = session ? session.name || session.shellType : '';
    const rect = renameAnchor.getBoundingClientRect();

    const input = document.createElement('input');
    input.type = 'text';
    input.className = 'session-rename-input';
    input.value = currentName;
    input.style.position = 'fixed';
    input.style.left = `${rect.left}px`;
    input.style.top = `${rect.top}px`;
    input.style.width = `${rect.width + 20}px`;
    input.style.height = `${rect.height}px`;
    input.style.zIndex = '10000';
    document.body.appendChild(input);

    let committed = false;
    const finishRename = (): void => {
      if (committed) return;
      committed = true;
      const newName = input.value;
      input.remove();
      renameSession(sessionId, newName);
    };

    const cancelRename = (): void => {
      if (committed) return;
      committed = true;
      input.remove();
    };

    input.addEventListener('blur', finishRename);
    input.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') {
        event.preventDefault();
        input.blur();
      } else if (event.key === 'Escape') {
        event.preventDefault();
        cancelRename();
      }
    });

    input.focus();
    input.select();
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

  function resolvePinnedHistoryTarget(
    sessionId: string,
    session: Session,
  ): ResolvedPinnedHistoryTarget | null {
    const historyMode = resolveSessionHistoryMode(session);
    const fgInfo = getBookmarkForegroundInfo(sessionId, session);
    const surfaceType = getBookmarkSurfaceType(session, historyMode.profile);

    if (historyMode.launchMode === 'appServerControl' && historyMode.profile) {
      const workingDirectory = fgInfo.cwd ?? session.currentDirectory ?? '';
      if (!workingDirectory) {
        log.info(
          () =>
            `pinSessionToHistory: missing working directory for appServerControl session ${sessionId}`,
        );
        return null;
      }

      return {
        commandLine: null,
        dedupeKey: buildAppServerControlHistoryDedupeKey(historyMode.profile, workingDirectory),
        executable: historyMode.profile,
        fgInfo,
        foregroundProcessCommandLine: null,
        foregroundProcessDisplayName: null,
        foregroundProcessIdentity: null,
        foregroundProcessName: null,
        historyMode,
        surfaceType,
        workingDirectory,
      };
    }

    const tupleKey = buildProcessCwdTuple(
      fgInfo.name,
      fgInfo.commandLine,
      fgInfo.cwd,
      fgInfo.processIdentity,
    );
    if (!fgInfo.name || !tupleKey) {
      log.info(() => `pinSessionToHistory: missing process tuple for ${sessionId}`);
      return null;
    }

    return {
      commandLine: fgInfo.commandLine,
      dedupeKey: tupleKey,
      executable: fgInfo.name,
      fgInfo,
      foregroundProcessCommandLine: fgInfo.commandLine,
      foregroundProcessDisplayName: fgInfo.displayName,
      foregroundProcessIdentity: fgInfo.processIdentity,
      foregroundProcessName: fgInfo.name,
      historyMode,
      surfaceType,
      workingDirectory: fgInfo.cwd ?? '',
    };
  }

  function buildPinnedHistoryLabel(session: Session): string | null {
    const trimmedName = (session.name || '').trim();
    return trimmedName && trimmedName !== session.shellType ? trimmedName : null;
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

  function canManageAdHocBookmarks(session: Session): boolean {
    if (!isAdHocSession(session)) {
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
    if (previousBookmarkId && previousBookmarkId !== bookmarkId) {
      log.info(() => `Pinned to history (new tuple): ${target.fgInfo.name} (id=${bookmarkId})`);
      return;
    }
    if (previousBookmarkId === bookmarkId) {
      log.info(
        () =>
          `Pinned to history (updated existing tuple): ${target.fgInfo.name} (id=${bookmarkId})`,
      );
      return;
    }

    log.info(() => `Pinned to history: ${target.fgInfo.name} (id=${bookmarkId})`);
  }

  async function pinSessionToHistory(sessionId: string): Promise<void> {
    const sessionRef = resolveBookmarkSessionRef(sessionId);
    if (!sessionRef) {
      log.warn(() => `pinSessionToHistory: session ${sessionId} not found`);
      return;
    }

    const { machineId, session } = sessionRef;
    if (!canManageAdHocBookmarks(session)) {
      log.info(() => `pinSessionToHistory: skipping ineligible session ${sessionId}`);
      return;
    }

    const label = buildPinnedHistoryLabel(session);
    const previousBookmarkId = session.bookmarkId ?? null;
    const target = resolvePinnedHistoryTarget(sessionId, session);
    if (!target) {
      return;
    }

    try {
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
