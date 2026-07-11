export type InputHistoryKind = 'prompt' | 'textPaste' | 'imagePaste' | 'fileUpload';

export interface InputHistoryEntry {
  id: string;
  sessionId: string;
  sessionName: string | null;
  workingDirectory: string | null;
  kind: InputHistoryKind;
  source: string;
  surface: 'terminal' | 'agentControl';
  createdAt: string;
  text: string | null;
  path: string | null;
  displayName: string | null;
  mimeType: string | null;
  sizeBytes: number | null;
  bracketedPaste: boolean;
  isFilePath: boolean;
  submit: boolean;
}

interface InputHistoryListResponse {
  totalCount: number;
  entries: InputHistoryEntry[];
}

export async function fetchInputHistory(
  args: {
    sessionId?: string | null;
    kind?: InputHistoryKind | null;
    limit?: number;
  } = {},
): Promise<InputHistoryListResponse> {
  const url = new URL('/api/input-history', window.location.origin);
  url.searchParams.set('limit', Math.max(1, Math.min(500, args.limit ?? 100)).toString());
  if (args.sessionId) {
    url.searchParams.set('sessionId', args.sessionId);
  }
  if (args.kind) {
    url.searchParams.set('kind', args.kind);
  }

  const response = await fetch(url);
  if (!response.ok) {
    throw new Error((await response.text()) || 'Failed to load input history.');
  }

  return (await response.json()) as InputHistoryListResponse;
}

export async function replayInputHistory(
  entryId: string,
  targetSessionId?: string | null,
): Promise<void> {
  const response = await fetch(`/api/input-history/${encodeURIComponent(entryId)}/replay`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(targetSessionId ? { targetSessionId } : {}),
  });
  if (!response.ok) {
    throw new Error((await response.text()) || 'Failed to replay input history.');
  }
}

export async function deleteInputHistory(entryId: string): Promise<void> {
  const response = await fetch(`/api/input-history/${encodeURIComponent(entryId)}`, {
    method: 'DELETE',
  });
  if (!response.ok && response.status !== 404) {
    throw new Error((await response.text()) || 'Failed to delete input history.');
  }
}
