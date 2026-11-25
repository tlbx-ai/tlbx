import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { getBrowserPreviewStatus, getWebPreviewTarget, listWebPreviewSessions } from './webApi';
import { rememberPreview, restoreSessionPreviews } from './webPreviewRecovery';

vi.mock('../../utils/cookies', () => ({
  getOrCreateClientId: () => 'profile',
  getOrCreateTabId: () => 'exact-tab',
}));
vi.mock('./webApi', () => ({
  getBrowserPreviewStatus: vi.fn(),
  getWebPreviewTarget: vi.fn(),
  listWebPreviewSessions: vi.fn(),
}));

const target = {
  sessionId: 'session',
  previewName: 'canvas',
  routeKey: 'new-route',
  url: 'https://authoritative.invalid/',
  active: true,
  targetRevision: 5,
};

describe('authoritative preview recovery protocol', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    const storage = new Map<string, string>();
    vi.stubGlobal('sessionStorage', {
      getItem: (key: string) => storage.get(key) ?? null,
      setItem: (key: string, value: string) => storage.set(key, value),
    });
    vi.mocked(listWebPreviewSessions).mockResolvedValue([target]);
    vi.mocked(getWebPreviewTarget).mockResolvedValue(target);
    vi.mocked(getBrowserPreviewStatus).mockResolvedValue({
      ownerBrowserId: 'profile:exact-tab',
    } as Awaited<ReturnType<typeof getBrowserPreviewStatus>>);
  });

  afterEach(() => vi.unstubAllGlobals());

  it('recovers a server target when tab storage has no remembered URL', async () => {
    const restore = vi.fn().mockResolvedValue(undefined);
    await restoreSessionPreviews('session', restore);
    expect(restore).toHaveBeenCalledExactlyOnceWith({
      sessionId: 'session',
      previewName: 'canvas',
      url: target.url,
      targetRevision: 5,
    });
  });

  it('discards stale tab navigation after the server advances the revision', async () => {
    rememberPreview({ ...target, url: 'https://stale.invalid/', targetRevision: 4 });
    const restore = vi.fn().mockResolvedValue(undefined);
    await restoreSessionPreviews('session', restore);
    expect(restore.mock.calls[0]?.[0].url).toBe(target.url);
  });

  it('does not restore a preview owned by another exact tab', async () => {
    vi.mocked(getBrowserPreviewStatus).mockResolvedValue({
      ownerBrowserId: 'profile:offline-owner',
      ownerConnected: false,
    } as Awaited<ReturnType<typeof getBrowserPreviewStatus>>);
    const restore = vi.fn();
    await restoreSessionPreviews('session', restore);
    expect(restore).not.toHaveBeenCalled();
  });

  it('never resurrects a UI-only target missing from server state', async () => {
    rememberPreview({ ...target, url: 'https://stale.invalid/' });
    vi.mocked(listWebPreviewSessions).mockResolvedValue([]);
    const restore = vi.fn();
    await restoreSessionPreviews('session', restore);
    expect(restore).not.toHaveBeenCalled();
    expect(sessionStorage.getItem('mt-preview-recovery')).toBe('[]');
    expect(getWebPreviewTarget).not.toHaveBeenCalled();
  });

  it('does not restore a target cleared while recovery was reading status', async () => {
    vi.mocked(getWebPreviewTarget).mockResolvedValue({ ...target, active: false, url: null });
    const restore = vi.fn();
    await restoreSessionPreviews('session', restore);
    expect(restore).not.toHaveBeenCalled();
  });
});
