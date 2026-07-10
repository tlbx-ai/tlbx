import type { BrowserPreviewClientResponse } from './webApi';

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
  const prefix = `/webpreview/${encodeURIComponent(previewClient.routeKey)}`;
  const proxyUrl = new URL(path === '/' ? `${prefix}/` : `${prefix}${path}`, frameOrigin);
  proxyUrl.search = parsed.search;
  proxyUrl.hash = parsed.hash;
  if (previewClient.previewId && previewClient.previewToken) {
    proxyUrl.searchParams.set(PREVIEW_QUERY_ID_PARAM, previewClient.previewId);
    proxyUrl.searchParams.set(PREVIEW_QUERY_TOKEN_PARAM, previewClient.previewToken);
  }
  proxyUrl.searchParams.set(PREVIEW_QUERY_TARGET_REVISION_PARAM, String(targetRevision));
  if (buildOptions.reloadToken) {
    proxyUrl.searchParams.set(PREVIEW_QUERY_RELOAD_TOKEN_PARAM, buildOptions.reloadToken);
  }
  return proxyUrl.toString();
}

export function stripInternalPreviewQueryParams(url: URL): void {
  url.searchParams.delete(PREVIEW_QUERY_ID_PARAM);
  url.searchParams.delete(PREVIEW_QUERY_TOKEN_PARAM);
  url.searchParams.delete(PREVIEW_QUERY_TARGET_REVISION_PARAM);
  url.searchParams.delete(PREVIEW_QUERY_RELOAD_TOKEN_PARAM);
}

export function sanitizePreviewDisplayUrl(urlText: string): string {
  if (!urlText.includes('__mt')) {
    return urlText;
  }

  try {
    const parsed = new URL(urlText);
    stripInternalPreviewQueryParams(parsed);
    return parsed.toString();
  } catch {
    return urlText;
  }
}
