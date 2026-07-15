/**
 * Preview sandbox policy shared by docked and detached web preview surfaces.
 *
 * Dev mode keeps the existing "sandbox everything" behavior. Outside dev
 * mode, tlbx still force-sandboxes obviously untrusted targets so an
 * arbitrary website or local HTML file cannot execute with full access to the
 * owning tlbx shell origin.
 */

const LOOPBACK_HOSTS = new Set(['localhost', '127.0.0.1', '::1', '[::1]']);

function tryParseUrl(targetUrl: string, currentOrigin: string): URL | null {
  try {
    return new URL(targetUrl, currentOrigin);
  } catch {
    return null;
  }
}

function isLoopbackHost(hostname: string): boolean {
  return LOOPBACK_HOSTS.has(hostname.trim().toLowerCase());
}

export function shouldForceSandboxForTarget(
  targetUrl: string | null | undefined,
  currentOrigin = window.location.origin,
): boolean {
  if (!targetUrl) {
    return false;
  }

  const parsed = tryParseUrl(targetUrl, currentOrigin);
  if (!parsed) {
    return false;
  }

  if (parsed.protocol === 'file:') {
    return true;
  }

  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
    return false;
  }

  const shellUrl = tryParseUrl(currentOrigin, currentOrigin);
  if (!shellUrl) {
    return true;
  }

  if (parsed.origin === shellUrl.origin) {
    return false;
  }

  if (parsed.hostname === shellUrl.hostname) {
    return false;
  }

  return !isLoopbackHost(parsed.hostname);
}

export function shouldSandboxPreviewFrame(
  targetUrl: string | null | undefined,
  devMode: boolean,
  currentOrigin = window.location.origin,
): boolean {
  if (devMode) {
    return true;
  }

  return shouldForceSandboxForTarget(targetUrl, currentOrigin);
}
