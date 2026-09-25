import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { LayoutNode } from '../../types';

const mocks = vi.hoisted(() => ({
  createTerminalForSession: vi.fn(),
  setSuppressLayoutAutoFit: vi.fn(),
  fetch: vi.fn(),
}));

vi.mock('../terminal/manager', () => ({
  createTerminalForSession: mocks.createTerminalForSession,
}));

vi.mock('../../state', () => ({
  sessionTerminals: new Map<string, unknown>(),
  setSuppressLayoutAutoFit: mocks.setSuppressLayoutAutoFit,
}));

async function loadHarness() {
  vi.resetModules();
  vi.useFakeTimers();
  vi.stubGlobal('fetch', mocks.fetch);

  const localStorageData = new Map<string, string>();
  vi.stubGlobal('localStorage', {
    getItem: vi.fn((key: string) => localStorageData.get(key) ?? null),
    setItem: vi.fn((key: string, value: string) => {
      localStorageData.set(key, value);
    }),
    removeItem: vi.fn((key: string) => {
      localStorageData.delete(key);
    }),
  });

  mocks.createTerminalForSession.mockReset();
  mocks.setSuppressLayoutAutoFit.mockReset();
  mocks.fetch.mockReset();
  mocks.fetch.mockResolvedValue({
    ok: true,
    status: 200,
    json: async () => ({ revision: 1, root: null, focusedSessionId: null }),
  });

  const stores = await import('../../stores');
  stores.$layout.set({ root: null });
  stores.$focusedSessionId.set(null);
  stores.$activeSessionId.set(null);
  stores.$sessions.set({
    'session-a': { id: 'session-a', cols: 120, rows: 30 } as any,
    'session-b': { id: 'session-b', cols: 120, rows: 30 } as any,
  });

  const layoutStore = await import('./layoutStore');
  return { stores, layoutStore };
}

function buildHorizontalLayout(): LayoutNode {
  return {
    type: 'split',
    direction: 'horizontal',
    children: [
      { type: 'leaf', sessionId: 'session-a' },
      { type: 'leaf', sessionId: 'session-b' },
    ],
  };
}

it('does not reintroduce a closing pane or focus it from a newer server layout', async () => {
  const { stores, layoutStore } = await loadHarness();
  stores.markSessionClosing('session-a');
  try {
    layoutStore.applyServerLayoutState({
      revision: 10,
      root: buildHorizontalLayout(),
      focusedSessionId: 'session-a',
    });
    expect(stores.$layout.get().root).toEqual({ type: 'leaf', sessionId: 'session-b' });
    expect(stores.$activeSessionId.get()).toBe('session-b');
  } finally {
    stores.cancelSessionClosing('session-a');
  }
});

function buildVerticalLayout(): LayoutNode {
  return {
    type: 'split',
    direction: 'vertical',
    children: [
      { type: 'leaf', sessionId: 'session-a' },
      { type: 'leaf', sessionId: 'session-b' },
    ],
  };
}

describe('layoutStore server sync', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('ignores stale server snapshots while a local layout change is pending', async () => {
    const { stores, layoutStore } = await loadHarness();
    layoutStore.applyServerLayoutState({ revision: 1, root: null, focusedSessionId: null });
    layoutStore.initLayoutPersistence();
    layoutStore.markLayoutPersistenceReady();

    const optimisticLayout = buildHorizontalLayout();
    stores.$layout.set({ root: optimisticLayout });
    stores.$focusedSessionId.set('session-b');

    layoutStore.applyServerLayoutState({ revision: 1, root: null, focusedSessionId: null });

    expect(stores.$layout.get().root).toEqual(optimisticLayout);
    expect(stores.$focusedSessionId.get()).toBe('session-b');
  });

  it('does not let an older server ack overwrite a newer pending local layout edit', async () => {
    const { stores, layoutStore } = await loadHarness();
    layoutStore.applyServerLayoutState({ revision: 1, root: null, focusedSessionId: null });
    layoutStore.initLayoutPersistence();
    layoutStore.markLayoutPersistenceReady();

    const olderLayout = buildHorizontalLayout();
    const newerLayout = buildVerticalLayout();
    stores.$layout.set({ root: olderLayout });
    stores.$focusedSessionId.set('session-b');
    stores.$layout.set({ root: newerLayout });

    layoutStore.applyServerLayoutState({
      revision: 2,
      root: olderLayout,
      focusedSessionId: 'session-b',
    });

    expect(stores.$layout.get().root).toEqual(newerLayout);
    expect(stores.$focusedSessionId.get()).toBe('session-b');
  });

  it('docks onto AppServerControl sessions without creating a terminal surface for the AppServerControl target', async () => {
    const { stores, layoutStore } = await loadHarness();
    stores.$sessions.set({
      'terminal-session': {
        id: 'terminal-session',
        cols: 120,
        rows: 30,
        appServerControlOnly: false,
      } as any,
      'appServerControl-session': {
        id: 'appServerControl-session',
        cols: 0,
        rows: 0,
        appServerControlOnly: true,
      } as any,
    });

    layoutStore.dockSession('appServerControl-session', 'terminal-session', 'left');

    expect(mocks.createTerminalForSession).toHaveBeenCalledTimes(1);
    expect(mocks.createTerminalForSession).toHaveBeenCalledWith(
      'terminal-session',
      expect.objectContaining({ id: 'terminal-session' }),
    );
    expect(stores.$layout.get().root).toEqual({
      type: 'split',
      direction: 'horizontal',
      children: [
        { type: 'leaf', sessionId: 'terminal-session' },
        { type: 'leaf', sessionId: 'appServerControl-session' },
      ],
    });
  });

  it.each([1, 2])(
    'settles an identical layout ACK at revision %i without resending',
    async (revision) => {
      const { stores, layoutStore } = await loadHarness();
      layoutStore.applyServerLayoutState({ revision: 1, root: null, focusedSessionId: null });
      mocks.fetch.mockImplementation(async (_url, init) => ({
        ok: true,
        status: 200,
        json: async () => ({ ...JSON.parse(init.body), revision }),
      }));
      layoutStore.initLayoutPersistence();
      layoutStore.markLayoutPersistenceReady();
      stores.$layout.set({ root: buildHorizontalLayout() });
      stores.$focusedSessionId.set('session-b');
      await vi.advanceTimersByTimeAsync(100);
      expect(mocks.fetch).toHaveBeenCalledTimes(1);
      await vi.advanceTimersByTimeAsync(60_000);
      expect(mocks.fetch).toHaveBeenCalledTimes(1);
    },
  );

  it('remembers revisions even when the server layout already matches locally', async () => {
    const { stores, layoutStore } = await loadHarness();
    layoutStore.applyServerLayoutState({ revision: 27, root: null, focusedSessionId: null });
    layoutStore.initLayoutPersistence();
    layoutStore.markLayoutPersistenceReady();
    stores.$layout.set({ root: buildHorizontalLayout() });
    await vi.advanceTimersByTimeAsync(1);
    expect(JSON.parse(mocks.fetch.mock.calls[0]![1].body).revision).toBe(27);
  });

  it('accepts canonical normalization of the acknowledged edit', async () => {
    const { stores, layoutStore } = await loadHarness();
    const root = buildHorizontalLayout();
    mocks.fetch.mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({ revision: 1, root, focusedSessionId: 'session-a' }),
    });
    layoutStore.initLayoutPersistence();
    layoutStore.markLayoutPersistenceReady();
    stores.$layout.set({ root });
    await vi.advanceTimersByTimeAsync(100);
    expect(stores.$focusedSessionId.get()).toBe('session-a');
    expect(mocks.fetch).toHaveBeenCalledTimes(1);
  });

  it('sends the newer local edit after a delayed older ACK, using the acknowledged revision', async () => {
    const { stores, layoutStore } = await loadHarness();
    let acknowledge!: (value: unknown) => void;
    mocks.fetch.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          acknowledge = resolve;
        }),
    );
    mocks.fetch.mockImplementation(async (_url, init) => ({
      ok: true,
      status: 200,
      json: async () => ({ ...JSON.parse(init.body), revision: 2 }),
    }));
    layoutStore.initLayoutPersistence();
    layoutStore.markLayoutPersistenceReady();
    stores.$layout.set({ root: buildHorizontalLayout() });
    await vi.advanceTimersByTimeAsync(1);
    const old = JSON.parse(mocks.fetch.mock.calls[0]![1].body);
    stores.$layout.set({ root: buildVerticalLayout() });
    acknowledge({ ok: true, status: 200, json: async () => ({ ...old, revision: 1 }) });
    await vi.advanceTimersByTimeAsync(100);
    expect(mocks.fetch).toHaveBeenCalledTimes(2);
    expect(JSON.parse(mocks.fetch.mock.calls[1]![1].body)).toMatchObject({
      revision: 1,
      root: buildVerticalLayout(),
    });
    expect(stores.$layout.get().root).toEqual(buildVerticalLayout());
    await vi.advanceTimersByTimeAsync(60_000);
    expect(mocks.fetch).toHaveBeenCalledTimes(2);
  });

  it('rebases a conflict once when the server revision advances', async () => {
    const { stores, layoutStore } = await loadHarness();
    layoutStore.applyServerLayoutState({ revision: 1, root: null, focusedSessionId: null });
    mocks.fetch.mockResolvedValueOnce({
      ok: false,
      status: 409,
      json: async () => ({ revision: 2, root: buildVerticalLayout(), focusedSessionId: null }),
    });
    mocks.fetch.mockImplementation(async (_url, init) => ({
      ok: true,
      status: 200,
      json: async () => ({ ...JSON.parse(init.body), revision: 3 }),
    }));
    layoutStore.initLayoutPersistence();
    layoutStore.markLayoutPersistenceReady();
    stores.$layout.set({ root: buildHorizontalLayout() });
    await vi.advanceTimersByTimeAsync(100);
    expect(mocks.fetch).toHaveBeenCalledTimes(2);
    expect(JSON.parse(mocks.fetch.mock.calls[1]![1].body)).toMatchObject({
      revision: 2,
      root: buildHorizontalLayout(),
    });
  });

  it('does not spin on an unchanged conflict response', async () => {
    const { stores, layoutStore } = await loadHarness();
    layoutStore.applyServerLayoutState({ revision: 1, root: null, focusedSessionId: null });
    mocks.fetch.mockResolvedValue({
      ok: false,
      status: 409,
      json: async () => ({ revision: 1, root: null, focusedSessionId: null }),
    });
    layoutStore.initLayoutPersistence();
    layoutStore.markLayoutPersistenceReady();
    stores.$layout.set({ root: buildHorizontalLayout() });
    await vi.advanceTimersByTimeAsync(100);
    expect(mocks.fetch).toHaveBeenCalledTimes(1);
  });

  it('does not rewrite unchanged local storage', async () => {
    const { stores, layoutStore } = await loadHarness();
    stores.$layout.set({ root: buildHorizontalLayout() });
    stores.$focusedSessionId.set('session-a');
    layoutStore.saveLayoutToStorage();
    vi.mocked(localStorage.setItem).mockClear();
    layoutStore.saveLayoutToStorage();
    expect(localStorage.setItem).not.toHaveBeenCalled();
  });
});
