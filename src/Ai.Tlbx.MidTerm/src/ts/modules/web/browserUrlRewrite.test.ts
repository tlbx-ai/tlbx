import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

const source = readFileSync(
  new URL('../../../../Services/WebPreview/WebPreviewProxyMiddleware.cs', import.meta.url),
  'utf8',
);
const rewriteSource = source.slice(
  source.indexOf('function r(u){'),
  source.indexOf('// === Network APIs ==='),
);

function rewrite(value: string, external = false) {
  let parses = 0;
  class BoundedURL extends URL {
    constructor(url: string, base?: string) {
      if (++parses > 10) throw new Error('Repeated URL resolution');
      super(url, base);
    }
  }
  const result = new Function(
    'URL',
    'window',
    'location',
    'document',
    'u',
    `
    var P='/webpreview', PP=P, E=P+'/_ext?u=';
    function ar(u){return u;}
    ${rewriteSource}
    return r(u);
  `,
  )(
    BoundedURL,
    { __mtExternalDocument: external, __mtDocumentUrl: 'https://example.org/docs/' },
    { protocol: 'https:', origin: 'https://localhost:2001', host: 'localhost:2001' },
    { baseURI: 'https://localhost:2001/webpreview/docs/' },
    value,
  );
  return { result, parses };
}

describe('browser URL rewriting', () => {
  it.each([
    'mailto:person@example.org',
    'tel:+4912345',
    'urn:isbn:123',
    'FTP://example.org/file',
    'data:text/plain,hi',
    'javascript:void(0)',
    '#section',
  ])('passes %s through without parsing or recursion', (value) => {
    expect(rewrite(value)).toEqual({ result: value, parses: 0 });
    expect(rewrite(value, true)).toEqual({ result: value, parses: 0 });
  });

  it('still proxies network and relative links', () => {
    expect(rewrite('https://example.org/a').result).toBe(
      '/webpreview/_ext?u=https%3A%2F%2Fexample.org%2Fa',
    );
    expect(rewrite('//example.org/a').result).toBe(
      '/webpreview/_ext?u=https%3A%2F%2Fexample.org%2Fa',
    );
    expect(rewrite('next').result).toBe('https://localhost:2001/webpreview/docs/next');
    expect(rewrite('next', true).result).toBe(
      '/webpreview/_ext?u=https%3A%2F%2Fexample.org%2Fdocs%2Fnext',
    );
  });
});
