import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  getConnectPreviewIntegration,
  wrapConnectPreviewUrl,
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
