import { getOrCreateClientId, getOrCreateTabId } from '../../utils/cookies';

export interface BoardEntry {
  id: string;
  text: string;
  done: boolean;
  date?: string | null;
  year?: number | null;
  week?: number | null;
  quarter?: number | null;
  sender?: string | null;
  url?: string | null;
}

export interface BoardItem {
  id: string;
  revision: number;
  kind: 'note' | 'todo' | 'calendar' | 'mail' | 'timeline' | 'browser';
  title: string;
  text: string;
  color: string;
  x: number;
  y: number;
  width: number;
  height: number;
  z: number;
  entries: BoardEntry[];
  url?: string | null;
  localDirectory?: string | null;
  browserSessionId?: string | null;
  browserPreviewName?: string | null;
  workingDirectory?: string | null;
  launchCommand?: string | null;
  prompt?: string | null;
  sessionProfile?: string | null;
  appServerControlOnly?: boolean | null;
  launches?: BoardLaunch[];
  calendarMonth?: string | null;
}

export interface BoardLaunch {
  id: string;
  boardId: string;
  itemId: string;
  entryId?: string | null;
  sessionId?: string | null;
  state: 'starting' | 'submitting' | 'submitted' | 'failed' | 'uncertain';
  name: string;
  error?: string | null;
  createdAt: string;
}

export interface BoardDocument {
  id: string;
  title: string;
  revision: number;
  panX: number;
  panY: number;
  zoom: number;
  items: BoardItem[];
  workingDirectory?: string | null;
  launchCommand?: string | null;
  sessionProfile?: string | null;
  appServerControlOnly?: boolean;
}

export class BoardApiError extends Error {
  constructor(
    public readonly status: number,
    detail: string,
  ) {
    super(detail);
  }
}

export function getBoardTabId(boardId: string): string {
  return `${getOrCreateTabId()}/board/${boardId}`;
}

export async function boardRequest<T>(path = '', method = 'GET', body?: unknown): Promise<T> {
  const boardId = /^\/([a-f0-9]{32})(?:\/|$)/.exec(path)?.[1];
  const response = await fetch(`/api/boards${path}`, {
    method,
    headers: {
      'Content-Type': 'application/json',
      'X-MidTerm-Tab-Id': boardId ? getBoardTabId(boardId) : getOrCreateTabId(),
      'X-MidTerm-Client-Id': getOrCreateClientId(),
    },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
  if (!response.ok) throw new BoardApiError(response.status, problemDetail(await response.text()));
  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}

function problemDetail(text: string): string {
  try {
    const value = JSON.parse(text) as unknown;
    if (typeof value === 'string') return value;
    if (value && typeof value === 'object' && 'detail' in value && typeof value.detail === 'string')
      return value.detail;
  } catch {
    /* Plain-text API errors remain readable. */
  }
  return text;
}

export function newBoardItem(kind: BoardItem['kind'], x: number, y: number): BoardItem {
  return {
    id: '',
    revision: 0,
    kind,
    title: '',
    text: '',
    color: kind === 'note' ? 'sand' : 'plain',
    x,
    y,
    width: kind === 'browser' ? 640 : kind === 'mail' ? 420 : 320,
    height: kind === 'browser' || kind === 'calendar' ? 440 : kind === 'mail' ? 360 : 280,
    z: 0,
    entries: [],
  };
}
