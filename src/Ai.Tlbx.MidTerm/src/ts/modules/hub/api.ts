import type {
  CreateHistoryRequest,
  CreateSessionRequest,
  RenameSessionRequest,
  SessionInfoDto,
} from '../../api/types';
import type {
  HubMachineState,
  HubMachineUpsertRequest,
  HubPinRequest,
  HubStateResponse,
  HubUpdateRolloutRequest,
  HubUpdateRolloutResponse,
} from './types';

interface CreateHistoryResponse {
  id: string;
}

async function parseJson<T>(response: Response): Promise<T> {
  if (!response.ok) {
    throw new Error((await response.text()) || `Request failed (${response.status})`);
  }

  return (await response.json()) as T;
}

export async function getHubState(): Promise<HubStateResponse> {
  const response = await fetch('/api/hub/state');
  return parseJson<HubStateResponse>(response);
}

export async function refreshHubMachine(machineId: string): Promise<HubMachineState> {
  const response = await fetch(`/api/hub/machines/${encodeURIComponent(machineId)}/refresh`, {
    method: 'POST',
  });
  return parseJson<HubMachineState>(response);
}

export async function createHubMachine(request: HubMachineUpsertRequest): Promise<void> {
  const response = await fetch('/api/hub/machines', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
  if (!response.ok) {
    throw new Error(await response.text());
  }
}

export async function updateHubMachine(
  machineId: string,
  request: HubMachineUpsertRequest,
): Promise<void> {
  const response = await fetch(`/api/hub/machines/${encodeURIComponent(machineId)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
  if (!response.ok) {
    throw new Error(await response.text());
  }
}

export async function deleteHubMachine(machineId: string): Promise<void> {
  const response = await fetch(`/api/hub/machines/${encodeURIComponent(machineId)}`, {
    method: 'DELETE',
  });
  if (!response.ok) {
    throw new Error(await response.text());
  }
}

export async function pinHubMachine(machineId: string, request: HubPinRequest = {}): Promise<void> {
  const response = await fetch(`/api/hub/machines/${encodeURIComponent(machineId)}/pin`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
  if (!response.ok) {
    throw new Error(await response.text());
  }
}

export async function clearHubMachinePin(machineId: string): Promise<void> {
  const response = await fetch(`/api/hub/machines/${encodeURIComponent(machineId)}/pin`, {
    method: 'DELETE',
  });
  if (!response.ok) {
    throw new Error(await response.text());
  }
}

export async function createRemoteSession(
  machineId: string,
  request?: CreateSessionRequest,
): Promise<SessionInfoDto> {
  const response = await fetch(`/api/hub/machines/${encodeURIComponent(machineId)}/sessions`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request ?? {}),
  });
  return parseJson<SessionInfoDto>(response);
}

export async function deleteRemoteSession(machineId: string, sessionId: string): Promise<void> {
  const response = await fetch(
    `/api/hub/machines/${encodeURIComponent(machineId)}/sessions/${encodeURIComponent(sessionId)}`,
    { method: 'DELETE' },
  );
  if (!response.ok) {
    throw new Error(await response.text());
  }
}

export async function renameRemoteSession(
  machineId: string,
  sessionId: string,
  request: RenameSessionRequest,
): Promise<void> {
  const response = await fetch(
    `/api/hub/machines/${encodeURIComponent(machineId)}/sessions/${encodeURIComponent(sessionId)}/name`,
    {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    },
  );
  if (!response.ok) {
    throw new Error(await response.text());
  }
}

export async function createRemoteHistoryEntry(
  machineId: string,
  request: CreateHistoryRequest,
): Promise<CreateHistoryResponse> {
  const response = await fetch(`/api/hub/machines/${encodeURIComponent(machineId)}/history`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
  return parseJson<CreateHistoryResponse>(response);
}

export async function setRemoteSessionBookmark(
  machineId: string,
  sessionId: string,
  bookmarkId: string,
): Promise<void> {
  const response = await fetch(
    `/api/hub/machines/${encodeURIComponent(machineId)}/sessions/${encodeURIComponent(sessionId)}/bookmark`,
    {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ bookmarkId }),
    },
  );
  if (!response.ok) {
    throw new Error(await response.text());
  }
}

export async function applyHubUpdates(
  request: HubUpdateRolloutRequest,
): Promise<HubUpdateRolloutResponse> {
  const response = await fetch('/api/hub/updates/apply', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
  return parseJson<HubUpdateRolloutResponse>(response);
}
