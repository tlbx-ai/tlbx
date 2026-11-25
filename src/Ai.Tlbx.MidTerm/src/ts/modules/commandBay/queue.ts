import type {
  AppServerControlTurnRequest,
  AppServerControlTurnStartResponse,
} from '../../api/types';
import { waitForAntigravityQuickSettings } from '../appServerControl/quickSettings';
import type { NormalizedManagerButton } from '../managerBar/workflow';

async function readQueueError(response: Response, fallback: string): Promise<Error> {
  const detail = await response
    .text()
    .then((text) => text.trim())
    .catch(() => '');

  return new Error(detail || response.statusText || fallback);
}

export async function enqueueCommandBayAction(
  sessionId: string,
  action: NormalizedManagerButton,
): Promise<void> {
  const response = await fetch('/api/command-bay/queue', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sessionId, action }),
  });

  if (!response.ok) {
    throw await readQueueError(response, 'Failed to enqueue automation.');
  }
}

export async function sendCommandBayPrompt(
  sessionId: string,
  turn: AppServerControlTurnRequest,
): Promise<AppServerControlTurnStartResponse | null> {
  await waitForAntigravityQuickSettings(sessionId);
  const response = await fetch('/api/command-bay/queue', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sessionId, turn }),
  });

  if (!response.ok) {
    throw await readQueueError(response, 'Failed to send prompt.');
  }
  const body = await response.text();
  return body.trim() ? (JSON.parse(body) as AppServerControlTurnStartResponse) : null;
}

export async function removeCommandBayQueueEntry(queueId: string): Promise<void> {
  const response = await fetch(`/api/command-bay/queue/${encodeURIComponent(queueId)}`, {
    method: 'DELETE',
  });

  if (!response.ok && response.status !== 404) {
    throw await readQueueError(response, 'Failed to remove queued item.');
  }
}
