import { afterEach, beforeEach, expect, it, vi } from 'vitest';
vi.mock('../../utils/cookies', () => ({
  getOrCreateClientId: () => 'client',
  getOrCreateTabId: () => 'tab',
}));
vi.mock('./webApi', () => ({
  getBrowserPreviewStatus: vi.fn(),
  getWebPreviewTarget: vi.fn(),
}));
import { getBrowserPreviewStatus, getWebPreviewTarget } from './webApi';
import { forgetPreview, rememberPreview, restoreSessionPreviews } from './webPreviewRecovery';

const saved = {
  sessionId: 'background',
  previewName: 'docs',
  url: 'https://example.org/next',
  targetRevision: 4,
};
let stored: Map<string, string>;
beforeEach(() => {
  stored = new Map();
  vi.stubGlobal('sessionStorage', {
    getItem: (key: string) => stored.get(key) ?? null,
    setItem: (key: string, value: string) => stored.set(key, value),
  });
  vi.mocked(getBrowserPreviewStatus).mockResolvedValue({ ownerBrowserId: 'client:tab' } as Awaited<
    ReturnType<typeof getBrowserPreviewStatus>
  >);
  vi.mocked(getWebPreviewTarget).mockResolvedValue({
    ...saved,
    active: true,
    routeKey: 'route',
    url: 'https://example.org/start',
  });
  rememberPreview(saved);
});
afterEach(() => {
  vi.unstubAllGlobals();
  vi.resetAllMocks();
});

it('restores the navigated page of an owned background named preview', async () => {
  const restore = vi.fn();
  await restoreSessionPreviews('background', restore);
  expect(restore).toHaveBeenCalledExactlyOnceWith(saved);
  expect([...stored.values()].join()).not.toContain('previewToken');
});

it('uses the new target after a server-side target replacement', async () => {
  vi.mocked(getWebPreviewTarget).mockResolvedValue({
    ...saved,
    active: true,
    routeKey: 'route',
    targetRevision: 5,
    url: 'https://example.org/new',
  });
  const restore = vi.fn();
  await restoreSessionPreviews('background', restore);
  expect(restore).toHaveBeenCalledExactlyOnceWith({
    ...saved,
    targetRevision: 5,
    url: 'https://example.org/new',
  });
});

it('does not restore or retain a preview handed off to a different tab', async () => {
  vi.mocked(getBrowserPreviewStatus).mockResolvedValue({
    ownerBrowserId: 'client:other-tab',
  } as Awaited<ReturnType<typeof getBrowserPreviewStatus>>);
  const restore = vi.fn();
  await restoreSessionPreviews('background', restore);
  expect(restore).not.toHaveBeenCalled();
  expect(JSON.parse(stored.get('mt-preview-recovery')!)).toEqual([]);
});

it('does not reopen closed targets or explicitly removed previews', async () => {
  vi.mocked(getWebPreviewTarget).mockResolvedValue({
    ...saved,
    active: false,
    routeKey: 'route',
    url: null,
  });
  const restore = vi.fn();
  await restoreSessionPreviews('background', restore);
  expect(restore).not.toHaveBeenCalled();
  rememberPreview(saved);
  forgetPreview('background', 'docs');
  await restoreSessionPreviews('background', restore);
  expect(restore).not.toHaveBeenCalled();
});

it('isolates sessions and retains recovery data during a transient API failure', async () => {
  const restore = vi.fn();
  await restoreSessionPreviews('other', restore);
  expect(getBrowserPreviewStatus).not.toHaveBeenCalled();
  vi.mocked(getBrowserPreviewStatus).mockResolvedValue(null);
  await restoreSessionPreviews('background', restore);
  expect(restore).not.toHaveBeenCalled();
  expect(JSON.parse(stored.get('mt-preview-recovery')!)).toEqual([saved]);
});
