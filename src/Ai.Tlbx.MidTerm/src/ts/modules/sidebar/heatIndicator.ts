/**
 * Heat Indicator Module
 *
 * Renders per-session thermal activity strips in the sidebar. Heat is sourced
 * from mux output events and server session snapshots. Mux replay suppression
 * happens before recordBytes is called, so replay traffic cannot re-arm heat.
 */

import { $sessionList } from '../../stores';

const DRAW_THRESHOLD = 0.003;
const CANVAS_CSS_H = 36;
const RISE_TRANSITION_MS = 220;
const FALL_SLOWDOWN_FACTOR = 1.4;
// Decay targets:
// - 1.0 -> ~0.25 in 42s so idle sessions still keep a visible hierarchy
// - effectively gone after roughly 3 minutes
const FALL_TIME_CONSTANT_MS = (30_000 * FALL_SLOWDOWN_FACTOR) / Math.log(4);

// Color stops: [heat_value, r, g, b]
const GRADIENT: [number, number, number, number][] = [
  [0.0, 10, 14, 26],
  [0.18, 18, 54, 102],
  [0.4, 92, 198, 255],
  [0.58, 170, 232, 255],
  [0.66, 220, 78, 104],
  [0.84, 224, 44, 52],
  [1.0, 220, 28, 28],
];

const COLOR_LUT_SIZE = 101;
const colorLUT: [number, number, number][] = new Array<[number, number, number]>(COLOR_LUT_SIZE);

interface SessionHeat {
  element: HTMLElement | null;
  heat: number;
  activityHeat: number;
  lastActivityAtMs: number | null;
  lastServerActivityAtMs: number | null;
  transitionFromHeat: number;
  transitionToHeat: number;
  transitionStartedAtMs: number;
  transitionDurationMs: number;
  fallTimerId: number | null;
}

const sessions = new Map<string, SessionHeat>();
let unsubscribeSessionList: (() => void) | null = null;

function clampHeat(heat: number): number {
  if (!Number.isFinite(heat)) {
    return 0;
  }

  return Math.max(0, Math.min(1, heat));
}

function lerpColorRaw(t: number): [number, number, number] {
  const stops = GRADIENT;
  const first = stops[0];
  const last = stops[stops.length - 1];
  if (!first || !last) return [10, 14, 26];
  if (t <= first[0]) return [first[1], first[2], first[3]];
  if (t >= last[0]) return [last[1], last[2], last[3]];

  for (let i = 0; i < stops.length - 1; i += 1) {
    const a = stops[i];
    const b = stops[i + 1];
    if (a && b && t >= a[0] && t <= b[0]) {
      const f = (t - a[0]) / (b[0] - a[0]);
      return [
        Math.round(a[1] + (b[1] - a[1]) * f),
        Math.round(a[2] + (b[2] - a[2]) * f),
        Math.round(a[3] + (b[3] - a[3]) * f),
      ];
    }
  }

  return [10, 14, 26];
}

function buildColorLUT(): void {
  for (let i = 0; i < COLOR_LUT_SIZE; i += 1) {
    colorLUT[i] = lerpColorRaw(i / (COLOR_LUT_SIZE - 1));
  }
}

function lerpColor(t: number): [number, number, number] {
  const index = Math.min(COLOR_LUT_SIZE - 1, Math.max(0, Math.round(t * (COLOR_LUT_SIZE - 1))));
  return colorLUT[index] ?? [10, 14, 26];
}

function getNowMs(): number {
  return Date.now();
}

function parseActivityTimestampMs(
  timestamp: string | number | Date | null | undefined,
): number | null {
  if (timestamp instanceof Date) {
    const dateMs = timestamp.getTime();
    return Number.isFinite(dateMs) ? dateMs : null;
  }

  if (typeof timestamp === 'number') {
    return Number.isFinite(timestamp) ? timestamp : null;
  }

  if (typeof timestamp === 'string' && timestamp.trim().length > 0) {
    const parsedMs = Date.parse(timestamp);
    return Number.isFinite(parsedMs) ? parsedMs : null;
  }

  return null;
}

function getTargetHeatAt(state: SessionHeat, nowMs: number): number {
  if (state.lastActivityAtMs === null) {
    return state.heat;
  }

  const elapsedMs = Math.max(0, nowMs - state.lastActivityAtMs);
  return clampHeat(state.activityHeat * Math.exp(-elapsedMs / FALL_TIME_CONSTANT_MS));
}

function getDisplayedHeatAt(state: SessionHeat, nowMs: number): number {
  if (state.transitionDurationMs <= 0) {
    return state.transitionToHeat;
  }

  const elapsedMs = Math.max(0, nowMs - state.transitionStartedAtMs);
  if (elapsedMs >= state.transitionDurationMs) {
    return state.transitionToHeat;
  }

  const progress = elapsedMs / state.transitionDurationMs;
  return clampHeat(
    state.transitionFromHeat + (state.transitionToHeat - state.transitionFromHeat) * progress,
  );
}

function computeTransitionDurationMs(fromHeat: number, toHeat: number): number {
  if (Math.abs(toHeat - fromHeat) < 0.0001) {
    return 0;
  }

  if (toHeat > fromHeat) {
    return RISE_TRANSITION_MS;
  }

  const startingHeat = Math.max(fromHeat, DRAW_THRESHOLD);
  return Math.ceil(FALL_TIME_CONSTANT_MS * Math.log(startingHeat / DRAW_THRESHOLD));
}

function applyHeatStyles(element: HTMLElement, heat: number, durationMs: number): void {
  const visible = heat >= DRAW_THRESHOLD;
  const [r, g, b] = lerpColor(heat);
  const alpha = visible ? Math.min(1, heat * 2) : 0;
  const edgeAlpha = alpha * 0.15;
  const visibleScale = visible ? Math.max(4 / CANVAS_CSS_H, Math.sqrt(heat)) : 0;
  const gradient = `linear-gradient(180deg, rgba(${r}, ${g}, ${b}, ${edgeAlpha.toFixed(3)}), rgba(${r}, ${g}, ${b}, ${alpha.toFixed(3)}) 35%, rgba(${r}, ${g}, ${b}, ${alpha.toFixed(3)}) 65%, rgba(${r}, ${g}, ${b}, ${edgeAlpha.toFixed(3)}))`;

  element.style.setProperty('--session-heat-transition-ms', `${Math.round(durationMs)}ms`);
  element.style.setProperty(
    '--session-heat-transition-easing',
    durationMs > RISE_TRANSITION_MS ? 'cubic-bezier(0.16, 1, 0.3, 1)' : 'linear',
  );
  element.style.setProperty('--session-heat-gradient', gradient);
  element.style.setProperty('--session-heat-opacity', alpha.toFixed(3));
  element.style.setProperty('--session-heat-scale', visibleScale.toFixed(4));
}

function setDisplayedHeatTarget(
  state: SessionHeat,
  nextHeat: number,
  nowMs: number,
  immediate: boolean = false,
): void {
  const currentHeat = immediate ? nextHeat : getDisplayedHeatAt(state, nowMs);
  const durationMs = immediate ? 0 : computeTransitionDurationMs(currentHeat, nextHeat);

  state.transitionFromHeat = currentHeat;
  state.transitionToHeat = nextHeat;
  state.transitionStartedAtMs = nowMs;
  state.transitionDurationMs = durationMs;

  if (state.element) {
    applyHeatStyles(state.element, nextHeat, durationMs);
  }
}

function getOrCreateSessionHeat(sessionId: string): SessionHeat {
  let state = sessions.get(sessionId);
  if (!state) {
    state = {
      element: null,
      heat: 0,
      activityHeat: 0,
      lastActivityAtMs: null,
      lastServerActivityAtMs: null,
      transitionFromHeat: 0,
      transitionToHeat: 0,
      transitionStartedAtMs: getNowMs(),
      transitionDurationMs: 0,
      fallTimerId: null,
    };
    sessions.set(sessionId, state);
  }

  return state;
}

function syncSessionHeatVisual(sessionId: string, nowMs: number, immediate: boolean = false): void {
  const state = getOrCreateSessionHeat(sessionId);
  setDisplayedHeatTarget(state, getTargetHeatAt(state, nowMs), nowMs, immediate);
}

function clearFallTimer(state: SessionHeat): void {
  if (state.fallTimerId !== null) {
    window.clearTimeout(state.fallTimerId);
    state.fallTimerId = null;
  }
}

function startFall(sessionId: string): void {
  const state = sessions.get(sessionId);
  if (!state || document.hidden) {
    return;
  }

  state.fallTimerId = null;
  const nowMs = getNowMs();
  setDisplayedHeatTarget(state, 0, nowMs);
}

function scheduleFall(sessionId: string, state: SessionHeat): void {
  clearFallTimer(state);
  if (document.hidden) {
    return;
  }

  state.fallTimerId = window.setTimeout(() => {
    startFall(sessionId);
  }, RISE_TRANSITION_MS);
}

function applyHeat(
  sessionId: string,
  heat: number,
  lastActivityAt?: string | number | Date | null,
): void {
  const state = getOrCreateSessionHeat(sessionId);
  const nextHeat = clampHeat(heat);
  const nowMs = getNowMs();
  const parsedActivityAtMs = parseActivityTimestampMs(lastActivityAt);
  const candidateActivityAtMs =
    parsedActivityAtMs === null ? null : Math.min(parsedActivityAtMs, nowMs);
  let effectiveHeat = nextHeat;

  if (nextHeat > 0) {
    const resolvedActivityAtMs = candidateActivityAtMs ?? nowMs;
    const isFreshActivity =
      state.lastServerActivityAtMs === null || resolvedActivityAtMs >= state.lastServerActivityAtMs;

    if (isFreshActivity) {
      state.lastActivityAtMs = resolvedActivityAtMs;
      state.activityHeat = nextHeat;
      state.lastServerActivityAtMs = resolvedActivityAtMs;
    } else {
      // Ignore stale/cached hot snapshots so reloads and resumes cannot re-arm heat.
      effectiveHeat = 0;
    }
  } else {
    state.lastActivityAtMs = null;
    state.activityHeat = 0;
  }

  state.heat = effectiveHeat;
  syncSessionHeatVisual(sessionId, nowMs, document.hidden || !state.element);
  if (effectiveHeat > 0) {
    scheduleFall(sessionId, state);
  } else {
    clearFallTimer(state);
  }
}

function syncHeatFromSessionList(): void {
  const activeIds = new Set<string>();
  for (const session of $sessionList.get()) {
    activeIds.add(session.id);
    applyHeat(
      session.id,
      session.supervisor?.currentHeat ?? 0,
      session.supervisor?.lastOutputAt ?? null,
    );
  }

  for (const sessionId of sessions.keys()) {
    if (!activeIds.has(sessionId)) {
      applyHeat(sessionId, 0);
    }
  }
}

buildColorLUT();

export function registerHeatCanvas(sessionId: string, element: HTMLElement): void {
  const state = getOrCreateSessionHeat(sessionId);
  state.element = element;
  applyHeatStyles(element, getDisplayedHeatAt(state, getNowMs()), 0);
}

export function unregisterHeatCanvas(sessionId: string): void {
  const state = sessions.get(sessionId);
  if (!state) return;

  state.element = null;
}

export function pruneHeatSessions(sessionIds: Iterable<string>): void {
  const validIds = new Set(sessionIds);
  for (const sessionId of sessions.keys()) {
    if (!validIds.has(sessionId)) {
      clearFallTimer(getOrCreateSessionHeat(sessionId));
      sessions.delete(sessionId);
    }
  }
}

export function setSessionHeat(sessionId: string, heat: number): void {
  applyHeat(sessionId, heat);
}

export function recordBytes(sessionId: string, bytes: number): void {
  if (bytes <= 0) return;
  applyHeat(sessionId, 1, Date.now());
}

export function getSessionHeat(sessionId: string): number {
  return sessions.get(sessionId)?.heat ?? 0;
}

export function getDisplayedSessionHeat(sessionId: string): number {
  const state = sessions.get(sessionId);
  if (!state) {
    return 0;
  }

  return getDisplayedHeatAt(state, getNowMs());
}

export function suppressAllHeat(_durationMs: number): void {
  // No-op: server-side heat is not driven by browser-side replay or refresh traffic.
}

function resumeHeatRendering(): void {
  if (document.hidden) {
    return;
  }

  const nowMs = getNowMs();
  sessions.forEach((state, sessionId) => {
    syncSessionHeatVisual(sessionId, nowMs, true);
    if (getTargetHeatAt(state, nowMs) >= DRAW_THRESHOLD) {
      scheduleFall(sessionId, state);
    }
  });
}

function handleVisibilityChange(): void {
  const nowMs = getNowMs();
  sessions.forEach((state, sessionId) => {
    syncSessionHeatVisual(sessionId, nowMs, true);
    if (document.hidden) {
      clearFallTimer(state);
    }
  });

  if (!document.hidden) {
    resumeHeatRendering();
  }
}

function handleFocus(): void {
  resumeHeatRendering();
}

export function initHeatIndicator(): void {
  if (unsubscribeSessionList !== null) {
    return;
  }

  syncHeatFromSessionList();
  unsubscribeSessionList = $sessionList.subscribe(() => {
    syncHeatFromSessionList();
  });

  document.addEventListener('visibilitychange', handleVisibilityChange);

  window.addEventListener('focus', handleFocus);
  window.addEventListener('pageshow', handleFocus);
}

export function destroyHeatIndicator(): void {
  sessions.forEach(clearFallTimer);
  unsubscribeSessionList?.();
  unsubscribeSessionList = null;

  sessions.clear();
}
