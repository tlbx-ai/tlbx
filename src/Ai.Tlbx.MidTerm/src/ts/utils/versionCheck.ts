/**
 * Version Check Utility
 *
 * Checks if the frontend JS version matches the server version.
 * Used on WebSocket reconnect to detect server updates and decide whether
 * the current shell can keep running until the user refreshes.
 */

import { JS_BUILD_VERSION, MUX_PROTOCOL_VERSION } from '../constants';
import { createLogger } from '../modules/logging';
import { getVersion, getVersionDetails } from '../api/client';
import {
  clearFrontendRefreshState,
  requestFrontendRefresh,
  setFrontendRefreshState,
} from '../modules/updating/runtime';

const log = createLogger('version');

let checkInFlight: Promise<boolean> | null = null;

function getCurrentDocumentAssetVersion(): string | null {
  if (typeof document === 'undefined') {
    return null;
  }

  const script = document.querySelector<HTMLScriptElement>('script[src*="/js/terminal.min.js"]');
  const src = script?.getAttribute('src')?.trim();
  if (!src) {
    return null;
  }

  try {
    const url = new URL(src, window.location.href);
    const value = url.searchParams.get('v')?.trim();
    return value ? value : null;
  } catch {
    return null;
  }
}

function isSourceDevAssetVersion(assetVersion: string | null): boolean {
  return typeof assetVersion === 'string' && assetVersion.startsWith('dev-');
}

async function fetchServerAssetVersion(): Promise<string | null> {
  const response = await fetch('/index.html', { cache: 'no-store' });
  if (!response.ok) {
    return null;
  }

  const html = await response.text();
  const match = html.match(/\/js\/terminal\.min\.js\?v=([^"'\\s>]+)/i);
  // Preview transport parameters are not part of the source asset version.
  const value = match?.[1]?.split('&', 1)[0]?.trim();
  return value ? decodeURIComponent(value) : null;
}

async function handleSourceDevVersionMismatch(currentAssetVersion: string): Promise<boolean> {
  const serverAssetVersion = await fetchServerAssetVersion();
  if (serverAssetVersion && serverAssetVersion !== currentAssetVersion) {
    log.info(
      () =>
        `Source-dev asset mismatch: client=${currentAssetVersion}, server=${serverAssetVersion}`,
    );
    requestFrontendRefresh();
    return true;
  }

  clearFrontendRefreshState();
  return false;
}

function resolveFrontendRefreshPolicy(
  detailsResult: Awaited<ReturnType<typeof getVersionDetails>> | null,
): {
  refreshStatus: 'available' | 'required';
  updateType: 'webOnly' | 'unknown';
  protocolCompatible: boolean;
  webOnlyCompatible: boolean;
} {
  const manifest = detailsResult?.data ?? null;
  const protocolCompatible =
    manifest?.protocol === undefined || manifest.protocol === MUX_PROTOCOL_VERSION;
  const webOnlyCompatible = manifest?.webOnly === true;
  return {
    refreshStatus: protocolCompatible && webOnlyCompatible ? 'available' : 'required',
    updateType: manifest?.webOnly === true ? 'webOnly' : 'unknown',
    protocolCompatible,
    webOnlyCompatible,
  };
}

async function handleReleasedAssetVersionMismatch(options?: {
  forceReloadOnMismatch?: boolean;
}): Promise<boolean> {
  const [{ data, response }, detailsResult] = await Promise.all([
    getVersion(),
    getVersionDetails().catch(() => null),
  ]);
  if (!response.ok || !data) {
    return false;
  }

  if (data === JS_BUILD_VERSION) {
    clearFrontendRefreshState();
    return false;
  }

  const refreshPolicy = resolveFrontendRefreshPolicy(detailsResult);
  log.info(
    () =>
      `Version mismatch: client=${JS_BUILD_VERSION}, server=${data}, protocolCompatible=${String(refreshPolicy.protocolCompatible)}, webOnlyCompatible=${String(refreshPolicy.webOnlyCompatible)}`,
  );
  setFrontendRefreshState(data, {
    status: refreshPolicy.refreshStatus,
    updateType: refreshPolicy.updateType,
  });

  if (options?.forceReloadOnMismatch === true || !refreshPolicy.protocolCompatible) {
    requestFrontendRefresh();
    return true;
  }

  return false;
}

/**
 * Check if server version differs from frontend version and coordinate a safe
 * shell refresh policy for the current tab.
 * Returns true when a reload was requested and callers must stop processing
 * the current command in the stale shell.
 */
export async function checkVersionAndReload(options?: {
  forceReloadOnMismatch?: boolean;
}): Promise<boolean> {
  if (checkInFlight) {
    const reloadRequested = await checkInFlight;
    if (reloadRequested || options?.forceReloadOnMismatch !== true) {
      return reloadRequested;
    }
  }

  const checkPromise = (async (): Promise<boolean> => {
    try {
      const currentAssetVersion = getCurrentDocumentAssetVersion();
      if (currentAssetVersion && isSourceDevAssetVersion(currentAssetVersion)) {
        return await handleSourceDevVersionMismatch(currentAssetVersion);
      }

      return await handleReleasedAssetVersionMismatch(options);
    } catch {
      // Network error during version check - ignore, will retry on next reconnect
      return false;
    }
  })();

  checkInFlight = checkPromise;

  try {
    return await checkPromise;
  } finally {
    if (checkInFlight === checkPromise) {
      checkInFlight = null;
    }
  }
}
