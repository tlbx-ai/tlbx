/**
 * Session Tab Manager
 *
 * Manages per-session tab state, creates/destroys session wrappers,
 * handles tab switching and terminal container reparenting.
 */

import { createLogger } from '../logging';
import type { SessionTabId, IdeBarActionId } from './tabBar';
import {
  createTabBar,
  isTabVisible,
  setActiveTab,
  setActionActive,
  setTabLabel,
  setTabVisible,
  updateCwd,
  updateGitIndicator,
} from './tabBar';
import {
  $activeSessionId,
  $processStates,
  $sessionList,
  hasTerminalSizeControl,
} from '../../stores';
import { sessionTerminals } from '../../state';
import type { GitRepoBinding, GitStatusResponse } from '../git/types';
import type { Session } from '../../types';
import { isSessionInLayout } from '../layout/layoutStore';
import {
  applyTerminalScalingSync,
  fitSessionToScreen,
  fitTerminalToContainer,
  refreshTerminalPresentation,
} from '../terminal/scaling';
import { t } from '../i18n';
import { getAgentSurfaceLabel, resolveSessionSurfaceMode } from '../sessionSurface';
import {
  clearRememberedSessionState,
  getRememberedSessionTab,
  persistSessionTab,
} from '../updating/appShellState';

const log = createLogger('tabManager');

interface SessionTabState {
  wrapper: HTMLDivElement;
  tabBar: HTMLDivElement;
  panels: Record<SessionTabId, HTMLDivElement>;
  activeTab: SessionTabId;
  appServerControlAvailable: boolean;
}

const sessionTabStates = new Map<string, SessionTabState>();

const tabActivationCallbacks: Partial<
  Record<SessionTabId, Array<(sessionId: string, panel: HTMLDivElement) => void>>
> = {};
const tabDeactivationCallbacks: Partial<Record<SessionTabId, Array<(sessionId: string) => void>>> =
  {};

function invokeTabActivated(sessionId: string, tab: SessionTabId, panel: HTMLDivElement): void {
  for (const callback of tabActivationCallbacks[tab] ?? []) {
    callback(sessionId, panel);
  }
}

function invokeTabDeactivated(sessionId: string, tab: SessionTabId): void {
  for (const callback of tabDeactivationCallbacks[tab] ?? []) {
    callback(sessionId);
  }
}

function getSessionById(sessionId: string): Session | null {
  return $sessionList.get().find((session) => session.id === sessionId) ?? null;
}

function resolvePrimaryTab(
  session: Session | null | undefined,
): Extract<SessionTabId, 'terminal' | 'agent'> {
  return resolveSessionSurfaceMode(session) === 'agent' ? 'agent' : 'terminal';
}

function getTabDisplayLabel(session: Session | null | undefined, tab: SessionTabId): string {
  if (tab === 'agent') {
    return getAgentSurfaceLabel(session);
  }

  if (tab === 'files') {
    return t('sessionTabs.files');
  }

  return t('session.terminal');
}

function resolveInitialTab(
  sessionId: string,
  session: Session | null | undefined,
): Extract<SessionTabId, 'terminal' | 'agent' | 'files'> {
  const rememberedTab = getRememberedSessionTab(sessionId);
  if (rememberedTab === 'files') {
    return 'files';
  }

  const primaryTab = resolvePrimaryTab(session);
  if (rememberedTab === primaryTab) {
    return rememberedTab;
  }

  return primaryTab;
}

/**
 * Lets feature modules attach tab-specific behavior without coupling those
 * modules to session-wrapper creation order or tab-bar DOM internals.
 */
export function onTabActivated(
  tab: SessionTabId,
  callback: (sessionId: string, panel: HTMLDivElement) => void,
): void {
  (tabActivationCallbacks[tab] ??= []).push(callback);
}

/**
 * Lets modules release tab-scoped resources as soon as a surface loses focus,
 * which matters for streams like AppServerControl that should not keep running invisibly.
 */
export function onTabDeactivated(tab: SessionTabId, callback: (sessionId: string) => void): void {
  (tabDeactivationCallbacks[tab] ??= []).push(callback);
}

/**
 * Builds the stable per-session shell that all non-terminal surfaces hang off,
 * preserving the terminal instance while tlbx layers IDE-style panels around it.
 */
export function ensureSessionWrapper(sessionId: string): SessionTabState {
  const existing = sessionTabStates.get(sessionId);
  if (existing) return existing;
  const session = getSessionById(sessionId);
  const defaultTab = resolveInitialTab(sessionId, session);

  const wrapper = document.createElement('div');
  wrapper.className = 'session-wrapper';
  wrapper.dataset.sessionId = sessionId;
  wrapper.dataset.activeTab = defaultTab;

  const tabBar = createTabBar(
    sessionId,
    (tab) => {
      switchTab(sessionId, tab);
    },
    defaultTab,
  );

  const panelsContainer = document.createElement('div');
  panelsContainer.className = 'session-tab-panels';

  const tabs: SessionTabId[] = ['terminal', 'agent', 'files'];
  const panels = {} as Record<SessionTabId, HTMLDivElement>;

  for (const tabId of tabs) {
    const panel = document.createElement('div');
    panel.className = 'session-tab-panel';
    if (tabId === defaultTab) panel.classList.add('active');
    panel.dataset.panel = tabId;

    if (tabId === 'agent') {
      panel.classList.add('agent-tab-panel');
    }

    if (tabId === 'files') {
      panel.innerHTML =
        '<div class="file-browser"><div class="file-browser-tree"></div><div class="file-browser-preview"></div></div>';
    }

    panels[tabId] = panel;
    panelsContainer.appendChild(panel);
  }

  wrapper.appendChild(tabBar);
  wrapper.appendChild(panelsContainer);

  const state: SessionTabState = {
    wrapper,
    tabBar,
    panels,
    activeTab: defaultTab,
    appServerControlAvailable: false,
  };

  sessionTabStates.set(sessionId, state);
  persistSessionTab(sessionId, defaultTab);

  const termState = sessionTerminals.get(sessionId);
  if (termState) {
    panels.terminal.appendChild(termState.container);
  }

  const processState = $processStates.get()[sessionId];
  if (processState?.foregroundCwd) {
    updateCwd(tabBar, processState.foregroundCwd);
  }

  setActiveTab(tabBar, defaultTab);
  syncSessionTabCapabilities(sessionId, session);
  invokeTabActivated(sessionId, state.activeTab, state.panels[state.activeTab]);

  return state;
}

/**
 * Removes session-tab chrome when a session disappears without making other
 * modules guess which wrapper nodes are still safe to use.
 */
export function destroySessionWrapper(sessionId: string): void {
  const state = sessionTabStates.get(sessionId);
  if (!state) return;

  state.wrapper.remove();
  sessionTabStates.delete(sessionId);
  clearRememberedSessionState(sessionId);
}

/**
 * Exposes the wrapper host so feature modules can mount session-local surfaces
 * without duplicating wrapper lookup logic.
 */
export function getSessionWrapper(sessionId: string): HTMLDivElement | null {
  return sessionTabStates.get(sessionId)?.wrapper ?? null;
}

/**
 * Gives feature modules the correct per-session mount point for their panel so
 * tab-local UI can remain outside the tab manager's internal state.
 */
export function getTabPanel(sessionId: string, tab: SessionTabId): HTMLDivElement | null {
  return sessionTabStates.get(sessionId)?.panels[tab] ?? null;
}

/**
 * Exposes active-tab state to cooperating modules that need to adapt behavior
 * based on whether the user is in Terminal, Files, or an experimental surface.
 */
export function getActiveTab(sessionId: string): SessionTabId {
  const existing = sessionTabStates.get(sessionId);
  if (existing) {
    return existing.activeTab;
  }

  return resolvePrimaryTab(getSessionById(sessionId));
}

/**
 * Centralizes capability checks so modules do not accidentally surface tabs the
 * current session is not supposed to expose.
 */
export function isTabAvailable(sessionId: string, tab: SessionTabId): boolean {
  const state = sessionTabStates.get(sessionId);
  if (!state) {
    if (tab === 'files') {
      return true;
    }

    return tab === resolvePrimaryTab(getSessionById(sessionId));
  }

  if (tab === 'agent') {
    return state.appServerControlAvailable;
  }

  return isTabVisible(state.tabBar, tab);
}

/**
 * Reconciles live session capabilities with visible tab chrome so AppServerControl-backed
 * sessions stay accessible as soon as the backend says they are eligible.
 */
export function syncSessionTabCapabilities(
  sessionId: string,
  session: Session | null | undefined,
): void {
  const state = sessionTabStates.get(sessionId);
  if (!state) {
    return;
  }

  const primaryTab = resolvePrimaryTab(session);

  state.appServerControlAvailable = primaryTab === 'agent';
  setTabLabel(state.tabBar, 'agent', getAgentSurfaceLabel(session));
  setTabVisible(state.tabBar, 'terminal', primaryTab === 'terminal');
  setTabVisible(state.tabBar, 'agent', primaryTab === 'agent');

  if (state.activeTab !== 'files' && state.activeTab !== primaryTab) {
    switchTab(sessionId, primaryTab);
    return;
  }
}

/**
 * Lets AppServerControl-driven flows expose the tab as soon as real conversation content
 * exists, even before session-list metadata has caught up.
 */
export function setSessionAppServerControlAvailability(
  sessionId: string,
  _available: boolean,
): void {
  const state = sessionTabStates.get(sessionId);
  if (!state) {
    return;
  }

  const session = getSessionById(sessionId);
  state.appServerControlAvailable = session?.appServerControlOnly === true;
  setTabVisible(state.tabBar, 'agent', state.appServerControlAvailable);
  setTabVisible(state.tabBar, 'terminal', session?.appServerControlOnly !== true);
}

export function getTabLabelForSession(sessionId: string, tab: SessionTabId): string {
  return getTabDisplayLabel(getSessionById(sessionId), tab);
}

/**
 * Switches visible workflow surfaces while preserving the underlying terminal
 * container, which is the key reason tlbx can feel IDE-like without forking sessions.
 */
export function switchTab(
  sessionId: string,
  tab: SessionTabId,
  options?: { forceHidden?: boolean },
): void {
  const state = sessionTabStates.get(sessionId);
  if (!state) return;
  if (tab === 'agent') {
    if (!state.appServerControlAvailable && !isTabVisible(state.tabBar, tab)) return;
  } else if (!options?.forceHidden && !isTabVisible(state.tabBar, tab)) {
    return;
  }

  const previousTab = state.activeTab;
  if (previousTab === tab) return;

  state.panels[previousTab].classList.remove('active');
  invokeTabDeactivated(sessionId, previousTab);

  state.activeTab = tab;
  state.wrapper.dataset.activeTab = tab;
  state.panels[tab].classList.add('active');
  setActiveTab(state.tabBar, tab);
  persistSessionTab(sessionId, tab);

  invokeTabActivated(sessionId, tab, state.panels[tab]);

  if (tab === 'terminal') {
    const termState = sessionTerminals.get(sessionId);
    if (termState) {
      requestAnimationFrame(() => {
        refreshTerminalPresentation(sessionId, termState);

        if (isSessionInLayout(sessionId)) {
          const terminalPanel = termState.container.parentElement;
          if (terminalPanel instanceof HTMLElement) {
            if (hasTerminalSizeControl(sessionId)) {
              fitTerminalToContainer(sessionId, terminalPanel);
            } else {
              applyTerminalScalingSync(termState);
            }
          }
        } else if (hasTerminalSizeControl(sessionId)) {
          // Standalone terminal tab re-entry may reveal a hidden terminal that was
          // opened earlier. Only this session's size owner may sync that viewport size.
          fitSessionToScreen(sessionId);
        } else {
          applyTerminalScalingSync(termState);
        }

        termState.terminal.focus();
      });
    }
  }

  log.verbose(() => `Tab switched: ${sessionId} -> ${tab}`);
}

/**
 * Moves the existing terminal DOM back under the session wrapper instead of
 * recreating it, so reconnects and tab switches do not disrupt terminal state.
 */
export function reparentTerminalContainer(sessionId: string, container: HTMLDivElement): void {
  const state = sessionTabStates.get(sessionId);
  if (!state) return;
  state.panels.terminal.appendChild(container);
}

/**
 * Keeps session-tab chrome anchored in the user's real working context, which
 * matters when tlbx is used as a multi-session workspace rather than one shell.
 */
export function updateSessionCwd(sessionId: string, cwd: string): void {
  const state = sessionTabStates.get(sessionId);
  if (!state) return;
  updateCwd(state.tabBar, cwd);
}

/**
 * Provides a measured tab-bar height so surrounding layout can reserve the
 * right amount of space instead of relying on duplicated CSS constants.
 */
export function getTabBarHeight(): number {
  for (const state of sessionTabStates.values()) {
    if (state.wrapper.offsetParent !== null) {
      return state.tabBar.offsetHeight;
    }
  }
  return 0;
}

/**
 * Keeps global action chrome visually scoped to the active session, which
 * avoids implying that hidden session panels are currently receiving actions.
 */
export function setActionButtonActive(actionId: IdeBarActionId, active: boolean): void {
  const activeSessionId = $activeSessionId.get();
  for (const [sessionId, state] of sessionTabStates.entries()) {
    setActionActive(state.tabBar, actionId, active && sessionId === activeSessionId);
  }
}

/**
 * Updates lightweight git state in the tab bar so repo context stays visible
 * even when the heavier Git panel is closed.
 */
export function updateGitIndicatorForSession(
  sessionId: string,
  status: GitRepoBinding[] | GitStatusResponse | null,
): void {
  const state = sessionTabStates.get(sessionId);
  if (!state) return;
  updateGitIndicator(state.tabBar, status);
}

/**
 * Binds session-tab state to live process and session changes so workflow
 * surfaces appear and disappear without forcing a full reload.
 */
export function initSessionTabs(): void {
  $processStates.subscribe((states) => {
    for (const [sessionId, processState] of Object.entries(states)) {
      if (processState.foregroundCwd) {
        updateSessionCwd(sessionId, processState.foregroundCwd);
      }
    }
  });

  $sessionList.subscribe((sessions) => {
    for (const session of sessions) {
      syncSessionTabCapabilities(session.id, session);
    }
  });

  log.info(() => 'Session tabs initialized');
}
