export type ControlPlaneWorkItemState =
  | 'open'
  | 'active'
  | 'waiting'
  | 'blocked'
  | 'done'
  | 'dismissed';

export type ControlPlaneSessionState = 'working' | 'waiting' | 'needsInput' | 'blocked' | 'done';

export interface ControlPlaneWorkItem {
  id: string;
  kind: string;
  state: ControlPlaneWorkItemState;
  priority: 'low' | 'normal' | 'high' | 'urgent';
  title: string;
  summary: string | null;
  nextAction: string | null;
  project: string | null;
  repositoryPath: string | null;
  sessionId: string | null;
  url: string | null;
  source: string;
  dedupeKey: string | null;
  createdAt: string;
  updatedAt: string;
  revision: number;
}

export interface ControlPlaneSessionStatus {
  sessionId: string;
  state: ControlPlaneSessionState;
  summary: string;
  currentTask: string | null;
  nextAction: string | null;
  project: string | null;
  repositoryPath: string | null;
  source: string;
  updatedAt: string;
  revision: number;
}

export interface ControlPlaneCheckpoint {
  id: string;
  sessionId: string;
  kind: string;
  summary: string;
  details: string | null;
  project: string | null;
  repositoryPath: string | null;
  source: string;
  createdAt: string;
}

export interface ControlPlaneSnapshot {
  workItems: ControlPlaneWorkItem[];
  sessionStatuses: ControlPlaneSessionStatus[];
  checkpoints: ControlPlaneCheckpoint[];
}

export interface ControlPlaneEvent {
  sequence: number;
  type:
    | 'workItemCreated'
    | 'workItemUpdated'
    | 'workItemDeleted'
    | 'sessionStatusPublished'
    | 'sessionStatusCleared'
    | 'checkpointCreated';
  entityId: string;
  sessionId: string | null;
  state: string | null;
  priority: string | null;
  summary: string;
  source: string;
  createdAt: string;
}

export interface ControlPlaneEventListResponse {
  latestSequence: number;
  events: ControlPlaneEvent[];
}

export async function fetchControlPlane(
  machineId?: string | null,
  signal?: AbortSignal,
): Promise<ControlPlaneSnapshot> {
  const url = machineId
    ? `/api/hub/machines/${encodeURIComponent(machineId)}/control-plane`
    : '/api/control-plane';
  const response = await fetch(url, signal ? { signal } : undefined);
  if (!response.ok) {
    throw new Error((await response.text()) || 'Failed to load the control plane.');
  }
  return (await response.json()) as ControlPlaneSnapshot;
}

export async function fetchControlPlaneEvents(
  after: number,
  limit: number,
  machineId?: string | null,
  signal?: AbortSignal,
): Promise<ControlPlaneEventListResponse> {
  const base = machineId
    ? `/api/hub/machines/${encodeURIComponent(machineId)}/control-plane/events`
    : '/api/control-plane/events';
  const query = new URLSearchParams({
    after: Math.max(0, after).toString(),
    limit: Math.max(1, Math.min(500, limit)).toString(),
  });
  const response = await fetch(`${base}?${query.toString()}`, signal ? { signal } : undefined);
  if (!response.ok) {
    throw new Error((await response.text()) || 'Failed to load control-plane events.');
  }
  return (await response.json()) as ControlPlaneEventListResponse;
}
