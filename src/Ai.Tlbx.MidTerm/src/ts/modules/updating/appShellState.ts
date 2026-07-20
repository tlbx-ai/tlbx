import type { SessionTabId } from '../sessionTabs';
import { $activeSessionId } from '../../stores';

const ACTIVE_SESSION_STORAGE_KEY = 'midterm.activeSessionId';
const SESSION_TAB_STORAGE_PREFIX = 'midterm.sessionTab.';
const APP_REFRESH_MARKER_KEY = 'midterm.pendingAppRefresh';

let activeSessionPersistenceInitialized = false;

function normalizeSessionId(value: string | null): string | null {
  const trimmed = value?.trim();
  return trimmed ? trimmed : null;
}

function isValidSessionTab(value: string | null): value is SessionTabId {
  return value === 'terminal' || value === 'agent' || value === 'files';
}

export function initAppShellStatePersistence(): void {
  if (activeSessionPersistenceInitialized) {
    return;
  }

  activeSessionPersistenceInitialized = true;
  persistActiveSessionId($activeSessionId.get());
  $activeSessionId.subscribe((sessionId) => {
    persistActiveSessionId(sessionId);
  });
}

export function getRememberedActiveSessionId(): string | null {
  try {
    return normalizeSessionId(localStorage.getItem(ACTIVE_SESSION_STORAGE_KEY));
  } catch {
    return null;
  }
}

export function persistActiveSessionId(sessionId: string | null): void {
  try {
    const normalized = normalizeSessionId(sessionId);
    if (normalized) {
      localStorage.setItem(ACTIVE_SESSION_STORAGE_KEY, normalized);
    } else {
      localStorage.removeItem(ACTIVE_SESSION_STORAGE_KEY);
    }
  } catch {
    // Ignore localStorage failures and keep the state in-memory only.
  }
}

export function getRememberedSessionTab(sessionId: string): SessionTabId | null {
  try {
    const value = localStorage.getItem(SESSION_TAB_STORAGE_PREFIX + sessionId);
    return isValidSessionTab(value) ? value : null;
  } catch {
    return null;
  }
}

export function persistSessionTab(sessionId: string, tab: SessionTabId): void {
  try {
    localStorage.setItem(SESSION_TAB_STORAGE_PREFIX + sessionId, tab);
  } catch {
    // Ignore localStorage failures and keep the state in-memory only.
  }
}

export function clearRememberedSessionState(sessionId: string): void {
  try {
    localStorage.removeItem(SESSION_TAB_STORAGE_PREFIX + sessionId);
    if (getRememberedActiveSessionId() === sessionId) {
      localStorage.removeItem(ACTIVE_SESSION_STORAGE_KEY);
    }
  } catch {
    // Ignore localStorage failures and keep the state in-memory only.
  }
}

export function clearPendingAppRefreshMarker(): void {
  try {
    sessionStorage.removeItem(APP_REFRESH_MARKER_KEY);
  } catch {
    // Ignore sessionStorage failures and continue.
  }

  document.documentElement.classList.remove('tlbx-app-refreshing');
}

export function reloadAppShell(): void {
  try {
    sessionStorage.setItem(APP_REFRESH_MARKER_KEY, '1');
  } catch {
    // Ignore sessionStorage failures and continue with the reload.
  }

  location.reload();
}
