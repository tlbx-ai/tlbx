import { t } from '../i18n';
import { registerBackButtonLayer } from '../navigation/backButtonGuard';
import { fetchDiffView, fetchGitRepos } from '../git/gitApi';
import { renderPatchFile } from '../git/gitDiffView';
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
  header.append(title, close);
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
  const releaseBack = registerBackButtonLayer(closeDialog);
  function cleanup(): void {
    if (disposed) return;
    disposed = true;
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
  body.textContent = t('git.loadingDiff');
  void loadCurrentDiff(sessionId, file.path, body).catch(() => {
    if (body.isConnected) body.textContent = t('git.diffUnavailable');
  });
}

async function loadCurrentDiff(sessionId: string, path: string, body: HTMLElement): Promise<void> {
  const normalize = (value: string) => value.replace(/\\/g, '/').replace(/\/$/, '');
  const matchCase = (value: string) => (/^[a-z]:/i.test(value) ? value.toLowerCase() : value);
  const absolute = normalize(path);
  const repos = await fetchGitRepos(sessionId);
  if (!body.isConnected) return;
  const repo = repos?.repos
    .filter((candidate) =>
      matchCase(absolute).startsWith(matchCase(normalize(candidate.repoRoot)) + '/'),
    )
    .sort((a, b) => b.repoRoot.length - a.repoRoot.length)[0];
  if (!repo) {
    body.textContent = t('appServerControl.files.noDiff');
    return;
  }
  const relative = absolute.slice(normalize(repo.repoRoot).length + 1);
  const staged =
    repo.status?.staged.some((entry) => entry.path === relative) &&
    !repo.status.modified.some((entry) => entry.path === relative);
  const diff = await fetchDiffView(
    sessionId,
    repo.repoRoot,
    relative,
    staged ? 'staged' : 'worktree',
  );
  if (!document.contains(body)) return;
  if (!diff?.files.length) {
    body.textContent = t('appServerControl.files.noDiff');
    return;
  }
  body.innerHTML = diff.files.map((file) => renderPatchFile(file, { historical: true })).join('');
  // This dialog is read-only; Git's optional preview action belongs to its full inspector.
  body.querySelectorAll('[data-action="open-file-viewer"]').forEach((button) => {
    button.remove();
  });
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
    button.textContent = file.path;
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
