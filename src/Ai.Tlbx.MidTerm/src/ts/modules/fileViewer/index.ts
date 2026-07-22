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
import { escapeHtml } from '../../utils';
import {
  getFileName,
  joinPath,
  getFileIcon,
  formatSize,
  formatViewerHeaderSubtitle,
  formatBinaryDump,
  createLineNumberedEditor,
  createLineNumberedViewer,
} from './rendering';
import {
  copyFileToClipboard,
  downloadFile as triggerFileDownload,
  loadBinaryPreviewPage,
  resolveFilePreviewKind,
} from './shared';
import { createImageView } from './imageView';

const log = createLogger('fileViewer');

const SIZE_LIMIT = 500 * 1024;

let modal: HTMLElement | null = null;
let currentPath: string | null = null;
let currentSessionId: string | null = null;
let navigationHistory: string[] = [];
let lastVideoVolume = 0.15;

let isDirty = false;
let isFullContentLoaded = true;
let currentContent = '';
let currentContentIsPartial = false;
let currentFileInfo: FilePathInfo | null = null;
let releaseBackButtonLayer: (() => void) | null = null;

export function initFileViewer(): void {
  modal = document.getElementById('file-viewer-modal');
  if (!modal) {
    log.warn(() => 'File viewer modal element not found');
    return;
  }

  const backdrop = modal.querySelector('.modal-backdrop');
  const closeBtn = modal.querySelector('.modal-close');
  const maximizeBtn = modal.querySelector('#file-viewer-maximize');
  const dockBtn = modal.querySelector('#file-viewer-dock-btn');

  backdrop?.addEventListener('click', closeViewer);
  closeBtn?.addEventListener('click', closeViewer);
  maximizeBtn?.addEventListener('click', toggleFullscreen);
  dockBtn?.addEventListener('click', dockViewer);

  modal.querySelector('#file-viewer-save')?.addEventListener('click', () => void saveFile());

  // Dock panel buttons
  const dockPanel = document.getElementById('file-viewer-dock');
  if (dockPanel) {
    dockPanel.querySelector('#dock-close')?.addEventListener('click', closeFileViewerDock);
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
    if (e.key === 'Escape' && modal && !modal.classList.contains('hidden')) {
      if (document.fullscreenElement) {
        void document.exitFullscreen();
      } else {
        closeViewer();
      }
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

function resetEditState(): void {
  isDirty = false;
  isFullContentLoaded = true;
  currentContent = '';
  currentContentIsPartial = false;
  currentFileInfo = null;
  updateSaveButtonVisibility(false);
  updateFileActionButtons(null);
}

function toggleFullscreen(): void {
  const content = modal?.querySelector('.file-viewer-modal-content') as HTMLElement | undefined;
  if (document.fullscreenElement) {
    void document.exitFullscreen();
  } else if (content) {
    void content.requestFullscreen();
  }
}

function dockViewer(): void {
  if (!currentPath) return;

  // Mutual exclusion: close sidebar docks if open
  if ($commandsPanelDocked.get()) closeCommandsDock();
  if ($gitPanelDocked.get()) closeGitDock();

  const path = currentPath;
  const sessionId = currentSessionId;

  // Close the modal
  closeViewer();

  // Update stores and show dock
  $dockedFilePath.set(path);
  $fileViewerDocked.set(true);

  // Restore the session ID for the dock
  currentPath = path;
  currentSessionId = sessionId;

  const dockPanel = document.getElementById('file-viewer-dock');
  const terminalPage = document.getElementById('app');

  if (dockPanel) {
    dockPanel.classList.remove('hidden');
  }
  terminalPage?.classList.add('file-viewer-docked');

  // Render the file in dock
  void renderInDock(path);

  adjustInnerDockPositions();
  updateAllDockMargins();
  handleDockLayoutChange();
}

export function closeFileViewerDock(): void {
  const dockPanel = document.getElementById('file-viewer-dock');
  const terminalPage = document.getElementById('app');

  if (dockPanel) {
    dockPanel.classList.add('hidden');
  }
  terminalPage?.classList.remove('file-viewer-docked');

  $fileViewerDocked.set(false);
  $dockedFilePath.set(null);
  currentPath = null;
  currentSessionId = null;
  resetEditState();

  adjustInnerDockPositions();
  updateAllDockMargins();
  handleDockLayoutChange();
}

export function openFileViewerDock(path: string): void {
  $dockedFilePath.set(path);
  $fileViewerDocked.set(true);
  currentPath = path;
  currentSessionId = $activeSessionId.get() ?? null;

  const dockPanel = document.getElementById('file-viewer-dock');
  const terminalPage = document.getElementById('app');
  if (dockPanel) dockPanel.classList.remove('hidden');
  terminalPage?.classList.add('file-viewer-docked');

  void renderInDock(path);

  adjustInnerDockPositions();
  updateAllDockMargins();
  handleDockLayoutChange();
}

function undockViewer(): void {
  const path = $dockedFilePath.get();
  if (!path) return;

  // Close dock first
  closeFileViewerDock();

  // Open in modal
  void openFile(path);
}

async function refreshDock(): Promise<void> {
  const path = $dockedFilePath.get();
  if (path) {
    await renderInDock(path);
  }
}

async function renderInDock(path: string): Promise<void> {
  const dockPanel = document.getElementById('file-viewer-dock');
  if (!dockPanel) return;

  const titleEl = dockPanel.querySelector('.file-viewer-dock-title');
  const pathEl = dockPanel.querySelector('.file-viewer-dock-path');
  const bodyEl = dockPanel.querySelector('.file-viewer-dock-body');

  if (titleEl || pathEl) {
    updateDockHeader(path);
  }
  if (bodyEl)
    bodyEl.innerHTML = `<div class="file-viewer-loading">${t('fileViewer.loading')}</div>`;

  resetEditState();

  const info = await checkFilePath(path);

  if (!info || !info.exists) {
    if (bodyEl)
      bodyEl.innerHTML = `<div class="file-viewer-error">${t('fileViewer.fileNotFound')}</div>`;
    updateFileActionButtons(null);
    return;
  }

  currentFileInfo = info;
  updateFileActionButtons(info);

  if (info.isDirectory) {
    if (bodyEl)
      bodyEl.innerHTML = `<div class="file-viewer-error">${t('fileViewer.dirNotSupported')}</div>`;
  } else if (bodyEl) {
    await renderFile(path, info, bodyEl);
  }
}

function closeViewer(): void {
  releaseBackButtonLayer?.();
  releaseBackButtonLayer = null;

  if (modal) {
    modal.classList.add('hidden');
  }
  currentPath = null;
  currentSessionId = null;
  navigationHistory = [];
  resetEditState();
}

export async function openFile(path: string, info?: FilePathInfo | null): Promise<void> {
  if (!modal) {
    log.error(() => 'File viewer modal not initialized');
    return;
  }

  if (!releaseBackButtonLayer) {
    releaseBackButtonLayer = registerBackButtonLayer(closeViewer);
  }

  currentPath = path;
  currentSessionId = $activeSessionId.get() ?? null;
  modal.classList.remove('hidden');

  const titleEl = modal.querySelector('.file-viewer-title');
  const pathEl = modal.querySelector('.file-viewer-path');
  const bodyEl = modal.querySelector('.file-viewer-body');
  const downloadBtn = modal.querySelector<HTMLElement>('#file-viewer-download');
  const copyBtn = modal.querySelector<HTMLElement>('#file-viewer-copy');

  if (titleEl || pathEl) {
    updateModalHeader(path);
  }
  if (bodyEl)
    bodyEl.innerHTML = `<div class="file-viewer-loading">${t('fileViewer.loading')}</div>`;

  resetEditState();

  if (downloadBtn) {
    downloadBtn.onclick = () => {
      triggerFileDownload(path, currentSessionId ?? $activeSessionId.get());
    };
  }
  if (copyBtn) {
    copyBtn.onclick = () => {
      void copyCurrentFileToClipboard(path);
    };
  }

  if (!info) {
    info = await checkFilePath(path);
  }

  if (!info || !info.exists) {
    if (bodyEl)
      bodyEl.innerHTML = `<div class="file-viewer-error">${t('fileViewer.fileNotFound')}</div>`;
    updateFileActionButtons(null);
    return;
  }

  currentFileInfo = info;
  updateFileActionButtons(info);

  if (!bodyEl) return;

  if (info.isDirectory) {
    await renderDirectory(path, bodyEl);
  } else {
    await renderFile(path, info, bodyEl);
  }
}

async function checkFilePath(path: string): Promise<FilePathInfo | null> {
  try {
    const sessionId = currentSessionId ?? $activeSessionId.get();
    const url = sessionId
      ? `/api/files/check?sessionId=${encodeURIComponent(sessionId)}`
      : '/api/files/check';
    const resp = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ paths: [path] }),
    });
    if (!resp.ok) return null;
    const data = (await resp.json()) as { results: Record<string, FilePathInfo> };
    return data.results[path] || null;
  } catch (e) {
    log.error(() => `Failed to check file path: ${String(e)}`);
    return null;
  }
}

async function renderDirectory(path: string, container: Element): Promise<void> {
  try {
    const sessionId = currentSessionId ?? $activeSessionId.get();
    let url = `/api/files/list?path=${encodeURIComponent(path)}`;
    if (sessionId) {
      url += `&sessionId=${encodeURIComponent(sessionId)}`;
    }
    const resp = await fetch(url);
    if (!resp.ok) {
      const body = await resp.text().catch(() => '');
      log.error(() => `List directory failed: ${resp.status} ${resp.statusText} ${body}`);
      container.innerHTML = `<div class="file-viewer-error">${t('fileViewer.failedToList')} (${resp.status})</div>`;
      return;
    }

    const data = (await resp.json()) as DirectoryListResponse;
    renderDirectoryListing(data.entries, path, container);
  } catch (e) {
    log.error(() => `Failed to list directory: ${String(e)}`);
    container.innerHTML = `<div class="file-viewer-error">${t('fileViewer.failedToList')}</div>`;
  }
}

function renderDirectoryListing(
  entries: DirectoryEntry[],
  basePath: string,
  container: Element,
): void {
  const html = `
    <div class="file-list">
      ${
        navigationHistory.length > 0
          ? `
        <div class="file-list-item file-list-parent" data-action="back">
          <span class="file-icon">📁</span>
          <span class="file-name">..</span>
        </div>
      `
          : ''
      }
      ${entries
        .map((entry) => {
          const name = entry.name;
          const isDir = entry.isDirectory;
          const size = entry.size ?? null;
          return `
        <div class="file-list-item ${isDir ? 'file-list-dir' : 'file-list-file'}"
             data-path="${escapeHtml(joinPath(basePath, name))}"
             data-is-dir="${isDir}">
          <span class="file-icon">${isDir ? '📁' : getFileIcon(name)}</span>
          <span class="file-name">${escapeHtml(name)}</span>
          ${!isDir && size != null ? `<span class="file-size">${formatSize(size)}</span>` : ''}
        </div>
      `;
        })
        .join('')}
      ${entries.length === 0 ? `<div class="file-list-empty">${t('fileViewer.emptyDirectory')}</div>` : ''}
    </div>
  `;

  container.innerHTML = html;

  container.querySelectorAll('.file-list-item').forEach((item) => {
    item.addEventListener('click', () => {
      const action = item.getAttribute('data-action');
      if (action === 'back') {
        const prevPath = navigationHistory.pop();
        if (prevPath) void openFile(prevPath);
        return;
      }

      const itemPath = item.getAttribute('data-path');
      const isDir = item.getAttribute('data-is-dir') === 'true';
      if (itemPath) {
        if (isDir && currentPath) {
          navigationHistory.push(currentPath);
        }
        void openFile(itemPath);
      }
    });
  });
}

function buildViewUrl(path: string): string {
  const sessionId = currentSessionId ?? $activeSessionId.get();
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
  if (titleEl) titleEl.textContent = getFileName(path);
  if (pathEl) pathEl.textContent = formatViewerHeaderSubtitle(path, metadata);
}

function updateDockHeader(path: string, metadata?: string | null): void {
  const dockPanel = document.getElementById('file-viewer-dock');
  if (!dockPanel) {
    return;
  }

  const titleEl = dockPanel.querySelector('.file-viewer-dock-title');
  const pathEl = dockPanel.querySelector('.file-viewer-dock-path');
  if (titleEl) titleEl.textContent = getFileName(path);
  if (pathEl) pathEl.textContent = formatViewerHeaderSubtitle(path, metadata);
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

async function renderFile(path: string, info: FilePathInfo, container: Element): Promise<void> {
  const mime = info.mimeType || 'application/octet-stream';
  const viewUrl = buildViewUrl(path);
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
    container.innerHTML = `<iframe class="file-viewer-pdf" src="${viewUrl}"></iframe>`;
  } else if (previewKind === 'text') {
    await renderTextFile(path, info, container);
  } else {
    await renderBinaryContent(path, info, container);
  }
}

async function renderTextFile(path: string, info: FilePathInfo, container: Element): Promise<void> {
  try {
    const viewUrl = buildViewUrl(path);
    const fileSize = info.size ?? 0;

    let resp: Response;
    if (fileSize > SIZE_LIMIT) {
      resp = await fetch(viewUrl, {
        headers: { Range: `bytes=0-${SIZE_LIMIT - 1}` },
      });
      isFullContentLoaded = false;
      currentContentIsPartial = true;
    } else {
      resp = await fetch(viewUrl);
      isFullContentLoaded = true;
      currentContentIsPartial = false;
    }

    if (!resp.ok && resp.status !== 206) {
      container.innerHTML = `<div class="file-viewer-error">${t('fileViewer.failedToLoadFile')}</div>`;
      return;
    }

    const text = await resp.text();
    currentContent = text;
    isDirty = false;

    const stack = createCodeStack();
    const editor = createLineNumberedEditor(text);
    const textarea = editor.textarea;
    textarea.addEventListener('input', () => {
      isDirty = true;
      currentContent = textarea.value;
      updateSaveButtonVisibility(true);
    });

    container.innerHTML = '';
    stack.appendChild(editor.root);
    container.appendChild(stack);

    if (!isFullContentLoaded) {
      const loadMoreBtn = document.createElement('button');
      loadMoreBtn.className = 'file-viewer-load-more';
      loadMoreBtn.textContent = `${t('fileViewer.loadMore')} (${formatSize(fileSize)})`;
      loadMoreBtn.addEventListener('click', () => {
        loadMoreBtn.textContent = t('fileViewer.loading');
        loadMoreBtn.disabled = true;
        void fetch(viewUrl).then(async (fullResp) => {
          if (fullResp.ok) {
            const fullText = await fullResp.text();
            editor.setText(fullText);
            currentContent = fullText;
            isDirty = false;
            isFullContentLoaded = true;
            currentContentIsPartial = false;
            updateSaveButtonVisibility(false);
            loadMoreBtn.remove();
          } else {
            loadMoreBtn.textContent = `${t('fileViewer.loadMore')} (${formatSize(fileSize)})`;
            loadMoreBtn.disabled = false;
          }
        });
      });
      stack.appendChild(loadMoreBtn);
    }

    updateSaveButtonVisibility(false);
  } catch (e) {
    log.error(() => `Failed to load text file: ${String(e)}`);
    container.innerHTML = `<div class="file-viewer-error">${t('fileViewer.failedToLoadFile')}</div>`;
  }
}

async function renderBinaryContent(
  path: string,
  info: FilePathInfo,
  container: Element,
): Promise<void> {
  try {
    const viewUrl = buildViewUrl(path);
    const fileSize = info.size ?? null;
    const initialPage = await loadBinaryPreviewPage({ viewUrl, fileSize });
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
        void loadBinaryPreviewPage({ viewUrl, startOffset: nextOffset, fileSize })
          .then((page) => {
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
    log.error(() => `Failed to load binary file: ${String(e)}`);
    container.innerHTML = `<div class="file-viewer-error">${t('fileViewer.failedToLoadFile')}</div>`;
  }
}

async function saveFile(): Promise<void> {
  if (!currentPath || !isDirty || !isFullContentLoaded) return;

  const sessionId = currentSessionId ?? $activeSessionId.get();
  try {
    const saveUrl = sessionId
      ? `/api/files/save?sessionId=${encodeURIComponent(sessionId)}`
      : '/api/files/save';
    const resp = await fetch(saveUrl, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ path: currentPath, content: currentContent }),
    });

    if (resp.ok) {
      isDirty = false;
      updateSaveButtonVisibility(false);
      log.info(() => `File saved: ${currentPath}`);
    } else {
      log.error(() => `Save failed: ${resp.status}`);
    }
  } catch (e) {
    log.error(() => `Save failed: ${String(e)}`);
  }
}

function updateSaveButtonVisibility(dirty: boolean): void {
  const modalSaveBtn = modal?.querySelector<HTMLElement>('#file-viewer-save');
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
