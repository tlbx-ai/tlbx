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
  detachPreview: vi.fn(),
  dockBack: vi.fn(),
  isDetachedOpenForSession: vi.fn(() => false),
  setDetachedPreviewViewport: vi.fn(() => false),
  setViewportSize: vi.fn(),
  openWebPreviewDock: vi.fn(),
  setWebPreviewTarget: vi.fn(),
  getSessionPreview: vi.fn(() => null),
  getSessionSelectedPreviewName: vi.fn(() => 'default'),
  setSessionMode: vi.fn(),
  setSessionSelectedPreviewName: vi.fn((_sessionId: string, previewName?: string | null) =>
    previewName?.trim() ? previewName.trim() : 'default',
  ),
  upsertSessionPreview: vi.fn(),
  syncActiveWebPreview: vi.fn().mockResolvedValue(undefined),
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
  setWebPreviewTarget: mocks.setWebPreviewTarget,
}));

vi.mock('../web/webSessionState', () => ({
  getSessionPreview: mocks.getSessionPreview,
  getSessionSelectedPreviewName: mocks.getSessionSelectedPreviewName,
  setSessionMode: mocks.setSessionMode,
  setSessionSelectedPreviewName: mocks.setSessionSelectedPreviewName,
  upsertSessionPreview: mocks.upsertSessionPreview,
}));

vi.mock('../web', () => ({
  syncActiveWebPreview: mocks.syncActiveWebPreview,
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
let resetStateChannelRuntimeForTests: typeof import('./stateChannel').resetStateChannelRuntimeForTests;
let setSelectSessionCallback: typeof import('./stateChannel').setSelectSessionCallback;
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
      resetStateChannelRuntimeForTests,
      setSelectSessionCallback,
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
    mocks.setWebPreviewTarget.mockResolvedValue({
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

    await Promise.resolve();
    await Promise.resolve();

    expect(mocks.selectSession).not.toHaveBeenCalled();
    expect(stores.$activeSessionId.get()).toBe('user1234');
    expect(mocks.openWebPreviewDock).not.toHaveBeenCalled();
    expect(mocks.syncActiveWebPreview).not.toHaveBeenCalled();
    expect(mocks.setWebPreviewTarget).toHaveBeenCalledWith(
      'agent5678',
      'default',
      'http://localhost:3000',
    );
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
    expect(mocks.setWebPreviewTarget).not.toHaveBeenCalled();
    expect(mocks.openWebPreviewDock).not.toHaveBeenCalled();
    expect(mocks.selectSession).not.toHaveBeenCalled();
  });

  it('does not switch sessions when browser open explicitly disables activation', async () => {
    const { stores, ws } = await loadHarness();
    mocks.setWebPreviewTarget.mockResolvedValue({
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

    await Promise.resolve();
    await Promise.resolve();

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

  it('activates the target session when browser open explicitly requests it', async () => {
    const { stores, ws } = await loadHarness();
    mocks.setWebPreviewTarget.mockResolvedValue({
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

    await Promise.resolve();
    await Promise.resolve();

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

  it('does not resize a visible main-browser terminal from a state snapshot', async () => {
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

    expect(resize).not.toHaveBeenCalled();
    expect(mocks.applyTerminalScaling).not.toHaveBeenCalled();
    expect(state.sessionTerminals.get('session-a')?.serverCols).toBe(120);
    expect(state.sessionTerminals.get('session-a')?.serverRows).toBe(30);
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
