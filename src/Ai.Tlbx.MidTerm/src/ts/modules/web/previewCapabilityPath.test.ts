import { beforeEach, describe, expect, it, vi } from 'vitest';
import { buildProxyUrl, sanitizePreviewDisplayUrl } from './previewProxyUrl';
import type { BrowserPreviewClientResponse } from './webApi';

const origin = 'https://192.0.2.5:2000';
const client = {
  routeKey: 'route',
  previewId: 'owner',
  previewToken: 'fixture-token',
} as BrowserPreviewClientResponse;
beforeEach(() => vi.stubGlobal('window', { location: { origin } }));

describe('direct preview capability paths', () => {
  it('keeps relative module subimports and CSS resources inside the same capability', () => {
    const document = buildProxyUrl('https://upstream.example/docs/page.html', client, 1);
    expect(new URL(document).pathname).toBe(
      '/webpreview/route/_access/owner/fixture-token/docs/page.html',
    );
    expect(new URL('./chunk.js', document).pathname).toBe(
      '/webpreview/route/_access/owner/fixture-token/docs/chunk.js',
    );
    expect(new URL('../styles/site.css', document).pathname).toBe(
      '/webpreview/route/_access/owner/fixture-token/styles/site.css',
    );
  });
  it.each([
    '/webpreview/route/_access/owner/fixture-token/docs/a%2Fb',
    '/webpreview/route/%5Faccess/%6fwner/fixture-token/docs/a%2Fb',
    '/%77ebpreview/route/%5faccess/owner/fixture-token/docs/a%2Fb',
  ])('never displays path credentials while preserving upstream escaping: %s', (path) => {
    const sanitized = sanitizePreviewDisplayUrl(
      origin + path + '?__mtPreviewToken=fixture-token&app=1',
    );
    expect(sanitized).not.toContain('fixture-token');
    expect(sanitized).not.toContain('owner');
    expect(sanitized).toContain('/docs/a%2Fb?app=1');
  });
  it('also hides credentials in relative native URLs', () => {
    expect(sanitizePreviewDisplayUrl('/webpreview/route/%5Faccess/owner/fixture-token/docs/')).toBe(
      '/webpreview/route/docs/',
    );
  });
  it('preserves ordinary upstream URLs byte-for-byte', () => {
    const url = 'https://upstream.example/a%2Fb?q=value#frag';
    expect(sanitizePreviewDisplayUrl(url)).toBe(url);
  });
});
