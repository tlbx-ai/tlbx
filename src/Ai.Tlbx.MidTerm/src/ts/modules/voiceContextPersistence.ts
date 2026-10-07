/** Browser-local voice focus and campaign persistence. */
import type { CampaignGoalPhase, CampaignGoalState, FocusContextState } from '../types';

const FOCUS_CONTEXT_STORAGE_KEY = 'midterm.voice.focusContext.v1';
const CAMPAIGN_GOAL_STORAGE_KEY = 'midterm.voice.campaignGoal.v1';
export const CAMPAIGN_GOAL_PHASES = new Set<CampaignGoalPhase>([
  'orient',
  'execute',
  'verify',
  'report',
  'blocked',
  'done',
]);

export function emptyFocusContextState(): FocusContextState {
  return {
    active: false,
    sessionId: null,
    sessionTitle: null,
    sessionExists: null,
    previewName: null,
    previewId: null,
    repoRoot: null,
    reason: null,
    updatedAt: null,
  };
}

export function emptyCampaignGoalState(): CampaignGoalState {
  return {
    active: false,
    objective: null,
    phase: null,
    targetSessionIds: [],
    currentFocusSessionId: null,
    exitCriteria: null,
    nextReport: null,
    createdAt: null,
    updatedAt: null,
    reason: null,
  };
}

function isNullableString(value: unknown): value is string | null {
  return value === null || typeof value === 'string';
}

function isFocusContextState(value: unknown): value is FocusContextState {
  if (!value || typeof value !== 'object') return false;

  const candidate = value as Partial<FocusContextState>;
  return (
    typeof candidate.active === 'boolean' &&
    isNullableString(candidate.sessionId) &&
    isNullableString(candidate.sessionTitle) &&
    (candidate.sessionExists === null || typeof candidate.sessionExists === 'boolean') &&
    isNullableString(candidate.previewName) &&
    isNullableString(candidate.previewId) &&
    isNullableString(candidate.repoRoot) &&
    isNullableString(candidate.reason) &&
    isNullableString(candidate.updatedAt)
  );
}

function isCampaignGoalState(value: unknown): value is CampaignGoalState {
  if (!value || typeof value !== 'object') return false;

  const candidate = value as Partial<CampaignGoalState>;
  const phase = candidate.phase;
  return (
    typeof candidate.active === 'boolean' &&
    isNullableString(candidate.objective) &&
    (phase === null || (typeof phase === 'string' && CAMPAIGN_GOAL_PHASES.has(phase))) &&
    Array.isArray(candidate.targetSessionIds) &&
    candidate.targetSessionIds.every((sessionId) => typeof sessionId === 'string') &&
    isNullableString(candidate.currentFocusSessionId) &&
    isNullableString(candidate.exitCriteria) &&
    isNullableString(candidate.nextReport) &&
    isNullableString(candidate.createdAt) &&
    isNullableString(candidate.updatedAt) &&
    isNullableString(candidate.reason)
  );
}

interface PersistedState<T> {
  state: T;
  persisted: boolean;
}

function loadState<T>(
  key: string,
  empty: () => T,
  isValid: (value: unknown) => value is T,
): PersistedState<T> {
  if (typeof window === 'undefined') return { state: empty(), persisted: false };

  try {
    const raw = window.localStorage.getItem(key);
    if (!raw) return { state: empty(), persisted: true };

    const parsed: unknown = JSON.parse(raw);
    return isValid(parsed)
      ? { state: parsed, persisted: true }
      : { state: empty(), persisted: false };
  } catch {
    return { state: empty(), persisted: false };
  }
}

function persistState(key: string, state: unknown): boolean {
  if (typeof window === 'undefined') return false;

  try {
    window.localStorage.setItem(key, JSON.stringify(state));
    return true;
  } catch {
    return false;
  }
}

export function loadPersistedFocusContextState(): PersistedState<FocusContextState> {
  return loadState(FOCUS_CONTEXT_STORAGE_KEY, emptyFocusContextState, isFocusContextState);
}

export function loadPersistedCampaignGoalState(): PersistedState<CampaignGoalState> {
  return loadState(CAMPAIGN_GOAL_STORAGE_KEY, emptyCampaignGoalState, isCampaignGoalState);
}

export function persistFocusContextState(state: FocusContextState): boolean {
  return persistState(FOCUS_CONTEXT_STORAGE_KEY, state);
}

export function persistCampaignGoalState(state: CampaignGoalState): boolean {
  return persistState(CAMPAIGN_GOAL_STORAGE_KEY, state);
}
