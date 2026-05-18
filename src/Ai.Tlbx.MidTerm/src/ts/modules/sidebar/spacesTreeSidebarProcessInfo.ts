import { t } from '../i18n';
import { getCachedGitReposForSession } from '../git';
import type { GitRepoBinding, GitStatusResponse } from '../git/types';
import { getForegroundInfo } from '../process';
import { createForegroundIndicator } from './sessionList';

export interface SpacesTreeSidebarSessionProcessEntry {
  id: string;
  session: {
    currentDirectory?: string | null;
    workspacePath?: string | null;
    shellType?: string | null;
  };
}

function getForegroundDisplay(entry: SpacesTreeSidebarSessionProcessEntry): {
  cwd: string | null;
  commandLine: string | null;
  processName: string;
  displayName: string | null;
} {
  const foreground = getForegroundInfo(entry.id);
  return {
    cwd: foreground.cwd || entry.session.currentDirectory || entry.session.workspacePath || null,
    commandLine: foreground.commandLine,
    processName: foreground.name?.trim() || entry.session.shellType || t('session.terminal'),
    displayName: foreground.displayName,
  };
}

function getForegroundSignature(entry: SpacesTreeSidebarSessionProcessEntry): string {
  const display = getForegroundDisplay(entry);
  return [
    display.cwd ?? '',
    display.commandLine ?? '',
    display.processName,
    display.displayName ?? '',
    getExtraGitRepoSignature(entry.id),
  ].join('\u001f');
}

function getExtraGitRepoSignature(sessionId: string): string {
  return getCachedGitReposForSession(sessionId)
    .filter(isExtraRepo)
    .map(getExtraGitRepoSignaturePart)
    .join('\u001e');
}

function isExtraRepo(repo: GitRepoBinding): boolean {
  return !repo.isPrimary;
}

function getExtraGitRepoSignaturePart(repo: GitRepoBinding): string {
  const statusParts = getExtraGitRepoStatusSignatureParts(repo.status ?? null);
  return [repo.repoRoot, repo.label, repo.role, ...statusParts].join(':');
}

function getExtraGitRepoStatusSignatureParts(
  status: GitStatusResponse | null,
): Array<string | number> {
  if (!status) {
    return ['', 0, 0, 0, 0, 0, 0, 0, 0];
  }

  return [
    status.branch,
    status.totalAdditions,
    status.totalDeletions,
    status.ahead,
    status.behind,
    status.staged.length,
    status.modified.length,
    status.untracked.length,
    status.conflicted.length,
  ];
}

export function syncSpacesTreeSidebarSessionProcessInfoElement(
  processInfo: HTMLElement,
  entry: SpacesTreeSidebarSessionProcessEntry,
): void {
  const signature = getForegroundSignature(entry);
  if (processInfo.dataset.foregroundSignature === signature) {
    return;
  }

  const display = getForegroundDisplay(entry);
  const children: HTMLElement[] = [
    createForegroundIndicator(
      display.cwd,
      display.commandLine,
      display.processName,
      display.displayName,
    ),
  ];
  children.push(...createExtraGitRepoLines(entry.id));
  processInfo.dataset.foregroundSignature = signature;
  processInfo.replaceChildren(...children);
}

function createExtraGitRepoLines(sessionId: string): HTMLElement[] {
  return getCachedGitReposForSession(sessionId).filter(isExtraRepo).map(createExtraGitRepoLine);
}

function createExtraGitRepoLine(repo: GitRepoBinding): HTMLElement {
  const status = repo.status ?? null;
  const line = document.createElement('div');
  line.className = 'session-extra-git';
  line.title = buildExtraGitRepoTitle(repo, status);

  const details = document.createElement('span');
  details.className = 'session-extra-git-details';

  const repoName = document.createElement('span');
  repoName.className = 'session-extra-git-repo';
  appendMiddleEllipsizedPath(
    repoName,
    repo.repoRoot || repo.label || getRepoNameFromRoot(repo.repoRoot) || repo.role || 'repo',
  );

  const branchSeparator = document.createElement('span');
  branchSeparator.className = 'session-extra-git-separator';
  branchSeparator.textContent = '-';

  const branch = document.createElement('span');
  branch.className = 'session-extra-git-branch';
  branch.textContent = status?.branch || 'HEAD';

  const additions = document.createElement('span');
  additions.className = 'session-extra-git-stat-additions';
  additions.textContent = `+${status?.totalAdditions ?? 0}`;

  const deletions = document.createElement('span');
  deletions.className = 'session-extra-git-stat-deletions';
  deletions.textContent = `-${status?.totalDeletions ?? 0}`;

  const stats = document.createElement('span');
  stats.className = 'session-extra-git-stats';
  stats.append(additions, deletions);

  details.append(repoName, branchSeparator, branch);
  line.append(details, stats);
  return line;
}

function buildExtraGitRepoTitle(repo: GitRepoBinding, status: GitStatusResponse | null): string {
  const repoName =
    repo.repoRoot || repo.label || getRepoNameFromRoot(repo.repoRoot) || repo.role || 'repo';
  const branch = status?.branch || 'HEAD';
  const sync =
    status && (status.ahead > 0 || status.behind > 0)
      ? `, ahead ${status.ahead}, behind ${status.behind}`
      : '';
  return `${repoName} - ${branch} - +${status?.totalAdditions ?? 0} -${status?.totalDeletions ?? 0}${sync}\n${repo.repoRoot}`;
}

function getRepoNameFromRoot(repoRoot: string): string {
  const trimmed = repoRoot.replace(/[\\/]+$/, '');
  return trimmed.split(/[\\/]/).pop() ?? trimmed;
}

function appendMiddleEllipsizedPath(host: HTMLElement, path: string): void {
  const parts = splitPathForMiddleEllipsis(path);
  const root = document.createElement('span');
  root.className = 'session-extra-git-path-root';
  root.textContent = parts.root;

  const middle = document.createElement('span');
  middle.className = 'session-extra-git-path-middle';
  middle.textContent = parts.middle;

  const tail = document.createElement('span');
  tail.className = 'session-extra-git-path-tail';
  tail.textContent = parts.tail;

  host.append(root, middle, tail);
}

function splitPathForMiddleEllipsis(path: string): { root: string; middle: string; tail: string } {
  const trimmed = path.replace(/[\\/]+$/, '');
  const separator = trimmed.includes('\\') ? '\\' : '/';
  const rootMatch = /^[A-Za-z]:[\\/]/.exec(trimmed);
  let root = rootMatch ? trimmed.slice(0, rootMatch[0].length) : '';
  let rest = root ? trimmed.slice(root.length) : trimmed;

  if (!root && trimmed.startsWith('\\\\')) {
    const parts = trimmed.slice(2).split(/[\\/]/);
    if (parts.length >= 2) {
      root = `\\\\${parts[0]}\\${parts[1]}\\`;
      rest = parts.slice(2).join('\\');
    }
  } else if (!root && trimmed.startsWith('/')) {
    root = '/';
    rest = trimmed.slice(1);
  }

  const segments = rest.split(/[\\/]/).filter(Boolean);
  if (segments.length === 0) {
    return { root: '', middle: '', tail: trimmed };
  }

  const tail = segments[segments.length - 1] ?? trimmed;
  const middle = segments.length > 1 ? `${segments.slice(0, -1).join(separator)}${separator}` : '';
  return { root, middle, tail };
}

export function syncSpacesTreeSidebarSessionProcessInfo(
  host: HTMLElement,
  entries: SpacesTreeSidebarSessionProcessEntry[],
  sessionId: string,
): boolean {
  const entry = entries.find((candidate) => candidate.id === sessionId);
  if (!entry) {
    return true;
  }

  const items = Array.from(
    host.querySelectorAll<HTMLElement>('.session-item[data-session-id]'),
  ).filter((item) => item.dataset.sessionId === sessionId);
  if (items.length === 0) {
    return true;
  }

  for (const item of items) {
    const processInfo = item.querySelector<HTMLElement>('.session-process-info');
    if (!processInfo) {
      return false;
    }
    syncSpacesTreeSidebarSessionProcessInfoElement(processInfo, entry);
  }

  return true;
}
