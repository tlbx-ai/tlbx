import { afterEach, describe, expect, it, vi } from 'vitest';
import { $activeSessionId } from '../../stores';
import { fetchPreviewCookie, resolvePreviewMessageTarget } from './previewMessageTarget';
import {
  getActiveUrl,
  getSessionPreview,
  removeSessionState,
  setSessionDockedClient,
  setSessionNavigationUrl,
  upsertSessionPreview,
} from './webSessionState';
import { shouldRemountPreviewFrame } from './previewLoadToken';

const source = {} as Window;
function setup(sessionId: string, previewName = 'default') {
  const info = {
    sessionId,
    previewName,
    routeKey: `${sessionId}-${previewName}`,
    url: `https://${sessionId}.test/`,
    active: true,
    targetRevision: 1,
  };
  upsertSessionPreview(info);
  const client = {
    sessionId,
    previewName,
    routeKey: info.routeKey,
    previewId: `${info.routeKey}-id`,
    previewToken: `${info.routeKey}-token`,
  };
  setSessionDockedClient(sessionId, previewName, client);
  return {
    info,
    client,
    frame: { contentWindow: source, dataset: { previewFrameKey: `${sessionId}::${previewName}` } },
    message: { ...client, targetRevision: 1 },
  };
}
afterEach(() => {
  removeSessionState('a');
  removeSessionState('b');
  $activeSessionId.set(null);
  vi.unstubAllGlobals();
});

describe('preview message scope', () => {
  it('writes and reads a background cookie using its own route while another session is selected', async () => {
    const a = setup('a');
    setup('b');
    $activeSessionId.set('b');
    const target = resolvePreviewMessageTarget(a.frame, source, a.message)!;
    const fetchMock = vi.fn().mockResolvedValue(new Response('{}'));
    vi.stubGlobal('fetch', fetchMock);
    await fetchPreviewCookie(
      {
        ...a.message,
        type: 'mt-cookie-request',
        requestId: 'write',
        action: 'set',
        raw: 'campaign=lane-a',
      },
      target,
      'https://tlbx.test',
    );
    await fetchPreviewCookie(
      { ...a.message, type: 'mt-cookie-request', requestId: 'read', action: 'get' },
      target,
      'https://tlbx.test',
    );
    expect(fetchMock.mock.calls[0]?.[0]).toBe(
      'https://tlbx.test/webpreview/a-default/_cookies?u=https%3A%2F%2Fa.test%2F',
    );
    expect(fetchMock.mock.calls[0]?.[1].body).toBe('{"raw":"campaign=lane-a"}');
    expect(fetchMock.mock.calls[1]?.[0]).toBe(fetchMock.mock.calls[0]?.[0]);
  });

  it('rejects sibling-session, sibling-preview, stale-revision and unrelated-window messages', () => {
    const a = setup('a');
    const b = setup('b');
    const sibling = setup('a', 'other');
    expect(resolvePreviewMessageTarget(a.frame, source, b.message)).toBeNull();
    expect(resolvePreviewMessageTarget(a.frame, source, sibling.message)).toBeNull();
    expect(resolvePreviewMessageTarget(a.frame, {} as Window, a.message)).toBeNull();
    expect(
      resolvePreviewMessageTarget(a.frame, source, { ...a.message, targetRevision: 0 }),
    ).toBeNull();
    expect(
      resolvePreviewMessageTarget(a.frame, source, { ...a.message, sessionId: 'b' }),
    ).toBeNull();
    expect(
      resolvePreviewMessageTarget(a.frame, source, { ...a.message, previewToken: 'wrong' }),
    ).toBeNull();
  });

  it('keeps background navigation through a session sync without changing the configured target or sibling URL', () => {
    const a = setup('a');
    setup('b');
    $activeSessionId.set('b');
    const target = resolvePreviewMessageTarget(a.frame, source, a.message)!;
    setSessionNavigationUrl(target.sessionId, target.previewName, 'https://a.test/next');
    upsertSessionPreview(a.info);
    expect(getActiveUrl()).toBe('https://b.test/');
    expect(getSessionPreview('a', 'default')?.url).toBe('https://a.test/');
    $activeSessionId.set('a');
    expect(getActiveUrl()).toBe('https://a.test/next');
    upsertSessionPreview({ ...a.info, targetRevision: 2 });
    expect(getActiveUrl()).toBe('https://a.test/');
  });

  it('does not remount an existing document when the injected bridge adds revision metadata to window.name', () => {
    const a = setup('a');
    const frame = {
      name: JSON.stringify({ ...a.client, targetRevision: 1 }),
      dataset: { mtPreviewLoadToken: '1:https://a.test/' },
    } as unknown as HTMLIFrameElement;
    expect(shouldRemountPreviewFrame(frame, JSON.stringify(a.client), 'https://a.test/', 1)).toBe(
      false,
    );
    expect(
      shouldRemountPreviewFrame(
        frame,
        JSON.stringify({ ...a.client, previewId: 'new' }),
        'https://a.test/',
        1,
      ),
    ).toBe(true);
  });
});
