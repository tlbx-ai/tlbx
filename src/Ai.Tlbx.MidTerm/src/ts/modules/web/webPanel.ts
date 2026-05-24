/**
 * Web Preview Panel
 *
 * Manages the URL bar, named preview tabs, and iframe content in the dock panel.
 */

import { $webPreviewUrl, $activeSessionId } from '../../stores';
import {
  clearWebPreviewState,
  getBrowserPreviewStatus,
  runBrowserCommand,
  captureBrowserScreenshotRaw,
  clearWebPreviewCookies,
  createBrowserPreviewClient,
  reloadWebPreview,
  setWebPreviewTarget,
  type BrowserPreviewClientResponse,
} from './webApi';
import { pasteToTerminal } from '../terminal';
import { sendInput } from '../comms/muxChannel';
import { getForegroundInfo } from '../process';
import { createLogger } from '../logging';
import { getAgentGuidanceFile } from '../midtermGuidance';
import { isDevMode } from '../sidebar/voiceSection';
import {
  buildPreviewLoadToken,
  PREVIEW_LOAD_TOKEN_ATTRIBUTE,
  PREVIEW_LOAD_TOKEN_DATASET_KEY,
  shouldReloadPreviewFrame,
} from './previewLoadToken';
import {
  buildProxyUrl,
  sanitizePreviewDisplayUrl,
  stripInternalPreviewQueryParams,
} from './previewProxyUrl';
import { buildPreviewTabLabel } from './webPreviewTabLabel';
import {
  getActiveDockedClient,
  getActivePreview,
  getActivePreviewName,
  getActiveUrl,
  getSessionDockedClient,
  getSessionPreview,
  listSessionPreviews,
  setActiveMode,
  setActiveUrl,
  setSessionDockedClient,
  upsertSessionPreview,
} from './webSessionState';
import { shouldSandboxPreviewFrame } from './previewSandbox';
import { buildBrowserPreviewStatusIndicatorState } from './webPreviewStatus';

interface UploadResponse {
  path?: string;
}

interface PreviewBridgeMessage {
  previewId?: string;
  previewToken?: string;
  sessionId?: string;
  previewName?: string;
}

interface PreviewNavigationMessage extends PreviewBridgeMessage {
  type: 'mt-navigation';
  url: string;
  targetOrigin?: string;
  upstreamUrl?: string;
}

interface PreviewCookieRequestMessage extends PreviewBridgeMessage {
  type: 'mt-cookie-request';
  requestId: string;
  action: 'get' | 'set';
  raw?: string;
  upstreamUrl?: string;
}

interface PreviewCookieResponseMessage extends PreviewBridgeMessage {
  type: 'mt-cookie-response';
  requestId: string;
  header?: string;
  error?: string;
}

const SANDBOX_BASE_FLAGS = [
  'allow-scripts',
  'allow-forms',
  'allow-popups',
  'allow-modals',
  'allow-downloads',
];

const log = createLogger('webPanel');
const PREVIEW_CONTEXT_COOKIE_NAME = 'mt-preview-ctx';
let urlInput: HTMLInputElement | null = null;
let iframeHost: HTMLElement | null = null;
let previewTabs: HTMLElement | null = null;
let statusIndicator: HTMLElement | null = null;
let actionMessage: HTMLElement | null = null;
let screenshotButton: HTMLButtonElement | null = null;
let loadedUrl: string | null = null;
let previewTabSelectHandler: ((previewName: string) => void) | null = null;
let previewTabCloseHandler: ((previewName: string) => void) | null = null;
let activeFrameKey: string | null = null;
const previewFrames = new Map<string, HTMLIFrameElement>();
const STATUS_REFRESH_INTERVAL_MS = 4000;
const PREVIEW_VISIBILITY_REFRESH_DELAYS_MS = [0, 50, 200, 500] as const;
const PREVIEW_TAB_CHANGED_EVENT = 'midterm:web-preview-active-tab-changed';
let statusRefreshTimer: number | null = null;
let screenshotInFlight = false;
type PreviewReloadMode = 'soft' | 'force' | 'hard';

const FRAME_ALLOW_ATTR = `
  camera *;
  microphone *;
  geolocation *;
  fullscreen *;
  autoplay *;
  clipboard-read *;
  clipboard-write *;
  display-capture *;
`;

/** Get the URL currently loaded in the iframe. */
export function getLoadedUrl(): string | null {
  return loadedUrl;
}

function createForceReloadToken(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }

  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

/** Register a callback for preview tab selection. */
export function setPreviewTabSelectHandler(handler: (previewName: string) => void): void {
  previewTabSelectHandler = handler;
}

export function setPreviewTabCloseHandler(handler: (previewName: string) => void): void {
  previewTabCloseHandler = handler;
}

/** Render the active session's named preview tabs. */
export function renderPreviewTabs(): void {
  if (!previewTabs) {
    return;
  }

  const sessionId = $activeSessionId.get();
  const selectedPreviewName = getActivePreviewName();
  previewTabs.replaceChildren();

  if (!sessionId) {
    return;
  }

  for (const preview of listSessionPreviews(sessionId)) {
    const tab = document.createElement('div');
    tab.className = 'web-preview-tab-shell';
    tab.dataset.previewName = preview.previewName;
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'web-preview-tab';
    if (preview.previewName === selectedPreviewName) {
      tab.classList.add('active');
    }
    if (preview.mode === 'detached') {
      tab.classList.add('detached');
    }
    if (!preview.url) {
      tab.classList.add('empty');
    }
    const label = buildPreviewTabLabel(preview.url);
    button.textContent = label;
    button.title = preview.url?.trim() || label;
    button.setAttribute('aria-label', `Preview tab ${label}`);
    button.addEventListener('click', () => {
      previewTabSelectHandler?.(preview.previewName);
    });
    tab.appendChild(button);

    const closeButton = document.createElement('button');
    closeButton.type = 'button';
    closeButton.className = 'web-preview-tab-close';
    closeButton.textContent = '×';
    closeButton.title = `Close ${label}`;
    closeButton.setAttribute('aria-label', `Close preview tab ${label}`);
    closeButton.addEventListener('click', (event) => {
      event.preventDefault();
      event.stopPropagation();
      previewTabCloseHandler?.(preview.previewName);
    });
    tab.appendChild(closeButton);

    previewTabs.appendChild(tab);
  }

  updateScreenshotButtonState();
}

/** Initialize the web preview panel. */
export function initWebPanel(): void {
  urlInput = document.getElementById('web-preview-url-input') as HTMLInputElement | null;
  iframeHost = document.getElementById('web-preview-iframe-host');
  previewTabs = document.getElementById('web-preview-tabs');
  statusIndicator = document.getElementById('web-preview-status-indicator');
  actionMessage = document.getElementById('web-preview-action-message');
  screenshotButton = document.getElementById('web-preview-screenshot') as HTMLButtonElement | null;

  const goBtn = document.getElementById('web-preview-go');
  const refreshBtn = document.getElementById('web-preview-refresh');

  applyIframeSandbox();
  renderPreviewTabs();

  goBtn?.addEventListener('click', () => {
    void handleGo();
  });
  urlInput?.addEventListener('keydown', (e: KeyboardEvent) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      void handleGo();
    }
  });
  refreshBtn?.addEventListener('click', (e: MouseEvent) => {
    const hard = e.shiftKey || e.ctrlKey || e.altKey;
    void handleRefresh(hard ? 'hard' : 'force');
  });
  screenshotButton?.addEventListener('click', (event: MouseEvent) => {
    void handleScreenshot(event.ctrlKey);
  });
  document.getElementById('web-preview-clear-cookies')?.addEventListener('click', () => {
    closeWebPreviewOverflowMenu();
    void handleClearCookies();
  });
  document.getElementById('web-preview-clear-state')?.addEventListener('click', () => {
    closeWebPreviewOverflowMenu();
    void handleClearState();
  });
  initWebPreviewOverflowMenu();
  document.getElementById('web-preview-agent-hint')?.addEventListener('click', handleAgentHint);
  document.addEventListener('visibilitychange', () => {
    void refreshBrowserPreviewStatus();
  });
  window.addEventListener('focus', () => {
    void refreshBrowserPreviewStatus();
  });
  window.addEventListener('blur', () => {
    void refreshBrowserPreviewStatus();
  });
  if (statusRefreshTimer === null) {
    statusRefreshTimer = window.setInterval(() => {
      void refreshBrowserPreviewStatus();
    }, STATUS_REFRESH_INTERVAL_MS);
  }

  window.addEventListener('message', (e: MessageEvent<unknown>) => {
    if (!findPreviewIframeByWindow(e.source)) {
      return;
    }

    const data = e.data as { type?: string } | null;
    if (!data || typeof data.type !== 'string') {
      return;
    }

    if (data.type === 'mt-navigation') {
      const nav = e.data as PreviewNavigationMessage;
      if (!isActivePreviewMessage(nav)) {
        return;
      }
      updateUrlBarFromIframe(
        nav.url,
        nav.upstreamUrl,
        typeof nav.targetOrigin === 'string' ? nav.targetOrigin : undefined,
      );
      return;
    }

    if (data.type === 'mt-cookie-request') {
      const request = e.data as PreviewCookieRequestMessage;
      if (!isActivePreviewMessage(request)) {
        return;
      }
      void handleCookieBridgeRequest(e, request);
    }
  });
}

/** Restore the active preview URL into the URL bar. */
export function restoreLastUrl(): void {
  const saved = getActiveUrl();
  if (!urlInput) {
    return;
  }
  urlInput.value = saved ?? '';
  updateScreenshotButtonState();
}

function initWebPreviewOverflowMenu(): void {
  const trigger = document.getElementById('web-preview-more') as HTMLButtonElement | null;
  const menu = document.getElementById('web-preview-overflow-menu');
  if (!trigger || !menu) {
    return;
  }

  trigger.addEventListener('click', (event) => {
    event.preventDefault();
    event.stopPropagation();
    const open = menu.hidden;
    menu.hidden = !open;
    trigger.setAttribute('aria-expanded', String(open));
  });

  menu.addEventListener('click', (event) => {
    event.stopPropagation();
  });

  document.addEventListener('click', closeWebPreviewOverflowMenu);
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') {
      closeWebPreviewOverflowMenu();
    }
  });
}

function closeWebPreviewOverflowMenu(): void {
  const trigger = document.getElementById('web-preview-more') as HTMLButtonElement | null;
  const menu = document.getElementById('web-preview-overflow-menu');
  if (!trigger || !menu || menu.hidden) {
    return;
  }

  menu.hidden = true;
  trigger.setAttribute('aria-expanded', 'false');
}

function normalizeUrl(raw: string): string {
  if (!raw.includes('://')) {
    const isLocal =
      raw.startsWith('localhost') || raw.startsWith('127.0.0.1') || raw.startsWith('[::1]');
    return `${isLocal ? 'http://' : 'https://'}${raw}`;
  }
  return raw;
}

function getProxyPrefix(routeKey: string): string {
  return `/webpreview/${encodeURIComponent(routeKey)}`;
}

function getCookieBridgePath(routeKey: string): string {
  return `${getProxyPrefix(routeKey)}/_cookies`;
}

function setPreviewContextCookie(previewClient: BrowserPreviewClientResponse): void {
  const routeKey = previewClient.routeKey.trim();
  if (!routeKey || !previewClient.previewId || !previewClient.previewToken) {
    return;
  }

  const payload = encodeURIComponent(
    JSON.stringify({
      sessionId: previewClient.sessionId ?? '',
      previewName: previewClient.previewName,
      routeKey: previewClient.routeKey,
      previewId: previewClient.previewId,
      previewToken: previewClient.previewToken,
    }),
  );

  document.cookie =
    `${PREVIEW_CONTEXT_COOKIE_NAME}=${payload}; ` +
    `path=${getProxyPrefix(routeKey)}/; secure; samesite=lax`;
}

function shouldAllowSameOriginSandbox(frameOrigin?: string): boolean {
  if (!frameOrigin) {
    return false;
  }

  try {
    return new URL(frameOrigin).origin !== window.location.origin;
  } catch {
    return false;
  }
}

function getSandboxFlags(frameOrigin?: string): string {
  const flags = [...SANDBOX_BASE_FLAGS];
  if (shouldAllowSameOriginSandbox(frameOrigin)) {
    flags.push('allow-same-origin');
  }
  return flags.join(' ');
}

function applyIframeSandbox(
  frameOrigin?: string,
  targetFrame?: HTMLIFrameElement | null,
  targetUrl?: string | null,
): void {
  const frame = targetFrame ?? getActiveIframe();
  if (!frame) {
    return;
  }

  if (shouldSandboxPreviewFrame(targetUrl ?? getActiveUrl(), isDevMode())) {
    frame.setAttribute('sandbox', getSandboxFlags(frameOrigin));
    return;
  }
  frame.removeAttribute('sandbox');
}

async function handleGo(): Promise<void> {
  if (!urlInput) {
    return;
  }

  const sessionId = $activeSessionId.get();
  const previewName = getActivePreviewName();
  if (!sessionId) {
    return;
  }

  const url = normalizeUrl(urlInput.value.trim());
  if (!url) {
    return;
  }

  urlInput.value = url;

  log.info(() => `Setting web preview target: ${sessionId}/${previewName} -> ${url}`);
  const result = await setWebPreviewTarget(sessionId, previewName, url);
  if (!result?.active) {
    log.warn(() => 'Failed to set web preview target');
    return;
  }

  setActiveMode('docked');
  setCurrentPreviewUrl(url);
  await loadPreview();
}

function decodeIframeNavigationUrl(
  iframeUrl: string,
  routeKey: string,
  targetOrigin?: string,
): string | null {
  const parsed = new URL(iframeUrl, window.location.origin);
  const prefix = getProxyPrefix(routeKey);

  if (parsed.pathname === `${prefix}/_ext`) {
    return parsed.searchParams.get('u');
  }

  let path = parsed.pathname;
  if (path.startsWith(`${prefix}/`)) {
    path = path.slice(prefix.length);
  } else if (path === prefix) {
    path = '/';
  } else {
    return parsed.toString();
  }

  stripInternalPreviewQueryParams(parsed);

  const baseOrigin =
    targetOrigin ||
    (() => {
      const target = getActiveUrl() ?? $webPreviewUrl.get();
      if (!target) {
        return null;
      }
      return new URL(target).origin;
    })();

  if (!baseOrigin) {
    return null;
  }

  return `${baseOrigin}${path}${parsed.search}${parsed.hash}`;
}

function setCurrentPreviewUrl(url: string | null, updateInput = true): void {
  const sanitizedUrl = url ? sanitizePreviewDisplayUrl(url) : null;
  const nextInputValue = sanitizedUrl ?? '';
  if (
    loadedUrl === sanitizedUrl &&
    $webPreviewUrl.get() === sanitizedUrl &&
    (!updateInput || !urlInput || urlInput.value === nextInputValue)
  ) {
    return;
  }

  loadedUrl = sanitizedUrl;
  setActiveUrl(sanitizedUrl);
  $webPreviewUrl.set(sanitizedUrl);
  if (updateInput && urlInput) {
    urlInput.value = nextInputValue;
  }
  updateScreenshotButtonState();
}

async function ensureDockedPreviewClient(
  sessionId: string,
  previewName: string,
): Promise<BrowserPreviewClientResponse | null> {
  const existing = getSessionDockedClient(sessionId, previewName);
  if (existing?.previewId && existing.previewToken && existing.routeKey) {
    return existing;
  }

  const created = await createBrowserPreviewClient(sessionId, previewName);
  if (!created) {
    return null;
  }

  setSessionDockedClient(sessionId, previewName, created);
  return created;
}

function isActivePreviewMessage(message: PreviewBridgeMessage): boolean {
  const activeClient = getActiveDockedClient();
  return (
    !!activeClient &&
    message.previewId === activeClient.previewId &&
    message.previewToken === activeClient.previewToken
  );
}

function getPreviewFrameKey(sessionId: string, previewName: string): string {
  return `${sessionId}::${previewName}`;
}

function getActivePreviewFrameKey(): string | null {
  const sessionId = $activeSessionId.get();
  if (!sessionId) {
    return null;
  }

  return getPreviewFrameKey(sessionId, getActivePreviewName());
}

function getActiveIframe(): HTMLIFrameElement | null {
  const key = activeFrameKey ?? getActivePreviewFrameKey();
  if (!key) {
    return null;
  }

  return previewFrames.get(key) ?? null;
}

function createPreviewIframe(frameKey: string): HTMLIFrameElement | null {
  if (!iframeHost) {
    return null;
  }

  const frame = document.createElement('iframe');
  frame.className = 'web-preview-iframe hidden';
  frame.src = 'about:blank';
  frame.setAttribute('allow', FRAME_ALLOW_ATTR.trim());
  frame.dataset.previewFrameKey = frameKey;
  iframeHost.appendChild(frame);
  previewFrames.set(frameKey, frame);
  return frame;
}

function replacePreviewIframe(frameKey: string): HTMLIFrameElement | null {
  const existing = previewFrames.get(frameKey);
  if (existing) {
    destroyPreviewFrameByKey(frameKey, existing);
  }

  if (activeFrameKey === frameKey) {
    activeFrameKey = null;
  }

  return createPreviewIframe(frameKey);
}

function ensurePreviewIframe(sessionId: string, previewName: string): HTMLIFrameElement | null {
  const frameKey = getPreviewFrameKey(sessionId, previewName);
  return previewFrames.get(frameKey) ?? createPreviewIframe(frameKey);
}

function destroyPreviewFrameByKey(
  frameKey: string,
  frame: HTMLIFrameElement | null = previewFrames.get(frameKey) ?? null,
): void {
  if (!frame) {
    return;
  }

  frame.name = '';
  frame.src = 'about:blank';
  frame.removeAttribute(PREVIEW_LOAD_TOKEN_ATTRIBUTE);
  frame.remove();
  previewFrames.delete(frameKey);

  if (activeFrameKey === frameKey) {
    activeFrameKey = null;
    loadedUrl = null;
  }
}

export function destroyPreviewFrame(sessionId: string, previewName: string): void {
  destroyPreviewFrameByKey(getPreviewFrameKey(sessionId, previewName));
}

function shouldRemountPreviewFrame(
  frame: HTMLIFrameElement,
  previewClient: BrowserPreviewClientResponse,
  targetUrl: string,
  targetRevision: number,
): boolean {
  const nextLoadToken = buildPreviewLoadToken(targetUrl, targetRevision);
  if (frame.dataset[PREVIEW_LOAD_TOKEN_DATASET_KEY] !== nextLoadToken) {
    return true;
  }

  const currentFrameIdentity = frame.name || '';
  const nextFrameIdentity = JSON.stringify(previewClient);
  return currentFrameIdentity !== nextFrameIdentity;
}

function findPreviewIframeByWindow(source: MessageEventSource | null): HTMLIFrameElement | null {
  if (!source) {
    return null;
  }

  for (const frame of previewFrames.values()) {
    if (frame.contentWindow === source) {
      return frame;
    }
  }

  return null;
}

function refreshPreviewBridgeVisibility(frame: HTMLIFrameElement, visible: boolean): void {
  const postRefresh = (): void => {
    try {
      frame.contentWindow?.postMessage(
        { type: 'mt-refresh-browser-state', force: true, visible },
        '*',
      );
    } catch {
      // Ignore cross-origin or not-yet-loaded frames; the next load creates a fresh bridge.
    }
  };

  for (const delayMs of PREVIEW_VISIBILITY_REFRESH_DELAYS_MS) {
    if (delayMs === 0) {
      requestAnimationFrame(postRefresh);
      continue;
    }

    window.setTimeout(postRefresh, delayMs);
  }
}

function setVisiblePreviewFrame(frameKey: string | null): void {
  activeFrameKey = frameKey;
  for (const [key, frame] of previewFrames) {
    const isActive = key === frameKey;
    frame.classList.toggle('hidden', !isActive);
    frame.setAttribute('aria-hidden', isActive ? 'false' : 'true');
    frame.tabIndex = isActive ? 0 : -1;
    refreshPreviewBridgeVisibility(frame, isActive);
  }
  window.dispatchEvent(new CustomEvent(PREVIEW_TAB_CHANGED_EVENT, { detail: { frameKey } }));
}

function postCookieBridgeResponse(
  target: WindowProxy | null,
  message: PreviewCookieResponseMessage,
): void {
  if (!target) {
    return;
  }
  target.postMessage(message, '*');
}

function createCookieBridgeResponseMessage(
  request: PreviewCookieRequestMessage,
): PreviewCookieResponseMessage {
  const responseMessage: PreviewCookieResponseMessage = {
    type: 'mt-cookie-response',
    requestId: request.requestId,
  };
  if (typeof request.previewId === 'string') {
    responseMessage.previewId = request.previewId;
  }
  if (typeof request.previewToken === 'string') {
    responseMessage.previewToken = request.previewToken;
  }
  if (typeof request.sessionId === 'string') {
    responseMessage.sessionId = request.sessionId;
  }
  if (typeof request.previewName === 'string') {
    responseMessage.previewName = request.previewName;
  }
  return responseMessage;
}

function buildCookieBridgeUrl(routeKey: string, upstreamUrl?: string | null): URL {
  const url = new URL(getCookieBridgePath(routeKey), window.location.origin);
  if (upstreamUrl) {
    url.searchParams.set('u', upstreamUrl);
  }
  return url;
}

async function fetchCookieBridge(
  request: PreviewCookieRequestMessage,
  routeKey: string,
): Promise<Response> {
  const upstreamUrl =
    typeof request.upstreamUrl === 'string' ? request.upstreamUrl : getActiveUrl();
  const url = buildCookieBridgeUrl(routeKey, upstreamUrl);
  if (request.action === 'set') {
    return fetch(url.toString(), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ raw: request.raw ?? '' }),
    });
  }

  return fetch(url.toString(), { method: 'GET' });
}

async function handleCookieBridgeRequest(
  event: MessageEvent<unknown>,
  request: PreviewCookieRequestMessage,
): Promise<void> {
  const target = event.source as WindowProxy | null;
  const activePreview = getActivePreview();
  const routeKey = activePreview?.routeKey ?? getActiveDockedClient()?.routeKey ?? null;
  const responseMessage = createCookieBridgeResponseMessage(request);

  if (!routeKey) {
    responseMessage.error = 'No active preview route';
    postCookieBridgeResponse(target, responseMessage);
    return;
  }

  try {
    const response = await fetchCookieBridge(request, routeKey);

    if (!response.ok) {
      responseMessage.error = `Cookie bridge failed: ${response.status}`;
      postCookieBridgeResponse(target, responseMessage);
      return;
    }

    const data = (await response.json()) as { header?: string };
    responseMessage.header = typeof data.header === 'string' ? data.header : '';
  } catch (error) {
    responseMessage.error = String(error);
  }

  postCookieBridgeResponse(target, responseMessage);
}

function clearActivePreviewFrame(): void {
  setVisiblePreviewFrame(null);
  loadedUrl = null;
  hideStatusIndicator();
  updateScreenshotButtonState();
}

function isStillActivePreviewSession(sessionId: string, previewName: string): boolean {
  return $activeSessionId.get() === sessionId && getActivePreviewName() === previewName;
}

async function resolvePreviewLoadContext(): Promise<{
  sessionId: string;
  previewName: string;
  currentUrl: string;
  currentTargetRevision: number;
  frameKey: string;
  previewClient: BrowserPreviewClientResponse;
} | null> {
  const sessionId = $activeSessionId.get();
  const previewName = getActivePreviewName();
  const currentPreview = getActivePreview();
  const currentUrl = currentPreview?.url ?? $webPreviewUrl.get();
  const currentTargetRevision = currentPreview?.targetRevision ?? 0;

  if (!currentUrl || !sessionId) {
    clearActivePreviewFrame();
    return null;
  }

  const previewClient = await ensureDockedPreviewClient(sessionId, previewName);
  if (!isStillActivePreviewSession(sessionId, previewName)) {
    return null;
  }
  if (!previewClient) {
    setVisiblePreviewFrame(null);
    log.warn(() => `Failed to create browser preview client for ${sessionId}/${previewName}`);
    await refreshBrowserPreviewStatus();
    return null;
  }

  return {
    sessionId,
    previewName,
    currentUrl,
    currentTargetRevision,
    frameKey: getPreviewFrameKey(sessionId, previewName),
    previewClient,
  };
}

function ensurePreviewLoadFrame(sessionId: string, previewName: string): HTMLIFrameElement | null {
  const frame = ensurePreviewIframe(sessionId, previewName);
  if (!frame) {
    log.warn(() => `Failed to allocate dock iframe for ${sessionId}/${previewName}`);
    return null;
  }
  return frame;
}

function resetBrokenPreviewFrame(frame: HTMLIFrameElement): void {
  frame.name = '';
  frame.src = 'about:blank';
  frame.classList.add('hidden');
  frame.removeAttribute(PREVIEW_LOAD_TOKEN_ATTRIBUTE);
}

/** Load the current active named preview into the iframe. */
export async function loadPreview(reloadToken?: string): Promise<void> {
  if (!iframeHost) {
    return;
  }

  const context = await resolvePreviewLoadContext();
  if (!context) {
    return;
  }

  const { sessionId, previewName, currentUrl, currentTargetRevision, frameKey, previewClient } =
    context;
  const initialFrame = ensurePreviewLoadFrame(sessionId, previewName);
  if (!initialFrame) {
    return;
  }

  let frame: HTMLIFrameElement = initialFrame;

  try {
    if (shouldRemountPreviewFrame(frame, previewClient, currentUrl, currentTargetRevision)) {
      const replacementFrame = replacePreviewIframe(frameKey);
      if (!replacementFrame) {
        log.warn(() => `Failed to recreate dock iframe for ${sessionId}/${previewName}`);
        return;
      }
      frame = replacementFrame;
    }

    applyIframeSandbox(previewClient.origin, frame, currentUrl);
    setPreviewContextCookie(previewClient);
    frame.name = JSON.stringify(previewClient);
    const proxyUrl = buildProxyUrl(
      currentUrl,
      previewClient,
      currentTargetRevision,
      previewClient.origin ?? window.location.origin,
      reloadToken,
    );
    if (shouldReloadPreviewFrame(frame, proxyUrl, currentUrl, currentTargetRevision)) {
      if (frame.src === proxyUrl) {
        frame.src = 'about:blank';
      }
      frame.src = proxyUrl;
    }
    frame.dataset[PREVIEW_LOAD_TOKEN_DATASET_KEY] = buildPreviewLoadToken(
      currentUrl,
      currentTargetRevision,
    );
    setVisiblePreviewFrame(frameKey);
    loadedUrl = currentUrl;
    await refreshBrowserPreviewStatus();
  } catch {
    resetBrokenPreviewFrame(frame);
    await refreshBrowserPreviewStatus();
  }
}

async function handleRefresh(mode: PreviewReloadMode = 'force'): Promise<void> {
  const sessionId = $activeSessionId.get();
  const previewName = getActivePreviewName();
  if (!sessionId) {
    return;
  }

  if (mode === 'hard') {
    await clearWebPreviewBrowserStateAsync();
  }

  const currentUrl = getActiveUrl() ?? $webPreviewUrl.get();
  if (currentUrl) {
    const result = await setWebPreviewTarget(sessionId, previewName, currentUrl);
    if (!result?.active) {
      log.warn(() => 'Failed to refresh web preview target');
      return;
    }
    upsertSessionPreview(result);
    setCurrentPreviewUrl(currentUrl, false);
  }

  if (mode === 'soft') {
    await reloadWebPreview(sessionId, previewName, mode);
    await loadPreview();
    return;
  }

  if (mode === 'hard') {
    await reloadWebPreview(sessionId, previewName, mode);
  }

  await loadPreview(createForceReloadToken());
}

/**
 * Update the URL bar to reflect in-iframe navigation.
 */
function updateUrlBarFromIframe(
  iframeUrl: string,
  upstreamUrl?: string,
  targetOrigin?: string,
): void {
  try {
    const routeKey = getActivePreview()?.routeKey ?? getActiveDockedClient()?.routeKey;
    if (!routeKey) {
      return;
    }
    const displayUrl = upstreamUrl || decodeIframeNavigationUrl(iframeUrl, routeKey, targetOrigin);
    if (!displayUrl) {
      return;
    }
    setCurrentPreviewUrl(displayUrl);
  } catch {
    // ignore malformed URLs
  }
}

/** Show the web preview iframe and hide the detached placeholder. */
export function showIframe(): void {
  const frameKey = getActivePreviewFrameKey();
  if (frameKey) {
    setVisiblePreviewFrame(frameKey);
  }
  const placeholder = document.getElementById('web-preview-detached-msg');
  if (placeholder) {
    placeholder.classList.add('hidden');
  }
}

/** Hide the web preview iframe. */
export function hideIframe(): void {
  setVisiblePreviewFrame(null);
}

/** Unload the iframe by navigating to about:blank and hiding it. */
export function unloadIframe(sessionId?: string | null, previewName?: string | null): void {
  const frameKey =
    sessionId && previewName
      ? getPreviewFrameKey(sessionId, previewName)
      : getActivePreviewFrameKey();
  if (!frameKey) {
    loadedUrl = null;
    return;
  }

  const frame = previewFrames.get(frameKey);
  if (frame) {
    frame.name = '';
    frame.src = 'about:blank';
    frame.classList.add('hidden');
    frame.removeAttribute(PREVIEW_LOAD_TOKEN_ATTRIBUTE);
  }

  if (activeFrameKey === frameKey) {
    activeFrameKey = null;
    loadedUrl = null;
  }
}

/** Show the detached placeholder message and hide the iframe. */
export function showDetachedPlaceholder(): void {
  hideIframe();
  const placeholder = document.getElementById('web-preview-detached-msg');
  if (placeholder) {
    placeholder.classList.remove('hidden');
  }
}

/** Hide the detached placeholder message and show the iframe. */
export function hideDetachedPlaceholder(): void {
  const placeholder = document.getElementById('web-preview-detached-msg');
  if (placeholder) {
    placeholder.classList.add('hidden');
  }
  showIframe();
}

/**
 * Inject a chat message into the active terminal telling the agent to read
 * the browser control guidance file.
 */
function handleAgentHint(): void {
  const sessionId = $activeSessionId.get();
  if (!sessionId) {
    return;
  }

  const fg = getForegroundInfo(sessionId);
  const guidanceFile = getAgentGuidanceFile(fg.name);
  const message = `Read the file ${guidanceFile} for instructions on how to interact with this browser preview.\n`;

  sendInput(sessionId, message);
}

function decodeScreenshotDataUrl(dataUrl: string): Blob | null {
  const commaIndex = dataUrl.indexOf(',');
  if (commaIndex < 0) {
    return null;
  }

  const meta = dataUrl.slice(0, commaIndex);
  const mime = /^data:([^;]+)/.exec(meta)?.[1] ?? 'image/png';
  try {
    const binary = atob(dataUrl.slice(commaIndex + 1));
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
      bytes[i] = binary.charCodeAt(i);
    }
    return new Blob([bytes], { type: mime });
  } catch {
    return null;
  }
}

function setActionMessage(severity: 'info' | 'error', message: string | null): void {
  if (!actionMessage) {
    return;
  }

  if (!message) {
    actionMessage.textContent = '';
    actionMessage.classList.add('hidden');
    actionMessage.dataset.severity = '';
    return;
  }

  actionMessage.textContent = message;
  actionMessage.dataset.severity = severity;
  actionMessage.classList.remove('hidden');
}

function updateScreenshotButtonState(): void {
  if (!screenshotButton) {
    return;
  }

  const sessionId = $activeSessionId.get();
  const preview = getSessionPreview(sessionId, getActivePreviewName());
  screenshotButton.disabled = screenshotInFlight || !preview?.url;
}

function setScreenshotButtonBusy(active: boolean): void {
  if (!screenshotButton) {
    return;
  }

  const idleGlyph = screenshotButton.dataset.idleGlyph ?? screenshotButton.innerHTML;
  screenshotButton.dataset.idleGlyph = idleGlyph;
  const idleTitle = screenshotButton.dataset.idleTitle ?? screenshotButton.title;
  screenshotButton.dataset.idleTitle = idleTitle;

  if (active) {
    screenshotButton.disabled = true;
    screenshotButton.setAttribute('aria-busy', 'true');
    screenshotButton.classList.add('web-preview-action-working');
    screenshotButton.innerHTML = '<span class="web-preview-button-glyph">&#x21bb;</span>';
    screenshotButton.title = 'Capturing screenshot...';
    return;
  }

  screenshotButton.setAttribute('aria-busy', 'false');
  screenshotButton.classList.remove('web-preview-action-working');
  screenshotButton.innerHTML = idleGlyph;
  screenshotButton.title = idleTitle;
  updateScreenshotButtonState();
}

/**
 * Capture a screenshot of a named web preview.
 */
async function handleScreenshot(download = false, requestedPreviewName?: string): Promise<void> {
  if (screenshotInFlight) {
    return;
  }

  const sessionId = $activeSessionId.get();
  const previewName = requestedPreviewName ?? getActivePreviewName();
  const preview = getSessionPreview(sessionId, previewName);
  if (!sessionId || !preview?.url) {
    setActionMessage('error', 'Screenshot failed: there is no active browser preview to capture.');
    return;
  }

  screenshotInFlight = true;
  setActionMessage('info', null);
  setScreenshotButtonBusy(true);

  const ts = new Date().toISOString().replace(/[:.]/g, '-');
  const filename = `screenshot_${ts}.png`;

  try {
    const dataUrl = await captureBrowserScreenshotRaw(sessionId, undefined, previewName);
    if (!dataUrl) {
      setActionMessage(
        'error',
        'Screenshot failed: MidTerm did not receive image data back from the dev browser.',
      );
      log.warn(() => 'Browser screenshot capture failed');
      return;
    }

    const blob = decodeScreenshotDataUrl(dataUrl);
    if (!blob) {
      setActionMessage('error', 'Screenshot failed: the returned image data could not be decoded.');
      log.warn(() => 'Failed to decode browser screenshot');
      return;
    }

    if (download) {
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = filename;
      link.click();
      URL.revokeObjectURL(url);
      setActionMessage('info', null);
      log.info(() => 'Screenshot downloaded');
      return;
    }

    const file = new File([blob], filename, { type: 'image/png' });
    const formData = new FormData();
    formData.append('file', file);

    const resp = await fetch(`/api/sessions/${sessionId}/upload`, {
      method: 'POST',
      body: formData,
    });
    if (!resp.ok) {
      setActionMessage(
        'error',
        `Screenshot failed: MidTerm could not upload it to the session (${resp.status}).`,
      );
      log.warn(() => `Screenshot upload failed: ${resp.status}`);
      return;
    }
    const result = (await resp.json()) as UploadResponse;
    if (result.path) {
      await pasteToTerminal(sessionId, result.path, true);
      setActionMessage('info', null);
      log.info(() => 'Screenshot pasted to terminal');
      return;
    }
    setActionMessage(
      'error',
      'Screenshot failed: the upload completed but MidTerm did not return a usable file path.',
    );
  } catch (err) {
    setActionMessage('error', `Screenshot failed: ${String(err)}`);
    log.warn(() => `Screenshot upload error: ${String(err)}`);
  } finally {
    screenshotInFlight = false;
    setScreenshotButtonBusy(false);
  }
}

async function handleClearCookies(): Promise<void> {
  const sessionId = $activeSessionId.get();
  const previewName = getActivePreviewName();
  if (!sessionId) {
    return;
  }

  const ok = await clearWebPreviewCookies(sessionId, previewName);
  if (ok) {
    log.info(() => 'Cookies cleared');
    await loadPreview();
  } else {
    log.warn(() => 'Failed to clear cookies');
    await refreshBrowserPreviewStatus();
  }
}

async function handleClearState(): Promise<void> {
  const sessionId = $activeSessionId.get();
  const previewName = getActivePreviewName();
  if (!sessionId) {
    return;
  }

  const cleared = await clearWebPreviewState(sessionId, previewName);
  if (!cleared) {
    setStatusIndicatorMessage('error', 'Failed to clear the session-scoped preview state.');
    log.warn(() => 'Failed to clear session-scoped preview state');
    await refreshBrowserPreviewStatus();
    return;
  }

  upsertSessionPreview(cleared);
  setCurrentPreviewUrl(cleared.url);

  const browserResult = await runBrowserCommand('clearstate', sessionId, previewName);

  if (!browserResult?.success) {
    const error =
      browserResult?.error?.trim() ||
      'Server-side preview state was cleared, but browser-side state could not be cleared.';
    setStatusIndicatorMessage(
      'warn',
      `Server-side preview state cleared. Browser-side state could not be fully cleared: ${error}`,
    );
    log.warn(() => `Browser-side clearstate failed: ${error}`);
  } else {
    log.info(() => 'Preview state cleared');
  }

  await loadPreview();
}

async function clearWebPreviewBrowserStateAsync(): Promise<void> {
  if ('serviceWorker' in navigator) {
    try {
      const registrations = await navigator.serviceWorker.getRegistrations();
      await Promise.all(
        registrations.filter((r) => r.scope.includes('/webpreview')).map((r) => r.unregister()),
      );
    } catch {
      // ignore
    }
  }

  if ('caches' in window) {
    try {
      const keys = await caches.keys();
      await Promise.all(
        keys.filter((k) => k.toLowerCase().includes('webpreview')).map((k) => caches.delete(k)),
      );
    } catch {
      // ignore
    }
  }
}

function hideStatusIndicator(): void {
  if (!statusIndicator) {
    return;
  }

  statusIndicator.textContent = '!';
  statusIndicator.title = '';
  statusIndicator.classList.add('hidden');
  statusIndicator.dataset.severity = 'info';
  statusIndicator.setAttribute('aria-hidden', 'true');
  statusIndicator.removeAttribute('aria-label');
}

function setStatusIndicatorMessage(severity: 'info' | 'warn' | 'error', message: string): void {
  if (!statusIndicator) {
    return;
  }

  statusIndicator.textContent = '!';
  statusIndicator.title = message;
  statusIndicator.dataset.severity = severity;
  statusIndicator.classList.remove('hidden');
  statusIndicator.setAttribute('aria-hidden', 'false');
  statusIndicator.setAttribute('aria-label', message);
}

async function refreshBrowserPreviewStatus(): Promise<void> {
  const dock = document.getElementById('web-preview-dock');
  if (dock?.classList.contains('hidden')) {
    return;
  }

  const sessionId = $activeSessionId.get();
  if (!sessionId) {
    hideStatusIndicator();
    return;
  }

  const previewName = getActivePreviewName();
  const preview = getActivePreview();
  if (!preview?.url && !getActiveDockedClient()?.previewId) {
    hideStatusIndicator();
    return;
  }

  const status = await getBrowserPreviewStatus(sessionId, previewName);

  if (!status) {
    setStatusIndicatorMessage(
      'warn',
      'Browser status is currently unavailable, so the dev browser state cannot be verified honestly.',
    );
    return;
  }

  const indicatorState = buildBrowserPreviewStatusIndicatorState(status);
  if (!indicatorState) {
    hideStatusIndicator();
    return;
  }

  setStatusIndicatorMessage(indicatorState.severity, indicatorState.message);
}
