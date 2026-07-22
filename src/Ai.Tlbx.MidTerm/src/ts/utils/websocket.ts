/**
 * WebSocket Utilities
 *
 * Helper functions for WebSocket connection management.
 */

import { getBrowserDeviceLabel, getOrCreateTabId } from './cookies';

function getEmbeddedWebPreviewPrefix(): string {
  const path = location.pathname || '';
  const match = path.match(/^\/webpreview\/[^/]+/);
  return match ? match[0] : '';
}

/**
 * Create WebSocket URL with correct protocol (ws/wss based on page protocol)
 */
export function createWsUrl(path: string): string {
  const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
  const normalizedPath = path.startsWith('/') ? path : `/${path}`;
  const proxyPrefix = getEmbeddedWebPreviewPrefix();
  const url = new URL(`${protocol}//${location.host}${proxyPrefix}${normalizedPath}`);
  url.searchParams.set('tabId', getOrCreateTabId());
  url.searchParams.set('deviceLabel', getBrowserDeviceLabel());
  return url.toString();
}

/**
 * Close WebSocket cleanly, preventing reconnect loops.
 * Sets onclose to null before closing to prevent the close handler from triggering.
 */
export function closeWebSocket(ws: WebSocket | null, setter?: (ws: null) => void): void {
  if (ws) {
    ws.onclose = null;
    ws.close();
    if (setter) setter(null);
  }
}
