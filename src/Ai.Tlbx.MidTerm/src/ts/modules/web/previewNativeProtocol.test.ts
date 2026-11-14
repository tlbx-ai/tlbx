import { readFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { runInNewContext } from 'node:vm';
import { describe, expect, it, vi } from 'vitest';

// Execute the actual injected URL protocol functions, rather than duplicating
// their logic or starting a UI. Both native and Connect documents use this code.
const source = readFileSync(
  new URL('../../../../Services/WebPreview/WebPreviewProxyMiddleware.cs', import.meta.url),
  'utf8',
);
function section(start: string, end: string): string {
  const first = source.indexOf(start);
  const last = source.indexOf(end, first + start.length);
  if (first < 0 || last < 0) throw new Error(`Missing injected protocol section: ${start}`);
  return source.slice(first, last);
}
// The separately prepared storage change makes this same dispatcher async.
const dispatcherStart = source.includes('async function handleBCmd(msg){')
  ? 'async function handleBCmd(msg){'
  : 'function handleBCmd(msg){';
const protocol = [
  section('function mtProxyPrefix(){', 'var P="/webpreview"'),
  'var BP=mtProxyPrefix(),PP=BP+P,E=PP+"/_ext?u=";',
  section('function syncProxyBase(){', '          syncProxyBase();'),
  section('function ar(u){', 'function frameUrl(u){'),
  section('function mtProxyUrl(u,p){', '// === Network APIs ==='),
  section('function mtUpstreamSearch(search){', '// Application Location is upstream-facing;'),
  section('function mtMetadataUrl(value){', 'function mtResponseUrl(nativeUrl,readHeader){'),
  section('function mtResponseUrl(nativeUrl,readHeader){', 'var mtHeadersGet='),
  section('function mtInstallScriptUrl(proto){', '          mtInstallScriptUrl(window.HTMLScriptElement'),
  section('function mtNavigationUrl(value){', 'var mtLocationView={};'),
].join('\n');
const route = '0123456789abcdefabcd';
const nativePrefix = `/webpreview/${route}`;
const upstream = 'http://127.0.0.1:53836';

function exercise(
  virtualPrefix: string,
  suffix: string,
  baseSuffix = '/docs/',
  origin = 'https://preview.example',
  ownRoute = nativePrefix,
  reloadToken = '',
  targetOrigin = upstream,
) {
  const location = new URL(`${origin}${virtualPrefix}${ownRoute}${suffix}`);
  let base = ownRoute + baseSuffix;
  const baseElement = {
    getAttribute: () => base,
    setAttribute: (_: string, value: string) => {
      base = value;
    },
  };
  const document = {
    querySelector: () => baseElement,
    get baseURI() {
      return new URL(base, location).href;
    },
  };
  const context = {
    P: ownRoute,
    location,
    mtNativeLocation: location,
    document,
    URL,
    URLSearchParams,
    window: { __mtTargetOrigin: targetOrigin, __mtDocumentUrl: `${targetOrigin}/docs/page.html` },
    mtReloadToken: reloadToken,
    mtNestedPreview: false,
    mtCtx: { origin: 'https://inner-proxy.example' },
  };
  const result = runInNewContext(
    `${protocol}\nsyncProxyBase();({curU,r,mtNavigationUrl,mtResponseUrl,mtInstallResponseUrl,mtInstallScriptUrl,moduleUrl:window.__mtPreviewModuleUrl})`,
    context,
  ) as {
    curU(): string;
    r(value: string): string;
    mtNavigationUrl(value: string): string;
    mtResponseUrl(value: string, readHeader: (name: string) => string | null): string;
    mtInstallResponseUrl(
      proto: object,
      key: string,
      readHeader: (this: object, name: string) => string | null,
    ): void;
    mtInstallScriptUrl(proto: object): void;
    moduleUrl(value: unknown, finalUrl: string): unknown;
  };
  return {
    ...result,
    get base() {
      return base;
    },
  };
}

describe.each(['', `/p/connection/${route}`])('native preview URL protocol at %s', (prefix) => {
  it('reports the upstream root when the proxy route has no trailing slash', () => {
    expect(exercise(prefix, '?own=value#hash').curU()).toBe(`${upstream}/?own=value#hash`);
  });
  it('reports the original nested upstream URL without transport credentials', () => {
    const result = exercise(
      prefix,
      '/docs/page.html?view=a%20b&__mtPreviewId=id&__mtPreviewToken=token&__mtTargetRevision=2&__mtReloadToken=reload&__mtSubframe=1#section',
    );
    expect(result.curU()).toBe(`${upstream}/docs/page.html?view=a%20b#section`);
  });
  it('preserves the injected document directory and relative assets', () => {
    const result = exercise(prefix, '/docs/page.html');
    expect(result.base).toBe(`${prefix}${nativePrefix}/docs/`);
    expect(result.r('asset.js?mode=one#v2')).toBe(
      `https://preview.example${prefix}${nativePrefix}/docs/asset.js?mode=one#v2`,
    );
  });
  it('recognizes native and virtual proxy navigation without duplicating prefixes', () => {
    const result = exercise(prefix, '/docs/page.html');
    const tail = '/next?own=value#hash';
    expect(result.mtNavigationUrl(nativePrefix + tail)).toBe(prefix + nativePrefix + tail);
    expect(result.mtNavigationUrl(prefix + nativePrefix + tail)).toBe(prefix + nativePrefix + tail);
    expect(result.mtNavigationUrl(nativePrefix)).toBe(prefix + nativePrefix);
    expect(result.mtNavigationUrl(nativePrefix + '?own=value#hash')).toBe(
      prefix + nativePrefix + '?own=value#hash',
    );
    expect(
      result.mtNavigationUrl('https://preview.example' + nativePrefix + '?own=value#hash'),
    ).toBe('https://preview.example' + prefix + nativePrefix + '?own=value#hash');
  });
  it('decodes external upstream URL, query and hash independently of the virtual prefix', () => {
    const external = 'https://other.example/docs?q=a%20b&__mtPreviewToken=hidden#initial';
    const result = exercise(prefix, `/_ext?u=${encodeURIComponent(external)}#changed`);
    expect(result.curU()).toBe('https://other.example/docs?q=a%20b&__mtPreviewToken=hidden#changed');
  });
});

describe.each(['', `/p/connection/${route}`, '/webpreview/outer/_access/parent/parent-token'])(
  'preview response URL protocol at %s',
  (prefix) => {
    const ownRoute = `${nativePrefix}/_access/id/token`;
    const header = 'X-Tlbx-Preview-Upstream-Url';
    const final = 'https://stream.example/redirected/master.m3u8?__mtReloadToken=site&quality=high';
    function fixture() {
      return exercise(prefix, '/demo/', '/demo/', 'https://preview.example', ownRoute, 'reload');
    }
    function transport(path: string) {
      return `https://preview.example${prefix}${ownRoute}${path}`;
    }
    it('inverts direct and external URLs while removing only outer transport controls', () => {
      const f = fixture();
      expect(f.mtResponseUrl(transport('/master.m3u8?quality=high&__mtReloadToken=transport'), () => null))
        .toBe(`${upstream}/master.m3u8?quality=high`);
      expect(f.mtResponseUrl(transport(`/_ext?u=${encodeURIComponent(final)}&__mtPreviewToken=transport`), () => null))
        .toBe(final);
      expect(f.mtResponseUrl(transport('/master.m3u8'), (name) => name === header ? final : null))
        .toBe(final);
      expect(f.mtResponseUrl(transport('/master.m3u8'), () => 'https://user:secret@stream.example/master.m3u8'))
        .toBe('https://stream.example/master.m3u8');
      if (prefix) {
        expect(f.mtResponseUrl(transport('/master.m3u8'), () =>
          `https://inner-proxy.example${ownRoute}/_ext?u=${encodeURIComponent(final)}&__mtReloadToken=outer`))
          .toBe(final);
      }
      expect(f.r(final)).toContain(`/_ext?u=${encodeURIComponent(final)}`);
      expect(new URL(f.r(final), 'https://preview.example').searchParams.get('__mtReloadToken'))
        .toBe('reload');
    });
    it('ignores reserved metadata on synthetic, failed, other-route and foreign responses', () => {
      const f = fixture();
      for (const url of [
        '',
        `https://unrelated.example${prefix}${ownRoute}/master.m3u8`,
        transport('suffix/result'),
        'https://preview.example/webpreview/another/_ext?u=https%3A%2F%2Fother.example',
      ]) {
        const read = vi.fn(() => final);
        expect(f.mtResponseUrl(url, read)).toBe(url);
        expect(read).not.toHaveBeenCalled();
      }
      expect(f.mtResponseUrl(transport('/master.m3u8'), () => 'javascript:alert(1)'))
        .toBe(`${upstream}/master.m3u8`);
      expect(f.mtResponseUrl(transport('/master.m3u8'), () => { throw new Error('unavailable'); }))
        .toBe(`${upstream}/master.m3u8`);
    });
    it('keeps native XHR receivers, empty/error state and a reused request URL', () => {
      const state = new WeakMap<object, { url: string; final: string | null }>();
      class XhrFixture {
        get responseURL() {
          const value = state.get(this);
          if (!value) throw new TypeError('Illegal invocation');
          return value.url;
        }
        getResponseHeader(name: string) {
          const value = state.get(this);
          if (!value) throw new TypeError('Illegal invocation');
          return name === header ? value.final : null;
        }
      }
      fixture().mtInstallResponseUrl(XhrFixture.prototype, 'responseURL', XhrFixture.prototype.getResponseHeader);
      const xhr = new XhrFixture();
      state.set(xhr, { url: '', final });
      expect(xhr.responseURL).toBe('');
      state.set(xhr, { url: transport('/master.m3u8'), final });
      expect(xhr.responseURL).toBe(final);
      state.set(xhr, { url: transport('/next.m3u8'), final: null });
      expect(xhr.responseURL).toBe(`${upstream}/next.m3u8`);
      const get = Object.getOwnPropertyDescriptor(XhrFixture.prototype, 'responseURL')!.get!;
      expect(() => get.call({})).toThrow(TypeError);
    });
    it('leaves nonconfigurable native descriptors unchanged', () => {
      const proto = {};
      const get = () => transport('/master.m3u8');
      Object.defineProperty(proto, 'url', { get, configurable: false, enumerable: true });
      fixture().mtInstallResponseUrl(proto, 'url', () => final);
      expect(Object.getOwnPropertyDescriptor(proto, 'url')).toEqual({
        get, set: undefined, configurable: false, enumerable: true,
      });
    });
  },
);

describe('composed script and response URL metadata', () => {
  const outerRoute = '/webpreview/outer/_access/parent/parent-token';
  const innerRoute = `${nativePrefix}/_access/id/token`;
  const innerOrigin = 'https://inner-proxy.example';
  const actual = 'http://localhost:2314/cdn/classic/main.js?site=1';
  const physical = `https://preview.example${outerRoute}${innerRoute}/_ext?u=${encodeURIComponent(actual)}`;
  function wrappers() {
    return {
      outer: exercise('', '/demo/', '/demo/', 'https://preview.example', outerRoute, '', innerOrigin),
      inner: exercise(outerRoute, '/demo/', '/demo/', 'https://preview.example', innerRoute),
    };
  }

  it('accepts only the exact own capability and registered origin after an outer getter unwraps', () => {
    const { outer, inner } = wrappers();
    const unwrapped = outer.mtResponseUrl(physical, () => null);
    expect(unwrapped).toContain(`${innerOrigin}${innerRoute}/_ext?`);
    expect(inner.mtResponseUrl(unwrapped, () => null)).toBe(actual);
    for (const value of [
      `https://foreign.example${innerRoute}/_ext?u=${encodeURIComponent(actual)}`,
      `${innerOrigin}/webpreview/another/_access/other/secret/_ext?u=${encodeURIComponent(actual)}`,
    ]) {
      const header = vi.fn(() => actual);
      expect(inner.mtResponseUrl(value, header)).toBe(value);
      expect(header).not.toHaveBeenCalled();
    }
    const changed = new URL('https://application.example/custom.js');
    expect(inner.moduleUrl(changed, actual)).toBe(changed);
    expect(inner.moduleUrl('https://application.example/custom.js', actual))
      .toBe('https://application.example/custom.js');
    expect(inner.moduleUrl(unwrapped, 'https://user:secret@cdn.example/final/meta.js'))
      .toBe('https://cdn.example/final/meta.js');
  });

  it.each(['url', 'responseURL'])('composes existing %s getters without replacing native receivers', (key) => {
    const state = new WeakMap<object, string>();
    const proto = {};
    Object.defineProperty(proto, key, {
      configurable: true,
      enumerable: true,
      get(this: object) {
        if (!state.has(this)) throw new TypeError('Illegal invocation');
        return state.get(this);
      },
    });
    const object = Object.create(proto) as Record<string, string>;
    state.set(object, physical);
    const { outer, inner } = wrappers();
    outer.mtInstallResponseUrl(proto, key, () => null);
    inner.mtInstallResponseUrl(proto, key, () => null);
    expect(object[key]).toBe(actual);
    state.set(object, '');
    expect(object[key]).toBe('');
    const get = Object.getOwnPropertyDescriptor(proto, key)!.get!;
    expect(() => get.call({})).toThrow(TypeError);
  });

  it('keeps currentScript identity, setter branding and relative URLs through composed src getters', () => {
    const state = new WeakMap<object, string>();
    class ScriptFixture {
      get src() {
        if (!state.has(this)) throw new TypeError('Illegal invocation');
        return state.get(this)!;
      }
      set src(value: string) {
        if (!state.has(this)) throw new TypeError('Illegal invocation');
        state.set(this, value);
      }
    }
    const setter = Object.getOwnPropertyDescriptor(ScriptFixture.prototype, 'src')!.set;
    const script = new ScriptFixture();
    const document = { currentScript: script };
    state.set(script, physical);
    const { outer, inner } = wrappers();
    outer.mtInstallScriptUrl(ScriptFixture.prototype);
    inner.mtInstallScriptUrl(ScriptFixture.prototype);
    expect(document.currentScript).toBe(script);
    expect(script).toBeInstanceOf(ScriptFixture);
    expect(script.src).toBe(actual);
    expect(new URL('./theme.js', document.currentScript.src).href)
      .toBe('http://localhost:2314/cdn/classic/theme.js');
    const descriptor = Object.getOwnPropertyDescriptor(ScriptFixture.prototype, 'src')!;
    expect(descriptor.set).toBe(setter);
    expect(() => descriptor.get!.call({})).toThrow(TypeError);
    script.src = 'https://application.example/ordinary.js';
    expect(script.src).toBe('https://application.example/ordinary.js');
  });
});

it('preserves real native Response branding, redirects, clone and streaming bodies', async () => {
  // Real fetch supplies native URL slots. Only the proxy response is simulated;
  // no Response wrapper or replacement body implementation participates.
  const final = 'https://stream.example/levels/master.m3u8?__mtPreviewToken=site';
  const server = createServer((request, response) => {
    if (request.url?.endsWith('/redirect')) {
      response.writeHead(302, { Location: `${nativePrefix}/final` });
      response.end();
      return;
    }
    response.setHeader('X-Tlbx-Preview-Upstream-Url', final);
    response.write('#EXTM3U\n');
    response.end('level.m3u8\n');
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error('Missing fixture address');
  const origin = `http://127.0.0.1:${address.port}`;
  const original = Object.getOwnPropertyDescriptor(Response.prototype, 'url')!;
  const headers = Object.getOwnPropertyDescriptor(Response.prototype, 'headers')!.get!;
  const getHeader = Headers.prototype.get;
  try {
    exercise('', '/demo/', '/demo/', origin).mtInstallResponseUrl(Response.prototype, 'url', function (name) {
      return getHeader.call(headers.call(this) as Headers, name);
    });
    for (const path of ['/direct', '/redirect']) {
      const response = await fetch(`${origin}${nativePrefix}${path}`);
      expect(response).toBeInstanceOf(Response);
      expect(response.url).toBe(final);
      expect(response.redirected).toBe(path === '/redirect');
      const clone = response.clone();
      expect(clone.url).toBe(final);
      const reader = response.body!.getReader();
      const chunks: Uint8Array[] = [];
      for (;;) {
        const part = await reader.read();
        if (part.done) break;
        chunks.push(part.value);
      }
      expect(Buffer.concat(chunks).toString()).toBe('#EXTM3U\nlevel.m3u8\n');
      expect(response.bodyUsed).toBe(true);
      expect(await clone.text()).toBe('#EXTM3U\nlevel.m3u8\n');
      await expect(response.text()).rejects.toThrow(TypeError);
    }
    expect(new Response('synthetic', { headers: { 'X-Tlbx-Preview-Upstream-Url': final } }).url).toBe('');
    expect(Response.error().url).toBe('');
    expect(() => Object.getOwnPropertyDescriptor(Response.prototype, 'url')!.get!.call({})).toThrow(TypeError);
  } finally {
    Object.defineProperty(Response.prototype, 'url', original);
    server.closeAllConnections();
    await new Promise<void>((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
  }
});

describe('preview navigation attachment metadata', () => {
  const bootstrap = section(
    'function mtReadBootstrapContext(){',
    'function mtPersistPreviewContext(){',
  );
  function read(saved: Record<string, unknown>) {
    return runInNewContext(`${bootstrap} mtReadBootstrapContext();`, {
      URLSearchParams,
      mtParentContext: null,
      mtInjectionRoute: nativePrefix,
      mtNativeLocation: new URL(`https://native${nativePrefix}/_access/id/token/docs/next`),
      window: { name: JSON.stringify(saved) },
    }) as Record<string, unknown>;
  }
  it('retains revision and session context on subsequent navigation of the same attachment', () => {
    expect(
      read({ previewId: 'id', previewToken: 'token', targetRevision: 7, sessionId: 'session' }),
    ).toEqual({
      previewId: 'id',
      previewToken: 'token',
      targetRevision: 7,
      sessionId: 'session',
    });
  });
  it.each([
    { previewId: 'other', previewToken: 'token' },
    { previewId: 'id', previewToken: 'other' },
  ])('does not inherit metadata from a different capability %j', (saved) => {
    expect(read({ ...saved, targetRevision: 99, sessionId: 'old-session' })).toEqual({
      previewId: 'id',
      previewToken: 'token',
    });
  });
  it('reads its own capability through an outer proxy and ignores conflicting bootstrap credentials', () => {
    const own = `${nativePrefix}/_access/id/token`;
    const saved = {
      previewId: 'id',
      previewToken: 'token',
      routeKey: route,
      sessionId: 'child-session',
      targetRevision: 7,
    };
    const context = runInNewContext(`${bootstrap} mtReadBootstrapContext();`, {
      URLSearchParams,
      mtParentContext: null,
      mtInjectionRoute: own,
      mtNativeLocation: new URL(
        `https://native/webpreview/outer/_access/parent/parent-token${own}/next?__mtPreviewId=parent&__mtPreviewToken=parent-token`,
      ),
      window: { name: JSON.stringify(saved) },
    });
    expect(context).toEqual(saved);
  });
});

describe('shared preview custom checkable click protocol', () => {
  // Native .click is a boundary spy. Browser activation owns checked/radio-group
  // state, label forwarding, cancellation and input/change; do not model those here.
  function fixture() {
    const replies: { success: boolean; error: string | null }[] = [];
    const order: string[] = [];
    class Element {
      constructor(public tagName: string) {}
      type = 'checkbox';
      isConnected = true;
      nodeType = 1;
      hidden = false;
      inert = false;
      disabled = false;
      readOnly = false;
      ariaDisabled = false;
      ariaReadOnly = false;
      opacity = '1';
      visibility = 'visible';
      display = 'block';
      hasRect = true;
      parentElement: Element | null = null;
      control: Element | null = null;
      labels: Element[] = [];
      onIntent: (() => void) | null = null;
      rect = { left: 0, top: 0, width: 40, height: 40 };
      getClientRects() { return this.hasRect ? [this.rect] : []; }
      getBoundingClientRect() { return this.rect; }
      matches(selector: string) { return selector === ':disabled' && this.disabled; }
      closest(selector: string): Element | null {
        for (let node: Element | null = this; node; node = node.parentElement) {
          if (selector === 'label' && node.tagName === 'LABEL') return node;
          if (selector.includes('aria-disabled') && node.ariaDisabled) return node;
          if (selector.includes('readonly') && (node.ariaReadOnly || node.readOnly)) return node;
          if (selector.startsWith('a,button') && ['A', 'BUTTON', 'INPUT', 'SELECT', 'TEXTAREA'].includes(node.tagName)) return node;
        }
        return null;
      }
      contains(other: Element | null): boolean {
        for (let node = other; node; node = node.parentElement) if (node === this) return true;
        return false;
      }
      dispatchEvent = vi.fn((event: { type: string }) => {
        order.push(`${this.tagName}:${event.type}`);
        if (event.type === 'mousedown') this.onIntent?.();
        return true;
      });
      click = vi.fn(() => order.push(`${this.tagName}:click`));
      set checked(_: boolean) { throw new Error('checked state must be owned by native activation'); }
    }
    class IntentEvent {
      constructor(public type: string, options: object) { Object.assign(this, options); }
    }
    const input = new Element('INPUT');
    input.opacity = '0';
    const label = new Element('LABEL');
    label.control = input;
    input.labels = [label];
    let matches = [input];
    let hit: Element | null = label;
    const document = {
      querySelectorAll: () => matches,
      elementFromPoint: vi.fn(() => hit),
    };
    const dispatch = runInNewContext(
      `${section('function isControlVisible(el){', 'function handleBCmd(msg){')}
       ${section('function handleBCmd(msg){', 'var bwsReconnectTimer=')}
       handleBCmd;`,
      {
        document,
        getComputedStyle: (el: Element) => ({ opacity: el.opacity, visibility: el.visibility, display: el.display }),
        MouseEvent: IntentEvent,
        PointerEvent: IntentEvent,
        mtCtx: { sessionId: 'owned-session', previewId: 'owned-preview' },
        bws: { send: (value: string) => replies.push(JSON.parse(value)) },
      },
    ) as (message: object) => void;
    return {
      input, label, order, document,
      node: (tag: string) => new Element(tag),
      select: (...elements: Element[]) => { matches = elements; },
      hit: (element: Element | null) => { hit = element; },
      click() {
        dispatch({ id: 'click', command: 'click', selector: '.control' });
        return replies.at(-1)!;
      },
      fill() {
        dispatch({ id: 'fill', command: 'fill', selector: '.control', value: 'ignored' });
        return replies.at(-1)!;
      },
    };
  }

  it.each(['checkbox', 'radio'])('activates a transparent %s once through its visible associated label', (type) => {
    const f = fixture();
    f.input.type = type;
    expect(f.click()).toMatchObject({ success: true });
    expect(f.order).toEqual(['LABEL:pointerdown', 'LABEL:mousedown', 'LABEL:pointerup', 'LABEL:mouseup', 'LABEL:click']);
    expect(f.label.click).toHaveBeenCalledTimes(1);
    expect(f.input.click).not.toHaveBeenCalled();
  });
  it('uses the transparent input itself only when it is the actual pointer hit', () => {
    const f = fixture();
    f.input.labels = [];
    f.hit(f.input);
    expect(f.click()).toMatchObject({ success: true });
    expect(f.input.click).toHaveBeenCalledTimes(1);
    expect(f.label.click).not.toHaveBeenCalled();
    expect(f.input.dispatchEvent.mock.calls[0]![0]).toMatchObject({ type: 'pointerdown', clientX: 20, clientY: 20, bubbles: true });
  });
  it('supports explicit label and noninteractive label-child intent without double activation', () => {
    const f = fixture();
    f.select(f.label);
    expect(f.click()).toMatchObject({ success: true });
    expect(f.label.click).toHaveBeenCalledTimes(1);
    const text = f.node('SPAN');
    text.parentElement = f.label;
    f.select(text);
    f.hit(text);
    expect(f.click()).toMatchObject({ success: true });
    expect(text.click).toHaveBeenCalledTimes(1);
    expect(f.input.click).not.toHaveBeenCalled();
  });
  it.each(['hidden', 'display', 'visibility', 'ancestor-opacity', 'inert', 'detached', 'disabled', 'aria-disabled', 'readonly', 'aria-readonly', 'covered', 'unassociated', 'interactive-child', 'zero-size'])(
    'rejects %s custom controls before pointer intent or activation', (kind) => {
      const f = fixture();
      if (kind === 'hidden') f.input.hidden = true;
      if (kind === 'display') f.input.display = 'none';
      if (kind === 'visibility') f.input.visibility = 'hidden';
      if (kind === 'ancestor-opacity') {
        const parent = f.node('DIV'); parent.opacity = '0'; f.input.parentElement = parent;
      }
      if (kind === 'inert') f.input.inert = true;
      if (kind === 'detached') f.input.isConnected = false;
      if (kind === 'disabled') f.input.disabled = true;
      if (kind === 'aria-disabled') f.input.ariaDisabled = true;
      if (kind === 'readonly') f.input.readOnly = true;
      if (kind === 'aria-readonly') f.input.ariaReadOnly = true;
      if (kind === 'covered') f.hit(f.node('DIV'));
      if (kind === 'unassociated') f.label.control = null;
      if (kind === 'interactive-child') {
        const link = f.node('A'); link.parentElement = f.label; f.hit(link);
      }
      if (kind === 'zero-size') f.input.rect.width = 0;
      expect(f.click()).toMatchObject({ success: false });
      expect(f.order).toEqual([]);
    },
  );
  it('keeps ambiguous actionable selectors ambiguous even when one control is transparent', () => {
    const f = fixture();
    f.select(f.input, f.node('INPUT'));
    expect(f.click()).toMatchObject({ success: false, error: expect.stringContaining('ambiguous selector') });
    expect(f.order).toEqual([]);
  });
  it.each(['disabled', 'readonly'])('cannot bypass %s by selecting the associated label itself', (kind) => {
    const f = fixture();
    f.select(f.label);
    if (kind === 'disabled') f.input.disabled = true;
    else f.input.readOnly = true;
    expect(f.click()).toMatchObject({ success: false });
    expect(f.order).toEqual([]);
  });
  it('keeps an interactive link inside a label as its own action', () => {
    const f = fixture();
    const link = f.node('A');
    link.parentElement = f.label;
    f.input.readOnly = true;
    f.select(link);
    f.hit(link);
    expect(f.click()).toMatchObject({ success: true });
    expect(link.click).toHaveBeenCalledTimes(1);
    expect(f.label.click).not.toHaveBeenCalled();
    expect(f.input.click).not.toHaveBeenCalled();
  });
  it.each(['disabled', 'association', 'hidden', 'submit-type'])('revalidates %s after pointer intent and before native activation', (kind) => {
    const f = fixture();
    f.label.onIntent = () => {
      if (kind === 'disabled') f.input.disabled = true;
      if (kind === 'association') f.label.control = f.node('INPUT');
      if (kind === 'hidden') f.label.hidden = true;
      if (kind === 'submit-type') f.input.type = 'submit';
    };
    expect(f.click()).toMatchObject({ success: false });
    expect(f.label.click).not.toHaveBeenCalled();
    expect(f.input.click).not.toHaveBeenCalled();
  });
  it('does not extend hidden-element access to fill or non-checkable inputs', () => {
    const f = fixture();
    expect(f.fill()).toMatchObject({ success: false });
    f.input.type = 'text';
    expect(f.click()).toMatchObject({ success: false });
    expect(f.order).toEqual([]);
  });
});

describe('shared preview fill editing protocol', () => {
  // Exercise the injected dispatcher and guards. The native edit is a boundary spy,
  // not a browser/ProseMirror undo simulation; that acceptance belongs in the browser.
  function fixture() {
    const replies: { success: boolean; result: string | null; error: string | null }[] = [];
    const order: string[] = [];
    const selection = {
      removeAllRanges: vi.fn(() => order.push('clear-selection')),
      addRange: vi.fn(() => order.push('select')),
    };
    const target = {
      isConnected: true,
      isContentEditable: true,
      parentElement: null as { isContentEditable: boolean } | null,
      hidden: false,
      inert: false,
      disabled: false,
      readOnly: false,
      nonEditableChild: false,
      nodeType: 1,
      getClientRects: () => [{}],
      getBoundingClientRect: () => ({ left: 0, top: 0, width: 100, height: 30 }),
      matches: (selector: string): boolean => selector === ':disabled' && target.disabled,
      closest: (selector: string): object | null => {
        if (selector.includes('readonly') && target.readOnly) return target;
        return null;
      },
      querySelector: (): object | null => (target.nonEditableChild ? {} : null),
      dispatchEvent: vi.fn(() => true),
      focus: vi.fn((): void => {
        order.push('focus');
        document.activeElement = target;
      }),
      // Any direct content write fails this contract instead of mimicking editor state.
      set textContent(_: string) {
        throw new Error('direct textContent write');
      },
      set innerHTML(_: string) {
        throw new Error('HTML write');
      },
    };
    let matches = [target];
    const range = { selectNodeContents: vi.fn(() => order.push('range')) };
    const document = {
      activeElement: null as typeof target | null,
      querySelectorAll: () => matches,
      createRange: () => range,
      queryCommandSupported: vi.fn(() => true),
      execCommand: vi.fn((_command: string, _ui: boolean, _value: string) => {
        order.push('edit');
        return true;
      }),
    };
    const dispatch = runInNewContext(
      `${section('function previewSelectorParts(selector){', 'function queryBudget(){')}
       ${section('function isControlVisible(el){', dispatcherStart)}
       ${section(dispatcherStart, 'var bwsReconnectTimer=')}
       handleBCmd;`,
      {
        document,
        window: { getSelection: () => selection },
        getComputedStyle: () => ({ visibility: 'visible', display: 'block', opacity: '1' }),
        MouseEvent: class {},
        mtCtx: { sessionId: 'owned-session', previewId: 'owned-preview' },
        bws: { send: (value: string) => replies.push(JSON.parse(value)) },
      },
    ) as (message: object) => void;
    return {
      target,
      document,
      range,
      selection,
      order,
      duplicate: () => {
        matches = [target, target];
      },
      fill(value: string) {
        dispatch({ id: 'fill', command: 'fill', selector: '#editor', value });
        return replies.at(-1)!;
      },
    };
  }

  it.each(['Grüße 東京 👩🏽‍💻\n<em>literal</em>\t&', ' \t\n', ''])(
    'delegates literal %j as one native replacement after focusing and selecting only the host',
    (value) => {
      const f = fixture();
      expect(f.fill(value)).toMatchObject({ success: true, result: 'filled' });
      expect(f.range.selectNodeContents).toHaveBeenCalledWith(f.target);
      expect(f.order).toEqual(['focus', 'range', 'clear-selection', 'select', 'edit']);
      expect(f.document.execCommand).toHaveBeenCalledExactlyOnceWith(
        value === '' ? 'delete' : 'insertText',
        false,
        value,
      );
      // Only pointer intent is dispatched here; native editing owns the input event.
      expect(f.target.dispatchEvent).toHaveBeenCalledTimes(2);
    },
  );

  it.each([
    'readonly',
    'disabled',
    'hidden',
    'ambiguous',
    'nested',
    'noneditable',
    'noteditable',
    'unsupported',
  ])('rejects %s before focus, selection or native editing', (kind) => {
    const f = fixture();
    if (kind === 'readonly') f.target.readOnly = true;
    if (kind === 'disabled') f.target.disabled = true;
    if (kind === 'hidden') f.target.hidden = true;
    if (kind === 'ambiguous') f.duplicate();
    if (kind === 'nested') f.target.parentElement = { isContentEditable: true };
    if (kind === 'noneditable') f.target.nonEditableChild = true;
    if (kind === 'noteditable') f.target.isContentEditable = false;
    if (kind === 'unsupported') f.document.queryCommandSupported.mockReturnValue(false);
    expect(f.fill('never')).toMatchObject({ success: false });
    expect(f.target.focus).not.toHaveBeenCalled();
    expect(f.selection.addRange).not.toHaveBeenCalled();
    expect(f.document.execCommand).not.toHaveBeenCalled();
  });

  it('reports native rejection without falling back to DOM writes or duplicate input', () => {
    const f = fixture();
    f.document.execCommand.mockReturnValue(false);
    expect(f.fill('literal')).toMatchObject({ success: false });
    expect(f.document.execCommand).toHaveBeenCalledTimes(1);
    expect(f.target.dispatchEvent).toHaveBeenCalledTimes(2);
  });

  it('rechecks editability after activation before changing selection or text', () => {
    const f = fixture();
    f.target.focus.mockImplementation(() => {
      f.document.activeElement = f.target;
      f.target.readOnly = true;
    });
    expect(f.fill('never')).toMatchObject({ success: false });
    expect(f.selection.addRange).not.toHaveBeenCalled();
    expect(f.document.execCommand).not.toHaveBeenCalled();
  });
});

describe('opaque preview Web Storage protocol', () => {
  function fixture(seed: Record<string, string> = {}) {
    const fetch = vi.fn(async (_url: string, _init: RequestInit) => ({ ok: true, status: 204 }));
    const bootstrap = { token: 'fixture-storage-token', local: seed, session: {} };
    const code = section('function mtStoragePrefix(name){', 'function mkSwContainer(){').replace(
      '/*__MT_STORAGE_BOOTSTRAP__*/null',
      `JSON.parse(${JSON.stringify(JSON.stringify(bootstrap))})`,
    );
    const protocol = runInNewContext(
      `${code};({local:mkStore('localStorage'),session:mkStore('sessionStorage'),flush:mtFlushStorage,prefix:mtStoragePrefix})`,
      {
        P: '/webpreview/route',
        PP: '/webpreview/route/_access/fixture/id',
        mtNativeLocation: new URL('https://tlbx.invalid/webpreview/route/'),
        window: {
          fetch,
          __mtTargetOrigin: 'https://first.invalid',
          origin: 'null',
          localStorage: { getItem: () => 'stale outer-adapter memory', setItem: vi.fn() },
        },
        URL,
        DOMException,
        AbortController,
        setTimeout: () => 1,
        clearTimeout: () => undefined,
      },
    ) as {
      local: Storage;
      session: Storage;
      flush: () => Promise<void>;
      prefix: (name: string) => string;
    };
    return { ...protocol, fetch };
  }

  it('seeds reads synchronously and persists named-property TodoMVC writes', async () => {
    const f = fixture({ todos: '[1]' });
    expect(f.local.getItem('todos')).toBe('[1]');
    expect(f.local.todos).toBe('[1]');
    expect(f.fetch).not.toHaveBeenCalled();
    f.local.todos = '[1,2]';
    f.local.setItem('__proto__', 'literal');
    expect(Object.keys(f.local)).toEqual(['todos', '__proto__']);
    expect(f.local.length).toBe(2);
    await f.flush();
    const request = JSON.parse(String(f.fetch.mock.calls[0]?.[1].body));
    expect(request.operations).toEqual([
      { store: 'local', action: 'set', key: 'todos', value: '[1,2]' },
      { store: 'local', action: 'set', key: '__proto__', value: 'literal' },
    ]);
    expect(f.prefix('localStorage')).toContain('https%3A%2F%2Ffirst.invalid');
  });

  it('treats special seed keys and HTML-looking values as literal storage data', () => {
    const seed = Object.fromEntries([['__proto__', '</script><script>literal</script>']]);
    const f = fixture(seed);
    expect(f.local.getItem('__proto__')).toBe('</script><script>literal</script>');
    expect(Object.keys(f.local)).toEqual(['__proto__']);
  });

  it('coalesces pending keys and orders a later batch behind the in-flight acknowledgement', async () => {
    const f = fixture();
    let acknowledge: ((response: { ok: boolean; status: number }) => void) | undefined;
    f.fetch.mockImplementationOnce(
      () =>
        new Promise<{ ok: boolean; status: number }>((resolve) => {
          acknowledge = resolve;
        }),
    );
    f.local.todos = 'first';
    const flushing = f.flush();
    f.local.todos = 'second';
    f.local.todos = 'last';
    expect(f.fetch).toHaveBeenCalledTimes(1);
    acknowledge?.({ ok: true, status: 204 });
    await flushing;
    expect(f.fetch).toHaveBeenCalledTimes(2);
    const batches = f.fetch.mock.calls.map((call) => JSON.parse(String(call[1].body)));
    expect(batches.map((batch) => batch.sequence)).toEqual([1, 2]);
    expect(batches[1].operations).toEqual([
      { store: 'local', action: 'set', key: 'todos', value: 'last' },
    ]);
  });

  it('retries the same batch after an uncertain response and keeps local and tab storage separate', async () => {
    const f = fixture();
    f.local.todos = 'persistent';
    f.session.todos = 'this-tab';
    f.fetch.mockRejectedValueOnce(new Error('disconnected'));
    await expect(f.flush()).rejects.toThrow('storage connection failed');
    await f.flush();
    expect(f.fetch.mock.calls[0]?.[1].body).toBe(f.fetch.mock.calls[1]?.[1].body);
    expect(f.local.todos).toBe('persistent');
    expect(f.session.todos).toBe('this-tab');
    expect(() => f.local.setItem('huge', 'x'.repeat(65536))).toThrow(/quota/i);
    expect(f.local.getItem('huge')).toBeNull();
  });

  it('orders clear, property deletion and later writes without preserving discarded keys', async () => {
    const f = fixture({ old: 'value' });
    f.local.temporary = 'discard';
    f.local.clear();
    f.local.todos = 'keep';
    delete f.local.todos;
    f.local.todos = 'final';
    await f.flush();
    expect(JSON.parse(String(f.fetch.mock.calls[0]?.[1].body)).operations).toEqual([
      { store: 'local', action: 'clear' },
      { store: 'local', action: 'set', key: 'todos', value: 'final' },
    ]);
    expect(Object.keys(f.local)).toEqual(['todos']);
  });

  it('bounds pending tombstones even when the live store stays empty', () => {
    const f = fixture();
    for (let i = 0; i < 258; i++) {
      f.local.setItem(`temporary-${i}`, 'value');
      f.local.removeItem(`temporary-${i}`);
    }
    expect(() => f.local.setItem('overflow', 'not committed')).toThrow(/quota/i);
    expect(f.local.length).toBe(0);
    expect(f.local.getItem('overflow')).toBeNull();
  });

  it.each([true, false])(
    'awaits the flush barrier before controlled reload (success=%s)',
    async (success) => {
      const reload = vi.fn();
      const replies: { success: boolean; error?: string }[] = [];
      let release: (() => void) | undefined;
      const barrier = new Promise<void>((resolve) => {
        release = resolve;
      });
      const dispatch = runInNewContext(
        `${section('async function handleBCmd(msg){', 'var bwsReconnectTimer=')};handleBCmd;`,
        {
          mtFlushStorage: async () => {
            await barrier;
            if (!success) throw new Error('storage unavailable');
          },
          mtCtx: null,
          mtNativeLocation: { reload },
          bws: { send: (value: string) => replies.push(JSON.parse(value)) },
        },
      ) as (message: object) => Promise<void>;
      const pending = dispatch({ id: 'reload', command: 'reload', value: 'soft' });
      expect(reload).not.toHaveBeenCalled();
      release?.();
      await pending;
      expect(reload).toHaveBeenCalledTimes(success ? 1 : 0);
      expect(replies[0]?.success).toBe(success);
    },
  );
});

describe('explicit open-shadow selector and serialization protocol', () => {
  // This fixture models root boundaries, not a CSS engine or browser rendering.
  // Execute the injected resolver/guards/serializer against those boundaries.
  function fixture() {
    const setup = String.raw`
      function append(parent,child){parent.childNodes.push(child);child.parentNode=parent;child.parentElement=parent.nodeType===1?parent:null;return child;}
      function descendants(root){var out=[];for(var child of root.childNodes||[]){if(child.nodeType===1)out.push(child);out.push(...descendants(child));}return out;}
      function root(){return {nodeType:11,mode:'open',childNodes:[],get children(){return this.childNodes.filter(n=>n.nodeType===1);},querySelectorAll:query,getElementById:function(id){return id?descendants(this).find(n=>n.id===id)||null:null;}};}
      function query(selector){
        if(selector==='[')throw new Error('invalid native CSS');
        if(this.queries)this.queries.push(selector);
        return descendants(this).filter(n=>selector==='*'||n.matches(selector));
      }
      function element(tag,id,text){
        var attrs=new Map();if(id)attrs.set('id',id);
        var el={nodeType:1,localName:tag,tagName:tag.toUpperCase(),childNodes:[],attrs:attrs,css:{},hidden:false,inert:false,isConnected:true,
          get id(){return attrs.get('id')||'';},get children(){return this.childNodes.filter(n=>n.nodeType===1);},
          get classList(){var list=(attrs.get('class')||'').split(' ').filter(Boolean);return {length:list.length,item:i=>list[i]};},
          get attributes(){return Array.from(attrs,([name,value])=>({name,value}));},
          getAttribute:k=>attrs.get(k)??null,hasAttribute:k=>attrs.has(k),
          getRootNode:function(){var n=this;while(n.parentNode)n=n.parentNode;return n;},
          querySelectorAll:query,querySelector:function(s){return query.call(this,s)[0]||null;},
          closest:function(s){for(var n=this;n;n=n.parentElement)if(n.matches(s))return n;return null;},
          matches:function(s){return s[0]==='#'?this.id===s.slice(1):s[0]==='.'?(attrs.get('class')||'').split(' ').includes(s.slice(1)):s==='[aria-disabled="true"]'?attrs.get('aria-disabled')==='true':s===tag;},
          getClientRects:()=>[{}],getBoundingClientRect:()=>({left:0,top:0,width:100,height:30}),
          cloneNode:()=>{throw new Error('cloned in the inspected document');}
        };
        if(text)append(el,{nodeType:3,data:text,childNodes:[]});
        Object.defineProperty(el,'outerHTML',{get:()=>serialize(el)});
        return el;
      }
      function escape(text){return String(text).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/"/g,'&quot;');}
      function serialize(node){
        if(node.nodeType===3)return escape(node.data);
        if(node.nodeType===8)return '<!--'+node.data+'-->';
        var attrs=Array.from(node.attrs||[],([k,v])=>' '+k+'="'+escape(v)+'"').join('');
        return '<'+node.localName+attrs+'>'+(node.content||node).childNodes.map(serialize).join('')+'</'+node.localName+'>';
      }
      var imports=0;
      function copy(tag,attrs){
        var el=element(tag);el.cloneNode=undefined;
        for(var [k,v] of attrs||[])el.attrs.set(k,v);
        el.setAttribute=(k,v)=>el.attrs.set(k,v);el.removeAttribute=k=>el.attrs.delete(k);
        el.appendChild=n=>append(el,n);
        if(tag==='template'){el.content=root();el.content.appendChild=n=>append(el.content,n);}
        return el;
      }
      var inert={importNode:n=>{imports++;return copy(n.localName,n.attrs);},createElement:tag=>copy(tag),
        createComment:data=>({nodeType:8,data}),createTextNode:data=>({nodeType:3,data})};
      var document=root();document.nodeType=9;document.queries=[];
      document.implementation={createHTMLDocument:()=>inert};
      document.createDocumentFragment=()=>({querySelectorAll:s=>{if(s==='[')throw new Error('invalid native CSS');return [];}});
      document.createTreeWalker=el=>{var nodes=[],i=0;function walk(n){for(var c of n.childNodes||[]){nodes.push(c);walk(c);}}walk(el);return {nextNode:()=>nodes[i++]||null};};
      var html=append(document,element('html'));document.documentElement=html;
      var left=append(html,element('div','left'));left.attrs.set('class','host');left.shadowRoot=root();left.shadowRoot.host=left;
      var action=append(left.shadowRoot,element('button','action','Shadow control ✓'));
      var nested=append(left.shadowRoot,element('div','nested'));nested.shadowRoot=root();nested.shadowRoot.host=nested;
      var deep=append(nested.shadowRoot,element('button','deep','Deep control'));
      var right=append(html,element('div','right'));right.attrs.set('class','host');right.shadowRoot=root();right.shadowRoot.host=right;
      append(right.shadowRoot,element('button','action','Other control'));
      var plain=append(html,element('button','plain','Light control'));
      document.activeElement=left;left.shadowRoot.activeElement=action;
      var CSS={escape:s=>s},NodeFilter={SHOW_ALL:0};
      function getComputedStyle(el){return Object.assign({display:'block',visibility:'visible',opacity:'1',overflowX:'visible',overflowY:'visible',contentVisibility:'visible'},el.css);}
    `;
    return runInNewContext(
      `${setup}
       ${section('function previewSelectorParts(selector){', 'function settleHiddenCaptureTransitions(){')}
       ${section('function isControlVisible(el){', dispatcherStart)}
       ({parts:previewSelectorParts,find:previewQueryAll,control:resolveControl,outline:semanticOutline,snapshot:previewSnapshot,text:el=>queryText(el,queryBudget()),
         html:()=>html.outerHTML,host:left,action:action,deep:deep,plain:plain,queries:document.queries,
         hide:()=>left.css.opacity='0',disable:()=>left.attrs.set('aria-disabled','true'),
         close:()=>left.shadowRoot.mode='closed',closeAll:()=>{left.shadowRoot=null;right.shadowRoot=null;},imports:()=>imports,
         longAttribute:()=>left.attrs.set('data-long','x'.repeat(5000)),
         truncated:()=>{var budget=queryBudget();return {html:truncEl(left,1,budget),budget};}});`,
    ) as {
      parts: (selector: string) => string[];
      find: (selector: string) => unknown[];
      control: (selector: string) => unknown;
      outline: (root: unknown, depth: number) => { text: string; count: number };
      snapshot: () => string;
      text: (root: unknown) => string;
      html: () => string;
      host: unknown;
      action: unknown;
      deep: unknown;
      plain: unknown;
      queries: string[];
      hide: () => void;
      disable: () => void;
      close: () => void;
      closeAll: () => void;
      imports: () => number;
      longAttribute: () => void;
      truncated: () => { html: string; budget: { truncated: boolean } };
    };
  }

  it('splits only explicit boundaries outside CSS quotes, brackets, functions, comments and escapes', () => {
    const f = fixture();
    expect(f.parts('[data-note=">>>"]:is(.a,.b) >>> button')).toEqual([
      '[data-note=">>>"]:is(.a,.b)',
      'button',
    ]);
    expect(f.parts(String.raw`#escaped\>\>\> /* >>> */ >>> [title='a\'>>>b']`)).toEqual([
      String.raw`#escaped\>\>\> /* >>> */`,
      String.raw`[title='a\'>>>b']`,
    ]);
    expect(() => f.parts('#left >>> >>> button')).toThrow(/empty|unbalanced/);
    expect(() => f.parts('#left >>> [title="unfinished')).toThrow(/unbalanced/);
    expect(() => f.find('#missing >>> [')).toThrow(/unbalanced|native CSS/);
    expect(() => f.parts(Array(33).fill('host').join(' >>> '))).toThrow(/32 CSS segments/);
  });

  it('keeps normal CSS in the document and enters only explicit open roots', () => {
    const f = fixture();
    expect(f.find('#action')).toHaveLength(0);
    expect(f.find('#plain')).toEqual([f.plain]);
    expect(f.find('#left >>> #action')).toEqual([f.action]);
    expect(f.find('#left >>> #nested >>> button')).toEqual([f.deep]);
    expect(f.find('.host >>> button')).toHaveLength(2);
    f.close();
    expect(f.find('#left >>> #action')).toHaveLength(0);
    expect(f.snapshot()).not.toContain('Shadow control ✓');
  });

  it('retains ambiguity and host visibility/disabled guards across the boundary', () => {
    const f = fixture();
    expect(() => f.control('.host >>> button')).toThrow(/ambiguous/);
    expect(f.control('#left >>> button')).toBe(f.action);
    f.disable();
    expect(() => f.control('#left >>> button')).toThrow(/disabled/);
    const hidden = fixture();
    hidden.hide();
    expect(() => hidden.control('#left >>> button')).toThrow(/no visible/);
  });

  it('emits distinct actionable selectors for duplicate IDs in different roots', () => {
    const f = fixture();
    const outline = f.outline(f.host, 4);
    expect(outline.text).toContain('open-shadow-root');
    expect(outline.text).toContain('Shadow control ✓');
    expect(outline.text).toContain('selector="#left >>> #action"');
    expect(outline.text).toContain('selector="#left >>> #nested >>> #deep"');
    expect(outline.text).toContain('focused');
    expect(f.find('#left >>> #action')).toEqual([f.action]);
    expect(f.text(f.host)).toContain('Deep control');
    expect(f.outline(f.host, 0).text).toContain('outline truncated');
  });

  it('serializes open roots as inert declarative templates without clipping full snapshots', () => {
    const f = fixture();
    f.longAttribute();
    const snapshot = f.snapshot();
    expect(snapshot).toContain(
      '<template shadowrootmode="open"><button id="action">Shadow control ✓</button>',
    );
    expect(snapshot).toContain('x'.repeat(5000));
    expect(f.imports()).toBeGreaterThan(0);
    const bounded = f.truncated();
    expect(bounded.html).toContain('shadowrootmode="open"');
    expect(bounded.budget.truncated).toBe(true);
  });

  it('keeps the native full-snapshot path when no open root is available', () => {
    const f = fixture();
    f.closeAll();
    expect(f.snapshot()).toBe(f.html());
    expect(f.imports()).toBe(0);
  });

  it.each([false, true])(
    'uses the shared selector for element screenshots and rejects selector/fullPage conflicts (%s)',
    async (fullPage) => {
      const target = {};
      const select = vi.fn((_selector: string) => [target]);
      const capture = vi.fn(async (_target: unknown, _options: unknown) => ({
        toCanvas: async () => ({ toDataURL: () => 'data:image/png;base64,fixture' }),
      }));
      const reply = await new Promise<{ success: boolean; error?: string }>((resolve) => {
        const dispatch = runInNewContext(
          `${section(dispatcherStart, 'var bwsReconnectTimer=')};handleBCmd;`,
          {
            document: { documentElement: {} },
            window: {},
            mtCtx: null,
            previewQueryAll: select,
            isControlVisible: () => true,
            settleHiddenCaptureTransitions: () => undefined,
            captureBackground: () => undefined,
            ensureCapture: async () => capture,
            WebSocket: { OPEN: 1 },
            bws: { readyState: 1, send: (value: string) => resolve(JSON.parse(value)) },
          },
        ) as (message: object) => void;
        dispatch({ id: 'capture', command: 'screenshot', selector: '#left >>> button', fullPage });
      });
      expect(reply.success).toBe(!fullPage);
      if (fullPage) expect(capture).not.toHaveBeenCalled();
      else {
        expect(select).toHaveBeenCalledExactlyOnceWith('#left >>> button');
        expect(capture).toHaveBeenCalledWith(target, expect.objectContaining({ clip: null }));
      }
    },
  );
});
