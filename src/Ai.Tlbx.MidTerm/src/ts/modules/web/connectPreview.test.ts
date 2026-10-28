import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  getConnectPreviewIntegration,
  wrapConnectPreviewUrl,
  wrapConnectWorkspaceUrl,
  type ConnectPreviewIntegration,
} from './connectPreview';

function install(overrides: Partial<ConnectPreviewIntegration> = {}): ConnectPreviewIntegration {
  const integration: ConnectPreviewIntegration = {
    origin: 'https://preview.connect.example',
    wrapUrl: (value) => {
      const broker = new URL(
        '/preview.html?connectionId=connection',
        'https://preview.connect.example',
      );
      const native = new URL(value);
      broker.hash = new URLSearchParams({ url: native.pathname + native.search }).toString();
      return broker.toString();
    },
    fetch: vi.fn(),
    ...overrides,
  };
  vi.stubGlobal('window', {
    location: { origin: 'https://workspace.connect.example' },
    __tlbxConnectPreview: integration,
  });
  return integration;
}

afterEach(() => vi.unstubAllGlobals());

describe('Connect scoped preview transport hook', () => {
  it('keeps native browser behavior without an installed hook', () => {
    vi.stubGlobal('window', { location: { origin: 'https://native.example' } });
    const url = 'https://native.example:2101/webpreview/route/';
    expect(wrapConnectPreviewUrl(url)).toBe(url);
    expect(wrapConnectWorkspaceUrl('/pdf/index.html?file=document.pdf#page=2')).toBe(
      '/pdf/index.html?file=document.pdf#page=2',
    );
  });

  it('keeps trusted native URLs unchanged when the optional workspace hook is absent', () => {
    install();
    const url = '/web-preview-popup.html?session=fixture&previewToken=fixture-token';
    expect(wrapConnectWorkspaceUrl(url)).toBe(url);
  });

  it.each([
    '/web-preview-popup.html?session=fixture&previewToken=fixture-token',
    '/pdf/index.html?file=%2Fapi%2Ffiles%2Fview%3Fpath%3Ddocument.pdf#page=2',
    '/?board=fixture&handoff=1',
  ])('routes trusted document navigation through the explicit workspace hook: %s', (url) => {
    const workspaceUrl = vi.fn((value: string) => `/remote/connection${value}`);
    install({ workspaceUrl });
    expect(wrapConnectWorkspaceUrl(url)).toBe(`/remote/connection${url}`);
    expect(workspaceUrl).toHaveBeenCalledExactlyOnceWith(url);
  });

  it('keeps scoped bootstrap credentials in the broker fragment', () => {
    install();
    const url = wrapConnectPreviewUrl(
      'https://preview.connect.example/webpreview/0123456789abcdefabcd/?__mtPreviewId=fixture-id&__mtPreviewToken=fixture-token',
    );
    const result = new URL(url);
    expect(result.origin).toBe('https://preview.connect.example');
    expect(result.search).not.toContain('fixture-token');
    expect(new URLSearchParams(result.hash.slice(1)).get('url')).toContain(
      '__mtPreviewToken=fixture-token',
    );
  });

  it.each([
    'https://workspace.connect.example',
    'http://preview.connect.example',
    'https://user:password@preview.connect.example',
    'https://preview.connect.example/another/path',
  ])('rejects a preview transport that would lose origin isolation: %s', (origin) => {
    install({ origin });
    expect(getConnectPreviewIntegration()).toBeNull();
  });

  it('rejects a wrapper that changes the preview origin', () => {
    install({ wrapUrl: () => 'https://workspace.connect.example/untrusted-preview' });
    expect(() =>
      wrapConnectPreviewUrl('https://preview.connect.example/webpreview/route/'),
    ).toThrow('different origin');
  });
});
