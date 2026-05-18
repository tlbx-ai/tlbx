/**
 * API Client
 *
 * Type-safe API client generated from OpenAPI spec.
 * C# DTOs -> OpenAPI -> TypeScript types -> openapi-fetch client
 *
 * Types are exported from api/types.ts - import types from there.
 * This module provides the client functions only.
 */

import createClient from 'openapi-fetch';
import type { FetchResponse, MaybeOptionalInit } from 'openapi-fetch';
import type { MediaType, PathsWithMethod } from 'openapi-typescript-helpers';
import type { paths } from '../api.generated';
import { AppServerControlHttpError } from './errors';
import type {
  MidTermSettingsPublic,
  MidTermSettingsUpdate,
  CreateSessionRequest,
  SessionInfoDto,
  WorkerBootstrapRequest,
  WorkerBootstrapResponse,
  ProviderResumeCatalogEntryDto,
  SessionPromptRequest,
  SessionBufferTextResponse,
  SessionStateResponse,
  AppServerControlTurnRequest,
  AppServerControlTurnStartResponse,
  AppServerControlInterruptRequest,
  AppServerControlGoalSetRequest,
  AppServerControlCommandAcceptedResponse,
  AppServerControlHistoryDelta,
  AppServerControlRequestDecisionRequest,
  AppServerControlUserInputAnswerRequest,
  AppServerControlHistorySnapshot,
  CreateHistoryRequest,
  HistoryPatchRequest,
  CreateShareLinkRequest,
  CreateShareLinkResponse,
  ActiveShareGrantListResponse,
  ClaimShareRequest,
  ClaimShareResponse,
  ShareBootstrapResponse,
  AgentSessionFeedResponse,
  AgentSessionVibeResponse,
} from './types';
import {
  approveAppServerControlRequestWs,
  attachAppServerControlSession,
  declineAppServerControlRequestWs,
  detachAppServerControlSession,
  getAppServerControlHistoryWindowWs,
  interruptAppServerControlTurnWs,
  openAppServerControlHistorySocket,
  setAppServerControlGoalWs,
  updateAppServerControlHistorySocketWindow,
  resolveAppServerControlUserInputWs,
  submitAppServerControlTurnWs,
} from './appServerControlWebSocket';

const client = createClient<paths>({ baseUrl: '' });

type ClientGetPath = PathsWithMethod<paths, 'get'>;
type ClientPostPath = PathsWithMethod<paths, 'post'>;
type ClientPutPath = PathsWithMethod<paths, 'put'>;
type ClientDeletePath = PathsWithMethod<paths, 'delete'>;
type ClientPatchPath = PathsWithMethod<paths, 'patch'>;

type ClientGetResult<
  Path extends ClientGetPath,
  Init extends MaybeOptionalInit<paths[Path], 'get'> = MaybeOptionalInit<paths[Path], 'get'>,
> = Promise<FetchResponse<paths[Path]['get'], Init, MediaType>>;

type ClientPostResult<
  Path extends ClientPostPath,
  Init extends MaybeOptionalInit<paths[Path], 'post'> = MaybeOptionalInit<paths[Path], 'post'>,
> = Promise<FetchResponse<paths[Path]['post'], Init, MediaType>>;

type ClientPutResult<
  Path extends ClientPutPath,
  Init extends MaybeOptionalInit<paths[Path], 'put'> = MaybeOptionalInit<paths[Path], 'put'>,
> = Promise<FetchResponse<paths[Path]['put'], Init, MediaType>>;

type ClientDeleteResult<
  Path extends ClientDeletePath,
  Init extends MaybeOptionalInit<paths[Path], 'delete'> = MaybeOptionalInit<paths[Path], 'delete'>,
> = Promise<FetchResponse<paths[Path]['delete'], Init, MediaType>>;

type ClientPatchResult<
  Path extends ClientPatchPath,
  Init extends MaybeOptionalInit<paths[Path], 'patch'> = MaybeOptionalInit<paths[Path], 'patch'>,
> = Promise<FetchResponse<paths[Path]['patch'], Init, MediaType>>;

// Re-export all types from api/types.ts for backward compatibility
export * from './types';
export { AppServerControlHttpError } from './errors';

export class ApiProblemError extends Error {
  readonly status: number;
  readonly title: string;
  readonly detail: string;
  readonly errorDetails: string;
  readonly errorStage: string;
  readonly exceptionType: string;
  readonly nativeErrorCode: number | null;

  constructor(options: {
    status: number;
    title?: string;
    detail?: string;
    errorDetails?: string;
    errorStage?: string;
    exceptionType?: string;
    nativeErrorCode?: number | null;
  }) {
    const title = options.title?.trim() || `HTTP ${options.status}`;
    const detail = options.detail?.trim() || '';
    super(detail || title);
    this.name = 'ApiProblemError';
    this.status = options.status;
    this.title = title;
    this.detail = detail;
    this.errorDetails = options.errorDetails?.trim() || '';
    this.errorStage = options.errorStage?.trim() || '';
    this.exceptionType = options.exceptionType?.trim() || '';
    this.nativeErrorCode =
      typeof options.nativeErrorCode === 'number' ? options.nativeErrorCode : null;
  }
}

async function throwHttpError(response: Response, fallback: string): Promise<never> {
  const detail = await response
    .text()
    .then((text) => text.trim())
    .catch(() => '');

  if (detail) {
    throw new AppServerControlHttpError(response.status, detail);
  }

  throw new AppServerControlHttpError(response.status, response.statusText || fallback);
}

async function readResponseBody(response: Response): Promise<string> {
  return response
    .text()
    .then((text) => text.trim())
    .catch(() => '');
}

function resolveApiProblemText(value: unknown, fallback: string, response: Response): string {
  return typeof value === 'string' && value.trim() ? value : response.statusText || fallback;
}

function buildParsedApiProblem(
  response: Response,
  parsed: Record<string, unknown>,
  fallback: string,
): ConstructorParameters<typeof ApiProblemError>[0] {
  const problem: ConstructorParameters<typeof ApiProblemError>[0] = {
    status: response.status,
    title: resolveApiProblemText(parsed.title, fallback, response),
    detail: resolveApiProblemText(parsed.detail, fallback, response),
  };

  if (typeof parsed.errorDetails === 'string') {
    problem.errorDetails = parsed.errorDetails;
  }
  if (typeof parsed.errorStage === 'string') {
    problem.errorStage = parsed.errorStage;
  }
  if (typeof parsed.exceptionType === 'string') {
    problem.exceptionType = parsed.exceptionType;
  }
  if (typeof parsed.nativeErrorCode === 'number') {
    problem.nativeErrorCode = parsed.nativeErrorCode;
  }

  return problem;
}

function tryParseApiProblemBody(
  response: Response,
  body: string,
  fallback: string,
): ConstructorParameters<typeof ApiProblemError>[0] | null {
  if (!body.startsWith('{')) {
    return null;
  }

  try {
    const parsed = JSON.parse(body) as Record<string, unknown>;
    return buildParsedApiProblem(response, parsed, fallback);
  } catch {
    return null;
  }
}

async function throwApiProblem(response: Response, fallback: string): Promise<never> {
  const body = await readResponseBody(response);
  const parsedProblem = tryParseApiProblemBody(response, body, fallback);
  if (parsedProblem) {
    throw new ApiProblemError(parsedProblem);
  }

  throw new ApiProblemError({
    status: response.status,
    title: response.statusText || fallback,
    detail: body || response.statusText || fallback,
  });
}

async function postJsonWithProblem<TResponse>(
  path: string,
  body?: unknown,
  parse?: (text: string) => TResponse,
): Promise<{ data: TResponse | undefined; response: Response }> {
  const response = await fetch(path, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(body ?? {}),
  });

  if (!response.ok) {
    await throwApiProblem(response, 'Request failed.');
  }

  const text = await readResponseBody(response);
  return {
    data: text ? (parse ? parse(text) : (JSON.parse(text) as TResponse)) : undefined,
    response,
  };
}

async function fetchAppServerControlJson<T>(
  path: string,
  fallback: string,
  init?: RequestInit,
): Promise<T> {
  const response = await fetch(path, init);
  if (!response.ok) {
    await throwHttpError(response, fallback);
  }

  const text = await response.text();
  if (!text.trim()) {
    throw new Error(`${fallback} Response body was empty.`);
  }

  return JSON.parse(text) as T;
}

// =============================================================================
// API Functions
// =============================================================================

// --- Auth ---

export async function login(password: string): ClientPostResult<'/api/auth/login'> {
  return client.POST('/api/auth/login', {
    body: { password },
  });
}

export async function logout(): ClientPostResult<'/api/auth/logout'> {
  return client.POST('/api/auth/logout');
}

export async function changePassword(
  currentPassword: string | null,
  newPassword: string,
): ClientPostResult<'/api/auth/change-password'> {
  return client.POST('/api/auth/change-password', {
    body: { currentPassword, newPassword },
  });
}

export async function getAuthStatus(): ClientGetResult<'/api/auth/status'> {
  return client.GET('/api/auth/status');
}

export async function getSecurityStatus(): ClientGetResult<'/api/security/status'> {
  return client.GET('/api/security/status');
}

export async function getApiKeys(): ClientGetResult<'/api/security/api-keys'> {
  return client.GET('/api/security/api-keys');
}

export async function createApiKey(name: string): ClientPostResult<'/api/security/api-keys'> {
  return client.POST('/api/security/api-keys', {
    body: { name },
  });
}

export async function deleteApiKey(id: string): ClientDeleteResult<'/api/security/api-keys/{id}'> {
  return client.DELETE('/api/security/api-keys/{id}', {
    params: { path: { id } },
  });
}

export async function getFirewallRuleStatus(): ClientGetResult<'/api/security/firewall'> {
  return client.GET('/api/security/firewall');
}

export async function addFirewallRule(): ClientPostResult<'/api/security/firewall'> {
  return client.POST('/api/security/firewall');
}

export async function removeFirewallRule(): ClientDeleteResult<'/api/security/firewall'> {
  return client.DELETE('/api/security/firewall');
}

// --- Bootstrap ---

export async function getBootstrap(): ClientGetResult<'/api/bootstrap'> {
  return client.GET('/api/bootstrap');
}

export async function getBootstrapLogin(): ClientGetResult<'/api/bootstrap/login'> {
  return client.GET('/api/bootstrap/login');
}

// --- Sessions ---

export async function getSessions(): ClientGetResult<'/api/sessions'> {
  return client.GET('/api/sessions', { cache: 'no-store' });
}

export async function createSession(
  request?: CreateSessionRequest,
): Promise<{ data: SessionInfoDto | undefined; response: Response }> {
  return postJsonWithProblem(
    '/api/sessions',
    request,
    (text) => JSON.parse(text) as SessionInfoDto,
  );
}

export async function bootstrapWorker(
  request: WorkerBootstrapRequest,
): Promise<{ data: WorkerBootstrapResponse | undefined; response: Response }> {
  return postJsonWithProblem(
    '/api/workers/bootstrap',
    request,
    (text) => JSON.parse(text) as WorkerBootstrapResponse,
  );
}

export async function getProviderResumeCandidates(
  provider: string,
  options?: {
    workingDirectory?: string | null;
    scope?: 'current' | 'all';
  },
): Promise<ProviderResumeCatalogEntryDto[]> {
  const params = new URLSearchParams();
  const workingDirectory = options?.workingDirectory?.trim();
  if (workingDirectory) {
    params.set('workingDirectory', workingDirectory);
  }
  if (options?.scope) {
    params.set('scope', options.scope);
  }

  const suffix = params.size > 0 ? `?${params.toString()}` : '';
  const response = await fetch(
    `/api/providers/${encodeURIComponent(provider)}/resume-candidates${suffix}`,
  );
  if (!response.ok) {
    await throwApiProblem(response, 'Failed to load provider resume candidates.');
  }

  return (await response.json()) as ProviderResumeCatalogEntryDto[];
}

export async function deleteSession(id: string): ClientDeleteResult<'/api/sessions/{id}'> {
  return client.DELETE('/api/sessions/{id}', {
    params: { path: { id } },
  });
}

export async function resizeSession(
  id: string,
  cols: number,
  rows: number,
): ClientPostResult<'/api/sessions/{id}/resize'> {
  return client.POST('/api/sessions/{id}/resize', {
    params: { path: { id } },
    body: { cols, rows },
  });
}

export async function renameSession(
  id: string,
  name: string,
  auto = false,
): ClientPutResult<'/api/sessions/{id}/name'> {
  return client.PUT('/api/sessions/{id}/name', {
    params: { path: { id }, query: { auto } },
    body: { name },
  });
}

export async function setSessionBookmark(
  id: string,
  bookmarkId: string,
): ClientPutResult<'/api/sessions/{id}/bookmark'> {
  return client.PUT('/api/sessions/{id}/bookmark', {
    params: { path: { id } },
    body: { bookmarkId },
  });
}

export async function setSessionNotes(id: string, notes: string | null): Promise<SessionInfoDto> {
  const response = await fetch(`/api/sessions/${encodeURIComponent(id)}/notes`, {
    method: 'PUT',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ notes }),
  });

  if (!response.ok) {
    throw new Error(await response.text());
  }

  return (await response.json()) as SessionInfoDto;
}

export async function setSessionTopic(id: string, topic: string | null): Promise<SessionInfoDto> {
  const response = await fetch(`/api/sessions/${encodeURIComponent(id)}/topic`, {
    method: 'PUT',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ topic }),
  });

  if (!response.ok) {
    throw new Error(await response.text());
  }

  return (await response.json()) as SessionInfoDto;
}

export async function setSessionControl(id: string, agentControlled: boolean): Promise<void> {
  const response = await fetch(`/api/sessions/${encodeURIComponent(id)}/control`, {
    method: 'PUT',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ agentControlled }),
  });

  if (!response.ok) {
    throw new Error(await response.text());
  }
}

export async function getSessionAgentVibe(
  id: string,
  tailLines: number,
  activitySeconds: number,
  bellLimit: number,
): Promise<AgentSessionVibeResponse> {
  const { data } = await client.GET('/api/sessions/{id}/agent', {
    params: {
      path: { id },
      query: { tailLines, activitySeconds, bellLimit },
    },
  });

  return data as AgentSessionVibeResponse;
}

export async function getSessionAgentFeed(
  id: string,
  tailLines: number,
  activitySeconds: number,
  bellLimit: number,
): Promise<AgentSessionFeedResponse> {
  const { data } = await client.GET('/api/sessions/{id}/agent/feed', {
    params: {
      path: { id },
      query: { tailLines, activitySeconds, bellLimit },
    },
  });

  return data as AgentSessionFeedResponse;
}

export async function sendSessionPrompt(id: string, request: SessionPromptRequest): Promise<void> {
  const response = await fetch(`/api/sessions/${encodeURIComponent(id)}/input/prompt`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(request),
  });

  if (!response.ok) {
    throw new Error(await response.text());
  }
}

export async function getSessionState(
  id: string,
  includeBuffer = true,
): Promise<SessionStateResponse> {
  const url = new URL(`/api/sessions/${encodeURIComponent(id)}/state`, window.location.origin);
  url.searchParams.set('includeBuffer', includeBuffer ? 'true' : 'false');

  return fetchAppServerControlJson<SessionStateResponse>(
    url.toString(),
    'Session state fetch failed.',
  );
}

export async function getSessionBufferText(
  id: string,
  includeBase64 = false,
): Promise<SessionBufferTextResponse> {
  const url = new URL(
    `/api/sessions/${encodeURIComponent(id)}/buffer/text`,
    window.location.origin,
  );
  url.searchParams.set('includeBase64', includeBase64 ? 'true' : 'false');

  const response = await fetch(url.toString());
  if (!response.ok) {
    await throwHttpError(response, 'Session buffer text fetch failed.');
  }

  return response.json() as Promise<SessionBufferTextResponse>;
}

export async function getSessionBufferTail(
  id: string,
  lines = 120,
  stripAnsi = true,
): Promise<string> {
  const url = new URL(
    `/api/sessions/${encodeURIComponent(id)}/buffer/tail`,
    window.location.origin,
  );
  url.searchParams.set('lines', String(lines));
  url.searchParams.set('stripAnsi', stripAnsi ? 'true' : 'false');

  const response = await fetch(url.toString());
  if (!response.ok) {
    await throwHttpError(response, 'Session buffer tail fetch failed.');
  }

  return response.text();
}

export async function attachSessionAppServerControl(id: string): Promise<void> {
  await attachAppServerControlSession(id);
}

export async function detachSessionAppServerControl(id: string): Promise<void> {
  await detachAppServerControlSession(id);
}

export async function sendAppServerControlTurn(
  id: string,
  request: AppServerControlTurnRequest,
): Promise<AppServerControlTurnStartResponse> {
  return submitAppServerControlTurnWs(id, request);
}

export async function getAppServerControlHistoryWindow(
  id: string,
  startIndex?: number,
  count?: number,
  windowRevision?: string,
  viewportWidth?: number,
): Promise<AppServerControlHistorySnapshot> {
  return getAppServerControlHistoryWindowWs(id, startIndex, count, windowRevision, viewportWidth);
}

export async function interruptAppServerControlTurn(
  id: string,
  request: AppServerControlInterruptRequest,
): Promise<AppServerControlCommandAcceptedResponse> {
  return interruptAppServerControlTurnWs(id, request);
}

export async function setAppServerControlGoal(
  id: string,
  request: AppServerControlGoalSetRequest,
): Promise<AppServerControlCommandAcceptedResponse> {
  return setAppServerControlGoalWs(id, request);
}

export async function approveAppServerControlRequest(
  id: string,
  requestId: string,
): Promise<AppServerControlCommandAcceptedResponse> {
  return approveAppServerControlRequestWs(id, requestId);
}

export async function declineAppServerControlRequest(
  id: string,
  requestId: string,
  request: AppServerControlRequestDecisionRequest = { decision: 'decline' },
): Promise<AppServerControlCommandAcceptedResponse> {
  return declineAppServerControlRequestWs(id, requestId, request);
}

export async function resolveAppServerControlUserInput(
  id: string,
  requestId: string,
  request: AppServerControlUserInputAnswerRequest,
): Promise<AppServerControlCommandAcceptedResponse> {
  return resolveAppServerControlUserInputWs(id, requestId, request);
}

export interface AppServerControlHistoryStreamCallbacks {
  onPatch(patch: AppServerControlHistoryDelta): void;
  onHistoryWindow?(historyWindow: AppServerControlHistorySnapshot): void;
  onOpen?(): void;
  onError?(error: Event): void;
}

export function openAppServerControlHistoryStream(
  id: string,
  afterSequence: number,
  startIndex: number | undefined,
  count: number | undefined,
  windowRevision: string | undefined,
  callbacks: AppServerControlHistoryStreamCallbacks,
  viewportWidth?: number,
): () => void {
  return openAppServerControlHistorySocket(
    id,
    afterSequence,
    startIndex,
    count,
    windowRevision,
    callbacks,
    viewportWidth,
  );
}

export function updateAppServerControlHistoryStreamWindow(
  id: string,
  startIndex: number | undefined,
  count: number | undefined,
  windowRevision: string | undefined,
  viewportWidth?: number,
): void {
  updateAppServerControlHistorySocketWindow(id, startIndex, count, windowRevision, viewportWidth);
}

// --- Settings ---

export async function getSettings(): ClientGetResult<'/api/settings'> {
  return client.GET('/api/settings');
}

export async function updateSettings(
  settings: MidTermSettingsUpdate,
): ClientPutResult<'/api/settings'> {
  return client.PUT('/api/settings', {
    body: settings as unknown as MidTermSettingsPublic,
  });
}

export interface BackgroundImageInfo {
  hasImage: boolean;
  fileName: string | null;
  revision: number;
}

export async function uploadBackgroundImage(file: File): Promise<BackgroundImageInfo> {
  const formData = new FormData();
  formData.append('file', file);

  const response = await fetch('/api/settings/background-image', {
    method: 'POST',
    body: formData,
  });

  if (!response.ok) {
    await throwApiProblem(response, 'Background image upload failed.');
  }

  return (await response.json()) as BackgroundImageInfo;
}

export async function deleteBackgroundImage(): Promise<BackgroundImageInfo> {
  const response = await fetch('/api/settings/background-image', {
    method: 'DELETE',
  });

  if (!response.ok) {
    await throwApiProblem(response, 'Background image delete failed.');
  }

  return (await response.json()) as BackgroundImageInfo;
}

export async function reloadSettings(): ClientPostResult<'/api/settings/reload'> {
  return client.POST('/api/settings/reload');
}

export async function restartServer(): Promise<Response> {
  return fetch('/api/restart', { method: 'POST' });
}

// --- System ---

export async function getVersion(): ClientGetResult<'/api/version', { parseAs: 'text' }> {
  return client.GET('/api/version', { parseAs: 'text' });
}

export async function getVersionDetails(): ClientGetResult<'/api/version/details'> {
  return client.GET('/api/version/details');
}

export async function getHealth(): ClientGetResult<'/api/health'> {
  return client.GET('/api/health');
}

export async function getSystem(): ClientGetResult<'/api/system'> {
  return client.GET('/api/system');
}

export async function getPaths(): ClientGetResult<'/api/paths'> {
  return client.GET('/api/paths');
}

export async function getShells(): ClientGetResult<'/api/shells'> {
  return client.GET('/api/shells');
}

export async function getUsers(): ClientGetResult<'/api/users'> {
  return client.GET('/api/users');
}

export async function getNetworks(): ClientGetResult<'/api/networks'> {
  return client.GET('/api/networks');
}

// --- Certificates ---

export async function getCertificateInfo(): ClientGetResult<'/api/certificate/info'> {
  return client.GET('/api/certificate/info');
}

export async function getSharePacket(): ClientGetResult<'/api/certificate/share-packet'> {
  return client.GET('/api/certificate/share-packet');
}

export async function createShareLink(
  request: CreateShareLinkRequest,
): Promise<CreateShareLinkResponse> {
  const response = await fetch('/api/share/create', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });

  if (!response.ok) {
    throw new Error(await response.text());
  }

  return (await response.json()) as CreateShareLinkResponse;
}

export async function getActiveShares(limit = 6): Promise<ActiveShareGrantListResponse> {
  const url = new URL('/api/share/active', window.location.origin);
  url.searchParams.set('limit', String(limit));

  const response = await fetch(url.toString());
  if (!response.ok) {
    throw new Error(await response.text());
  }

  return (await response.json()) as ActiveShareGrantListResponse;
}

export async function revokeShare(grantId: string): Promise<void> {
  const response = await fetch(`/api/share/${encodeURIComponent(grantId)}`, {
    method: 'DELETE',
  });

  if (!response.ok) {
    throw new Error(await response.text());
  }
}

export async function claimShareLink(request: ClaimShareRequest): Promise<ClaimShareResponse> {
  const response = await fetch('/api/share/claim', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });

  if (!response.ok) {
    throw new Error(await response.text());
  }

  return (await response.json()) as ClaimShareResponse;
}

export async function getShareBootstrap(): Promise<ShareBootstrapResponse> {
  const response = await fetch('/api/share/bootstrap');
  if (!response.ok) {
    throw new Error(await response.text());
  }

  return (await response.json()) as ShareBootstrapResponse;
}

export async function regenerateCertificate(): Promise<Response> {
  return fetch('/api/certificate/regenerate', { method: 'POST' });
}

// --- Updates ---

export async function checkUpdate(): ClientGetResult<'/api/update/check'> {
  return client.GET('/api/update/check');
}

export async function applyUpdate(source?: string): ClientPostResult<'/api/update/apply'> {
  return client.POST('/api/update/apply', {
    params: { query: source ? { source } : {} },
  });
}

export async function getUpdateResult(clear = false): ClientGetResult<'/api/update/result'> {
  return client.GET('/api/update/result', {
    params: { query: { clear } },
  });
}

export async function deleteUpdateResult(): ClientDeleteResult<'/api/update/result'> {
  return client.DELETE('/api/update/result');
}

export async function getUpdateLog(): ClientGetResult<'/api/update/log', { parseAs: 'text' }> {
  return client.GET('/api/update/log', { parseAs: 'text' });
}

// --- History ---

export async function getHistory(): ClientGetResult<'/api/history'> {
  return client.GET('/api/history');
}

export async function createHistoryEntry(
  entry: CreateHistoryRequest,
): ClientPostResult<'/api/history'> {
  return client.POST('/api/history', {
    body: entry,
  });
}

export async function patchHistoryEntry(
  id: string,
  patch: HistoryPatchRequest,
): ClientPatchResult<'/api/history/{id}'> {
  return client.PATCH('/api/history/{id}', {
    params: { path: { id } },
    body: patch,
  });
}

export async function toggleHistoryStar(id: string): ClientPutResult<'/api/history/{id}/star'> {
  return client.PUT('/api/history/{id}/star', {
    params: { path: { id } },
  });
}

export async function deleteHistoryEntry(id: string): ClientDeleteResult<'/api/history/{id}'> {
  return client.DELETE('/api/history/{id}', {
    params: { path: { id } },
  });
}

// --- Files ---

export async function registerFilePaths(
  sessionId: string,
  paths: string[],
): ClientPostResult<'/api/files/register'> {
  return client.POST('/api/files/register', {
    body: { sessionId, paths },
  });
}

export async function checkFilePaths(
  paths: string[],
  sessionId?: string,
): ClientPostResult<'/api/files/check'> {
  return client.POST('/api/files/check', {
    params: { query: sessionId ? { sessionId } : {} },
    body: { paths },
  });
}

export async function listDirectory(
  path: string,
  sessionId?: string,
): ClientGetResult<'/api/files/list'> {
  return client.GET('/api/files/list', {
    params: { query: sessionId ? { path, sessionId } : { path } },
  });
}

export async function resolveFilePath(
  sessionId: string,
  path: string,
  deep = false,
): ClientGetResult<'/api/files/resolve'> {
  return client.GET('/api/files/resolve', {
    params: { query: { sessionId, path, deep } },
  });
}
