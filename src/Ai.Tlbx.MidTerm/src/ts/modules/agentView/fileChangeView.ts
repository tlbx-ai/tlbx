import { t } from '../i18n';
import { registerBackButtonLayer } from '../navigation/backButtonGuard';
import { openFileInEditor } from '../fileBrowser';
import { createHistoryFileReview } from './historyFileReview';
import {
  getHistoryChangedFiles,
  buildRenderedDiffLines,
  type HistoryChangedFile,
} from './historyContent';
import { isHistoryError } from './historyVisibility';
import type { AppServerControlHistoryEntry } from './types';

/** Read-only review: recorded provider patches first, current Git changes otherwise. */
export function openHistoryFileDiff(
  sessionId: string,
  file: HistoryChangedFile,
  renderRecorded: (diff: string) => HTMLElement,
  trigger: HTMLElement,
): void {
  const dialog = document.createElement('dialog');
  dialog.className = 'git-workspace-dialog agent-file-diff-dialog';
  dialog.setAttribute('aria-label', `${t('git.showDiff')}: ${file.path}`);
  const header = document.createElement('header');
  const title = document.createElement('h3');
  title.textContent = file.path;
  const close = document.createElement('button');
  close.type = 'button';
  close.className = 'btn-secondary';
  close.textContent = t('dialog.close');
  close.addEventListener('click', () => {
    closeDialog();
  });
  const actions = document.createElement('div');
  actions.className = 'agent-file-diff-actions';
  if (file.path !== t('git.showDiff')) {
    const edit = document.createElement('button');
    edit.type = 'button';
    edit.className = 'btn-secondary';
    edit.textContent = t('appServerControl.files.openEditor');
    edit.addEventListener('click', () => {
      closeDialog();
      void openFileInEditor(sessionId, file.path);
    });
    actions.append(edit);
  }
  actions.append(close);
  header.append(title, actions);
  const scope = document.createElement('p');
  scope.className = 'agent-file-diff-scope';
  scope.textContent = t(
    file.diff ? 'appServerControl.files.recordedDiff' : 'appServerControl.files.currentDiff',
  );
  const body = document.createElement('div');
  body.className = 'agent-file-diff-content';
  dialog.append(header, scope, body);
  document.body.append(dialog);
  let disposed = false;
  const load = new AbortController();
  const releaseBack = registerBackButtonLayer(closeDialog);
  function cleanup(): void {
    if (disposed) return;
    disposed = true;
    load.abort();
    releaseBack();
    dialog.remove();
    if (trigger.isConnected) trigger.focus({ preventScroll: true });
  }
  function closeDialog(): void {
    if (dialog.open) dialog.close();
    cleanup();
  }
  dialog.addEventListener('close', cleanup, { once: true });
  dialog.addEventListener('cancel', (event) => {
    event.preventDefault();
    closeDialog();
  });
  dialog.addEventListener('keydown', (event) => {
    event.stopPropagation();
  });
  dialog.showModal();
  close.focus();
  if (file.diff) {
    body.append(renderRecorded(file.diff));
    return;
  }
  dialog.classList.add('agent-file-review-dialog');
  body.append(createHistoryFileReview(sessionId, file.path, scope, load.signal));
}

export function createFileChangeHistoryBody(
  entry: AppServerControlHistoryEntry,
  sessionId: string,
  cwd: string | null | undefined,
  historyEntries: () => readonly AppServerControlHistoryEntry[],
  renderRecorded: (diff: string) => HTMLElement,
): HTMLElement {
  const body = document.createElement('div');
  body.className = 'agent-history-file-change-list';
  const label = document.createElement('div');
  label.className = 'agent-history-file-change-label';
  label.textContent = t('appServerControl.files.changed');
  body.append(label);
  const files = getHistoryChangedFiles(entry, cwd);
  if (!files.length && entry.kind === 'diff' && entry.body.trim())
    files.push({ path: t('git.showDiff'), diff: entry.body });
  for (const file of files) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'agent-history-file-change-link';
    button.title = file.path;
    button.setAttribute('aria-label', file.path);
    appendFilePath(button, file.path);
    button.addEventListener('click', () => {
      // Resolve a retained turn patch on demand; do not copy its hunks into every tool row.
      const recorded =
        file.diff ||
        (entry.sourceTurnId
          ? historyEntries()
              .filter(
                (candidate) =>
                  candidate.kind === 'diff' && candidate.sourceTurnId === entry.sourceTurnId,
              )
              .flatMap((candidate) => getHistoryChangedFiles(candidate, cwd))
              .find(
                (candidate) => candidate.path.replace(/\\/g, '/') === file.path.replace(/\\/g, '/'),
              )?.diff
          : '');
      openHistoryFileDiff(sessionId, { ...file, diff: recorded || '' }, renderRecorded, button);
    });
    body.append(button);
  }
  if (isHistoryError(entry)) {
    const error = document.createElement('pre');
    error.className = 'agent-history-body';
    error.textContent =
      entry.toolPresentation?.evidence || entry.toolPresentation?.outcome || entry.body;
    body.append(error);
  }
  return body;
}

/** CSS shrinks the directory first, retaining the filename and path ends without layout reads. */
function appendFilePath(button: HTMLElement, path: string): void {
  const separator = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'));
  const split = separator >= 0 ? separator + 1 : Math.ceil(path.length / 2);
  const start = document.createElement('span');
  start.className = 'agent-file-path-start';
  start.textContent = path.slice(0, split);
  const end = document.createElement('span');
  end.className = 'agent-file-path-end';
  const filename = document.createElement('span');
  filename.textContent = path.slice(split);
  end.append(filename);
  button.append(start, end);
}

function createDiffLineGutterNode(
  oldLineNumber: number | undefined,
  newLineNumber: number | undefined,
): HTMLElement {
  const gutter = document.createElement('span');
  gutter.className = 'agent-history-diff-line-gutter';
  gutter.appendChild(createDiffLineNumberNode(oldLineNumber, 'old'));
  gutter.appendChild(createDiffLineNumberNode(newLineNumber, 'new'));
  return gutter;
}

function createDiffLineNumberNode(value: number | undefined, lane: 'old' | 'new'): HTMLElement {
  const cell = document.createElement('span');
  cell.className = `agent-history-diff-line-number agent-history-diff-line-number-${lane}`;
  if (typeof value === 'number' && Number.isFinite(value)) {
    cell.textContent = String(value);
  } else {
    cell.textContent = '';
    cell.setAttribute('aria-hidden', 'true');
  }
  return cell;
}

export function createHistoryDiffBody(bodyText: string, cwd?: string | null): HTMLElement {
  const body = document.createElement('div');
  body.className = 'agent-history-body agent-history-diff-body';
  const content = document.createElement('pre');
  content.className = 'agent-history-diff-content';
  for (const line of buildRenderedDiffLines(bodyText, cwd && cwd.length > 0 ? cwd : null)) {
    const row = document.createElement('span');
    row.className = `agent-history-diff-line ${line.className}`;
    const hasLineNumbers =
      typeof line.oldLineNumber === 'number' || typeof line.newLineNumber === 'number';
    if (hasLineNumbers) {
      row.dataset.hasLineNumbers = 'true';
      row.appendChild(createDiffLineGutterNode(line.oldLineNumber, line.newLineNumber));
    }
    const text = document.createElement('span');
    text.className = 'agent-history-diff-line-text';
    text.textContent = line.text || ' ';
    row.appendChild(text);
    content.appendChild(row);
  }
  body.appendChild(content);
  return body;
}
