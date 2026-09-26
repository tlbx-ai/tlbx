import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { describe, expect, it } from 'vitest';

const source = readFileSync(
  new URL('../../../../Services/WebPreview/WebPreviewProxyMiddleware.cs', import.meta.url),
  'utf8',
);
const network = source.slice(
  source.indexOf('var F=window.fetch;'),
  source.indexOf('// === Element property setters'),
);
const cookies = source.slice(
  source.indexOf('var cc=/*__MT_INITIAL_COOKIES__*/'),
  source.indexOf('// === MutationObserver'),
);

function harness(initial = 'theme=dark') {
  const requests: { requestId: string; action: string; raw: string }[] = [];
  const sent: string[] = [];
  let listener: (event: { data: object }) => void = () => {};
  let nativeWrites = 0;
  class FakeDocument {
    get cookie() {
      return 'otherPreview=private';
    }
    set cookie(_value: string) {
      nativeWrites++;
    }
  }
  class FakeXhr {
    open() {}
    send() {
      sent.push('xhr');
    }
    abort() {}
    addEventListener() {}
    removeEventListener() {}
    dispatchEvent() {}
  }
  const document = new FakeDocument();
  const window = {
    fetch: async () => {
      sent.push(document.cookie);
      return { ok: true };
    },
    addEventListener: (_type: string, handler: typeof listener) => {
      listener = handler;
    },
  };
  runInNewContext(
    network + cookies.replace('/*__MT_INITIAL_COOKIES__*/""', JSON.stringify(initial)),
    {
      window,
      document,
      Document: FakeDocument,
      HTMLDocument: FakeDocument,
      XMLHttpRequest: FakeXhr,
      navigator: {},
      mtCtx: null,
      PP: '/webpreview/test',
      mtMsg: (type: string, data: object) => ({ type, ...data }),
      _realParent: { postMessage: (message: (typeof requests)[number]) => requests.push(message) },
      curU: () => 'https://example.com/app',
      r: (url: string) => url,
      setTimeout: () => 0,
    },
  );
  const respond = (index: number, header: string) =>
    listener({
      data: {
        type: 'mt-cookie-response',
        requestId: requests[index].requestId,
        header,
      },
    });
  return { document, window, requests, sent, respond, FakeXhr, nativeWrites: () => nativeWrites };
}

const flush = async () => {
  for (let i = 0; i < 12; i++) await Promise.resolve();
};

describe('proxy cookie ordering', () => {
  it('provides scoped startup cookies without falling back to the shared native jar', () => {
    const h = harness();
    expect(h.document.cookie).toBe('theme=dark');
    h.document.cookie = 'campaign=one; Path=/';
    expect(h.nativeWrites()).toBe(0);
    expect(h.document.cookie).not.toContain('otherPreview');
  });

  it('holds fetch until a write is confirmed and ignores an older refresh', async () => {
    const h = harness();
    h.document.cookie = 'campaign=one; Path=/';
    const fetched = h.window.fetch();
    await flush();
    expect(h.sent).toEqual([]);
    h.respond(0, 'theme=old');
    await flush();
    expect(h.document.cookie).toContain('campaign=one');
    h.respond(1, 'theme=dark; campaign=one');
    await fetched;
    expect(h.sent).toEqual(['theme=dark; campaign=one']);
  });

  it('serializes rapid writes and forwards only after both complete', async () => {
    const h = harness('');
    h.document.cookie = 'campaign=one';
    h.document.cookie = 'campaign=two';
    const fetched = h.window.fetch();
    await flush();
    expect(h.requests.filter((r) => r.action === 'set')).toHaveLength(1);
    h.respond(1, 'campaign=one');
    await flush();
    expect(h.document.cookie).toBe('campaign=two');
    expect(h.sent).toEqual([]);
    h.respond(2, 'campaign=two');
    await fetched;
    expect(h.sent).toEqual(['campaign=two']);
  });

  it('does not send an aborted XHR after its cookie barrier clears', async () => {
    const h = harness();
    h.document.cookie = 'campaign=one';
    const xhr = new h.FakeXhr();
    xhr.open();
    xhr.send();
    xhr.abort();
    await flush();
    h.respond(1, 'campaign=one');
    await flush();
    expect(h.sent).toEqual([]);
  });
});
