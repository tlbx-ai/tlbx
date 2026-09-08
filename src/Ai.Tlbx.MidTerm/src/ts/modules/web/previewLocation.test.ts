import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

const source = readFileSync(
  new URL('../../../../Services/WebPreview/WebPreviewProxyMiddleware.cs', import.meta.url),
  'utf8',
);
const start = source.indexOf('          // Application Location is upstream-facing;');
const end = source.indexOf('          function postMt(type,extra){', start);
const install = new Function('window', 'location', 'r', 'curU', source.slice(start, end));

function fixture() {
  let upstream = 'http://localhost:5178/admin?tab=structure#sizes';
  const navigations: string[] = [];
  const native = {
    href: 'https://host:2001/webpreview/route/admin',
    assign: (url: string) => navigations.push(`assign:${url}`),
    replace: (url: string) => navigations.push(`replace:${url}`),
    reload: () => navigations.push('reload'),
  };
  const window = { location: native } as {
    location: typeof native;
    __mtPreviewLocation: (value: unknown) => Location;
  };
  install(
    window,
    native,
    (url: string) => `proxy:${url}`,
    () => upstream,
  );
  return {
    view: window.__mtPreviewLocation(native),
    adapt: window.__mtPreviewLocation,
    native,
    navigations,
    navigate: (url: string) => (upstream = url),
  };
}

describe('upstream application location', () => {
  it('exposes the real app route and dev port while retaining the native proxy location', () => {
    const { view, native } = fixture();
    expect(view.pathname).toBe('/admin');
    expect(view.port).toBe('5178');
    expect(view.origin).toBe('http://localhost:5178');
    expect(view.search).toBe('?tab=structure');
    expect(view.hash).toBe('#sizes');
    expect(native.href).toBe('https://host:2001/webpreview/route/admin');
  });

  it('leaves shadowed location objects unchanged', () => {
    const { adapt } = fixture();
    const local = { pathname: '/custom' };
    expect(adapt(local)).toBe(local);
    expect(adapt(undefined)).toBeUndefined();
    expect(adapt(null)).toBeNull();
  });

  it('tracks history and hash changes through live getters', () => {
    const { view, navigate } = fixture();
    navigate('http://localhost:5178/admin/items?sort=name#new');
    expect(view.pathname).toBe('/admin/items');
    expect(view.search).toBe('?sort=name');
    expect(String(view)).toBe('http://localhost:5178/admin/items?sort=name#new');
  });

  it('routes location writes, assign, replace and reload through the proxy', () => {
    const { view, navigations } = fixture();
    view.href = '/login';
    view.assign('items');
    view.replace('/admin');
    view.reload();
    expect(navigations).toEqual([
      'assign:proxy:http://localhost:5178/login',
      'assign:proxy:http://localhost:5178/items',
      'replace:proxy:http://localhost:5178/admin',
      'reload',
    ]);
  });
});
