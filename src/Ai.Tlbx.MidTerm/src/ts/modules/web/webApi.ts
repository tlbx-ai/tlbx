/**
 * Web Preview API
 *
 * REST wrappers for session-scoped, named web preview contexts.
 */

import { getOrCreateTabId } from '../../utils/cookies';

export interface WebPreviewSessionInfo {
  sessionId: string;
  previewName: string;
  routeKey: string;
  url: string | null;
  active: boolean;
  targetRevision: number;
}

export interface WebPreviewSessionListResponse {
  previews: WebPreviewSessionInfo[];
}

export interface WebPreviewTargetResponse {
  sessionId: string;
  previewName: string;
  routeKey: string;
  url: string | null;
  active: boolean;
  targetRevision: number;
}

export interface BrowserPreviewClientResponse {
  sessionId: string | null;
  previewName: string;
  routeKey: string;
  previewId: string;
  previewToken: string;
  origin?: string;
}

export interface BrowserClientInfo {
  sessionId?: string | null;
  previewName?: string | null;
  previewId?: string | null;
  browserId?: string | null;
  connectedAtUtc: string;
  isMainBrowser: boolean;
  isVisible: boolean;
  hasFocus: boolean;
  isTopLevel: boolean;
}

export interface BrowserStatusResponse {
  connected: boolean;
  controllable: boolean;
  hasTarget: boolean;
  hasUiClient: boolean;
  isScoped: boolean;
  state: string;
  scopeDescription?: string | null;
  statusMessage?: string | null;
  connectedClientCount: number;
  totalConnectedClientCount: number;
  connectedUiClientCount: number;
  targetUrl?: string | null;
  ownerBrowserId?: string | null;
  ownerConnected: boolean;
  defaultClient?: BrowserClientInfo | null;
  clients: BrowserClientInfo[];
}

export interface BrowserCommandResponse {
  success: boolean;
  result?: string | null;
  error?: string | null;
  matchCount?: number;
}

function buildPreviewQuery(sessionId: string, previewName?: string): string {
  const query = new URLSearchParams();
  query.set('sessionId', sessionId);
  if (previewName) {
    query.set('previewName', previewName);
  }
  return query.toString();
}

function getEmbeddedWebPreviewPrefix(): string {
  const match = location.pathname.match(/^\/webpreview\/[^/]+/);
  return match ? match[0] : '';
}

function apiUrl(path: string): string {
  const normalizedPath = path.startsWith('/') ? path : `/${path}`;
  return `${getEmbeddedWebPreviewPrefix()}${normalizedPath}`;
}

/** List all named preview sessions for a terminal session. */
export async function listWebPreviewSessions(
  sessionId: string,
): Promise<WebPreviewSessionInfo[] | null> {
  if (!sessionId) {
    return [];
  }

  try {
    const res = await fetch(apiUrl(`/api/webpreview/previews?${buildPreviewQuery(sessionId)}`));
    if (!res.ok) {
      return null;
    }
    const data = (await res.json()) as WebPreviewSessionListResponse;
    return Array.isArray(data.previews) ? data.previews : [];
  } catch {
    return null;
  }
}

/** Ensure a named preview session exists and return its current metadata. */
export async function ensureWebPreviewSession(
  sessionId: string,
  previewName: string,
): Promise<WebPreviewSessionInfo | null> {
  if (!sessionId) {
    return null;
  }

  try {
    const res = await fetch(apiUrl('/api/webpreview/previews'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sessionId, previewName }),
    });
    if (!res.ok) {
      return null;
    }
    return (await res.json()) as WebPreviewSessionInfo;
  } catch {
    return null;
  }
}

/** Delete a named preview session. */
export async function deleteWebPreviewSession(
  sessionId: string,
  previewName: string,
): Promise<boolean> {
  if (!sessionId) {
    return false;
  }

  try {
    const res = await fetch(
      apiUrl(`/api/webpreview/previews?${buildPreviewQuery(sessionId, previewName)}`),
      {
        method: 'DELETE',
      },
    );
    return res.ok;
  } catch {
    return false;
  }
}

/** Set the reverse proxy target URL for a specific named web preview. */
export async function setWebPreviewTarget(
  sessionId: string,
  previewName: string,
  url: string,
): Promise<WebPreviewTargetResponse | null> {
  try {
    const res = await fetch(apiUrl('/api/webpreview/target'), {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sessionId, previewName, url }),
    });
    if (!res.ok) {
      return null;
    }
    return (await res.json()) as WebPreviewTargetResponse;
  } catch {
    return null;
  }
}

/** Get the current reverse proxy target URL and route key for a named web preview. */
export async function getWebPreviewTarget(
  sessionId: string,
  previewName: string,
): Promise<WebPreviewTargetResponse | null> {
  if (!sessionId) {
    return null;
  }

  try {
    const res = await fetch(
      apiUrl(`/api/webpreview/target?${buildPreviewQuery(sessionId, previewName)}`),
    );
    if (!res.ok) {
      return null;
    }
    return (await res.json()) as WebPreviewTargetResponse;
  } catch {
    return null;
  }
}

/** Clear the reverse proxy target for a named web preview. */
export async function clearWebPreviewTarget(sessionId: string, previewName: string): Promise<void> {
  if (!sessionId) {
    return;
  }

  try {
    await fetch(apiUrl(`/api/webpreview/target?${buildPreviewQuery(sessionId, previewName)}`), {
      method: 'DELETE',
    });
  } catch {
    // ignore
  }
}

/** Clear all cookies in the server-side proxy cookie jar for a named preview. */
export async function clearWebPreviewCookies(
  sessionId: string,
  previewName: string,
): Promise<boolean> {
  if (!sessionId) {
    return false;
  }

  try {
    const res = await fetch(
      apiUrl(`/api/webpreview/cookies/clear?${buildPreviewQuery(sessionId, previewName)}`),
      {
        method: 'POST',
      },
    );
    return res.ok;
  } catch {
    return false;
  }
}

/** Clear session-scoped server-side preview state while preserving the active target. */
export async function clearWebPreviewState(
  sessionId: string,
  previewName: string,
): Promise<WebPreviewTargetResponse | null> {
  if (!sessionId) {
    return null;
  }

  try {
    const res = await fetch(
      apiUrl(`/api/webpreview/state/clear?${buildPreviewQuery(sessionId, previewName)}`),
      {
        method: 'POST',
      },
    );
    if (!res.ok) {
      return null;
    }
    return (await res.json()) as WebPreviewTargetResponse;
  } catch {
    return null;
  }
}

/** Trigger a soft, force, or hard reload for a named web preview. */
export async function reloadWebPreview(
  sessionId: string,
  previewName: string,
  mode: 'soft' | 'force' | 'hard',
): Promise<boolean> {
  if (!sessionId) {
    return false;
  }

  try {
    const res = await fetch(apiUrl('/api/webpreview/reload'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sessionId, previewName, mode }),
    });
    return res.ok;
  } catch {
    return false;
  }
}

/** Register a preview client identity for iframe or popup browser bridge traffic. */
export async function createBrowserPreviewClient(
  sessionId: string,
  previewName: string,
): Promise<BrowserPreviewClientResponse | null> {
  if (!sessionId) {
    return null;
  }

  try {
    const res = await fetch(apiUrl('/api/browser/preview-client'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sessionId, previewName, tabId: getOrCreateTabId() }),
    });
    if (!res.ok) {
      return null;
    }
    return (await res.json()) as BrowserPreviewClientResponse;
  } catch {
    return null;
  }
}

/** Get scoped browser bridge status for the active preview. */
export async function getBrowserPreviewStatus(
  sessionId: string,
  previewName: string,
  previewId?: string,
): Promise<BrowserStatusResponse | null> {
  if (!sessionId) {
    return null;
  }

  const query = new URLSearchParams();
  query.set('sessionId', sessionId);
  if (previewName) {
    query.set('previewName', previewName);
  }
  if (previewId) {
    query.set('previewId', previewId);
  }

  try {
    const res = await fetch(apiUrl(`/api/browser/status?${query.toString()}`));
    if (!res.ok) {
      return null;
    }
    return (await res.json()) as BrowserStatusResponse;
  } catch {
    return null;
  }
}

/** Run a scoped browser command through the MidTerm browser bridge. */
export async function runBrowserCommand(
  command: string,
  sessionId: string,
  previewName: string,
  previewId?: string,
): Promise<BrowserCommandResponse | null> {
  if (!sessionId) {
    return null;
  }

  try {
    const res = await fetch(apiUrl('/api/browser/command'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        command,
        sessionId,
        previewName,
        ...(previewId ? { previewId } : {}),
      }),
    });
    return (await res.json()) as BrowserCommandResponse;
  } catch {
    return null;
  }
}

/** Capture a screenshot through the injected browser bridge and return its data URL. */
export async function captureBrowserScreenshotRaw(
  sessionId: string,
  previewId?: string,
  previewName?: string,
): Promise<string | null> {
  try {
    const res = await fetch(apiUrl('/api/browser/screenshot-raw'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        sessionId,
        ...(previewName ? { previewName } : {}),
        ...(previewId ? { previewId } : {}),
      }),
    });
    if (!res.ok) {
      return null;
    }
    const data = (await res.json()) as {
      success?: boolean;
      result?: string;
    };
    return data.success && typeof data.result === 'string' ? data.result : null;
  } catch {
    return null;
  }
}
