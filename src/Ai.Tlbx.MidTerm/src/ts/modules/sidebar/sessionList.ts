/**
 * Session List Module
 *
 * Handles rendering and updating the sidebar session list,
 * including empty state and mobile title updates.
 */

import type { Session, ProcessState } from '../../types';
import { t } from '../i18n';
import { pendingSessions, dom } from '../../state';
import {
  $settingsOpen,
  $activeSessionId,
  $currentSettings,
  $sessionList,
  getSession,
  isChildSession,
} from '../../stores';
import { MOBILE_BREAKPOINT, MOBILE_TOUCH_BREAKPOINT, icon } from '../../constants';
import { addProcessStateListener, getForegroundInfo } from '../process';
import {
  getLayoutSessionIds,
  isLayoutActive,
  isSessionInLayout,
  undockSession,
} from '../layout/layoutStore';
import { getHubSession, getHubSidebarSections, isHubSessionId } from '../hub/runtime';
import { getPrimarySurfaceLabel, isAgentSurfaceSession } from '../sessionSurface';
import { pruneHeatSessions, registerHeatCanvas, unregisterHeatCanvas } from './heatIndicator';
import { createSessionFilterController } from './sessionFilterController';
import {
  filterSessionsByQuery,
  getSessionControlMode,
  getSupervisorBadgeLabel,
  getSupervisorState,
  groupSessionsByController,
  needsAttention,
  shouldShowAgentControlAction,
  syncSessionItemActiveStates,
} from './sessionListLogic';
import type { SessionControlMode, SessionGroup } from './sessionListLogic';

export {
  filterSessionsByQuery,
  groupSessionsByController,
  shouldShowAgentControlAction,
  syncSessionItemActiveStates,
} from './sessionListLogic';
export type { SessionControlMode, SessionGroup } from './sessionListLogic';

// =============================================================================
// Helpers
// =============================================================================

/**
 * Check if a foreground process name is just the session's own shell.
 * Compares basename + extensionless identity of both values, handling
 * full paths, quoted command lines, and command arguments.
 */
function isShellProcess(processName: string, sessionId: string): boolean {
  const session = getSession(sessionId);
  if (!session?.shellType) return false;
  const normalizedProcess = normalizeExecutableName(processName);
  const normalizedShell = normalizeExecutableName(session.shellType);
  return normalizedProcess !== '' && normalizedProcess === normalizedShell;
}

/**
 * Normalize a shell/process identifier to a comparable executable identity.
 * - strips command-line arguments
 * - strips quotes
 * - extracts basename from paths
 * - removes ".exe" extension
 */
function normalizeExecutableName(value: string): string {
  const trimmed = value.trim();
  if (!trimmed) return '';

  let candidate = trimmed;
  const firstChar = candidate[0] ?? '';
  if (firstChar === '"' || firstChar === "'") {
    const quote = firstChar;
    const closingQuote = candidate.indexOf(quote, 1);
    if (closingQuote > 1) {
      candidate = candidate.slice(1, closingQuote);
    }
  }

  const basename = candidate.replace(/\\/g, '/').split('/').pop() ?? candidate;
  const token = basename.trim().split(/\s+/)[0] ?? basename.trim();
  return token.replace(/\.exe$/i, '').toLowerCase();
}

// =============================================================================
// Callback Types
// =============================================================================

/** Callbacks for session list interactions */
export interface SessionListCallbacks {
  onSelect: (sessionId: string) => void;
  onDelete: (sessionId: string) => void;
  onRename: (sessionId: string) => void;
  onToggleAgentControl: (sessionId: string) => void;
  onPinToHistory: (sessionId: string) => void;
  onEnableMidtermFeatures?: (sessionId: string) => void;
  onCloseSidebar: () => void;
}

let callbacks: SessionListCallbacks | null = null;
let mobileActionBackdrop: HTMLDivElement | null = null;
let mobileMenuListenersBound = false;
let queuedProcessInfoFrameId: number | null = null;
const queuedProcessInfoSessionIds = new Set<string>();
const SESSION_GROUP_STORAGE_KEYS = {
  human: 'midterm.sidebar.humanSessionsCollapsed',
  agent: 'midterm.sidebar.agentSessionsCollapsed',
} as const;
const SESSION_FILTER_STORAGE_KEY = 'midterm.sidebar.sessionFilter';
const HUB_MACHINE_STORAGE_PREFIX = 'midterm.sidebar.hubMachineCollapsed.';

function isHubMachineCollapsed(machineId: string): boolean {
  return localStorage.getItem(`${HUB_MACHINE_STORAGE_PREFIX}${machineId}`) === 'true';
}

function toggleHubMachineCollapsed(section: HTMLElement, machineId: string): void {
  const collapsed = section.classList.toggle('collapsed');
  localStorage.setItem(`${HUB_MACHINE_STORAGE_PREFIX}${machineId}`, String(collapsed));
}

function loadStoredSessionFilter(): string {
  try {
    return (localStorage.getItem(SESSION_FILTER_STORAGE_KEY) ?? '').trim();
  } catch {
    return '';
  }
}

function persistSessionFilter(value: string): void {
  try {
    if (value === '') {
      localStorage.removeItem(SESSION_FILTER_STORAGE_KEY);
    } else {
      localStorage.setItem(SESSION_FILTER_STORAGE_KEY, value);
    }
  } catch {
    // Ignore localStorage failures and keep the filter in memory.
  }
}

export function isSidebarSessionFilterEnabled(): boolean {
  return $currentSettings.get()?.showSidebarSessionFilter === true;
}
const sessionFilterController = createSessionFilterController({
  getElements: () => ({
    filterBar: dom.sessionFilterBar,
    filterInput: dom.sessionFilterInput,
    filterClear: dom.sessionFilterClear,
  }),
  isEnabled: isSidebarSessionFilterEnabled,
  areSettingsLoaded: () => $currentSettings.get() !== null,
  loadStoredFilter: loadStoredSessionFilter,
  persistFilter: persistSessionFilter,
  render: () => {
    renderSessionList();
  },
  translate: t,
});

export function isSessionFilterActive(): boolean {
  return sessionFilterController.isActive();
}

export function applySessionFilterSettingChange(): void {
  sessionFilterController.applySettingChange();
}

// =============================================================================
// Initialization
// =============================================================================

/**
 * Initialize session list module
 */
export function initializeSessionList(): void {
  addProcessStateListener(handleProcessStateChange);
  sessionFilterController.initialize();

  if (!mobileMenuListenersBound) {
    document.addEventListener('keydown', handleMobileMenuKeydown);
    window.addEventListener('resize', closeMobileActionMenu);
    window.addEventListener('orientationchange', closeMobileActionMenu);
    mobileMenuListenersBound = true;
  }
}

/**
 * Set callbacks for session list interactions
 */
export function setSessionListCallbacks(cbs: SessionListCallbacks): void {
  callbacks = cbs;
}

function flushQueuedProcessInfoUpdates(): void {
  queuedProcessInfoFrameId = null;
  const sessionIds = Array.from(queuedProcessInfoSessionIds);
  queuedProcessInfoSessionIds.clear();
  for (const sessionId of sessionIds) {
    updateSessionProcessInfo(sessionId);
  }
}

function queueProcessInfoUpdate(sessionId: string): void {
  queuedProcessInfoSessionIds.add(sessionId);
  if (queuedProcessInfoFrameId !== null) {
    return;
  }

  queuedProcessInfoFrameId = window.requestAnimationFrame(() => {
    flushQueuedProcessInfoUpdates();
  });
}

/**
 * Handle process state change and update the UI
 */
function handleProcessStateChange(sessionId: string, _state: ProcessState): void {
  queueProcessInfoUpdate(sessionId);
}

/**
 * Close the mobile action menu via keyboard.
 */
function handleMobileMenuKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    closeMobileActionMenu();
  }
}

/**
 * Session action dropdowns are only used on mobile layouts.
 */
function isMobileSessionMenuEnabled(): boolean {
  if (window.matchMedia(`(max-width: ${MOBILE_BREAKPOINT}px)`).matches) {
    return true;
  }

  return (
    window.matchMedia('(hover: none) and (pointer: coarse)').matches &&
    window.innerWidth <= MOBILE_TOUCH_BREAKPOINT
  );
}

/**
 * Render shared icon + label content for session action buttons.
 */
function setActionButtonContent(
  button: HTMLButtonElement,
  label: string,
  iconMarkupOrText: string,
  useTextIcon: boolean = false,
): void {
  const iconEl = document.createElement('span');
  iconEl.className = `session-action-icon${useTextIcon ? ' text-icon' : ''}`;

  if (useTextIcon) {
    iconEl.textContent = iconMarkupOrText;
  } else {
    iconEl.innerHTML = iconMarkupOrText;
  }

  const labelEl = document.createElement('span');
  labelEl.className = 'session-action-label';
  labelEl.textContent = label;

  button.replaceChildren(iconEl, labelEl);
  button.title = label;
  button.setAttribute('aria-label', label);
}

function getProcessTitleFallback(sessionId: string): string {
  const session = getSession(sessionId);
  return session && isAgentSurfaceSession(session)
    ? getPrimarySurfaceLabel(session)
    : session?.shellType || t('session.terminal');
}

/**
 * Create the foreground process indicator element
 * Layout: ...directory> process...
 * - Directory ellipsis from left (end of path is most important)
 * - Process ellipsis from right (process name is most important)
 */
export function createForegroundIndicator(
  cwd: string | null | undefined,
  commandLine: string | null | undefined,
  processName: string,
  displayName: string | null | undefined,
): HTMLElement {
  const container = document.createElement('span');
  container.className = 'session-foreground';

  const cmdDisplay = displayName?.trim() || commandLine || processName;
  container.title = `${commandLine ?? processName}\n${cwd ?? ''}`;

  if (cwd) {
    const cwdSpan = document.createElement('span');
    cwdSpan.className = 'fg-cwd';
    cwdSpan.textContent = cwd;
    container.appendChild(cwdSpan);

    const separator = document.createElement('span');
    separator.className = 'fg-separator';
    separator.textContent = '>';
    container.appendChild(separator);
  }

  const processSpan = document.createElement('span');
  processSpan.className = 'fg-process';
  processSpan.textContent = cmdDisplay;
  container.appendChild(processSpan);

  return container;
}

function createForegroundTitleNode(
  fgInfo: {
    cwd?: string | null;
    name?: string | null;
    commandLine?: string | null;
    displayName?: string | null;
  },
  sessionId: string,
): HTMLElement {
  if (fgInfo.name && fgInfo.name !== 'shell' && !isShellProcess(fgInfo.name, sessionId)) {
    const fgIndicator = createForegroundIndicator(
      fgInfo.cwd,
      fgInfo.commandLine,
      fgInfo.name,
      fgInfo.displayName,
    );
    fgIndicator.classList.add('process-title');
    return fgIndicator;
  }

  if (fgInfo.cwd) {
    const cwdSpan = document.createElement('span');
    cwdSpan.className = 'session-foreground process-title';
    const cwdInner = document.createElement('span');
    cwdInner.className = 'fg-cwd';
    cwdInner.textContent = fgInfo.cwd;
    cwdSpan.appendChild(cwdInner);
    cwdSpan.title = fgInfo.cwd;
    return cwdSpan;
  }

  const title = document.createElement('span');
  title.className = 'session-title truncate';
  title.textContent = getProcessTitleFallback(sessionId);
  return title;
}

function createForegroundProcessInfoNode(
  fgInfo: {
    cwd?: string | null;
    name?: string | null;
    commandLine?: string | null;
    displayName?: string | null;
  },
  sessionId: string,
): HTMLElement | null {
  if (fgInfo.name && fgInfo.name !== 'shell' && !isShellProcess(fgInfo.name, sessionId)) {
    return createForegroundIndicator(
      fgInfo.cwd,
      fgInfo.commandLine,
      fgInfo.name,
      fgInfo.displayName,
    );
  }

  if (!fgInfo.cwd) {
    return null;
  }

  const cwdSpan = document.createElement('span');
  cwdSpan.className = 'session-foreground';
  const cwdInner = document.createElement('span');
  cwdInner.className = 'fg-cwd';
  cwdInner.textContent = fgInfo.cwd;
  cwdSpan.appendChild(cwdInner);
  cwdSpan.title = fgInfo.cwd;
  return cwdSpan;
}

function buildForegroundSignature(
  sessionId: string,
  mode: 'title' | 'row',
  fgInfo: {
    cwd?: string | null;
    name?: string | null;
    commandLine?: string | null;
    displayName?: string | null;
  },
): string {
  return [
    mode,
    fgInfo.cwd ?? '',
    fgInfo.name ?? '',
    fgInfo.commandLine ?? '',
    fgInfo.displayName ?? '',
    mode === 'title' ? getProcessTitleFallback(sessionId) : '',
  ].join('\u001f');
}

function syncForegroundHost(
  host: HTMLElement,
  sessionId: string,
  mode: 'title' | 'row',
  fgInfo = getForegroundInfo(sessionId),
): void {
  const signature = buildForegroundSignature(sessionId, mode, fgInfo);
  if (host.dataset.foregroundSignature === signature) {
    return;
  }

  host.dataset.foregroundSignature = signature;
  if (mode === 'title') {
    host.replaceChildren(createForegroundTitleNode(fgInfo, sessionId));
    return;
  }

  const content = createForegroundProcessInfoNode(fgInfo, sessionId);
  if (content) {
    host.replaceChildren(content);
  } else {
    host.replaceChildren();
  }
}

/**
 * Render pinned/unpinned state on a sidebar pin button.
 */
export function applyPinButtonState(pinBtn: HTMLButtonElement, isPinned: boolean): void {
  const label = t('session.pinToQuickLaunch');
  pinBtn.classList.toggle('pinned', isPinned);
  setActionButtonContent(pinBtn, label, isPinned ? '\u2605' : '\u2606', true);
  pinBtn.setAttribute('aria-pressed', isPinned ? 'true' : 'false');
  if (!isPinned) {
    pinBtn.classList.remove('save-success');
  }
}

/**
 * Update process info display for a specific session
 */
function updateSessionProcessInfo(sessionId: string): void {
  const sessionItem = document.querySelector<HTMLElement>(
    `.session-item[data-session-id="${sessionId}"]`,
  );
  if (!sessionItem) return;

  // Unnamed sessions: update the title row directly
  if (sessionItem.dataset.processAsTitle === '1') {
    const titleHost = sessionItem.querySelector<HTMLElement>('[data-foreground-host="title"]');
    if (!titleHost) return;
    syncForegroundHost(titleHost, sessionId, 'title');
    return;
  }

  // Named sessions: update the process info row
  const processInfoEl = sessionItem.querySelector<HTMLElement>('.session-process-info');
  if (!processInfoEl) return;
  syncForegroundHost(processInfoEl, sessionId, 'row');
}

// =============================================================================
// Mobile Action Menu
// =============================================================================

/**
 * Close any open mobile action menus
 */
export function closeMobileActionMenu(): void {
  document.querySelectorAll<HTMLElement>('.session-item.menu-open').forEach((el) => {
    el.classList.remove('menu-open');
    el.classList.remove('menu-open-up');

    const actions = el.querySelector<HTMLElement>('.session-actions');
    if (actions) {
      actions.style.removeProperty('left');
      actions.style.removeProperty('top');
      actions.style.removeProperty('max-height');
    }

    const menuBtn = el.querySelector<HTMLButtonElement>('.session-menu-btn');
    menuBtn?.setAttribute('aria-expanded', 'false');
  });
  if (mobileActionBackdrop) {
    mobileActionBackdrop.remove();
    mobileActionBackdrop = null;
  }
}

/**
 * Show backdrop for mobile action menu
 */
function showMobileBackdrop(): void {
  if (mobileActionBackdrop) return;
  mobileActionBackdrop = document.createElement('div');
  mobileActionBackdrop.className = 'session-action-backdrop';
  mobileActionBackdrop.addEventListener('click', () => {
    closeMobileActionMenu();
  });
  document.body.appendChild(mobileActionBackdrop);
}

/**
 * Position the mobile dropdown next to its trigger while keeping it on-screen.
 */
function positionMobileActionMenu(item: HTMLElement): void {
  if (!isMobileSessionMenuEnabled()) {
    return;
  }

  const actions = item.querySelector<HTMLElement>('.session-actions');
  const menuBtn = item.querySelector<HTMLElement>('.session-menu-btn');
  if (!actions || !menuBtn) {
    return;
  }

  const viewportPadding = 12;
  const gap = 8;
  const triggerRect = menuBtn.getBoundingClientRect();

  item.classList.remove('menu-open-up');
  actions.style.removeProperty('max-height');

  const initialRect = actions.getBoundingClientRect();
  const availableBelow = window.innerHeight - triggerRect.bottom - viewportPadding - gap;
  const availableAbove = triggerRect.top - viewportPadding - gap;
  const openUp =
    availableBelow < Math.min(initialRect.height, 220) && availableAbove > availableBelow;

  item.classList.toggle('menu-open-up', openUp);

  const heightBudget = Math.max(
    96,
    Math.min(openUp ? availableAbove : availableBelow, window.innerHeight - viewportPadding * 2),
  );
  actions.style.maxHeight = `${heightBudget}px`;

  const menuRect = actions.getBoundingClientRect();
  const menuHeight = Math.min(menuRect.height, heightBudget);
  const menuWidth = menuRect.width;

  let left = triggerRect.right - menuWidth;
  left = Math.max(viewportPadding, Math.min(left, window.innerWidth - viewportPadding - menuWidth));

  let top = triggerRect.bottom + gap;
  if (openUp) {
    top = Math.max(viewportPadding, triggerRect.top - menuHeight - gap);
  } else {
    top = Math.min(top, window.innerHeight - viewportPadding - menuHeight);
  }

  actions.style.left = `${left}px`;
  actions.style.top = `${top}px`;
}

// =============================================================================
// Session Display
// =============================================================================

interface SessionDisplayInfo {
  primary: string;
  secondary: string | null;
  useProcessAsTitle?: boolean;
}

/**
 * Get display info for a session (primary title and optional secondary subtitle)
 */
export function getSessionDisplayInfo(session: Session): SessionDisplayInfo {
  const sessionSurfaceLabel = getPrimarySurfaceLabel(session);
  const termTitle = isAgentSurfaceSession(session)
    ? sessionSurfaceLabel
    : session.terminalTitle || session.shellType || t('session.terminal');
  if (session.name) {
    return { primary: session.name, secondary: termTitle };
  }
  if (isAgentSurfaceSession(session)) {
    return { primary: termTitle, secondary: null };
  }
  // Process set a console title — show it as the primary title with process info below
  if (session.terminalTitle && !isShellProcess(session.terminalTitle, session.id)) {
    return { primary: session.terminalTitle, secondary: null };
  }
  // No name, no console title: show cwd + process as the title row
  return { primary: termTitle, secondary: null, useProcessAsTitle: true };
}

/**
 * Get the display name for a session (primary title only, for mobile/island)
 */
export function getSessionDisplayName(session: Session): string {
  const info = getSessionDisplayInfo(session);
  return info.primary;
}

function isSessionGroupCollapsed(group: SessionControlMode): boolean {
  return localStorage.getItem(SESSION_GROUP_STORAGE_KEYS[group]) === 'true';
}

function toggleSessionGroup(section: HTMLElement, group: SessionControlMode): void {
  const collapsed = section.classList.toggle('collapsed');
  localStorage.setItem(SESSION_GROUP_STORAGE_KEYS[group], String(collapsed));
}

// =============================================================================
// Rendering
// =============================================================================

/**
 * Get sidebar display order.
 * Keeps all sessions currently in the active layout contiguous and in layout-tree order.
 */
function getSidebarDisplaySessions(): Session[] {
  const sessions = $sessionList.get();
  if (sessions.length <= 1 || !isLayoutActive()) {
    return sessions;
  }

  const layoutIds = getLayoutSessionIds();
  if (layoutIds.length < 2) {
    return sessions;
  }

  const sessionsById = new Map<string, Session>(sessions.map((s) => [s.id, s]));
  const layoutIdSet = new Set(layoutIds);

  const groupedLayoutSessions: Session[] = [];
  for (const id of layoutIds) {
    const session = sessionsById.get(id);
    if (session) {
      groupedLayoutSessions.push(session);
    }
  }

  if (groupedLayoutSessions.length < 2) {
    return sessions;
  }

  const firstLayoutIndex = sessions.findIndex((s) => layoutIdSet.has(s.id));
  if (firstLayoutIndex < 0) {
    return sessions;
  }

  const nonLayoutSessions = sessions.filter((s) => !layoutIdSet.has(s.id));
  const nonLayoutBeforeAnchorCount = sessions
    .slice(0, firstLayoutIndex)
    .filter((s) => !layoutIdSet.has(s.id)).length;

  return [
    ...nonLayoutSessions.slice(0, nonLayoutBeforeAnchorCount),
    ...groupedLayoutSessions,
    ...nonLayoutSessions.slice(nonLayoutBeforeAnchorCount),
  ];
}

function createSessionFilterEmptyState(): HTMLDivElement {
  const emptyState = document.createElement('div');
  emptyState.className = 'session-filter-empty';
  emptyState.textContent = t('sidebar.noMatchingTerminals');
  return emptyState;
}

function buildSessionItemClassName(
  session: Session,
  isActive: boolean,
  isPending: boolean,
  inLayout: boolean,
  isChild: boolean,
  controlMode: SessionControlMode,
  supervisorState: string | null,
): string {
  return (
    'session-item' +
    (isActive ? ' active' : '') +
    (isPending ? ' pending' : '') +
    (inLayout ? ' in-layout' : '') +
    (isChild ? ' tmux-child' : '') +
    (controlMode === 'agent' ? ' agent-controlled' : '') +
    (needsAttention(session) ? ' needs-attention' : '') +
    (supervisorState ? ` supervisor-${supervisorState}` : '')
  );
}

function bindSessionItemSelection(
  item: HTMLDivElement,
  sessionId: string,
  isPending: boolean,
): void {
  if (isPending) {
    return;
  }

  let lastImmediateSelectionAt = 0;
  const selectSession = () => {
    closeMobileActionMenu();
    if (callbacks && sessionId) {
      callbacks.onSelect(sessionId);
      callbacks.onCloseSidebar();
    }
  };

  item.addEventListener('pointerdown', (event) => {
    const target = event.target as HTMLElement | null;
    if (
      event.button !== 0 ||
      event.pointerType === 'touch' ||
      target?.closest(
        'button, a, input, select, textarea, [role="menu"], [role="menuitem"], .session-actions',
      )
    ) {
      return;
    }

    lastImmediateSelectionAt = Date.now();
    selectSession();
  });

  item.addEventListener('click', (event) => {
    const target = event.target as HTMLElement | null;
    if (
      target?.closest(
        'button, a, input, select, textarea, [role="menu"], [role="menuitem"], .session-actions',
      )
    ) {
      return;
    }

    if (Date.now() - lastImmediateSelectionAt < 750) {
      return;
    }

    selectSession();
  });
}

function appendSessionTitleContent(
  item: HTMLDivElement,
  titleRow: HTMLDivElement,
  _session: Session,
  sessionId: string,
  displayInfo: ReturnType<typeof getSessionDisplayInfo>,
  controlMode: SessionControlMode,
  stateBadge: HTMLSpanElement,
  supervisorBadgeLabel: string | null,
  agentBadge: HTMLSpanElement,
  layoutBadge: HTMLSpanElement,
): void {
  if (displayInfo.useProcessAsTitle) {
    item.dataset.processAsTitle = '1';
    const titleHost = document.createElement('span');
    titleHost.dataset.foregroundHost = 'title';
    syncForegroundHost(titleHost, sessionId, 'title');
    titleRow.appendChild(titleHost);
    appendSessionTitleBadges(titleRow, controlMode, supervisorBadgeLabel, agentBadge, stateBadge);
    titleRow.appendChild(layoutBadge);
    return;
  }

  const title = document.createElement('span');
  title.className = 'session-title truncate';
  title.textContent = displayInfo.primary;
  titleRow.appendChild(title);
  appendSessionTitleBadges(titleRow, controlMode, supervisorBadgeLabel, agentBadge, stateBadge);
  titleRow.appendChild(layoutBadge);

  if (displayInfo.secondary) {
    item.classList.add('two-line');
    const subtitle = document.createElement('span');
    subtitle.className = 'session-subtitle truncate';
    subtitle.textContent = displayInfo.secondary;
    titleRow.appendChild(subtitle);
  }
}

function appendSessionTitleBadges(
  titleRow: HTMLDivElement,
  controlMode: SessionControlMode,
  supervisorBadgeLabel: string | null,
  agentBadge: HTMLSpanElement,
  stateBadge: HTMLSpanElement,
): void {
  if (controlMode !== 'agent') {
    return;
  }

  titleRow.appendChild(agentBadge);
  if (supervisorBadgeLabel) {
    titleRow.appendChild(stateBadge);
  }
}

function populateSessionProcessInfo(
  processInfo: HTMLDivElement,
  displayInfo: ReturnType<typeof getSessionDisplayInfo>,
  sessionId: string,
): void {
  if (displayInfo.useProcessAsTitle) {
    return;
  }
  syncForegroundHost(processInfo, sessionId, 'row');
}

function appendSessionActionButton(
  actions: HTMLDivElement,
  className: string,
  label: string,
  iconMarkup: string,
  sessionId: string,
  handler: (sessionId: string) => void,
): void {
  const button = document.createElement('button');
  button.className = className;
  setActionButtonContent(button, label, iconMarkup);
  button.setAttribute('role', 'menuitem');
  button.addEventListener('click', (event) => {
    event.stopPropagation();
    closeMobileActionMenu();
    handler(sessionId);
  });
  actions.appendChild(button);
}

function appendSessionActions(
  actions: HTMLDivElement,
  sessionId: string,
  isPending: boolean,
  isRemoteSession: boolean,
  controlMode: SessionControlMode,
): void {
  if (isPending || !sessionId) {
    return;
  }

  if (!isRemoteSession && shouldShowAgentControlAction(controlMode)) {
    const controlBtn = document.createElement('button');
    controlBtn.className = 'session-control';
    controlBtn.classList.add('active');
    setActionButtonContent(controlBtn, t('session.markHumanControlled'), 'AI', true);
    controlBtn.setAttribute('role', 'menuitem');
    controlBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      closeMobileActionMenu();
      callbacks?.onToggleAgentControl(sessionId);
    });
    actions.appendChild(controlBtn);
  }

  if (!isRemoteSession) {
    appendSessionActionButton(
      actions,
      'session-inject',
      t('session.injectGuidance'),
      icon('inject'),
      sessionId,
      (id) => callbacks?.onEnableMidtermFeatures?.(id),
    );
    appendSessionActionButton(
      actions,
      'session-undock',
      t('session.removeFromLayout'),
      icon('undock'),
      sessionId,
      undockSession,
    );
  }

  appendSessionActionButton(
    actions,
    'session-rename',
    t('session.rename'),
    icon('rename'),
    sessionId,
    (id) => callbacks?.onRename(id),
  );
  appendSessionActionButton(
    actions,
    'session-close',
    t('session.close'),
    icon('close'),
    sessionId,
    (id) => callbacks?.onDelete(id),
  );
}

function appendSessionMenuButton(
  item: HTMLDivElement,
  actions: HTMLDivElement,
  isPending: boolean,
): void {
  if (isPending) {
    return;
  }

  const menuBtn = document.createElement('button');
  menuBtn.className = 'session-menu-btn';
  menuBtn.innerHTML = icon('menu');
  menuBtn.title = t('session.actions');
  menuBtn.setAttribute('aria-label', t('session.actions'));
  menuBtn.setAttribute('aria-haspopup', 'menu');
  menuBtn.setAttribute('aria-controls', actions.id);
  menuBtn.setAttribute('aria-expanded', 'false');
  menuBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    if (!isMobileSessionMenuEnabled()) {
      return;
    }

    const isOpen = item.classList.contains('menu-open');
    closeMobileActionMenu();
    if (!isOpen) {
      item.classList.add('menu-open');
      menuBtn.setAttribute('aria-expanded', 'true');
      showMobileBackdrop();
      requestAnimationFrame(() => {
        positionMobileActionMenu(item);
      });
    }
  });
  item.appendChild(menuBtn);
}

/**
 * Create a session item DOM element
 */
function createSessionItem(
  session: Session,
  isActive: boolean,
  isPending: boolean,
): HTMLDivElement {
  const sessionId = session.id;
  const isRemoteSession = isHubSessionId(sessionId);
  const inLayout = isSessionInLayout(sessionId);
  const isChild = isChildSession(sessionId);
  const controlMode = getSessionControlMode(session);
  const supervisorState = getSupervisorState(session);
  const item = document.createElement('div');
  item.className = buildSessionItemClassName(
    session,
    isActive,
    isPending,
    inLayout,
    isChild,
    controlMode,
    supervisorState,
  );
  item.dataset.sessionId = sessionId;
  item.dataset.controlMode = controlMode;
  if (isChild) {
    item.dataset.parentId = session.parentSessionId ?? '';
  }
  item.setAttribute('aria-current', isActive ? 'true' : 'false');
  item.draggable = !isPending && !isChild && !isSessionFilterActive();
  bindSessionItemSelection(item, sessionId, isPending);

  const info = document.createElement('div');
  info.className = 'session-info';

  if (isPending) {
    const spinner = document.createElement('span');
    spinner.className = 'session-spinner';
    info.appendChild(spinner);
  }

  const displayInfo = getSessionDisplayInfo(session);

  const titleRow = document.createElement('div');
  titleRow.className = 'session-title-row';

  // Layout badge (shown when session is in layout)
  const layoutBadge = document.createElement('span');
  layoutBadge.className = 'layout-badge';
  layoutBadge.textContent = t('session.split');
  layoutBadge.title = t('session.splitTooltip');

  const agentBadge = document.createElement('span');
  agentBadge.className = 'session-role-badge';
  agentBadge.textContent = 'AI';
  agentBadge.title = t('sidebar.agentControlled');

  const supervisorBadgeLabel = getSupervisorBadgeLabel(session);
  const stateBadge = document.createElement('span');
  stateBadge.className = 'session-state-badge';
  stateBadge.textContent = supervisorBadgeLabel ?? '';
  stateBadge.hidden = supervisorBadgeLabel === null;
  if (session.supervisor?.attentionReason) {
    stateBadge.title = session.supervisor.attentionReason;
  }

  appendSessionTitleContent(
    item,
    titleRow,
    session,
    sessionId,
    displayInfo,
    controlMode,
    stateBadge,
    supervisorBadgeLabel,
    agentBadge,
    layoutBadge,
  );

  info.appendChild(titleRow);

  // Process indicator container (used for named sessions, empty for unnamed)
  const processInfo = document.createElement('div');
  processInfo.className = 'session-process-info';
  processInfo.dataset.sessionId = sessionId;
  processInfo.dataset.foregroundHost = 'row';

  populateSessionProcessInfo(processInfo, displayInfo, sessionId);

  // Always add processInfo container so updateSessionProcessInfo can find it later
  info.appendChild(processInfo);

  const actions = document.createElement('div');
  actions.className = 'session-actions';
  actions.id = `session-actions-${sessionId}`;
  actions.setAttribute('role', 'menu');

  appendSessionActions(actions, sessionId, isPending, isRemoteSession, controlMode);

  // Heat indicator strip (left edge, driven by server-side session telemetry)
  const heatIndicator = document.createElement('div');
  heatIndicator.className = 'heat-canvas';
  registerHeatCanvas(sessionId, heatIndicator);
  item.prepend(heatIndicator);

  item.appendChild(info);

  appendSessionMenuButton(item, actions, isPending);

  item.appendChild(actions);
  return item;
}

/**
 * Create a collapsible session group section.
 */
function createSessionGroupSection(group: SessionGroup): HTMLDivElement {
  const section = document.createElement('div');
  section.className = `session-group session-group-${group.key}`;
  if (group.showHeader && group.collapsed) {
    section.classList.add('collapsed');
  }

  if (group.showHeader) {
    const toggle = document.createElement('button');
    toggle.className = 'session-group-toggle';
    toggle.type = 'button';
    toggle.setAttribute('aria-expanded', group.collapsed ? 'false' : 'true');
    toggle.addEventListener('click', () => {
      toggleSessionGroup(section, group.key);
      toggle.setAttribute(
        'aria-expanded',
        section.classList.contains('collapsed') ? 'false' : 'true',
      );
    });

    const caret = document.createElement('span');
    caret.className = 'session-group-caret';
    caret.textContent = '▾';

    const label = document.createElement('span');
    label.className = 'session-group-label';
    label.textContent = group.label;

    const count = document.createElement('span');
    count.className = 'session-group-count';
    count.textContent = String(group.sessions.length);

    toggle.append(caret, label, count);
    if (group.key === 'agent' && group.attentionCount > 0) {
      const attention = document.createElement('span');
      attention.className = 'session-group-attention';
      attention.textContent = `${group.attentionCount} !`;
      toggle.appendChild(attention);
    }
    section.appendChild(toggle);
  } else {
    section.classList.add('session-group-flat');
  }

  const items = document.createElement('div');
  items.className = 'session-group-items';
  group.sessions.forEach((session) => {
    items.appendChild(
      createSessionItem(
        session,
        session.id === $activeSessionId.get(),
        pendingSessions.has(session.id),
      ),
    );
  });

  section.appendChild(items);
  return section;
}

function createHubMachineSection(
  machine: ReturnType<typeof getHubSidebarSections>[number],
): HTMLDivElement {
  const section = document.createElement('div');
  section.className = 'session-group session-group-hub-machine';
  if (isHubMachineCollapsed(machine.machine.machine.id)) {
    section.classList.add('collapsed');
  }

  const toggle = document.createElement('button');
  toggle.className = 'session-group-toggle';
  toggle.type = 'button';
  toggle.setAttribute('aria-expanded', section.classList.contains('collapsed') ? 'false' : 'true');
  toggle.addEventListener('click', () => {
    toggleHubMachineCollapsed(section, machine.machine.machine.id);
    toggle.setAttribute(
      'aria-expanded',
      section.classList.contains('collapsed') ? 'false' : 'true',
    );
  });

  const caret = document.createElement('span');
  caret.className = 'session-group-caret';
  caret.textContent = '▾';

  const label = document.createElement('span');
  label.className = 'session-group-label';
  label.textContent = machine.machine.machine.name;

  const count = document.createElement('span');
  count.className = 'session-group-count';
  count.textContent = String(machine.sessions.length);

  toggle.append(caret, label, count);
  section.appendChild(toggle);

  const items = document.createElement('div');
  items.className = 'session-group-items';
  machine.sessions.forEach((session) => {
    items.appendChild(createSessionItem(session, session.id === $activeSessionId.get(), false));
  });
  section.appendChild(items);
  return section;
}

function applySidebarGroupingClasses(sessionList: HTMLElement): void {
  sessionList.querySelectorAll<HTMLElement>('.session-group-items').forEach((groupItems) => {
    const allItems = groupItems.querySelectorAll<HTMLElement>('.session-item');
    allItems.forEach((item) => {
      item.classList.remove('tmux-last-child');
      item.classList.remove(
        'layout-group-start',
        'layout-group-middle',
        'layout-group-end',
        'layout-group-single',
      );
    });

    allItems.forEach((item, idx) => {
      if (!item.classList.contains('tmux-child')) {
        return;
      }

      const nextItem = allItems[idx + 1];
      if (
        !nextItem ||
        !nextItem.classList.contains('tmux-child') ||
        nextItem.dataset.parentId !== item.dataset.parentId
      ) {
        item.classList.add('tmux-last-child');
      }
    });

    allItems.forEach((item, idx) => {
      if (!item.classList.contains('in-layout')) return;

      const prev = allItems[idx - 1];
      const next = allItems[idx + 1];
      const prevInLayout = !!prev?.classList.contains('in-layout');
      const nextInLayout = !!next?.classList.contains('in-layout');

      if (!prevInLayout && !nextInLayout) {
        item.classList.add('layout-group-single');
      } else if (!prevInLayout) {
        item.classList.add('layout-group-start');
      } else if (!nextInLayout) {
        item.classList.add('layout-group-end');
      } else {
        item.classList.add('layout-group-middle');
      }
    });
  });
}

/**
 * Render the session list in the sidebar.
 */
export function renderSessionList(): void {
  if (!dom.sessionList) return;

  const sessionList = dom.sessionList;
  const displaySessions = getSidebarDisplaySessions();
  const filteredSessions = filterSessionsByQuery(
    displaySessions,
    sessionFilterController.isEnabled() ? sessionFilterController.getFilterValue() : '',
    getForegroundInfo,
  );
  const groups = groupSessionsByController(filteredSessions, {
    humanLabel: t('sidebar.humanControlled'),
    agentLabel: t('sidebar.agentControlled'),
    isCollapsed: isSessionGroupCollapsed,
  });
  const hubSections = getHubSidebarSections();
  pruneHeatSessions([
    ...displaySessions.map((session) => session.id),
    ...hubSections.flatMap((machine) => machine.sessions.map((session) => session.id)),
  ]);

  closeMobileActionMenu();
  sessionList.querySelectorAll<HTMLElement>('.session-item').forEach((item) => {
    const sessionId = item.dataset.sessionId;
    if (sessionId) {
      unregisterHeatCanvas(sessionId);
    }
  });

  sessionList.replaceChildren();
  sessionList.classList.toggle('filter-active', isSessionFilterActive());

  if (groups.length === 0 && displaySessions.length > 0 && isSessionFilterActive()) {
    sessionList.appendChild(createSessionFilterEmptyState());
  } else {
    groups.forEach((group) => {
      sessionList.appendChild(createSessionGroupSection(group));
    });
    hubSections.forEach((machine) => {
      if (machine.sessions.length > 0) {
        sessionList.appendChild(createHubMachineSection(machine));
      }
    });
  }

  applySidebarGroupingClasses(sessionList);
  syncSessionItemActiveStates(sessionList, $activeSessionId.get());
}

/**
 * Update the empty state visibility based on session count
 */
export function updateEmptyState(): void {
  if (!dom.emptyState) return;

  const isSettingsOpen = $settingsOpen.get();
  const sessions = $sessionList.get();
  const hasHubSessions = getHubSidebarSections().some((machine) => machine.sessions.length > 0);
  if (sessions.length === 0 && !hasHubSessions) {
    // Only show empty state if settings panel is not open
    if (!isSettingsOpen) {
      dom.emptyState.classList.remove('hidden');
      if (dom.settingsView) dom.settingsView.classList.add('hidden');
    }
  } else if (!isSettingsOpen) {
    dom.emptyState.classList.add('hidden');
  }
}

/**
 * Update the mobile title bar with current session name.
 * Also updates the desktop collapsed title bar.
 */
export function updateMobileTitle(): void {
  if (!dom.mobileTitle) return;

  const sessions = $sessionList.get();
  const activeSessionId = $activeSessionId.get();
  const session =
    sessions.find((s) => s.id === activeSessionId) ??
    (activeSessionId ? getHubSession(activeSessionId) : null);
  dom.mobileTitle.textContent = session ? getSessionDisplayName(session) : 'MidTerm';

  if (dom.topbarActions) {
    if (session) {
      dom.topbarActions.classList.remove('no-terminal');
    } else {
      dom.topbarActions.classList.add('no-terminal');
    }
  }

  // Also update the desktop collapsed title bar
  updateTitleBar(session ?? undefined);
}

/**
 * Update the collapsed title bar with current session info
 */
function updateTitleBar(session: Session | undefined): void {
  if (!dom.titleBarCustom || !dom.titleBarTerminal || !dom.titleBarSeparator) return;

  if (!session) {
    dom.titleBarCustom.textContent = 'MidTerm';
    dom.titleBarTerminal.textContent = '';
    dom.titleBarSeparator.style.display = 'none';
    return;
  }

  const info = getSessionDisplayInfo(session);

  if (session.name) {
    dom.titleBarCustom.textContent = info.primary;
    dom.titleBarTerminal.textContent = info.secondary || '';
    dom.titleBarSeparator.style.display = info.secondary ? '' : 'none';
  } else {
    dom.titleBarCustom.textContent = info.primary;
    dom.titleBarTerminal.textContent = '';
    dom.titleBarSeparator.style.display = 'none';
  }
}
