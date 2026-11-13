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
      `${section('function isControlVisible(el){', 'function handleBCmd(msg){')}
       ${section('function handleBCmd(msg){', 'var bwsReconnectTimer=')}
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
