/**
 * Git Module
 *
 * VS Code-like git integration as a sidebar dock.
 * Provides a live status indicator in the IDE bar and a full
 * git panel in a right-side dock.
 */

import { createLogger } from '../logging';
import {
  setGitClickHandler,
  setGitRepoActionHandlers,
  updateGitIndicatorForSession,
} from '../sessionTabs';
import { $activeSessionId } from '../../stores';
import { addProcessStateListener } from '../process';
import { updateGitStatus, destroyGitPanel } from './gitPanel';
import {
  setGitReposCallback,
  setGitStatusCallback,
  subscribeToSession,
  unsubscribeFromSession,
  triggerGitFallback,
} from './gitChannel';
import { toggleGitDock, closeGitDock, setupGitDockResize } from './gitDock';
import { registerGitDockCloser } from '../commands/dock';
import { addGitRepo, fetchGitRepos, refreshGitRepo, removeGitRepo } from './gitApi';
import { showTextPrompt } from '../../utils/dialog';
import type { GitRepoBinding, GitStatusResponse } from './types';
import type { GitDiagEvent } from './gitChannel';

const log = createLogger('git');

const cachedStatuses = new Map<string, GitStatusResponse>();
const cachedRepos = new Map<string, GitRepoBinding[]>();
const sessionCwds = new Map<string, string>();
const repoCacheListeners = new Set<(sessionId: string) => void>();
let previousSessionId: string | null = null;

export type { GitDiagEvent };

export function initGitPanel(): void {
  setGitStatusCallback((sessionId, status) => {
    cachedStatuses.set(makeRepoStatusKey(sessionId, status.repoRoot), status);
    syncCachedRepoStatus(sessionId, status);
    updateGitStatus(sessionId, status);
    updateGitIndicatorForSession(sessionId, cachedRepos.get(sessionId) ?? status);
  });

  setGitReposCallback((sessionId, repos) => {
    cacheRepos(sessionId, repos);
    updateGitIndicatorForSession(sessionId, repos);
  });

  $activeSessionId.subscribe((sessionId) => {
    if (previousSessionId && previousSessionId !== sessionId) {
      unsubscribeFromSession(previousSessionId);
    }
    previousSessionId = sessionId ?? null;

    if (!sessionId) {
      return;
    }
    subscribeToSession(sessionId);
    const cached = cachedRepos.get(sessionId);
    updateGitIndicatorForSession(sessionId, cached ?? null);
  });

  addProcessStateListener((sessionId, state) => {
    const oldCwd = sessionCwds.get(sessionId);
    const newCwd = state.foregroundCwd;

    if (!newCwd || newCwd === oldCwd) return;

    sessionCwds.set(sessionId, newCwd);

    if (oldCwd) {
      emitCwdDiag(sessionId, oldCwd, newCwd);
      clearSessionGitCache(sessionId);
      updateGitIndicatorForSession(sessionId, null);
      triggerGitFallback(sessionId);
    }
  });

  setGitClickHandler((repoRoot) => {
    const sessionId = $activeSessionId.get();
    if (sessionId) {
      toggleGitDock(sessionId, repoRoot || resolvePrimaryRepoRoot(sessionId));
    }
  });

  setGitRepoActionHandlers({
    add: (sessionId) => {
      void promptAndAddRepo(sessionId);
    },
    remove: (sessionId, repoRoot) => {
      void removeGitRepo(sessionId, repoRoot).then((response) => {
        if (response) {
          cacheRepos(sessionId, response.repos);
          updateGitIndicatorForSession(sessionId, response.repos);
        }
      });
    },
    refresh: (sessionId, repoRoot) => {
      void refreshGitRepo(sessionId, repoRoot).then((response) => {
        if (response) {
          cacheRepos(sessionId, response.repos);
          updateGitIndicatorForSession(sessionId, response.repos);
        }
      });
    },
  });

  registerGitDockCloser(closeGitDock);
  setupGitDockResize();

  log.info(() => 'Git panel initialized');
}

let cwdDiagCb: ((event: GitDiagEvent) => void) | null = null;

export function setGitCwdDiagCallback(cb: ((event: GitDiagEvent) => void) | null): void {
  cwdDiagCb = cb;
}

function emitCwdDiag(sessionId: string, oldCwd: string, newCwd: string): void {
  cwdDiagCb?.({
    type: 'cwd-change',
    detail: `${sessionId.substring(0, 8)}: ${oldCwd} → ${newCwd}`,
    timestamp: Date.now(),
  });
}

export {
  connectGitWebSocket,
  disconnectGitWebSocket,
  setGitDiagCallback,
  getGitWsState,
  getSubscribedSessions,
} from './gitChannel';

export function destroyGitSession(sessionId: string): void {
  unsubscribeFromSession(sessionId);
  destroyGitPanel(sessionId);
  clearSessionGitCache(sessionId);
  sessionCwds.delete(sessionId);
  if (previousSessionId === sessionId) {
    previousSessionId = null;
  }
}

export function getCachedGitReposForSession(sessionId: string): GitRepoBinding[] {
  return cachedRepos.get(sessionId) ?? [];
}

export function addGitRepoCacheListener(listener: (sessionId: string) => void): () => void {
  repoCacheListeners.add(listener);
  return () => {
    repoCacheListeners.delete(listener);
  };
}

function notifyGitRepoCacheChanged(sessionId: string): void {
  for (const listener of repoCacheListeners) {
    listener(sessionId);
  }
}

function cacheRepos(sessionId: string, repos: GitRepoBinding[]): void {
  cachedRepos.set(sessionId, repos);
  for (const repo of repos) {
    if (repo.status) {
      cachedStatuses.set(makeRepoStatusKey(sessionId, repo.repoRoot), repo.status);
    }
  }
  notifyGitRepoCacheChanged(sessionId);
}

function syncCachedRepoStatus(sessionId: string, status: GitStatusResponse): void {
  const repos = cachedRepos.get(sessionId);
  if (!repos) {
    cachedRepos.set(sessionId, [statusToRepoBinding(status)]);
    return;
  }

  const index = repos.findIndex((repo) => repo.repoRoot === status.repoRoot);
  if (index >= 0) {
    const existing = repos[index];
    if (existing) {
      repos[index] = { ...existing, status };
    }
  } else {
    repos.push(statusToRepoBinding(status));
  }
  notifyGitRepoCacheChanged(sessionId);
}

function statusToRepoBinding(status: GitStatusResponse): GitRepoBinding {
  return {
    repoRoot: status.repoRoot,
    label: status.label || status.role || status.branch || 'repo',
    role: status.role || (status.isPrimary ? 'cwd' : 'target'),
    source: status.source || 'auto',
    isPrimary: status.isPrimary !== false,
    status,
  };
}

function makeRepoStatusKey(sessionId: string, repoRoot?: string): string {
  return repoRoot ? `${sessionId}|${repoRoot}` : sessionId;
}

function resolvePrimaryRepoRoot(sessionId: string): string | undefined {
  const repos = cachedRepos.get(sessionId);
  return repos?.find((repo) => repo.isPrimary)?.repoRoot ?? repos?.[0]?.repoRoot;
}

function clearSessionGitCache(sessionId: string): void {
  cachedRepos.delete(sessionId);
  for (const key of cachedStatuses.keys()) {
    if (key === sessionId || key.startsWith(`${sessionId}|`)) {
      cachedStatuses.delete(key);
    }
  }
  notifyGitRepoCacheChanged(sessionId);
}

async function promptAndAddRepo(sessionId: string): Promise<void> {
  const path = await showTextPrompt({
    title: 'Track Git repository',
    message: 'Add another repository to the current MidTerm session.',
    placeholder: 'Q:\\repos\\Jpa',
    confirmLabel: 'Track',
    validate: (value) => (value.trim().length === 0 ? 'Repository path is required.' : null),
  });

  if (!path) {
    return;
  }

  const response = await addGitRepo(sessionId, path);
  if (response) {
    cacheRepos(sessionId, response.repos);
    updateGitIndicatorForSession(sessionId, response.repos);
    return;
  }

  const fallback = await fetchGitRepos(sessionId);
  if (fallback) {
    cacheRepos(sessionId, fallback.repos);
    updateGitIndicatorForSession(sessionId, fallback.repos);
  }
}
