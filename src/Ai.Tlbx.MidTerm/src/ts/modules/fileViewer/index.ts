import { createPdfView } from '../fileViewer/pdfView';
/**
 * File Viewer Module
 *
 * Provides a modal viewer/editor for files and directories detected in terminal output.
 * Supports inline preview for images, video, audio, PDF, and editable text files.
 * Binary files are shown as line-numbered hex/ASCII dumps.
 * Directories show a browsable file listing.
 */

import type { FilePathInfo, DirectoryEntry, DirectoryListResponse } from '../../types';
import { createLogger } from '../logging';
import { t } from '../i18n';
import { registerBackButtonLayer } from '../navigation/backButtonGuard';
import {
  $activeSessionId,
  $fileViewerDocked,
  $dockedFilePath,
  $commandsPanelDocked,
  $gitPanelDocked,
} from '../../stores';
import { handleDockLayoutChange } from '../terminal/scaling';
import { closeCommandsDock } from '../commands/dock';
import { closeGitDock } from '../git/gitDock';
import { adjustInnerDockPositions, updateAllDockMargins } from '../web';
import {
  getFileName,
  formatSize,
  formatViewerHeaderSubtitle,
  formatBinaryDump,
  createLineNumberedViewer,
} from './rendering';
import {
  copyFileToClipboard,
  downloadFile as triggerFileDownload,
  loadBinaryPreviewPage,
  resolveFilePreviewKind,
} from './shared';
import { createImageView } from './imageView';
import {
  FileEditState,
  confirmFileNavigation,
  releaseFileEdits,
  trackFileEdits,
} from './editState';
import { FileViewLoad, beginFileOpen, isFileOpenCurrent, cancelFileOpen } from './loadState';
import { readFileDocument, writeFileDocument, type FileDocument } from './documentApi';
import { createFileSaveStatus } from './saveStatus';
import { createDocumentNotice } from './documentNotice';
import { FileNavigationHistory, type FileNavigationSnapshot } from './navigationHistory';
import type { Schemas } from '../../api/types';
import { createFileVisibilityControls, getFileVisibility } from './visibility';
import { DirectoryListing } from './directoryListing';
import { resizeFilePane } from './paneResize';
import { getWebPreviewDockWidth } from '../web/webDock';
import { createTextView, getDocumentViewMode } from './textView';
import { createFileViewerFocus } from './modalFocus';
import { pathDetailsButton } from './pathDetails';

const log = createLogger('fileViewer');

let modal: HTMLElement | null = null;
let modalFocus: ReturnType<typeof createFileViewerFocus> | null = null;
let currentPath: string | null = null;
let currentSessionId: string | null = null;
const navigationHistory = new FileNavigationHistory();
let parentPath: string | null = null;
let navigationBar: HTMLElement | null = null;
let releaseDirectoryVisibility: (() => void) | null = null;
let directoryVisibilityGeneration = 0;
let directoryListing: DirectoryListing | null = null;
let lastVideoVolume = 0.15;

let isDirty = false;
let isFullContentLoaded = true;
let currentContent = '';
let currentContentIsPartial = false;
let currentFileInfo: FilePathInfo | null = null;
let releaseBackButtonLayer: (() => void) | null = null;
let releaseDockBackButtonLayer: (() => void) | null = null;
let currentEdit: FileEditState | null = null;
let currentEditOwner: HTMLElement | null = null;
let viewerLoad: FileViewLoad | null = null;

function beginViewerLoad(path: string, container: HTMLElement): FileViewLoad {
  viewerLoad?.abort();
  viewerLoad = new FileViewLoad(path, currentSessionId, container);
  return viewerLoad;
}

function cancelViewerLoad(): void {
  cancelFileOpen();
  viewerLoad?.abort();
  viewerLoad = null;
}

export function initFileViewer(): void {
  modal = document.getElementById('file-viewer-modal');
  if (!modal) {
    log.warn(() => 'File viewer modal element not found');
    return;
  }

  const backdrop = modal.querySelector('.modal-backdrop');
  modalFocus = createFileViewerFocus(modal, escapeViewer, () =>
    document.querySelector<HTMLElement>(
      `.session-wrapper[data-session-id="${CSS.escape($activeSessionId.get() ?? '')}"] .session-tab[data-tab="terminal"]`,
    ),
  );
  const closeBtn = modal.querySelector('.modal-close');
  const maximizeBtn = modal.querySelector('#file-viewer-maximize');
  const dockBtn = modal.querySelector('#file-viewer-dock-btn');
  setupPathDetails();

  backdrop?.addEventListener('click', () => void requestCloseViewer());
  closeBtn?.addEventListener('click', () => void requestCloseViewer());
  maximizeBtn?.addEventListener('click', () => {
    setViewerMaximized(!isViewerMaximized());
  });
  setViewerMaximized(false);
  document.addEventListener('tlbx-locale-changed', () => {
    setViewerMaximized(isViewerMaximized());
  });
  dockBtn?.addEventListener('click', dockViewer);

  modal.querySelector('#file-viewer-save')?.addEventListener('click', () => void saveFile());
  bindModalFileActions(modal);
  initViewerNavigation(modal);
  $activeSessionId.listen((sessionId) => {
    if (
      currentSessionId &&
      sessionId !== currentSessionId &&
      modal &&
      !modal.classList.contains('hidden') &&
      !currentEdit?.dirty
    )
      closeViewer();
  });

  // Dock panel buttons
  const dockPanel = document.getElementById('file-viewer-dock');
  if (dockPanel) {
    setupFileDockResize(dockPanel);
    dockPanel.querySelector('#dock-close')?.addEventListener('click', () => {
      void requestCloseFileViewerDock();
    });
    dockPanel.querySelector('#dock-undock')?.addEventListener('click', undockViewer);
    dockPanel.querySelector('#dock-refresh')?.addEventListener('click', () => {
      void refreshDock();
    });
    dockPanel.querySelector('#dock-download')?.addEventListener('click', () => {
      const path = $dockedFilePath.get();
      if (path) {
        triggerFileDownload(path, currentSessionId ?? $activeSessionId.get());
      }
    });
    dockPanel.querySelector('#dock-copy')?.addEventListener('click', () => {
      void copyCurrentFileToClipboard($dockedFilePath.get());
    });
    dockPanel.querySelector('#dock-save')?.addEventListener('click', () => void saveFile());
  }

  document.addEventListener('keydown', (e) => {
    if (document.querySelector('.modal-overlay')) return;
    if (e.key === 'Escape' && modal && !modal.classList.contains('hidden')) {
      e.preventDefault();
      escapeViewer();
    }

    if ((e.ctrlKey || e.metaKey) && e.key === 's') {
      const modalVisible = modal && !modal.classList.contains('hidden');
      const dockVisible = $fileViewerDocked.get();
      if ((modalVisible || dockVisible) && isDirty && isFullContentLoaded) {
        e.preventDefault();
        void saveFile();
      }
    }
  });

  log.info(() => 'File viewer initialized');
}

function setupPathDetails(): void {
  const paths = (): string[] => (currentPath ? [currentPath] : []);
  document
    .querySelectorAll('.file-viewer-header-actions, .file-viewer-dock-actions')
    .forEach((actions) => {
      actions.prepend(pathDetailsButton(paths));
    });
}

function setupFileDockResize(panel: HTMLElement): void {
  const parent = panel.parentElement;
  if (!parent) return;
  const grip = document.createElement('div');
  grip.className = 'file-pane-resize-grip file-dock-resize-grip';
  grip.setAttribute('aria-label', t('fileViewer.resizeDock'));
  grip.setAttribute('aria-controls', panel.id);
  panel.prepend(grip);
  const web = document.getElementById('web-preview-dock');
  resizeFilePane({
    panel,
    grip,
    key: 'tlbx-file-dock-width',
    defaultWidth: 400,
    direction: -1,
    bounds: () => {
      const available = parent.clientWidth - getWebPreviewDockWidth();
      const overlay = window.innerWidth <= 768 || available < 500;
      panel.dataset.fileOverlay = String(overlay);
      const max = Math.max(250, Math.min(1000, available - 260));
      return { min: 250, max, enabled: !overlay };
    },
    observe: [parent, panel, ...(web ? [web] : [])],
    changed: () => {
      adjustInnerDockPositions();
      updateAllDockMargins();
      handleDockLayoutChange();
    },
  });
}

function initViewerNavigation(viewer: HTMLElement): void {
  navigationBar = document.createElement('nav');
  navigationBar.className = 'file-viewer-navigation';
  navigationBar.setAttribute('aria-label', t('fileViewer.navigation'));
  for (const [action, symbol] of [
    ['back', '←'],
    ['forward', '→'],
    ['up', '↑'],
  ] as const) {
    const button = document.createElement('button');
    button.type = 'button';
    button.dataset.fileNavigation = action;
    button.textContent = `${symbol} ${t(`fileViewer.${action}`)}`;
    button.addEventListener('click', () => {
      void navigateHistory(action);
    });
    navigationBar.appendChild(button);
  }
  viewer.querySelector('.modal-header')?.insertAdjacentElement('afterend', navigationBar);
  updateNavigationButtons();
}

function bindModalFileActions(viewer: HTMLElement): void {
  viewer.querySelector('#file-viewer-download')?.addEventListener('click', () => {
    if (currentPath) triggerFileDownload(currentPath, currentSessionId);
  });
  viewer.querySelector('#file-viewer-copy')?.addEventListener('click', () => {
    void copyCurrentFileToClipboard();
  });
}

function resetEditState(): void {
  directoryListing?.dispose();
  directoryListing = null;
  releaseDirectoryVisibility?.();
  releaseDirectoryVisibility = null;
  if (currentEditOwner) releaseFileEdits(currentEditOwner);
  currentEditOwner = null;
  currentEdit = null;
  isDirty = false;
  isFullContentLoaded = true;
  currentContent = '';
  currentContentIsPartial = false;
  currentFileInfo = null;
  updateSaveButtonVisibility(false);
  updateFileActionButtons(null);
}

function confirmCurrentFileNavigation(): Promise<boolean> {
  return currentEditOwner ? confirmFileNavigation(currentEditOwner) : Promise.resolve(true);
}

async function requestCloseViewer(): Promise<void> {
  if (await confirmCurrentFileNavigation()) closeViewer();
}

export async function requestCloseFileViewerDock(): Promise<boolean> {
  if (!(await confirmCurrentFileNavigation())) return false;
  closeFileViewerDock();
  return true;
}

function registerDockBackLayer(): void {
  releaseDockBackButtonLayer?.();
  releaseDockBackButtonLayer = registerBackButtonLayer(() => {
    if (navigationHistory.canBack) void navigateHistory('back');
    else void requestCloseFileViewerDock();
  });
}

function isViewerMaximized(): boolean {
  return (
    modal?.querySelector('.file-viewer-modal-content')?.classList.contains('is-maximized') ?? false
  );
}

function setViewerMaximized(maximized: boolean): void {
  modal?.querySelector('.file-viewer-modal-content')?.classList.toggle('is-maximized', maximized);
  const button = modal?.querySelector<HTMLButtonElement>('#file-viewer-maximize');
  if (!button) return;
  const label = t(maximized ? 'fileViewer.restoreSize' : 'fileViewer.maximize');
  button.title = label;
  button.setAttribute('aria-label', label);
  button.setAttribute('aria-pressed', String(maximized));
  button
    .querySelector('path')
    ?.setAttribute(
      'd',
      maximized
        ? 'M4 1h11v11h-3v3H1V4h3V1zm1 1v2h7v7h2V2H5zM2 5v9h9V5H2z'
        : 'M2 2h5v1H3v4H2V2zm7 0h5v5h-1V3H9V2zM3 9v4h4v1H2V9h1zm10 0v5H9v-1h4V9h1z',
    );
}

function escapeViewer(): void {
  if (isViewerMaximized()) setViewerMaximized(false);
  else void requestCloseViewer();
}

function dockViewer(): void {
  if (!currentPath) return;
  const source = modal?.querySelector<HTMLElement>('.file-viewer-body');
  const dockPanel = document.getElementById('file-viewer-dock');
  const target = dockPanel?.querySelector<HTMLElement>('.file-viewer-dock-body');
  if (!dockPanel || !source || !target) return;

  // Mutual exclusion: close sidebar docks if open
  if ($commandsPanelDocked.get()) closeCommandsDock();
  if ($gitPanelDocked.get()) closeGitDock();

  // Update stores and show dock
  $dockedFilePath.set(currentPath);
  $fileViewerDocked.set(true);

  const terminalPage = document.getElementById('app');
  dockPanel.classList.remove('hidden');
  terminalPage?.classList.add('file-viewer-docked');

  updateDockHeader(currentPath);
  if (navigationBar)
    dockPanel
      .querySelector('.file-viewer-dock-header')
      ?.insertAdjacentElement('afterend', navigationBar);
  transferViewerBody(source, target);
  hideModal(false);
  registerDockBackLayer();
  updateSaveButtonVisibility(currentEdit?.dirty ?? false);

  adjustInnerDockPositions();
  updateAllDockMargins();
  handleDockLayoutChange();
}

export function closeFileViewerDock(): void {
  cancelViewerLoad();
  hideFileDock();
  currentPath = null;
  currentSessionId = null;
  navigationHistory.clear();
  parentPath = null;
  resetEditState();
}

function hideFileDock(): void {
  releaseDockBackButtonLayer?.();
  releaseDockBackButtonLayer = null;
  const dockPanel = document.getElementById('file-viewer-dock');
  const terminalPage = document.getElementById('app');

  if (dockPanel) {
    dockPanel.classList.add('hidden');
  }
  terminalPage?.classList.remove('file-viewer-docked');

  $fileViewerDocked.set(false);
  $dockedFilePath.set(null);
  if (modal?.classList.contains('hidden')) modalFocus?.restore();

  adjustInnerDockPositions();
  updateAllDockMargins();
  handleDockLayoutChange();
}

export function getFileViewerNavigationState(): FileNavigationSnapshot {
  captureCurrentVisit();
  return navigationHistory.snapshot();
}

export function openFileViewerDock(path: string, state?: FileNavigationSnapshot): void {
  void restoreFileViewerDock(path, state);
}

async function restoreFileViewerDock(path: string, state?: FileNavigationSnapshot): Promise<void> {
  const request = beginFileOpen();
  if (!(await confirmCurrentFileNavigation()) || !isFileOpenCurrent(request)) return;
  registerDockBackLayer();
  $dockedFilePath.set(path);
  $fileViewerDocked.set(true);
  currentPath = path;
  currentSessionId = request.sessionId;
  navigationHistory.clear();
  if (state) navigationHistory.restore(state);
  navigationHistory.push(path);

  const dockPanel = document.getElementById('file-viewer-dock');
  const terminalPage = document.getElementById('app');
  if (dockPanel) dockPanel.classList.remove('hidden');
  if (navigationBar)
    dockPanel
      ?.querySelector('.file-viewer-dock-header')
      ?.insertAdjacentElement('afterend', navigationBar);
  terminalPage?.classList.add('file-viewer-docked');
  hideModal(false);

  void renderInDock(path);

  adjustInnerDockPositions();
  updateAllDockMargins();
  handleDockLayoutChange();
}

function undockViewer(): void {
  if (!currentPath || !modal) return;
  const source = document.querySelector<HTMLElement>('#file-viewer-dock .file-viewer-dock-body');
  const target = modal.querySelector<HTMLElement>('.file-viewer-body');
  if (!source || !target) return;
  showModal();
  updateModalHeader(currentPath);
  transferViewerBody(source, target);
  hideFileDock();
  updateSaveButtonVisibility(currentEdit?.dirty ?? false);
}

function transferViewerBody(source: HTMLElement, target: HTMLElement): void {
  const scrollTop = source.scrollTop;
  const scrollLeft = source.scrollLeft;
  const editor = source.querySelector<HTMLTextAreaElement>('textarea');
  const editorHadFocus = document.activeElement === editor;
  const editorScrollTop = editor?.scrollTop ?? 0;
  const editorScrollLeft = editor?.scrollLeft ?? 0;
  const start = editor?.selectionStart ?? 0;
  const end = editor?.selectionEnd ?? 0;
  target.replaceChildren(...source.childNodes);
  if (viewerLoad?.container === source) viewerLoad.container = target;
  if (currentEdit && currentEditOwner === source) {
    releaseFileEdits(source);
    currentEditOwner = target;
    trackFileEdits(target, currentEdit, currentSessionId ?? '');
  }
  requestAnimationFrame(() => {
    target.scrollTop = scrollTop;
    target.scrollLeft = scrollLeft;
    if (editor?.isConnected) {
      if (editorHadFocus) editor.focus({ preventScroll: true });
      editor.setSelectionRange(start, end);
      editor.scrollTop = editorScrollTop;
      editor.scrollLeft = editorScrollLeft;
    }
  });
}

async function refreshDock(): Promise<void> {
  const path = $dockedFilePath.get();
  const sessionId = currentSessionId;
  if (
    path &&
    (await confirmCurrentFileNavigation()) &&
    path === $dockedFilePath.get() &&
    sessionId === currentSessionId
  ) {
    await renderInDock(path);
  }
}

async function renderInDock(path: string): Promise<void> {
  const dockPanel = document.getElementById('file-viewer-dock');
  if (!dockPanel) return;

  const titleEl = dockPanel.querySelector('.file-viewer-dock-title');
  const pathEl = dockPanel.querySelector('.file-viewer-dock-path');
  const bodyEl = dockPanel.querySelector<HTMLElement>('.file-viewer-dock-body');
  if (!bodyEl) return;

  if (titleEl || pathEl) {
    updateDockHeader(path);
  }
  captureCurrentVisit();
  bodyEl.innerHTML = `<div class="file-viewer-loading">${t('fileViewer.loading')}</div>`;

  resetEditState();

  const load = beginViewerLoad(path, bodyEl);
  parentPath = null;
  updateNavigationButtons();
  await updateNavigation(load);
  if (!load.isActive()) return;
  const info = await checkFilePath(load);
  if (!load.isActive()) return;

  if (!info || !info.exists) {
    load.container.innerHTML = `<div class="file-viewer-error">${t('fileViewer.fileNotFound')}</div>`;
    updateFileActionButtons(null);
    return;
  }

  currentFileInfo = info;
  updateFileActionButtons(info);

  if (info.isDirectory) {
    await renderDirectory(load);
  } else {
    await renderFile(load, info);
  }
}

function hideModal(restoreFocus = true): void {
  setViewerMaximized(false);
  releaseBackButtonLayer?.();
  releaseBackButtonLayer = null;

  if (modal) {
    modal.classList.add('hidden');
  }
  if (restoreFocus) modalFocus?.restore();
}

function showModal(): void {
  const opening = modal?.classList.contains('hidden');
  if (opening) modalFocus?.remember($fileViewerDocked.get());
  if (navigationBar)
    modal?.querySelector('.modal-header')?.insertAdjacentElement('afterend', navigationBar);
  if (!releaseBackButtonLayer) {
    releaseBackButtonLayer = registerBackButtonLayer(() => {
      if (isViewerMaximized()) setViewerMaximized(false);
      else if (navigationHistory.canBack) void navigateHistory('back');
      else void requestCloseViewer();
    });
  }
  modal?.classList.remove('hidden');
  if (opening) modalFocus?.enter();
}

function closeViewer(): void {
  cancelViewerLoad();
  hideModal();
  currentPath = null;
  currentSessionId = null;
  navigationHistory.clear();
  resetEditState();
}

export async function openFile(
  path: string,
  info?: FilePathInfo | null,
  request = beginFileOpen(),
  historyAction: 'push' | 'back' | 'forward' = 'push',
): Promise<void> {
  if (!modal) {
    log.error(() => 'File viewer modal not initialized');
    return;
  }

  if (!isFileOpenCurrent(request)) return;
  if (!(await confirmCurrentFileNavigation()) || !isFileOpenCurrent(request)) return;

  commitNavigation(path, request.sessionId, historyAction);

  currentPath = path;
  currentSessionId = request.sessionId;
  const bodyEl = viewerBodyForOpen(path);
  if (!bodyEl) return;

  updateViewerHeaders(path);
  bodyEl.innerHTML = `<div class="file-viewer-loading">${t('fileViewer.loading')}</div>`;

  resetEditState();
  const load = beginViewerLoad(path, bodyEl);
  await updateNavigation(load);
  if (!load.isActive()) return;

  if (!info) {
    info = await checkFilePath(load);
  }
  if (!load.isActive()) return;

  if (!info || !info.exists) {
    load.container.innerHTML = `<div class="file-viewer-error">${t('fileViewer.fileNotFound')}</div>`;
    updateFileActionButtons(null);
    return;
  }

  currentFileInfo = info;
  updateFileActionButtons(info);

  if (info.isDirectory) {
    await renderDirectory(load);
  } else {
    await renderFile(load, info);
  }
}

function viewerBodyForOpen(path: string): HTMLElement | null {
  if ($fileViewerDocked.get()) {
    $dockedFilePath.set(path);
    return document.querySelector<HTMLElement>('#file-viewer-dock .file-viewer-dock-body');
  }
  showModal();
  return modal?.querySelector<HTMLElement>('.file-viewer-body') ?? null;
}

function commitNavigation(
  path: string,
  sessionId: string | null,
  action: 'push' | 'back' | 'forward',
): void {
  captureCurrentVisit();
  if (currentSessionId !== sessionId) navigationHistory.clear();
  if (action === 'back') navigationHistory.back();
  else if (action === 'forward') navigationHistory.forward();
  else navigationHistory.push(path);
  parentPath = null;
  updateNavigationButtons();
}

function captureCurrentVisit(): void {
  const previous = navigationHistory.current;
  if (previous && viewerLoad) {
    previous.scrollTop = viewerLoad.container.scrollTop;
    previous.selectedPaths = [...(directoryListing?.surface.selection.paths ?? [])];
    previous.selectionAnchor = directoryListing?.surface.selection.anchorPath ?? null;
  }
}

function updateNavigationButtons(): void {
  for (const button of navigationBar?.querySelectorAll<HTMLButtonElement>('button') ?? []) {
    const action = button.dataset.fileNavigation;
    button.disabled =
      action === 'back'
        ? !navigationHistory.canBack
        : action === 'forward'
          ? !navigationHistory.canForward
          : !parentPath;
  }
}

async function updateNavigation(load: FileViewLoad): Promise<void> {
  const params = new URLSearchParams({ path: load.path });
  if (load.sessionId) params.set('sessionId', load.sessionId);
  try {
    const response = await fetch(`/api/files/navigation?${params}`, { signal: load.signal });
    if (!response.ok) return;
    const result = (await response.json()) as Schemas['FileNavigationResponse'];
    if (load.isActive()) parentPath = result.parentPath;
  } catch {
    /* Existing visit history remains usable if hierarchy lookup fails. */
  }
  if (load.isActive()) updateNavigationButtons();
}

async function navigateHistory(action: 'back' | 'forward' | 'up'): Promise<void> {
  const path =
    action === 'back'
      ? navigationHistory.backPath
      : action === 'forward'
        ? navigationHistory.forwardPath
        : parentPath;
  if (!path) return;
  await openFile(path, null, beginFileOpen(currentSessionId), action === 'up' ? 'push' : action);
}

async function checkFilePath(load: FileViewLoad): Promise<FilePathInfo | null> {
  try {
    const sessionId = load.sessionId;
    const url = sessionId
      ? `/api/files/check?sessionId=${encodeURIComponent(sessionId)}`
      : '/api/files/check';
    const resp = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ paths: [load.path] }),
      signal: load.signal,
    });
    if (!resp.ok) return null;
    const data = (await resp.json()) as { results: Record<string, FilePathInfo> };
    return load.isActive() ? data.results[load.path] || null : null;
  } catch (e) {
    if (!load.isActive()) return null;
    log.error(() => `Failed to check file path: ${String(e)}`);
    return null;
  }
}

async function renderDirectory(load: FileViewLoad): Promise<void> {
  const generation = ++directoryVisibilityGeneration;
  const path = load.path;
  try {
    const sessionId = load.sessionId;
    let url = `/api/files/list?path=${encodeURIComponent(path)}`;
    if (sessionId) {
      url += `&sessionId=${encodeURIComponent(sessionId)}`;
    }
    const preferences = getFileVisibility();
    url += `&showHidden=${String(preferences.showHidden)}&showIgnored=${String(preferences.showIgnored)}`;
    const resp = await fetch(url, { signal: load.signal });
    if (!load.isActive() || generation !== directoryVisibilityGeneration) return;
    if (!resp.ok) {
      const body = await resp.text().catch(() => '');
      if (!load.isActive()) return;
      log.error(() => `List directory failed: ${resp.status} ${resp.statusText} ${body}`);
      showDirectoryLoadError(load.container);
      return;
    }

    const data = (await resp.json()) as DirectoryListResponse;
    if (!load.isActive()) return;
    if (getFileVisibility() !== preferences) {
      await renderDirectory(load);
      return;
    }
    renderDirectoryListing(data.entries, path, load.container, data.visibility.excludedCount > 0);
    releaseDirectoryVisibility?.();
    load.container.querySelector('.file-visibility-controls')?.remove();
    load.container.querySelector('.file-directory-filter-error')?.remove();
    const controls = createFileVisibilityControls(() => {
      if (load.isActive()) void renderDirectory(load);
    });
    controls.update(data.visibility);
    load.container.prepend(controls.root);
    releaseDirectoryVisibility = controls.dispose;
  } catch (e) {
    if (!load.isActive()) return;
    log.error(() => `Failed to list directory: ${String(e)}`);
    showDirectoryLoadError(load.container);
  }
}

function showDirectoryLoadError(container: HTMLElement): void {
  if (!container.querySelector('.file-list')) {
    container.innerHTML = `<div class="file-viewer-error">${t('fileViewer.failedToList')}</div>`;
    return;
  }
  let error = container.querySelector<HTMLElement>('.file-directory-filter-error');
  if (!error) {
    error = document.createElement('div');
    error.className = 'file-directory-filter-error';
    error.setAttribute('role', 'alert');
    container.prepend(error);
  }
  error.textContent = t('fileViewer.failedToList');
}

function renderDirectoryListing(
  entries: DirectoryEntry[],
  basePath: string,
  container: Element,
  filtered = false,
): void {
  const reused =
    directoryListing?.path === basePath && directoryListing.root.parentElement === container;
  if (!reused) {
    const sessionId = currentSessionId;
    directoryListing = new DirectoryListing(
      basePath,
      sessionId,
      (path) => {
        void openFile(path, null, beginFileOpen(sessionId));
      },
      async () => {
        const load = viewerLoad;
        if (load?.path === basePath && load.sessionId === sessionId && load.isActive())
          await renderDirectory(load);
      },
    );
    container.replaceChildren(directoryListing.root);
    const visit = navigationHistory.current;
    if (visit?.path === basePath) {
      directoryListing.surface.selection.restore(visit.selectedPaths, visit.selectionAnchor);
      requestAnimationFrame(() => {
        if (currentPath === basePath) (container as HTMLElement).scrollTop = visit.scrollTop;
      });
    }
  }
  directoryListing?.render(entries, filtered);
}

function buildViewUrl(
  path: string,
  sessionId = currentSessionId ?? $activeSessionId.get(),
): string {
  let url = `/api/files/view?path=${encodeURIComponent(path)}`;
  if (sessionId) {
    url += `&sessionId=${encodeURIComponent(sessionId)}`;
  }
  return url;
}

function createCodeStack(): HTMLDivElement {
  const stack = document.createElement('div');
  stack.className = 'file-viewer-code-stack';
  return stack;
}

function updateModalHeader(path: string, metadata?: string | null): void {
  if (!modal) {
    return;
  }

  const titleEl = modal.querySelector('.file-viewer-title');
  const pathEl = modal.querySelector('.file-viewer-path');
  if (titleEl) {
    titleEl.textContent = getFileName(path);
    titleEl.setAttribute('title', path);
  }
  if (pathEl) {
    pathEl.textContent = formatViewerHeaderSubtitle(path, metadata);
    pathEl.setAttribute('title', path);
  }
}

function updateDockHeader(path: string, metadata?: string | null): void {
  const dockPanel = document.getElementById('file-viewer-dock');
  if (!dockPanel) {
    return;
  }

  const titleEl = dockPanel.querySelector('.file-viewer-dock-title');
  const pathEl = dockPanel.querySelector('.file-viewer-dock-path');
  if (titleEl) {
    titleEl.textContent = getFileName(path);
    titleEl.setAttribute('title', path);
  }
  if (pathEl) {
    pathEl.textContent = formatViewerHeaderSubtitle(path, metadata);
    pathEl.setAttribute('title', path);
  }
}

function updateViewerHeaders(path: string, metadata?: string | null): void {
  updateModalHeader(path, metadata);
  updateDockHeader(path, metadata);
}

function formatBinaryHeaderMetadata(info: FilePathInfo): string {
  const parts = [info.mimeType || t('fileViewer.binaryFile')];
  if (info.size != null) {
    parts.push(formatSize(info.size));
  }
  return parts.join(' | ');
}

async function renderFile(load: FileViewLoad, info: FilePathInfo): Promise<void> {
  const path = load.path;
  const container = load.container;
  const mime = info.mimeType || 'application/octet-stream';
  const viewUrl = buildViewUrl(path, load.sessionId);
  const previewKind = resolveFilePreviewKind(path, mime, info.isText);

  if (previewKind === 'image') {
    updateSaveButtonVisibility(false);
    container.innerHTML = '';
    container.appendChild(createImageView(viewUrl, getFileName(path)));
  } else if (previewKind === 'video') {
    updateSaveButtonVisibility(false);
    const video = document.createElement('video');
    video.className = 'file-viewer-video';
    video.controls = true;
    video.autoplay = true;
    video.muted = true;
    video.src = viewUrl;
    video.volume = lastVideoVolume;
    video.addEventListener('volumechange', () => {
      if (!video.muted) {
        lastVideoVolume = video.volume;
      }
    });
    video.addEventListener(
      'click',
      () => {
        if (video.muted) {
          video.muted = false;
          video.volume = lastVideoVolume;
        }
      },
      { once: true },
    );
    container.innerHTML = '';
    container.appendChild(video);
  } else if (previewKind === 'audio') {
    updateSaveButtonVisibility(false);
    container.innerHTML = `<audio class="file-viewer-audio" controls src="${viewUrl}"></audio>`;
  } else if (previewKind === 'pdf') {
    updateSaveButtonVisibility(false);
    container.replaceChildren(
      createPdfView(viewUrl, 'file-viewer-pdf', load.signal, () => {
        if (!modal || modal.classList.contains('hidden')) return false;
        escapeViewer();
        return true;
      }),
    );
  } else if (previewKind === 'text') {
    await renderTextFile(load);
  } else {
    await renderBinaryContent(load, info);
  }
}

async function renderTextFile(load: FileViewLoad): Promise<void> {
  try {
    const document = await readFileDocument(load.path, load.sessionId, false, load.signal);
    if (load.isActive()) renderTextDocument(load, document);
  } catch (error) {
    if (!load.isActive()) return;
    log.error(() => `Failed to load text document: ${String(error)}`);
    load.container.innerHTML = `<div class="file-viewer-error">${t('fileViewer.failedToLoadFile')}</div>`;
  }
}

function renderTextDocument(
  load: FileViewLoad,
  snapshot: FileDocument,
  previousOffsets: number[] = [],
): void {
  const previousMode = getDocumentViewMode(load.container);
  resetEditState();
  const container = load.container;
  let version = snapshot.version;
  isFullContentLoaded = !snapshot.isTruncated;
  currentContentIsPartial = snapshot.isTruncated;
  currentContent = snapshot.content;
  currentEdit = new FileEditState(load.path, snapshot.content, async (content, reviewedVersion) => {
    const saved = await writeFileDocument(
      load.path,
      load.sessionId,
      content,
      reviewedVersion ?? version,
    );
    version = saved.version;
    snapshot = {
      ...snapshot,
      size: saved.size,
      encoding: saved.encoding,
      lineEnding: saved.lineEnding,
    };
    const updatedNotice = createNotice();
    notice.replaceWith(updatedNotice);
    notice = updatedNotice;
    updateViewerHeaders(
      load.path,
      `${saved.encoding} | ${saved.lineEnding.toUpperCase()} | ${formatSize(saved.size)}`,
    );
  });
  const edit = currentEdit;
  currentEditOwner = container;
  trackFileEdits(container, edit, load.sessionId ?? '');
  updateViewerHeaders(
    load.path,
    `${snapshot.encoding} | ${snapshot.lineEnding.toUpperCase()} | ${formatSize(snapshot.size)}`,
  );
  const stack = createCodeStack();
  const view = createTextView({
    path: load.path,
    edit,
    readOnly: snapshot.readOnly,
    mode: previousMode,
    save: saveFile,
  });
  edit.subscribe(() => {
    if (currentEdit !== edit) return;
    currentContent = edit.content;
    isDirty = edit.dirty;
    updateSaveButtonVisibility(isDirty);
  });
  const status = createFileSaveStatus(edit, {
    readLatest: () => readFileDocument(load.path, load.sessionId, true, load.signal),
    active: () => load.isActive(),
    replace: (latest) => {
      version = latest.version;
      snapshot = latest;
      isFullContentLoaded = !latest.isTruncated;
      currentContentIsPartial = latest.isTruncated;
      view.setReadOnly(latest.readOnly);
      edit.replaceFromDisk(latest.content);
      const updatedNotice = createNotice();
      notice.replaceWith(updatedNotice);
      notice = updatedNotice;
      updateViewerHeaders(
        load.path,
        `${latest.encoding} | ${latest.lineEnding.toUpperCase()} | ${formatSize(latest.size)}`,
      );
    },
  });
  const createNotice = (): HTMLElement =>
    createDocumentNotice(snapshot, {
      read: (full, offset) =>
        readFileDocument(load.path, load.sessionId, full, load.signal, offset),
      active: () => load.isActive(),
      previousOffsets,
      display: (next, history) => {
        renderTextDocument(load, next, history);
      },
    });
  let notice = createNotice();
  stack.append(notice, status, view.root);
  container.replaceChildren(stack);
  updateSaveButtonVisibility(false);
}

async function renderBinaryContent(load: FileViewLoad, info: FilePathInfo): Promise<void> {
  const path = load.path;
  try {
    const viewUrl = buildViewUrl(path, load.sessionId);
    const fileSize = info.size ?? null;
    const initialPage = await loadBinaryPreviewPage({ viewUrl, fileSize, signal: load.signal });
    if (!load.isActive()) return;
    const container = load.container;
    const stack = createCodeStack();
    const viewer = createLineNumberedViewer(
      formatBinaryDump(initialPage.bytes, initialPage.startOffset),
      ['file-viewer-binary-shell'],
    );
    viewer.pre.classList.add('file-viewer-binary-text');
    updateViewerHeaders(path, formatBinaryHeaderMetadata(info));

    container.innerHTML = '';
    stack.appendChild(viewer.root);

    let nextOffset = initialPage.endOffsetExclusive;

    if (initialPage.hasMore) {
      const loadMoreBtn = document.createElement('button');
      loadMoreBtn.className = 'file-viewer-load-more';
      loadMoreBtn.textContent =
        fileSize != null
          ? `${t('fileViewer.loadMore')} (${formatSize(fileSize)})`
          : t('fileViewer.loadMore');
      loadMoreBtn.addEventListener('click', () => {
        loadMoreBtn.textContent = t('fileViewer.loading');
        loadMoreBtn.disabled = true;
        void loadBinaryPreviewPage({
          viewUrl,
          startOffset: nextOffset,
          fileSize,
          signal: load.signal,
        })
          .then((page) => {
            if (!load.isActive()) return;
            viewer.setText(formatBinaryDump(page.bytes, page.startOffset));
            nextOffset = page.endOffsetExclusive;
            if (page.hasMore) {
              loadMoreBtn.textContent =
                fileSize != null
                  ? `${t('fileViewer.loadMore')} (${formatSize(fileSize)})`
                  : t('fileViewer.loadMore');
              loadMoreBtn.disabled = false;
            } else {
              loadMoreBtn.remove();
            }
          })
          .catch((error: unknown) => {
            if (!load.isActive()) return;
            log.error(() => `Failed to load binary file page: ${String(error)}`);
            loadMoreBtn.textContent =
              fileSize != null
                ? `${t('fileViewer.loadMore')} (${formatSize(fileSize)})`
                : t('fileViewer.loadMore');
            loadMoreBtn.disabled = false;
          });
      });
      stack.appendChild(loadMoreBtn);
    }

    container.appendChild(stack);
    updateSaveButtonVisibility(false);
  } catch (e) {
    if (!load.isActive()) return;
    log.error(() => `Failed to load binary file: ${String(e)}`);
    load.container.innerHTML = `<div class="file-viewer-error">${t('fileViewer.failedToLoadFile')}</div>`;
  }
}

async function saveFile(): Promise<void> {
  const edit = currentEdit;
  if (!edit?.dirty || !isFullContentLoaded) return;
  try {
    await edit.save();
    if (currentEdit === edit) {
      isDirty = edit.dirty;
      updateSaveButtonVisibility(isDirty);
      log.info(() => `File saved: ${edit.path}`);
    }
  } catch (e) {
    log.error(() => `Save failed: ${String(e)}`);
  }
}

function updateSaveButtonVisibility(dirty: boolean): void {
  const modalSaveBtn = modal?.querySelector<HTMLElement>('#file-viewer-save');
  const busy = currentEdit?.saving ?? false;
  for (const button of document.querySelectorAll<HTMLButtonElement>(
    '#file-viewer-save, #dock-save',
  )) {
    button.disabled = busy;
    button.setAttribute('aria-busy', String(busy));
  }
  if (modalSaveBtn) {
    modalSaveBtn.style.display = dirty ? '' : 'none';
  }
  const dockSaveBtn = document.querySelector<HTMLElement>('#dock-save');
  if (dockSaveBtn) {
    dockSaveBtn.style.display = dirty ? '' : 'none';
  }
}

function updateFileActionButtons(info: FilePathInfo | null): void {
  const showFileActions = info?.exists === true && !info.isDirectory;

  for (const selector of [
    '#file-viewer-download',
    '#file-viewer-copy',
    '#dock-download',
    '#dock-copy',
  ]) {
    const button = document.querySelector<HTMLElement>(selector);
    if (!button) {
      continue;
    }

    button.style.display = showFileActions ? '' : 'none';
  }
}

async function copyCurrentFileToClipboard(path?: string | null): Promise<void> {
  const targetPath = path ?? currentPath;
  if (!targetPath) {
    return;
  }

  await copyFileToClipboard({
    path: targetPath,
    sessionId: currentSessionId ?? $activeSessionId.get(),
    mimeType: currentFileInfo?.mimeType,
    size: currentFileInfo?.size,
    isText: currentFileInfo?.isText,
    currentText: currentContent,
    currentTextIsPartial: currentContentIsPartial,
    currentTextIsDirty: isDirty,
  });
}
