import type { BrowserPreviewClientResponse } from './webApi';

export interface UploadResponse {
  path?: string;
}

export interface PreviewBridgeMessage {
  targetRevision?: number;
  previewId?: string;
  previewToken?: string;
  sessionId?: string;
  previewName?: string;
}

export interface PreviewLoadContext {
  sessionId: string;
  previewName: string;
  currentUrl: string;
  currentTargetRevision: number;
  frameKey: string;
  previewClient: BrowserPreviewClientResponse;
}

export interface PreviewNavigationMessage extends PreviewBridgeMessage {
  type: 'mt-navigation';
  url: string;
  targetOrigin?: string;
  upstreamUrl?: string;
}

export interface PreviewCookieRequestMessage extends PreviewBridgeMessage {
  type: 'mt-cookie-request';
  requestId: string;
  action: 'get' | 'set';
  raw?: string;
  upstreamUrl?: string;
}

export interface PreviewCookieResponseMessage extends PreviewBridgeMessage {
  type: 'mt-cookie-response';
  requestId: string;
  header?: string;
  error?: string;
}
