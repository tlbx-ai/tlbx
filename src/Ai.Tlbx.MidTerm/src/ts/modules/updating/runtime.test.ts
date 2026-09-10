import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
  reloadAppShell: vi.fn(),
  setInterval: vi.fn<typeof setInterval>(),
  clearInterval: vi.fn<typeof clearInterval>(),
  setTimeout: vi.fn<typeof setTimeout>(),
  clearTimeout: vi.fn<typeof clearTimeout>(),
  fetch: vi.fn<typeof fetch>(),
}));

vi.mock('../logging', () => ({
  createLogger: () => ({
    info: vi.fn(),
  }),
}));

vi.mock('../i18n', () => ({
  t: (key: string) => key,
}));

vi.mock('./appShellState', () => ({
  reloadAppShell: mocks.reloadAppShell,
}));

type FakeElement = {
  className: string;
  innerHTML: string;
  removed?: boolean;
  querySelector: <T>(selector: string) => T | null;
  remove: () => void;
};

function createOverlayElement(): FakeElement {
  return {
    className: '',
    innerHTML: '',
    querySelector: () => null,
    remove() {
      this.removed = true;
    },
  };
}

async function flushAsyncWork(iterations = 4): Promise<void> {
  for (let i = 0; i < iterations; i += 1) {
    await Promise.resolve();
  }
}

describe('update runtime', () => {
  const originalDocument = globalThis.document;
  const originalFetch = globalThis.fetch;
  const originalSetInterval = globalThis.setInterval;
  const originalClearInterval = globalThis.clearInterval;
  const originalSetTimeout = globalThis.setTimeout;
  const originalClearTimeout = globalThis.clearTimeout;
  const originalWindow = (globalThis as typeof globalThis & { window?: Window }).window;

  beforeEach(() => {
    vi.resetModules();
    mocks.reloadAppShell.mockReset();
    mocks.clearInterval.mockReset();
    mocks.clearTimeout.mockReset();
    mocks.fetch.mockReset();

    let nextTimerId = 1;
    mocks.setInterval.mockImplementation(() => nextTimerId++ as ReturnType<typeof setInterval>);
    mocks.setTimeout.mockImplementation(((callback: TimerHandler, delay?: number) => {
      if (delay === 150 && typeof callback === 'function') {
        callback();
      }

      return nextTimerId++ as ReturnType<typeof setTimeout>;
    }) as typeof setTimeout);

    globalThis.document = {
      body: {
        appendChild: vi.fn(),
      },
      createElement: () => createOverlayElement(),
    } as unknown as Document;

    globalThis.fetch = mocks.fetch;
    globalThis.setInterval = mocks.setInterval;
    globalThis.clearInterval = mocks.clearInterval;
    globalThis.setTimeout = mocks.setTimeout;
    globalThis.clearTimeout = mocks.clearTimeout;
    vi.stubGlobal('window', globalThis);
  });

  afterEach(() => {
    globalThis.document = originalDocument;
    globalThis.fetch = originalFetch;
    globalThis.setInterval = originalSetInterval;
    globalThis.clearInterval = originalClearInterval;
    globalThis.setTimeout = originalSetTimeout;
    globalThis.clearTimeout = originalClearTimeout;
    if (originalWindow) {
      vi.stubGlobal('window', originalWindow);
    } else {
      vi.unstubAllGlobals();
    }
    vi.restoreAllMocks();
  });

  it('reloads after an update only when the expected server version is live', async () => {
    mocks.fetch.mockResolvedValueOnce({ ok: true } as Response).mockResolvedValueOnce({
      ok: true,
      text: vi.fn().mockResolvedValue('1.2.3'),
    } as unknown as Response);

    const runtime = await import('./runtime');
    runtime.beginServerRestartLifecycle('update', {
      updateType: 'webOnly',
      expectedServerVersion: '1.2.3',
    });

    await flushAsyncWork();

    expect(mocks.reloadAppShell).toHaveBeenCalledTimes(1);
  });

  it('waits when health is back but the old server version is still responding', async () => {
    mocks.fetch.mockResolvedValueOnce({ ok: true } as Response).mockResolvedValueOnce({
      ok: true,
      text: vi.fn().mockResolvedValue('1.2.2'),
    } as unknown as Response);

    const runtime = await import('./runtime');
    runtime.beginServerRestartLifecycle('update', {
      updateType: 'webOnly',
      expectedServerVersion: '1.2.3',
    });

    await flushAsyncWork();

    expect(mocks.reloadAppShell).not.toHaveBeenCalled();
  });

  it('waits for a real restart when reinstalling without a new version number', async () => {
    mocks.fetch.mockResolvedValue({ ok: true } as Response);
    const runtime = await import('./runtime');
    runtime.beginServerRestartLifecycle('update', { updateType: 'full' });
    await flushAsyncWork();
    expect(mocks.reloadAppShell).not.toHaveBeenCalled();

    const poll = mocks.setInterval.mock.calls.at(-1)?.[0] as () => Promise<void>;
    mocks.fetch.mockRejectedValueOnce(new Error('server restarting'));
    await poll();
    await flushAsyncWork();
    expect(mocks.reloadAppShell).not.toHaveBeenCalled();
    await poll();
    await flushAsyncWork();
    expect(mocks.reloadAppShell).toHaveBeenCalledTimes(1);
  });
});
