/**
 * File Browser Tree View
 *
 * Expandable tree component with lazy-loaded directory contents.
 */

import type { FileTreeEntry, FileTreeResponse } from './treeApi';
import { fetchTree } from './treeApi';
import { t } from '../i18n';
import { patchFileStatusBadge } from '../git/fileStatus';
import { getFileVisibility } from '../fileViewer/visibility';
import { getFileIcon, formatSize } from '../fileViewer/rendering';
import { reconcileKeyedChildren } from '../../utils/domReconcile';
import { FileSelectionSurface } from '../fileViewer/selectionSurface';
import { createSelectionToolbar } from '../fileViewer/selectionToolbar';
import { FileActions } from '../fileViewer/fileActions';
import type { OperationResult } from '../fileViewer/operationsApi';
import {
  createListingControls,
  createListingHeader,
  fileType,
  matchesListing,
  modifiedLabel,
  sortListing,
  type FileViewOptions,
  type ListingControls,
  type ListingHeader,
} from '../fileViewer/listingView';

interface TreeState {
  rootPath: string;
  sessionId: string;
  expandedPaths: Set<string>;
  selectedPath: string | null;
  revealedFile: FileTreeEntry | null;
  treeData: Map<string, FileTreeEntry[]>;
  visibleEntries: Map<string, FileTreeEntry>;
  container: HTMLElement;
  rowsRoot: HTMLElement;
  controls: ListingControls;
  header: ListingHeader;
  view: FileViewOptions;
  surface: FileSelectionSurface;
  actions: FileActions;
  selectionRequest: number;
  rootRequest: number;
  excludedCount: number;
  viewSelection: string[];
  viewAnchor: string | null;
  rendering: boolean;
  savedRoots: Map<
    string,
    {
      expanded: Set<string>;
      selected: string | null;
      paths: string[];
      anchor: string | null;
      scroll: number;
    }
  >;
  onFileSelect: (entry: FileTreeEntry, focus?: boolean) => boolean | Promise<boolean>;
  onDirectoryOpen: ((entry: FileTreeEntry) => void) | undefined;
}

const treeStates = new Map<string, TreeState>();
const rowEntries = new WeakMap<HTMLElement, FileTreeEntry>();

export function getTreeSelection(sessionId: string): FileTreeEntry | null {
  const state = treeStates.get(sessionId);
  if (!state?.selectedPath) return null;
  for (const entries of state.treeData.values()) {
    const selected = entries.find((entry) => entry.fullPath === state.selectedPath);
    if (selected) return selected;
  }
  return null;
}

export function setTreeSelection(sessionId: string, path: string): void {
  const state = treeStates.get(sessionId);
  if (!state) return;
  state.selectedPath = path;
  state.viewSelection = [path];
  state.viewAnchor = path;
  state.surface.selection.restore([path]);
  renderTree(state);
}

export function getTreeEntry(sessionId: string, path: string): FileTreeEntry | undefined {
  const state = treeStates.get(sessionId);
  return state ? findEntry(state, path) : undefined;
}

/** An explicitly opened file stays visible without changing global hidden-file preferences. */
export function revealTreeFile(sessionId: string, entry: FileTreeEntry): void {
  const state = treeStates.get(sessionId);
  if (!state) return;
  const entries = state.treeData.get(state.rootPath);
  state.revealedFile = entries?.some((item) => item.fullPath === entry.fullPath) ? null : entry;
  state.selectedPath = entry.fullPath;
  retainRevealedFile(state, entries);
  const filter = state.controls.root.querySelector<HTMLInputElement>('input[type="search"]');
  if (filter?.value) {
    filter.value = '';
    filter.dispatchEvent(new Event('input'));
  }
  setTreeSelection(sessionId, entry.fullPath);
  Array.from(state.rowsRoot.querySelectorAll<HTMLElement>('.tree-node'))
    .find((node) => node.dataset.path === entry.fullPath)
    ?.scrollIntoView({ block: 'nearest' });
}

function retainRevealedFile(state: TreeState, entries: FileTreeEntry[] | undefined): void {
  const entry = state.revealedFile;
  if (
    entries &&
    entry?.fullPath === state.selectedPath &&
    !entries.some((item) => item.fullPath === entry.fullPath)
  )
    entries.push(entry);
}

export function createTreeView(
  container: HTMLElement,
  sessionId: string,
  onFileSelect: TreeState['onFileSelect'],
  onDirectoryOpen?: TreeState['onDirectoryOpen'],
  onOperationChanged: (result: OperationResult) => Promise<void> = async () => {},
): void {
  const rowsRoot = document.createElement('div');
  rowsRoot.className = 'file-tree-rows';
  rowsRoot.setAttribute('role', 'tree');
  rowsRoot.setAttribute('aria-label', t('fileBrowser.files'));
  rowsRoot.setAttribute('aria-multiselectable', 'true');
  const surface = new FileSelectionSurface(
    () =>
      [...rowsRoot.querySelectorAll<HTMLElement>('.tree-node')].filter(
        (row) => !row.closest('.tree-children[hidden]'),
      ),
    () => {
      if (!state.rendering) {
        state.viewSelection = [...surface.selection.paths];
        state.viewAnchor = surface.selection.anchorPath;
      }
      toolbar.update();
      actions.update();
    },
    (path) => {
      openSelectedPath(state, path);
    },
  );
  const toolbar = createSelectionToolbar(
    () =>
      [...surface.selection.paths].map((path) => ({
        path,
        isDirectory: findEntry(state, path)?.isDirectory ?? true,
        gitStatus: findEntry(state, path)?.gitStatus,
      })),
    (path) => {
      openSelectedPath(state, path);
    },
    () => sessionId,
    {
      active: () => surface.multiSelect,
      toggle: () => {
        surface.toggleMultiSelect();
      },
    },
  );
  const controls = createListingControls(`tree:${sessionId}`, true, (options) => {
    state.view = options;
    renderTree(state);
  });
  const header = createListingHeader((sort) => {
    controls.setSort(sort);
  });
  controls.root.insertBefore(toolbar.root, controls.root.querySelector('.file-listing-count'));
  const head = document.createElement('div');
  head.className = 'file-browser-list-head';
  head.append(controls.root, header.root);
  container.replaceChildren(head, rowsRoot);
  const footer = document.createElement('div');
  footer.className = 'file-browser-list-footer';
  const count = controls.root.querySelector('.file-listing-count');
  if (count) footer.append(count);
  const hint = document.createElement('span');
  hint.className = 'file-browser-open-hint';
  hint.textContent = t('fileBrowser.openHint');
  footer.append(hint);
  container.append(footer);
  const actions = new FileActions({
    owner: container,
    sessionId: () => sessionId,
    directory: () => state.rootPath,
    selected: () =>
      [...surface.selection.paths].map((path) => ({
        path,
        isDirectory: findEntry(state, path)?.isDirectory ?? true,
      })),
    changed: onOperationChanged,
    select: (paths) => {
      state.viewSelection = [...paths];
      state.viewAnchor = paths[0] ?? null;
      surface.selection.restore(paths);
      surface.update();
    },
  });
  const state: TreeState = {
    rootPath: '',
    sessionId,
    expandedPaths: new Set(),
    selectedPath: null,
    revealedFile: null,
    treeData: new Map(),
    visibleEntries: new Map(),
    container,
    rowsRoot,
    controls,
    header,
    view: controls.options,
    surface,
    actions,
    selectionRequest: 0,
    rootRequest: 0,
    excludedCount: 0,
    viewSelection: [],
    viewAnchor: null,
    rendering: false,
    savedRoots: new Map(),
    onFileSelect,
    onDirectoryOpen,
  };
  treeStates.set(sessionId, state);
  actions.attach(toolbar.root);
  surface.update();
}

function findEntry(state: TreeState, path: string): FileTreeEntry | undefined {
  return state.visibleEntries.get(path);
}

function openSelectedPath(state: TreeState, path: string): void {
  const entry = findEntry(state, path);
  if (!entry) return;
  if (entry.isDirectory) state.onDirectoryOpen?.(entry);
  else void selectFile(state, entry, true);
}

/** Apply a validated directory only after both navigation and listing have succeeded. */
export function applyTreeRoot(sessionId: string, rootPath: string, data: FileTreeResponse): void {
  const state = treeStates.get(sessionId);
  if (!state) return;
  state.rootRequest++;
  state.selectionRequest++;
  if (state.rootPath)
    state.savedRoots.set(state.rootPath, {
      expanded: new Set(state.expandedPaths),
      selected: state.selectedPath,
      paths: [...state.viewSelection],
      anchor: state.viewAnchor,
      scroll: state.container.scrollTop,
    });
  const saved = state.savedRoots.get(rootPath);
  state.rootPath = rootPath;
  state.revealedFile = null;
  state.expandedPaths = new Set(saved?.expanded ?? []);
  state.selectedPath = saved?.selected ?? null;
  state.viewSelection = [...(saved?.paths ?? [])];
  state.viewAnchor = saved?.anchor ?? null;
  state.surface.selection.restore(state.viewSelection, state.viewAnchor);
  state.treeData.set(rootPath, data.entries);
  state.excludedCount = data.visibility.excludedCount;
  renderTree(state);
  state.container.scrollTop = saved?.scroll ?? 0;
}

export async function applyTreeVisibility(
  sessionId: string,
  rootPath: string,
  data: FileTreeResponse,
  signal?: AbortSignal,
): Promise<void> {
  const state = treeStates.get(sessionId);
  if (!state || state.rootPath !== rootPath) return;
  const request = ++state.rootRequest;
  const scroll = state.container.scrollTop;
  const preferences = getFileVisibility();
  const loaded = new Map<string, FileTreeEntry[]>([[rootPath, data.entries]]);
  retainRevealedFile(state, data.entries);
  const expanded = [...state.expandedPaths];
  for (let index = 0; index < expanded.length; index += 4) {
    const pages = await Promise.all(
      expanded
        .slice(index, index + 4)
        .map(async (path) => ({ path, data: await fetchTree(path, sessionId, signal) })),
    );
    if (
      state.rootRequest !== request ||
      signal?.aborted ||
      state.rootPath !== rootPath ||
      getFileVisibility() !== preferences
    )
      return;
    for (const page of pages) if (page.data) loaded.set(page.path, page.data.entries);
  }
  state.treeData = loaded;
  const available = new Set(
    [...loaded.values()].flatMap((entries) => entries.map((entry) => entry.fullPath)),
  );
  state.viewSelection = state.viewSelection.filter((path) => available.has(path));
  state.excludedCount = data.visibility.excludedCount;
  renderTree(state);
  state.container.scrollTop = scroll;
}

export function destroyTreeView(sessionId: string): void {
  const state = treeStates.get(sessionId);
  if (state) {
    state.actions.dispose();
    state.selectionRequest++;
    state.rootRequest++;
  }
  treeStates.delete(sessionId);
}

function renderTree(state: TreeState): void {
  const focused = document.activeElement as HTMLElement | null;
  const hadFocus = !!focused && state.container.contains(focused);
  const entries = state.treeData.get(state.rootPath);
  if (!entries) return;
  const visible = filterEntries(state, entries);
  state.rowsRoot.className = `file-tree-rows file-listing-${state.view.mode}`;
  state.rowsRoot.setAttribute('role', state.view.mode === 'tree' ? 'tree' : 'listbox');
  state.header.update(state.view);
  state.controls.setCount(visible.length, entries.length);
  state.visibleEntries.clear();
  if (visible.length === 0) {
    const message = state.view.query
      ? 'fileBrowser.noMatches'
      : state.excludedCount
        ? 'fileBrowser.allFiltered'
        : 'fileViewer.emptyDirectory';
    state.rowsRoot.innerHTML = `<div class="tree-empty" role="status">${t(message)}</div>`;
    updateSelection(state);
    return;
  }

  renderBranch(state.rowsRoot, state, visible, 0, true);
  updateSelection(state);
  if (hadFocus && !focused.isConnected) {
    state.container.tabIndex = -1;
    state.container.focus({ preventScroll: true });
  }
}

function renderBranch(
  container: HTMLElement,
  state: TreeState,
  entries: FileTreeEntry[],
  depth: number,
  alreadyFiltered = false,
): void {
  const visible = alreadyFiltered ? entries : filterEntries(state, entries);
  reconcileKeyedChildren(container, sortListing(visible, state.view), {
    key: (entry) => entry.fullPath,
    create: () => createTreeNode(state),
    patch: (wrapper, entry) => {
      patchTreeNode(wrapper, state, entry, depth);
    },
  });
}

function filterEntries(state: TreeState, entries: FileTreeEntry[]): FileTreeEntry[] {
  if (!state.view.query) return entries;
  if (state.view.mode !== 'tree')
    return entries.filter((entry) => matchesListing(entry, state.view.query));
  const visited = new Set<string>();
  const matchesTree = (entry: FileTreeEntry): boolean => {
    if (matchesListing(entry, state.view.query)) return true;
    if (!entry.isDirectory || visited.has(entry.fullPath)) return false;
    visited.add(entry.fullPath);
    return (state.treeData.get(entry.fullPath) ?? []).some(matchesTree);
  };
  return entries.filter(matchesTree);
}

function updateSelection(state: TreeState): void {
  state.rendering = true;
  state.surface.selection.restore(state.viewSelection, state.viewAnchor);
  state.surface.update();
  state.rendering = false;
}

function shouldPreviewFile(state: TreeState, entry: FileTreeEntry): boolean {
  return (
    !entry.isDirectory &&
    (state.view.mode === 'tree' ||
      state.container.closest('.file-browser')?.getAttribute('data-file-pane') === 'detail')
  );
}

function createTreeNode(state: TreeState): HTMLElement {
  const node = document.createElement('div');
  node.className = 'tree-node';
  node.setAttribute('role', 'treeitem');
  state.surface.bind(node);
  const label = document.createElement('span');
  label.className = 'file-entry-label';
  const metadata = document.createElement('span');
  metadata.className = 'file-entry-metadata';
  for (const className of [
    'tree-indent',
    'tree-expand',
    'tree-icon',
    'tree-name',
    'file-visibility-badge',
    'tree-git-badge',
    'tree-type',
    'tree-modified',
    'tree-size',
  ]) {
    const span = document.createElement(className === 'tree-expand' ? 'button' : 'span');
    span.className = className;
    if (span instanceof HTMLButtonElement) {
      span.type = 'button';
      span.tabIndex = -1;
      span.addEventListener('click', (event) => {
        event.stopPropagation();
        const entry = rowEntries.get(node);
        if (entry?.isDirectory) void toggleExpand(state, entry);
      });
    }
    if (['tree-name', 'file-visibility-badge', 'tree-git-badge'].includes(className))
      label.append(span);
    else if (['tree-type', 'tree-modified', 'tree-size'].includes(className)) metadata.append(span);
    else node.appendChild(span);
  }
  node.querySelector('.tree-icon')?.after(label);
  node.append(metadata);
  node.addEventListener('click', (event) => {
    const entry = rowEntries.get(node);
    if (!entry) return;
    if (event.ctrlKey || event.metaKey || event.shiftKey || state.surface.multiSelect) {
      state.surface.choose(
        node,
        event.ctrlKey || event.metaKey || state.surface.multiSelect,
        event.shiftKey,
      );
      node.focus({ preventScroll: true });
      return;
    }
    if (matchMedia('(pointer: coarse)').matches && state.view.mode !== 'tree') {
      if (entry.isDirectory) {
        state.surface.choose(node);
        openSelectedPath(state, entry.fullPath);
      } else void selectFile(state, entry, true);
      return;
    }
    if (entry.isDirectory && state.view.mode === 'tree') {
      state.surface.choose(node);
      void toggleExpand(state, entry);
    } else if (shouldPreviewFile(state, entry)) {
      void selectFile(state, entry);
    } else {
      state.surface.choose(node);
      node.focus({ preventScroll: true });
    }
  });
  node.addEventListener('dblclick', (event) => {
    const entry = rowEntries.get(node);
    if (!entry) return;
    if (event.ctrlKey || event.metaKey || event.shiftKey) return;
    if (state.view.mode === 'tree' && !entry.isDirectory) return;
    event.preventDefault();
    openSelectedPath(state, entry.fullPath);
  });
  node.addEventListener('contextmenu', (event) => {
    if (!state.surface.selection.paths.has(node.dataset.path ?? '')) state.surface.choose(node);
    state.actions.showContext(event);
  });
  node.addEventListener('keydown', (event) => {
    if (state.view.mode !== 'tree' || event.altKey || event.ctrlKey || event.metaKey) return;
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
    event.preventDefault();
    event.stopPropagation();
    void navigateTreeBranch(state, node, event.key);
  });

  const wrapper = document.createElement('div');
  wrapper.className = 'tree-node-group';
  wrapper.appendChild(node);
  const children = document.createElement('div');
  children.className = 'tree-children';
  children.id = `file-children-${crypto.randomUUID()}`;
  children.setAttribute('role', 'group');
  wrapper.appendChild(children);
  return wrapper;
}

function patchTreeNode(
  wrapper: HTMLElement,
  state: TreeState,
  entry: FileTreeEntry,
  depth: number,
): void {
  const node = wrapper.firstElementChild as HTMLElement;
  rowEntries.set(node, entry);
  state.visibleEntries.set(entry.fullPath, entry);
  node.dataset.path = entry.fullPath;
  node.title = entry.fullPath;
  node.dataset.isDir = String(entry.isDirectory);
  state.surface.label(node, entry.name);
  node.setAttribute('aria-label', entry.fullPath);
  patchTreePresentation(node, state, entry, depth);
  patchTreeMetadata(node, entry);
  const children = wrapper.lastElementChild as HTMLElement;
  const expanded =
    state.view.mode === 'tree' && entry.isDirectory && state.expandedPaths.has(entry.fullPath);
  children.hidden = !expanded;
  if (state.view.mode === 'tree') node.setAttribute('aria-owns', children.id);
  else node.removeAttribute('aria-owns');
  if (expanded) renderBranch(children, state, state.treeData.get(entry.fullPath) ?? [], depth + 1);
}

function patchTreePresentation(
  node: HTMLElement,
  state: TreeState,
  entry: FileTreeEntry,
  depth: number,
): void {
  node.setAttribute('role', state.view.mode === 'tree' ? 'treeitem' : 'option');
  if (state.view.mode === 'tree') node.setAttribute('aria-level', String(depth + 1));
  else node.removeAttribute('aria-level');
  const indent = node.querySelector<HTMLElement>('.tree-indent');
  if (indent) indent.style.width = state.view.mode === 'tree' ? `${depth * 16}px` : '';
  const expanded =
    state.view.mode === 'tree' && entry.isDirectory && state.expandedPaths.has(entry.fullPath);
  if (state.view.mode === 'tree' && entry.isDirectory)
    node.setAttribute('aria-expanded', String(expanded));
  else node.removeAttribute('aria-expanded');
  const expand = node.querySelector<HTMLElement>('.tree-expand');
  if (expand) {
    expand.textContent =
      state.view.mode === 'tree' && entry.isDirectory ? (expanded ? '▾' : '▸') : '';
    expand.classList.toggle('expandable', entry.isDirectory);
    expand.style.visibility = entry.isDirectory ? '' : 'hidden';
    expand.setAttribute('aria-hidden', String(!entry.isDirectory));
    const label = `${t(expanded ? 'fileBrowser.collapseFolder' : 'fileBrowser.expandFolder')}: ${entry.name}`;
    expand.setAttribute('aria-label', label);
    expand.title = label;
  }
}

function patchTreeMetadata(node: HTMLElement, entry: FileTreeEntry): void {
  const text = (selector: string, value: string): void => {
    const element = node.querySelector<HTMLElement>(selector);
    if (element && element.textContent !== value) element.textContent = value;
  };
  text('.tree-icon', getFileIcon(entry.name, entry.isDirectory));
  text('.tree-name', entry.name);
  text(
    '.file-visibility-badge',
    [entry.isHidden ? t('fileBrowser.hidden') : '', entry.isIgnored ? t('fileBrowser.ignored') : '']
      .filter(Boolean)
      .join(', '),
  );
  const gitBadge = node.querySelector<HTMLElement>('.tree-git-badge');
  if (gitBadge) patchFileStatusBadge(gitBadge, node, entry.gitStatus, entry.isDirectory);
  text('.tree-type', fileType(entry));
  text('.tree-modified', modifiedLabel(entry));
  text('.tree-size', !entry.isDirectory && entry.size != null ? formatSize(entry.size) : '');
  for (const selector of [
    '.file-visibility-badge',
    '.tree-git-badge',
    '.tree-type',
    '.tree-modified',
    '.tree-size',
  ]) {
    hideEmptyMetadata(node, selector);
  }
}

function hideEmptyMetadata(node: HTMLElement, selector: string): void {
  const span = node.querySelector<HTMLElement>(selector);
  if (span) span.style.display = span.textContent ? '' : 'none';
}

async function selectFile(state: TreeState, entry: FileTreeEntry, focus = false): Promise<void> {
  const request = ++state.selectionRequest;
  const revision = state.surface.revision;
  const accepted = await state.onFileSelect(entry, focus);
  if (!accepted || request !== state.selectionRequest) return;
  state.selectedPath = entry.fullPath;
  if (revision !== state.surface.revision) return;
  state.viewSelection = [entry.fullPath];
  state.viewAnchor = entry.fullPath;
  state.surface.selection.restore([entry.fullPath]);
  state.surface.update();
}

async function navigateTreeBranch(state: TreeState, node: HTMLElement, key: string): Promise<void> {
  const entry = rowEntries.get(node);
  if (!entry) return;
  const expanded = state.expandedPaths.has(entry.fullPath);
  if (
    entry.isDirectory &&
    ((key === 'ArrowLeft' && expanded) || (key === 'ArrowRight' && !expanded))
  ) {
    await toggleExpand(state, entry);
    return;
  }
  const target =
    key === 'ArrowRight'
      ? node.parentElement?.querySelector<HTMLElement>(
          '.tree-children > .tree-node-group > .tree-node',
        )
      : (node.parentElement?.parentElement?.closest('.tree-node-group')
          ?.firstElementChild as HTMLElement | null);
  if (target && !target.closest('.tree-children[hidden]')) {
    state.surface.choose(target);
    target.focus();
  }
}

async function toggleExpand(state: TreeState, entry: FileTreeEntry): Promise<void> {
  if (state.expandedPaths.has(entry.fullPath)) {
    state.expandedPaths.delete(entry.fullPath);
  } else {
    state.expandedPaths.add(entry.fullPath);
    const request = state.rootRequest;
    const data = await fetchTree(entry.fullPath, state.sessionId);
    if (request !== state.rootRequest || !state.expandedPaths.has(entry.fullPath)) return;
    if (data) {
      state.treeData.set(entry.fullPath, data.entries);
    }
  }
  renderTree(state);
}
