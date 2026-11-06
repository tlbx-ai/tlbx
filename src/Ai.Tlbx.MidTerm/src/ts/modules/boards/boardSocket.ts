import { getOrCreateClientId, initializeTabIdentity } from '../../utils/cookies';
import { ReconnectController } from '../../utils/reconnect';
import { handleAuthenticatedWebSocketClose } from '../auth/sessionLifetime';
import { getBoardTabId } from './boardApi';

export interface BoardBrowserInstruction {
  type: 'browser-ui';
  command: string;
  requestId?: string;
  sessionId?: string;
  previewName?: string;
  targetRevision?: number;
  url?: string;
  width?: number;
  height?: number;
}

export function connectBoardSocket(
  boardId: string,
  changed: () => void,
  command: (instruction: BoardBrowserInstruction) => Promise<void>,
): { ready: Promise<void>; dispose: () => void } {
  const reconnect = new ReconnectController();
  let socket: WebSocket | null = null;
  let disposed = false;
  let resolveReady: () => void = () => {};
  let rejectReady: (error: Error) => void = () => {};
  const ready = new Promise<void>((resolve, reject) => {
    resolveReady = resolve;
    rejectReady = reject;
  });
  const timeout = setTimeout(() => {
    rejectReady(new Error('Board browser connection did not become ready.'));
  }, 10000);
  let operations = Promise.resolve();
  const connect = (): void => {
    if (disposed) return;
    const url = new URL('/ws/boards', location.href);
    url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    url.search = new URLSearchParams({ boardId, tabId: getBoardTabId(boardId) }).toString();
    const ws = new WebSocket(url);
    socket = ws;
    ws.onmessage = (event) => {
      const data = JSON.parse(String(event.data)) as { type: string };
      if (data.type === 'board-ready') {
        clearTimeout(timeout);
        reconnect.reset();
        resolveReady();
        changed();
      } else if (data.type === 'board-changed') changed();
      else if (data.type === 'browser-ui') {
        operations = operations
          .then(() => command(data as BoardBrowserInstruction))
          .catch(() => {});
      }
    };
    ws.onclose = (event) => {
      if (!disposed && !handleAuthenticatedWebSocketClose(event)) reconnect.schedule(connect);
    };
  };
  getOrCreateClientId();
  void initializeTabIdentity()
    .then(connect)
    .catch((error: unknown) => {
      rejectReady(error instanceof Error ? error : new Error(String(error)));
    });
  return {
    ready,
    dispose: () => {
      disposed = true;
      clearTimeout(timeout);
      reconnect.cancel();
      socket?.close();
      resolveReady();
    },
  };
}
