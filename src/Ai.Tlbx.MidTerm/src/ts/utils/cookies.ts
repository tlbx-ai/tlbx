/**
 * Cookie Utilities
 *
 * Functions for reading and writing browser cookies.
 */

/**
 * Get a cookie value by name
 */
export function getCookie(name: string): string | null {
  const match = document.cookie.match(new RegExp('(^| )' + name + '=([^;]+)'));
  const value = match?.[2];
  return value !== undefined ? decodeURIComponent(value) : null;
}

/**
 * Set a cookie value
 */
export function setCookie(name: string, value: string, days: number = 365): void {
  const expires = new Date(Date.now() + days * 24 * 60 * 60 * 1000).toUTCString();
  document.cookie = `${name}=${encodeURIComponent(value)}; expires=${expires}; path=/`;
}

/**
 * Get or create a persistent client ID cookie for unique-client tracking.
 */
export function getOrCreateClientId(): string {
  const name = 'mt-client-id';
  let id = getCookie(name);
  if (!id) {
    id = crypto.randomUUID();
    setCookie(name, id, 365);
  }
  return id;
}

const TAB_ID_STORAGE_KEY = 'mt-tab-id';
const TAB_ID_CHANNEL_NAME = 'tlbx-tab-identity-v1';
const TAB_ID_PROBE_DELAY_MS = 60;
let volatileTabId: string | null = null;
const tabIdentityChannels = new Set<BroadcastChannel>();
let tabIdentityInitialization: Promise<string> | null = null;

/**
 * Get or create a tab-local ID for distinguishing multiple tabs in one browser session.
 */
export function getOrCreateTabId(): string {
  try {
    const existing = sessionStorage.getItem(TAB_ID_STORAGE_KEY);
    if (existing) {
      return existing;
    }

    const id = crypto.randomUUID();
    sessionStorage.setItem(TAB_ID_STORAGE_KEY, id);
    return id;
  } catch {
    volatileTabId ??= crypto.randomUUID();
    return volatileTabId;
  }
}

interface TabIdentityMessage {
  type: 'probe' | 'occupied';
  tabId: string;
  runtimeId: string;
  targetRuntimeId?: string;
}

/**
 * Detect copied sessionStorage IDs (for example Chrome's Duplicate tab action)
 * before WebSockets are opened. Existing tabs keep their identity; the newcomer
 * rekeys itself so shared profile cookies never collapse two tabs into one owner.
 */
export function initializeTabIdentity(): Promise<string> {
  if (tabIdentityInitialization) return tabIdentityInitialization;

  tabIdentityInitialization = new Promise<string>((resolve) => {
    let tabId = getOrCreateTabId();
    if (typeof BroadcastChannel === 'undefined') {
      resolve(tabId);
      return;
    }

    const runtimeId = crypto.randomUUID();
    let channel: BroadcastChannel;
    try {
      channel = new BroadcastChannel(TAB_ID_CHANNEL_NAME);
    } catch {
      resolve(tabId);
      return;
    }
    tabIdentityChannels.add(channel);
    let occupied = false;

    channel.onmessage = (event: MessageEvent<TabIdentityMessage>) => {
      const message = event.data;
      if (message.runtimeId === runtimeId || message.tabId !== tabId) return;

      if (message.type === 'probe') {
        channel.postMessage({
          type: 'occupied',
          tabId,
          runtimeId,
          targetRuntimeId: message.runtimeId,
        } satisfies TabIdentityMessage);
      } else if (message.targetRuntimeId === runtimeId) {
        occupied = true;
      }
    };

    channel.postMessage({ type: 'probe', tabId, runtimeId } satisfies TabIdentityMessage);
    globalThis.setTimeout(() => {
      if (occupied) {
        tabId = crypto.randomUUID();
        try {
          sessionStorage.setItem(TAB_ID_STORAGE_KEY, tabId);
        } catch {
          volatileTabId = tabId;
        }
      }
      resolve(tabId);
    }, TAB_ID_PROBE_DELAY_MS);
  });

  return tabIdentityInitialization;
}

export function getBrowserDeviceLabel(): string {
  if (typeof navigator === 'undefined') return 'Browser';

  const userAgent = navigator.userAgent || '';
  const device = detectDeviceLabel(userAgent, navigator.maxTouchPoints);
  const browser = detectBrowserLabel(userAgent);
  return browser ? `${device} · ${browser}` : device;
}

/**
 * HTTP header values must remain ASCII-safe. The backend decodes this value
 * before exposing the human-readable device label.
 */
export function getBrowserDeviceHeaderValue(): string {
  return encodeURIComponent(getBrowserDeviceLabel());
}

function detectDeviceLabel(userAgent: string, maxTouchPoints: number): string {
  if (/iPad/i.test(userAgent) || (/Macintosh/i.test(userAgent) && maxTouchPoints > 1)) {
    return 'iPad';
  }
  if (/iPhone|iPod/i.test(userAgent)) return 'iPhone';
  if (/Android/i.test(userAgent)) {
    return /Mobile/i.test(userAgent) ? 'Android phone' : 'Android tablet';
  }
  if (/Windows/i.test(userAgent)) return 'Windows PC';
  if (/Mac/i.test(userAgent)) return 'Mac';
  if (/Linux/i.test(userAgent)) return 'Linux PC';
  return 'Browser';
}

function detectBrowserLabel(userAgent: string): string {
  if (/Edg\//i.test(userAgent)) return 'Edge';
  if (/Firefox\//i.test(userAgent)) return 'Firefox';
  if (/CriOS\/|Chrome\//i.test(userAgent)) return 'Chrome';
  if (/Safari\//i.test(userAgent)) return 'Safari';
  return '';
}

/**
 * Delete a cookie by name
 */
export function deleteCookie(name: string): void {
  document.cookie = `${name}=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/`;
}

/**
 * Detect clipboard shortcut style based on platform
 */
export function getClipboardStyle(setting: 'auto' | 'windows' | 'unix'): 'windows' | 'unix' {
  if (setting !== 'auto') return setting;
  const ua = navigator.userAgent || '';
  return /Windows|Win32|Win64/i.test(ua) ? 'windows' : 'unix';
}
