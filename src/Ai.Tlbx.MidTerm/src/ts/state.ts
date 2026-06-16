/**
 * Application State
 *
 * Ephemeral state that doesn't need reactivity - WebSocket instances,
 * terminal Maps, DOM cache, etc. Reactive state lives in stores/index.ts.
 */

import type { TerminalState, DOMElements } from './types';

// =============================================================================
// WebSocket State
// =============================================================================

/** State WebSocket connection */
export let stateWs: WebSocket | null = null;

/** Mux WebSocket connection */
export let muxWs: WebSocket | null = null;

/** Server's mux protocol version (received in init frame) */
export let serverProtocolVersion: number | null = null;

/** Suppress bell notifications during initial buffer replay after reconnect */
export let bellNotificationsSuppressed = true;

// =============================================================================
// WebSocket Traffic Metrics
// =============================================================================

/** Accumulated TX bytes since last sample */
export let wsTxAccum = 0;

/** Accumulated RX bytes since last sample */
export let wsRxAccum = 0;

/** EMA-smoothed TX rate (bytes/sec) */
export let wsTxRateEma = 0;

/** EMA-smoothed RX rate (bytes/sec) */
export let wsRxRateEma = 0;

type WsTrafficListener = () => void;
const wsTrafficListeners = new Set<WsTrafficListener>();

// =============================================================================
// Terminal State
// =============================================================================

/** Per-session terminal state */
export const sessionTerminals = new Map<string, TerminalState>();

/** Maximum simultaneous WebGL contexts (browser typically limits to 6-8) */
export const MAX_WEBGL_CONTEXTS = 6;

/** Track which terminals have active WebGL contexts */
export const terminalsWithWebgl = new Set<string>();

/** Sessions created in this browser session (use WebSocket buffering) */
export const newlyCreatedSessions = new Set<string>();

/** Hidden session IDs for command overlay terminals (excluded from state channel cleanup) */
export const hiddenSessionIds = new Set<string>();

/** Pending sessions being created (for optimistic UI) */
export const pendingSessions = new Set<string>();

/** Buffer WebSocket output frames for terminals not yet opened */
export const pendingOutputFrames = new Map<string, Uint8Array[]>();

/** Sessions that overflowed pending frames and need full resync when opened */
export const sessionsNeedingResync = new Set<string>();

/** Active bell notifications per session (for deduplication + auto-close) */
export const activeNotifications = new Map<string, Notification>();

/** Font loading promise */
export let fontsReadyPromise: Promise<void> | null = null;

/** Suppress next layout auto-fit (restore from storage — scale only, don't resize) */
export let suppressLayoutAutoFit = false;

// =============================================================================
// DOM Element Cache
// =============================================================================

/** Cached DOM elements */
export const dom: DOMElements = {
  sessionList: null,
  sessionFilterBar: null,
  sessionFilterInput: null,
  sessionFilterClear: null,
  terminalsArea: null,
  emptyState: null,
  mobileTitle: null,
  topbarActions: null,
  app: null,
  sidebarOverlay: null,
  settingsView: null,
  settingsBtn: null,
  titleBarCustom: null,
  titleBarTerminal: null,
  titleBarSeparator: null,
};

// =============================================================================
// State Setters
// =============================================================================

export function setStateWs(ws: WebSocket | null): void {
  stateWs = ws;
}

export function setMuxWs(ws: WebSocket | null): void {
  muxWs = ws;
}

export function setServerProtocolVersion(version: number | null): void {
  serverProtocolVersion = version;
}

export function setFontsReadyPromise(promise: Promise<void>): void {
  fontsReadyPromise = promise;
}

export function setBellNotificationsSuppressed(suppressed: boolean): void {
  bellNotificationsSuppressed = suppressed;
}

export function setSuppressLayoutAutoFit(suppress: boolean): void {
  suppressLayoutAutoFit = suppress;
}

// =============================================================================
// Traffic Metrics Setters
// =============================================================================

export function addWsTxBytes(bytes: number): void {
  wsTxAccum += bytes;
  notifyWsTrafficListeners();
}

export function addWsRxBytes(bytes: number): void {
  wsRxAccum += bytes;
  notifyWsTrafficListeners();
}

export function resetWsAccum(): { tx: number; rx: number } {
  const result = { tx: wsTxAccum, rx: wsRxAccum };
  wsTxAccum = 0;
  wsRxAccum = 0;
  return result;
}

export function setWsRateEma(tx: number, rx: number): void {
  wsTxRateEma = tx;
  wsRxRateEma = rx;
}

export function onWsTraffic(listener: WsTrafficListener): () => void {
  wsTrafficListeners.add(listener);
  return () => wsTrafficListeners.delete(listener);
}

function notifyWsTrafficListeners(): void {
  wsTrafficListeners.forEach((listener) => {
    listener();
  });
}

// =============================================================================
// DOM Element Cache Initialization
// =============================================================================

/**
 * Cache DOM elements for quick access
 */
export function cacheDOMElements(): void {
  dom.sessionList = document.getElementById('session-list');
  dom.sessionFilterBar = document.getElementById('session-filter-bar');
  dom.sessionFilterInput = document.getElementById(
    'session-filter-input',
  ) as HTMLInputElement | null;
  dom.sessionFilterClear = document.getElementById(
    'session-filter-clear',
  ) as HTMLButtonElement | null;
  dom.terminalsArea = document.querySelector('.terminals-area');
  dom.emptyState = document.getElementById('empty-state');
  dom.mobileTitle = document.getElementById('mobile-title');
  dom.topbarActions = document.getElementById('topbar-actions');
  dom.app = document.getElementById('app');
  dom.sidebarOverlay = document.getElementById('sidebar-overlay');
  dom.settingsView = document.getElementById('settings-view');
  dom.settingsBtn = document.getElementById('btn-settings');
  dom.titleBarCustom = document.getElementById('title-bar-custom');
  dom.titleBarTerminal = document.getElementById('title-bar-terminal');
  dom.titleBarSeparator = document.getElementById('title-bar-separator');
}
