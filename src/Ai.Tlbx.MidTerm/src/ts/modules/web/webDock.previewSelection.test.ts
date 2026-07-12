import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { $activeSessionId } from '../../stores';
import {
  getSessionSelectedPreviewName,
  removeSessionState,
  setSessionSelectedPreviewName,
  upsertSessionPreview,
} from './webSessionState';

vi.mock('../terminal/scaling', () => ({
  rescaleAllTerminalsImmediate: vi.fn(),
  autoResizeAllTerminalsImmediate: vi.fn(),
}));

vi.mock('../sessionTabs', () => ({
  setActionButtonActive: vi.fn(),
}));

vi.mock('./webPanel', () => ({
  hideIframe: vi.fn(),
  restoreLastUrl: vi.fn(),
  showIframe: vi.fn(),
  unloadIframe: vi.fn(),
}));

vi.mock('./webDetach', () => ({
  isDetachedOpenForSession: () => false,
}));

vi.mock('./webApi', () => ({
  clearWebPreviewTarget: vi.fn(),
}));

vi.mock('../logging', () => ({
  createLogger: () => ({
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    verbose: vi.fn(),
  }),
}));

import { selectPreferredActivePreview } from './webDock';

describe('webDock preferred preview selection', () => {
  const sessionId = 'session-a';

  beforeEach(() => {
    $activeSessionId.set(sessionId);
    removeSessionState(sessionId);
  });

  afterEach(() => {
    removeSessionState(sessionId);
    $activeSessionId.set(null);
  });

  it('reuses an existing named preview target instead of opening the empty default', () => {
    upsertSessionPreview({
      sessionId,
      previewName: 'mobile-social',
      routeKey: 'route-1',
      url: 'https://example.com/',
      active: true,
      targetRevision: 1,
    });

    selectPreferredActivePreview();

    expect(getSessionSelectedPreviewName(sessionId)).toBe('mobile-social');
  });

  it('keeps the current selection when it already has a target', () => {
    upsertSessionPreview({
      sessionId,
      previewName: 'default',
      routeKey: 'route-1',
      url: 'https://default.example/',
      active: true,
      targetRevision: 1,
    });
    upsertSessionPreview({
      sessionId,
      previewName: 'mobile-social',
      routeKey: 'route-2',
      url: 'https://example.com/',
      active: true,
      targetRevision: 1,
    });

    selectPreferredActivePreview();

    expect(getSessionSelectedPreviewName(sessionId)).toBe('default');
  });

  it('keeps an explicitly selected named preview', () => {
    upsertSessionPreview({
      sessionId,
      previewName: 'mobile-social',
      routeKey: 'route-1',
      url: 'https://example.com/',
      active: true,
      targetRevision: 1,
    });
    upsertSessionPreview({
      sessionId,
      previewName: 'second',
      routeKey: 'route-2',
      url: 'https://second.example/',
      active: true,
      targetRevision: 1,
    });
    setSessionSelectedPreviewName(sessionId, 'second');

    selectPreferredActivePreview();

    expect(getSessionSelectedPreviewName(sessionId)).toBe('second');
  });

  it('stays on the empty default when no named preview has a target', () => {
    selectPreferredActivePreview();

    expect(getSessionSelectedPreviewName(sessionId)).toBe('default');
  });
});
