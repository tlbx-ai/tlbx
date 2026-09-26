import type { BrowserPreviewClientResponse } from './webApi';
import type { PreviewBridgeMessage, PreviewCookieRequestMessage } from './webPanelTypes';
import { getSessionPreview, type WebPreviewState } from './webSessionState';

export interface PreviewMessageTarget {
  sessionId: string;
  previewName: string;
  routeKey: string;
  preview: WebPreviewState;
}

/** Authenticate the sender against its own frame and current preview, never the selected tab. */
export function resolvePreviewMessageTarget(
  frame: Pick<HTMLIFrameElement, 'contentWindow' | 'dataset'> | null,
  source: MessageEventSource | null,
  message: PreviewBridgeMessage,
): PreviewMessageTarget | null {
  if (!frame || !source || frame.contentWindow !== source) return null;
  const key = frame.dataset.previewFrameKey;
  const separator = key?.indexOf('::') ?? -1;
  if (!key || separator < 1) return null;
  const sessionId = key.slice(0, separator);
  const previewName = key.slice(separator + 2);
  const preview = getSessionPreview(sessionId, previewName);
  const client = preview?.dockedClient;
  if (
    !preview ||
    !client?.routeKey ||
    !client.previewToken ||
    !matchesPreviewMessage(message, client, sessionId, previewName, preview.targetRevision)
  )
    return null;
  return { sessionId, previewName, routeKey: client.routeKey, preview };
}

function matchesPreviewMessage(
  message: PreviewBridgeMessage,
  client: BrowserPreviewClientResponse,
  sessionId: string,
  previewName: string,
  targetRevision: number,
): boolean {
  return (
    message.previewId === client.previewId &&
    message.previewToken === client.previewToken &&
    (message.sessionId === undefined || message.sessionId === sessionId) &&
    (message.previewName === undefined || message.previewName === previewName) &&
    message.targetRevision === targetRevision
  );
}

/** Use the authenticated sender route for both cookie writes and reads. */
export async function fetchPreviewCookie(
  request: PreviewCookieRequestMessage,
  target: PreviewMessageTarget,
  origin: string,
): Promise<Response> {
  const url = new URL(`/webpreview/${encodeURIComponent(target.routeKey)}/_cookies`, origin);
  const upstreamUrl = request.upstreamUrl ?? target.preview.navigationUrl ?? target.preview.url;
  if (upstreamUrl) url.searchParams.set('u', upstreamUrl);
  return fetch(
    url.toString(),
    request.action === 'set'
      ? {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ raw: request.raw ?? '' }),
        }
      : { method: 'GET' },
  );
}
