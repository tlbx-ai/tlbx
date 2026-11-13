import { readFileSync } from 'node:fs';
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
const protocol = [
  section('function mtProxyPrefix(){', 'var P="/webpreview"'),
  'var BP=mtProxyPrefix(),PP=BP+P,E=PP+"/_ext?u=";',
  section('function syncProxyBase(){', '          syncProxyBase();'),
  section('function ar(u){', 'function frameUrl(u){'),
  section('function mtProxyUrl(u,p){', '// === Network APIs ==='),
  section('function mtUpstreamSearch(search){', '// Application Location is upstream-facing;'),
  section('function mtNavigationUrl(value){', 'var mtLocationView={};'),
].join('\n');
const route = '0123456789abcdefabcd';
const nativePrefix = `/webpreview/${route}`;
const upstream = 'http://127.0.0.1:53836';

function exercise(virtualPrefix: string, suffix: string, baseSuffix = '/docs/') {
  const location = new URL(`https://preview.example${virtualPrefix}${nativePrefix}${suffix}`);
  let base = nativePrefix + baseSuffix;
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
    P: nativePrefix,
    location,
    mtNativeLocation: location,
    document,
    URL,
    URLSearchParams,
    window: { __mtTargetOrigin: upstream, __mtDocumentUrl: `${upstream}/docs/page.html` },
    mtReloadToken: '',
    mtNestedPreview: false,
  };
  const result = runInNewContext(
    `${protocol}\nsyncProxyBase();({curU,r,mtNavigationUrl})`,
    context,
  ) as {
    curU(): string;
    r(value: string): string;
    mtNavigationUrl(value: string): string;
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
    expect(result.curU()).toBe('https://other.example/docs?q=a%20b#changed');
  });
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
      `${section('function isControlVisible(el){', 'async function handleBCmd(msg){')}
       ${section('async function handleBCmd(msg){', 'var bwsReconnectTimer=')}
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
