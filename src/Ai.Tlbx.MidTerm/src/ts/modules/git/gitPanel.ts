/**
 * Git Panel
 *
 * Accordion-style git status viewer with a dock-native diff/commit inspector
 * and explicit terminal command handoff helpers.
 */

import type {
  GitCommitDetailsResponse,
  GitDiffFileView,
  GitDiffViewResponse,
  GitFileEntry,
  GitLogEntry,
  GitStatusResponse,
  GitOperationRequest,
  GitStatusReadResult,
  GitOperationResponse,
} from './types';

import {
  fetchCommitDetails,
  fetchDiffView,
  fetchGitHistoryPage,
  fetchGitStatus,
  fetchGitStatusResult,
} from './gitApi';
import {
  createGitReadUi,
  acceptGitRead,
  markGitReadFailure,
  renderGitReadState,
  type GitReadUi,
} from './gitReadState';
import { buildCommitCommandSuggestions, buildFileCommandSuggestions } from './gitCommands';
import { pasteToTerminal } from '../terminal';
import { escapeHtml } from '../../utils';
import { renderPatchFile } from './gitDiffView';
import { hasSummaryChanged, renderPanelFill, renderSummary } from './gitPanelSummary';
import { openGitWorkspace } from './gitWorkspace';
import { renderCommitsSection, handleGitHistoryKey } from './gitHistory';
import { renderGitFileTree } from './gitFileTree';
import {
  createGitFileUi,
  renderGitFileControls,
  filteredGitFiles,
  pruneGitFileSelection,
  handleGitFileControl,
  toggleGitFolder,
  selectedGitPaths,
  type GitFileUi,
} from './gitFileControls';
import { handleGitFileKey } from './gitFileNavigation';
import {
  renderCommitComparison,
  captureCommitParentFocus,
  renderEmptyComparison,
  renderCommitMetadata,
} from './gitCommitComparison';
import {
  createGitDiffUi,
  clearDiffSelection,
  renderSelectedDiffControls,
  handleDiffControl,
  handleDiffKey,
  type GitDiffUi,
} from './gitDiffControls';
import { t } from '../i18n';
import { areJsonLikeEqual, $sessions } from '../../stores';
import { syncGitPanelDom } from './gitPanelDom';
import { showPathDetails, repositoryFilePath } from '../fileViewer/pathDetails';
import { fileStatusPresentation } from './fileStatus';
import { writeTextToClipboard } from '../fileViewer/shared';

import { addProcessStateListener } from '../process';
import { renderCommandSuggestions, canInsertCommand } from './gitCommandSuggestions';
import {
  createGitOperationsUi,
  buildGitMutationRequest,
  gitSectionFiles,
  buildGitSelectionRequest,
  renderGitOperations,
  gitSectionAction,
  renderGitInspectorAction,
  executeGitOperation,
  setGitCommitMessage,
  canCommit,
  type GitOperationsUi,
} from './gitOperations';
import {
  connectGitPanelLayout,
  disconnectGitPanelLayout,
  focusGitSelection,
  type GitPanelLayoutState,
} from './gitPanelLayout';

type SectionId = 'conflicts' | 'staged' | 'changes' | 'untracked';
type PanelSectionId = SectionId | 'commits';
type FileScope = 'worktree' | 'staged';

interface GitFileSelection {
  kind: 'file';
  path: string;
  scope: FileScope;
  status: string;
  originalPath?: string | undefined;
  diff: GitDiffViewResponse | null;
  loading: boolean;
  error: string | null;
}

interface GitCommitSelection {
  kind: 'commit';
  hash: string;
  parentIndex: number;
  details: GitCommitDetailsResponse | null;
  loading: boolean;
  error: string | null;
}

type GitSelection = GitFileSelection | GitCommitSelection | null;

interface GitPanelState extends GitPanelLayoutState {
  operations: GitOperationsUi;
  wrapDiff: boolean;
  diffUi: GitDiffUi;
  fileUi: GitFileUi;
  read: GitReadUi;
  sessionId: string;
  repoRoot?: string | undefined;
  container: HTMLElement;
  status: GitStatusResponse | null;
  expandedSections: Set<PanelSectionId>;
  selection: GitSelection;
  historyEntries: GitLogEntry[];
  historyLoading: boolean;
  historyError: string | null;
  historyFocusedHash: string | null;
  previousSectionCount: number;
  hasMoreHistory: boolean;
  inspectorSelection?: GitSelection;
  inspectorHtml?: string;
}

const panelStates = new Map<string, GitPanelState>();
const HISTORY_PAGE_SIZE = 20;
const DIFF_WRAP_KEY = 'tlbx-git-diff-wrap';
const nowrapScroll = new WeakMap<HTMLElement, number>();
// Only one panel may own a shared dock body, including while fetches are pending.
const activePanels = new WeakMap<HTMLElement, GitPanelState>();
const boundPanelContainers = new WeakSet<HTMLElement>();

if (typeof document !== 'undefined')
  document.addEventListener('tlbx-locale-changed', () => {
    for (const state of panelStates.values()) {
      if (activePanels.get(state.container) !== state) continue;
      delete state.inspectorSelection;
      renderPanel(state);
      void refreshPanelRead(state);
    }
  });

addProcessStateListener((sessionId) => {
  for (const state of panelStates.values()) {
    if (state.sessionId !== sessionId || activePanels.get(state.container) !== state) continue;
    const allowed = canInsertCommand(sessionId);
    for (const button of state.container.querySelectorAll<HTMLButtonElement>(
      '[data-action="insert-command"]',
    )) {
      button.disabled = !allowed || button.getAttribute('aria-busy') === 'true';
      button.title = t(allowed ? 'git.insertHint' : 'git.insertShellOnly');
    }
  }
});

export function suspendGitPanel(container: HTMLElement): void {
  disconnectGitPanelLayout(container);
  activePanels.delete(container);
}

export function createGitPanel(container: HTMLElement, sessionId: string, repoRoot?: string): void {
  const state = createPanelState(container, sessionId, repoRoot);
  panelStates.set(getPanelKey(sessionId, repoRoot), state);
  activePanels.set(container, state);
  renderPanel(state);
}

export function updateGitStatus(sessionId: string, status: GitStatusResponse): void {
  const explicit = panelStates.get(getPanelKey(sessionId, status.repoRoot));
  const implicit = panelStates.get(sessionId);
  const match = implicit?.status?.repoRoot === status.repoRoot ? implicit : undefined;
  for (const state of new Set([explicit, match])) {
    if (!state) continue;
    if (status.filesOmitted) {
      const open = activePanels.get(state.container) === state;
      if (open && hasSummaryChanged(state.status, status)) void refreshPanelRead(state);
      continue;
    }
    if (areJsonLikeEqual(state.status, status) && state.read.phase === 'ready') continue;
    state.read.sequence++;
    applyPanelRead(state, { ok: true, status });
  }
}

export async function refreshGitPanel(sessionId: string, repoRoot?: string): Promise<void> {
  const state = panelStates.get(getPanelKey(sessionId, repoRoot));
  if (state) return refreshPanelRead(state);
  const status = await fetchGitStatus(sessionId, repoRoot);
  if (status) updateGitStatus(sessionId, status);
}

async function refreshPanelRead(state: GitPanelState): Promise<void> {
  const sequence = ++state.read.sequence;
  state.read.phase = 'loading';
  renderPanel(state);
  const result = await fetchGitStatusResult(state.sessionId, state.repoRoot);
  if (state.read.sequence === sequence) applyPanelRead(state, result);
}

function applyPanelRead(state: GitPanelState, result: GitStatusReadResult): void {
  const previousIndex = state.status?.indexVersion;
  state.status = acceptGitRead(state.read, result, state.status);
  if (state.status) {
    pruneGitFileSelection(state.fileUi, state.status);
    syncHistoryFromStatus(state, state.status);
  }
  syncSelectionAfterStatus(state);
  renderPanel(state);
  if (
    state.read.phase === 'ready' &&
    previousIndex !== state.status?.indexVersion &&
    state.selection?.kind === 'file'
  )
    void openFileSelection(state, state.selection, true);
}

export function updateGitConnection(connected: boolean): void {
  for (const state of panelStates.values()) {
    if (connected) {
      if (activePanels.get(state.container) === state) void refreshPanelRead(state);
    } else {
      state.read.sequence++;
      markGitReadFailure(state.read, state.status, t('git.offlineStatus'));
      renderPanel(state);
    }
  }
}

export function updateGitRepoBindings(sessionId: string, roots: string[]): void {
  for (const state of panelStates.values()) {
    if (state.sessionId !== sessionId || !state.repoRoot || roots.includes(state.repoRoot))
      continue;
    state.read.sequence++;
    markGitReadFailure(state.read, state.status, t('git.repositoryUnbound'));
    renderPanel(state);
  }
}

export async function renderGitPanelInto(
  container: HTMLElement,
  sessionId: string,
  repoRoot?: string,
): Promise<void> {
  const key = getPanelKey(sessionId, repoRoot);
  let state = panelStates.get(key);
  if (!state) {
    state = createPanelState(container, sessionId, repoRoot);
    panelStates.set(key, state);
  } else {
    state.container = container;
    state.repoRoot = repoRoot;
  }

  activePanels.set(container, state);
  await refreshPanelRead(state);
}

export async function showCommitInGitPanel(
  sessionId: string,
  hash: string,
  repoRoot?: string,
): Promise<void> {
  const key = getPanelKey(sessionId, repoRoot);
  let state = panelStates.get(key);
  if (!state) {
    const container = document.getElementById('git-dock')?.querySelector('.git-dock-body');
    if (!(container instanceof HTMLElement)) {
      return;
    }

    await renderGitPanelInto(container, sessionId, repoRoot);
    state = panelStates.get(key);
    if (!state) {
      return;
    }
  }

  await openCommitSelection(state, hash);
}

export function destroyGitPanel(sessionId: string): void {
  for (const key of panelStates.keys()) {
    if (key === sessionId || key.startsWith(`${sessionId}|`)) {
      const state = panelStates.get(key);
      if (state && activePanels.get(state.container) === state) {
        disconnectGitPanelLayout(state.container);
        activePanels.delete(state.container);
      }
      panelStates.delete(key);
    }
  }
}

function createPanelState(
  container: HTMLElement,
  sessionId: string,
  repoRoot?: string,
): GitPanelState {
  return {
    sessionId,
    repoRoot,
    container,
    status: null,
    expandedSections: new Set<PanelSectionId>(),
    selection: null,
    operations: createGitOperationsUi(sessionId, repoRoot),
    wrapDiff: readDiffWrap(),
    diffUi: createGitDiffUi(),
    fileUi: createGitFileUi(),
    read: createGitReadUi(),
    detailActive: true,
    scrollPositions: new Map(),
    historyEntries: [],
    historyLoading: false,
    historyError: null,
    historyFocusedHash: null,
    previousSectionCount: -1,
    hasMoreHistory: false,
  };
}

function readDiffWrap(): boolean {
  try {
    return localStorage.getItem(DIFF_WRAP_KEY) === 'true';
  } catch {
    return false;
  }
}

function toggleDiffWrap(state: GitPanelState): void {
  const panes = state.container.querySelectorAll<HTMLElement>('.git-diff-lines');
  state.wrapDiff = !state.wrapDiff;
  if (state.wrapDiff) for (const pane of panes) nowrapScroll.set(pane, pane.scrollLeft);
  const shell = state.container.querySelector<HTMLElement>('.git-panel-shell');
  if (shell) shell.dataset.diffWrap = String(state.wrapDiff);
  if (!state.wrapDiff) for (const pane of panes) pane.scrollLeft = nowrapScroll.get(pane) ?? 0;
  state.container
    .querySelector('[data-action="toggle-diff-wrap"]')
    ?.setAttribute('aria-pressed', String(state.wrapDiff));
  try {
    localStorage.setItem(DIFF_WRAP_KEY, String(state.wrapDiff));
  } catch {
    /* The current reading mode still works without storage. */
  }
}

function getPanelKey(sessionId: string, repoRoot?: string): string {
  return repoRoot ? `${sessionId}|${repoRoot}` : sessionId;
}

function syncHistoryFromStatus(state: GitPanelState, status: GitStatusResponse): void {
  const currentHead = state.historyEntries[0]?.hash;
  const nextHead = status.recentCommits[0]?.hash;
  const shouldReset =
    state.historyEntries.length === 0 || (currentHead && nextHead && currentHead !== nextHead);

  if (shouldReset) {
    state.historyEntries = status.recentCommits;
    state.hasMoreHistory = status.recentCommits.length >= HISTORY_PAGE_SIZE;
    state.historyLoading = false;
    state.historyError = null;
  }
}

function syncSelectionAfterStatus(state: GitPanelState): void {
  if (!hasGitRepoStatus(state.status) || !state.selection) {
    state.selection = null;
    return;
  }

  if (state.selection.kind !== 'file') {
    return;
  }

  const selection = state.selection;
  const status = state.status;
  const workingFiles = [...status.conflicted, ...status.modified, ...status.untracked];
  let scope = selection.scope;
  let entry = (scope === 'staged' ? status.staged : workingFiles).find(
    (file) => file.path === selection.path,
  );
  if (!entry) {
    scope = scope === 'staged' ? 'worktree' : 'staged';
    entry = (scope === 'staged' ? status.staged : workingFiles).find(
      (file) => file.path === selection.path,
    );
  }
  if (!entry) {
    state.selection = null;
  } else if (
    scope !== selection.scope ||
    entry.status !== selection.status ||
    entry.originalPath !== selection.originalPath
  ) {
    state.selection = {
      ...selection,
      scope,
      status: entry.status,
      originalPath: entry.originalPath,
    };
  }
}

function hasGitRepoStatus(status: GitStatusResponse | null): status is GitStatusResponse {
  return Boolean(status && (status.repoRoot || status.branch));
}

function panelShellClass(state: GitPanelState): string {
  return `git-panel-shell${state.selection && state.detailActive ? ' git-panel-detail-active' : ' git-panel-no-inspector'}${state.diffUi.sideBySide ? ' git-panel-side-comparison' : ''}`;
}

function tallyLoc(files: GitFileEntry[]): { add: number; del: number } {
  let add = 0;
  let del = 0;
  for (const file of files) {
    add += file.additions;
    del += file.deletions;
  }
  return { add, del };
}

function applyDefaultExpandedSections(state: GitPanelState, status: GitStatusResponse): void {
  if (state.expandedSections.size > 0) {
    return;
  }

  const sections: SectionId[] = [];

  if (status.conflicted.length > 0) {
    sections.push('conflicts');
  }
  if (status.staged.length > 0) {
    sections.push('staged');
  }
  if (status.modified.length > 0) {
    sections.push('changes');
  }
  if (status.untracked.length > 0) {
    sections.push('untracked');
  }

  if (sections.length === 0 && state.historyEntries.length > 0) {
    state.expandedSections.add('commits');
    return;
  }

  for (const section of sections) {
    state.expandedSections.add(section);
  }
}

function renderPanel(state: GitPanelState): void {
  const { status, container } = state;
  if (activePanels.get(container) !== state) return;
  bindPanelEvents(container);

  if (!hasGitRepoStatus(status)) {
    state.selection = null;
    syncGitPanelDom(
      container,
      `
      <div class="git-panel">
        <div class="git-panel-content">
          ${renderUnavailableGit(state)}
        </div>
      </div>`,
    );
    return;
  }

  applyDefaultExpandedSections(state, status);

  const hasConflicts = status.conflicted.length > 0;
  const hasStaged = status.staged.length > 0;
  const hasChanges = status.modified.length > 0;
  const hasUntracked = status.untracked.length > 0;
  const sectionCount = [hasConflicts, hasStaged, hasChanges, hasUntracked].filter(Boolean).length;
  expandHistoryWhenClean(state, sectionCount);
  const singleFillSection = sectionCount === 1;

  let html = `<div class="${panelShellClass(state)}" data-diff-wrap="${state.wrapDiff}" data-git-phase="${state.read.phase}">`;
  html += '<div class="git-panel-list">';
  html += renderGitReadState(state.read);
  html += renderSummary(status);
  html += renderGitOperations(state.operations, status, state.read.phase !== 'ready');
  html += renderVisibleFileControls(state, sectionCount);
  html += '<div class="git-panel-content">';

  if (sectionCount > 0) {
    html += `<div class="git-panel-sections${singleFillSection ? ' git-panel-sections-fill' : ''}">`;
    if (hasConflicts) {
      html += renderSection(
        state,
        'conflicts',
        t('git.conflicts'),
        status.conflicted,
        'worktree',
        singleFillSection,
      );
    }
    if (hasStaged) {
      html += renderSection(
        state,
        'staged',
        t('git.stagedChanges'),
        status.staged,
        'staged',
        singleFillSection,
      );
    }
    if (hasChanges) {
      html += renderSection(
        state,
        'changes',
        t('git.changes'),
        status.modified,
        'worktree',
        singleFillSection,
      );
    }
    if (hasUntracked) {
      html += renderSection(
        state,
        'untracked',
        t('git.untracked'),
        status.untracked,
        'worktree',
        singleFillSection,
      );
    }
    html += '</div>';
  } else {
    html += renderPanelFill(t('git.workingTreeClean'), '', 'git-panel-clean');
  }

  if (state.historyEntries.length > 0) {
    html += renderCommitsSection(state, sectionCount === 0);
  }

  html += '</div></div>';
  if (state.inspectorHtml === undefined || state.inspectorSelection !== state.selection) {
    state.inspectorHtml = renderInspector(state);
    state.inspectorSelection = state.selection;
  }
  html += '<div class="git-panel-inspector"></div>';
  html += '</div>';

  syncGitPanelDom(container, html);
  syncCommitMessage(container, state.operations.message);
  const inspector = container.querySelector<HTMLElement>('.git-panel-inspector');
  if (inspector) syncGitPanelDom(inspector, state.inspectorHtml);
  container
    .querySelector('[data-action="toggle-diff-wrap"]')
    ?.setAttribute('aria-pressed', String(state.wrapDiff));
  connectGitPanelLayout(container, () => activePanels.get(container))?.update();
}

function renderUnavailableGit(state: GitPanelState): string {
  return state.read.phase === 'no-repo'
    ? renderPanelFill(t('git.notARepo'), t('git.notARepoHint'), 'git-panel-empty')
    : renderGitReadState(state.read);
}

function syncCommitMessage(container: HTMLElement, message: string): void {
  const input = container.querySelector<HTMLTextAreaElement>('.git-commit-message');
  if (input && input.value !== message) input.value = message;
}

function renderSection(
  state: GitPanelState,
  sectionId: SectionId,
  title: string,
  files: GitFileEntry[],
  scope: FileScope,
  fill = false,
): string {
  const isExpanded = state.expandedSections.has(sectionId);
  const visible = filteredGitFiles(files, state.fileUi);
  const count = visible.length;
  const loc = tallyLoc(visible);

  let locHtml = '';
  if (sectionId !== 'untracked') {
    locHtml =
      ` <span class="git-section-loc">` +
      `<span class="git-loc-add">+${loc.add}</span> ` +
      `<span class="git-loc-del">-${loc.del}</span>` +
      `</span>`;
  }

  let html = `<div data-section="${sectionId}" class="git-section${isExpanded ? ' git-section-expanded' : ''}${fill ? ' git-section-fill' : ''}">
    <div class="git-section-heading"><button class="git-section-header" data-action="toggle-section" data-section="${sectionId}" type="button">
      <span class="git-section-chevron">${isExpanded ? '\u25BE' : '\u25B8'}</span>
      <span class="git-section-title">${escapeHtml(title)}</span>
      <span class="git-section-count">${count}</span>
      ${locHtml}
    </button>${gitSectionAction(sectionId, isGitOperationBusy(state) || visible.length === 0)}</div>`;

  if (isExpanded) {
    html += `<div class="git-section-body" role="${state.fileUi.layout === 'tree' ? 'tree' : 'listbox'}" aria-multiselectable="true" aria-label="${escapeHtml(title)}">`;
    html += renderGitFileTree(files, {
      scope,
      section: sectionId,
      selection: state.selection,
      root: state.status?.repoRoot ?? state.repoRoot ?? '',
      busy: isGitOperationBusy(state),
      ui: state.fileUi,
    });
    html += `</div>`;
  }

  html += '</div>';
  return html;
}

function renderInspector(state: GitPanelState): string {
  const selection = state.selection;
  let body = renderInspectorFill(t('git.chooseReview'));

  if (selection?.kind === 'file') {
    body = renderFileInspector(
      selection,
      state.sessionId,
      state.status?.repoRoot ?? state.repoRoot ?? '',
      state,
    );
  } else if (selection?.kind === 'commit') {
    body = renderCommitInspector(
      selection,
      state.sessionId,
      state.status?.repoRoot ?? state.repoRoot ?? '',
      state.diffUi,
    );
  }

  return `<div class="git-inspector-header">
      <button class="git-inspector-back" data-action="show-list" aria-label="${t('git.backToList')}" title="${t('git.backToList')}" type="button">←</button>
      ${renderGitInspectorAction(state.selection, isGitOperationBusy(state))}
      <details class="git-diff-options"><summary aria-label="${t('fileBrowser.viewOptions')}" title="${t('fileBrowser.viewOptions')}">⋯</summary><div><button class="git-diff-wrap" data-action="toggle-diff-wrap" aria-pressed="${state.wrapDiff}" type="button">${t('git.wrapLines')}</button></div></details>
      ${renderSelectedDiffControls(state.diffUi, state.selection, isGitOperationBusy(state))}
    </div>
    <div class="git-inspector-body">${body}</div>`;
}

function renderVisibleFileControls(state: GitPanelState, sectionCount: number): string {
  return sectionCount > 0 || state.fileUi.filter
    ? renderGitFileControls(state.fileUi, isGitOperationBusy(state))
    : '';
}

function renderFileInspector(
  selection: GitFileSelection,
  sessionId: string,
  root: string,
  state: GitPanelState,
): string {
  if (selection.loading) {
    return renderInspectorFill(t('git.loadingDiff'));
  }

  if (selection.error) {
    return renderInspectorFill(selection.error);
  }

  const diff = selection.diff;
  const file =
    diff?.files.find((candidate) => candidate.path === selection.path) ??
    createFallbackDiffFile(selection);

  const suggestions = buildFileCommandSuggestions(
    file,
    selection.scope,
    root,
    $sessions.get()[sessionId]?.shellType ?? '',
  );
  const pathHtml = escapeHtml(file.path);
  const originalHtml = file.originalPath
    ? `<div class="git-inspector-subtitle">${t('git.renamedFrom')}: ${escapeHtml(file.originalPath)}</div>`
    : '';

  let html = `<div class="git-inspector-summary">
    <div class="git-inspector-title-row">
      <span class="git-inspector-title" title="${pathHtml}">${escapeHtml(file.path.slice(file.path.lastIndexOf('/') + 1))}</span>
      <span class="git-summary-pill" data-git-scope="${selection.scope}">${t(selection.scope === 'staged' ? 'git.stagedScope' : 'git.worktreeScope')}</span>
      ${file.hasLineStats === false || file.isBinary ? '' : `<span class="git-file-loc"><span class="git-loc-add">+${file.additions}</span> <span class="git-loc-del">−${file.deletions}</span></span>`}
      <button class="git-path-details" data-action="path-details" data-path="${pathHtml}" data-status="${escapeHtml(selection.status)}"${file.originalPath ? ` data-original-path="${escapeHtml(file.originalPath)}"` : ''} type="button">${t('fileBrowser.details')}</button>
    </div>
    ${originalHtml}
  </div>`;
  html += renderPatchFile(file, {
    hideFileHeader: true,
    ui: state.diffUi,
    selectable: !!diff?.canSelectLines,
    staged: selection.scope === 'staged',
    busy: isGitOperationBusy(state),
  });
  html += renderCommandSuggestions(suggestions, sessionId);
  return html;
}

function renderCommitInspector(
  selection: GitCommitSelection,
  sessionId: string,
  root: string,
  diffUi: GitDiffUi,
): string {
  if (selection.loading) {
    return renderInspectorFill(t('git.loadingCommit'));
  }

  if (selection.error || !selection.details) {
    return renderInspectorFill(selection.error ?? t('git.commitUnavailable'));
  }

  const commit = selection.details;
  const suggestions = buildCommitCommandSuggestions(
    commit,
    root,
    $sessions.get()[sessionId]?.shellType ?? '',
  );
  let html = renderCommitMetadata(commit);
  html += renderCommitComparison(commit);
  html += renderEmptyComparison(commit);
  html += `<div class="git-commit-patch-list">${commit.files.map((file) => renderPatchFile(file, { ui: diffUi, historical: true })).join('')}</div>`;
  html += renderCommandSuggestions(suggestions, sessionId);
  return html;
}

function renderInspectorFill(message: string): string {
  return `<div class="git-inspector-empty"><p>${escapeHtml(message)}</p></div>`;
}

function createFallbackDiffFile(selection: GitFileSelection): GitDiffFileView {
  return {
    path: selection.path,
    originalPath: selection.originalPath,
    status: selection.status,
    additions: 0,
    deletions: 0,
    isBinary: false,
    isTruncated: false,
    hunks: [],
  };
}

function bindPanelEvents(container: HTMLElement): void {
  if (boundPanelContainers.has(container)) return;
  boundPanelContainers.add(container);
  container.addEventListener('focusin', (event) => {
    const state = activePanels.get(container);
    const row =
      event.target instanceof HTMLElement
        ? event.target.closest<HTMLElement>('.git-commit-entry')
        : null;
    if (!state || !row) return;
    state.historyFocusedHash = row.dataset.hash ?? null;
    container.querySelectorAll<HTMLElement>('.git-commit-entry').forEach((item) => {
      item.tabIndex = item === row ? 0 : -1;
    });
  });
  container.addEventListener('click', (event) => {
    const state = activePanels.get(container);
    const element =
      event.target instanceof Element ? event.target.closest<HTMLElement>('[data-action]') : null;
    if (!state || !element || !container.contains(element)) return;
    handlePanelAction(state, element, event);
  });
  container.addEventListener('input', (event) => {
    const state = activePanels.get(container);
    if (
      state &&
      event.target instanceof HTMLInputElement &&
      event.target.dataset.gitFileFilter === 'true'
    ) {
      state.fileUi.filter = event.target.value;
      state.fileUi.filterCollapsed.clear();
      if (state.status) pruneGitFileSelection(state.fileUi, state.status);
      renderPanel(state);
      return;
    }
    if (
      !state ||
      !(event.target instanceof HTMLTextAreaElement) ||
      event.target.dataset.gitCommitMessage !== 'true'
    )
      return;
    setGitCommitMessage(state.operations, event.target.value);
    const commit = container.querySelector<HTMLButtonElement>('[data-action="commit-staged"]');
    if (commit && state.status)
      commit.disabled = isGitOperationBusy(state) || !canCommit(state.operations, state.status);
  });
  container.addEventListener('keydown', (event) => {
    const state = activePanels.get(container);
    if (state) handleGitHistoryKey(state, event, container);
    if (state) handleDiffKey(state.diffUi, event, container);
    if (state)
      handleGitFileKey(state.fileUi, event, container, (row) => {
        toggleGitFolder(state.fileUi, row, container, () => {
          renderPanel(state);
        });
      });
    if (
      !(event.target instanceof HTMLTextAreaElement) ||
      event.target.dataset.gitCommitMessage !== 'true'
    )
      return;
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      event.stopPropagation();
      const commit = container.querySelector<HTMLButtonElement>('[data-action="commit-staged"]');
      if (commit && !commit.disabled) commit.click();
    }
  });
  container.addEventListener('change', (event) => {
    const state = activePanels.get(container);
    if (
      !state ||
      !(event.target instanceof HTMLSelectElement) ||
      event.target.dataset.gitCommitParent !== 'true'
    )
      return;
    if (event.target.dataset.commitHash)
      void openCommitSelection(state, event.target.dataset.commitHash, Number(event.target.value));
  });
}

function handleContentAction(
  state: GitPanelState,
  element: HTMLElement,
  event: MouseEvent,
): boolean {
  if (element.dataset.action === 'git-workspace' || element.dataset.action === 'resolve-conflict') {
    if (!state.status || isGitOperationBusy(state)) return true;
    void openGitWorkspace(
      state.sessionId,
      state.status.repoRoot,
      async (request) => {
        if (activePanels.get(state.container) !== state || isGitOperationBusy(state)) return null;
        return await performMutation(state, request);
      },
      element.dataset.action === 'resolve-conflict' ? element.dataset.path : undefined,
    );
    return true;
  }
  if (element.dataset.action === 'retry-git-status') {
    void refreshPanelRead(state);
    return true;
  }
  if (
    handleGitFileControl(
      state.fileUi,
      element,
      event,
      state.container,
      () => {
        renderPanel(state);
      },
      state.operations.busy,
    )
  )
    return true;
  const data = element.dataset;
  if (
    handleDiffControl(state.diffUi, element, event, state.container, () => {
      delete state.inspectorSelection;
      renderPanel(state);
    })
  )
    return true;
  if (data.action === 'apply-diff-hunk' || data.action === 'apply-diff-selection') {
    applyDiffSelection(state, data.action === 'apply-diff-hunk' ? Number(data.hunk) : undefined);
    return true;
  }
  return false;
}

function handlePanelAction(state: GitPanelState, element: HTMLElement, event: MouseEvent): void {
  if (handleContentAction(state, element, event)) return;
  const data = element.dataset;
  if (handleMutationAction(state, data)) return;
  switch (data.action) {
    case 'toggle-diff-wrap':
      toggleDiffWrap(state);
      break;
    case 'toggle-section': {
      const section = data.section as PanelSectionId;
      if (state.expandedSections.has(section)) state.expandedSections.delete(section);
      else state.expandedSections.add(section);
      renderPanel(state);
      break;
    }
    case 'open-file':
      openFileFromButton(state, data);
      break;
    case 'open-commit':
      state.fileUi.selected.clear();
      openCommitFromButton(state, data);
      break;
    case 'path-details': {
      showGitPathDetails(state, data);
      break;
    }
    case 'open-file-viewer':
      void openReviewFileInViewer(state, data.path);
      break;
    case 'load-more-history':
      void loadMoreHistory(state);
      break;
    case 'copy-command':
    case 'insert-command':
      void runPanelCommand(state, element);
      break;
    case 'show-list':
      state.detailActive = false;
      renderPanel(state);
      focusGitSelection(state.container, state);
      break;
    case undefined:
    default:
      break;
  }
}

function buildFileMutationRequest(
  state: GitPanelState,
  data: DOMStringMap,
  status: GitStatusResponse,
): GitOperationRequest {
  const request = buildGitMutationRequest(state.sessionId, state.operations.message, data, status);
  if (data.action === 'stage-selected-files' || data.action === 'unstage-selected-files')
    request.paths = selectedGitPaths(
      state.fileUi,
      request.action === 'unstage' ? 'staged' : 'worktree',
    );
  if (data.action === 'stage-section')
    request.paths = filteredGitFiles(gitSectionFiles(status, data.section ?? ''), state.fileUi).map(
      (file) => file.path,
    );
  return request;
}

function handleMutationAction(state: GitPanelState, data: DOMStringMap): boolean {
  if (
    ![
      'stage-file',
      'unstage-file',
      'stage-inspected-file',
      'unstage-inspected-file',
      'stage-section',
      'stage-all',
      'unstage-all',
      'commit-staged',
      'stage-selected-files',
      'unstage-selected-files',
      'retry-operation',
    ].includes(data.action ?? '')
  )
    return false;
  if (state.operations.busy || state.read.phase !== 'ready' || !state.status) return true;
  if (data.action === 'retry-operation' && state.operations.pending) {
    void performMutation(state, state.operations.pending);
    return true;
  }
  if (state.operations.pending) return true;
  void performMutation(state, buildFileMutationRequest(state, data, state.status));
  return true;
}

function applyDiffSelection(state: GitPanelState, hunk?: number): void {
  const selection = state.selection;
  if (
    selection?.kind !== 'file' ||
    !selection.diff?.canSelectLines ||
    !state.status ||
    isGitOperationBusy(state) ||
    state.operations.pending
  )
    return;
  if (hunk !== undefined && !Number.isInteger(hunk)) return;
  void performMutation(
    state,
    buildGitSelectionRequest(
      state.sessionId,
      state.status.repoRoot,
      selection.path,
      selection.scope === 'staged',
      selection.diff,
      state.diffUi.selected,
      hunk,
    ),
  );
}

async function performMutation(
  state: GitPanelState,
  request: GitOperationRequest,
): Promise<GitOperationResponse | null> {
  const pending = executeGitOperation(state.operations, request);
  delete state.inspectorSelection;
  renderPanel(state);
  const result = await pending;
  if (result) acceptMutationStatus(state, request, result);
  if (result?.state === 'completed' && request.action !== 'fetch' && request.action !== 'push') {
    clearDiffSelection(state.diffUi);
    state.fileUi.selected.clear();
  }
  delete state.inspectorSelection;
  renderPanel(state);
  if (result?.state === 'completed' && state.selection?.kind === 'file')
    await openFileSelection(state, state.selection, true);
  return result;
}

function acceptMutationStatus(
  state: GitPanelState,
  request: GitOperationRequest,
  result: GitOperationResponse,
): void {
  if (result.status) {
    if (result.status.conflicted.length > 0) state.expandedSections.add('conflicts');
    state.read.sequence++;
    state.status = acceptGitRead(state.read, { ok: true, status: result.status }, state.status);
    if (state.status) pruneGitFileSelection(state.fileUi, state.status);
    if (result.state === 'completed' && request.action.startsWith('stage'))
      state.expandedSections.add('staged');
    if (result.state === 'completed' && request.action.startsWith('unstage')) {
      state.expandedSections.add('changes');
      state.expandedSections.add('untracked');
    }
    if (state.status) syncHistoryFromStatus(state, state.status);
    syncSelectionAfterStatus(state);
  }
}

async function openReviewFileInViewer(state: GitPanelState, path?: string): Promise<void> {
  if (!path || !state.status) return;
  const { openFile } = await import('../fileViewer');
  if (activePanels.get(state.container) !== state) return;
  await openFile(repositoryFilePath(state.status.repoRoot, path));
}

function openFileFromButton(state: GitPanelState, data: DOMStringMap): void {
  if (!data.path || !data.scope || !data.status) return;
  void openFileSelection(state, {
    path: data.path,
    scope: data.scope as FileScope,
    status: data.status,
    originalPath: data.originalPath,
  });
}

function showGitPathDetails(state: GitPanelState, data: DOMStringMap): void {
  const root = state.status?.repoRoot ?? state.repoRoot;
  if (!root || !data.path) return;
  const paths = [repositoryFilePath(root, data.path)];
  if (data.originalPath) paths.push(repositoryFilePath(root, data.originalPath));
  void showPathDetails(paths, [fileStatusPresentation(data.status)?.label ?? '']);
}

async function runPanelCommand(state: GitPanelState, button: HTMLElement): Promise<void> {
  const data = button.dataset;
  if (!data.command) return;
  if (button.getAttribute('aria-busy') === 'true') return;
  const copy = data.action === 'copy-command';
  const status = button
    .closest('.git-command-row')
    ?.querySelector<HTMLElement>('.git-action-status');
  button.setAttribute('aria-busy', 'true');
  button.setAttribute('disabled', '');
  if (status) {
    status.textContent = t(copy ? 'git.copying' : 'git.inserting');
    status.setAttribute('role', 'status');
  }
  let ok = false;
  try {
    if (copy) ok = await writeTextToClipboard(data.command);
    else if (canInsertCommand(state.sessionId)) {
      await pasteToTerminal(state.sessionId, data.command, false, 'git-panel-insert');
      ok = true;
    }
  } catch {
    /* The visible result below reports failure without a false execution claim. */
  }
  if (!button.isConnected) return;
  button.removeAttribute('disabled');
  button.removeAttribute('aria-busy');
  updateCommandResult(status ?? null, copy, ok);
}

function updateCommandResult(status: HTMLElement | null, copy: boolean, ok: boolean): void {
  if (!status) return;
  const success = copy ? 'git.copied' : 'git.inserted';
  const failure = copy ? 'git.copyFailed' : 'git.insertFailed';
  status.textContent = t(ok ? success : failure);
  status.setAttribute('role', ok ? 'status' : 'alert');
}

async function openFileSelection(
  state: GitPanelState,
  selection: Pick<GitFileSelection, 'path' | 'scope' | 'status' | 'originalPath'>,
  force = false,
): Promise<void> {
  const existing = getExistingFileSelection(state, selection);
  state.detailActive = true;
  if (!existing) {
    clearDiffSelection(state.diffUi);
    state.scrollPositions.delete('inspector');
  }

  if (existing && !existing.loading && existing.diff && !force) {
    renderPanel(state);
    return;
  }

  const pendingSelection = createLoadingFileSelection(selection, existing?.diff ?? null);
  state.selection = pendingSelection;
  renderPanel(state);

  const diff = await fetchDiffView(
    state.sessionId,
    state.repoRoot,
    selection.path,
    selection.scope,
  );
  const currentSelection = panelStates.get(getPanelKey(state.sessionId, state.repoRoot))?.selection;
  if (currentSelection !== pendingSelection) {
    return;
  }

  if (diff?.patchVersion !== existing?.diff?.patchVersion) clearDiffSelection(state.diffUi);
  state.selection = createResolvedFileSelection(selection, currentSelection, diff);
  renderPanel(state);
}

function getExistingFileSelection(
  state: GitPanelState,
  selection: Pick<GitFileSelection, 'path' | 'scope'>,
): GitFileSelection | null {
  return state.selection?.kind === 'file' &&
    state.selection.path === selection.path &&
    state.selection.scope === selection.scope
    ? state.selection
    : null;
}

function createLoadingFileSelection(
  selection: Pick<GitFileSelection, 'path' | 'scope' | 'status' | 'originalPath'>,
  diff: GitFileSelection['diff'],
): GitFileSelection {
  return {
    kind: 'file',
    path: selection.path,
    scope: selection.scope,
    status: selection.status,
    originalPath: selection.originalPath,
    diff,
    loading: true,
    error: null,
  };
}

function createResolvedFileSelection(
  selection: Pick<GitFileSelection, 'path' | 'scope' | 'status' | 'originalPath'>,
  currentSelection: GitFileSelection,
  diff: GitDiffViewResponse | null,
): GitFileSelection {
  return {
    kind: 'file',
    path: selection.path,
    scope: selection.scope,
    status: selection.status,
    originalPath: selection.originalPath,
    diff:
      diff && diff.files.length > 0
        ? diff
        : {
            scope: selection.scope,
            title: t(selection.scope === 'staged' ? 'git.stagedScope' : 'git.worktreeScope'),
            isTruncated: false,
            files: [createFallbackDiffFile(currentSelection)],
          },
    loading: false,
    error: diff ? null : t('git.diffUnavailable'),
  };
}

function openCommitFromButton(state: GitPanelState, data: DOMStringMap): void {
  if (data.hash) void openCommitSelection(state, data.hash);
}

async function openCommitSelection(
  state: GitPanelState,
  hash: string,
  parentIndex = 0,
): Promise<void> {
  const restoreParentFocus = captureCommitParentFocus(
    state.container,
    () => activePanels.get(state.container) === state,
  );
  const existing =
    state.selection?.kind === 'commit' &&
    state.selection.hash === hash &&
    state.selection.parentIndex === parentIndex
      ? state.selection
      : null;
  state.detailActive = true;
  if (!existing) state.scrollPositions.delete('inspector');

  if (existing && !existing.loading && existing.details) {
    renderPanel(state);
    return;
  }

  const pendingSelection: GitCommitSelection = {
    kind: 'commit',
    hash,
    parentIndex,
    details: existing?.details ?? null,
    loading: true,
    error: null,
  };
  state.selection = pendingSelection;
  renderPanel(state);

  const details = await fetchCommitDetails(state.sessionId, state.repoRoot, hash, parentIndex);
  const currentSelection = panelStates.get(getPanelKey(state.sessionId, state.repoRoot))?.selection;
  if (currentSelection !== pendingSelection) {
    return;
  }

  state.selection = {
    kind: 'commit',
    hash,
    parentIndex,
    details,
    loading: false,
    error: details ? null : t('git.commitUnavailable'),
  };
  renderPanel(state);
  restoreParentFocus();
}

async function loadMoreHistory(state: GitPanelState): Promise<void> {
  if (state.historyLoading || !state.status?.headHash) return;
  const head = state.status.headHash;
  const root = state.status.repoRoot;
  const previousCount = state.historyEntries.length;
  const wasFocused =
    document.activeElement === state.container.querySelector('[data-action="load-more-history"]');
  state.historyLoading = true;
  state.historyError = null;
  renderPanel(state);
  const entries = await fetchGitHistoryPage(
    state.sessionId,
    root,
    head,
    previousCount,
    HISTORY_PAGE_SIZE + 1,
  );
  if (!isCurrentHistorySnapshot(state, root, head)) return;
  state.historyLoading = false;
  if (entries === null) state.historyError = t('git.historyReadFailed');
  else {
    state.historyEntries.push(...entries.slice(0, HISTORY_PAGE_SIZE));
    state.hasMoreHistory = entries.length > HISTORY_PAGE_SIZE;
  }
  renderPanel(state);
  if (wasFocused && entries !== null)
    state.container.querySelectorAll<HTMLElement>('.git-commit-entry')[previousCount]?.focus();
}

function isGitOperationBusy(state: GitPanelState): boolean {
  return state.operations.busy || state.operations.pending !== null || state.read.phase !== 'ready';
}

function isCurrentHistorySnapshot(state: GitPanelState, root: string, head: string): boolean {
  return (
    activePanels.get(state.container) === state &&
    state.status?.headHash === head &&
    state.status.repoRoot === root
  );
}

function expandHistoryWhenClean(state: GitPanelState, sectionCount: number): void {
  if (sectionCount === 0 && state.previousSectionCount > 0) state.expandedSections.add('commits');
  state.previousSectionCount = sectionCount;
}
