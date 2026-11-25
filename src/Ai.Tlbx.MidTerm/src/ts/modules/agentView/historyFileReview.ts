import { t } from '../i18n';
import { readFileDocument, type FileDocument } from '../fileViewer/documentApi';
import { createLineNumberedViewer } from '../fileViewer/rendering';
import {
  fetchCommitDetails,
  fetchDiffView,
  fetchGitHistoryPage,
  fetchGitRepoRoots,
} from '../git/gitApi';
import { renderPatchFile } from '../git/gitDiffView';
import type { GitDiffFileView, GitLogEntry } from '../git/types';

interface FileRepository {
  root: string;
  path: string;
  head: string;
}

/** Read-only file/history fallback. File reads never wait for repository metadata. */
export function createHistoryFileReview(
  sessionId: string,
  path: string,
  scope: HTMLElement,
  signal: AbortSignal,
): HTMLElement {
  return new HistoryFileReview(sessionId, path, scope, signal).root;
}

class HistoryFileReview {
  readonly root = document.createElement('div');
  private readonly content = document.createElement('div');
  private readonly commits = document.createElement('div');
  private readonly more = button('git.loadMoreHistory', () => void this.loadHistory());
  private readonly fileButton = button(
    'appServerControl.files.currentFile',
    () => void this.showFile(),
  );
  private readonly currentButton = button(
    'appServerControl.files.currentDiff',
    () => void this.showCurrent(),
  );
  private repository: Promise<FileRepository | null>;
  private readonly filePages = new Map<number, FileDocument>();
  private readonly offsets = [0];
  private readonly lineNumbers = [1];
  private selection = new AbortController();
  private skip = 0;
  private page = 0;
  private repositoryReadFailed = false;

  constructor(
    private readonly sessionId: string,
    private readonly path: string,
    private readonly scope: HTMLElement,
    private readonly signal: AbortSignal,
  ) {
    this.root.className = 'agent-file-review';
    this.currentButton.disabled = true;
    this.content.className = 'agent-file-review-content';
    this.content.setAttribute('role', 'region');
    this.content.setAttribute('aria-label', path);
    const main = document.createElement('section');
    main.className = 'agent-file-review-main';
    const toolbar = document.createElement('div');
    toolbar.className = 'agent-file-review-toolbar';
    toolbar.append(this.fileButton, this.currentButton);
    main.append(toolbar, this.content);
    const aside = document.createElement('aside');
    aside.className = 'agent-file-review-history';
    aside.setAttribute('aria-label', t('appServerControl.files.fileHistory'));
    const title = document.createElement('h4');
    title.textContent = t('appServerControl.files.fileHistory');
    this.commits.className = 'agent-file-review-commits';
    this.commits.textContent = t('fileViewer.loading');
    this.more.hidden = true;
    aside.append(title, this.commits, this.more);
    this.root.append(main, aside);
    this.repository = this.resolveRepository();
    void this.showFile();
    void this.loadHistory();
  }

  private isActive(signal: AbortSignal): boolean {
    return !signal.aborted && this.root.isConnected;
  }

  private begin(label: string, selected: HTMLButtonElement): AbortSignal {
    this.selection.abort();
    this.selection = new AbortController();
    this.scope.textContent = label;
    this.content.textContent = t('fileViewer.loading');
    for (const candidate of this.root.querySelectorAll<HTMLButtonElement>('[aria-pressed]'))
      candidate.setAttribute('aria-pressed', String(candidate === selected));
    selected.setAttribute('aria-pressed', 'true');
    return AbortSignal.any([this.signal, this.selection.signal]);
  }

  private async showFile(): Promise<void> {
    const signal = this.begin(t('appServerControl.files.currentFile'), this.fileButton);
    const offset = this.offsets[this.page] ?? 0;
    try {
      const document =
        this.filePages.get(offset) ??
        (await readFileDocument(this.path, this.sessionId, false, signal, offset));
      if (!this.isActive(signal)) return;
      this.filePages.set(offset, document);
      if (this.filePages.size > 8) {
        const oldest = this.filePages.keys().next().value;
        if (oldest !== undefined) this.filePages.delete(oldest);
      }
      const view = createLineNumberedViewer(
        document.content,
        [],
        undefined,
        this.lineNumbers[this.page] ?? 1,
      );
      this.content.replaceChildren(view.root);
      this.renderFilePages(document);
    } catch {
      if (this.isActive(signal))
        this.content.textContent = t('appServerControl.files.fileUnavailable');
    }
  }

  private renderFilePages(document: FileDocument): void {
    if (this.page === 0 && !document.isTruncated) return;
    const controls = window.document.createElement('div');
    controls.className = 'agent-file-review-toolbar';
    const previous = button('fileViewer.previousPage', () => {
      this.page--;
      void this.showFile();
    });
    previous.disabled = this.page === 0;
    const next = button('fileViewer.nextPage', () => {
      this.offsets[++this.page] = document.nextOffset;
      this.lineNumbers[this.page] =
        (this.lineNumbers[this.page - 1] ?? 1) + (document.content.match(/\n/g)?.length ?? 0);
      void this.showFile();
    });
    next.disabled = document.nextOffset >= document.size;
    controls.append(previous, next);
    this.content.append(controls);
  }

  private async resolveRepository(): Promise<FileRepository | null> {
    const repos = await fetchGitRepoRoots(this.sessionId, this.signal);
    this.repositoryReadFailed = repos === null;
    const normalize = (value: string) => value.replace(/\\/g, '/').replace(/\/$/, '');
    const compare = (value: string) =>
      /^[a-z]:/i.test(value) || value.startsWith('//') ? value.toLowerCase() : value;
    const absolute = normalize(this.path);
    const repo = repos
      ?.filter((candidate) =>
        compare(absolute).startsWith(compare(normalize(candidate.repoRoot)) + '/'),
      )
      .sort((a, b) => b.repoRoot.length - a.repoRoot.length)[0];
    if (!repo) return null;
    return {
      root: repo.repoRoot,
      path: absolute.slice(normalize(repo.repoRoot).length + 1),
      head: repo.status?.headHash ?? '',
    };
  }

  private async loadHistory(): Promise<void> {
    this.more.disabled = true;
    if (this.repositoryReadFailed) this.repository = this.resolveRepository();
    const repo = await this.repository;
    if (!this.isActive(this.signal)) return;
    this.currentButton.disabled = !repo;
    if (!repo) {
      this.commits.textContent = t(
        this.repositoryReadFailed ? 'git.historyReadFailed' : 'appServerControl.files.noRepository',
      );
      this.more.hidden = !this.repositoryReadFailed;
      this.more.disabled = false;
      this.more.textContent = t('fileViewer.refresh');
      return;
    }
    const entries = await fetchGitHistoryPage(this.sessionId, repo.root, repo.head, this.skip, 20, {
      path: repo.path,
      signal: this.signal,
    });
    if (!this.isActive(this.signal)) return;
    if (!entries) {
      if (!this.skip) this.commits.textContent = t('git.historyReadFailed');
      this.more.hidden = false;
      this.more.disabled = false;
      this.more.textContent = t('fileViewer.refresh');
      return;
    }
    if (!this.skip) this.commits.replaceChildren();
    for (const entry of entries) this.commits.append(this.commitButton(entry));
    this.skip += entries.length;
    if (!this.skip) this.commits.textContent = t('appServerControl.files.noFileHistory');
    this.more.hidden = entries.length < 20;
    this.more.disabled = false;
    this.more.textContent = t('git.loadMoreHistory');
  }

  private commitButton(entry: GitLogEntry): HTMLButtonElement {
    const item = window.document.createElement('button');
    item.type = 'button';
    item.className = 'agent-file-review-commit';
    item.setAttribute('aria-pressed', 'false');
    const message = window.document.createElement('span');
    message.textContent = entry.message;
    const meta = window.document.createElement('small');
    meta.textContent = `${entry.shortHash} · ${entry.date.slice(0, 10)} · ${entry.author}`;
    item.title = `${entry.hash}\n${entry.message}\n${entry.date}`;
    item.append(message, meta);
    item.addEventListener('click', () => void this.showCommit(entry, item));
    return item;
  }

  private async showCommit(entry: GitLogEntry, item: HTMLButtonElement): Promise<void> {
    const signal = this.begin(`${entry.shortHash} · ${entry.message}`, item);
    const repo = await this.repository;
    if (!repo || !this.isActive(signal)) return;
    const result = await fetchCommitDetails(this.sessionId, repo.root, entry.hash, 0, {
      path: repo.path,
      signal,
    });
    if (!this.isActive(signal)) return;
    if (!result) {
      this.content.textContent = t('git.commitUnavailable');
      return;
    }
    this.scope.textContent = `${result.comparedParentHash?.slice(0, 8) ?? '∅'} → ${entry.shortHash} · ${entry.message}`;
    this.showPatch(result.files, result.isTruncated);
  }

  private async showCurrent(): Promise<void> {
    const signal = this.begin(t('appServerControl.files.currentDiff'), this.currentButton);
    const repo = await this.repository;
    if (!repo || !this.isActive(signal)) return;
    const results = await Promise.all(
      ['worktree', 'staged'].map((scope) =>
        fetchDiffView(this.sessionId, repo.root, repo.path, scope as 'worktree' | 'staged', signal),
      ),
    );
    if (!this.isActive(signal)) return;
    this.content.replaceChildren();
    for (const result of results) {
      if (!result) {
        this.content.append(t('git.diffUnavailable'));
        continue;
      }
      if (!result.files.length) continue;
      const section = document.createElement('section');
      const title = document.createElement('h4');
      title.textContent = t(result.scope === 'staged' ? 'git.stagedScope' : 'git.worktreeScope');
      const patch = document.createElement('div');
      this.showPatch(result.files, result.isTruncated, patch);
      section.append(title, patch);
      this.content.append(section);
    }
    if (!this.content.childNodes.length) this.content.textContent = t('git.noPatch');
  }

  private showPatch(files: GitDiffFileView[], truncated: boolean, target = this.content): void {
    target.innerHTML = files.map((file) => renderPatchFile(file, { historical: true })).join('');
    target.querySelectorAll('[data-action="open-file-viewer"]').forEach((button) => {
      button.remove();
    });
    if (!files.length) target.textContent = t('git.noPatch');
    if (truncated) {
      const notice = document.createElement('p');
      notice.textContent = t('git.patchTruncated');
      target.append(notice);
    }
  }
}

function button(key: string, action: () => void): HTMLButtonElement {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'btn-secondary';
  button.textContent = t(key);
  button.addEventListener('click', action);
  return button;
}
