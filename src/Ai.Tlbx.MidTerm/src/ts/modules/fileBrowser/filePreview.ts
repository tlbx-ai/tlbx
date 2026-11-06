import { createPdfView } from '../fileViewer/pdfView';
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
  formatViewerHeaderSubtitle,
  buildViewUrl,
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
import { FileEditState, releaseFileEdits, trackFileEdits } from '../fileViewer/editState';
import { FileViewLoad } from '../fileViewer/loadState';
import { readFileDocument, writeFileDocument, type FileDocument } from '../fileViewer/documentApi';
import { createFileSaveStatus } from '../fileViewer/saveStatus';
import { createDocumentNotice } from '../fileViewer/documentNotice';
import { createTextView, getDocumentViewMode } from '../fileViewer/textView';
import { pathDetailsButton } from '../fileViewer/pathDetails';

const log = createLogger('filePreview');
const previewLoads = new WeakMap<HTMLElement, FileViewLoad>();

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
  name.title = entry.fullPath;
  meta.appendChild(name);

  const subtitle = document.createElement('span');
  subtitle.className = 'preview-toolbar-subtitle';
  subtitle.textContent = formatViewerHeaderSubtitle(entry.fullPath, subtitleMetadata);
  subtitle.title = entry.fullPath;
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

  const menu = document.createElement('details');
  menu.className = 'file-preview-actions-menu';
  const summary = document.createElement('summary');
  summary.textContent = '⋯';
  summary.title = t('fileBrowser.actions');
  summary.setAttribute('aria-label', summary.title);
  const items = document.createElement('div');
  for (const [button, label] of [
    [copyBtn, t('trust.copy')],
    [downloadBtn, t('fileViewer.download')],
  ] as const) {
    button.classList.remove('btn-icon');
    button.append(document.createTextNode(label));
    items.append(button);
  }
  const details = pathDetailsButton(() => [entry.fullPath]);
  items.append(details);
  // Close and focus the menu before a button opens a dialog, so the dialog owns focus.
  items.addEventListener(
    'click',
    (event) => {
      if (event.target instanceof HTMLElement && event.target.closest('button')) {
        menu.open = false;
        summary.focus();
      }
    },
    true,
  );
  menu.append(summary, items);
  actions.append(menu);
}

export function renderPreview(
  container: HTMLElement,
  entry: FileTreeEntry,
  sessionId: string,
): void {
  previewLoads.get(container)?.abort();
  const load = new FileViewLoad(entry.fullPath, sessionId, container);
  previewLoads.set(container, load);
  releaseFileEdits(container);
  container.innerHTML = '';

  if (entry.isDirectory) {
    container.innerHTML = `<div class="preview-empty">${t('fileBrowser.selectFile')}</div>`;
    return;
  }

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
    body.replaceChildren(createPdfView(viewUrl, 'preview-pdf', load.signal));
    container.appendChild(shell);
    return;
  }

  if (previewKind === 'text') {
    container.innerHTML = `<div class="preview-loading">${t('fileBrowser.loading')}</div>`;
    void fetchAndRenderText(load, entry, sessionId);
    return;
  }

  container.innerHTML = `<div class="preview-loading">${t('fileBrowser.loading')}</div>`;
  void fetchAndRenderBinary(load, viewUrl, entry, sessionId);
}

async function fetchAndRenderText(
  load: FileViewLoad,
  entry: FileTreeEntry,
  sessionId: string,
): Promise<void> {
  const container = load.container;
  try {
    const document = await readFileDocument(entry.fullPath, sessionId, false, load.signal);
    if (!load.isActive()) return;
    renderTextContent(container, entry, sessionId, document, load);
  } catch (e) {
    if (!load.isActive()) return;
    log.error(() => `Failed to load preview: ${String(e)}`);
    container.innerHTML = `<div class="preview-error">${t('fileBrowser.failedToLoad')}</div>`;
  }
}

async function fetchAndRenderBinary(
  load: FileViewLoad,
  url: string,
  entry: FileTreeEntry,
  sessionId: string,
): Promise<void> {
  const container = load.container;
  try {
    const { shell, body, actions } = createPreviewShell(entry, formatBinaryPreviewMetadata(entry));
    appendPreviewFileActions(actions, entry, sessionId);
    const stack = document.createElement('div');
    stack.className = 'file-viewer-code-stack';
    const initialPage = await loadBinaryPreviewPage({
      viewUrl: url,
      fileSize: entry.size ?? null,
      signal: load.signal,
    });
    if (!load.isActive()) return;
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
          signal: load.signal,
        })
          .then((page) => {
            if (!load.isActive()) return;
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
            if (!load.isActive()) return;
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
    if (!load.isActive()) return;
    log.error(() => `Failed to load preview: ${String(e)}`);
    container.innerHTML = `<div class="preview-error">${t('fileBrowser.failedToLoad')}</div>`;
  }
}

function renderTextContent(
  container: HTMLElement,
  entry: FileTreeEntry,
  sessionId: string,
  snapshot: FileDocument,
  load: FileViewLoad,
  previousOffsets: number[] = [],
): void {
  const previousMode = getDocumentViewMode(container);
  let version = snapshot.version;
  const { shell, body, actions } = createPreviewShell(entry);
  const edit = new FileEditState(
    entry.fullPath,
    snapshot.content,
    async (content, reviewedVersion) => {
      const saved = await writeFileDocument(
        entry.fullPath,
        sessionId,
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
      body.querySelector('.file-document-notice')?.replaceWith(createNotice());
    },
  );
  trackFileEdits(container, edit, sessionId);
  appendPreviewFileActions(actions, entry, sessionId, () => edit.content);
  const saveCurrentText = async (): Promise<void> => {
    if (!edit.dirty || edit.saving) return;
    try {
      await edit.save();
    } catch (error) {
      log.error(() => `Save failed for ${entry.fullPath}: ${String(error)}`);
    }
  };
  const view = createTextView({
    path: entry.fullPath,
    edit,
    readOnly: snapshot.readOnly,
    mode: previousMode,
    save: saveCurrentText,
  });
  const modeControls = view.root.querySelector('.file-document-modes');
  if (modeControls) actions.prepend(modeControls);
  const saveBtn = document.createElement('button');
  saveBtn.type = 'button';
  saveBtn.className = 'preview-save-btn';
  const updateSave = (): void => {
    saveBtn.disabled = !edit.dirty || edit.saving || snapshot.readOnly;
    saveBtn.hidden = !edit.dirty && !edit.saving;
    saveBtn.textContent = t(edit.saving ? 'modal.saving' : 'fileViewer.save');
  };
  edit.subscribe(updateSave);
  updateSave();
  saveBtn.addEventListener('click', () => {
    void saveCurrentText();
  });
  actions.append(saveBtn);
  const status = createFileSaveStatus(edit, {
    readLatest: () => readFileDocument(entry.fullPath, sessionId, true, load.signal),
    active: () => load.isActive(),
    replace: (latest) => {
      version = latest.version;
      snapshot = latest;
      edit.replaceFromDisk(latest.content);
      view.setReadOnly(latest.readOnly);
      body.querySelector('.file-document-notice')?.replaceWith(createNotice());
    },
  });
  const createNotice = (): HTMLElement =>
    createDocumentNotice(snapshot, {
      read: (full, offset) =>
        readFileDocument(entry.fullPath, sessionId, full, load.signal, offset),
      active: () => load.isActive(),
      previousOffsets,
      display: (next, history) => {
        renderTextContent(container, entry, sessionId, next, load, history);
      },
    });
  body.append(createNotice(), status, view.root);
  container.replaceChildren(shell);
}

export function clearPreview(container: HTMLElement): void {
  previewLoads.get(container)?.abort();
  previewLoads.delete(container);
  releaseFileEdits(container);
  container.innerHTML = `<div class="preview-empty">${t('fileBrowser.selectFile')}</div>`;
}
