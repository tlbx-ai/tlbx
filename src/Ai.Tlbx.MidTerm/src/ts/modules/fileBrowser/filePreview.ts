/**
 * File Browser Preview Panel
 *
 * Shows file content preview in the right panel.
 * Reuses rendering functions from fileViewer module.
 */

import { createLogger } from '../logging';
import { t } from '../i18n';
import type { FileTreeEntry } from './treeApi';
import { escapeHtml } from '../../utils';
import {
  formatSize,
  getExtension,
  formatViewerHeaderSubtitle,
  highlightCode,
  buildViewUrl,
  createLineNumberedEditor,
  createLineNumberedViewer,
  formatBinaryDump,
} from '../fileViewer/rendering';
import {
  copyFileToClipboard,
  downloadFile,
  loadBinaryPreviewPage,
  resolveFilePreviewKind,
} from '../fileViewer/shared';
import { createImageView } from '../fileViewer/imageView';

const log = createLogger('filePreview');

function buildSaveUrl(sessionId: string): string {
  let url = '/api/files/save';
  if (sessionId) {
    url += `?sessionId=${encodeURIComponent(sessionId)}`;
  }
  return url;
}

function formatBinaryPreviewMetadata(entry: FileTreeEntry): string {
  const parts = [entry.mimeType || t('fileViewer.binaryFile')];
  if (entry.size != null) {
    parts.push(formatSize(entry.size));
  }
  return parts.join(' | ');
}

/**
 * Keep the preview toolbar as the only header. New preview types should reuse
 * this shell and put extra metadata into the subtitle instead of adding a
 * renderer-specific bar above the body.
 */
function createPreviewShell(
  entry: FileTreeEntry,
  subtitleMetadata?: string | null,
): {
  shell: HTMLDivElement;
  body: HTMLDivElement;
  actions: HTMLDivElement;
} {
  const shell = document.createElement('div');
  shell.className = 'preview-text-shell';

  const toolbar = document.createElement('div');
  toolbar.className = 'preview-toolbar';

  const meta = document.createElement('div');
  meta.className = 'preview-toolbar-meta';

  const name = document.createElement('span');
  name.className = 'preview-toolbar-name';
  name.textContent = entry.name;
  meta.appendChild(name);

  const subtitle = document.createElement('span');
  subtitle.className = 'preview-toolbar-subtitle';
  subtitle.textContent = formatViewerHeaderSubtitle(entry.fullPath, subtitleMetadata);
  meta.appendChild(subtitle);

  const actions = document.createElement('div');
  actions.className = 'preview-toolbar-actions';

  const body = document.createElement('div');
  body.className = 'preview-text-body';

  toolbar.appendChild(meta);
  toolbar.appendChild(actions);
  shell.appendChild(toolbar);
  shell.appendChild(body);

  return { shell, body, actions };
}

function createPreviewIconButton(
  className: string,
  title: string,
  svgMarkup: string,
): HTMLButtonElement {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = `btn-icon ${className}`;
  button.title = title;
  button.setAttribute('aria-label', title);
  button.innerHTML = svgMarkup;
  return button;
}

function appendPreviewFileActions(
  actions: HTMLDivElement,
  entry: FileTreeEntry,
  sessionId: string,
  getCurrentText?: (() => string) | null,
): void {
  const downloadBtn = createPreviewIconButton(
    'preview-toolbar-action-btn',
    t('fileViewer.download'),
    `<svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor" aria-hidden="true">
      <path d="M8 12l-4-4h2.5V3h3v5H12L8 12z" />
      <path d="M2 13h12v1H2v-1z" />
    </svg>`,
  );
  downloadBtn.addEventListener('click', () => {
    downloadFile(entry.fullPath, sessionId);
  });

  const copyBtn = createPreviewIconButton(
    'preview-toolbar-action-btn',
    t('trust.copy'),
    `<svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor" aria-hidden="true">
      <path d="M5 2h7a2 2 0 0 1 2 2v7h-1V4a1 1 0 0 0-1-1H5V2z" />
      <path d="M3 5h7a2 2 0 0 1 2 2v6a2 2 0 0 1-2 2H3a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2zm0 1a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h7a1 1 0 0 0 1-1V7a1 1 0 0 0-1-1H3z" />
    </svg>`,
  );
  copyBtn.addEventListener('click', () => {
    void copyFileToClipboard({
      path: entry.fullPath,
      sessionId,
      mimeType: entry.mimeType,
      size: entry.size ?? null,
      isText: entry.isText ?? null,
      currentText: getCurrentText?.() ?? null,
    });
  });

  actions.appendChild(copyBtn);
  actions.appendChild(downloadBtn);
}

export function renderPreview(
  container: HTMLElement,
  entry: FileTreeEntry,
  sessionId: string,
): void {
  container.innerHTML = '';

  if (entry.isDirectory) {
    container.innerHTML = `<div class="preview-empty">${t('fileBrowser.selectFile')}</div>`;
    return;
  }

  const ext = getExtension(entry.name).toLowerCase();
  const mime = entry.mimeType ?? '';
  const viewUrl = buildViewUrl(entry.fullPath, sessionId);
  const previewKind = resolveFilePreviewKind(entry.fullPath, mime, entry.isText);

  if (previewKind === 'image') {
    const { shell, body, actions } = createPreviewShell(entry);
    appendPreviewFileActions(actions, entry, sessionId);
    body.appendChild(createImageView(viewUrl, entry.name));
    container.appendChild(shell);
    return;
  }

  if (previewKind === 'video') {
    const { shell, body, actions } = createPreviewShell(entry);
    appendPreviewFileActions(actions, entry, sessionId);
    body.innerHTML = `<video class="preview-video" controls src="${escapeHtml(viewUrl)}"></video>`;
    container.appendChild(shell);
    return;
  }

  if (previewKind === 'audio') {
    const { shell, body, actions } = createPreviewShell(entry);
    appendPreviewFileActions(actions, entry, sessionId);
    body.innerHTML = `<audio class="preview-audio" controls src="${escapeHtml(viewUrl)}"></audio>`;
    container.appendChild(shell);
    return;
  }

  if (previewKind === 'pdf') {
    const { shell, body, actions } = createPreviewShell(entry);
    appendPreviewFileActions(actions, entry, sessionId);
    body.innerHTML = `<iframe class="preview-pdf" src="${escapeHtml(viewUrl)}"></iframe>`;
    container.appendChild(shell);
    return;
  }

  if (previewKind === 'text') {
    container.innerHTML = `<div class="preview-loading">${t('fileBrowser.loading')}</div>`;
    void fetchAndRenderText(container, viewUrl, entry, sessionId, ext);
    return;
  }

  container.innerHTML = `<div class="preview-loading">${t('fileBrowser.loading')}</div>`;
  void fetchAndRenderBinary(container, viewUrl, entry, sessionId);
}

async function fetchAndRenderText(
  container: HTMLElement,
  url: string,
  entry: FileTreeEntry,
  sessionId: string,
  ext: string,
): Promise<void> {
  try {
    const res = await fetch(url);
    if (!res.ok) {
      container.innerHTML = `<div class="preview-error">${t('fileBrowser.failedToLoad')} (${res.status})</div>`;
      return;
    }

    const text = await res.text();
    const isMarkdown = ext === '.md' || ext === '.markdown';
    renderTextContent(container, entry, sessionId, text, ext, isMarkdown);
  } catch (e) {
    log.error(() => `Failed to load preview: ${String(e)}`);
    container.innerHTML = `<div class="preview-error">${t('fileBrowser.failedToLoad')}</div>`;
  }
}

async function fetchAndRenderBinary(
  container: HTMLElement,
  url: string,
  entry: FileTreeEntry,
  sessionId: string,
): Promise<void> {
  try {
    const { shell, body, actions } = createPreviewShell(entry, formatBinaryPreviewMetadata(entry));
    appendPreviewFileActions(actions, entry, sessionId);
    const stack = document.createElement('div');
    stack.className = 'file-viewer-code-stack';
    const initialPage = await loadBinaryPreviewPage({ viewUrl: url, fileSize: entry.size ?? null });
    const viewer = createLineNumberedViewer(
      formatBinaryDump(initialPage.bytes, initialPage.startOffset),
      ['file-viewer-binary-shell'],
    );
    viewer.pre.classList.add('file-viewer-binary-text');

    let nextOffset = initialPage.endOffsetExclusive;
    stack.appendChild(viewer.root);

    if (initialPage.hasMore) {
      const loadMoreBtn = document.createElement('button');
      loadMoreBtn.className = 'file-viewer-load-more';
      loadMoreBtn.textContent =
        entry.size != null
          ? `${t('fileViewer.loadMore')} (${formatSize(entry.size)})`
          : t('fileViewer.loadMore');
      loadMoreBtn.addEventListener('click', () => {
        loadMoreBtn.textContent = t('fileViewer.loading');
        loadMoreBtn.disabled = true;
        void loadBinaryPreviewPage({
          viewUrl: url,
          startOffset: nextOffset,
          fileSize: entry.size ?? null,
        })
          .then((page) => {
            viewer.setText(formatBinaryDump(page.bytes, page.startOffset));
            nextOffset = page.endOffsetExclusive;
            if (page.hasMore) {
              loadMoreBtn.textContent =
                entry.size != null
                  ? `${t('fileViewer.loadMore')} (${formatSize(entry.size)})`
                  : t('fileViewer.loadMore');
              loadMoreBtn.disabled = false;
            } else {
              loadMoreBtn.remove();
            }
          })
          .catch((error: unknown) => {
            log.error(() => `Failed to load binary preview page: ${String(error)}`);
            loadMoreBtn.textContent =
              entry.size != null
                ? `${t('fileViewer.loadMore')} (${formatSize(entry.size)})`
                : t('fileViewer.loadMore');
            loadMoreBtn.disabled = false;
          });
      });
      stack.appendChild(loadMoreBtn);
    }

    body.appendChild(stack);

    container.innerHTML = '';
    container.appendChild(shell);
  } catch (e) {
    log.error(() => `Failed to load preview: ${String(e)}`);
    container.innerHTML = `<div class="preview-error">${t('fileBrowser.failedToLoad')}</div>`;
  }
}

function renderTextContent(
  container: HTMLElement,
  entry: FileTreeEntry,
  sessionId: string,
  originalText: string,
  ext: string,
  startInEditor: boolean,
): void {
  let currentText = originalText;
  let isEditing = startInEditor;
  let isDirty = false;

  const { shell, body, actions } = createPreviewShell(entry);
  appendPreviewFileActions(actions, entry, sessionId, () => currentText);

  const editBtn = document.createElement('button');
  editBtn.type = 'button';
  editBtn.className = 'preview-editor-btn';
  editBtn.textContent = t('commands.edit');
  editBtn.style.display = isEditing ? 'none' : '';

  const saveBtn = document.createElement('button');
  saveBtn.type = 'button';
  saveBtn.className = 'preview-save-btn';
  saveBtn.textContent = t('fileViewer.save');
  saveBtn.disabled = true;
  saveBtn.style.display = isEditing ? '' : 'none';

  const updateDirtyState = (dirty: boolean): void => {
    isDirty = dirty;
    saveBtn.disabled = !dirty;
  };

  const saveCurrentText = async (): Promise<void> => {
    if (!isDirty) return;

    const previousLabel = saveBtn.textContent;
    saveBtn.disabled = true;
    saveBtn.textContent = t('modal.saving');

    try {
      const resp = await fetch(buildSaveUrl(sessionId), {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          path: entry.fullPath,
          content: currentText,
        }),
      });

      if (!resp.ok) {
        log.error(() => `Save failed for ${entry.fullPath}: ${resp.status}`);
        saveBtn.textContent = previousLabel;
        saveBtn.disabled = false;
        return;
      }

      originalText = currentText;
      updateDirtyState(false);
      saveBtn.textContent = previousLabel;
    } catch (e) {
      log.error(() => `Save failed for ${entry.fullPath}: ${String(e)}`);
      saveBtn.textContent = previousLabel;
      saveBtn.disabled = false;
    }
  };

  const renderReadOnly = (): void => {
    body.innerHTML = '';
    const viewer = createLineNumberedViewer(currentText, [], highlightCode(currentText, ext));
    body.appendChild(viewer.root);
  };

  const renderEditor = (): void => {
    body.innerHTML = '';
    const editor = createLineNumberedEditor(currentText, ['preview-textarea']);
    const textarea = editor.textarea;
    textarea.addEventListener('input', () => {
      currentText = textarea.value;
      updateDirtyState(currentText !== originalText);
    });
    textarea.addEventListener('keydown', (e) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
        e.preventDefault();
        void saveCurrentText();
      }
    });
    body.appendChild(editor.root);
  };

  const renderMode = (): void => {
    editBtn.style.display = isEditing ? 'none' : '';
    saveBtn.style.display = isEditing ? '' : 'none';

    if (isEditing) {
      renderEditor();
    } else {
      renderReadOnly();
    }
  };

  editBtn.addEventListener('click', () => {
    isEditing = true;
    renderMode();
  });

  saveBtn.addEventListener('click', () => {
    void saveCurrentText();
  });

  actions.appendChild(editBtn);
  actions.appendChild(saveBtn);

  container.innerHTML = '';
  container.appendChild(shell);

  renderMode();
}

export function clearPreview(container: HTMLElement): void {
  container.innerHTML = `<div class="preview-empty">${t('fileBrowser.selectFile')}</div>`;
}
