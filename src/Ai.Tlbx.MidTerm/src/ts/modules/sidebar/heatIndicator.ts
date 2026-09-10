/** Text activity comes only from the server. CSS owns the finite cooldown;
 * transport bytes, replay and application busy state never rearm it. */
import { $sessionList } from '../../stores';

const COOL_MS = 10_000;
interface HeatState {
  stamp: string | null;
  activityAt: number | null;
  element: HTMLElement | null;
  cycle: boolean;
}
const sessions = new Map<string, HeatState>();
let unsubscribe: (() => void) | null = null;
function stateFor(id: string): HeatState {
  let state = sessions.get(id);
  if (!state) {
    state = { stamp: null, activityAt: null, element: null, cycle: false };
    sessions.set(id, state);
  }
  return state;
}
function ageOf(state: HeatState): number {
  return state.activityAt === null ? COOL_MS : Math.max(0, Date.now() - state.activityAt);
}
function render(state: HeatState): void {
  if (!state.element) return;
  const age = ageOf(state);
  const active = age < COOL_MS && !document.hidden;
  state.cycle = !state.cycle;
  const suffix = state.cycle ? 'a' : 'b';
  state.element.style.setProperty('--heat-age', `${-age}ms`);
  state.element.style.setProperty('--heat-red', active ? `heat-red-${suffix}` : 'none');
  state.element.style.setProperty('--heat-blue', active ? `heat-blue-${suffix}` : 'none');
}
/** The timestamp orders events; server-provided age avoids client/server clock skew. */
export function recordTextActivity(id: string, stamp: string | null, ageMs: number | null): void {
  if (!stamp || ageMs === null || !Number.isFinite(ageMs)) return;
  const state = stateFor(id);
  if (state.stamp && stamp < state.stamp) return;
  const activityAt = Date.now() - Math.max(0, ageMs);
  if (stamp === state.stamp) {
    // A fresh snapshot can correct an event buffered while the browser was frozen.
    // Never make the same output younger; tolerate ordinary delivery jitter.
    if (state.activityAt !== null && activityAt < state.activityAt - 250) {
      state.activityAt = activityAt;
      render(state);
    }
    return;
  }
  state.stamp = stamp;
  state.activityAt = activityAt;
  render(state);
}
function syncSnapshots(): void {
  const list = $sessionList.get();
  for (const session of list) {
    recordTextActivity(
      session.id,
      session.supervisor?.lastTextOutputAt ?? null,
      session.supervisor?.textActivityAgeMs ?? null,
    );
  }
  pruneHeatSessions(list.map((session) => session.id));
}
export function registerHeatCanvas(id: string, element: HTMLElement): void {
  const state = stateFor(id);
  if (state.element === element) return;
  state.element = element;
  render(state);
}
export function unregisterHeatCanvas(id: string): void {
  const state = sessions.get(id);
  if (state) state.element = null;
}
export function pruneHeatSessions(ids: Iterable<string>): void {
  const valid = new Set(ids);
  for (const id of sessions.keys()) if (!valid.has(id)) sessions.delete(id);
}
export function getDisplayedSessionHeat(id: string): number {
  const state = sessions.get(id);
  if (!state) return 0;
  const age = ageOf(state);
  if (age >= COOL_MS) return 0;
  return age <= 3000 ? 1 - (0.6 * age) / 3000 : (0.4 * (COOL_MS - age)) / 7000;
}
export function getSessionHeat(id: string): number {
  const state = sessions.get(id);
  return state && ageOf(state) < 1000 ? 1 : 0;
}
function resume(): void {
  sessions.forEach(render);
}
export function initHeatIndicator(): void {
  if (unsubscribe) return;
  syncSnapshots();
  unsubscribe = $sessionList.subscribe(syncSnapshots);
  document.addEventListener('visibilitychange', resume);
  window.addEventListener('pageshow', resume);
}
export function destroyHeatIndicator(): void {
  unsubscribe?.();
  unsubscribe = null;
  document.removeEventListener('visibilitychange', resume);
  window.removeEventListener('pageshow', resume);
  sessions.clear();
}
