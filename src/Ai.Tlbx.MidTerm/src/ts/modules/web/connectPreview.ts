/** Optional transport integration installed by the trusted tlbx Connect workspace shell. */
export interface ConnectPreviewIntegration {
  origin: string;
  wrapUrl(url: string): string;
  fetch(url: string, init?: RequestInit): Promise<Response>;
  workspaceUrl?(url: string): string;
}

declare global {
  interface Window {
    __tlbxConnectPreview?: ConnectPreviewIntegration;
  }
}

export function getConnectPreviewIntegration(): ConnectPreviewIntegration | null {
  const integration = window.__tlbxConnectPreview;
  if (
    !integration ||
    typeof integration.origin !== 'string' ||
    typeof integration.wrapUrl !== 'function' ||
    typeof integration.fetch !== 'function'
  ) {
    return null;
  }
  try {
    const origin = new URL(integration.origin);
    if (
      origin.origin === window.location.origin ||
      origin.username ||
      origin.password ||
      origin.pathname !== '/' ||
      origin.search ||
      origin.hash ||
      (origin.protocol !== 'https:' &&
        !(
          origin.protocol === 'http:' &&
          ['localhost', '127.0.0.1', '[::1]'].includes(origin.hostname)
        ))
    ) {
      return null;
    }
    return integration;
  } catch {
    return null;
  }
}

export function wrapConnectPreviewUrl(url: string): string {
  const integration = getConnectPreviewIntegration();
  if (!integration) return url;
  const wrapped = integration.wrapUrl(url);
  if (new URL(wrapped).origin !== new URL(integration.origin).origin) {
    throw new Error('Connect preview transport returned a different origin.');
  }
  return wrapped;
}

/** Keep trusted tlbx documents inside the current Connect workspace namespace. */
export function wrapConnectWorkspaceUrl(url: string): string {
  const integration = getConnectPreviewIntegration();
  return integration?.workspaceUrl ? integration.workspaceUrl(url) : url;
}
