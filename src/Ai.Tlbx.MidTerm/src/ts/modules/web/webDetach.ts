/**
 * Web Preview Detach
 *
 * Handles detaching named web previews to chromeless popup windows
 * and docking them back into the main panel.
 */

import {
  $activeSessionId,
  $webPreviewDetached,
  $webPreviewUrl,
  $webPreviewViewport,
} from '../../stores';
import { loadPreview } from './webPanel';
import { createLogger } from '../logging';
import {
  getActivePreviewName,
  getSessionPreview,
  setSessionDockedClient,
  setSessionMode,
  setSessionSelectedPreviewName,
  setSessionUrl,
} from './webSessionState';
import { createBrowserPreviewClient } from './webApi';
import { isDevMode } from '../sidebar/voiceSection';
import { shouldSandboxPreviewFrame } from './previewSandbox';
import { hideWebPreviewDockForDetach, openWebPreviewDock } from './webDock';

const log = createLogger('webDetach');

const popups = new Map<string, Window>();
const channels = new Map<string, BroadcastChannel>();
const POPUP_FEATURES = 'popup,width=1280,height=900,menubar=no,toolbar=no,location=no,status=no';

export interface DetachPreviewOptions {
  mobileMode?: boolean;
  suppressFocus?: boolean;
}

function popupKey(sessionId: string, previewName: string): string {
  return `${sessionId}::${previewName}`;
}

function channelName(sessionId: string, previewName: string): string {
  return `midterm-web-preview-${sessionId}-${previewName}`;
}

function getPopupKeysForSession(sessionId: string): string[] {
  const prefix = `${sessionId}::`;
  return Array.from(popups.keys()).filter((key) => key.startsWith(prefix));
}

/** Initialize the detach system: wire up detach and dock-back buttons. */
export function initDetach(): void {
  document.getElementById('web-preview-detach')?.addEventListener('click', () => {
    void detachPreview();
  });
  document.getElementById('web-preview-detach-mobile')?.addEventListener('click', () => {
    void detachPreview(undefined, undefined, { mobileMode: true });
  });
  document.getElementById('web-preview-dock-back')?.addEventListener('click', () => {
    dockBack();
  });
}

function handleMessage(
  e: MessageEvent<{ type: string; sessionId?: string; previewName?: string; url?: string }>,
): void {
  const { type, sessionId, previewName, url } = e.data;
  const targetPreviewName = previewName ?? 'default';

  if (type === 'navigation' && sessionId && typeof url === 'string') {
    setSessionUrl(sessionId, targetPreviewName, url);
    if (sessionId === $activeSessionId.get() && targetPreviewName === getActivePreviewName()) {
      $webPreviewUrl.set(url);
    }
    return;
  }

  if (type === 'dock-back' || type === 'popup-closed') {
    dockBack(sessionId ?? undefined, targetPreviewName);
  }
}

function getPreviewUrlForDetach(sessionId: string, previewName: string): string | null {
  const preview = getSessionPreview(sessionId, previewName);
  return (
    preview?.url ??
    (sessionId === $activeSessionId.get() && previewName === getActivePreviewName()
      ? $webPreviewUrl.get()
      : null)
  );
}

function getActiveDetachedViewport(
  sessionId: string,
  previewName: string,
): { width: number; height: number } | null {
  return sessionId === $activeSessionId.get() && previewName === getActivePreviewName()
    ? $webPreviewViewport.get()
    : null;
}

function buildDetachedPopupUrl(args: {
  sessionId: string;
  previewName: string;
  routeKey: string;
  previewId: string;
  previewToken: string;
  origin: string | null;
  url: string | null;
  viewport: { width: number; height: number } | null;
  mobileMode: boolean;
}): string {
  return (
    '/web-preview-popup.html' +
    `?session=${encodeURIComponent(args.sessionId)}` +
    `&preview=${encodeURIComponent(args.previewName)}` +
    `&routeKey=${encodeURIComponent(args.routeKey)}` +
    `&previewId=${encodeURIComponent(args.previewId)}` +
    `&previewToken=${encodeURIComponent(args.previewToken)}` +
    (args.origin ? `&origin=${encodeURIComponent(args.origin)}` : '') +
    (args.viewport
      ? `&viewportWidth=${encodeURIComponent(String(args.viewport.width))}` +
        `&viewportHeight=${encodeURIComponent(String(args.viewport.height))}`
      : '') +
    (args.mobileMode ? '&mobile=1' : '') +
    (shouldSandboxPreviewFrame(args.url, isDevMode()) ? '&sandbox=1' : '') +
    (args.url ? `&url=${encodeURIComponent(args.url)}` : '')
  );
}

function syncDetachedStateToActiveDock(sessionId: string, previewName: string): void {
  if (sessionId !== $activeSessionId.get() || previewName !== getActivePreviewName()) {
    return;
  }

  $webPreviewDetached.set(true);
  hideWebPreviewDockForDetach();
}

function activateExistingPopup(
  key: string,
  popup: Window,
  mobileMode: boolean,
  suppressFocus: boolean,
): void {
  channels.get(key)?.postMessage({
    type: 'mobile-mode',
    enabled: mobileMode,
  });

  if (suppressFocus) {
    return;
  }

  popup.focus();
}

function openDetachedBootstrapPopup(sessionId: string, previewName: string): Window | null {
  const popup = window.open(
    'about:blank',
    `midterm-web-preview-${sessionId}-${previewName}`,
    POPUP_FEATURES,
  );
  if (!popup) {
    return null;
  }

  try {
    popup.document.title = 'Opening browser — tlbx';
    popup.document.body.textContent = 'Opening preview...';
  } catch {
    // Ignore cross-origin or popup bootstrap access failures.
  }

  return popup;
}

/** Open a named web preview in a chromeless popup window and hide the dock panel. */
export async function detachPreview(
  sessionId?: string,
  previewName?: string,
  options?: DetachPreviewOptions,
): Promise<void> {
  const targetSessionId = sessionId ?? $activeSessionId.get();
  if (!targetSessionId) {
    return;
  }

  const targetPreviewName = setSessionSelectedPreviewName(targetSessionId, previewName);
  const key = popupKey(targetSessionId, targetPreviewName);
  const mobileMode = options?.mobileMode === true;
  const existing = popups.get(key);
  if (existing && !existing.closed) {
    activateExistingPopup(key, existing, mobileMode, options?.suppressFocus === true);
    return;
  }

  const popup = openDetachedBootstrapPopup(targetSessionId, targetPreviewName);
  if (!popup) {
    return;
  }

  const url = getPreviewUrlForDetach(targetSessionId, targetPreviewName);

  const previewClient = await createBrowserPreviewClient(targetSessionId, targetPreviewName);
  if (!previewClient) {
    popup.close();
    log.warn(
      () => `Failed to create detached browser client for ${targetSessionId}/${targetPreviewName}`,
    );
    return;
  }

  setSessionDockedClient(targetSessionId, targetPreviewName, previewClient);
  const popupUrl = buildDetachedPopupUrl({
    sessionId: targetSessionId,
    previewName: targetPreviewName,
    routeKey: previewClient.routeKey,
    previewId: previewClient.previewId,
    previewToken: previewClient.previewToken,
    origin: previewClient.origin ?? null,
    url,
    viewport: getActiveDetachedViewport(targetSessionId, targetPreviewName),
    mobileMode,
  });

  try {
    popup.location.replace(popupUrl);
  } catch {
    popup.close();
    return;
  }

  closePopupForPreview(targetSessionId, targetPreviewName);
  popups.set(key, popup);

  const ch = new BroadcastChannel(channelName(targetSessionId, targetPreviewName));
  ch.onmessage = handleMessage;
  channels.set(key, ch);

  setSessionMode(targetSessionId, targetPreviewName, 'detached');
  syncDetachedStateToActiveDock(targetSessionId, targetPreviewName);

  if (options?.suppressFocus) {
    try {
      window.focus();
    } catch {
      // Ignore focus hand-off failures from the host browser.
    }
  }

  log.info(() => `Web preview detached to popup for ${targetSessionId}/${targetPreviewName}`);
}

/** Close a detached popup and restore the named web preview into the dock panel. */
export function dockBack(sessionId?: string, previewName?: string): void {
  const targetSessionId = sessionId ?? $activeSessionId.get();
  if (!targetSessionId) {
    return;
  }

  const targetPreviewName =
    previewName ??
    (targetSessionId === $activeSessionId.get() ? getActivePreviewName() : undefined) ??
    'default';

  closePopupForPreview(targetSessionId, targetPreviewName);
  setSessionMode(targetSessionId, targetPreviewName, 'docked');

  if (targetSessionId === $activeSessionId.get() && targetPreviewName === getActivePreviewName()) {
    $webPreviewDetached.set(false);
    openWebPreviewDock();
    void loadPreview();
    log.info(() => `Web preview docked back for ${targetSessionId}/${targetPreviewName}`);
  }
}

/** Close all detached popup windows and release their channels. */
export function cleanupDetach(): void {
  for (const key of Array.from(popups.keys())) {
    const [sessionId, previewName] = key.split('::', 2);
    if (sessionId && previewName) {
      closePopupForPreview(sessionId, previewName);
    }
  }
}

/** Close all detached popups owned by a terminal session. */
export function closeDetachedIfOwnedBy(sessionId: string | null): void {
  if (!sessionId) {
    return;
  }

  for (const key of getPopupKeysForSession(sessionId)) {
    const previewName = key.slice(`${sessionId}::`.length);
    closePopupForPreview(sessionId, previewName);
  }
}

/** Check whether a detached popup is open for a session or a specific named preview. */
export function isDetachedOpenForSession(
  sessionId: string | null,
  previewName?: string | null,
): boolean {
  if (!sessionId) {
    return false;
  }

  if (previewName) {
    const popup = popups.get(popupKey(sessionId, previewName));
    return !!popup && !popup.closed;
  }

  return getPopupKeysForSession(sessionId).some((key) => {
    const popup = popups.get(key);
    return !!popup && !popup.closed;
  });
}

export function closeDetachedPreview(sessionId: string, previewName: string): void {
  closePopupForPreview(sessionId, previewName);
  if (sessionId === $activeSessionId.get() && previewName === getActivePreviewName()) {
    $webPreviewDetached.set(false);
  }
}

/** Apply a viewport override to an already-detached popup preview. */
export function setDetachedPreviewViewport(
  sessionId: string,
  previewName: string,
  width: number,
  height: number,
): boolean {
  const key = popupKey(sessionId, previewName);
  const popup = popups.get(key);
  const channel = channels.get(key);
  if (!popup || popup.closed || !channel) {
    return false;
  }

  channel.postMessage({
    type: 'viewport',
    width,
    height,
  });

  try {
    popup.focus();
  } catch {
    // Ignore popup focus failures from the host browser.
  }

  return true;
}

function closePopupForPreview(sessionId: string, previewName: string): void {
  const key = popupKey(sessionId, previewName);
  const popup = popups.get(key);
  if (popup && !popup.closed) {
    popup.close();
  }
  popups.delete(key);

  const channel = channels.get(key);
  if (channel) {
    channel.close();
    channels.delete(key);
  }
}
