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
