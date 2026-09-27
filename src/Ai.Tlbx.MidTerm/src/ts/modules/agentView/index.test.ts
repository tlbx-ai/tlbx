import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';

const onTabActivated = vi.fn();
const onTabDeactivated = vi.fn();
const switchTab = vi.fn();
const ensureSessionWrapper = vi.fn();
const getTabPanel = vi.fn();
const setSessionAppServerControlAvailability = vi.fn();
const getActiveTab = vi.fn(() => 'agent');
const getSessionState = vi.fn();
const getSessionBufferTail = vi.fn();
const attachSessionAppServerControl = vi.fn();
const detachSessionAppServerControl = vi.fn(() => Promise.resolve());
const getAppServerControlHistoryWindow = vi.fn();
const getAppServerControlEvents = vi.fn();
const openAppServerControlHistoryStream = vi.fn(() => vi.fn());
const updateAppServerControlHistoryStreamWindow = vi.fn();
const interruptAppServerControlTurn = vi.fn();
const approveAppServerControlRequest = vi.fn();
const declineAppServerControlRequest = vi.fn();
const resolveAppServerControlUserInput = vi.fn();
const showDevErrorDialog = vi.fn();
let activeSessionId: string | null = null;
let currentSettings: any = { showUnknownAgentMessages: true };
const activeSessionSubscribers: Array<(sessionId: string | null) => void> = [];
const documentEventListeners = new Map<string, Array<() => void>>();
const windowEventListeners = new Map<string, Array<() => void>>();
let resetAgentViewRuntimeForTests: typeof import('./index').resetAgentViewRuntimeForTests;
const agentViewModulePromise = import('./index');

describe('variable-height history wheel prefetch', () => {
  it('requests the next retained window before an upward wheel reaches the pixel edge', async () => {
    const { shouldPrefetchHistoryWindowForWheel } = await agentViewModulePromise;
    expect(
      shouldPrefetchHistoryWindowForWheel({
        deltaYPx: -220,
        scrollTop: 900,
        scrollHeight: 8000,
        clientHeight: 800,
        hasOlderHistory: true,
        hasNewerHistory: true,
      }),
    ).toBe(true);
    expect(
      shouldPrefetchHistoryWindowForWheel({
        deltaYPx: -220,
        scrollTop: 2000,
        scrollHeight: 8000,
        clientHeight: 800,
        hasOlderHistory: true,
        hasNewerHistory: true,
      }),
    ).toBe(false);
  });

  it('prefetches symmetrically near the lower edge and respects global endpoints', async () => {
    const { shouldPrefetchHistoryWindowForWheel } = await agentViewModulePromise;
    expect(
      shouldPrefetchHistoryWindowForWheel({
        deltaYPx: 220,
        scrollTop: 6300,
        scrollHeight: 8000,
        clientHeight: 800,
        hasOlderHistory: true,
        hasNewerHistory: true,
      }),
    ).toBe(true);
    expect(
      shouldPrefetchHistoryWindowForWheel({
        deltaYPx: 220,
        scrollTop: 6300,
        scrollHeight: 8000,
        clientHeight: 800,
        hasOlderHistory: true,
        hasNewerHistory: false,
      }),
    ).toBe(false);
  });
});

function createMockDomNode(overrides: Record<string, unknown> = {}): any {
  const node: any = {
    dataset: {} as DOMStringMap,
    style: {} as CSSStyleDeclaration,
    className: '',
    textContent: '',
    innerHTML: '',
    hidden: false,
    disabled: false,
    value: '',
    children: [] as any[],
    childNodes: [] as any[],
    firstChild: null as any,
    lastChild: null as any,
    append: vi.fn(function (this: any, ...items: any[]) {
      items.forEach((item) => this.insertBefore(item, null));
    }),
    appendChild: vi.fn(function (this: any, child: any) {
      return this.insertBefore(child, null);
    }),
    replaceChildren: vi.fn(function (this: any, ...items: any[]) {
      this.childNodes = [];
      this.children = [];
      items.forEach((item) => this.insertBefore(item, null));
    }),
    insertBefore: vi.fn(function (this: any, child: any, anchor: any) {
      const nodes = this.childNodes as any[];
      const existingIndex = nodes.indexOf(child);
      if (existingIndex >= 0) {
        nodes.splice(existingIndex, 1);
      }

      const anchorIndex = anchor ? nodes.indexOf(anchor) : -1;
      if (anchorIndex >= 0) {
        nodes.splice(anchorIndex, 0, child);
      } else {
        nodes.push(child);
      }

      this.childNodes = nodes;
      this.children = nodes;
      this.firstChild = nodes[0] ?? null;
      this.lastChild = nodes[nodes.length - 1] ?? null;
      return child;
    }),
    removeChild: vi.fn(function (this: any, child: any) {
      const nodes = (this.childNodes as any[]).filter((candidate) => candidate !== child);
      this.childNodes = nodes;
      this.children = nodes;
      this.firstChild = nodes[0] ?? null;
      this.lastChild = nodes[nodes.length - 1] ?? null;
      return child;
    }),
    setAttribute: vi.fn(),
    addEventListener: vi.fn(),
    classList: {
      add: vi.fn(),
      remove: vi.fn(),
      toggle: vi.fn(),
      contains: vi.fn(() => false),
    },
    scrollTop: 0,
    scrollHeight: 0,
    clientHeight: 0,
  };

  return Object.assign(node, overrides);
}

function registerEventListener(
  store: Map<string, Array<() => void>>,
  event: string,
  callback: EventListenerOrEventListenerObject,
): void {
  const listeners = store.get(event) ?? [];
  const normalized =
    typeof callback === 'function' ? callback : callback.handleEvent.bind(callback);
  listeners.push(normalized as () => void);
  store.set(event, listeners);
}

function triggerDocumentEvent(event: string): void {
  for (const listener of documentEventListeners.get(event) ?? []) {
    listener();
  }
}

vi.mock('../sessionTabs', () => ({
  ensureSessionWrapper,
  getActiveTab,
  getTabPanel,
  onTabActivated,
  onTabDeactivated,
  setSessionAppServerControlAvailability,
  switchTab,
}));

vi.mock('../../stores', () => ({
  $activeSessionId: {
    get: () => activeSessionId,
    subscribe: (callback: (sessionId: string | null) => void) => {
      activeSessionSubscribers.push(callback);
      return () => {};
    },
  },
  $currentSettings: {
    get: () => currentSettings,
    subscribe: () => () => {},
  },
  getSession: () => ({
    id: 's1',
    currentDirectory: 'Q:\\repos\\MidTerm',
  }),
}));

vi.mock('../../api/client', () => ({
  AppServerControlHttpError: class AppServerControlHttpError extends Error {
    detail: string;
    status: number;

    constructor(status: number, detail: string) {
      super(`HTTP ${status}: ${detail}`);
      this.name = 'AppServerControlHttpError';
      this.status = status;
      this.detail = detail;
    }
  },
  getSessionState,
  getSessionBufferTail,
  attachSessionAppServerControl,
  detachSessionAppServerControl,
  getAppServerControlHistoryWindow,
  getAppServerControlHistoryWindow,
  getAppServerControlEvents,
  openAppServerControlHistoryStream,
  openAppServerControlHistoryStream,
  updateAppServerControlHistoryStreamWindow,
  interruptAppServerControlTurn,
  approveAppServerControlRequest,
  declineAppServerControlRequest,
  resolveAppServerControlUserInput,
}));

vi.mock('../../utils/devErrorDialog', () => ({
  showDevErrorDialog,
}));

vi.mock('../i18n', () => ({
  t: (key: string) => key,
}));

vi.mock('../logging', () => ({
  createLogger: () => ({
    info: vi.fn(),
    warn: vi.fn(),
  }),
}));

describe('agentView dev errors', () => {
  beforeAll(async () => {
    ({ resetAgentViewRuntimeForTests } = await agentViewModulePromise);
  });

  beforeEach(() => {
    vi.stubGlobal('document', {
      createElement: () => createMockDomNode(),
      createTextNode: (text: string) => ({ nodeType: 3, textContent: text }),
      createDocumentFragment: () => ({
        appendChild: vi.fn(),
        childNodes: [],
      }),
      addEventListener: vi.fn((event: string, callback: EventListenerOrEventListenerObject) => {
        registerEventListener(documentEventListeners, event, callback);
      }),
      visibilityState: 'visible',
      hidden: false,
    });
    vi.stubGlobal('window', {
      clearTimeout: vi.fn(),
      setTimeout: vi.fn(() => 1),
      addEventListener: vi.fn((event: string, callback: EventListenerOrEventListenerObject) => {
        registerEventListener(windowEventListeners, event, callback);
      }),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(() => true),
      location: {
        origin: 'https://midterm.test',
      },
      cancelAnimationFrame: vi.fn(),
      requestAnimationFrame: vi.fn((callback: FrameRequestCallback) => {
        queueMicrotask(() => callback(0));
        return 1;
      }),
    });
    onTabActivated.mockReset();
    onTabDeactivated.mockReset();
    switchTab.mockReset();
    ensureSessionWrapper.mockReset();
    getTabPanel.mockReset();
    setSessionAppServerControlAvailability.mockReset();
    getActiveTab.mockReset();
    getActiveTab.mockReturnValue('agent');
    getSessionState.mockReset();
    getSessionState.mockResolvedValue(null);
    getSessionBufferTail.mockReset();
    getSessionBufferTail.mockResolvedValue('');
    attachSessionAppServerControl.mockReset();
    detachSessionAppServerControl.mockReset();
    detachSessionAppServerControl.mockResolvedValue(undefined);
    getAppServerControlHistoryWindow.mockReset();
    getAppServerControlEvents.mockReset();
    openAppServerControlHistoryStream.mockReset();
    openAppServerControlHistoryStream.mockReturnValue(vi.fn());
    updateAppServerControlHistoryStreamWindow.mockReset();
    interruptAppServerControlTurn.mockReset();
    approveAppServerControlRequest.mockReset();
    declineAppServerControlRequest.mockReset();
    resolveAppServerControlUserInput.mockReset();
    showDevErrorDialog.mockReset();
    activeSessionId = null;
    currentSettings = { showUnknownAgentMessages: true };
    activeSessionSubscribers.length = 0;
    documentEventListeners.clear();
    windowEventListeners.clear();
    resetAgentViewRuntimeForTests();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function createPanel(): HTMLDivElement {
    const elements = new Map<string, any>();

    const getElement = (selector: string) => {
      if (!elements.has(selector)) {
        elements.set(selector, createMockDomNode());
      }

      return elements.get(selector);
    };

    return {
      ...createMockDomNode(),
      querySelector: vi.fn((selector: string) => getElement(selector)),
    } as unknown as HTMLDivElement;
  }

  function setActiveAppServerControlSession(sessionId: string | null): void {
    activeSessionId = sessionId;
    activeSessionSubscribers.forEach((callback) => callback(sessionId));
  }

  function createSnapshot(overrides: Record<string, any> = {}): any {
    return {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-29T10:00:00Z',
      latestSequence: 1,
      historyCount: 0,
      historyWindowStart: 0,
      historyWindowEnd: 0,
      hasOlderHistory: false,
      hasNewerHistory: false,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-29T10:00:00Z',
      },
      thread: {
        threadId: 'thread-1',
        state: 'active',
        stateLabel: 'Active',
      },
      currentTurn: {
        turnId: 'turn-1',
        state: 'completed',
        stateLabel: 'Completed',
        model: null,
        effort: null,
        startedAt: '2026-03-29T09:59:55Z',
        completedAt: '2026-03-29T10:00:00Z',
      },
      streams: {
        assistantText: '',
        reasoningText: '',
        reasoningSummaryText: '',
        planText: '',
        commandOutput: '',
        fileChangeOutput: '',
        unifiedDiff: '',
      },
      history: [],
      items: [],
      requests: [],
      notices: [],
      ...overrides,
    };
  }

  it('activates an already-selected Agent tab after init registers callbacks', async () => {
    const panel = createPanel();
    activeSessionId = 's1';
    getActiveTab.mockReturnValue('agent');
    getTabPanel.mockReturnValue(panel);
    getAppServerControlHistoryWindow.mockResolvedValue(
      createSnapshot({
        historyCount: 1,
        historyWindowEnd: 1,
        history: [
          {
            entryId: 'system:1',
            order: 1,
            kind: 'system',
            turnId: null,
            itemId: null,
            requestId: null,
            status: 'completed',
            itemType: 'system',
            title: 'System',
            body: 'Recovered from selected Agent tab.',
            attachments: [],
            streaming: false,
            createdAt: '2026-05-12T22:00:00Z',
            updatedAt: '2026-05-12T22:00:00Z',
          },
        ],
      }),
    );

    const { initAgentView } = await import('./index');
    initAgentView();

    await vi.waitFor(() => {
      expect(getTabPanel).toHaveBeenCalledWith('s1', 'agent');
      expect(attachSessionAppServerControl).toHaveBeenCalledWith('s1');
    });
    await vi.waitFor(() => {
      const history = panel.querySelector('[data-agent-field="history"]') as any;
      expect(history.childNodes.length).toBeGreaterThan(0);
    });
    expect(panel.classList.add).toHaveBeenCalledWith('agent-view-panel');
  });

  it('coalesces concurrent activation requests for the same Agent session', async () => {
    const panel = createPanel();
    getAppServerControlHistoryWindow.mockResolvedValue(createSnapshot());
    let finishAttach: (() => void) | undefined;
    attachSessionAppServerControl.mockReturnValue(
      new Promise<void>((resolve) => {
        finishAttach = resolve;
      }),
    );

    const { initAgentView } = await import('./index');
    initAgentView();
    const activate = onTabActivated.mock.calls[0]?.[1] as
      ((sessionId: string, panel: HTMLDivElement) => void) | undefined;

    activate?.('s1', panel);
    activate?.('s1', panel);

    await vi.waitFor(() => {
      expect(attachSessionAppServerControl).toHaveBeenCalledTimes(1);
    });
    finishAttach?.();
    await vi.waitFor(() => {
      expect(openAppServerControlHistoryStream).toHaveBeenCalledTimes(1);
    });
  });

  it('clears stale browse sync state when returning AppServerControl history to the live edge', async () => {
    const { createAgentHistoryRender } = await import('./historyRender');

    const panel = createPanel();
    const viewport = createMockDomNode({
      clientHeight: 600,
      clientWidth: 900,
      scrollHeight: 2400,
      scrollTop: 400,
      querySelector: vi.fn(),
      scrollTo: vi.fn(function (this: any, options: { top?: number }) {
        if (typeof options?.top === 'number') {
          this.scrollTop = options.top;
        }
      }),
      getBoundingClientRect: vi.fn(() => ({ top: 0, bottom: 600 })),
    });
    const scrollButton = createMockDomNode();
    panel.querySelector = vi.fn((selector: string) => {
      if (selector === '[data-agent-field="history"]') {
        return viewport;
      }
      if (selector === '[data-agent-field="scroll-to-bottom"]') {
        return scrollButton;
      }

      return createMockDomNode();
    });

    const state = {
      panel,
      snapshot: createSnapshot({
        provider: 'codex',
        historyCount: 40,
        historyWindowStart: 0,
        historyWindowEnd: 40,
      }),
      debugScenarioActive: false,
      activationRunId: 0,
      historyViewport: viewport,
      historyEntries: [],
      historyWindowStart: 0,
      historyWindowCount: 40,
      historyWindowTargetCount: 40,
      historyViewportSyncPending: true,
      historyViewportSyncForcePending: true,
      historyViewportSyncQueuedDuringRefresh: true,
      historyViewportSyncSuppressUntil: Date.now() + 1000,
      disconnectStream: null,
      streamConnected: true,
      refreshInFlight: false,
      requestBusyIds: new Set(),
      requestDraftAnswersById: {},
      requestQuestionIndexById: {},
      historyScrollMode: 'browse',
      historyAutoScrollPinned: false,
      historyLastScrollMetrics: null,
      historyLastUserScrollIntentAt: Date.now(),
      historyLastVoidSyncScrollTop: 144,
      historyWindowRevision: null,
      historyWindowViewportWidth: null,
      historyNavigatorMode: 'browse',
      historyNavigatorAnchorIndex: null,
      historyNavigatorDragTargetIndex: 8,
      historyNavigatorQueuedTargetIndex: null,
      historyNavigatorQueuedRequestKind: null,
      historyNavigatorPreviewHandle: null,
      historyNavigatorHydrateHandle: null,
      historyNavigatorLastPreviewRequestAt: 0,
      historyPendingJumpTargetIndex: 12,
      historyPendingJumpAlign: 'center',
      historyRenderScheduled: null,
      historyRenderBatchHandle: null,
      activationState: 'ready',
      activationDetail: '',
      activationTrace: [],
      activationError: null,
      activationIssue: null,
      activationActionBusy: false,
      optimisticTurns: [],
      renderDirty: false,
      assistantMarkdownCache: new Map(),
      historyRenderedNodes: new Map(),
      historyMeasuredHeights: new Map(),
      historyObservedHeights: new Map(),
      historyMeasuredHeightsByBucket: new Map(),
      historyObservedHeightsByBucket: new Map(),
      historyObservedHeightSamplesByBucket: new Map(),
      historyMeasuredWidthBucket: 0,
      historyMeasurementObserver: null,
      historyViewportResizeObserver: null,
      historyViewportSize: null,
      historyLeadingPlaceholders: [],
      historyTrailingPlaceholders: [],
      historyEmptyState: null,
      pendingHistoryPrependAnchor: {
        entryId: 'assistant:10',
        topOffsetPx: 8,
        absoluteIndex: 9,
      },
      pendingHistoryLayoutAnchor: {
        entryId: 'assistant:11',
        topOffsetPx: 10,
        absoluteIndex: 10,
      },
      historyLastVirtualWindowKey: null,
      historyExpandedEntries: new Set(),
      runtimeStats: null,
      busyIndicatorTickHandle: null,
      completedTurnDurationEntries: new Map(),
    } as any;

    const render = createAgentHistoryRender({
      getState: () => state,
      scheduleHistoryRender: vi.fn(),
      syncAgentViewPresentation: vi.fn(),
      createHistoryEntry: vi.fn(() => createMockDomNode()),
      createRequestActionBlock: () => createMockDomNode(),
      pruneAssistantMarkdownCache: vi.fn(),
      renderRuntimeStats: vi.fn(),
    });

    render.scrollHistoryToBottom('s1');

    expect(state.historyAutoScrollPinned).toBe(true);
    expect(state.historyNavigatorMode).toBe('follow-live');
    expect(state.historyNavigatorDragTargetIndex).toBeNull();
    expect(state.historyViewportSyncPending).toBe(false);
    expect(state.historyViewportSyncForcePending).toBe(false);
    expect(state.historyViewportSyncQueuedDuringRefresh).toBe(false);
    expect(state.historyViewportSyncSuppressUntil).toBe(0);
    expect(state.pendingHistoryPrependAnchor).toBeNull();
    expect(state.pendingHistoryLayoutAnchor).toBeNull();
    expect(state.historyLastVoidSyncScrollTop).toBeNull();
    expect(viewport.scrollTo).toHaveBeenCalledWith({
      top: viewport.scrollHeight,
      behavior: 'auto',
    });
  });

  it('stops auto-follow on even small upward user scrolls near the live edge', async () => {
    const { resolveHistoryScrollMode } = await import('./index');

    expect(
      resolveHistoryScrollMode({
        previousMode: 'follow',
        previous: {
          scrollTop: 900,
          clientHeight: 600,
          scrollHeight: 1500,
        },
        current: {
          scrollTop: 860,
          clientHeight: 600,
          scrollHeight: 1500,
        },
        userInitiated: true,
        pendingAnchorRestore: false,
      }),
    ).toBe('browse');
  });

  it('stops auto-follow on even small upward viewport movement near the live edge when intent markers are missed', async () => {
    const { resolveHistoryScrollMode } = await import('./index');

    expect(
      resolveHistoryScrollMode({
        previousMode: 'follow',
        previous: {
          scrollTop: 900,
          clientHeight: 600,
          scrollHeight: 1500,
        },
        current: {
          scrollTop: 860,
          clientHeight: 600,
          scrollHeight: 1500,
        },
        userInitiated: false,
        pendingAnchorRestore: false,
      }),
    ).toBe('browse');
  });

  it('preserves browse mode and scroll position when AppServerControl returns to the foreground', async () => {
    const { prepareAppServerControlForForeground } = await import('./viewPresentation');

    const historyViewport = createMockDomNode({
      scrollTop: 987,
    });
    const state = {
      historyScrollMode: 'browse',
      historyAutoScrollPinned: false,
      pendingHistoryPrependAnchor: null,
      pendingHistoryLayoutAnchor: null,
      historyViewport,
    } as any;

    prepareAppServerControlForForeground(state);

    expect(state.historyScrollMode).toBe('browse');
    expect(state.historyAutoScrollPinned).toBe(false);
    expect(historyViewport.scrollTop).toBe(987);
  });

  it('keeps browse mode sticky near the live edge until the user explicitly returns to bottom', async () => {
    const { resolveHistoryScrollMode } = await import('./index');

    expect(
      resolveHistoryScrollMode({
        previousMode: 'browse',
        previous: {
          scrollTop: 1736,
          clientHeight: 600,
          scrollHeight: 2400,
        },
        current: {
          scrollTop: 1738,
          clientHeight: 600,
          scrollHeight: 2400,
        },
        userInitiated: false,
        pendingAnchorRestore: false,
      }),
    ).toBe('browse');
  });

  it('does not treat the bottom of an older retained kernel as the live edge', async () => {
    const { resolveHistoryScrollMode } = await import('./index');

    expect(
      resolveHistoryScrollMode({
        previousMode: 'browse',
        previous: {
          scrollTop: 1200,
          clientHeight: 600,
          scrollHeight: 1800,
        },
        current: {
          scrollTop: 1400,
          clientHeight: 600,
          scrollHeight: 2000,
        },
        userInitiated: true,
        pendingAnchorRestore: false,
        hasNewerHistory: true,
      }),
    ).toBe('browse');
  });

  it('restores canonical AppServerControl history when attach fails but a snapshot already exists', async () => {
    attachSessionAppServerControl.mockRejectedValue(new Error('AppServerControl attach failed'));
    getAppServerControlHistoryWindow.mockResolvedValue({
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-22T01:45:00Z',
      latestSequence: 1,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: 'Codex turn completed.',
        lastError: null,
        lastEventAt: '2026-03-22T01:45:00Z',
      },
      thread: {
        threadId: 'thread-1',
        state: 'active',
        stateLabel: 'Active',
      },
      currentTurn: {
        turnId: 'turn-1',
        state: 'completed',
        stateLabel: 'Completed',
        model: null,
        effort: null,
        startedAt: '2026-03-22T01:44:55Z',
        completedAt: '2026-03-22T01:45:00Z',
      },
      streams: {
        assistantText: 'AppServerControl snapshot still exists.',
        reasoningText: '',
        reasoningSummaryText: '',
        planText: '',
        commandOutput: '',
        fileChangeOutput: '',
        unifiedDiff: '',
      },
      items: [
        {
          itemId: 'assistant-1',
          turnId: 'turn-1',
          itemType: 'assistant_message',
          status: 'completed',
          title: 'Assistant message',
          detail: 'AppServerControl snapshot still exists.',
          attachments: [],
          updatedAt: '2026-03-22T01:45:00Z',
        },
      ],
      requests: [],
      notices: [],
    });
    getAppServerControlEvents.mockResolvedValue({
      sessionId: 's1',
      latestSequence: 1,
      events: [],
    });

    setActiveAppServerControlSession('s1');

    const { initAgentView } = await import('./index');
    initAgentView();

    const activate = onTabActivated.mock.calls[0]?.[1] as
      ((sessionId: string, panel: HTMLDivElement) => void) | undefined;
    expect(activate).toBeTypeOf('function');

    const panel = createPanel();
    activate?.('s1', panel);
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();

    expect(getAppServerControlHistoryWindow).toHaveBeenCalledWith(
      's1',
      undefined,
      expect.any(Number),
      expect.any(String),
    );
    expect(getAppServerControlEvents.mock.calls.length).toBeLessThanOrEqual(1);
    expect(showDevErrorDialog).not.toHaveBeenCalled();
  });

  it('retries live AppServerControl resume automatically after restoring canonical history', async () => {
    attachSessionAppServerControl.mockRejectedValueOnce(
      new Error('AppServerControl attach failed'),
    );
    attachSessionAppServerControl.mockResolvedValue(undefined);
    getAppServerControlHistoryWindow
      .mockRejectedValueOnce(new Error('AppServerControl snapshot unavailable'))
      .mockResolvedValue({
        sessionId: 's1',
        provider: 'codex',
        generatedAt: '2026-03-22T01:45:00Z',
        latestSequence: 1,
        session: {
          state: 'ready',
          stateLabel: 'Ready',
          reason: 'Codex turn completed.',
          lastError: null,
          lastEventAt: '2026-03-22T01:45:00Z',
        },
        thread: {
          threadId: 'thread-1',
          state: 'active',
          stateLabel: 'Active',
        },
        currentTurn: {
          turnId: 'turn-1',
          state: 'completed',
          stateLabel: 'Completed',
          model: null,
          effort: null,
          startedAt: '2026-03-22T01:44:55Z',
          completedAt: '2026-03-22T01:45:00Z',
        },
        streams: {
          assistantText: 'AppServerControl snapshot still exists.',
          reasoningText: '',
          reasoningSummaryText: '',
          planText: '',
          commandOutput: '',
          fileChangeOutput: '',
          unifiedDiff: '',
        },
        items: [
          {
            itemId: 'assistant-1',
            turnId: 'turn-1',
            itemType: 'assistant_message',
            status: 'completed',
            title: 'Assistant message',
            detail: 'AppServerControl snapshot still exists.',
            attachments: [],
            updatedAt: '2026-03-22T01:45:00Z',
          },
        ],
        requests: [],
        notices: [],
      });
    getAppServerControlEvents
      .mockResolvedValueOnce({
        sessionId: 's1',
        latestSequence: 1,
        events: [],
      })
      .mockResolvedValueOnce({
        sessionId: 's1',
        latestSequence: 1,
        events: [],
      });

    const { initAgentView } = await import('./index');
    initAgentView();

    const activate = onTabActivated.mock.calls[0]?.[1] as
      ((sessionId: string, panel: HTMLDivElement) => void) | undefined;
    expect(activate).toBeTypeOf('function');

    const panel = createPanel();
    activate?.('s1', panel);

    await vi.waitFor(() => {
      expect(attachSessionAppServerControl).toHaveBeenCalled();
    });
    expect(showDevErrorDialog.mock.calls.length).toBeLessThanOrEqual(1);
  });

  it('refreshes AppServerControl history and reconnects the stream after an accepted turn from read-only history', async () => {
    attachSessionAppServerControl.mockRejectedValue(
      new Error('HTTP 400: MidTerm could not determine the Codex resume id for this session.'),
    );
    getAppServerControlHistoryWindow
      .mockResolvedValueOnce({
        sessionId: 's1',
        provider: 'codex',
        generatedAt: '2026-03-23T21:40:01Z',
        latestSequence: 36,
        session: {
          state: 'ready',
          stateLabel: 'Ready',
          reason: 'Codex turn completed.',
          lastError: null,
          lastEventAt: '2026-03-23T21:40:01Z',
        },
        thread: {
          threadId: 'thread-1',
          state: 'active',
          stateLabel: 'Active',
        },
        currentTurn: {
          turnId: 'turn-1',
          state: 'completed',
          stateLabel: 'Completed',
          model: null,
          effort: null,
          startedAt: '2026-03-23T21:39:55Z',
          completedAt: '2026-03-23T21:40:01Z',
        },
        streams: {
          assistantText: '`C:\\Users\\johan`',
          reasoningText: '',
          reasoningSummaryText: '',
          planText: '',
          commandOutput: '',
          fileChangeOutput: '',
          unifiedDiff: '',
        },
        items: [],
        requests: [],
        notices: [],
      })
      .mockResolvedValueOnce({
        sessionId: 's1',
        provider: 'codex',
        generatedAt: '2026-03-23T21:40:32Z',
        latestSequence: 75,
        session: {
          state: 'ready',
          stateLabel: 'Ready',
          reason: 'Codex turn completed.',
          lastError: null,
          lastEventAt: '2026-03-23T21:40:32Z',
        },
        thread: {
          threadId: 'thread-1',
          state: 'active',
          stateLabel: 'Active',
        },
        currentTurn: {
          turnId: 'turn-2',
          state: 'completed',
          stateLabel: 'Completed',
          model: null,
          effort: null,
          startedAt: '2026-03-23T21:40:24Z',
          completedAt: '2026-03-23T21:40:32Z',
        },
        streams: {
          assistantText: 'Checking the current shell working directory directly.',
          reasoningText: '',
          reasoningSummaryText: '',
          planText: '',
          commandOutput: 'C:\\Users\\johan',
          fileChangeOutput: '',
          unifiedDiff: '',
        },
        items: [],
        requests: [],
        notices: [],
      });
    getAppServerControlEvents.mockResolvedValue({
      sessionId: 's1',
      latestSequence: 36,
      events: [],
    });

    const { initAgentView } = await import('./index');
    const { APP_SERVER_CONTROL_TURN_ACCEPTED_EVENT } = await import('../appServerControl/input');
    initAgentView();

    const activate = onTabActivated.mock.calls[0]?.[1] as
      ((sessionId: string, panel: HTMLDivElement) => void) | undefined;
    expect(activate).toBeTypeOf('function');

    const panel = createPanel();
    activate?.('s1', panel);
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();

    const acceptedListener = (window.addEventListener as ReturnType<typeof vi.fn>).mock.calls.find(
      ([name]) => name === APP_SERVER_CONTROL_TURN_ACCEPTED_EVENT,
    )?.[1] as ((event: Event) => void) | undefined;
    expect(acceptedListener).toBeTypeOf('function');

    acceptedListener?.({
      detail: {
        optimisticId: 'opt-1',
        sessionId: 's1',
        request: {
          text: 'what working dir are we in now?',
          attachments: [],
        },
        response: {
          sessionId: 's1',
          status: 'accepted',
          provider: 'codex',
          threadId: 'thread-1',
          turnId: 'turn-2',
          requestId: null,
          model: null,
          effort: null,
        },
      },
    } as Event);

    await vi.waitFor(() => {
      expect(openAppServerControlHistoryStream).toHaveBeenCalledWith(
        's1',
        expect.any(Number),
        expect.any(Number),
        expect.any(Number),
        expect.any(String),
        expect.any(Object),
      );
    });
  });

  it('does not close the live AppServerControl stream when the agent tab is deactivated', async () => {
    const disconnectStream = vi.fn();
    openAppServerControlHistoryStream.mockReturnValue(disconnectStream);
    attachSessionAppServerControl.mockResolvedValue(undefined);
    getAppServerControlHistoryWindow.mockResolvedValue({
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-28T11:00:00Z',
      latestSequence: 1,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-28T11:00:00Z',
      },
      thread: {
        threadId: 'thread-1',
        state: 'active',
        stateLabel: 'Active',
      },
      currentTurn: {
        turnId: 'turn-1',
        state: 'completed',
        stateLabel: 'Completed',
        model: null,
        effort: null,
        startedAt: '2026-03-28T10:59:30Z',
        completedAt: '2026-03-28T11:00:00Z',
      },
      streams: {
        assistantText: 'Done.',
        reasoningText: '',
        reasoningSummaryText: '',
        planText: '',
        commandOutput: '',
        fileChangeOutput: '',
        unifiedDiff: '',
      },
      items: [],
      requests: [],
      notices: [],
      history: [],
    });
    getAppServerControlEvents.mockResolvedValue({
      sessionId: 's1',
      latestSequence: 1,
      events: [],
    });

    setActiveAppServerControlSession('s1');

    const { initAgentView } = await import('./index');
    initAgentView();

    const activate = onTabActivated.mock.calls[0]?.[1] as
      ((sessionId: string, panel: HTMLDivElement) => void) | undefined;
    const deactivate = onTabDeactivated.mock.calls[0]?.[1] as
      ((sessionId: string) => void) | undefined;
    expect(activate).toBeTypeOf('function');
    expect(deactivate).toBeTypeOf('function');

    activate?.('s1', createPanel());

    await vi.waitFor(() => {
      expect(openAppServerControlHistoryStream).toHaveBeenCalledTimes(1);
    });

    deactivate?.('s1');
    await Promise.resolve();

    expect(disconnectStream).not.toHaveBeenCalled();
  });

  it('releases inactive-session history DOM and collapses background history to a latest window', async () => {
    const disconnectStream = vi.fn();
    openAppServerControlHistoryStream.mockReturnValue(disconnectStream);
    attachSessionAppServerControl.mockResolvedValue(undefined);
    getAppServerControlHistoryWindow
      .mockResolvedValueOnce({
        sessionId: 's1',
        provider: 'codex',
        generatedAt: '2026-03-28T11:00:00Z',
        latestSequence: 40,
        historyCount: 400,
        historyWindowStart: 160,
        historyWindowEnd: 240,
        hasOlderHistory: true,
        hasNewerHistory: true,
        session: {
          state: 'running',
          stateLabel: 'Running',
          reason: null,
          lastError: null,
          lastEventAt: '2026-03-28T11:00:00Z',
        },
        thread: {
          threadId: 'thread-1',
          state: 'active',
          stateLabel: 'Active',
        },
        currentTurn: {
          turnId: 'turn-1',
          state: 'running',
          stateLabel: 'Running',
          model: null,
          effort: null,
          startedAt: '2026-03-28T10:59:30Z',
          completedAt: null,
        },
        streams: {
          assistantText: 'Working…',
          reasoningText: '',
          reasoningSummaryText: '',
          planText: '',
          commandOutput: '',
          fileChangeOutput: '',
          unifiedDiff: '',
        },
        items: [],
        requests: [],
        notices: [],
        history: Array.from({ length: 80 }, (_value, index) => ({
          entryId: `assistant:${index + 161}`,
          turnId: 'turn-1',
          itemId: `assistant-${index + 161}`,
          requestId: null,
          order: index + 161,
          kind: 'assistant',
          status: 'running',
          title: 'Assistant',
          body: `Historical row ${index + 161}`,
          updatedAt: '2026-03-28T11:00:00Z',
          streaming: index === 79,
          attachments: [],
          createdAt: '2026-03-28T11:00:00Z',
        })),
      })
      .mockResolvedValueOnce({
        sessionId: 's1',
        provider: 'codex',
        generatedAt: '2026-03-28T11:00:05Z',
        latestSequence: 45,
        historyCount: 405,
        historyWindowStart: 325,
        historyWindowEnd: 405,
        hasOlderHistory: true,
        hasNewerHistory: false,
        session: {
          state: 'running',
          stateLabel: 'Running',
          reason: null,
          lastError: null,
          lastEventAt: '2026-03-28T11:00:05Z',
        },
        thread: {
          threadId: 'thread-1',
          state: 'active',
          stateLabel: 'Active',
        },
        currentTurn: {
          turnId: 'turn-1',
          state: 'running',
          stateLabel: 'Running',
          model: null,
          effort: null,
          startedAt: '2026-03-28T10:59:30Z',
          completedAt: null,
        },
        streams: {
          assistantText: 'Latest output',
          reasoningText: '',
          reasoningSummaryText: '',
          planText: '',
          commandOutput: '',
          fileChangeOutput: '',
          unifiedDiff: '',
        },
        items: [],
        requests: [],
        notices: [],
        history: Array.from({ length: 80 }, (_value, index) => ({
          entryId: `assistant:${index + 326}`,
          turnId: 'turn-1',
          itemId: `assistant-${index + 326}`,
          requestId: null,
          order: index + 326,
          kind: 'assistant',
          status: 'running',
          title: 'Assistant',
          body: `Latest row ${index + 326}`,
          updatedAt: '2026-03-28T11:00:05Z',
          streaming: index === 79,
          attachments: [],
          createdAt: '2026-03-28T11:00:05Z',
        })),
      });
    getAppServerControlEvents.mockResolvedValue({
      sessionId: 's1',
      latestSequence: 45,
      events: [],
    });

    setActiveAppServerControlSession('s1');

    const { initAgentView } = await import('./index');
    initAgentView();

    const activate = onTabActivated.mock.calls[0]?.[1] as
      ((sessionId: string, panel: HTMLDivElement) => void) | undefined;
    expect(activate).toBeTypeOf('function');

    const panel = createPanel();
    activate?.('s1', panel);

    await vi.waitFor(() => {
      expect(openAppServerControlHistoryStream).toHaveBeenCalledTimes(1);
    });

    const historyHost = panel.querySelector('[data-agent-field="history"]') as any;
    historyHost.replaceChildren.mockClear();

    setActiveAppServerControlSession('s2');

    await vi.waitFor(() => {
      expect(getAppServerControlHistoryWindow.mock.calls).toContainEqual([
        's1',
        undefined,
        240,
        expect.any(String),
      ]);
    });

    expect(disconnectStream).not.toHaveBeenCalled();
    expect(historyHost.replaceChildren).toHaveBeenCalled();
  });

  it('re-entering a still-connected AppServerControl tab refreshes the latest history window when the cached window is stale', async () => {
    const disconnectStream = vi.fn();
    openAppServerControlHistoryStream.mockImplementation((...args: any[]) => {
      const handlers = args[5];
      handlers?.onOpen?.();
      return disconnectStream;
    });
    attachSessionAppServerControl.mockResolvedValue(undefined);
    getAppServerControlHistoryWindow.mockResolvedValue(
      createSnapshot({
        latestSequence: 40,
        historyCount: 400,
        historyWindowStart: 160,
        historyWindowEnd: 240,
        hasOlderHistory: true,
        hasNewerHistory: true,
        history: Array.from({ length: 80 }, (_value, index) => ({
          entryId: `assistant:${index + 161}`,
          turnId: 'turn-1',
          itemId: `assistant-${index + 161}`,
          requestId: null,
          order: index + 161,
          kind: 'assistant',
          status: 'completed',
          title: 'Assistant',
          body: `Historical row ${index + 161}`,
          updatedAt: '2026-03-28T11:00:00Z',
          streaming: false,
          attachments: [],
          createdAt: '2026-03-28T11:00:00Z',
        })),
      }),
    );

    setActiveAppServerControlSession('s1');

    const { initAgentView } = await import('./index');
    initAgentView();

    const activate = onTabActivated.mock.calls[0]?.[1] as
      ((sessionId: string, panel: HTMLDivElement) => void) | undefined;
    expect(activate).toBeTypeOf('function');

    const panel = createPanel();
    activate?.('s1', panel);

    await vi.waitFor(() => {
      expect(openAppServerControlHistoryStream).toHaveBeenCalledTimes(1);
    });

    getAppServerControlHistoryWindow.mockClear();

    activate?.('s1', panel);

    await vi.waitFor(() => {
      expect(getAppServerControlHistoryWindow).toHaveBeenCalledWith(
        's1',
        undefined,
        expect.any(Number),
        expect.any(String),
      );
    });
  });

  it('treats explicit upward wheel intent as a browse detach before foreground recovery reloads history', async () => {
    const disconnectStream = vi.fn();
    openAppServerControlHistoryStream.mockReturnValue(disconnectStream);
    attachSessionAppServerControl.mockResolvedValue(undefined);
    getAppServerControlHistoryWindow
      .mockResolvedValueOnce(
        createSnapshot({
          latestSequence: 40,
          historyCount: 400,
          historyWindowStart: 160,
          historyWindowEnd: 240,
          hasOlderHistory: true,
          hasNewerHistory: true,
        }),
      )
      .mockResolvedValueOnce(
        createSnapshot({
          latestSequence: 45,
          historyCount: 405,
          historyWindowStart: 160,
          historyWindowEnd: 240,
          hasOlderHistory: true,
          hasNewerHistory: true,
        }),
      );
    getAppServerControlEvents.mockResolvedValue({
      sessionId: 's1',
      latestSequence: 45,
      events: [],
    });

    setActiveAppServerControlSession('s1');

    const { initAgentView } = await import('./index');
    initAgentView();

    const activate = onTabActivated.mock.calls[0]?.[1] as
      ((sessionId: string, panel: HTMLDivElement) => void) | undefined;
    expect(activate).toBeTypeOf('function');

    const panel = createPanel();
    const historyHost = panel.querySelector('[data-agent-field="history"]') as any;

    activate?.('s1', panel);

    await vi.waitFor(() => {
      expect(getAppServerControlHistoryWindow).toHaveBeenNthCalledWith(
        1,
        's1',
        undefined,
        expect.any(Number),
        expect.any(String),
      );
    });

    const wheelHandler = historyHost.addEventListener.mock.calls.find(
      ([eventName]: [string]) => eventName === 'wheel',
    )?.[1] as ((event: { deltaY: number }) => void) | undefined;
    expect(wheelHandler).toBeTypeOf('function');

    wheelHandler?.({ deltaY: -24 });

    (document as { visibilityState: string; hidden: boolean }).visibilityState = 'hidden';
    (document as { visibilityState: string; hidden: boolean }).hidden = true;
    triggerDocumentEvent('visibilitychange');

    (document as { visibilityState: string; hidden: boolean }).visibilityState = 'visible';
    (document as { visibilityState: string; hidden: boolean }).hidden = false;
    triggerDocumentEvent('visibilitychange');

    await vi.waitFor(() => {
      expect(getAppServerControlHistoryWindow).toHaveBeenNthCalledWith(
        2,
        's1',
        160,
        80,
        expect.any(String),
      );
    });
  });

  it('queues a follow-up viewport history sync when scroll continues during an in-flight browse fetch', async () => {
    const disconnectStream = vi.fn();
    openAppServerControlHistoryStream.mockReturnValue(disconnectStream);
    attachSessionAppServerControl.mockResolvedValue(undefined);

    const buildWindowSnapshot = (startIndex: number, count: number, latestSequence: number) =>
      createSnapshot({
        latestSequence,
        historyCount: 640,
        historyWindowStart: startIndex,
        historyWindowEnd: startIndex + count,
        hasOlderHistory: startIndex > 0,
        hasNewerHistory: startIndex + count < 640,
        history: Array.from({ length: count }, (_value, index) => {
          const order = startIndex + index + 1;
          return {
            entryId: `assistant:${order}`,
            turnId: `turn-${order}`,
            itemId: `assistant-${order}`,
            requestId: null,
            order,
            kind: 'assistant',
            status: 'completed',
            itemType: 'assistant_text',
            title: null,
            body: `History row ${order}`,
            updatedAt: '2026-04-15T12:00:00Z',
            streaming: false,
            attachments: [],
            createdAt: '2026-04-15T12:00:00Z',
          };
        }),
      });

    let resolveSecondWindow: (() => void) | null = null;
    const requestedWindows: Array<{ startIndex: number | undefined; count: number | undefined }> =
      [];
    getAppServerControlHistoryWindow.mockImplementation(
      async (_sessionId: string, startIndex?: number, count?: number): Promise<any> => {
        requestedWindows.push({ startIndex, count });

        if (requestedWindows.length === 1) {
          return buildWindowSnapshot(320, 80, 40);
        }

        if (requestedWindows.length === 2) {
          return new Promise((resolve) => {
            resolveSecondWindow = () =>
              resolve(buildWindowSnapshot(startIndex ?? 0, count ?? 80, 41));
          });
        }

        return buildWindowSnapshot(startIndex ?? 0, count ?? 80, 42);
      },
    );

    setActiveAppServerControlSession('s1');

    const { initAgentView } = await import('./index');
    initAgentView();

    const activate = onTabActivated.mock.calls[0]?.[1] as
      ((sessionId: string, panel: HTMLDivElement) => void) | undefined;
    expect(activate).toBeTypeOf('function');

    const panel = createPanel();
    const historyHost = panel.querySelector('[data-agent-field="history"]') as any;
    historyHost.clientHeight = 600;
    historyHost.clientWidth = 920;
    historyHost.scrollHeight = 12000;
    historyHost.scrollTop = 4200;
    historyHost.getBoundingClientRect = vi.fn(() => ({ top: 0, bottom: 600 }));
    historyHost.querySelector = vi.fn(() => null);

    activate?.('s1', panel);

    await vi.waitFor(() => {
      expect(getAppServerControlHistoryWindow).toHaveBeenCalledTimes(1);
    });
    const wheelHandler = historyHost.addEventListener.mock.calls.find(
      ([eventName]: [string]) => eventName === 'wheel',
    )?.[1] as ((event: { deltaY: number }) => void) | undefined;
    const scrollHandler = historyHost.addEventListener.mock.calls.find(
      ([eventName]: [string]) => eventName === 'scroll',
    )?.[1] as (() => void) | undefined;
    expect(wheelHandler).toBeTypeOf('function');
    expect(scrollHandler).toBeTypeOf('function');

    wheelHandler?.({ deltaY: -32 });

    historyHost.scrollTop = 1100;
    scrollHandler?.();

    await vi.waitFor(() => {
      expect(getAppServerControlHistoryWindow).toHaveBeenCalledTimes(2);
    });

    historyHost.scrollTop = 140;
    scrollHandler?.();
    await Promise.resolve();
    expect(getAppServerControlHistoryWindow).toHaveBeenCalledTimes(2);

    expect(resolveSecondWindow).toBeTypeOf('function');
    resolveSecondWindow?.();

    await vi.waitFor(() => {
      expect(getAppServerControlHistoryWindow.mock.calls.length).toBeGreaterThanOrEqual(2);
    });
    if (requestedWindows[2]) {
      expect(requestedWindows[2].startIndex).toBeTypeOf('number');
      expect((requestedWindows[2].count ?? 0) > 0).toBe(true);
    }
  });

  it('keeps background AppServerControl streams alive but skips history rerenders while hidden', async () => {
    const disconnectStream = vi.fn();
    openAppServerControlHistoryStream.mockReturnValue(disconnectStream);
    attachSessionAppServerControl.mockResolvedValue(undefined);
    getAppServerControlHistoryWindow.mockResolvedValue({
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-28T11:00:00Z',
      latestSequence: 1,
      session: {
        state: 'running',
        stateLabel: 'Running',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-28T11:00:00Z',
      },
      thread: {
        threadId: 'thread-1',
        state: 'active',
        stateLabel: 'Active',
      },
      currentTurn: {
        turnId: 'turn-1',
        state: 'running',
        stateLabel: 'Running',
        model: null,
        effort: null,
        startedAt: '2026-03-28T10:59:30Z',
        completedAt: null,
      },
      streams: {
        assistantText: 'Initial assistant text.',
        reasoningText: '',
        reasoningSummaryText: '',
        planText: '',
        commandOutput: '',
        fileChangeOutput: '',
        unifiedDiff: '',
      },
      items: [],
      requests: [],
      notices: [],
      history: [
        {
          entryId: 'assistant:turn-1',
          turnId: 'turn-1',
          itemId: 'assistant-1',
          requestId: null,
          order: 1,
          kind: 'assistant',
          status: 'running',
          title: 'Assistant',
          body: 'Initial assistant text.',
          updatedAt: '2026-03-28T11:00:00Z',
          streaming: true,
          attachments: [],
        },
      ],
    });
    getAppServerControlEvents.mockResolvedValue({
      sessionId: 's1',
      latestSequence: 1,
      events: [],
    });

    setActiveAppServerControlSession('s1');

    const { initAgentView } = await import('./index');
    initAgentView();

    const activate = onTabActivated.mock.calls[0]?.[1] as
      ((sessionId: string, panel: HTMLDivElement) => void) | undefined;
    expect(activate).toBeTypeOf('function');

    const panel = createPanel();
    activate?.('s1', panel);

    await vi.waitFor(() => {
      expect(openAppServerControlHistoryStream).toHaveBeenCalledTimes(1);
    });

    const historyHost = panel.querySelector('[data-agent-field="history"]') as any;
    historyHost.replaceChildren.mockClear();

    setActiveAppServerControlSession('s2');
    expect(historyHost.replaceChildren).toHaveBeenCalledTimes(1);
    historyHost.replaceChildren.mockClear();

    const streamCallbacks = openAppServerControlHistoryStream.mock.calls[0]?.[5] as
      { onPatch(delta: unknown): void } | undefined;
    expect(streamCallbacks).toBeTruthy();

    streamCallbacks?.onPatch({
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-28T11:00:01Z',
      latestSequence: 2,
      historyCount: 1,
      session: {
        state: 'running',
        stateLabel: 'Running',
        reason: 'Codex turn started.',
        lastError: null,
        lastEventAt: '2026-03-28T11:00:01Z',
      },
      thread: {
        threadId: 'thread-1',
        state: 'active',
        stateLabel: 'Active',
      },
      currentTurn: {
        turnId: 'turn-1',
        state: 'running',
        stateLabel: 'Running',
        model: 'gpt-5.4',
        effort: 'medium',
        startedAt: '2026-03-28T11:00:00Z',
        completedAt: null,
      },
      streams: {
        assistantText: 'partial answer',
        reasoningText: '',
        reasoningSummaryText: '',
        planText: '',
        commandOutput: '',
        fileChangeOutput: '',
        unifiedDiff: '',
      },
      historyUpserts: [
        {
          entryId: 'assistant:assistant-1',
          order: 1,
          kind: 'assistant',
          turnId: 'turn-1',
          itemId: 'assistant-1',
          requestId: null,
          status: 'streaming',
          itemType: 'assistant_text',
          title: null,
          body: 'partial answer',
          attachments: [],
          streaming: true,
          createdAt: '2026-03-28T11:00:00Z',
          updatedAt: '2026-03-28T11:00:01Z',
        },
      ],
      historyRemovals: [],
      itemUpserts: [],
      itemRemovals: [],
      requestUpserts: [],
      requestRemovals: [],
      noticeUpserts: [],
    });
    await Promise.resolve();

    expect(disconnectStream).not.toHaveBeenCalled();
    expect(historyHost.replaceChildren).not.toHaveBeenCalled();
  });

  it('batches live history patch renders to one paint every 250ms', async () => {
    attachSessionAppServerControl.mockResolvedValue(undefined);
    getAppServerControlHistoryWindow.mockResolvedValue(
      createSnapshot({
        currentTurn: {
          turnId: 'turn-1',
          state: 'running',
          stateLabel: 'Running',
          model: 'gpt-5.4',
          effort: 'medium',
          startedAt: '2026-03-28T11:00:00Z',
          completedAt: null,
        },
        session: {
          state: 'running',
          stateLabel: 'Running',
          reason: null,
          lastError: null,
          lastEventAt: '2026-03-28T11:00:00Z',
        },
      }),
    );
    getAppServerControlEvents.mockResolvedValue({
      sessionId: 's1',
      latestSequence: 1,
      events: [],
    });

    setActiveAppServerControlSession('s1');

    const { initAgentView } = await import('./index');
    initAgentView();

    const activate = onTabActivated.mock.calls[0]?.[1] as
      ((sessionId: string, panel: HTMLDivElement) => void) | undefined;
    expect(activate).toBeTypeOf('function');

    activate?.('s1', createPanel());

    await vi.waitFor(() => {
      expect(openAppServerControlHistoryStream).toHaveBeenCalledTimes(1);
    });

    const streamCallbacks = openAppServerControlHistoryStream.mock.calls[0]?.[5] as
      { onPatch(delta: unknown): void } | undefined;
    expect(streamCallbacks).toBeTruthy();

    const requestAnimationFrameMock = window.requestAnimationFrame as unknown as ReturnType<
      typeof vi.fn
    >;
    const setTimeoutMock = window.setTimeout as unknown as ReturnType<typeof vi.fn>;
    requestAnimationFrameMock.mockClear();
    setTimeoutMock.mockClear();

    const firstPatch = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-28T11:00:01Z',
      latestSequence: 2,
      historyCount: 1,
      session: {
        state: 'running',
        stateLabel: 'Running',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-28T11:00:01Z',
      },
      thread: {
        threadId: 'thread-1',
        state: 'active',
        stateLabel: 'Active',
      },
      currentTurn: {
        turnId: 'turn-1',
        state: 'running',
        stateLabel: 'Running',
        model: 'gpt-5.4',
        effort: 'medium',
        startedAt: '2026-03-28T11:00:00Z',
        completedAt: null,
      },
      streams: {
        assistantText: 'partial',
        reasoningText: '',
        reasoningSummaryText: '',
        planText: '',
        commandOutput: '',
        fileChangeOutput: '',
        unifiedDiff: '',
      },
      historyUpserts: [
        {
          entryId: 'assistant:assistant-1',
          order: 1,
          kind: 'assistant',
          turnId: 'turn-1',
          itemId: 'assistant-1',
          requestId: null,
          status: 'streaming',
          itemType: 'assistant_text',
          title: null,
          body: 'partial',
          attachments: [],
          streaming: true,
          createdAt: '2026-03-28T11:00:00Z',
          updatedAt: '2026-03-28T11:00:01Z',
        },
      ],
      historyRemovals: [],
      itemUpserts: [],
      itemRemovals: [],
      requestUpserts: [],
      requestRemovals: [],
      noticeUpserts: [],
    };

    streamCallbacks?.onPatch(firstPatch);
    streamCallbacks?.onPatch({
      ...firstPatch,
      generatedAt: '2026-03-28T11:00:02Z',
      latestSequence: 3,
      session: {
        ...firstPatch.session,
        lastEventAt: '2026-03-28T11:00:02Z',
      },
      streams: {
        ...firstPatch.streams,
        assistantText: 'partial answer',
      },
      historyUpserts: [
        {
          ...firstPatch.historyUpserts[0],
          body: 'partial answer',
          updatedAt: '2026-03-28T11:00:02Z',
        },
      ],
    });

    expect(setTimeoutMock).toHaveBeenCalledTimes(1);
    expect(setTimeoutMock.mock.calls[0]?.[1]).toBe(250);
    expect(requestAnimationFrameMock).not.toHaveBeenCalled();

    const flushRenderBatch = setTimeoutMock.mock.calls[0]?.[0] as (() => void) | undefined;
    expect(flushRenderBatch).toBeTypeOf('function');
    flushRenderBatch?.();
    await Promise.resolve();
    await Promise.resolve();

    expect(requestAnimationFrameMock).toHaveBeenCalledTimes(1);
  });

  it('keeps a browsed history window stable when live history appends beyond it', async () => {
    const disconnectStream = vi.fn();
    openAppServerControlHistoryStream.mockReturnValue(disconnectStream);
    attachSessionAppServerControl.mockResolvedValue(undefined);

    const createRows = (startOrder: number, count: number) =>
      Array.from({ length: count }, (_value, index) => {
        const order = startOrder + index;
        return {
          entryId: `assistant:${order}`,
          turnId: 'turn-scroll',
          itemId: `assistant-${order}`,
          requestId: null,
          order,
          estimatedHeightPx: 100,
          kind: 'assistant',
          status: 'completed',
          itemType: 'assistant_text',
          title: null,
          body: `Historical row ${order}`,
          attachments: [],
          streaming: false,
          createdAt: '2026-03-28T11:00:00Z',
          updatedAt: '2026-03-28T11:00:00Z',
        };
      });

    getAppServerControlHistoryWindow.mockImplementation(
      async (_sessionId: string, startIndex?: number, count?: number) => {
        const windowStart = startIndex ?? 40;
        const windowCount = count ?? 40;
        return createSnapshot({
          latestSequence: startIndex === undefined ? 1 : 3,
          historyCount: startIndex === undefined ? 120 : 121,
          estimatedTotalHistoryHeightPx: startIndex === undefined ? 12000 : 12100,
          estimatedHistoryBeforeWindowPx: windowStart * 100,
          estimatedHistoryAfterWindowPx: Math.max(0, 121 - windowStart - windowCount) * 100,
          historyWindowStart: windowStart,
          historyWindowEnd: windowStart + windowCount,
          hasOlderHistory: windowStart > 0,
          hasNewerHistory: windowStart + windowCount < 121,
          currentTurn: {
            turnId: 'turn-scroll',
            state: 'running',
            stateLabel: 'Running',
            model: 'gpt-5.4',
            effort: 'medium',
            startedAt: '2026-03-28T11:00:00Z',
            completedAt: null,
          },
          session: {
            state: 'running',
            stateLabel: 'Running',
            reason: null,
            lastError: null,
            lastEventAt: '2026-03-28T11:00:00Z',
          },
          history: createRows(windowStart + 1, windowCount),
        });
      },
    );
    getAppServerControlEvents.mockResolvedValue({
      sessionId: 's1',
      latestSequence: 1,
      events: [],
    });

    setActiveAppServerControlSession('s1');

    const { initAgentView } = await import('./index');
    initAgentView();

    const activate = onTabActivated.mock.calls[0]?.[1] as
      ((sessionId: string, panel: HTMLDivElement) => void) | undefined;
    expect(activate).toBeTypeOf('function');

    const panel = createPanel();
    const historyHost = panel.querySelector('[data-agent-field="history"]') as any;
    historyHost.clientHeight = 600;
    historyHost.clientWidth = 900;
    historyHost.scrollHeight = 12100;
    historyHost.scrollTop = 7600;
    historyHost.getBoundingClientRect = vi.fn(() => ({ top: 0, bottom: 600, height: 600 }));

    activate?.('s1', panel);

    await vi.waitFor(() => {
      expect(openAppServerControlHistoryStream).toHaveBeenCalledTimes(1);
    });

    historyHost.scrollTop = 7600;
    for (const child of historyHost.childNodes as any[]) {
      const entryId = child.dataset?.appServerControlEntryId as string | undefined;
      const order = Number(entryId?.split(':')[1] ?? 0);
      child.getBoundingClientRect = vi.fn(() => ({
        top: (order - 76) * 100,
        bottom: (order - 75) * 100,
        height: 100,
      }));
    }

    const wheelHandler = historyHost.addEventListener.mock.calls.find(
      ([eventName]: [string]) => eventName === 'wheel',
    )?.[1] as ((event: { deltaY: number }) => void) | undefined;
    expect(wheelHandler).toBeTypeOf('function');
    wheelHandler?.({ deltaY: -24 });

    getAppServerControlHistoryWindow.mockClear();

    const streamCallbacks = openAppServerControlHistoryStream.mock.calls[0]?.[5] as
      { onPatch(delta: unknown): void } | undefined;
    expect(streamCallbacks).toBeTruthy();

    streamCallbacks?.onPatch({
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-28T11:00:02Z',
      latestSequence: 2,
      historyCount: 121,
      estimatedTotalHistoryHeightPx: 12100,
      session: {
        state: 'running',
        stateLabel: 'Running',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-28T11:00:02Z',
      },
      thread: {
        threadId: 'thread-scroll',
        state: 'active',
        stateLabel: 'Active',
      },
      currentTurn: {
        turnId: 'turn-scroll',
        state: 'running',
        stateLabel: 'Running',
        model: 'gpt-5.4',
        effort: 'medium',
        startedAt: '2026-03-28T11:00:00Z',
        completedAt: null,
      },
      streams: {
        assistantText: '',
        reasoningText: '',
        reasoningSummaryText: '',
        planText: '',
        commandOutput: '',
        fileChangeOutput: '',
        unifiedDiff: '',
      },
      historyUpserts: [
        {
          entryId: 'assistant:121',
          order: 121,
          estimatedHeightPx: 100,
          kind: 'assistant',
          turnId: 'turn-scroll',
          itemId: 'assistant-121',
          requestId: null,
          status: 'completed',
          itemType: 'assistant_text',
          title: null,
          body: 'New tail row',
          attachments: [],
          streaming: false,
          createdAt: '2026-03-28T11:00:02Z',
          updatedAt: '2026-03-28T11:00:02Z',
        },
      ],
      historyRemovals: [],
      itemUpserts: [],
      itemRemovals: [],
      requestUpserts: [],
      requestRemovals: [],
      noticeUpserts: [],
    });

    await Promise.resolve();
    await Promise.resolve();
    expect(getAppServerControlHistoryWindow).not.toHaveBeenCalled();
  });

  it('drops optimistic placeholders once canonical history entries exist for the turn', async () => {
    const { applyOptimisticAppServerControlTurns } = await import('./index');

    const result = applyOptimisticAppServerControlTurns(
      {
        sessionId: 's1',
        provider: 'codex',
        generatedAt: '2026-03-22T09:00:00Z',
        latestSequence: 12,
        session: {
          state: 'running',
          stateLabel: 'Running',
          reason: null,
          lastError: null,
          lastEventAt: '2026-03-22T09:00:00Z',
        },
        thread: {
          threadId: 'thread-1',
          state: 'active',
          stateLabel: 'Active',
        },
        currentTurn: {
          turnId: 'turn-1',
          state: 'running',
          stateLabel: 'Running',
          model: null,
          effort: null,
          startedAt: '2026-03-22T09:00:00Z',
          completedAt: null,
        },
        streams: {
          assistantText: 'Working on it',
          reasoningText: '',
          reasoningSummaryText: '',
          planText: '',
          commandOutput: '',
          fileChangeOutput: '',
          unifiedDiff: '',
        },
        items: [],
        requests: [],
        notices: [],
      },
      [
        {
          id: 'user:turn-1',
          order: 1,
          kind: 'user',
          tone: 'info',
          label: 'User',
          title: '',
          body: 'Summarize the repo state.',
          meta: '09:00',
        },
        {
          id: 'assistant:turn-1',
          order: 2,
          kind: 'assistant',
          tone: 'info',
          label: 'Assistant',
          title: '',
          body: 'Working on it',
          meta: '09:00',
        },
      ],
      [
        {
          optimisticId: 'opt-1',
          turnId: 'turn-1',
          text: 'Summarize the repo state.',
          attachments: [],
          submittedAt: '2026-03-22T09:00:00Z',
          status: 'accepted',
        } as any,
      ],
    );

    expect(result.entries).toHaveLength(2);
    expect(result.optimisticTurns).toHaveLength(0);
  });

  it('shows one settled assistant row when the final assistant item lands after streaming', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-27T16:40:59Z',
      latestSequence: 12,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-27T16:40:59Z',
      },
      thread: {
        threadId: 'thread-1',
        state: 'active',
        stateLabel: 'Active',
      },
      currentTurn: {
        turnId: 'turn-2',
        state: 'completed',
        stateLabel: 'Completed',
        model: 'gpt-5',
        effort: 'medium',
        startedAt: '2026-03-27T16:40:24Z',
        completedAt: '2026-03-27T16:40:59Z',
      },
      streams: {
        assistantText: '',
        reasoningText: '',
        reasoningSummaryText: '',
        planText: '',
        commandOutput: '',
        fileChangeOutput: '',
        unifiedDiff: '',
      },
      history: [
        {
          entryId: 'user:turn-2',
          order: 1,
          kind: 'user',
          turnId: 'turn-2',
          itemId: 'local-user:turn-2',
          requestId: null,
          status: 'completed',
          itemType: 'user_message',
          title: null,
          body: 'erstelle eine tabelle',
          attachments: [],
          streaming: false,
          createdAt: '2026-03-27T16:40:24Z',
          updatedAt: '2026-03-27T16:40:24Z',
        },
        {
          entryId: 'assistant-stream:turn-2',
          order: 2,
          kind: 'assistant',
          turnId: 'turn-2',
          itemId: 'assistant-item-2',
          requestId: null,
          status: 'completed',
          itemType: 'assistant_message',
          title: null,
          body: '| Name | Groesse |\n| --- | --- |\n| file.txt | 42 |',
          attachments: [],
          streaming: false,
          createdAt: '2026-03-27T16:40:25Z',
          updatedAt: '2026-03-27T16:40:59Z',
        },
        {
          entryId: 'tool:tool-1',
          order: 3,
          kind: 'tool',
          turnId: 'turn-2',
          itemId: 'tool-1',
          requestId: null,
          status: 'completed',
          itemType: 'command',
          title: 'Get-ChildItem',
          body: 'Dateiliste abgefragt',
          attachments: [],
          streaming: false,
          createdAt: '2026-03-27T16:40:32Z',
          updatedAt: '2026-03-27T16:40:32Z',
        },
      ],
      items: [],
      requests: [],
      notices: [],
    } as any;

    const history = buildAppServerControlHistoryEntries(snapshot);

    expect(history.map((entry) => entry.id)).toEqual([
      'user:turn-2',
      'assistant-stream:turn-2',
      'tool:tool-1',
    ]);
    expect(history[1]?.body).toContain('| file.txt | 42 |');
    expect(history[1]?.live).toBe(false);
  });

  it('preserves previously shown command tails when later updates regress the same row', async () => {
    const { preservePersistentCommandEntries } = await import('./index');

    const previousEntries = [
      {
        id: 'tool:cmd-1',
        order: 1,
        kind: 'tool',
        tone: 'positive',
        label: 'Tool',
        title: '',
        body: '',
        meta: '',
        sourceItemId: 'cmd-1',
        sourceTurnId: 'turn-1',
        sourceItemType: 'command_output',
        commandText: 'git status --short --branch',
        commandOutputTail: ['## dev...origin/dev'],
      },
    ] as any;

    const entries = [
      {
        id: 'tool:cmd-1',
        order: 1,
        kind: 'tool',
        tone: 'positive',
        label: 'Tool',
        title: 'Tool completed',
        body: '',
        meta: '20:00:03',
        sourceItemId: 'cmd-1',
        sourceTurnId: 'turn-1',
        sourceItemType: 'command_execution',
      },
    ] as any;

    const stabilized = preservePersistentCommandEntries(entries, previousEntries, {
      historyWindowStart: 0,
      historyWindowEnd: 1,
    });

    expect(stabilized).toHaveLength(1);
    expect(stabilized[0]).toMatchObject({
      body: '',
      meta: '',
      commandText: 'git status --short --branch',
      commandOutputTail: ['## dev...origin/dev'],
    });
  });

  it('keeps Agent Controller Session DOM work bounded for 10k item histories', async () => {
    const { computeHistoryVirtualWindow } = await import('./index');

    const entries = Array.from({ length: 10000 }, (_, index) => ({
      id: `row-${index}`,
      order: index + 1,
      kind: index % 4 === 0 ? 'user' : 'assistant',
      tone: 'info',
      label: index % 4 === 0 ? 'User' : 'Assistant',
      title: '',
      body:
        index % 29 === 0
          ? `Row ${index}\n\n| Metric | Value |\n| :--- | ---: |\n| retained | ${index % 100} |`
          : `Row ${index} `.repeat(18),
      meta: 'now',
    })) as any;

    const desktopWindow = computeHistoryVirtualWindow(entries, 480000, 900, 1200);
    const mobileWindow = computeHistoryVirtualWindow(entries, 620000, 640, 390);

    expect(desktopWindow.start).toBeGreaterThan(0);
    expect(desktopWindow.end).toBeLessThan(entries.length);
    expect(desktopWindow.end - desktopWindow.start).toBeLessThanOrEqual(40);
    expect(desktopWindow.topSpacerPx).toBeGreaterThan(0);
    expect(desktopWindow.bottomSpacerPx).toBeGreaterThan(0);

    expect(mobileWindow.start).toBeGreaterThan(0);
    expect(mobileWindow.end).toBeLessThan(entries.length);
    expect(mobileWindow.end - mobileWindow.start).toBeLessThanOrEqual(32);
    expect(mobileWindow.topSpacerPx).toBeGreaterThan(0);
    expect(mobileWindow.bottomSpacerPx).toBeGreaterThan(0);
  });

  it('keeps the pending prepend anchor inside a bounded render corridor', async () => {
    const { createAgentHistoryRender } = await import('./historyRender');

    const historyViewport = createMockDomNode({
      childNodes: [],
      children: [],
      clientHeight: 606,
      clientWidth: 900,
      scrollTop: 33771,
      scrollHeight: 56000,
      querySelector: vi.fn(() => null),
      getBoundingClientRect: vi.fn(() => ({ top: 0, bottom: 606 })),
    });
    const scrollButton = createMockDomNode();
    const composerShell = createMockDomNode();
    const composerInterruption = createMockDomNode();
    const panel = createMockDomNode({
      querySelector: vi.fn((selector: string) => {
        switch (selector) {
          case '[data-agent-field="history"]':
            return historyViewport;
          case '[data-agent-field="scroll-to-bottom"]':
            return scrollButton;
          case '[data-agent-field="composer-shell"]':
            return composerShell;
          case '[data-agent-field="composer-interruption"]':
            return composerInterruption;
          default:
            return null;
        }
      }),
    });
    const createdNodes = new Map<string, any>();
    const state = {
      panel,
      snapshot: {
        historyWindowStart: 320,
        historyWindowEnd: 406,
        historyCount: 520,
        provider: 'codex',
        requests: [],
      },
      historyViewport,
      historyEntries: [],
      historyRenderedNodes: new Map(),
      historyMeasuredHeights: new Map(),
      historyObservedHeights: new Map(),
      historyMeasuredWidthBucket: 0,
      historyLeadingPlaceholders: [],
      historyTrailingPlaceholders: [],
      historyEmptyState: null,
      pendingHistoryPrependAnchor: {
        entryId: 'row-360',
        topOffsetPx: 24,
        absoluteIndex: 360,
      },
      historyLastVirtualWindowKey: null,
      historyAutoScrollPinned: false,
      historyLastScrollMetrics: null,
      activationState: 'ready',
      assistantMarkdownCache: new Map(),
      runtimeStats: null,
    } as any;
    const scheduleHistoryRender = vi.fn();
    const render = createAgentHistoryRender({
      getState: () => state,
      scheduleHistoryRender,
      syncAgentViewPresentation: vi.fn(),
      createHistoryEntry: vi.fn((entry: any) => {
        const node = createMockDomNode({
          textContent: entry.body,
          getBoundingClientRect: vi.fn(() => ({ top: 24, bottom: 124, height: 100 })),
        });
        createdNodes.set(entry.id, node);
        return node;
      }),
      createHistorySpacer: vi.fn((heightPx: number) =>
        createMockDomNode({
          className: 'agent-history-spacer',
          style: { height: `${heightPx}px` },
        }),
      ),
      createRequestActionBlock: vi.fn(() => createMockDomNode()),
      pruneAssistantMarkdownCache: vi.fn(),
      renderRuntimeStats: vi.fn(),
    });

    const entries = Array.from({ length: 86 }, (_, index) => ({
      id: `row-${320 + index}`,
      order: 321 + index,
      kind: 'assistant',
      tone: 'info',
      label: 'Assistant',
      title: '',
      body: `Row ${320 + index}`,
      meta: 'now',
    })) as any;

    render.renderActivationView('s1', panel, state, entries);

    expect(historyViewport.childNodes.length).toBeLessThan(entries.length + 2);
    expect(state.historyRenderedNodes.size).toBeLessThan(entries.length);
    expect(state.historyRenderedNodes.has('row-360')).toBe(true);
    expect(state.pendingHistoryPrependAnchor).toBeNull();
    expect(scheduleHistoryRender).not.toHaveBeenCalled();
  });

  it('does not backpressure the progress navigator from passive row-height remeasurement', async () => {
    const { createAgentHistoryRender } = await import('./historyRender');

    const historyViewport = createMockDomNode({
      childNodes: [],
      children: [],
      clientHeight: 606,
      clientWidth: 900,
      scrollTop: 2800,
      scrollHeight: 12000,
      querySelector: vi.fn(() => null),
      getBoundingClientRect: vi.fn(() => ({ top: 0, bottom: 606, height: 606 })),
    });
    const progressNav = createMockDomNode({
      clientHeight: 606,
      clientWidth: 14,
      hidden: false,
      dataset: {},
      setAttribute: vi.fn(),
      getBoundingClientRect: vi.fn(() => ({ top: 0, height: 606 })),
    });
    const progressThumb = createMockDomNode({
      style: {} as CSSStyleDeclaration,
    });
    const scrollButton = createMockDomNode();
    const composerShell = createMockDomNode();
    const composerInterruption = createMockDomNode();
    const panel = createMockDomNode({
      querySelector: vi.fn((selector: string) => {
        switch (selector) {
          case '[data-agent-field="history"]':
            return historyViewport;
          case '[data-agent-field="history-progress-nav"]':
            return progressNav;
          case '[data-agent-field="history-progress-thumb"]':
            return progressThumb;
          case '[data-agent-field="scroll-to-bottom"]':
            return scrollButton;
          case '[data-agent-field="composer-shell"]':
            return composerShell;
          case '[data-agent-field="composer-interruption"]':
            return composerInterruption;
          default:
            return null;
        }
      }),
    });
    const state = {
      panel,
      snapshot: {
        historyWindowStart: 0,
        historyWindowEnd: 120,
        historyCount: 120,
        provider: 'codex',
        requests: [],
      },
      historyViewport,
      historyProgressNav: progressNav,
      historyProgressThumb: progressThumb,
      historyEntries: [],
      historyRenderedNodes: new Map(),
      historyMeasuredHeights: new Map(),
      historyObservedHeights: new Map(),
      historyMeasuredHeightsByBucket: new Map(),
      historyObservedHeightsByBucket: new Map(),
      historyObservedHeightSamplesByBucket: new Map(),
      historyMeasuredWidthBucket: 0,
      historyLeadingPlaceholders: [],
      historyTrailingPlaceholders: [],
      historyEmptyState: null,
      pendingHistoryPrependAnchor: null,
      pendingHistoryLayoutAnchor: null,
      historyPendingJumpTargetIndex: null,
      historyPendingJumpAlign: null,
      historyLastVirtualWindowKey: null,
      historyAutoScrollPinned: false,
      historyNavigatorMode: 'browse',
      historyNavigatorAnchorIndex: 47,
      historyNavigatorDragTargetIndex: null,
      historyLastScrollMetrics: null,
      activationState: 'ready',
      assistantMarkdownCache: new Map(),
      runtimeStats: null,
      historyMeasurementObserver: null,
      requestBusyIds: new Set(),
      activationActionBusy: false,
      requestDraftAnswersById: {},
      requestQuestionIndexById: {},
    } as any;

    const render = createAgentHistoryRender({
      getState: () => state,
      scheduleHistoryRender: vi.fn(),
      syncAgentViewPresentation: vi.fn(),
      createHistoryEntry: vi.fn((entry: any) =>
        createMockDomNode({
          textContent: entry.body,
          getBoundingClientRect: vi.fn(() => ({
            top: 0,
            bottom: entry.estimatedHeightPx,
            height: entry.estimatedHeightPx,
          })),
        }),
      ),
      createHistorySpacer: vi.fn((heightPx: number) =>
        createMockDomNode({
          className: 'agent-history-spacer',
          style: { height: `${heightPx}px` },
        }),
      ),
      createRequestActionBlock: vi.fn(() => createMockDomNode()),
      pruneAssistantMarkdownCache: vi.fn(),
      renderRuntimeStats: vi.fn(),
    });

    const entries = Array.from({ length: 120 }, (_, index) => ({
      id: `row-${index}`,
      order: index + 1,
      kind: 'assistant',
      tone: 'info',
      label: 'Assistant',
      title: '',
      body: `Row ${index + 1}`,
      meta: 'now',
      estimatedHeightPx: index % 6 === 0 ? 420 : 64,
    })) as any;

    render.renderActivationView('s1', panel, state, entries);
    const initialThumbTop = String(progressThumb.style.top);
    const initialValueNowCalls = progressNav.setAttribute.mock.calls.filter(
      ([name]: [string]) => name === 'aria-valuenow',
    );
    const initialValueNow = initialValueNowCalls[initialValueNowCalls.length - 1]?.[1];

    for (const entry of entries) {
      state.historyMeasuredHeights.set(entry.id, entry.estimatedHeightPx > 100 ? 72 : 520);
    }

    render.renderActivationView('s1', panel, state, entries);
    const afterRemeasureValueNowCalls = progressNav.setAttribute.mock.calls.filter(
      ([name]: [string]) => name === 'aria-valuenow',
    );
    const afterRemeasureValueNow =
      afterRemeasureValueNowCalls[afterRemeasureValueNowCalls.length - 1]?.[1];

    expect(state.historyNavigatorAnchorIndex).toBe(47);
    expect(initialValueNow).toBe('48');
    expect(afterRemeasureValueNow).toBe('48');
    expect(String(progressThumb.style.top)).toBe(initialThumbTop);
  });

  it('does not snap back to old rendered rows during a recent fast user scroll gap', async () => {
    const { createAgentHistoryRender } = await import('./historyRender');

    const historyViewport = createMockDomNode({
      childNodes: [],
      children: [],
      clientHeight: 606,
      clientWidth: 900,
      scrollTop: 2000,
      scrollHeight: 6000,
      querySelector: vi.fn(() => null),
      getBoundingClientRect: vi.fn(() => ({ top: 0, bottom: 606 })),
    });
    const scrollButton = createMockDomNode();
    const composerShell = createMockDomNode();
    const composerInterruption = createMockDomNode();
    const panel = createMockDomNode({
      querySelector: vi.fn((selector: string) => {
        switch (selector) {
          case '[data-agent-field="history"]':
            return historyViewport;
          case '[data-agent-field="scroll-to-bottom"]':
            return scrollButton;
          case '[data-agent-field="composer-shell"]':
            return composerShell;
          case '[data-agent-field="composer-interruption"]':
            return composerInterruption;
          default:
            return null;
        }
      }),
    });
    const syncViewportHistoryWindow = vi.fn();
    const state = {
      panel,
      snapshot: {
        historyWindowStart: 0,
        historyWindowEnd: 80,
        historyCount: 400,
        provider: 'codex',
        requests: [],
      },
      historyViewport,
      historyEntries: [],
      historyRenderedNodes: new Map(),
      historyMeasuredHeights: new Map(),
      historyObservedHeights: new Map(),
      historyMeasuredHeightsByBucket: new Map(),
      historyObservedHeightsByBucket: new Map(),
      historyObservedHeightSamplesByBucket: new Map(),
      historyMeasuredWidthBucket: 0,
      historyLeadingPlaceholders: [],
      historyTrailingPlaceholders: [],
      historyEmptyState: null,
      pendingHistoryPrependAnchor: null,
      pendingHistoryLayoutAnchor: null,
      historyLastVirtualWindowKey: null,
      historyAutoScrollPinned: false,
      historyLastScrollMetrics: null,
      historyLastUserScrollIntentAt: Date.now(),
      activationState: 'ready',
      assistantMarkdownCache: new Map(),
      runtimeStats: null,
      historyMeasurementObserver: null,
      requestBusyIds: new Set(),
      activationActionBusy: false,
      requestDraftAnswersById: {},
      requestQuestionIndexById: {},
    } as any;

    const render = createAgentHistoryRender({
      getState: () => state,
      scheduleHistoryRender: vi.fn(),
      syncAgentViewPresentation: vi.fn(),
      createHistoryEntry: vi.fn((entry: any) =>
        createMockDomNode({
          textContent: entry.body,
          getBoundingClientRect: vi.fn(() => ({ top: -1200, bottom: -1100, height: 100 })),
        }),
      ),
      createHistorySpacer: vi.fn(),
      createHistoryPlaceholderBlock: vi.fn((args: any) =>
        createMockDomNode({
          className: 'agent-history-placeholder',
          style: { height: `${args.heightPx}px` },
          dataset: { direction: args.direction },
          querySelector: vi.fn(() => null),
        }),
      ),
      createRequestActionBlock: vi.fn(() => createMockDomNode()),
      pruneAssistantMarkdownCache: vi.fn(),
      renderRuntimeStats: vi.fn(),
      syncViewportHistoryWindow,
    });

    const entries = Array.from({ length: 80 }, (_, index) => ({
      id: `row-${index}`,
      order: index + 1,
      kind: 'assistant',
      tone: 'info',
      label: 'Assistant',
      title: '',
      body: `Row ${index + 1}`,
      meta: 'now',
    })) as any;

    render.renderActivationView('s1', panel, state, entries);

    expect(historyViewport.scrollTop).toBe(2000);
    expect(syncViewportHistoryWindow).not.toHaveBeenCalled();
  });

  it('requests a viewport-centered history sync when placeholders fill the viewport without a concrete row', async () => {
    const { createAgentHistoryRender } = await import('./historyRender');

    const historyViewport = createMockDomNode({
      childNodes: [],
      children: [],
      clientHeight: 606,
      clientWidth: 900,
      scrollTop: 2000,
      scrollHeight: 6000,
      querySelector: vi.fn(() => null),
      getBoundingClientRect: vi.fn(() => ({ top: 0, bottom: 606 })),
    });
    const scrollButton = createMockDomNode();
    const composerShell = createMockDomNode();
    const composerInterruption = createMockDomNode();
    const panel = createMockDomNode({
      querySelector: vi.fn((selector: string) => {
        switch (selector) {
          case '[data-agent-field="history"]':
            return historyViewport;
          case '[data-agent-field="scroll-to-bottom"]':
            return scrollButton;
          case '[data-agent-field="composer-shell"]':
            return composerShell;
          case '[data-agent-field="composer-interruption"]':
            return composerInterruption;
          default:
            return null;
        }
      }),
    });
    const syncViewportHistoryWindow = vi.fn();
    const state = {
      panel,
      snapshot: {
        historyWindowStart: 0,
        historyWindowEnd: 80,
        historyCount: 400,
        provider: 'codex',
        requests: [],
      },
      historyViewport,
      historyEntries: [],
      historyRenderedNodes: new Map(),
      historyMeasuredHeights: new Map(),
      historyObservedHeights: new Map(),
      historyMeasuredHeightsByBucket: new Map(),
      historyObservedHeightsByBucket: new Map(),
      historyObservedHeightSamplesByBucket: new Map(),
      historyMeasuredWidthBucket: 0,
      historyLeadingPlaceholders: [],
      historyTrailingPlaceholders: [],
      historyEmptyState: null,
      pendingHistoryPrependAnchor: null,
      pendingHistoryLayoutAnchor: null,
      historyLastVirtualWindowKey: null,
      historyAutoScrollPinned: false,
      historyLastScrollMetrics: null,
      activationState: 'ready',
      assistantMarkdownCache: new Map(),
      runtimeStats: null,
      historyMeasurementObserver: null,
      requestBusyIds: new Set(),
      activationActionBusy: false,
      requestDraftAnswersById: {},
      requestQuestionIndexById: {},
    } as any;

    const render = createAgentHistoryRender({
      getState: () => state,
      scheduleHistoryRender: vi.fn(),
      syncAgentViewPresentation: vi.fn(),
      createHistoryEntry: vi.fn((entry: any) =>
        createMockDomNode({
          textContent: entry.body,
          getBoundingClientRect: vi.fn(() => ({ top: -1200, bottom: -1100, height: 100 })),
        }),
      ),
      createHistorySpacer: vi.fn(),
      createHistoryPlaceholderBlock: vi.fn((args: any) =>
        createMockDomNode({
          className: 'agent-history-placeholder',
          style: { height: `${args.heightPx}px` },
          dataset: { direction: args.direction },
          querySelector: vi.fn(() => null),
        }),
      ),
      createRequestActionBlock: vi.fn(() => createMockDomNode()),
      pruneAssistantMarkdownCache: vi.fn(),
      renderRuntimeStats: vi.fn(),
      syncViewportHistoryWindow,
    });

    const entries = Array.from({ length: 80 }, (_, index) => ({
      id: `row-${index}`,
      order: index + 1,
      kind: 'assistant',
      tone: 'info',
      label: 'Assistant',
      title: '',
      body: `Row ${index + 1}`,
      meta: 'now',
    })) as any;

    render.renderActivationView('s1', panel, state, entries);

    expect(syncViewportHistoryWindow).toHaveBeenCalledWith('s1');
  });

  it('does not remeasure unchanged visible rows on every render pass', async () => {
    const { createAgentHistoryRender } = await import('./historyRender');

    const historyViewport = createMockDomNode({
      childNodes: [],
      children: [],
      clientHeight: 606,
      clientWidth: 900,
      scrollTop: 0,
      scrollHeight: 5600,
      querySelector: vi.fn(() => null),
      getBoundingClientRect: vi.fn(() => ({ top: 0, bottom: 606 })),
    });
    const scrollButton = createMockDomNode();
    const composerShell = createMockDomNode();
    const composerInterruption = createMockDomNode();
    const panel = createMockDomNode({
      querySelector: vi.fn((selector: string) => {
        switch (selector) {
          case '[data-agent-field="history"]':
            return historyViewport;
          case '[data-agent-field="scroll-to-bottom"]':
            return scrollButton;
          case '[data-agent-field="composer-shell"]':
            return composerShell;
          case '[data-agent-field="composer-interruption"]':
            return composerInterruption;
          default:
            return null;
        }
      }),
    });
    const measuredNode = createMockDomNode({
      textContent: 'Row 0',
      getBoundingClientRect: vi.fn(() => ({ top: 24, bottom: 124, height: 100 })),
    });
    const state = {
      panel,
      snapshot: {
        historyWindowStart: 0,
        historyWindowEnd: 1,
        historyCount: 1,
        provider: 'codex',
        requests: [],
      },
      historyViewport,
      historyEntries: [],
      historyRenderedNodes: new Map(),
      historyMeasuredHeights: new Map(),
      historyObservedHeights: new Map(),
      historyMeasuredHeightsByBucket: new Map(),
      historyObservedHeightsByBucket: new Map(),
      historyObservedHeightSamplesByBucket: new Map(),
      historyMeasuredWidthBucket: 0,
      historyLeadingPlaceholders: [],
      historyTrailingPlaceholders: [],
      historyEmptyState: null,
      pendingHistoryPrependAnchor: null,
      pendingHistoryLayoutAnchor: null,
      historyLastVirtualWindowKey: null,
      historyAutoScrollPinned: false,
      historyLastScrollMetrics: null,
      activationState: 'ready',
      assistantMarkdownCache: new Map(),
      runtimeStats: null,
      historyMeasurementObserver: null,
    } as any;

    const render = createAgentHistoryRender({
      getState: () => state,
      scheduleHistoryRender: vi.fn(),
      syncAgentViewPresentation: vi.fn(),
      createHistoryEntry: vi.fn(() => measuredNode),
      createHistorySpacer: vi.fn((heightPx: number) =>
        createMockDomNode({
          className: 'agent-history-spacer',
          style: { height: `${heightPx}px` },
        }),
      ),
      createRequestActionBlock: vi.fn(() => createMockDomNode()),
      pruneAssistantMarkdownCache: vi.fn(),
      renderRuntimeStats: vi.fn(),
    });

    const entries = [
      {
        id: 'row-0',
        order: 1,
        kind: 'assistant',
        tone: 'info',
        label: 'Assistant',
        title: '',
        body: 'Row 0',
        meta: 'now',
      },
    ] as any;

    render.renderActivationView('s1', panel, state, entries);
    render.renderActivationView('s1', panel, state, entries);

    // One border-box measurement plus capture/restore reads for the stable browse anchor.
    expect(measuredNode.getBoundingClientRect).toHaveBeenCalledTimes(3);
  });

  it('keeps the captured anchor absolute index inside a viewport-centered history fetch', async () => {
    const { createAgentHistoryRender } = await import('./historyRender');

    const historyViewport = createMockDomNode({
      clientHeight: 606,
      clientWidth: 900,
      scrollTop: 26875,
    });
    const state = {
      historyViewport,
      historyEntries: Array.from({ length: 20 }, (_, index) => ({
        id: `row-${480 + index}`,
        order: 481 + index,
        kind: 'assistant',
        tone: 'info',
        label: 'Assistant',
        title: '',
        body: `Row ${480 + index}`,
        meta: 'now',
      })),
      snapshot: {
        historyWindowStart: 480,
        historyWindowEnd: 500,
        historyCount: 520,
      },
      pendingHistoryPrependAnchor: {
        entryId: 'row-496',
        topOffsetPx: 32,
        absoluteIndex: 496,
      },
      historyObservedHeights: new Map(),
      historyMeasuredHeights: new Map(),
      historyMeasuredWidthBucket: 0,
    } as any;

    const render = createAgentHistoryRender({
      getState: () => state,
      scheduleHistoryRender: vi.fn(),
      syncAgentViewPresentation: vi.fn(),
      createHistoryEntry: vi.fn(),
      createHistorySpacer: vi.fn(),
      createRequestActionBlock: vi.fn(),
      pruneAssistantMarkdownCache: vi.fn(),
      renderRuntimeStats: vi.fn(),
    });

    const requestedWindow = render.getViewportCenteredHistoryWindowRequest(state, {
      fetchAheadItems: 30,
      anchorAbsoluteIndex: state.pendingHistoryPrependAnchor.absoluteIndex,
    });

    expect(requestedWindow).not.toBeNull();
    expect(requestedWindow?.startIndex).toBeLessThanOrEqual(496);
    expect((requestedWindow?.startIndex ?? 0) + (requestedWindow?.count ?? 0)).toBeGreaterThan(496);
  });

  it('anchors sparse rendered history by canonical order instead of visual array position', async () => {
    const { createAgentHistoryRender } = await import('./historyRender');
    const historyViewport = createMockDomNode({
      clientHeight: 600,
      clientWidth: 900,
      scrollTop: 1200,
      getBoundingClientRect: () => ({ top: 0, bottom: 600, height: 600 }),
    });
    const anchorNode = createMockDomNode({
      getBoundingClientRect: () => ({ top: -20, bottom: 120, height: 140 }),
    });
    const state = {
      historyViewport,
      historyEntries: [
        { id: 'visual-1', order: 131 },
        { id: 'visual-2', order: 145 },
      ],
      historyRenderedNodes: new Map([
        [
          'visual-1',
          {
            node: createMockDomNode({
              getBoundingClientRect: () => ({ top: -200, bottom: -50, height: 150 }),
            }),
          },
        ],
        ['visual-2', { node: anchorNode }],
      ]),
      snapshot: { historyWindowStart: 100, historyWindowEnd: 150, historyCount: 180 },
      historyAutoScrollPinned: false,
      pendingHistoryPrependAnchor: null,
      pendingHistoryLayoutAnchor: null,
    } as any;
    const render = createAgentHistoryRender({
      getState: () => state,
      scheduleHistoryRender: vi.fn(),
      syncAgentViewPresentation: vi.fn(),
      createHistoryEntry: vi.fn(),
      createHistorySpacer: vi.fn(),
      createRequestActionBlock: vi.fn(),
      pruneAssistantMarkdownCache: vi.fn(),
      renderRuntimeStats: vi.fn(),
    });

    expect(render.captureHistoryViewportAnchor(state)).toBe(true);
    expect(state.pendingHistoryPrependAnchor?.absoluteIndex).toBe(144);
  });

  it('keeps the previous turn assistant row settled when a new turn starts before the next answer arrives', async () => {
    const { withLiveAssistantState } = await import('./index');

    const snapshot = {
      currentTurn: {
        turnId: 'turn-2',
        state: 'running',
      },
    } as any;

    const entries = [
      {
        id: 'user-1',
        order: 1,
        kind: 'user',
        tone: 'positive',
        label: 'You',
        title: '',
        body: 'First question',
        meta: 'now',
        sourceTurnId: 'turn-1',
      },
      {
        id: 'assistant-1',
        order: 2,
        kind: 'assistant',
        tone: 'info',
        label: 'Assistant',
        title: '',
        body: '# Final answer',
        meta: 'now',
        sourceTurnId: 'turn-1',
      },
      {
        id: 'user-2',
        order: 3,
        kind: 'user',
        tone: 'positive',
        label: 'You',
        title: '',
        body: 'Follow-up question',
        meta: 'now',
        sourceTurnId: 'turn-2',
      },
    ] as any;

    const marked = withLiveAssistantState(snapshot, entries);
    expect(marked.some((entry: any) => entry.kind === 'assistant' && entry.live)).toBe(false);
  });

  it('updates the busy elapsed label in place instead of forcing a full AppServerControl render on each timer tick', async () => {
    const { syncBusyIndicatorTicker } = await import('./historyProcessing');

    let timerCallback: (() => void) | null = null;
    (window.setTimeout as any) = vi.fn((callback: () => void) => {
      timerCallback = callback;
      return 1;
    });

    const renderCurrentAgentView = vi.fn();
    const updateBusyIndicatorElapsed = vi.fn(() => true);
    const state = { busyIndicatorTickHandle: null } as any;
    const snapshot = {
      sessionId: 's-busy',
      currentTurn: {
        startedAt: new Date(Date.now() - 5_000).toISOString(),
      },
    } as any;

    syncBusyIndicatorTicker({
      snapshot,
      state,
      entries: [{ id: 'busy', busyIndicator: true }] as any,
      renderCurrentAgentView,
      updateBusyIndicatorElapsed,
    });

    expect(typeof timerCallback).toBe('function');
    timerCallback?.();

    expect(updateBusyIndicatorElapsed).toHaveBeenCalledWith('s-busy', '5s');
    expect(renderCurrentAgentView).not.toHaveBeenCalled();
    expect(window.setTimeout).toHaveBeenCalledTimes(2);
  });

  it('caps visible command output tails from the AI Agents setting', async () => {
    const { createAgentHistoryDom, resolveToolCallOutputLineLimit } = await import('./index');
    currentSettings = { showUnknownAgentMessages: true, toolCallOutputLines: 5 };
    const historyDom = createAgentHistoryDom({
      getState: () => undefined,
      refreshAppServerControlSnapshot: vi.fn(),
      renderCurrentAgentView: vi.fn(),
      retryAppServerControlActivation: vi.fn(),
      logWarn: vi.fn(),
    });

    const article = historyDom.createHistoryEntry(
      {
        id: 'tool-command-cap',
        order: 1,
        kind: 'tool',
        tone: 'positive',
        label: 'Tool',
        title: 'Tool completed',
        body: 'pwsh -Command Get-Content out.log',
        meta: '20:00:00',
        sourceItemType: 'command_execution',
        commandText: 'pwsh -Command Get-Content out.log',
        commandOutputTail: Array.from({ length: 12 }, (_, index) => `line ${index + 1}`),
      },
      's1',
    ) as any;

    const commandBody = article.children.find((child: any) =>
      String(child.className).includes('agent-history-command-body'),
    );
    const output = commandBody.children.find((child: any) =>
      String(child.className).includes('agent-history-command-output-tail'),
    );

    expect(resolveToolCallOutputLineLimit()).toBe(5);
    expect(output.textContent).toBe('line 1\nline 2\nline 3\nline 4\nline 5');
  });

  it('trims diff preamble noise and caps rendered AppServerControl diffs with an ellipsis row', async () => {
    const { buildRenderedDiffLines } = await import('./index');

    const body =
      'diff --git a/report.md b/report.md\n' +
      'new file mode 100644\n' +
      'index 0000000..1111111\n' +
      '--- /dev/null\n' +
      '+++ b/report.md\n' +
      '@@ -0,0 +1,205 @@\n' +
      Array.from({ length: 205 }, (_, index) => `+line ${index + 1}`).join('\n');
    const rendered = buildRenderedDiffLines(body);

    expect(rendered).toHaveLength(201);
    expect(rendered[0]).toEqual({
      text: 'Edited report.md',
      className: 'agent-history-diff-line-file',
    });
    expect(rendered[1]).toEqual({
      text: '@@ -0,0 +1,205 @@',
      className: 'agent-history-diff-line-hunk',
    });
    expect(rendered.some((line) => line.text.startsWith('diff --git'))).toBe(false);
    expect(rendered.some((line) => line.text.startsWith('new file mode'))).toBe(false);
    expect(rendered.at(-1)).toEqual({
      text: '... 7 more diff lines omitted ...',
      className: 'agent-history-diff-line-ellipsis',
    });
  });

  it('applies canonical live deltas directly into the materialized history window', async () => {
    const { applyCanonicalAppServerControlDelta } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-28T10:00:00Z',
      latestSequence: 1,
      historyCount: 1,
      estimatedTotalHistoryHeightPx: 52,
      estimatedHistoryBeforeWindowPx: 0,
      estimatedHistoryAfterWindowPx: 0,
      historyWindowStart: 0,
      historyWindowEnd: 1,
      hasOlderHistory: false,
      hasNewerHistory: false,
      session: {
        state: 'running',
        stateLabel: 'Running',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-28T10:00:00Z',
      },
      thread: {
        threadId: 'thread-1',
        state: 'active',
        stateLabel: 'Active',
      },
      currentTurn: {
        turnId: 'turn-1',
        state: 'running',
        stateLabel: 'Running',
        model: 'gpt-5.4',
        effort: 'high',
        startedAt: '2026-03-28T10:00:00Z',
        completedAt: null,
      },
      streams: {
        assistantText: 'Hel',
        reasoningText: '',
        reasoningSummaryText: '',
        planText: '',
        commandOutput: '',
        fileChangeOutput: '',
        unifiedDiff: '',
      },
      history: [
        {
          entryId: 'assistant:assistant-1',
          order: 1,
          estimatedHeightPx: 52,
          kind: 'assistant',
          turnId: 'turn-1',
          itemId: 'assistant-1',
          requestId: null,
          status: 'streaming',
          itemType: 'assistant_text',
          title: null,
          body: 'Hel',
          attachments: [],
          streaming: true,
          createdAt: '2026-03-28T10:00:00Z',
          updatedAt: '2026-03-28T10:00:00Z',
        },
      ],
      items: [],
      requests: [],
      notices: [],
    } as any;

    const state = {
      snapshot,
      historyWindowStart: 0,
      historyWindowCount: 80,
    } as any;

    const requiresWindowRefresh = applyCanonicalAppServerControlDelta(state, {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-28T10:00:01Z',
      latestSequence: 2,
      historyCount: 1,
      estimatedTotalHistoryHeightPx: 68,
      session: {
        state: 'running',
        stateLabel: 'Running',
        reason: 'Codex turn started.',
        lastError: null,
        lastEventAt: '2026-03-28T10:00:01Z',
      },
      thread: {
        threadId: 'thread-1',
        state: 'active',
        stateLabel: 'Active',
      },
      currentTurn: {
        turnId: 'turn-1',
        state: 'running',
        stateLabel: 'Running',
        model: 'gpt-5.4',
        effort: 'high',
        startedAt: '2026-03-28T10:00:00Z',
        completedAt: null,
      },
      streams: {
        assistantText: 'Hello',
        reasoningText: '',
        reasoningSummaryText: '',
        planText: '',
        commandOutput: '',
        fileChangeOutput: '',
        unifiedDiff: '',
      },
      historyUpserts: [
        {
          entryId: 'assistant:assistant-1',
          order: 1,
          estimatedHeightPx: 68,
          kind: 'assistant',
          turnId: 'turn-1',
          itemId: 'assistant-1',
          requestId: null,
          status: 'streaming',
          itemType: 'assistant_text',
          title: null,
          body: 'Hello',
          attachments: [],
          streaming: true,
          createdAt: '2026-03-28T10:00:00Z',
          updatedAt: '2026-03-28T10:00:01Z',
        },
      ],
      historyRemovals: [],
      itemUpserts: [],
      itemRemovals: [],
      requestUpserts: [],
      requestRemovals: [],
      noticeUpserts: [],
    });

    expect(requiresWindowRefresh).toBe(false);
    expect(snapshot.latestSequence).toBe(2);
    expect(snapshot.generatedAt).toBe('2026-03-28T10:00:01Z');
    expect(snapshot.streams.assistantText).toBe('Hello');
    expect(snapshot.history).toHaveLength(1);
    expect(snapshot.history[0]?.body).toBe('Hello');
    expect(snapshot.estimatedHistoryBeforeWindowPx).toBe(0);
    expect(snapshot.history[0]?.streaming).toBe(true);
    expect(snapshot.historyWindowStart).toBe(0);
    expect(snapshot.historyWindowEnd).toBe(1);
    expect(snapshot.hasNewerHistory).toBe(false);
  });

  it('keeps the live-edge retention target when a short initial history window receives more rows', async () => {
    const { applyCanonicalAppServerControlDelta } = await import('./index');

    const snapshot = {
      sessionId: 's-live-retain',
      provider: 'codex',
      generatedAt: '2026-04-13T10:00:00Z',
      latestSequence: 1,
      historyCount: 1,
      historyWindowStart: 0,
      historyWindowEnd: 1,
      hasOlderHistory: false,
      hasNewerHistory: false,
      session: {
        state: 'running',
        stateLabel: 'Running',
        reason: null,
        lastError: null,
        lastEventAt: '2026-04-13T10:00:00Z',
      },
      thread: {
        threadId: 'thread-live-retain',
        state: 'active',
        stateLabel: 'Active',
      },
      currentTurn: {
        turnId: 'turn-live-retain',
        state: 'running',
        stateLabel: 'Running',
        model: 'gpt-5.4',
        effort: 'high',
        startedAt: '2026-04-13T10:00:00Z',
        completedAt: null,
      },
      quickSettings: {
        model: 'gpt-5.4',
        effort: 'high',
        planMode: 'off',
        permissionMode: 'manual',
      },
      streams: {
        assistantText: '',
        reasoningText: '',
        reasoningSummaryText: '',
        planText: '',
        commandOutput: '',
        fileChangeOutput: '',
        unifiedDiff: '',
      },
      history: [
        {
          entryId: 'user:turn-live-retain',
          order: 1,
          kind: 'user',
          turnId: 'turn-live-retain',
          itemId: 'user-live-retain',
          requestId: null,
          status: 'completed',
          itemType: 'user_message',
          title: null,
          body: 'Keep the prior history visible while streaming.',
          attachments: [],
          streaming: false,
          createdAt: '2026-04-13T10:00:00Z',
          updatedAt: '2026-04-13T10:00:00Z',
        },
      ],
      items: [],
      requests: [],
      notices: [],
    } as any;

    const state = {
      snapshot,
      historyWindowStart: 0,
      historyWindowCount: 1,
      historyWindowTargetCount: 80,
      historyAutoScrollPinned: true,
    } as any;

    const requiresWindowRefresh = applyCanonicalAppServerControlDelta(state, {
      sessionId: 's-live-retain',
      provider: 'codex',
      generatedAt: '2026-04-13T10:00:01Z',
      latestSequence: 2,
      historyCount: 2,
      session: snapshot.session,
      thread: snapshot.thread,
      currentTurn: snapshot.currentTurn,
      quickSettings: snapshot.quickSettings,
      streams: {
        ...snapshot.streams,
        assistantText: 'Streaming reply',
      },
      historyUpserts: [
        {
          entryId: 'assistant:turn-live-retain',
          order: 2,
          kind: 'assistant',
          turnId: 'turn-live-retain',
          itemId: 'assistant-live-retain',
          requestId: null,
          status: 'streaming',
          itemType: 'assistant_text',
          title: null,
          body: 'Streaming reply',
          attachments: [],
          streaming: true,
          createdAt: '2026-04-13T10:00:01Z',
          updatedAt: '2026-04-13T10:00:01Z',
        },
      ],
      historyRemovals: [],
      itemUpserts: [],
      itemRemovals: [],
      requestUpserts: [],
      requestRemovals: [],
      noticeUpserts: [],
    });

    expect(requiresWindowRefresh).toBe(false);
    expect(snapshot.historyWindowStart).toBe(0);
    expect(snapshot.historyWindowEnd).toBe(2);
    expect(snapshot.history).toHaveLength(2);
    expect(snapshot.history.map((entry: any) => entry.entryId)).toEqual([
      'user:turn-live-retain',
      'assistant:turn-live-retain',
    ]);
    expect(state.historyWindowCount).toBe(2);
    expect(state.historyWindowTargetCount).toBe(80);
  });

  it('does not refresh a browsed snapshot for a new tail entry outside its retained window', async () => {
    const { applyCanonicalAppServerControlDelta } = await import('./index');

    const snapshot = {
      sessionId: 's-scroll',
      provider: 'codex',
      generatedAt: '2026-03-28T10:00:00Z',
      latestSequence: 5,
      historyCount: 120,
      estimatedTotalHistoryHeightPx: 9600,
      estimatedHistoryBeforeWindowPx: 4200,
      estimatedHistoryAfterWindowPx: 3000,
      historyWindowStart: 40,
      historyWindowEnd: 80,
      hasOlderHistory: true,
      hasNewerHistory: true,
      session: {
        state: 'running',
        stateLabel: 'Running',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-28T10:00:00Z',
      },
      thread: {
        threadId: 'thread-scroll',
        state: 'active',
        stateLabel: 'Active',
      },
      currentTurn: {
        turnId: 'turn-scroll',
        state: 'running',
        stateLabel: 'Running',
        model: 'gpt-5.4',
        effort: 'high',
        startedAt: '2026-03-28T10:00:00Z',
        completedAt: null,
      },
      quickSettings: {
        model: 'gpt-5.4',
        effort: 'high',
        planMode: 'off',
        permissionMode: 'default',
      },
      streams: {
        assistantText: '',
        reasoningText: '',
        reasoningSummaryText: '',
        planText: '',
        commandOutput: '',
        fileChangeOutput: '',
        unifiedDiff: '',
      },
      history: [
        {
          entryId: 'assistant:window-1',
          order: 41,
          estimatedHeightPx: 84,
          kind: 'assistant',
          turnId: 'turn-scroll',
          itemId: 'assistant-window-1',
          requestId: null,
          status: 'completed',
          itemType: 'assistant_text',
          title: null,
          body: 'Older visible history',
          attachments: [],
          streaming: false,
          createdAt: '2026-03-28T09:59:00Z',
          updatedAt: '2026-03-28T09:59:00Z',
        },
      ],
      items: [],
      requests: [],
      notices: [],
    } as any;

    const state = {
      snapshot,
      historyWindowStart: 40,
      historyWindowCount: 40,
    } as any;

    const requiresWindowRefresh = applyCanonicalAppServerControlDelta(state, {
      sessionId: 's-scroll',
      provider: 'codex',
      generatedAt: '2026-03-28T10:00:02Z',
      latestSequence: 6,
      historyCount: 121,
      estimatedTotalHistoryHeightPx: 9684,
      session: snapshot.session,
      thread: snapshot.thread,
      currentTurn: snapshot.currentTurn,
      quickSettings: snapshot.quickSettings,
      streams: snapshot.streams,
      historyUpserts: [
        {
          entryId: 'assistant:new-tail',
          order: 121,
          estimatedHeightPx: 84,
          kind: 'assistant',
          turnId: 'turn-scroll',
          itemId: 'assistant-new-tail',
          requestId: null,
          status: 'completed',
          itemType: 'assistant_text',
          title: null,
          body: 'New tail entry',
          attachments: [],
          streaming: false,
          createdAt: '2026-03-28T10:00:02Z',
          updatedAt: '2026-03-28T10:00:02Z',
        },
      ],
      historyRemovals: [],
      itemUpserts: [],
      itemRemovals: [],
      requestUpserts: [],
      requestRemovals: [],
      noticeUpserts: [],
    });

    expect(requiresWindowRefresh).toBe(false);
    expect(snapshot.estimatedHistoryBeforeWindowPx).toBe(4200);
    expect(snapshot.estimatedHistoryAfterWindowPx).toBe(3000);
  });

  it('does not trim the front of a fully loaded history window while custom scrolling away from the live edge', async () => {
    const { applyCanonicalAppServerControlDelta } = await import('./index');

    const snapshot = {
      sessionId: 's-custom',
      provider: 'codex',
      generatedAt: '2026-03-28T10:00:00Z',
      latestSequence: 10,
      historyCount: 64,
      historyWindowStart: 0,
      historyWindowEnd: 64,
      hasOlderHistory: false,
      hasNewerHistory: false,
      session: {
        state: 'running',
        stateLabel: 'Running',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-28T10:00:00Z',
      },
      thread: {
        threadId: 'thread-custom',
        state: 'active',
        stateLabel: 'Active',
      },
      currentTurn: {
        turnId: 'turn-custom',
        state: 'running',
        stateLabel: 'Running',
        model: 'gpt-5.4',
        effort: 'high',
        startedAt: '2026-03-28T10:00:00Z',
        completedAt: null,
      },
      quickSettings: {
        model: 'gpt-5.4',
        effort: 'high',
        planMode: 'off',
        permissionMode: 'default',
      },
      streams: {
        assistantText: '',
        reasoningText: '',
        reasoningSummaryText: '',
        planText: '',
        commandOutput: '',
        fileChangeOutput: '',
        unifiedDiff: '',
      },
      history: Array.from({ length: 64 }, (_, index) => ({
        entryId: `assistant:${index + 1}`,
        order: index + 1,
        estimatedHeightPx: 84,
        kind: 'assistant',
        turnId: 'turn-custom',
        itemId: `assistant-${index + 1}`,
        requestId: null,
        status: 'completed',
        itemType: 'assistant_text',
        title: null,
        body: `Entry ${index + 1}`,
        attachments: [],
        streaming: false,
        createdAt: '2026-03-28T10:00:00Z',
        updatedAt: '2026-03-28T10:00:00Z',
      })),
      items: [],
      requests: [],
      notices: [],
    } as any;

    const state = {
      snapshot,
      historyWindowStart: 0,
      historyWindowCount: 64,
      historyAutoScrollPinned: false,
    } as any;

    const requiresWindowRefresh = applyCanonicalAppServerControlDelta(state, {
      sessionId: 's-custom',
      provider: 'codex',
      generatedAt: '2026-03-28T10:00:01Z',
      latestSequence: 11,
      historyCount: 65,
      session: snapshot.session,
      thread: snapshot.thread,
      currentTurn: snapshot.currentTurn,
      quickSettings: snapshot.quickSettings,
      streams: snapshot.streams,
      historyUpserts: [
        {
          entryId: 'assistant:65',
          order: 65,
          estimatedHeightPx: 84,
          kind: 'assistant',
          turnId: 'turn-custom',
          itemId: 'assistant-65',
          requestId: null,
          status: 'completed',
          itemType: 'assistant_text',
          title: null,
          body: 'Entry 65',
          attachments: [],
          streaming: false,
          createdAt: '2026-03-28T10:00:01Z',
          updatedAt: '2026-03-28T10:00:01Z',
        },
      ],
      historyRemovals: [],
      itemUpserts: [],
      itemRemovals: [],
      requestUpserts: [],
      requestRemovals: [],
      noticeUpserts: [],
    });

    expect(requiresWindowRefresh).toBe(false);
    expect(snapshot.historyWindowStart).toBe(0);
    expect(snapshot.historyWindowEnd).toBe(64);
    expect(snapshot.history[0]?.entryId).toBe('assistant:1');
    expect(snapshot.history).toHaveLength(64);
    expect(snapshot.hasNewerHistory).toBe(true);
  });
});
