import { interruptAppServerControlTurn, sendAppServerControlTurn } from '../../api/client';
import type {
  AppServerControlAttachmentReference,
  AppServerControlInterruptRequest,
  AppServerControlTurnRequest,
  AppServerControlTurnStartResponse,
} from '../../api/types';
import { $sessions } from '../../stores';
import { getActiveTab } from '../sessionTabs';
import {
  acceptAppServerControlQuickSettings,
  createAppServerControlTurnRequestWithQuickSettings,
} from './quickSettings';

export const APP_SERVER_CONTROL_TURN_SUBMITTED_EVENT = 'midterm:appServerControl-turn-submitted';
export const APP_SERVER_CONTROL_TURN_ACCEPTED_EVENT = 'midterm:appServerControl-turn-accepted';
export const APP_SERVER_CONTROL_TURN_FAILED_EVENT = 'midterm:appServerControl-turn-failed';

export interface AppServerControlTurnSubmittedEventDetail {
  optimisticId: string;
  sessionId: string;
  request: AppServerControlTurnRequest;
}

export interface AppServerControlTurnAcceptedEventDetail extends AppServerControlTurnSubmittedEventDetail {
  response: AppServerControlTurnStartResponse;
}

export interface AppServerControlTurnFailedEventDetail extends AppServerControlTurnSubmittedEventDetail {
  errorMessage: string;
}

export interface AppServerControlTurnExecutionSummary {
  turnId?: string | null;
  state?: string | null;
}

type AppServerControlTurnLifecycleEventDetail =
  | AppServerControlTurnSubmittedEventDetail
  | AppServerControlTurnAcceptedEventDetail
  | AppServerControlTurnFailedEventDetail;

interface AppServerControlTurnExecutionState {
  currentTurnId: string | null;
  currentTurnRunning: boolean;
  submittingTurn: boolean;
  pendingInterruptAfterSubmit: boolean;
  interruptInFlight: boolean;
}

const appServerControlTurnStates = new Map<string, AppServerControlTurnExecutionState>();

export function isAppServerControlActiveSession(sessionId: string | null | undefined): boolean {
  if (!sessionId) {
    return false;
  }

  const session = $sessions.get()[sessionId];
  return session?.appServerControlOnly === true && getActiveTab(sessionId) === 'agent';
}

export function createAppServerControlTurnRequest(
  text: string,
  attachments: AppServerControlAttachmentReference[] = [],
  sessionId?: string,
): AppServerControlTurnRequest {
  if (sessionId) {
    return createAppServerControlTurnRequestWithQuickSettings(sessionId, text, attachments);
  }

  return {
    text,
    model: null,
    effort: null,
    planMode: 'off',
    permissionMode: 'manual',
    attachments,
  };
}

export async function submitAppServerControlTurn(
  sessionId: string,
  request: AppServerControlTurnRequest,
): Promise<AppServerControlTurnStartResponse> {
  const normalizedRequest = normalizeAppServerControlTurnRequest(request);
  const optimisticId = dispatchSubmittedAppServerControlTurn(sessionId, normalizedRequest);
  const state = getOrCreateAppServerControlTurnState(sessionId);
  state.submittingTurn = true;

  try {
    const response = await sendAppServerControlTurnWithLifecycle(
      sessionId,
      optimisticId,
      normalizedRequest,
    );
    state.currentTurnId = response.turnId ?? state.currentTurnId;
    state.currentTurnRunning = true;
    await maybeInterruptAppServerControlTurnAfterSubmit(sessionId, state);
    return response;
  } catch (error) {
    state.pendingInterruptAfterSubmit = false;
    throw error;
  } finally {
    state.submittingTurn = false;
  }
}

export async function handleAppServerControlEscape(sessionId: string): Promise<boolean> {
  const state = getOrCreateAppServerControlTurnState(sessionId);
  if (state.currentTurnRunning) return requestAppServerControlTurnInterrupt(sessionId, state);
  if (state.submittingTurn) {
    state.pendingInterruptAfterSubmit = true;
    return true;
  }
  return false;
}

export function hasInterruptibleAppServerControlTurnWork(sessionId: string): boolean {
  const state = getOrCreateAppServerControlTurnState(sessionId);
  return state.currentTurnRunning || state.submittingTurn;
}

export function syncAppServerControlTurnExecutionState(
  sessionId: string,
  currentTurn: AppServerControlTurnExecutionSummary | null | undefined,
): void {
  const state = getOrCreateAppServerControlTurnState(sessionId);
  state.currentTurnId = currentTurn?.turnId?.trim() || null;
  state.currentTurnRunning = isRunningAppServerControlTurnState(currentTurn?.state);
  if (!state.currentTurnRunning) state.interruptInFlight = false;
}

export function clearAppServerControlTurnSessionState(sessionId: string): void {
  appServerControlTurnStates.delete(sessionId);
}

function dispatchAppServerControlTurnEvent(
  name: string,
  detail: AppServerControlTurnLifecycleEventDetail,
): void {
  if (typeof window === 'undefined' || typeof window.dispatchEvent !== 'function') {
    return;
  }

  window.dispatchEvent(new CustomEvent(name, { detail }));
}

function createOptimisticTurnId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }

  return `optimistic-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

function normalizeAppServerControlTurnRequest(
  request: AppServerControlTurnRequest,
): AppServerControlTurnRequest {
  return {
    ...(request.text === undefined ? {} : { text: request.text }),
    ...(request.model === undefined ? {} : { model: request.model ?? null }),
    ...(request.effort === undefined ? {} : { effort: request.effort ?? null }),
    ...(request.planMode === undefined ? {} : { planMode: request.planMode ?? 'off' }),
    ...(request.permissionMode === undefined
      ? {}
      : { permissionMode: request.permissionMode ?? 'manual' }),
    ...(request.fastMode === undefined ? {} : { fastMode: request.fastMode ?? 'off' }),
    ...(request.configValues === undefined ? {} : { configValues: { ...request.configValues } }),
    attachments: request.attachments.map((attachment) => ({ ...attachment })),
  };
}

function dispatchSubmittedAppServerControlTurn(
  sessionId: string,
  request: AppServerControlTurnRequest,
): string {
  const optimisticId = createOptimisticTurnId();
  dispatchAppServerControlTurnEvent(APP_SERVER_CONTROL_TURN_SUBMITTED_EVENT, {
    optimisticId,
    sessionId,
    request,
  });
  return optimisticId;
}

async function sendAppServerControlTurnWithLifecycle(
  sessionId: string,
  optimisticId: string,
  request: AppServerControlTurnRequest,
): Promise<AppServerControlTurnStartResponse> {
  try {
    const response = await sendAppServerControlTurn(sessionId, request);
    acceptAppServerControlQuickSettings(sessionId, response.provider, response.quickSettings);
    dispatchAppServerControlTurnEvent(APP_SERVER_CONTROL_TURN_ACCEPTED_EVENT, {
      optimisticId,
      sessionId,
      request,
      response,
    });
    return response;
  } catch (error) {
    dispatchAppServerControlTurnEvent(APP_SERVER_CONTROL_TURN_FAILED_EVENT, {
      optimisticId,
      sessionId,
      request,
      errorMessage: String(error),
    });
    throw error;
  }
}

function getOrCreateAppServerControlTurnState(
  sessionId: string,
): AppServerControlTurnExecutionState {
  const existing = appServerControlTurnStates.get(sessionId);
  if (existing) return existing;
  const created: AppServerControlTurnExecutionState = {
    currentTurnId: null,
    currentTurnRunning: false,
    submittingTurn: false,
    pendingInterruptAfterSubmit: false,
    interruptInFlight: false,
  };
  appServerControlTurnStates.set(sessionId, created);
  return created;
}

async function requestAppServerControlTurnInterrupt(
  sessionId: string,
  state: AppServerControlTurnExecutionState,
): Promise<boolean> {
  if (state.interruptInFlight) {
    return true;
  }

  state.interruptInFlight = true;
  const request: AppServerControlInterruptRequest = state.currentTurnId
    ? { turnId: state.currentTurnId }
    : {};

  try {
    await interruptAppServerControlTurn(sessionId, request);
    return true;
  } catch {
    state.interruptInFlight = false;
    return false;
  }
}

async function maybeInterruptAppServerControlTurnAfterSubmit(
  sessionId: string,
  state: AppServerControlTurnExecutionState,
): Promise<void> {
  if (!state.pendingInterruptAfterSubmit) {
    return;
  }

  state.pendingInterruptAfterSubmit = false;
  await requestAppServerControlTurnInterrupt(sessionId, state);
}

function isRunningAppServerControlTurnState(state: string | null | undefined): boolean {
  const normalized = (state || '').trim().toLowerCase();
  return (
    normalized === 'running' ||
    normalized === 'in_progress' ||
    normalized === 'started' ||
    normalized === 'submitted'
  );
}
