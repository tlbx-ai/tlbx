import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
  closeWebSocket: vi.fn(),
  createTerminalForSession: vi.fn(),
  destroyTerminalForSession: vi.fn(),
  applyTerminalScaling: vi.fn(),
  handleSessionClosed: vi.fn(),
  updateEmptyState: vi.fn(),
  updateMobileTitle: vi.fn(),
  renderUpdatePanel: vi.fn(),
  handleHiddenSessionClosed: vi.fn(),
  closeOverlay: vi.fn(),
  detachPreview: vi.fn().mockResolvedValue({ success: true }),
  dockBack: vi.fn(),
  isDetachedOpenForSession: vi.fn(() => false),
  setDetachedPreviewViewport: vi.fn(() => false),
  setViewportSize: vi.fn(),
  openWebPreviewDock: vi.fn(),
  getWebPreviewTarget: vi.fn(),
  applySessionViewportToFrame: vi.fn(() => true),
  getSessionPreview: vi.fn(() => null),
  getSessionSelectedPreviewName: vi.fn(() => 'default'),
  setSessionMode: vi.fn(),
  setSessionViewport: vi.fn(),
  setSessionSelectedPreviewName: vi.fn((_sessionId: string, previewName?: string | null) =>
    previewName?.trim() ? previewName.trim() : 'default',
  ),
  upsertSessionPreview: vi.fn(),
  syncActiveWebPreview: vi.fn().mockResolvedValue(undefined),
  syncBackgroundWebPreview: vi.fn().mockResolvedValue(undefined),
  closePreviewFromServer: vi.fn().mockResolvedValue(undefined),
  isSessionInLayout: vi.fn(() => false),
  restoreLayoutFromStorage: vi.fn(),
  applyServerLayoutState: vi.fn(),
  dockSession: vi.fn(),
  swapLayoutSessions: vi.fn(),
  markLayoutPersistenceReady: vi.fn(),
  initializeFromSession: vi.fn(),
  selectSession: vi.fn(),
  checkVersionAndReload: vi.fn().mockResolvedValue(false),
}));

vi.mock('../../utils', () => ({
  ReconnectController: class {
    cancel(): void {}
    reset(): void {}
    schedule(callback?: () => void): void {
      callback?.();
    }
  },
  createWsUrl: () => 'ws://midterm.test/ws/state',
  closeWebSocket: mocks.closeWebSocket,
}));

vi.mock('../logging', () => ({
  createLogger: () => ({
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    verbose: vi.fn(),
  }),
}));

vi.mock('../process', () => ({
  initializeFromSession: mocks.initializeFromSession,
}));

vi.mock('../terminal/manager', () => ({
  destroyTerminalForSession: mocks.destroyTerminalForSession,
  createTerminalForSession: mocks.createTerminalForSession,
}));

vi.mock('../terminal/scaling', () => ({
  applyTerminalScaling: mocks.applyTerminalScaling,
}));

vi.mock('../layout', () => ({
  handleSessionClosed: mocks.handleSessionClosed,
}));

vi.mock('../sidebar/sessionList', () => ({
  updateEmptyState: mocks.updateEmptyState,
  updateMobileTitle: mocks.updateMobileTitle,
}));

vi.mock('../updating/checker', () => ({
  renderUpdatePanel: mocks.renderUpdatePanel,
}));

vi.mock('../commands/commandsPanel', () => ({
  handleHiddenSessionClosed: mocks.handleHiddenSessionClosed,
}));

vi.mock('../commands/outputPanel', () => ({
  closeOverlay: mocks.closeOverlay,
}));

vi.mock('../web/webDetach', () => ({
  detachPreview: mocks.detachPreview,
  dockBack: mocks.dockBack,
  isDetachedOpenForSession: mocks.isDetachedOpenForSession,
  setDetachedPreviewViewport: mocks.setDetachedPreviewViewport,
}));

vi.mock('../web/webDock', () => ({
  setViewportSize: mocks.setViewportSize,
  openWebPreviewDock: mocks.openWebPreviewDock,
}));

vi.mock('../web/webApi', () => ({
  getWebPreviewTarget: mocks.getWebPreviewTarget,
}));

vi.mock('../web/webViewport', () => ({
  applySessionViewportToFrame: mocks.applySessionViewportToFrame,
}));

vi.mock('../web/webSessionState', () => ({
  getSessionPreview: mocks.getSessionPreview,
  getSessionSelectedPreviewName: mocks.getSessionSelectedPreviewName,
  setSessionMode: mocks.setSessionMode,
  setSessionViewport: mocks.setSessionViewport,
  setSessionSelectedPreviewName: mocks.setSessionSelectedPreviewName,
  upsertSessionPreview: mocks.upsertSessionPreview,
}));

vi.mock('../web', () => ({
  closePreviewFromServer: mocks.closePreviewFromServer,
  syncActiveWebPreview: mocks.syncActiveWebPreview,
  syncBackgroundWebPreview: mocks.syncBackgroundWebPreview,
}));

vi.mock('../web/webContext', () => ({
  isEmbeddedWebPreviewContext: () => false,
}));

vi.mock('../share', () => ({
  isSharedSessionRoute: () => false,
}));

vi.mock('../layout/layoutStore', () => ({
  restoreLayoutFromStorage: mocks.restoreLayoutFromStorage,
  applyServerLayoutState: mocks.applyServerLayoutState,
  dockSession: mocks.dockSession,
  isSessionInLayout: mocks.isSessionInLayout,
  markLayoutPersistenceReady: mocks.markLayoutPersistenceReady,
  swapLayoutSessions: mocks.swapLayoutSessions,
}));

vi.mock('../../utils/versionCheck', () => ({
  checkVersionAndReload: mocks.checkVersionAndReload,
}));

class MockWebSocket {
  public static readonly CONNECTING = 0;
  public static readonly OPEN = 1;
  public static readonly CLOSING = 2;
  public static readonly CLOSED = 3;
  public static instances: MockWebSocket[] = [];

  public readonly url: string;
  public readyState = MockWebSocket.OPEN;
  public onopen: ((event: Event) => void) | null = null;
  public onmessage: ((event: MessageEvent<string>) => void) | null = null;
  public onclose: ((event: CloseEvent) => void) | null = null;
  public onerror: ((event: Event) => void) | null = null;
  public send = vi.fn();
  public close = vi.fn(() => {
    this.readyState = MockWebSocket.CLOSED;
  });

  public constructor(url: string) {
    this.url = url;
    MockWebSocket.instances.push(this);
  }
}

let stores: typeof import('../../stores');
let state: typeof import('../../state');
let connectStateWebSocket: typeof import('./stateChannel').connectStateWebSocket;
let handleStateUpdate: typeof import('./stateChannel').handleStateUpdate;
let reportBrowserActivity: typeof import('./stateChannel').reportBrowserActivity;
let resetStateChannelRuntimeForTests: typeof import('./stateChannel').resetStateChannelRuntimeForTests;
let setSelectSessionCallback: typeof import('./stateChannel').setSelectSessionCallback;
let setInitialStateHydratedCallback: typeof import('./stateChannel').setInitialStateHydratedCallback;
let setTerminalNotificationCallback: typeof import('./stateChannel').setTerminalNotificationCallback;
const stateChannelModulePromise = import('./stateChannel');
const stateModulePromise = import('../../state');
const storesModulePromise = import('../../stores');

async function loadHarness() {
  MockWebSocket.instances = [];
  vi.stubGlobal('WebSocket', MockWebSocket);
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

  Object.values(mocks).forEach((value) => {
    if ('mockReset' in value && typeof value.mockReset === 'function') {
      value.mockReset();
    }
  });

  mocks.isDetachedOpenForSession.mockReturnValue(false);
  mocks.setDetachedPreviewViewport.mockReturnValue(false);
  mocks.getSessionPreview.mockReturnValue(null);
  mocks.getSessionSelectedPreviewName.mockReturnValue('default');
  mocks.setSessionSelectedPreviewName.mockImplementation(
    (_sessionId: string, previewName?: string | null) =>
      previewName?.trim() ? previewName.trim() : 'default',
  );
  mocks.syncActiveWebPreview.mockResolvedValue(undefined);
  mocks.syncBackgroundWebPreview.mockResolvedValue(undefined);
  mocks.closePreviewFromServer.mockResolvedValue(undefined);
  mocks.detachPreview.mockResolvedValue({ success: true });
  mocks.applySessionViewportToFrame.mockReturnValue(true);
  mocks.checkVersionAndReload.mockResolvedValue(false);

  resetStateChannelRuntimeForTests();
  stores.$activeSessionId.set('user1234');
  stores.$settingsOpen.set(false);
  stores.$webPreviewUrl.set(null);
  stores.$stateWsConnected.set(false);
  stores.$sessions.set({});
  stores.$browserSessions.set([]);

  state.setStateWs(null);
  state.sessionTerminals.clear();
  state.hiddenSessionIds.clear();
  state.newlyCreatedSessions.clear();
  state.pendingSessions.clear();

  setSelectSessionCallback(mocks.selectSession);
  connectStateWebSocket();

  const ws = MockWebSocket.instances[0];
  if (!ws) {
    throw new Error('Mock WebSocket was not created');
  }

  return { stores, ws, localStorageData };
}

describe('stateChannel browser-ui handling', () => {
  beforeAll(async () => {
    stores = await storesModulePromise;
    state = await stateModulePromise;
    ({
      connectStateWebSocket,
      handleStateUpdate,
      reportBrowserActivity,
      resetStateChannelRuntimeForTests,
      setSelectSessionCallback,
      setInitialStateHydratedCallback,
      setTerminalNotificationCallback,
    } = await stateChannelModulePromise);
  });

  beforeEach(() => {
    vi.useRealTimers();
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('does not switch sessions when opening a preview for a background session', async () => {
    const { stores, ws } = await loadHarness();
    mocks.getWebPreviewTarget.mockResolvedValue({
      sessionId: 'agent5678',
      previewName: 'default',
      routeKey: 'route-1',
      url: 'http://localhost:3000',
      active: true,
      targetRevision: 1,
    });

    ws.onmessage?.({
      data: JSON.stringify({
        type: 'browser-ui',
        command: 'open',
        sessionId: 'agent5678',
        previewName: 'default',
        url: 'http://localhost:3000',
      }),
    } as MessageEvent<string>);

    await vi.waitFor(() =>
      expect(mocks.syncBackgroundWebPreview).toHaveBeenCalledWith('agent5678', 'default'),
    );

    expect(mocks.selectSession).not.toHaveBeenCalled();
    expect(stores.$activeSessionId.get()).toBe('user1234');
    expect(mocks.openWebPreviewDock).not.toHaveBeenCalled();
    expect(mocks.syncActiveWebPreview).not.toHaveBeenCalled();
    expect(mocks.syncBackgroundWebPreview).toHaveBeenCalledWith('agent5678', 'default');
    expect(mocks.getWebPreviewTarget).toHaveBeenCalledWith('agent5678', 'default');
    expect(mocks.checkVersionAndReload).toHaveBeenCalledTimes(1);
    expect(mocks.checkVersionAndReload).toHaveBeenCalledWith({
      forceReloadOnMismatch: true,
    });
    expect(mocks.upsertSessionPreview).toHaveBeenCalledWith({
      sessionId: 'agent5678',
      previewName: 'default',
      routeKey: 'route-1',
      url: 'http://localhost:3000',
      active: true,
      targetRevision: 1,
    });
  });

  it('rejects an obsolete open command without mutating or rendering the newer target', async () => {
    const { ws } = await loadHarness();
    mocks.getWebPreviewTarget.mockResolvedValue({
      sessionId: 'agent5678',
      previewName: 'default',
      routeKey: 'route-1',
      url: 'http://localhost:3001/',
      active: true,
      targetRevision: 2,
    });
    const fetchMock = vi.fn().mockResolvedValue({ ok: true });
    vi.stubGlobal('fetch', fetchMock);

    ws.onmessage?.({
      data: JSON.stringify({
        type: 'browser-ui',
        command: 'open',
        requestId: 'open-1',
        sessionId: 'agent5678',
        previewName: 'default',
        url: 'http://localhost:3000',
        targetRevision: 1,
      }),
    } as MessageEvent<string>);

    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));

    expect(mocks.upsertSessionPreview).not.toHaveBeenCalled();
    expect(mocks.syncBackgroundWebPreview).not.toHaveBeenCalled();
    expect(JSON.parse(String(fetchMock.mock.calls[0]?.[1]?.body))).toMatchObject({
      requestId: 'open-1',
      command: 'open',
      success: false,
    });
  });

  it('serializes an in-flight open before a later close for the same preview', async () => {
    const { ws } = await loadHarness();
    let resolveTarget!: (value: {
      sessionId: string;
      previewName: string;
      routeKey: string;
      url: string;
      active: boolean;
      targetRevision: number;
    }) => void;
    mocks.getWebPreviewTarget.mockReturnValueOnce(
      new Promise((resolve) => {
        resolveTarget = resolve;
      }),
    );

    ws.onmessage?.({
      data: JSON.stringify({
        type: 'browser-ui',
        command: 'open',
        sessionId: 'agent5678',
        previewName: 'default',
        url: 'http://localhost:3000',
        targetRevision: 1,
      }),
    } as MessageEvent<string>);
    ws.onmessage?.({
      data: JSON.stringify({
        type: 'browser-ui',
        command: 'close',
        sessionId: 'agent5678',
        previewName: 'default',
      }),
    } as MessageEvent<string>);

    await vi.waitFor(() => expect(mocks.getWebPreviewTarget).toHaveBeenCalledTimes(1));
    expect(mocks.closePreviewFromServer).not.toHaveBeenCalled();

    resolveTarget({
      sessionId: 'agent5678',
      previewName: 'default',
      routeKey: 'route-1',
      url: 'http://localhost:3000/',
      active: true,
      targetRevision: 1,
    });

    await vi.waitFor(() =>
      expect(mocks.closePreviewFromServer).toHaveBeenCalledWith('agent5678', 'default'),
    );
  });

  it('stores and applies a viewport for a background preview without activating it', async () => {
    const { stores, ws } = await loadHarness();
    const fetchMock = vi.fn().mockResolvedValue({ ok: true });
    vi.stubGlobal('fetch', fetchMock);

    ws.onmessage?.({
      data: JSON.stringify({
        type: 'browser-ui',
        command: 'viewport',
        requestId: 'viewport-1',
        sessionId: 'agent5678',
        previewName: 'default',
        width: 390,
        height: 844,
      }),
    } as MessageEvent<string>);

    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));

    expect(mocks.setSessionViewport).toHaveBeenCalledWith('agent5678', 'default', 390, 844);
    expect(mocks.syncBackgroundWebPreview).toHaveBeenCalledWith('agent5678', 'default');
    expect(mocks.applySessionViewportToFrame).toHaveBeenCalledWith('agent5678', 'default');
    expect(mocks.openWebPreviewDock).not.toHaveBeenCalled();
    expect(mocks.selectSession).not.toHaveBeenCalled();
    expect(stores.$activeSessionId.get()).toBe('user1234');
  });

  it('reports a blocked detach and does not claim detached state', async () => {
    const { ws } = await loadHarness();
    mocks.detachPreview.mockResolvedValue({
      success: false,
      error: 'The browser blocked the detached preview window.',
    });
    const fetchMock = vi.fn().mockResolvedValue({ ok: true });
    vi.stubGlobal('fetch', fetchMock);

    ws.onmessage?.({
      data: JSON.stringify({
        type: 'browser-ui',
        command: 'detach',
        requestId: 'detach-1',
        sessionId: 'agent5678',
        previewName: 'default',
      }),
    } as MessageEvent<string>);

    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));

    expect(mocks.detachPreview).toHaveBeenCalledWith('agent5678', 'default', {
      suppressFocus: true,
    });
    expect(mocks.setSessionMode).not.toHaveBeenCalled();
    expect(JSON.parse(String(fetchMock.mock.calls[0]?.[1]?.body))).toMatchObject({
      requestId: 'detach-1',
      command: 'detach',
      success: false,
    });
  });

  it('delivers transient terminal notifications without turning them into state snapshots', async () => {
    const { ws } = await loadHarness();
    const notify = vi.fn();
    setTerminalNotificationCallback(notify);

    ws.onmessage?.(
      new MessageEvent('message', {
        data: JSON.stringify({
          type: 'terminal-notification',
          sessionId: 'agent5678',
          protocol: 'osc9',
          body: 'Agent turn complete',
        }),
      }),
    );

    expect(notify).toHaveBeenCalledOnce();
    expect(notify).toHaveBeenCalledWith('agent5678', {
      protocol: 'osc9',
      body: 'Agent turn complete',
    });
    expect(mocks.createTerminalForSession).not.toHaveBeenCalled();
  });

  it('preserves the force flag on explicit CLI notifications', async () => {
    const { ws } = await loadHarness();
    const notify = vi.fn();
    setTerminalNotificationCallback(notify);

    ws.onmessage?.(
      new MessageEvent('message', {
        data: JSON.stringify({
          type: 'terminal-notification',
          sessionId: 'agent5678',
          protocol: 'cli',
          title: 'tlbx',
          body: 'Release complete',
          force: true,
          priority: 'important',
          nativeHandled: true,
        }),
      }),
    );

    expect(notify).toHaveBeenCalledWith('agent5678', {
      protocol: 'cli',
      title: 'tlbx',
      body: 'Release complete',
      force: true,
      priority: 'important',
      nativeHandled: true,
    });
  });

  it('defers browser open commands when a frontend reload was requested', async () => {
    const { ws } = await loadHarness();
    mocks.checkVersionAndReload.mockResolvedValueOnce(true);

    ws.onmessage?.({
      data: JSON.stringify({
        type: 'browser-ui',
        command: 'open',
        sessionId: 'agent5678',
        previewName: 'default',
        url: 'http://localhost:3000',
        activateSession: true,
      }),
    } as MessageEvent<string>);

    await Promise.resolve();
    await Promise.resolve();

    expect(mocks.checkVersionAndReload).toHaveBeenCalledWith({
      forceReloadOnMismatch: true,
    });
    expect(mocks.getWebPreviewTarget).not.toHaveBeenCalled();
    expect(mocks.openWebPreviewDock).not.toHaveBeenCalled();
    expect(mocks.selectSession).not.toHaveBeenCalled();
  });

  it('does not switch sessions when browser open explicitly disables activation', async () => {
    const { stores, ws } = await loadHarness();
    mocks.getWebPreviewTarget.mockResolvedValue({
      sessionId: 'agent5678',
      previewName: 'default',
      routeKey: 'route-1',
      url: 'http://localhost:3000',
      active: true,
      targetRevision: 1,
    });

    ws.onmessage?.({
      data: JSON.stringify({
        type: 'browser-ui',
        command: 'open',
        sessionId: 'agent5678',
        previewName: 'default',
        url: 'http://localhost:3000',
        activateSession: false,
      }),
    } as MessageEvent<string>);

    await vi.waitFor(() =>
      expect(mocks.syncBackgroundWebPreview).toHaveBeenCalledWith('agent5678', 'default'),
    );

    expect(mocks.selectSession).not.toHaveBeenCalled();
    expect(stores.$activeSessionId.get()).toBe('user1234');
    expect(mocks.openWebPreviewDock).not.toHaveBeenCalled();
    expect(mocks.syncActiveWebPreview).not.toHaveBeenCalled();
  });

  it('checks frontend version on state websocket reconnect', async () => {
    const { ws } = await loadHarness();

    ws.onopen?.(new Event('open'));
    expect(mocks.checkVersionAndReload).not.toHaveBeenCalled();

    ws.onclose?.(new CloseEvent('close'));

    const next = MockWebSocket.instances[1];
    if (!next) {
      throw new Error('Reconnect WebSocket was not created');
    }

    next.onopen?.(new Event('open'));
    expect(mocks.checkVersionAndReload).toHaveBeenCalledTimes(1);
  });

  it('checks socket health with a bounded activity reply without claiming ownership', async () => {
    const { ws } = await loadHarness();
    vi.useFakeTimers();
    vi.stubGlobal('document', { visibilityState: 'visible', hidden: false, hasFocus: () => false });
    vi.stubGlobal('window', { setTimeout: globalThis.setTimeout });
    const { probeStateWebSocket } = await stateChannelModulePromise;
    const healthy = probeStateWebSocket();
    const command = JSON.parse(ws.send.mock.calls.at(-1)![0]);
    expect(command.action).toBe('browser.setActivity');
    expect(command.payload.isActive).toBe(false);
    ws.onmessage?.({
      data: JSON.stringify({ type: 'response', id: command.id, success: true }),
    } as MessageEvent<string>);
    expect(await healthy).toBe(true);
    const stale = probeStateWebSocket();
    await vi.advanceTimersByTimeAsync(2000);
    expect(await stale).toBe(false);
    const retired = probeStateWebSocket();
    connectStateWebSocket();
    expect(await retired).toBe(true);
    vi.useRealTimers();
  });

  it('reports page visibility separately from browser focus activity', async () => {
    const { ws } = await loadHarness();
    vi.stubGlobal('document', {
      visibilityState: 'visible',
      hidden: false,
      hasFocus: () => false,
    });
    vi.stubGlobal('window', { setTimeout: globalThis.setTimeout });
    ws.onopen?.(new Event('open'));
    ws.send.mockClear();

    reportBrowserActivity(false, true);

    expect(ws.send).toHaveBeenCalledTimes(1);
    expect(JSON.parse(ws.send.mock.calls[0][0])).toMatchObject({
      action: 'browser.setActivity',
      payload: {
        isActive: false,
        isVisible: true,
      },
    });
  });

  it('reports a hidden page as ineligible for live state delivery', async () => {
    const { ws } = await loadHarness();
    vi.stubGlobal('document', {
      visibilityState: 'hidden',
      hidden: true,
      hasFocus: () => false,
    });
    vi.stubGlobal('window', { setTimeout: globalThis.setTimeout });
    ws.onopen?.(new Event('open'));
    ws.send.mockClear();

    reportBrowserActivity(false, true);

    expect(JSON.parse(ws.send.mock.calls[0][0])).toMatchObject({
      action: 'browser.setActivity',
      payload: {
        isActive: false,
        isVisible: false,
      },
    });
  });

  it('reports visibility changes even when focus activity stays false', async () => {
    const { ws } = await loadHarness();
    const fakeDocument = {
      visibilityState: 'visible',
      hidden: false,
      hasFocus: () => false,
    };
    vi.stubGlobal('document', fakeDocument);
    vi.stubGlobal('window', { setTimeout: globalThis.setTimeout });

    reportBrowserActivity(false);
    fakeDocument.visibilityState = 'hidden';
    fakeDocument.hidden = true;
    reportBrowserActivity(false);

    expect(ws.send).toHaveBeenCalledTimes(2);
    expect(ws.send.mock.calls.map(([raw]) => JSON.parse(raw).payload.isVisible)).toEqual([
      true,
      false,
    ]);
  });

  it('stores browser session tree from main browser status messages', async () => {
    const { stores, ws } = await loadHarness();

    ws.onmessage?.({
      data: JSON.stringify({
        type: 'main-browser-status',
        isMain: false,
        showButton: true,
        browsers: [
          {
            browserId: 'browser-a:tab-1',
            isMain: true,
            isActive: true,
            connectionCount: 1,
            activeConnectionCount: 1,
            activeSessionId: 'session-a',
            activeSurface: 'terminal',
          },
        ],
      }),
    } as MessageEvent<string>);

    expect(stores.$isMainBrowser.get()).toBe(false);
    expect(stores.$showMainBrowserButton.get()).toBe(true);
    expect(stores.$browserSessions.get()).toEqual([
      {
        browserId: 'browser-a:tab-1',
        isMain: true,
        isActive: true,
        connectionCount: 1,
        activeConnectionCount: 1,
        activeSessionId: 'session-a',
        activeSurface: 'terminal',
      },
    ]);
  });

  it('ignores an older main-browser projection after a newer revision', async () => {
    const { stores, ws } = await loadHarness();

    ws.onmessage?.({
      data: JSON.stringify({
        type: 'main-browser-status',
        revision: 12,
        isMain: true,
        showButton: true,
        browsers: [],
      }),
    } as MessageEvent<string>);
    ws.onmessage?.({
      data: JSON.stringify({
        type: 'main-browser-status',
        revision: 11,
        isMain: false,
        showButton: false,
        browsers: [],
      }),
    } as MessageEvent<string>);

    expect(stores.$isMainBrowser.get()).toBe(true);
    expect(stores.$showMainBrowserButton.get()).toBe(true);
  });

  it('activates the target session when browser open explicitly requests it', async () => {
    const { stores, ws } = await loadHarness();
    mocks.getWebPreviewTarget.mockResolvedValue({
      sessionId: 'agent5678',
      previewName: 'default',
      routeKey: 'route-1',
      url: 'http://localhost:3000',
      active: true,
      targetRevision: 1,
    });
    mocks.selectSession.mockImplementation((sessionId: string) => {
      stores.$activeSessionId.set(sessionId);
    });

    ws.onmessage?.({
      data: JSON.stringify({
        type: 'browser-ui',
        command: 'open',
        sessionId: 'agent5678',
        previewName: 'default',
        url: 'http://localhost:3000',
        activateSession: true,
      }),
    } as MessageEvent<string>);

    await vi.waitFor(() =>
      expect(mocks.selectSession).toHaveBeenCalledWith('agent5678', {
        closeSettingsPanel: false,
      }),
    );

    expect(mocks.selectSession).toHaveBeenCalledWith('agent5678', {
      closeSettingsPanel: false,
    });
    expect(stores.$activeSessionId.get()).toBe('agent5678');
    expect(mocks.openWebPreviewDock).toHaveBeenCalledTimes(1);
    expect(mocks.syncActiveWebPreview).toHaveBeenCalledTimes(1);
  });

  it('skips proactive terminal creation for appServerControl-only sessions', async () => {
    await loadHarness();

    handleStateUpdate([
      {
        id: 'appServerControl-1',
        cols: 120,
        rows: 30,
        appServerControlOnly: true,
        foregroundPid: null,
        foregroundName: null,
        foregroundCommandLine: null,
        currentDirectory: 'Q:/repos/MidTerm',
      } as any,
    ]);

    expect(mocks.createTerminalForSession).not.toHaveBeenCalled();
  });

  it('applies authoritative server dimensions even to the owning browser', async () => {
    const { stores } = await loadHarness();
    stores.$isMainBrowser.set(true);
    const container = { classList: { contains: vi.fn(() => false) } };
    const resize = vi.fn();
    state.sessionTerminals.set('session-a', {
      opened: true,
      container: container as any,
      serverCols: 120,
      serverRows: 30,
      terminal: { resize } as any,
      fitAddon: {} as any,
    });

    handleStateUpdate([
      {
        id: 'session-a',
        cols: 100,
        rows: 24,
        appServerControlOnly: false,
        foregroundPid: null,
        foregroundName: null,
        foregroundCommandLine: null,
        currentDirectory: 'Q:/repos/MidTerm',
      } as any,
    ]);

    expect(resize).toHaveBeenCalledWith(100, 24);
    expect(mocks.applyTerminalScaling).toHaveBeenCalledOnce();
    expect(state.sessionTerminals.get('session-a')?.serverCols).toBe(100);
    expect(state.sessionTerminals.get('session-a')?.serverRows).toBe(24);
  });

  it('does not rerender session chrome for identical state snapshots', async () => {
    await loadHarness();
    const sessions = [
      {
        id: 'appServerControl-1',
        cols: 120,
        rows: 30,
        appServerControlOnly: true,
        foregroundPid: null,
        foregroundName: null,
        foregroundCommandLine: null,
        currentDirectory: 'Q:/repos/MidTerm',
      } as any,
    ];

    handleStateUpdate(sessions);
    handleStateUpdate(sessions);

    expect(mocks.updateEmptyState).toHaveBeenCalledTimes(1);
    expect(mocks.updateMobileTitle).toHaveBeenCalledTimes(1);
  });

  it('restores the remembered active session when reconnecting after a refresh', async () => {
    const { stores, localStorageData } = await loadHarness();
    localStorageData.set('midterm.activeSessionId', 'session-b');
    stores.$activeSessionId.set(null);

    handleStateUpdate([
      {
        id: 'session-a',
        cols: 120,
        rows: 30,
        appServerControlOnly: false,
        foregroundPid: null,
        foregroundName: null,
        foregroundCommandLine: null,
        currentDirectory: 'Q:/repos/MidTerm',
      } as any,
      {
        id: 'session-b',
        cols: 120,
        rows: 30,
        appServerControlOnly: false,
        foregroundPid: null,
        foregroundName: null,
        foregroundCommandLine: null,
        currentDirectory: 'Q:/repos/MidTerm',
      } as any,
    ]);

    expect(mocks.selectSession).toHaveBeenCalledWith('session-b', {
      closeSettingsPanel: false,
    });
  });

  it('prefers a running bookmarked process when no active session was remembered', async () => {
    const { stores } = await loadHarness();
    stores.$activeSessionId.set(null);

    handleStateUpdate([
      {
        id: 'agent-session',
        cols: 120,
        rows: 30,
        appServerControlOnly: true,
        bookmarkId: null,
        currentDirectory: 'Q:/repos/MidTerm',
      } as any,
      {
        id: 'bookmarked-terminal',
        cols: 120,
        rows: 30,
        appServerControlOnly: false,
        bookmarkId: 'bookmark-1',
        currentDirectory: 'Q:/repos/Jpa',
      } as any,
    ]);

    expect(mocks.selectSession).toHaveBeenCalledWith('bookmarked-terminal', {
      closeSettingsPanel: false,
    });
  });

  it('announces initial state hydration only after session selection is synchronized', async () => {
    const { stores } = await loadHarness();
    stores.$activeSessionId.set(null);
    const hydrated = vi.fn(() => {
      expect(mocks.selectSession).toHaveBeenCalledWith('session-a', {
        closeSettingsPanel: false,
      });
    });
    setInitialStateHydratedCallback(hydrated);

    handleStateUpdate([
      {
        id: 'session-a',
        cols: 120,
        rows: 30,
        appServerControlOnly: false,
        currentDirectory: 'Q:/repos/MidTerm',
      } as any,
    ]);
    handleStateUpdate([]);

    expect(hydrated).toHaveBeenCalledOnce();
  });

  it('preserves an optimistic session while unrelated server state arrives', async () => {
    const { stores } = await loadHarness();
    const pending = {
      id: 'pending-launch',
      cols: 120,
      rows: 30,
      shellType: 'Loading...',
      currentDirectory: 'Q:/repos/Jpa',
      appServerControlOnly: false,
    } as any;
    stores.setSession(pending);
    state.pendingSessions.add(pending.id);

    handleStateUpdate([
      {
        id: 'existing-session',
        cols: 120,
        rows: 30,
        shellType: 'Pwsh',
        currentDirectory: 'Q:/repos/tlbx',
        appServerControlOnly: false,
      } as any,
    ]);

    expect(stores.getSession('pending-launch')).toEqual(
      expect.objectContaining({ currentDirectory: 'Q:/repos/Jpa' }),
    );
    expect(stores.getSession('existing-session')).toBeDefined();
  });

  it('applies server layout snapshots from state updates', async () => {
    await loadHarness();

    handleStateUpdate(
      [
        {
          id: 'session-a',
          cols: 120,
          rows: 30,
          appServerControlOnly: false,
          foregroundPid: null,
          foregroundName: null,
          foregroundCommandLine: null,
          currentDirectory: 'Q:/repos/MidTerm',
        } as any,
        {
          id: 'session-b',
          cols: 120,
          rows: 30,
          appServerControlOnly: false,
          foregroundPid: null,
          foregroundName: null,
          foregroundCommandLine: null,
          currentDirectory: 'Q:/repos/MidTerm',
        } as any,
      ],
      {
        root: {
          type: 'split',
          direction: 'horizontal',
          children: [
            { type: 'leaf', sessionId: 'session-a' },
            { type: 'leaf', sessionId: 'session-b' },
          ],
        },
        focusedSessionId: 'session-b',
      },
    );

    expect(mocks.applyServerLayoutState).toHaveBeenCalledWith({
      root: {
        type: 'split',
        direction: 'horizontal',
        children: [
          { type: 'leaf', sessionId: 'session-a' },
          { type: 'leaf', sessionId: 'session-b' },
        ],
      },
      focusedSessionId: 'session-b',
    });
    expect(mocks.markLayoutPersistenceReady).toHaveBeenCalled();
  });
});
