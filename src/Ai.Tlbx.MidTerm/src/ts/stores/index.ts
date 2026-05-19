/**
 * Nanostores State Management
 *
 * Reactive state management using nanostores.
 * Replaces imperative state from state.ts with reactive stores.
 *
 * Naming convention: $storeName (dollar prefix for stores)
 *
 * Store types:
 * - atom: single value
 * - map: key-value collection
 * - computed: derived from other stores
 */

import { atom, map, computed } from 'nanostores';
import type {
  BrowserSessionStatus,
  Session,
  MidTermSettingsPublic,
  UpdateInfo,
  UpdateType,
  AuthStatusResponse,
  ProcessState,
  DisplayLayout,
  ShareAccessMode,
  ManagerBarQueueEntry,
} from '../types';

// =============================================================================
// Session Stores
// =============================================================================

/**
 * Session collection keyed by session ID.
 * Use $sessions.setKey(id, session) for updates.
 */
export const $sessions = map<Record<string, Session>>({});

/** Currently active session ID */
export const $activeSessionId = atom<string | null>(null);

/**
 * Pending renames awaiting server confirmation.
 * Maps sessionId -> pending name (empty string means clearing the name).
 * Protects optimistic updates from being overwritten by stale server state.
 */
const pendingRenames = new Map<string, string>();

/**
 * Mark a rename as pending (before optimistic update).
 * The pending name will be preserved until server confirms it.
 */
export function setPendingRename(sessionId: string, name: string): void {
  pendingRenames.set(sessionId, name);
}

/**
 * Clear pending rename when server confirms or on rollback.
 */
export function clearPendingRename(sessionId: string): void {
  pendingRenames.delete(sessionId);
}

/**
 * Sessions as a sorted array for rendering.
 * Top-level sessions sorted by _order, with tmux children inserted after their parent.
 */
export const $sessionList = computed($sessions, (sessions) => {
  const all = Object.values(sessions);
  const topLevel = all
    .filter((s) => !s.parentSessionId)
    .sort((a, b) => (a._order ?? 0) - (b._order ?? 0));
  const allSessionIds = new Set(all.map((s) => s.id));
  const childrenByParent = new Map<string, Session[]>();
  for (const s of all) {
    if (s.parentSessionId) {
      let children = childrenByParent.get(s.parentSessionId);
      if (!children) {
        children = [];
        childrenByParent.set(s.parentSessionId, children);
      }
      children.push(s);
    }
  }
  const result: Session[] = [];
  for (const parent of topLevel) {
    result.push(parent);
    const children = childrenByParent.get(parent.id);
    if (children) {
      children.sort((a, b) => (a._order ?? 0) - (b._order ?? 0));
      result.push(...children);
    }
  }
  // Orphaned children (parent no longer exists) go at the end
  for (const s of all) {
    if (s.parentSessionId && !allSessionIds.has(s.parentSessionId)) {
      result.push(s);
    }
  }
  return result;
});

/**
 * Check if a session is a tmux child session.
 */
export function isChildSession(sessionId: string): boolean {
  const session = $sessions.get()[sessionId];
  return !!session?.parentSessionId;
}

/**
 * Get the parent session ID for a tmux child session.
 */
export function getParentSessionId(sessionId: string): string | null {
  const session = $sessions.get()[sessionId];
  return session?.parentSessionId ?? null;
}

/** Current active session object (derived) */
export const $activeSession = computed([$sessions, $activeSessionId], (sessions, activeId) =>
  activeId ? (sessions[activeId] ?? null) : null,
);

/** Whether there are any sessions */
export const $hasSessions = computed($sessionList, (list) => list.length > 0);

// =============================================================================
// Process State Store
// =============================================================================

/**
 * Process state collection keyed by session ID.
 * Tracks foreground process and racing subprocess log.
 */
export const $processStates = map<Record<string, ProcessState>>({});

// =============================================================================
// UI State Stores
// =============================================================================

/** Settings panel visibility */
export const $settingsOpen = atom<boolean>(false);

/** Mobile sidebar visibility */
export const $sidebarOpen = atom<boolean>(false);

/** Desktop sidebar collapsed state */
export const $sidebarCollapsed = atom<boolean>(false);

/** File viewer docked state */
export const $fileViewerDocked = atom<boolean>(false);

/** Docked file path */
export const $dockedFilePath = atom<string | null>(null);

/** Commands panel docked to right sidebar */
export const $commandsPanelDocked = atom<boolean>(false);

/** Git panel docked to right sidebar */
export const $gitPanelDocked = atom<boolean>(false);

/** Web preview panel docked to right sidebar */
export const $webPreviewDocked = atom<boolean>(false);

/** Current web preview URL */
export const $webPreviewUrl = atom<string | null>(null);

/** Whether web preview is detached to popup window */
export const $webPreviewDetached = atom<boolean>(false);

/** Custom viewport size for responsive testing (null = full size) */
export const $webPreviewViewport = atom<{ width: number; height: number } | null>(null);

// =============================================================================
// Connection State Stores
// =============================================================================

/** State WebSocket connected flag */
export const $stateWsConnected = atom<boolean>(false);

/** Mux WebSocket connected flag */
export const $muxWsConnected = atom<boolean>(false);

/** Data loss detected for a session (output queue overflow) */
export const $dataLossDetected = atom<{ sessionId: string; timestamp: number } | null>(null);

/** Tracks if mux WebSocket has ever connected (for reconnect detection) */
export const $muxHasConnected = atom<boolean>(false);

/**
 * Connection status (derived).
 * Replaces updateConnectionStatus() function.
 */
export const $connectionStatus = computed(
  [$stateWsConnected, $muxWsConnected],
  (stateConnected, muxConnected): 'connected' | 'disconnected' | 'reconnecting' => {
    if (stateConnected && muxConnected) return 'connected';
    if (!stateConnected && !muxConnected) return 'disconnected';
    return 'reconnecting';
  },
);

// =============================================================================
// Data Stores
// =============================================================================

/** User settings from server */
export const $currentSettings = atom<MidTermSettingsPublic | null>(null);

/** Update info from server */
export const $updateInfo = atom<UpdateInfo | null>(null);
export const $managerBarQueue = atom<ManagerBarQueueEntry[]>([]);

export interface FrontendRefreshState {
  clientVersion: string;
  serverVersion: string;
  updateType: UpdateType | 'unknown';
  status: 'available' | 'required';
  reason: 'server-update';
}

/** Pending shell refresh state when the server is newer than the live frontend bundle. */
export const $frontendRefreshState = atom<FrontendRefreshState | null>(null);

/** Auth status from server */
export const $authStatus = atom<AuthStatusResponse | null>(null);

/** Windows build number for ConPTY configuration (null on non-Windows) */
export const $windowsBuildNumber = atom<number | null>(null);

/** Server hostname for tab title */
export const $serverHostname = atom<string>('');

/** Voice server password for authentication */
export const $voiceServerPassword = atom<string | null>(null);

/** Settings WebSocket connected flag */
export const $settingsWsConnected = atom<boolean>(false);

/** Whether the app is running in shared-session mode */
export const $sharedSessionMode = atom<boolean>(false);

/** Shared session ID for capability-scoped mode */
export const $sharedSessionId = atom<string | null>(null);

/** Shared session access mode */
export const $sharedAccessMode = atom<ShareAccessMode | null>(null);

/** Shared session link expiry */
export const $sharedExpiresAt = atom<string | null>(null);

// =============================================================================
// Helper Functions
// =============================================================================

/**
 * Get session by ID from the store.
 * Convenience function for quick lookups.
 */
export function getSession(sessionId: string): Session | undefined {
  return $sessions.get()[sessionId];
}

/**
 * Update a session in the store.
 * Creates if doesn't exist, updates if exists.
 * Preserves _order for existing sessions, assigns high order for new ones.
 */
export function setSession(session: Session): void {
  const id = session.id;
  if (!id) return;
  const existing = $sessions.get()[id];
  const order = session._order ?? existing?._order ?? Date.now();
  $sessions.setKey(id, { ...session, _order: order });
}

/**
 * Remove a session from the store.
 */
export function removeSession(sessionId: string): void {
  const sessions = { ...$sessions.get() };
  // eslint-disable-next-line @typescript-eslint/no-dynamic-delete
  delete sessions[sessionId];
  $sessions.set(sessions);
}

/**
 * Set all sessions (replaces entire collection).
 * Used when receiving session list from server.
 * Uses server's order field if present, otherwise array index.
 * Preserves pending rename names until server confirms them.
 */
export function setSessions(sessionList: Session[]): boolean {
  const currentSessions = $sessions.get();
  const sessionsMap: Record<string, Session> = {};
  sessionList.forEach((session) => {
    const id = session.id;
    if (!id) {
      return;
    }

    let name: string = session.name ?? '';

    // Check for pending rename
    const pendingName = pendingRenames.get(id);
    if (pendingName !== undefined) {
      if (session.name === pendingName) {
        // Server confirmed our rename - clear pending
        pendingRenames.delete(id);
      } else {
        // Server still has old name - preserve our pending name
        name = pendingName;
      }
    }

    const entry: Session = { ...session, name, _order: session.order };
    sessionsMap[id] = entry;
  });

  if (areJsonLikeRecordsEqual(currentSessions, sessionsMap)) {
    return false;
  }

  $sessions.set(sessionsMap);
  return true;
}

export function setManagerBarQueue(entries: ManagerBarQueueEntry[]): boolean {
  if (areJsonLikeEqual(entries, $managerBarQueue.get())) {
    return false;
  }

  $managerBarQueue.set(entries);
  return true;
}

function areJsonLikeRecordsEqual(
  left: Record<string, unknown>,
  right: Record<string, unknown>,
): boolean {
  const leftKeys = Object.keys(left);
  const rightKeys = Object.keys(right);
  if (leftKeys.length !== rightKeys.length) {
    return false;
  }

  for (const key of leftKeys) {
    if (
      !Object.prototype.hasOwnProperty.call(right, key) ||
      !areJsonLikeEqual(left[key], right[key])
    ) {
      return false;
    }
  }

  return true;
}

function areJsonLikeEqual(left: unknown, right: unknown): boolean {
  if (Object.is(left, right)) {
    return true;
  }

  if (left === null || right === null || typeof left !== 'object' || typeof right !== 'object') {
    return false;
  }

  if (Array.isArray(left) || Array.isArray(right)) {
    if (!Array.isArray(left) || !Array.isArray(right) || left.length !== right.length) {
      return false;
    }

    for (let i = 0; i < left.length; i++) {
      if (!areJsonLikeEqual(left[i], right[i])) {
        return false;
      }
    }

    return true;
  }

  return areJsonLikeRecordsEqual(left as Record<string, unknown>, right as Record<string, unknown>);
}

/**
 * Get process state by session ID.
 */
export function getProcessState(sessionId: string): ProcessState | undefined {
  return $processStates.get()[sessionId];
}

/**
 * Set process state for a session.
 */
export function setProcessState(sessionId: string, state: ProcessState): void {
  $processStates.setKey(sessionId, state);
}

/**
 * Remove process state for a session.
 */
export function removeProcessState(sessionId: string): void {
  const states = { ...$processStates.get() };
  // eslint-disable-next-line @typescript-eslint/no-dynamic-delete
  delete states[sessionId];
  $processStates.set(states);
}

/**
 * Reorder sessions by moving a session from one index to another.
 * Updates _order values for all affected sessions.
 */
export function reorderSessions(fromIndex: number, toIndex: number): void {
  if (fromIndex === toIndex) return;

  const sessionList = $sessionList.get();
  if (fromIndex < 0 || fromIndex >= sessionList.length) return;
  if (toIndex < 0 || toIndex >= sessionList.length) return;

  const reordered = [...sessionList];
  const moved = reordered.splice(fromIndex, 1)[0];
  if (!moved) return;
  reordered.splice(toIndex, 0, moved);

  const sessionsMap: Record<string, Session> = {};
  reordered.forEach((session, i) => {
    const id = session.id;
    if (!id) return;
    sessionsMap[id] = { ...session, _order: i };
  });
  $sessions.set(sessionsMap);
}

// =============================================================================
// Layout Stores
// =============================================================================

/** The current layout tree (null root when showing standalone session) */
export const $layout = atom<DisplayLayout>({ root: null });

/** Focused session within the layout (for keyboard input routing) */
export const $focusedSessionId = atom<string | null>(null);

// =============================================================================
// Main Browser Store
// =============================================================================

/** Whether this browser is the "main" browser that auto-resizes terminals (server-driven) */
export const $isMainBrowser = atom<boolean>(false);

/** Whether the main browser button should be visible (server has seen 2+ unique clients) */
export const $showMainBrowserButton = atom<boolean>(false);

/** Connected browser sessions and their server-observed active MidTerm session */
export const $browserSessions = atom<BrowserSessionStatus[]>([]);
