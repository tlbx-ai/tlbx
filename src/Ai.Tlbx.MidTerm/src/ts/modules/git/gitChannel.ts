/**
 * Git WebSocket Channel
 *
 * Manages real-time git status updates via WebSocket.
 */

import { createLogger } from '../logging';
import { ReconnectController, createWsUrl } from '../../utils';
import { handleAuthenticatedWebSocketClose } from '../auth/sessionLifetime';
import { fetchGitRepos, fetchGitStatus } from './gitApi';
import type { GitRepoBinding, GitWsMessage, GitStatusResponse } from './types';

const log = createLogger('gitChannel');
const gitReconnect = new ReconnectController();

let ws: WebSocket | null = null;
let browserBackgroundSuspended = false;
const subscribedSessions = new Set<string>();
let statusCallback: ((sessionId: string, status: GitStatusResponse) => void) | null = null;
let reposCallback: ((sessionId: string, repos: GitRepoBinding[]) => void) | null = null;
let connectionCallback: ((connected: boolean) => void) | null = null;
export function setGitConnectionCallback(callback: (connected: boolean) => void): void {
  connectionCallback = callback;
}

export type GitDiagEvent = {
  type: string;
  detail: string;
  timestamp: number;
};

type GitDiagCallback = (event: GitDiagEvent) => void;
let diagCallback: GitDiagCallback | null = null;

const pendingFallbacks = new Map<string, number>();

function emitDiag(type: string, detail: string): void {
  diagCallback?.({ type, detail, timestamp: Date.now() });
}

export function setGitDiagCallback(cb: GitDiagCallback | null): void {
  diagCallback = cb;
}

export function getGitWsState(): string {
  if (!ws) return 'disconnected';
  switch (ws.readyState) {
    case WebSocket.CONNECTING:
      return 'connecting';
    case WebSocket.OPEN:
      return 'open';
    case WebSocket.CLOSING:
      return 'closing';
    case WebSocket.CLOSED:
      return 'closed';
    default:
      return 'unknown';
  }
}

export function getSubscribedSessions(): string[] {
  return Array.from(subscribedSessions);
}

export function setGitStatusCallback(
  cb: (sessionId: string, status: GitStatusResponse) => void,
): void {
  statusCallback = cb;
}

export function setGitReposCallback(
  cb: (sessionId: string, repos: GitRepoBinding[]) => void,
): void {
  reposCallback = cb;
}

function handleStatusMessage(msg: GitWsMessage): void {
  if (!msg.status) return;
  const targetSessionIds = msg.sessionIds?.length ? msg.sessionIds : [msg.sessionId];
  const staged = msg.status.stagedCount ?? msg.status.staged.length;
  const modified = msg.status.modifiedCount ?? msg.status.modified.length;
  const untracked = msg.status.untrackedCount ?? msg.status.untracked.length;
  const summary = `${msg.status.branch} +${staged} ~${modified} ?${untracked}`;
  for (const targetSessionId of targetSessionIds) {
    if (!targetSessionId) continue;
    cancelFallback(targetSessionId);
    emitDiag('status', `${targetSessionId.substring(0, 8)}: ${summary}`);
    statusCallback?.(targetSessionId, msg.status);
  }
}

export function connectGitWebSocket(): void {
  if (browserBackgroundSuspended) return;
  if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;

  const socket = new WebSocket(createWsUrl('/ws/git'));
  ws = socket;

  socket.onopen = () => {
    if (ws !== socket) return;
    gitReconnect.reset();
    connectionCallback?.(true);
    log.info(() => 'Git WebSocket connected');
    emitDiag('ws-open', 'connected');
    for (const sessionId of subscribedSessions) {
      sendSubscribe(sessionId);
    }
  };

  socket.onmessage = (event) => {
    if (ws !== socket) return;
    try {
      const msg = JSON.parse(event.data as string) as GitWsMessage;
      if (msg.type === 'status' && msg.status && (msg.sessionId || msg.sessionIds?.length)) {
        handleStatusMessage(msg);
      } else if (msg.type === 'repos' && msg.repos && msg.sessionId) {
        cancelFallback(msg.sessionId);
        emitDiag('repos', `${msg.sessionId.substring(0, 8)}: ${msg.repos.length}`);
        reposCallback?.(msg.sessionId, msg.repos);
      }
    } catch (e) {
      log.error(() => `Failed to parse git WS message: ${String(e)}`);
    }
  };

  socket.onerror = () => {
    if (ws !== socket) return;
    log.warn(() => 'Git WebSocket error');
    emitDiag('ws-error', 'connection error');
    connectionCallback?.(false);
  };

  socket.onclose = (event) => {
    if (ws !== socket) return;
    log.info(() => 'Git WebSocket closed');
    emitDiag('ws-close', 'disconnected');
    ws = null;
    connectionCallback?.(false);
    if (handleAuthenticatedWebSocketClose(event)) {
      return;
    }
    if (!browserBackgroundSuspended && subscribedSessions.size > 0) {
      gitReconnect.schedule(connectGitWebSocket);
    }
  };
}

function sendSubscribe(sessionId: string): void {
  if (ws?.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify({ type: 'subscribe', sessionId }));
  }
}

function sendUnsubscribe(sessionId: string): void {
  if (ws?.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify({ type: 'unsubscribe', sessionId }));
  }
}

function scheduleFallback(sessionId: string): void {
  cancelFallback(sessionId);
  const timer = window.setTimeout(() => {
    pendingFallbacks.delete(sessionId);
    emitDiag('fallback', `REST fallback for ${sessionId.substring(0, 8)}`);
    void runFallbackAsync(sessionId);
  }, 3000);
  pendingFallbacks.set(sessionId, timer);
}

async function runFallbackAsync(sessionId: string): Promise<void> {
  try {
    const response = await fetchGitRepos(sessionId);
    if (response?.repos.length) {
      reposCallback?.(sessionId, response.repos);
      for (const repo of response.repos) {
        if (repo.status) {
          statusCallback?.(sessionId, repo.status);
        }
      }
      emitDiag('fallback-ok', `${response.repos.length} repos`);
      return;
    }
  } catch {
    // Fall through to the legacy single-repo status request.
  }

  const status = await fetchGitStatus(sessionId);
  if (status) {
    emitDiag('fallback-ok', status.branch);
    statusCallback?.(sessionId, status);
  } else {
    emitDiag('fallback-err', `no status for ${sessionId.substring(0, 8)}`);
  }
}

function cancelFallback(sessionId: string): void {
  const timer = pendingFallbacks.get(sessionId);
  if (timer !== undefined) {
    clearTimeout(timer);
    pendingFallbacks.delete(sessionId);
  }
}

export function triggerGitFallback(sessionId: string): void {
  scheduleFallback(sessionId);
}

export function disconnectGitWebSocket(): void {
  gitReconnect.cancel();
  for (const timer of pendingFallbacks.values()) {
    clearTimeout(timer);
  }
  pendingFallbacks.clear();
  if (ws) {
    ws.onclose = null;
    ws.close();
    ws = null;
  }
  log.info(() => 'Git WebSocket disconnected (IDE mode off)');
}

export function suspendGitWebSocketForBrowserBackground(): void {
  if (browserBackgroundSuspended) return;
  browserBackgroundSuspended = true;
  gitReconnect.cancel();
  for (const timer of pendingFallbacks.values()) clearTimeout(timer);
  pendingFallbacks.clear();
  const socket = ws;
  ws = null;
  if (socket) {
    socket.onopen = null;
    socket.onmessage = null;
    socket.onerror = null;
    socket.onclose = null;
    socket.close();
  }
}

export function recoverGitWebSocketAfterBrowserResume(): void {
  browserBackgroundSuspended = false;
  if (subscribedSessions.size > 0) connectGitWebSocket();
}

export function subscribeToSession(sessionId: string): void {
  subscribedSessions.add(sessionId);
  emitDiag('subscribe', sessionId.substring(0, 8));
  sendSubscribe(sessionId);
  scheduleFallback(sessionId);
  if (!ws || ws.readyState !== WebSocket.OPEN) {
    connectGitWebSocket();
  }
}

export function unsubscribeFromSession(sessionId: string): void {
  subscribedSessions.delete(sessionId);
  cancelFallback(sessionId);
  emitDiag('unsubscribe', sessionId.substring(0, 8));
  sendUnsubscribe(sessionId);
}
