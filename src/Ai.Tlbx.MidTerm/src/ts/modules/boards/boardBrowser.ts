import { boardRequest, getBoardTabId, type BoardItem, type BoardDocument } from './boardApi';
import {
  createBrowserPreviewClient,
  getWebPreviewTarget,
  type WebPreviewSessionInfo,
} from '../web/webApi';
import type { BoardBrowserInstruction } from './boardSocket';
import { t } from '../i18n';
import { wrapConnectWorkspaceUrl } from '../web/connectPreview';

const mounts = new WeakMap<HTMLIFrameElement, { key: string }>();

export function resetBoardBrowser(frame: HTMLIFrameElement): void {
  mounts.delete(frame);
}

export function createBoardBrowserFrame(): HTMLIFrameElement {
  const frame = document.createElement('iframe');
  frame.className = 'board-browser-frame';
  frame.title = t('boards.browser');
  frame.setAttribute('sandbox', 'allow-scripts allow-forms');
  frame.addEventListener('load', () => {
    if (new URL(frame.src, window.location.href).pathname.endsWith('/web-preview-popup.html'))
      showBrowserNotice(frame, null);
  });
  return frame;
}

function showBrowserNotice(frame: HTMLIFrameElement, message: string | null): void {
  const notice = frame.parentElement?.querySelector<HTMLElement>('.board-browser-notice');
  if (!notice) return;
  const text = notice.querySelector('p');
  if (text && message) text.textContent = message;
  notice.hidden = message === null;
  frame.hidden = message !== null;
}

function hasBrowserTarget(item: BoardItem): boolean {
  return !!(item.localDirectory || item.url || item.browserSessionId);
}

/** Reuse the complete tlbx preview surface and its sandboxed command bridge. */
export async function mountBoardBrowser(
  frame: HTMLIFrameElement,
  boardId: string,
  item: BoardItem,
  target?: WebPreviewSessionInfo,
): Promise<void> {
  if (!target && !hasBrowserTarget(item)) {
    if (frame.getAttribute('src') !== 'about:blank') frame.src = 'about:blank';
    resetBoardBrowser(frame);
    showBrowserNotice(frame, t('boards.configureBrowser'));
    return;
  }
  const key = JSON.stringify([
    boardId,
    item.id,
    item.localDirectory,
    item.url,
    item.browserSessionId,
    item.browserPreviewName,
  ]);
  if (!target && mounts.get(frame)?.key === key) return;
  const state = { key };
  mounts.set(frame, state);
  showBrowserNotice(frame, t('boards.loadingBrowser'));
  try {
    const preview =
      target ??
      (await boardRequest<WebPreviewSessionInfo>(
        `/${boardId}/items/${item.id}/browser`,
        'POST',
        {},
      ));
    const client = await createBrowserPreviewClient(
      preview.sessionId,
      preview.previewName,
      getBoardTabId(boardId),
    );
    if (!client) throw new Error('Browser preview could not be registered.');
    if (!frame.isConnected || mounts.get(frame) !== state) return;
    const query = new URLSearchParams({
      session: preview.sessionId,
      preview: preview.previewName,
      routeKey: client.routeKey,
      previewId: client.previewId,
      previewToken: client.previewToken,
      sandbox: '1',
      embedded: '1',
    });
    if (client.origin) query.set('origin', client.origin);
    if (preview.url) query.set('url', preview.url);
    // The outer document is tlbx's trusted browser shell; its content frame stays sandboxed.
    frame.removeAttribute('sandbox');
    frame.src = wrapConnectWorkspaceUrl(`/web-preview-popup.html?${query}`);
    frame.removeAttribute('data-error');
    frame.title = t('boards.browser');
  } catch (error) {
    frame.dataset.error = error instanceof Error ? error.message : String(error);
    frame.title = frame.dataset.error;
    showBrowserNotice(frame, frame.dataset.error);
  }
}

export async function executeBoardBrowserInstruction(
  plane: HTMLElement,
  board: BoardDocument,
  message: BoardBrowserInstruction,
): Promise<void> {
  const item = board.items.find(
    (entry) =>
      entry.kind === 'browser' &&
      (entry.browserSessionId ?? `board-${board.id}`) === message.sessionId &&
      (entry.browserPreviewName ?? entry.id) === message.previewName,
  );
  if (!item) throw new Error('This browser tile is not open in this board.');
  const frame = plane.querySelector<HTMLIFrameElement>(`[data-id="${item.id}"] iframe`);
  if (!frame) throw new Error('This browser tile is not attached.');
  switch (message.command) {
    case 'open':
      await navigateFrame(frame, board, item, message);
      break;
    case 'viewport':
      resizeFrame(message);
      break;
    case 'close':
      frame.src = 'about:blank';
      break;
    default:
      throw new Error('Use the board controls to detach or restore this workspace.');
  }
}

async function navigateFrame(
  frame: HTMLIFrameElement,
  board: BoardDocument,
  item: BoardItem,
  message: BoardBrowserInstruction,
): Promise<void> {
  const target = await getWebPreviewTarget(
    message.sessionId ?? '',
    message.previewName ?? 'default',
  );
  if (
    !target?.active ||
    (message.targetRevision !== undefined && target.targetRevision !== message.targetRevision)
  )
    throw new Error('The browser target changed before this command completed.');
  await mountBoardBrowser(frame, board.id, item, target);
  if (frame.dataset.error) throw new Error(frame.dataset.error);
}

function resizeFrame(message: BoardBrowserInstruction): void {
  const channel = new BroadcastChannel(
    `midterm-web-preview-${message.sessionId ?? ''}-${message.previewName ?? ''}`,
  );
  channel.postMessage({ type: 'viewport', width: message.width ?? 0, height: message.height ?? 0 });
  channel.close();
}
