import type { BrowserPreviewClientResponse } from './webApi';
import { getConnectPreviewIntegration, wrapConnectPreviewUrl } from './connectPreview';

const PREVIEW_QUERY_ID_PARAM = '__mtPreviewId';
const PREVIEW_QUERY_TOKEN_PARAM = '__mtPreviewToken';
const PREVIEW_QUERY_TARGET_REVISION_PARAM = '__mtTargetRevision';
const PREVIEW_QUERY_RELOAD_TOKEN_PARAM = '__mtReloadToken';

export interface BuildProxyUrlOptions {
  reloadToken?: string;
}

export function buildProxyUrl(
  targetUrl: string,
  previewClient: BrowserPreviewClientResponse,
  targetRevision: number,
  frameOrigin = window.location.origin,
  options: BuildProxyUrlOptions | string = {},
): string {
  const buildOptions = typeof options === 'string' ? { reloadToken: options } : options;
  const parsed = new URL(targetUrl);
  const path = parsed.pathname || '/';
  let prefix = `/webpreview/${encodeURIComponent(previewClient.routeKey)}`;
  const connect = getConnectPreviewIntegration();
  if (!connect) {
    if (!previewClient.previewId || !previewClient.previewToken) {
      throw new Error('A scoped preview capability is required.');
    }
    prefix += `/_access/${encodeURIComponent(previewClient.previewId)}/${encodeURIComponent(previewClient.previewToken)}`;
  }
  const proxyUrl = new URL(path === '/' ? `${prefix}/` : `${prefix}${path}`, frameOrigin);
  proxyUrl.search = parsed.search;
  proxyUrl.hash = parsed.hash;
  if (connect && previewClient.previewId && previewClient.previewToken) {
    proxyUrl.searchParams.set(PREVIEW_QUERY_ID_PARAM, previewClient.previewId);
    proxyUrl.searchParams.set(PREVIEW_QUERY_TOKEN_PARAM, previewClient.previewToken);
  }
  proxyUrl.searchParams.set(PREVIEW_QUERY_TARGET_REVISION_PARAM, String(targetRevision));
  if (buildOptions.reloadToken) {
    proxyUrl.searchParams.set(PREVIEW_QUERY_RELOAD_TOKEN_PARAM, buildOptions.reloadToken);
  }
  return wrapConnectPreviewUrl(proxyUrl.toString());
}

export function stripInternalPreviewQueryParams(url: URL): void {
  url.searchParams.delete(PREVIEW_QUERY_ID_PARAM);
  url.searchParams.delete(PREVIEW_QUERY_TOKEN_PARAM);
  url.searchParams.delete(PREVIEW_QUERY_TARGET_REVISION_PARAM);
  url.searchParams.delete(PREVIEW_QUERY_RELOAD_TOKEN_PARAM);
}

export function sanitizePreviewDisplayUrl(urlText: string): string {
  try {
    const relative = urlText.startsWith('/') && !urlText.startsWith('//');
    const parsed = new URL(urlText, window.location.origin);
    const originalPath = parsed.pathname;
    const originalQuery = parsed.search;
    stripInternalPreviewQueryParams(parsed);
    const segments = parsed.pathname.split('/');
    const decoded = (segment: string): string => {
      try {
        return decodeURIComponent(segment);
      } catch {
        return segment;
      }
    };
    for (let index = 0; index + 4 < segments.length; index++) {
      if (
        decoded(segments[index] ?? '').toLowerCase() === 'webpreview' &&
        decoded(segments[index + 2] ?? '') === '_access'
      ) {
        segments.splice(index + 2, 3);
        parsed.pathname = segments.join('/');
        break;
      }
    }
    if (parsed.pathname === originalPath && parsed.search === originalQuery) return urlText;
    return relative ? parsed.pathname + parsed.search + parsed.hash : parsed.toString();
  } catch {
    return urlText;
  }
}
