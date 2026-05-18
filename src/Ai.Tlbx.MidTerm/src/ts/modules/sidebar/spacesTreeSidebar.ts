/* eslint-disable max-lines -- Existing large sidebar owner; keyed reconciliation keeps DOM identity without a broader module split. */
import type { LaunchEntry, Session, SpaceSummaryDto, SpaceWorkspaceDto } from '../../api/types';
import type { SessionSelectionOptions } from '../../sessionActions';
import {
  patchHistoryEntry,
  setSessionNotes as apiSetSessionNotes,
  type HistoryPatchRequest,
} from '../../api/client';
import { icon } from '../../constants';
import { dom } from '../../state';
import {
  $activeSessionId,
  $currentSettings,
  $sessionList,
  $settingsOpen,
  getSession,
  setSession,
} from '../../stores';
import { getLaunchableHubMachines, getHubSidebarSections } from '../hub/runtime';
import { t } from '../i18n';
import { addProcessStateListener, getForegroundInfo } from '../process';
import {
  createHubWorktree,
  createLocalWorktree,
  deleteHubSpace,
  deleteHubWorktree,
  deleteLocalSpace,
  deleteLocalWorktree,
  fetchHubSpaces,
  fetchLocalSpaces,
  initHubGit,
  initLocalGit,
  updateHubSpace,
  updateHubWorkspace,
  updateLocalSpace,
  updateLocalWorkspace,
} from '../spaces/spacesApi';
import { addGitRepoCacheListener } from '../git';
import { showCreateWorktreeDialog } from '../spaces/spacesDialogs';
import { launchSpaceWorkspace, type SpaceSurface } from '../spaces/runtime';
import { showAlert, showConfirm, showTextPrompt } from '../../utils/dialog';
import { reconcileKeyedChildren } from '../../utils/domReconcile';
import {
  createSessionFilterController,
  type SessionFilterControllerElements,
} from './sessionFilterController';
import { pruneHeatSessions, registerHeatCanvas, unregisterHeatCanvas } from './heatIndicator';
import {
  getChildWorkspaces,
  getRootWorkspace,
  isAdHocSession,
  shouldShowAdHocBookmarkAction,
} from './spacesTreeSidebarLogic';
import {
  getSessionDisplayInfo,
  getSessionDisplayName as getLegacySessionDisplayName,
} from './sessionList';
import { getSessionControlMode } from './sessionListLogic';
import { isSessionInLayout, undockSession } from '../layout/layoutStore';
import {
  syncSpacesTreeSidebarSessionProcessInfo,
  syncSpacesTreeSidebarSessionProcessInfoElement,
} from './spacesTreeSidebarProcessInfo';
import {
  appendWorkspaceBadges,
  createSpaceChevron,
  createTextSpan,
} from './spacesTreeSidebarElements';

export interface SessionListCallbacks {
  onSelect: (sessionId: string, options?: SessionSelectionOptions) => void;
  onDelete: (sessionId: string) => void;
  onRename: (sessionId: string) => void;
  onToggleAgentControl: (sessionId: string) => void;
  onPinToHistory: (sessionId: string) => void;
  onEnableMidtermFeatures?: (sessionId: string) => void;
  onCloseSidebar: () => void;
  onLaunchRecent?: (machineId: string | null, entry: LaunchEntry) => void;
}

interface SidebarSpaceSection {
  id: string;
  label: string;
  machineId: string | null;
  spaces: SpaceSummaryDto[];
}

interface SidebarSessionRef {
  id: string;
  machineId: string | null;
  session: Session;
}

interface PopoverAction {
  label: string;
  tone?: 'default' | 'danger';
  run: () => void | Promise<void>;
}

interface SidebarSessionNodeOptions {
  reorderScope?: string | null;
}

type SidebarRootItem =
  | { kind: 'adhoc'; sessions: SidebarSessionRef[] }
  | { kind: 'section'; section: SidebarSpaceSection };

interface SidebarSpaceNodeContext {
  machineId: string | null;
  space: SpaceSummaryDto;
}

interface SidebarWorkspaceNodeContext {
  machineId: string | null;
  space: SpaceSummaryDto;
  workspace: SpaceWorkspaceDto;
}

const spaceNodeContexts = new WeakMap<HTMLElement, SidebarSpaceNodeContext>();
const workspaceNodeContexts = new WeakMap<HTMLElement, SidebarWorkspaceNodeContext>();

let callbacks: SessionListCallbacks | null = null;
let cachedSections: SidebarSpaceSection[] = [];
let loadPromise: Promise<void> | null = null;
let lastLoadedAt = 0;
let loadToken = 0;
let queuedRenderFrameId: number | null = null;
const queuedProcessInfoSessionIds = new Set<string>();
let actionPopoverEl: HTMLDivElement | null = null;
let chooserPopoverEl: HTMLDivElement | null = null;
const expandedNotesSessionIds = new Set<string>();
const pendingNoteSaveTimers = new Map<string, number>();

const SESSION_FILTER_STORAGE_KEY = 'midterm.sidebar.sessionFilter';
const SPACE_EXPANDED_PREFIX = 'midterm.sidebar.spaceExpanded.';
const TREE_TTL_MS = 15_000;
const SESSION_NOTES_SAVE_DELAY_MS = 350;
const SESSION_NOTES_MAX_LINES = 5;
const SESSION_NOTES_MAX_CHARS = 600;

export function getSessionDisplayName(session: Session): string {
  return getLegacySessionDisplayName(session);
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
    // Ignore localStorage failures.
  }
}

function isSidebarSessionFilterEnabled(): boolean {
  return $currentSettings.get()?.showSidebarSessionFilter === true;
}

const sessionFilterController = createSessionFilterController({
  getElements: () =>
    ({
      filterBar: dom.sessionFilterBar,
      filterInput: dom.sessionFilterInput,
      filterClear: dom.sessionFilterClear,
    }) satisfies SessionFilterControllerElements,
  isEnabled: isSidebarSessionFilterEnabled,
  areSettingsLoaded: () => $currentSettings.get() !== null,
  loadStoredFilter: loadStoredSessionFilter,
  persistFilter: persistSessionFilter,
  render: () => {
    renderSessionList();
    updateEmptyState();
  },
  translate: t,
});

export function initializeSessionList(): void {
  ensurePopovers();
  addProcessStateListener(queueSidebarSessionProcessInfoUpdate);
  addGitRepoCacheListener(queueSidebarSessionProcessInfoUpdate);
  sessionFilterController.initialize();
  syncSearchControls();
  document.addEventListener('click', handleGlobalPopoverClick);
  window.addEventListener('resize', closePopovers);
  window.addEventListener('orientationchange', closePopovers);
  void refreshSidebarSpacesTree(true);
}

export function setSessionListCallbacks(nextCallbacks: SessionListCallbacks): void {
  callbacks = nextCallbacks;
}

export function invalidateSidebarSpacesTree(): void {
  lastLoadedAt = 0;
  void refreshSidebarSpacesTree(true);
}

export function applySessionFilterSettingChange(): void {
  sessionFilterController.applySettingChange();
  syncSearchControls();
}

export function isSessionFilterActive(): boolean {
  return sessionFilterController.isActive();
}

export function renderSessionList(): void {
  if (!dom.sessionList) {
    return;
  }

  if (shouldRefreshSidebarTree()) {
    void refreshSidebarSpacesTree();
  }

  closePopovers();
  renderSidebarTree();
}

export function updateEmptyState(): void {
  if (!dom.emptyState) {
    return;
  }

  if ($settingsOpen.get()) {
    dom.emptyState.classList.add('hidden');
    return;
  }

  const hasSpaces = getVisibleSpaceSections().some((section) => section.spaces.length > 0);
  const hasSessions = getAllSidebarSessions().length > 0;
  dom.emptyState.classList.toggle('hidden', hasSpaces || hasSessions);
}

export function updateMobileTitle(): void {
  if (!dom.mobileTitle) {
    return;
  }

  const activeSessionId = $activeSessionId.get();
  const activeSession = getAllSidebarSessions().find(
    (entry) => entry.id === activeSessionId,
  )?.session;
  dom.mobileTitle.textContent = activeSession ? getSessionDisplayName(activeSession) : 'MidTerm';
}

async function refreshSidebarSpacesTree(force = false): Promise<void> {
  if (!force && !shouldRefreshSidebarTree()) {
    return;
  }

  if (loadPromise) {
    return loadPromise;
  }

  const currentToken = ++loadToken;
  loadPromise = loadSidebarTreeData(currentToken).finally(() => {
    loadPromise = null;
  });
  return loadPromise;
}

function shouldRefreshSidebarTree(): boolean {
  if (loadPromise) {
    return false;
  }

  const machineIds = getLaunchableHubMachines()
    .map((machine) => machine.machine.id)
    .sort()
    .join('|');
  const cachedMachineIds = cachedSections
    .filter((section) => section.machineId)
    .map((section) => section.machineId)
    .filter((machineId): machineId is string => typeof machineId === 'string')
    .sort()
    .join('|');

  return (
    cachedSections.length === 0 ||
    Date.now() - lastLoadedAt > TREE_TTL_MS ||
    machineIds !== cachedMachineIds
  );
}

async function loadSidebarTreeData(token: number): Promise<void> {
  const machines = getLaunchableHubMachines();
  const [localSpaces, remoteSections] = await Promise.all([
    fetchLocalSpaces({ pinnedOnly: true }).catch(() => []),
    Promise.all(
      machines.map(async (machine) => ({
        id: machine.machine.id,
        label: machine.machine.name,
        machineId: machine.machine.id,
        spaces: await fetchHubSpaces(machine.machine.id, { pinnedOnly: true }).catch(() => []),
      })),
    ),
  ]);

  if (token !== loadToken) {
    return;
  }

  cachedSections = [
    {
      id: 'local',
      label: t('sessionLauncher.localTargetTitle'),
      machineId: null,
      spaces: localSpaces,
    },
    ...remoteSections,
  ];
  lastLoadedAt = Date.now();
  renderSidebarTree();
  updateEmptyState();
  updateMobileTitle();
}

function renderSidebarTree(): void {
  if (!dom.sessionList) {
    return;
  }

  const host = dom.sessionList;
  host.className = 'session-list spaces-sidebar-tree';
  pruneHeatSessions(getAllSidebarSessions().map((entry) => entry.id));
  reconcileKeyedChildren(host, getSidebarRootItems(), {
    key: getSidebarRootItemKey,
    create: createSidebarRootNode,
    patch: patchSidebarRootNode,
    destroy: destroySidebarTreeNode,
  });
}

function getSidebarRootItems(): SidebarRootItem[] {
  const items: SidebarRootItem[] = [];
  const adHocSessions = getAdHocSessions();
  if (adHocSessions.length > 0) {
    items.push({ kind: 'adhoc', sessions: adHocSessions });
  }

  items.push(
    ...getVisibleSpaceSections().map((section) => ({ kind: 'section' as const, section })),
  );
  return items;
}

function getSidebarRootItemKey(item: SidebarRootItem): string {
  return item.kind === 'adhoc' ? 'adhoc' : `target:${item.section.machineId ?? 'local'}`;
}

function getSpaceKey(machineId: string | null, space: SpaceSummaryDto): string {
  return `space:${getSpaceStorageKey(machineId, space.id)}`;
}

function getWorkspaceKey(
  machineId: string | null,
  space: SpaceSummaryDto,
  workspace: SpaceWorkspaceDto,
): string {
  const workspaceKey = workspace.key || normalizeOptionalPath(workspace.path) || workspace.path;
  return `workspace:${getSpaceStorageKey(machineId, space.id)}:${workspaceKey}`;
}

function getSessionKey(entry: SidebarSessionRef): string {
  return `session:${entry.id}`;
}

function createSidebarRootNode(item: SidebarRootItem): HTMLElement {
  return item.kind === 'adhoc' ? createAdHocSection() : createSpaceTargetSection();
}

function patchSidebarRootNode(node: HTMLElement, item: SidebarRootItem): void {
  if (item.kind === 'adhoc') {
    patchAdHocSection(node, item.sessions);
    return;
  }

  patchSpaceTargetSection(node, item.section);
}

function getVisibleSpaceSections(): SidebarSpaceSection[] {
  return cachedSections
    .map((section) => filterSection(section))
    .filter((section) => section.spaces.length > 0);
}

function filterSection(section: SidebarSpaceSection): SidebarSpaceSection {
  return {
    ...section,
    spaces: section.spaces
      .map((space) => filterSpace(section.machineId, space))
      .filter((space): space is SpaceSummaryDto => space !== null),
  };
}

function filterSpace(machineId: string | null, space: SpaceSummaryDto): SpaceSummaryDto | null {
  if (!space.isPinned) {
    return null;
  }

  const searchValue = getSearchValue();
  if (!searchValue || matchesSpaceSearch(space)) {
    return space;
  }

  const matchingWorkspaces = space.workspaces.filter((workspace) =>
    matchesWorkspaceSearch(machineId, space, workspace),
  );
  const matchingSessions = getSpaceSessions(machineId, space).some(matchesSidebarSessionSearch);
  if (!matchingSessions && matchingWorkspaces.length === 0) {
    return null;
  }

  return space;
}

function getAllSidebarSessions(): SidebarSessionRef[] {
  const localSessions = $sessionList.get().map((session) => ({
    id: session.id,
    machineId: null,
    session,
  }));
  const remoteSessions = getHubSidebarSections().flatMap((machine) =>
    machine.sessions.map((session) => ({
      id: session.id,
      machineId: machine.machine.machine.id,
      session,
    })),
  );
  return [...localSessions, ...remoteSessions];
}

function getAdHocSessions(): SidebarSessionRef[] {
  return getAllSidebarSessions()
    .filter((entry) => isAdHocSession(entry.session))
    .filter(matchesSidebarSessionSearch);
}

function createSpaceTargetSection(): HTMLElement {
  const wrapper = document.createElement('section');
  wrapper.className = 'spaces-tree-target';

  const header = document.createElement('div');
  header.className = 'spaces-tree-target-header';
  header.appendChild(createTextSpan('spaces-tree-target-label', ''));
  wrapper.appendChild(header);

  const list = document.createElement('div');
  list.className = 'spaces-tree-space-list';
  wrapper.appendChild(list);

  return wrapper;
}

function patchSpaceTargetSection(wrapper: HTMLElement, section: SidebarSpaceSection): void {
  wrapper.className = 'spaces-tree-target';
  const label = wrapper.querySelector<HTMLElement>('.spaces-tree-target-label');
  if (label && label.textContent !== section.label) {
    label.textContent = section.label;
  }

  const list = wrapper.querySelector<HTMLElement>('.spaces-tree-space-list');
  if (!list) {
    return;
  }

  reconcileKeyedChildren(list, section.spaces, {
    key: (space) => {
      return getSpaceKey(section.machineId, space);
    },
    create: createSpaceNode,
    patch: (node, space) => {
      patchSpaceNode(node, section.machineId, space);
    },
    destroy: destroySidebarTreeNode,
  });
}

function createSpaceNode(): HTMLElement {
  const node = document.createElement('article');
  node.className = 'spaces-tree-space';

  const header = document.createElement('button');
  header.type = 'button';
  header.className = 'spaces-tree-space-header';
  header.addEventListener('click', () => {
    const context = spaceNodeContexts.get(node);
    if (context) {
      toggleSpaceExpanded(node, context.machineId, context.space);
    }
  });

  const identity = document.createElement('div');
  identity.className = 'spaces-tree-space-identity';
  identity.appendChild(createTextSpan('spaces-tree-space-title', ''));
  header.appendChild(identity);

  const meta = document.createElement('div');
  meta.className = 'spaces-tree-space-meta';
  meta.appendChild(createSpaceChevron());
  header.appendChild(meta);

  const actions = document.createElement('div');
  actions.className = 'spaces-tree-space-actions';

  const addButton = document.createElement('button');
  addButton.type = 'button';
  addButton.className = 'spaces-tree-add spaces-tree-inline-action';
  addButton.title = t('spaces.newSession');
  addButton.setAttribute('aria-label', t('spaces.newSession'));
  addButton.textContent = '+';
  addButton.addEventListener('click', (event) => {
    event.stopPropagation();
    const context = spaceNodeContexts.get(node);
    const rootWorkspace = context ? getRootWorkspace(context.space) : null;
    if (context && rootWorkspace) {
      openSurfaceChooser(addButton, context.machineId, context.space, rootWorkspace);
    }
  });
  actions.appendChild(addButton);

  const forgetButton = document.createElement('button');
  forgetButton.type = 'button';
  forgetButton.className = 'spaces-tree-forget spaces-tree-inline-action';
  forgetButton.title = t('spaces.deleteSpace');
  forgetButton.setAttribute('aria-label', forgetButton.title);
  forgetButton.innerHTML = icon('close');
  forgetButton.addEventListener('click', (event) => {
    event.stopPropagation();
    const context = spaceNodeContexts.get(node);
    if (context) {
      void promptAndForgetSpace(context.machineId, context.space);
    }
  });
  actions.appendChild(forgetButton);

  const menuButton = document.createElement('button');
  menuButton.type = 'button';
  menuButton.className = 'spaces-tree-overflow spaces-tree-inline-action';
  menuButton.title = t('session.actions');
  menuButton.setAttribute('aria-label', t('session.actions'));
  menuButton.textContent = '⋯';
  menuButton.addEventListener('click', (event) => {
    event.stopPropagation();
    const context = spaceNodeContexts.get(node);
    if (context) {
      openActionPopover(menuButton, buildSpaceActions(context.machineId, context.space));
    }
  });
  actions.appendChild(menuButton);
  header.appendChild(actions);

  node.appendChild(header);

  return node;
}

function patchSpaceNode(node: HTMLElement, machineId: string | null, space: SpaceSummaryDto): void {
  spaceNodeContexts.set(node, { machineId, space });

  const rootWorkspace = getRootWorkspace(space);
  const expanded = isSpaceExpanded(machineId, space, getSpaceSessions(machineId, space).length > 0);
  node.classList.toggle('expanded', expanded);

  const header = node.querySelector<HTMLButtonElement>('.spaces-tree-space-header');
  if (header) {
    header.title = space.rootPath;
  }

  const title = node.querySelector<HTMLElement>('.spaces-tree-space-title');
  if (title && title.textContent !== space.displayName) {
    title.textContent = space.displayName;
  }

  const meta = node.querySelector<HTMLElement>('.spaces-tree-space-meta');
  if (meta) {
    patchSpaceMeta(meta, rootWorkspace);
  }

  const addButton = node.querySelector<HTMLButtonElement>('.spaces-tree-add');
  if (addButton) {
    addButton.hidden = !rootWorkspace;
    addButton.title = t('spaces.newSession');
    addButton.setAttribute('aria-label', t('spaces.newSession'));
  }

  const forgetButton = node.querySelector<HTMLButtonElement>('.spaces-tree-forget');
  if (forgetButton) {
    forgetButton.title = t('spaces.deleteSpace');
    forgetButton.setAttribute('aria-label', forgetButton.title);
  }

  const menuButton = node.querySelector<HTMLButtonElement>('.spaces-tree-overflow');
  if (menuButton) {
    menuButton.title = t('session.actions');
    menuButton.setAttribute('aria-label', t('session.actions'));
  }

  if (expanded) {
    patchSpaceNodeExpandedContent(node, machineId, space);
  } else {
    removeSpaceNodeExpandedContent(node);
  }
}

function patchSpaceMeta(meta: HTMLElement, rootWorkspace: SpaceWorkspaceDto | null): void {
  const signature = rootWorkspace
    ? [
        rootWorkspace.branch,
        rootWorkspace.isDetached,
        rootWorkspace.locked,
        rootWorkspace.prunable,
        rootWorkspace.hasChanges,
        rootWorkspace.changeCount,
      ].join('\u001f')
    : '';
  if (meta.dataset.workspaceBadgeSignature === signature) {
    return;
  }

  meta.dataset.workspaceBadgeSignature = signature;
  meta.replaceChildren();
  if (rootWorkspace) {
    appendWorkspaceBadges(meta, rootWorkspace);
  }
  meta.appendChild(createSpaceChevron());
}

function patchSpaceNodeExpandedContent(
  node: HTMLElement,
  machineId: string | null,
  space: SpaceSummaryDto,
): void {
  const rootWorkspace = getRootWorkspace(space);
  const rootSessions = getVisibleRootSessions(machineId, space, rootWorkspace);
  const childWorkspaces = getVisibleChildWorkspaces(machineId, space);

  const workspaceList = ensureDirectChildContainer(
    node,
    'spaces-tree-workspace-list',
    rootSessions.length > 0 ? null : node.children[1],
  );

  if (rootSessions.length > 0) {
    const rootSessionList = ensureDirectChildContainer(
      node,
      'spaces-tree-space-session-list',
      workspaceList,
    );
    reconcileSidebarSessions(rootSessionList, rootSessions);
  } else {
    removeDirectChildContainer(node, 'spaces-tree-space-session-list');
  }

  if (childWorkspaces.length > 0) {
    reconcileKeyedChildren(workspaceList, childWorkspaces, {
      key: (workspace) => {
        return getWorkspaceKey(machineId, space, workspace);
      },
      create: createWorkspaceNode,
      patch: (block, workspace) => {
        patchWorkspaceNode(block, machineId, space, workspace);
      },
      destroy: destroySidebarTreeNode,
    });
  } else {
    removeDirectChildContainer(node, 'spaces-tree-workspace-list');
  }
}

function createWorkspaceNode(): HTMLElement {
  const block = document.createElement('section');
  block.className = 'spaces-tree-workspace-block';

  const row = document.createElement('div');
  row.className = 'spaces-tree-workspace';

  const mainButton = document.createElement('button');
  mainButton.type = 'button';
  mainButton.className = 'spaces-tree-workspace-open';
  mainButton.addEventListener('click', () => {
    const context = workspaceNodeContexts.get(block);
    if (!context) {
      return;
    }

    const sessions = getWorkspaceSessions(
      context.machineId,
      context.space,
      context.workspace,
    ).filter(matchesSidebarSessionSearch);
    if (sessions.length > 0) {
      const activeSession =
        sessions.find((session) => session.id === $activeSessionId.get()) ?? sessions[0];
      if (!activeSession) {
        return;
      }
      callbacks?.onSelect(activeSession.id);
      callbacks?.onCloseSidebar();
    }
  });

  const line = document.createElement('div');
  line.className = 'spaces-tree-workspace-line';
  mainButton.appendChild(line);
  row.appendChild(mainButton);

  const actions = document.createElement('div');
  actions.className = 'spaces-tree-workspace-actions';

  const addButton = document.createElement('button');
  addButton.type = 'button';
  addButton.className = 'spaces-tree-add spaces-tree-inline-action';
  addButton.title = t('spaces.newSession');
  addButton.setAttribute('aria-label', t('spaces.newSession'));
  addButton.textContent = '+';
  addButton.addEventListener('click', (event) => {
    event.stopPropagation();
    const context = workspaceNodeContexts.get(block);
    if (context) {
      openSurfaceChooser(addButton, context.machineId, context.space, context.workspace);
    }
  });
  actions.appendChild(addButton);

  const menuButton = document.createElement('button');
  menuButton.type = 'button';
  menuButton.className = 'spaces-tree-overflow spaces-tree-inline-action';
  menuButton.title = t('session.actions');
  menuButton.setAttribute('aria-label', t('session.actions'));
  menuButton.textContent = '⋯';
  menuButton.addEventListener('click', (event) => {
    event.stopPropagation();
    const context = workspaceNodeContexts.get(block);
    if (!context) {
      return;
    }

    const sessions = getWorkspaceSessions(
      context.machineId,
      context.space,
      context.workspace,
    ).filter(matchesSidebarSessionSearch);
    openActionPopover(
      menuButton,
      buildWorkspaceActions(context.machineId, context.space, context.workspace, sessions),
    );
  });
  actions.appendChild(menuButton);

  row.appendChild(actions);
  block.appendChild(row);

  const sessionList = document.createElement('div');
  sessionList.className = 'spaces-tree-workspace-session-list';
  block.appendChild(sessionList);

  return block;
}

function patchWorkspaceNode(
  block: HTMLElement,
  machineId: string | null,
  space: SpaceSummaryDto,
  workspace: SpaceWorkspaceDto,
): void {
  workspaceNodeContexts.set(block, { machineId, space, workspace });

  const sessions = getWorkspaceSessions(machineId, space, workspace).filter(
    matchesSidebarSessionSearch,
  );

  const mainButton = block.querySelector<HTMLButtonElement>('.spaces-tree-workspace-open');
  if (mainButton) {
    mainButton.disabled = sessions.length === 0;
    mainButton.title = workspace.path;
  }

  const line = block.querySelector<HTMLElement>('.spaces-tree-workspace-line');
  if (line) {
    patchWorkspaceLine(line, workspace);
  }

  const addButton = block.querySelector<HTMLButtonElement>('.spaces-tree-add');
  if (addButton) {
    addButton.title = t('spaces.newSession');
    addButton.setAttribute('aria-label', t('spaces.newSession'));
  }

  const menuButton = block.querySelector<HTMLButtonElement>('.spaces-tree-overflow');
  if (menuButton) {
    const canManage = canManageWorkspace(space, workspace);
    menuButton.hidden = !canManage;
    menuButton.title = t('session.actions');
    menuButton.setAttribute('aria-label', t('session.actions'));
  }

  const sessionList = block.querySelector<HTMLElement>('.spaces-tree-workspace-session-list');
  if (sessions.length > 0) {
    if (sessionList) {
      sessionList.hidden = false;
      reconcileSidebarSessions(sessionList, sessions);
    }
  } else if (sessionList) {
    sessionList.hidden = true;
    reconcileSidebarSessions(sessionList, []);
  }
}

function patchWorkspaceLine(line: HTMLElement, workspace: SpaceWorkspaceDto): void {
  const signature = [
    workspace.displayName,
    workspace.branch,
    workspace.isDetached,
    workspace.locked,
    workspace.prunable,
    workspace.hasChanges,
    workspace.changeCount,
  ].join('\u001f');
  if (line.dataset.workspaceLineSignature === signature) {
    return;
  }

  line.dataset.workspaceLineSignature = signature;
  line.replaceChildren(createTextSpan('spaces-tree-workspace-name', workspace.displayName));
  appendWorkspaceBadges(line, workspace);
}

function getDirectChildContainer(parent: HTMLElement, className: string): HTMLElement | null {
  return (
    Array.from(parent.children).find(
      (child): child is HTMLElement =>
        child instanceof HTMLElement && child.classList.contains(className),
    ) ?? null
  );
}

function ensureDirectChildContainer(
  parent: HTMLElement,
  className: string,
  before: Element | null | undefined = null,
): HTMLElement {
  let container = getDirectChildContainer(parent, className);
  if (!container) {
    container = document.createElement('div');
    container.className = className;
  }

  if (container.parentElement !== parent || (before && before !== container)) {
    parent.insertBefore(container, before ?? null);
  }

  return container;
}

function removeDirectChildContainer(parent: HTMLElement, className: string): void {
  const container = getDirectChildContainer(parent, className);
  if (!container) {
    return;
  }

  destroySidebarTreeNode(container);
  container.remove();
}

function createSidebarSessionNode(
  entry: SidebarSessionRef,
  options: SidebarSessionNodeOptions = {},
): HTMLElement {
  const reorderScope = normalizeSidebarReorderScope(options.reorderScope);
  const isReorderable = canReorderSidebarSession(entry, reorderScope);
  const item = document.createElement('div');
  configureSidebarSessionNode(item, entry, reorderScope, isReorderable);
  item.addEventListener('click', () => {
    callbacks?.onSelect(entry.id);
    callbacks?.onCloseSidebar();
  });

  const heatIndicator = document.createElement('div');
  heatIndicator.className = 'heat-canvas';
  registerHeatCanvas(entry.id, heatIndicator);
  item.appendChild(heatIndicator);

  const info = document.createElement('div');
  info.className = 'session-info';
  const displayInfo = getSessionDisplayInfo(entry.session);

  const titleRow = document.createElement('div');
  titleRow.className = 'session-title-row';

  const title = document.createElement('div');
  title.className = 'session-title';
  title.textContent = displayInfo.primary;
  titleRow.appendChild(title);

  if (displayInfo.secondary) {
    const subtitle = document.createElement('div');
    subtitle.className = 'session-subtitle';
    subtitle.textContent = displayInfo.secondary;
    titleRow.appendChild(subtitle);
  }

  info.appendChild(titleRow);
  const topic = createSessionTopicElement(entry.session);
  if (topic) {
    info.appendChild(topic);
  }

  const processInfo = document.createElement('div');
  processInfo.className = 'session-process-info';
  syncSpacesTreeSidebarSessionProcessInfoElement(processInfo, entry);
  info.appendChild(processInfo);

  info.appendChild(createSessionNotesPane(entry));

  item.appendChild(info);
  item.appendChild(createSidebarSessionActions(entry));
  return item;
}

function patchSidebarSessionNode(
  item: HTMLElement,
  entry: SidebarSessionRef,
  options: SidebarSessionNodeOptions = {},
): void {
  const reorderScope = normalizeSidebarReorderScope(options.reorderScope);
  configureSidebarSessionNode(
    item,
    entry,
    reorderScope,
    canReorderSidebarSession(entry, reorderScope),
  );
  syncSidebarSessionItemDisplayText(item, entry.session);

  const processInfo = item.querySelector<HTMLElement>('.session-process-info');
  if (processInfo) {
    syncSpacesTreeSidebarSessionProcessInfoElement(processInfo, entry);
  }

  let notesPane = item.querySelector<HTMLDivElement>('.session-notes-pane');
  const info = item.querySelector<HTMLElement>('.session-info');
  if (!notesPane && info) {
    notesPane = createSessionNotesPane(entry);
    info.appendChild(notesPane);
  }
  if (notesPane) {
    syncSessionNotesPane(notesPane, entry);
  }

  const actions = item.querySelector<HTMLDivElement>('.session-actions');
  if (actions) {
    patchSidebarSessionActions(actions, entry);
  }
}

export function syncSidebarSessionProcessInfo(sessionId: string): boolean {
  const host = dom.sessionList;
  if (!host) {
    return false;
  }

  return syncSpacesTreeSidebarSessionProcessInfo(host, getAllSidebarSessions(), sessionId);
}

function reconcileSidebarSessions(
  container: HTMLElement,
  sessions: SidebarSessionRef[],
  options: SidebarSessionNodeOptions = {},
): void {
  reconcileKeyedChildren(container, sessions, {
    key: getSessionKey,
    create: (entry) => createSidebarSessionNode(entry, options),
    patch: (item, entry) => {
      patchSidebarSessionNode(item, entry, options);
    },
    destroy: destroySidebarSessionNode,
  });
}

function destroySidebarSessionNode(item: HTMLElement): void {
  const sessionId = item.dataset.sessionId;
  if (sessionId) {
    unregisterHeatCanvas(sessionId);
  }
}

function destroySidebarTreeNode(element: HTMLElement): void {
  if (element.matches('.session-item[data-session-id]')) {
    destroySidebarSessionNode(element);
  }
  element.querySelectorAll<HTMLElement>('.session-item[data-session-id]').forEach((item) => {
    destroySidebarSessionNode(item);
  });
}

function syncSidebarSessionItemDisplayText(item: HTMLElement, session: Session): boolean {
  const displayInfo = getSessionDisplayInfo(session);
  const title = item.querySelector<HTMLElement>('.session-title');
  const titleRow = item.querySelector<HTMLElement>('.session-title-row');
  if (!title || !titleRow) {
    return false;
  }

  if (title.textContent !== displayInfo.primary) {
    title.textContent = displayInfo.primary;
  }

  let subtitle = item.querySelector<HTMLElement>('.session-subtitle');
  if (displayInfo.secondary) {
    if (!subtitle) {
      subtitle = document.createElement('div');
      subtitle.className = 'session-subtitle';
      titleRow.appendChild(subtitle);
    }
    if (subtitle.textContent !== displayInfo.secondary) {
      subtitle.textContent = displayInfo.secondary;
    }
  } else {
    subtitle?.remove();
  }

  syncSidebarSessionTopic(item, session.topic);

  return true;
}

function createSessionTopicElement(session: Session): HTMLDivElement | null {
  const topic = normalizeSessionTopic(session.topic);
  if (!topic) {
    return null;
  }

  const element = document.createElement('div');
  element.className = 'session-topic';
  element.textContent = topic;
  element.title = topic;
  return element;
}

function syncSidebarSessionTopic(item: HTMLElement, value: string | null | undefined): void {
  const topic = normalizeSessionTopic(value);
  let element = item.querySelector<HTMLDivElement>('.session-topic');
  const info = item.querySelector<HTMLElement>('.session-info');

  if (!topic) {
    element?.remove();
    return;
  }

  if (!element && info) {
    element = document.createElement('div');
    element.className = 'session-topic';
    const processInfo = info.querySelector<HTMLElement>('.session-process-info');
    if (processInfo) {
      info.insertBefore(element, processInfo);
    } else {
      info.appendChild(element);
    }
  }

  if (!element) {
    return;
  }

  if (element.textContent !== topic) {
    element.textContent = topic;
  }
  if (element.title !== topic) {
    element.title = topic;
  }
}

function normalizeSessionTopic(value: string | null | undefined): string | null {
  const normalized = value?.trim();
  return normalized ? normalized : null;
}

function configureSidebarSessionNode(
  item: HTMLElement,
  entry: SidebarSessionRef,
  reorderScope: string,
  isReorderable: boolean,
): void {
  const isChild = !!entry.session.parentSessionId;
  const classNames = ['session-item', 'two-line', 'spaces-tree-session-item'];
  if (entry.id === $activeSessionId.get()) {
    classNames.push('active');
  }
  if (isReorderable) {
    classNames.push('spaces-tree-session-item-reorderable');
  }
  if (isChild) {
    classNames.push('tmux-child');
    item.dataset.parentId = entry.session.parentSessionId ?? '';
  } else {
    delete item.dataset.parentId;
  }
  if (isSessionInLayout(entry.id)) {
    classNames.push('in-layout');
  }

  item.className = classNames.join(' ');
  item.dataset.sessionId = entry.id;
  item.dataset.controlMode = getSessionControlMode(entry.session);
  if (reorderScope) {
    item.dataset.reorderScope = reorderScope;
  } else {
    delete item.dataset.reorderScope;
  }
  item.setAttribute('aria-current', entry.id === $activeSessionId.get() ? 'true' : 'false');
  item.draggable = isReorderable || canDockSidebarSession(entry);
}

function normalizeSidebarReorderScope(value: string | null | undefined): string {
  return value?.trim() || '';
}

function canReorderSidebarSession(entry: SidebarSessionRef, reorderScope: string): boolean {
  return reorderScope !== '' && !entry.session.parentSessionId && getSearchValue() === '';
}

function canDockSidebarSession(entry: SidebarSessionRef): boolean {
  return entry.machineId === null && !entry.session.parentSessionId && getSearchValue() === '';
}

function createSidebarSessionActions(entry: SidebarSessionRef): HTMLDivElement {
  const actions = document.createElement('div');
  actions.className = 'session-actions';
  actions.setAttribute('role', 'menu');
  patchSidebarSessionActions(actions, entry);
  return actions;
}

function getSidebarSessionActionsSignature(entry: SidebarSessionRef): string {
  return [
    entry.id,
    entry.session.bookmarkId ?? '',
    shouldShowAdHocBookmarkAction(
      entry.session,
      entry.machineId,
      $currentSettings.get()?.showBookmarks !== false,
      $currentSettings.get()?.allowAdHocSessionBookmarks === true,
    ),
    isSessionNotesExpanded(entry.id),
    !!normalizeSessionNotes(entry.session.notes),
    t('session.pinToQuickLaunch'),
    t('session.notes'),
    t(isSessionNotesExpanded(entry.id) ? 'session.collapseNotes' : 'session.expandNotes'),
    isSessionInLayout(entry.id),
    t('session.removeFromLayout'),
    t('session.rename'),
    t('session.close'),
  ].join('\u001f');
}

function patchSidebarSessionActions(actions: HTMLDivElement, entry: SidebarSessionRef): void {
  actions.id = `session-actions-${entry.id}`;
  actions.className = 'session-actions';
  actions.setAttribute('role', 'menu');

  const signature = getSidebarSessionActionsSignature(entry);
  if (actions.dataset.actionsSignature === signature) {
    return;
  }

  actions.dataset.actionsSignature = signature;
  actions.replaceChildren();

  if (
    shouldShowAdHocBookmarkAction(
      entry.session,
      entry.machineId,
      $currentSettings.get()?.showBookmarks !== false,
      $currentSettings.get()?.allowAdHocSessionBookmarks === true,
    )
  ) {
    const pinButton = document.createElement('button');
    pinButton.className = `session-pin${entry.session.bookmarkId ? ' pinned' : ''}`;
    pinButton.setAttribute('role', 'menuitem');
    pinButton.setAttribute('aria-pressed', entry.session.bookmarkId ? 'true' : 'false');
    pinButton.title = t('session.pinToQuickLaunch');
    pinButton.setAttribute('aria-label', t('session.pinToQuickLaunch'));
    pinButton.innerHTML = `
      <span class="session-action-icon">${entry.session.bookmarkId ? '★' : '☆'}</span>
      <span class="session-action-label">${escapeHtml(t('session.pinToQuickLaunch'))}</span>
    `;
    pinButton.addEventListener('click', (event) => {
      event.preventDefault();
      event.stopPropagation();
      callbacks?.onPinToHistory(entry.id);
    });
    actions.appendChild(pinButton);
  }

  if (entry.machineId === null) {
    const notesButton = document.createElement('button');
    notesButton.className = `session-notes-toggle${normalizeSessionNotes(entry.session.notes) ? ' has-notes' : ''}`;
    notesButton.setAttribute('role', 'menuitem');
    notesButton.setAttribute('aria-expanded', isSessionNotesExpanded(entry.id) ? 'true' : 'false');
    notesButton.title = t(
      isSessionNotesExpanded(entry.id) ? 'session.collapseNotes' : 'session.expandNotes',
    );
    notesButton.setAttribute('aria-label', notesButton.title);
    notesButton.innerHTML = `
      <span class="session-action-icon">${icon('notes')}</span>
      <span class="session-action-label">${escapeHtml(t('session.notes'))}</span>
    `;
    notesButton.addEventListener('click', (event) => {
      event.preventDefault();
      event.stopPropagation();
      callbacks?.onSelect(entry.id, { closeSettingsPanel: false, focusTerminal: false });
      toggleSessionNotes(entry.id);
    });
    actions.appendChild(notesButton);
  }

  if (entry.machineId === null && isSessionInLayout(entry.id)) {
    const undockButton = document.createElement('button');
    undockButton.className = 'session-undock';
    undockButton.setAttribute('role', 'menuitem');
    undockButton.title = t('session.removeFromLayout');
    undockButton.setAttribute('aria-label', t('session.removeFromLayout'));
    undockButton.innerHTML = `
      <span class="session-action-icon">${icon('undock')}</span>
      <span class="session-action-label">${escapeHtml(t('session.removeFromLayout'))}</span>
    `;
    undockButton.addEventListener('click', (event) => {
      event.preventDefault();
      event.stopPropagation();
      undockSession(entry.id);
    });
    actions.appendChild(undockButton);
  }

  const renameButton = document.createElement('button');
  renameButton.className = 'session-rename';
  renameButton.setAttribute('role', 'menuitem');
  renameButton.title = t('session.rename');
  renameButton.setAttribute('aria-label', t('session.rename'));
  renameButton.innerHTML = `
    <span class="session-action-icon">${icon('rename')}</span>
    <span class="session-action-label">${escapeHtml(t('session.rename'))}</span>
  `;
  renameButton.addEventListener('click', (event) => {
    event.preventDefault();
    event.stopPropagation();
    callbacks?.onRename(entry.id);
  });
  actions.appendChild(renameButton);

  const closeButton = document.createElement('button');
  closeButton.className = 'session-close';
  closeButton.setAttribute('role', 'menuitem');
  closeButton.title = t('session.close');
  closeButton.setAttribute('aria-label', t('session.close'));
  closeButton.innerHTML = `
    <span class="session-action-icon">${icon('close')}</span>
    <span class="session-action-label">${escapeHtml(t('session.close'))}</span>
  `;
  closeButton.addEventListener('click', (event) => {
    event.preventDefault();
    event.stopPropagation();
    callbacks?.onDelete(entry.id);
  });

  actions.appendChild(closeButton);
}

function createSessionNotesPane(entry: SidebarSessionRef): HTMLDivElement {
  const pane = document.createElement('div');
  pane.className = 'session-notes-pane';
  const textarea = document.createElement('textarea');
  textarea.className = 'session-notes-input';
  textarea.rows = 1;
  textarea.spellcheck = true;
  textarea.placeholder = t('session.notesPlaceholder');
  textarea.setAttribute('aria-label', t('session.notes'));
  const stopRowInteraction = (event: Event) => {
    event.stopPropagation();
  };
  textarea.addEventListener('pointerdown', stopRowInteraction);
  textarea.addEventListener('mousedown', stopRowInteraction);
  textarea.addEventListener('touchstart', stopRowInteraction);
  textarea.addEventListener('click', stopRowInteraction);
  textarea.addEventListener('dblclick', stopRowInteraction);
  textarea.addEventListener('focus', () => {
    callbacks?.onSelect(entry.id, { closeSettingsPanel: false, focusTerminal: false });
  });
  textarea.addEventListener('keydown', (event) => {
    event.stopPropagation();
  });
  textarea.addEventListener('input', () => {
    const draft = constrainSessionNotesDraft(textarea.value);
    if (textarea.value !== draft) {
      textarea.value = draft;
    }
    resizeSessionNotesInput(textarea);
    updateSessionNotes(entry.id, normalizeSessionNotes(draft));
  });
  pane.appendChild(textarea);
  syncSessionNotesPane(pane, entry);
  return pane;
}

function syncSessionNotesPane(pane: HTMLDivElement, entry: SidebarSessionRef): void {
  const expanded = isSessionNotesExpanded(entry.id);
  const notes = normalizeSessionNotes(entry.session.notes);
  pane.hidden = !expanded;
  pane.classList.toggle('open', expanded);
  pane.dataset.sessionId = entry.id;

  const textarea = pane.querySelector<HTMLTextAreaElement>('.session-notes-input');
  if (!textarea) {
    return;
  }

  textarea.placeholder = t('session.notesPlaceholder');
  textarea.setAttribute('aria-label', t('session.notes'));
  if (document.activeElement !== textarea && textarea.value !== (notes ?? '')) {
    textarea.value = notes ?? '';
  }
  resizeSessionNotesInput(textarea);
}

function isSessionNotesExpanded(sessionId: string): boolean {
  return expandedNotesSessionIds.has(sessionId);
}

function toggleSessionNotes(sessionId: string): void {
  const nextExpanded = !isSessionNotesExpanded(sessionId);
  if (nextExpanded) {
    expandedNotesSessionIds.add(sessionId);
  } else {
    expandedNotesSessionIds.delete(sessionId);
  }

  const item = dom.sessionList?.querySelector<HTMLElement>(
    `.session-item[data-session-id="${CSS.escape(sessionId)}"]`,
  );
  if (!item) {
    renderSessionList();
    return;
  }

  const entry = getAllSidebarSessions().find((candidate) => candidate.id === sessionId);
  if (!entry) {
    return;
  }

  const actions = item.querySelector<HTMLDivElement>('.session-actions');
  if (actions) {
    delete actions.dataset.actionsSignature;
    patchSidebarSessionActions(actions, entry);
  }

  const pane = item.querySelector<HTMLDivElement>('.session-notes-pane');
  if (pane) {
    syncSessionNotesPane(pane, entry);
  }

  if (nextExpanded) {
    const input = item.querySelector<HTMLTextAreaElement>('.session-notes-input');
    requestAnimationFrame(() => {
      input?.focus();
      input?.setSelectionRange(input.value.length, input.value.length);
    });
  }
}

function updateSessionNotes(sessionId: string, notes: string | null): void {
  const session = getSession(sessionId);
  if (!session) {
    return;
  }

  if ((session.notes ?? null) !== notes) {
    setSession({ ...session, notes });
  }

  const existingTimer = pendingNoteSaveTimers.get(sessionId);
  if (existingTimer !== undefined) {
    window.clearTimeout(existingTimer);
  }

  const timer = window.setTimeout(() => {
    pendingNoteSaveTimers.delete(sessionId);
    apiSetSessionNotes(sessionId, notes)
      .then((updatedSession) => {
        const currentSession = getSession(sessionId);
        if (currentSession) {
          setSession({ ...currentSession, notes: updatedSession.notes ?? null });
          if (currentSession.bookmarkId) {
            patchHistoryEntry(currentSession.bookmarkId, {
              notes: updatedSession.notes ?? '',
            } as HistoryPatchRequest).catch(() => {});
          }
        }
      })
      .catch(() => {
        // Keep the local note visible; a later edit or state refresh can retry/resolve it.
      });
  }, SESSION_NOTES_SAVE_DELAY_MS);
  pendingNoteSaveTimers.set(sessionId, timer);
}

function normalizeSessionNotes(value: string | null | undefined): string | null {
  if (!value) {
    return null;
  }

  const normalized = constrainSessionNotesDraft(value).trim();
  if (!normalized) {
    return null;
  }

  return normalized;
}

function constrainSessionNotesDraft(value: string): string {
  return value
    .replace(/\r\n/g, '\n')
    .replace(/\r/g, '\n')
    .split('\n')
    .slice(0, SESSION_NOTES_MAX_LINES)
    .join('\n')
    .slice(0, SESSION_NOTES_MAX_CHARS);
}

function resizeSessionNotesInput(textarea: HTMLTextAreaElement): void {
  textarea.style.height = 'auto';
  textarea.style.height = `${String(textarea.scrollHeight)}px`;
}

function createAdHocSection(): HTMLElement {
  const section = document.createElement('section');
  section.className = 'spaces-tree-target spaces-tree-adhoc session-group session-group-flat';

  const header = document.createElement('div');
  header.className = 'spaces-tree-target-header';
  header.appendChild(createTextSpan('spaces-tree-target-label', ''));
  section.appendChild(header);

  const list = document.createElement('div');
  list.className = 'spaces-tree-adhoc-list session-group-items';
  section.appendChild(list);
  return section;
}

function patchAdHocSection(section: HTMLElement, sessions: SidebarSessionRef[]): void {
  section.className = 'spaces-tree-target spaces-tree-adhoc session-group session-group-flat';
  const label = section.querySelector<HTMLElement>('.spaces-tree-target-label');
  const title = t('spaces.adHocSessions');
  if (label && label.textContent !== title) {
    label.textContent = title;
  }

  const list = section.querySelector<HTMLElement>('.spaces-tree-adhoc-list');
  if (list) {
    reconcileSidebarSessions(list, sessions, { reorderScope: 'adhoc' });
  }
}

function getVisibleRootSessions(
  machineId: string | null,
  space: SpaceSummaryDto,
  rootWorkspace: SpaceWorkspaceDto | null,
): SidebarSessionRef[] {
  if (!rootWorkspace) {
    return [];
  }

  const sessions = getWorkspaceSessions(machineId, space, rootWorkspace);
  if (!getSearchValue() || matchesSpaceSearch(space)) {
    return sessions;
  }

  return sessions.filter(matchesSidebarSessionSearch);
}

function getVisibleChildWorkspaces(
  machineId: string | null,
  space: SpaceSummaryDto,
): SpaceWorkspaceDto[] {
  const childWorkspaces = getChildWorkspaces(space);
  if (!getSearchValue() || matchesSpaceSearch(space)) {
    return childWorkspaces;
  }

  return childWorkspaces.filter((workspace) => matchesWorkspaceSearch(machineId, space, workspace));
}

function getWorkspaceSessions(
  machineId: string | null,
  space: SpaceSummaryDto,
  workspace: SpaceWorkspaceDto,
): SidebarSessionRef[] {
  const normalizedWorkspacePath = normalizeOptionalPath(workspace.path);
  return getAllSidebarSessions()
    .filter((entry) => sessionBelongsToSpace(entry, machineId, space))
    .filter(
      (entry) =>
        normalizedWorkspacePath &&
        normalizeOptionalPath(entry.session.workspacePath || entry.session.currentDirectory) ===
          normalizedWorkspacePath,
    )
    .sort((left, right) =>
      getSessionDisplayName(left.session).localeCompare(getSessionDisplayName(right.session)),
    );
}

function getSpaceSessions(machineId: string | null, space: SpaceSummaryDto): SidebarSessionRef[] {
  return getAllSidebarSessions().filter((entry) => sessionBelongsToSpace(entry, machineId, space));
}

function sessionBelongsToSpace(
  entry: SidebarSessionRef,
  machineId: string | null,
  space: SpaceSummaryDto,
): boolean {
  if (entry.machineId !== machineId) {
    return false;
  }

  if (isAdHocSession(entry.session)) {
    return false;
  }

  if (entry.session.spaceId === space.id) {
    return true;
  }

  const sessionPath = normalizeOptionalPath(
    entry.session.workspacePath || entry.session.currentDirectory,
  );
  if (!sessionPath) {
    return false;
  }

  return space.workspaces.some(
    (workspace) => normalizeOptionalPath(workspace.path) === sessionPath,
  );
}

function getMachineLabel(machineId: string): string {
  return cachedSections.find((section) => section.machineId === machineId)?.label || machineId;
}

function matchesSpaceSearch(space: SpaceSummaryDto): boolean {
  return matchesSearchTokens([space.displayName, space.rootPath, space.importedPath, space.kind]);
}

function matchesWorkspaceSearch(
  machineId: string | null,
  space: SpaceSummaryDto,
  workspace: SpaceWorkspaceDto,
): boolean {
  if (
    getWorkspaceSessions(machineId, space, workspace).some((entry) =>
      matchesSidebarSessionSearch(entry),
    )
  ) {
    return true;
  }

  return matchesSearchTokens([
    workspace.displayName,
    workspace.path,
    workspace.branch,
    workspace.isDetached ? t('spaces.detached') : '',
    workspace.locked ? t('spaces.locked') : '',
    workspace.prunable ? t('spaces.prunable') : '',
  ]);
}

function matchesSidebarSessionSearch(entry: SidebarSessionRef): boolean {
  const foreground = getForegroundInfo(entry.id);
  return matchesSearchTokens([
    getSessionDisplayName(entry.session),
    entry.session.name,
    entry.session.topic,
    entry.session.terminalTitle,
    entry.session.currentDirectory,
    entry.session.workspacePath,
    entry.session.shellType,
    foreground.cwd,
    foreground.name,
    foreground.displayName,
    foreground.commandLine,
    entry.machineId ? getMachineLabel(entry.machineId) : '',
  ]);
}

function matchesSearchTokens(values: Array<string | null | undefined>): boolean {
  const searchValue = getSearchValue();
  if (!searchValue) {
    return true;
  }

  return values.some((value) => value?.toLowerCase().includes(searchValue));
}

function getSearchValue(): string {
  return normalizeSearchValue(sessionFilterController.getFilterValue());
}

function syncSearchControls(): void {
  if (dom.sessionFilterInput) {
    dom.sessionFilterInput.placeholder = t('spaces.searchPlaceholder');
    dom.sessionFilterInput.setAttribute('aria-label', t('spaces.searchPlaceholder'));
  }

  if (dom.sessionFilterClear) {
    dom.sessionFilterClear.title = t('spaces.clearSearch');
    dom.sessionFilterClear.setAttribute('aria-label', t('spaces.clearSearch'));
  }
}

async function initGit(machineId: string | null, spaceId: string): Promise<void> {
  try {
    if (machineId) {
      await initHubGit(machineId, spaceId);
    } else {
      await initLocalGit(spaceId);
    }
    invalidateSidebarSpacesTree();
  } catch (error) {
    await showAlert(error instanceof Error ? error.message : String(error), {
      title: t('spaces.gitInitFailed'),
    });
  }
}

async function promptAndCreateWorktree(machineId: string | null, spaceId: string): Promise<void> {
  const space = cachedSections
    .find((section) => section.machineId === machineId)
    ?.spaces.find((candidate) => candidate.id === spaceId);
  if (!space) {
    return;
  }

  const request = await showCreateWorktreeDialog({ machineId, space });
  if (!request) {
    return;
  }

  try {
    if (machineId) {
      await createHubWorktree(machineId, spaceId, request);
    } else {
      await createLocalWorktree(spaceId, request);
    }
    invalidateSidebarSpacesTree();
  } catch (error) {
    await showAlert(error instanceof Error ? error.message : String(error), {
      title: t('spaces.worktreeCreateFailed'),
    });
  }
}

async function promptAndRenameWorktree(
  machineId: string | null,
  spaceId: string,
  workspace: SpaceWorkspaceDto,
): Promise<void> {
  const nextName = await showTextPrompt({
    title: t('spaces.renameWorktree'),
    initialValue: workspace.isMain ? '' : workspace.displayName,
  });
  if (nextName === null) {
    return;
  }

  try {
    const request = { label: nextName.trim() || null };
    if (machineId) {
      await updateHubWorkspace(machineId, spaceId, workspace.key, request);
    } else {
      await updateLocalWorkspace(spaceId, workspace.key, request);
    }
    invalidateSidebarSpacesTree();
  } catch (error) {
    await showAlert(error instanceof Error ? error.message : String(error), {
      title: t('spaces.renameWorktreeFailed'),
    });
  }
}

async function promptAndDeleteWorktree(
  machineId: string | null,
  spaceId: string,
  workspace: SpaceWorkspaceDto,
  sessions: SidebarSessionRef[],
): Promise<void> {
  if (sessions.length > 0) {
    await showAlert(t('spaces.deleteWorktreeActiveSessions'), {
      title: t('spaces.deleteWorktreeBlockedTitle'),
    });
    return;
  }

  let force = false;
  if (workspace.hasChanges) {
    const dirtyConfirmed = await showConfirm(
      t('spaces.deleteWorktreeDirtyConfirm').replace('{name}', workspace.displayName),
      {
        title: t('spaces.deleteWorktreeDirtyTitle'),
        danger: true,
      },
    );
    if (!dirtyConfirmed) {
      return;
    }

    const finalConfirmed = await showConfirm(
      t('spaces.deleteWorktreeDirtyFinalConfirm').replace('{name}', workspace.displayName),
      {
        title: t('spaces.deleteWorktreeDirtyFinalTitle'),
        danger: true,
      },
    );
    if (!finalConfirmed) {
      return;
    }

    force = true;
  } else {
    const confirmed = await showConfirm(
      t('spaces.deleteWorktreeConfirm').replace('{name}', workspace.displayName),
      {
        title: t('spaces.deleteWorktreeTitle'),
        danger: true,
      },
    );
    if (!confirmed) {
      return;
    }
  }

  try {
    if (machineId) {
      await deleteHubWorktree(machineId, spaceId, workspace.key, { force });
    } else {
      await deleteLocalWorktree(spaceId, workspace.key, { force });
    }
    invalidateSidebarSpacesTree();
  } catch (error) {
    await showAlert(error instanceof Error ? error.message : String(error), {
      title: t('spaces.deleteWorktreeFailed'),
    });
  }
}

async function promptAndForgetSpace(
  machineId: string | null,
  space: SpaceSummaryDto,
): Promise<void> {
  const hasActiveSessions = getSpaceSessions(machineId, space).length > 0;
  const confirmed = await showConfirm(
    (hasActiveSessions
      ? t('spaces.deleteSpaceActiveSessions')
      : t('spaces.deleteSpaceConfirm')
    ).replace('{name}', space.displayName),
    {
      title: t('spaces.deleteSpaceTitle'),
      danger: true,
    },
  );
  if (!confirmed) {
    return;
  }

  try {
    if (machineId) {
      await deleteHubSpace(machineId, space.id);
    } else {
      await deleteLocalSpace(space.id);
    }
    invalidateSidebarSpacesTree();
  } catch (error) {
    await showAlert(error instanceof Error ? error.message : String(error), {
      title: t('spaces.deleteSpaceFailed'),
    });
  }
}

async function openWorkspace(
  machineId: string | null,
  spaceId: string,
  workspace: SpaceWorkspaceDto,
  surface: SpaceSurface,
): Promise<void> {
  closePopovers();
  const launched = await launchSpaceWorkspace(machineId, spaceId, workspace, surface);
  if (launched) {
    callbacks?.onCloseSidebar();
  }
}

function buildSpaceActions(machineId: string | null, space: SpaceSummaryDto): PopoverAction[] {
  const actions: PopoverAction[] = [];

  if (space.canCreateWorktree) {
    actions.push({
      label: t('spaces.newWorktree'),
      run: () => {
        void promptAndCreateWorktree(machineId, space.id);
      },
    });
  } else if (space.canInitGit) {
    actions.push({
      label: t('spaces.initGit'),
      run: () => {
        void initGit(machineId, space.id);
      },
    });
  }

  actions.push({
    label: space.isPinned ? t('spaces.unpinSpace') : t('spaces.pinSpace'),
    run: () => {
      void toggleSpacePinned(machineId, space);
    },
  });

  return actions;
}

function buildWorkspaceActions(
  machineId: string | null,
  space: SpaceSummaryDto,
  workspace: SpaceWorkspaceDto,
  sessions: SidebarSessionRef[],
): PopoverAction[] {
  return [
    {
      label: t('spaces.renameWorktreeShort'),
      run: () => {
        void promptAndRenameWorktree(machineId, space.id, workspace);
      },
    },
    {
      label: t('spaces.deleteWorktreeShort'),
      tone: 'danger',
      run: () => {
        void promptAndDeleteWorktree(machineId, space.id, workspace, sessions);
      },
    },
  ];
}

function canManageWorkspace(space: SpaceSummaryDto, workspace: SpaceWorkspaceDto): boolean {
  return space.kind === 'git' && !workspace.isMain;
}

function ensurePopovers(): void {
  if (!actionPopoverEl) {
    actionPopoverEl = document.createElement('div');
    actionPopoverEl.className = 'manager-bar-action-popover spaces-tree-popover hidden';
    document.body.appendChild(actionPopoverEl);
  }

  if (!chooserPopoverEl) {
    chooserPopoverEl = document.createElement('div');
    chooserPopoverEl.className = 'manager-bar-action-popover spaces-tree-popover hidden';
    document.body.appendChild(chooserPopoverEl);
  }
}

function openActionPopover(trigger: HTMLElement, actions: PopoverAction[]): void {
  if (!actionPopoverEl) {
    return;
  }

  chooserPopoverEl?.classList.add('hidden');
  actionPopoverEl.replaceChildren();
  for (const action of actions) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = `manager-bar-action-popover-btn${action.tone === 'danger' ? ' manager-bar-action-popover-delete' : ' manager-bar-action-popover-edit'}`;
    button.textContent = action.label;
    button.addEventListener('click', () => {
      closePopovers();
      void action.run();
    });
    actionPopoverEl.appendChild(button);
  }

  positionPopover(actionPopoverEl, trigger);
}

function openSurfaceChooser(
  trigger: HTMLElement,
  machineId: string | null,
  space: SpaceSummaryDto,
  workspace: SpaceWorkspaceDto,
): void {
  if (!chooserPopoverEl) {
    return;
  }

  actionPopoverEl?.classList.add('hidden');
  chooserPopoverEl.replaceChildren();

  for (const surface of ['terminal', 'codex', 'claude'] as const) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'manager-bar-action-popover-btn manager-bar-action-popover-edit';
    button.textContent = t(
      surface === 'terminal'
        ? 'sessionLauncher.startTerminal'
        : surface === 'codex'
          ? 'sessionLauncher.startCodex'
          : 'sessionLauncher.startClaude',
    );
    button.addEventListener('click', () => {
      void openWorkspace(machineId, space.id, workspace, surface);
    });
    chooserPopoverEl.appendChild(button);
  }

  positionPopover(chooserPopoverEl, trigger);
}

function positionPopover(popover: HTMLElement, trigger: HTMLElement): void {
  popover.classList.remove('hidden');
  const triggerRect = trigger.getBoundingClientRect();
  const popoverRect = popover.getBoundingClientRect();
  const gap = 6;
  const viewportPadding = 8;
  const openUp =
    window.innerHeight - triggerRect.bottom < popoverRect.height &&
    triggerRect.top > popoverRect.height;
  const top = openUp ? triggerRect.top - popoverRect.height - gap : triggerRect.bottom + gap;
  const left = Math.min(
    Math.max(viewportPadding, triggerRect.right - popoverRect.width),
    window.innerWidth - viewportPadding - popoverRect.width,
  );

  popover.style.top = `${Math.round(Math.max(viewportPadding, top))}px`;
  popover.style.left = `${Math.round(left)}px`;
}

function closePopovers(): void {
  actionPopoverEl?.classList.add('hidden');
  chooserPopoverEl?.classList.add('hidden');
}

function handleGlobalPopoverClick(event: MouseEvent): void {
  const target = event.target as HTMLElement | null;
  if (target?.closest('.spaces-tree-popover') || target?.closest('.spaces-tree-inline-action')) {
    return;
  }

  closePopovers();
}

function isSpaceExpanded(
  machineId: string | null,
  space: SpaceSummaryDto,
  hasSessions: boolean,
): boolean {
  if (getSearchValue()) {
    return true;
  }

  const stored = localStorage.getItem(
    `${SPACE_EXPANDED_PREFIX}${getSpaceStorageKey(machineId, space.id)}`,
  );
  if (stored === 'true') {
    return true;
  }

  if (stored === 'false') {
    return false;
  }

  return hasSessions;
}

function removeSpaceNodeExpandedContent(node: HTMLElement): void {
  const children = Array.from(node.children).slice(1);
  for (const child of children) {
    child.querySelectorAll<HTMLElement>('.session-item[data-session-id]').forEach((item) => {
      const sessionId = item.dataset.sessionId;
      if (sessionId) {
        unregisterHeatCanvas(sessionId);
      }
    });
    child.remove();
  }
}

function toggleSpaceExpanded(
  node: HTMLElement,
  machineId: string | null,
  space: SpaceSummaryDto,
): void {
  const isExpanded = node.classList.contains('expanded');
  localStorage.setItem(
    `${SPACE_EXPANDED_PREFIX}${getSpaceStorageKey(machineId, space.id)}`,
    String(!isExpanded),
  );
  if (getSearchValue()) {
    renderSessionList();
    return;
  }

  closePopovers();
  if (isExpanded) {
    removeSpaceNodeExpandedContent(node);
    node.classList.remove('expanded');
    return;
  }

  node.classList.add('expanded');
  patchSpaceNodeExpandedContent(node, machineId, space);
}

function getSpaceStorageKey(machineId: string | null, spaceId: string): string {
  return `${machineId ?? 'local'}:${spaceId}`;
}

async function toggleSpacePinned(machineId: string | null, space: SpaceSummaryDto): Promise<void> {
  try {
    if (machineId) {
      await updateHubSpace(machineId, space.id, { isPinned: !space.isPinned });
    } else {
      await updateLocalSpace(space.id, { isPinned: !space.isPinned });
    }
    invalidateSidebarSpacesTree();
  } catch (error) {
    await showAlert(error instanceof Error ? error.message : String(error), {
      title: space.isPinned ? t('spaces.unpinSpace') : t('spaces.pinSpace'),
    });
  }
}

function flushQueuedSidebarSessionProcessInfoUpdates(): void {
  const sessionIds = Array.from(queuedProcessInfoSessionIds);
  queuedProcessInfoSessionIds.clear();

  if (isSessionFilterActive()) {
    renderSessionList();
    updateEmptyState();
    updateMobileTitle();
    return;
  }

  for (const sessionId of sessionIds) {
    if (!syncSidebarSessionProcessInfo(sessionId)) {
      renderSessionList();
      updateEmptyState();
      updateMobileTitle();
      return;
    }
  }

  updateMobileTitle();
}

function queueSidebarSessionProcessInfoUpdate(sessionId: string): void {
  queuedProcessInfoSessionIds.add(sessionId);
  if (queuedRenderFrameId !== null) {
    return;
  }

  queuedRenderFrameId = window.requestAnimationFrame(() => {
    queuedRenderFrameId = null;
    flushQueuedSidebarSessionProcessInfoUpdates();
  });
}

function normalizeSearchValue(value: string): string {
  return value.trim().toLowerCase();
}

function normalizeOptionalPath(value: string | null | undefined): string | null {
  const trimmed = value?.trim();
  if (!trimmed) {
    return null;
  }

  return trimmed.replace(/\\/g, '/').replace(/\/+$/, '').toLowerCase();
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

/* eslint-enable max-lines */
