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

function triggerWindowEvent(event: string): void {
  for (const listener of windowEventListeners.get(event) ?? []) {
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

  it('shows a dev error modal when AppServerControl activation fails', async () => {
    attachSessionAppServerControl.mockRejectedValue(new Error('AppServerControl attach failed'));
    getAppServerControlHistoryWindow.mockRejectedValue(
      new Error('AppServerControl snapshot unavailable'),
    );
    getAppServerControlEvents.mockRejectedValue(new Error('AppServerControl events unavailable'));

    const { initAgentView } = await import('./index');
    initAgentView();

    const activate = onTabActivated.mock.calls[0]?.[1] as
      | ((sessionId: string, panel: HTMLDivElement) => void)
      | undefined;
    expect(activate).toBeTypeOf('function');

    const panel = createPanel();
    activate?.('s1', panel);
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
    await vi.waitFor(() => {
      expect(showDevErrorDialog).toHaveBeenCalledTimes(1);
    });
    expect(showDevErrorDialog).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'AppServerControl failed to open',
        context: 'AppServerControl activation failed for session s1',
        error: expect.any(Error),
      }),
    );
  });

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

  it('activates Agent-primary sessions when they become the active session', async () => {
    const panel = createPanel();
    getActiveTab.mockReturnValue('agent');
    getTabPanel.mockReturnValue(panel);
    getAppServerControlHistoryWindow.mockResolvedValue(createSnapshot());

    const { initAgentView } = await import('./index');
    initAgentView();
    setActiveAppServerControlSession('s1');

    await vi.waitFor(() => {
      expect(getTabPanel).toHaveBeenCalledWith('s1', 'agent');
      expect(attachSessionAppServerControl).toHaveBeenCalledWith('s1');
    });
    expect(panel.classList.add).toHaveBeenCalledWith('agent-view-panel');
  });

  it('can mount and render a debug scenario without requiring a pre-activated AppServerControl tab', async () => {
    const panel = createPanel();
    getTabPanel.mockReturnValue(panel);

    const { showAppServerControlDebugScenario } = await import('./index');

    expect(showAppServerControlDebugScenario('s1', 'workflow')).toBe(true);
    expect(ensureSessionWrapper).toHaveBeenCalledWith('s1');
    expect(setSessionAppServerControlAvailability).toHaveBeenCalledWith('s1', true);
    expect(switchTab).toHaveBeenCalledWith('s1', 'agent');
    expect(panel.classList.add).toHaveBeenCalledWith('agent-view-panel');
  });

  it('renders Codex AppServerControl as a full-width left layout', async () => {
    getAppServerControlHistoryWindow.mockResolvedValue(
      createSnapshot({
        historyCount: 2,
        historyWindowEnd: 2,
        history: [
          {
            entryId: 'user:turn-1',
            order: 1,
            kind: 'user',
            turnId: 'turn-1',
            itemId: 'user-1',
            requestId: null,
            status: 'completed',
            itemType: 'user_message',
            title: null,
            body: 'Make AppServerControl use the full width.',
            attachments: [],
            streaming: false,
            createdAt: '2026-03-29T09:59:56Z',
            updatedAt: '2026-03-29T09:59:56Z',
          },
          {
            entryId: 'assistant:turn-1',
            order: 2,
            kind: 'assistant',
            turnId: 'turn-1',
            itemId: 'assistant-1',
            requestId: null,
            status: 'completed',
            itemType: 'assistant_message',
            title: null,
            body: 'AppServerControl now starts every row from the left.',
            attachments: [],
            streaming: false,
            createdAt: '2026-03-29T10:00:00Z',
            updatedAt: '2026-03-29T10:00:00Z',
          },
        ],
      }),
    );
    getAppServerControlEvents.mockResolvedValue({
      sessionId: 's1',
      latestSequence: 1,
      events: [],
    });

    const { initAgentView } = await import('./index');
    initAgentView();

    const activate = onTabActivated.mock.calls[0]?.[1] as
      | ((sessionId: string, panel: HTMLDivElement) => void)
      | undefined;
    expect(activate).toBeTypeOf('function');

    const panel = createPanel();
    activate?.('s1', panel);

    await vi.waitFor(() => {
      expect(panel.dataset.appServerControlProvider).toBe('codex');
      expect(panel.dataset.appServerControlLayout).toBe('full-width-left');
    });
  });

  it('uses User and Agent badge labels for Codex AppServerControl history rows', async () => {
    const { resolveHistoryBadgeLabel } = await import('./index');

    expect(resolveHistoryBadgeLabel('user', 'codex')).toBe('User');
    expect(resolveHistoryBadgeLabel('assistant', 'codex')).toBe('Agent');
    expect(resolveHistoryBadgeLabel('assistant', 'claude')).toBe('Assistant');
  });

  it('shows the Agent badge only on the first assistant row of a turn', async () => {
    const { createAgentHistoryRender } = await import('./historyRender');

    const state = {
      panel: createPanel(),
      snapshot: createSnapshot({
        provider: 'codex',
        historyCount: 4,
        historyWindowEnd: 4,
      }),
      debugScenarioActive: false,
      activationRunId: 0,
      historyViewport: null,
      historyEntries: [],
      historyWindowStart: 0,
      historyWindowCount: 4,
      historyWindowTargetCount: 4,
      disconnectStream: null,
      streamConnected: true,
      refreshInFlight: false,
      requestBusyIds: new Set(),
      requestDraftAnswersById: {},
      requestQuestionIndexById: {},
      historyScrollMode: 'follow',
      historyAutoScrollPinned: true,
      historyLastScrollMetrics: null,
      historyLastUserScrollIntentAt: 0,
      historyWindowRevision: null,
      historyWindowViewportWidth: null,
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
      pendingHistoryPrependAnchor: null,
      pendingHistoryLayoutAnchor: null,
      historyLastVirtualWindowKey: null,
      historyExpandedEntries: new Set(),
      runtimeStats: null,
      busyIndicatorTickHandle: null,
      completedTurnDurationEntries: new Map(),
    } as any;

    const viewport = createMockDomNode({
      clientHeight: 900,
      clientWidth: 1200,
      scrollHeight: 900,
      querySelector: vi.fn(),
      scrollTo: vi.fn(),
      getBoundingClientRect: vi.fn(() => ({ top: 0, bottom: 900 })),
    });
    state.historyViewport = viewport;
    state.panel.querySelector = vi.fn((selector: string) => {
      if (selector === '[data-agent-field="history"]') {
        return viewport;
      }

      return createMockDomNode();
    });

    const visibleBadges: boolean[] = [];
    const render = createAgentHistoryRender({
      getState: () => state,
      scheduleHistoryRender: vi.fn(),
      syncAgentViewPresentation: vi.fn(),
      createHistoryEntry: (entry, _sessionId, options) => {
        const node = createMockDomNode({
          getBoundingClientRect: vi.fn(() => ({ height: 40, top: 0, bottom: 40 })),
        });
        node.dataset = {};
        if (entry.kind === 'assistant') {
          visibleBadges.push(options?.showAssistantBadge === true);
        }
        return node;
      },
      createHistorySpacer: () => createMockDomNode(),
      createRequestActionBlock: () => createMockDomNode(),
      pruneAssistantMarkdownCache: vi.fn(),
      renderRuntimeStats: vi.fn(),
    });

    render.renderHistory(
      state.panel,
      [
        {
          id: 'user:turn-1',
          order: 1,
          kind: 'user',
          tone: 'info',
          label: 'User',
          title: '',
          body: 'Question',
          meta: '12:00:00',
          sourceTurnId: 'turn-1',
        },
        {
          id: 'assistant:turn-1:1',
          order: 2,
          kind: 'assistant',
          tone: 'info',
          label: 'Agent',
          title: '',
          body: 'First part',
          meta: '12:00:01',
          sourceTurnId: 'turn-1',
        },
        {
          id: 'assistant:turn-1:2',
          order: 3,
          kind: 'assistant',
          tone: 'info',
          label: 'Agent',
          title: '',
          body: 'Second part',
          meta: '12:00:02',
          sourceTurnId: 'turn-1',
        },
        {
          id: 'assistant:turn-2:1',
          order: 4,
          kind: 'assistant',
          tone: 'info',
          label: 'Agent',
          title: '',
          body: 'New turn answer',
          meta: '12:00:03',
          sourceTurnId: 'turn-2',
        },
      ] as any,
      's1',
    );

    expect(visibleBadges).toEqual([true, false, true]);
  });

  it('keeps the busy indicator DOM node and syncs it in place across live updates', async () => {
    const { createAgentHistoryRender } = await import('./historyRender');

    const state = {
      panel: createPanel(),
      snapshot: createSnapshot({
        provider: 'codex',
        historyCount: 1,
        historyWindowEnd: 1,
      }),
      debugScenarioActive: false,
      activationRunId: 0,
      historyViewport: null,
      historyEntries: [],
      historyWindowStart: 0,
      historyWindowCount: 1,
      historyWindowTargetCount: 1,
      disconnectStream: null,
      streamConnected: true,
      refreshInFlight: false,
      requestBusyIds: new Set(),
      requestDraftAnswersById: {},
      requestQuestionIndexById: {},
      historyScrollMode: 'follow',
      historyAutoScrollPinned: true,
      historyLastScrollMetrics: null,
      historyLastUserScrollIntentAt: 0,
      historyWindowRevision: null,
      historyWindowViewportWidth: null,
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
      pendingHistoryPrependAnchor: null,
      pendingHistoryLayoutAnchor: null,
      historyLastVirtualWindowKey: null,
      historyExpandedEntries: new Set(),
      runtimeStats: null,
      busyIndicatorTickHandle: null,
      completedTurnDurationEntries: new Map(),
    } as any;

    const viewport = createMockDomNode({
      clientHeight: 900,
      clientWidth: 1200,
      scrollHeight: 900,
      querySelector: vi.fn(),
      scrollTo: vi.fn(),
      getBoundingClientRect: vi.fn(() => ({ top: 0, bottom: 900 })),
    });
    state.historyViewport = viewport;
    state.panel.querySelector = vi.fn((selector: string) => {
      if (selector === '[data-agent-field="history"]') {
        return viewport;
      }

      return createMockDomNode();
    });

    const busyNode = createMockDomNode({
      dataset: {},
      getBoundingClientRect: vi.fn(() => ({ height: 32, top: 0, bottom: 32 })),
    });
    const createHistoryEntry = vi.fn(() => busyNode);
    const syncBusyIndicatorEntry = vi.fn();
    const render = createAgentHistoryRender({
      getState: () => state,
      scheduleHistoryRender: vi.fn(),
      syncAgentViewPresentation: vi.fn(),
      createHistoryEntry,
      syncBusyIndicatorEntry,
      createHistorySpacer: () => createMockDomNode(),
      createRequestActionBlock: () => createMockDomNode(),
      pruneAssistantMarkdownCache: vi.fn(),
      renderRuntimeStats: vi.fn(),
    });

    const firstEntry = {
      id: 'busy-indicator:turn-1',
      order: 1,
      kind: 'assistant',
      tone: 'info',
      label: 'Agent',
      title: '',
      body: 'Working',
      meta: '',
      busyIndicator: true,
      busyElapsedText: '5s',
    };
    const updatedEntry = {
      ...firstEntry,
      body: 'Applying diff',
      busyElapsedText: '6s',
    };

    render.renderHistory(state.panel, [firstEntry] as any, 's1');
    render.renderHistory(state.panel, [updatedEntry] as any, 's1');

    expect(createHistoryEntry).toHaveBeenCalledTimes(1);
    expect(syncBusyIndicatorEntry).toHaveBeenCalledTimes(1);
    expect(syncBusyIndicatorEntry).toHaveBeenCalledWith(busyNode, updatedEntry);
    expect(state.historyRenderedNodes.get(firstEntry.id)?.node).toBe(busyNode);
  });

  it('suppresses immediate viewport rewindowing when a browse anchor is restored programmatically', async () => {
    const { createAgentHistoryRender } = await import('./historyRender');

    const panel = createPanel();
    const viewport = createMockDomNode({
      clientHeight: 600,
      clientWidth: 900,
      scrollHeight: 1800,
      scrollTop: 320,
      querySelector: vi.fn(),
      scrollTo: vi.fn(function (this: any, options: { top?: number }) {
        if (typeof options?.top === 'number') {
          this.scrollTop = options.top;
        }
      }),
      getBoundingClientRect: vi.fn(() => ({ top: 0, bottom: 600 })),
    });
    const scrollButton = createMockDomNode();
    const composerShell = createMockDomNode();
    const composerInterruption = createMockDomNode();
    panel.querySelector = vi.fn((selector: string) => {
      switch (selector) {
        case '[data-agent-field="history"]':
          return viewport;
        case '[data-agent-field="scroll-to-bottom"]':
          return scrollButton;
        case '[data-agent-field="composer-shell"]':
          return composerShell;
        case '[data-agent-field="composer-interruption"]':
          return composerInterruption;
        default:
          return createMockDomNode();
      }
    });

    const state = {
      panel,
      snapshot: createSnapshot({
        provider: 'codex',
        historyCount: 3,
        historyWindowStart: 0,
        historyWindowEnd: 3,
      }),
      debugScenarioActive: false,
      activationRunId: 0,
      historyViewport: viewport,
      historyEntries: [],
      historyWindowStart: 0,
      historyWindowCount: 3,
      historyWindowTargetCount: 3,
      historyViewportSyncPending: false,
      historyViewportSyncForcePending: false,
      historyViewportSyncQueuedDuringRefresh: false,
      historyViewportSyncSuppressUntil: 0,
      disconnectStream: null,
      streamConnected: true,
      refreshInFlight: false,
      requestBusyIds: new Set(),
      requestDraftAnswersById: {},
      requestQuestionIndexById: {},
      historyScrollMode: 'browse',
      historyAutoScrollPinned: false,
      historyLastScrollMetrics: null,
      historyLastUserScrollIntentAt: 0,
      historyLastVoidSyncScrollTop: null,
      historyWindowRevision: null,
      historyWindowViewportWidth: null,
      historyNavigatorMode: 'browse',
      historyNavigatorAnchorIndex: null,
      historyNavigatorDragTargetIndex: null,
      historyNavigatorQueuedTargetIndex: null,
      historyNavigatorQueuedRequestKind: null,
      historyNavigatorPreviewHandle: null,
      historyNavigatorHydrateHandle: null,
      historyNavigatorLastPreviewRequestAt: 0,
      historyPendingJumpTargetIndex: null,
      historyPendingJumpAlign: null,
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
      pendingHistoryPrependAnchor: null,
      pendingHistoryLayoutAnchor: {
        entryId: 'assistant:2',
        topOffsetPx: 18,
        absoluteIndex: 1,
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
      createHistoryEntry: (entry) =>
        createMockDomNode({
          dataset: {},
          getBoundingClientRect: vi.fn(() =>
            entry.id === 'assistant:2'
              ? { top: 96, bottom: 156, height: 60 }
              : { top: 180, bottom: 240, height: 60 },
          ),
        }),
      createRequestActionBlock: () => createMockDomNode(),
      pruneAssistantMarkdownCache: vi.fn(),
      renderRuntimeStats: vi.fn(),
    });

    render.renderHistory(
      panel,
      [
        {
          id: 'user:1',
          order: 1,
          kind: 'user',
          tone: 'info',
          label: 'User',
          title: '',
          body: 'Question',
          meta: '12:00',
        },
        {
          id: 'assistant:2',
          order: 2,
          kind: 'assistant',
          tone: 'info',
          label: 'Agent',
          title: '',
          body: 'Answer',
          meta: '12:01',
        },
        {
          id: 'assistant:3',
          order: 3,
          kind: 'assistant',
          tone: 'info',
          label: 'Agent',
          title: '',
          body: 'More',
          meta: '12:02',
        },
      ] as any,
      's1',
    );

    expect(state.pendingHistoryLayoutAnchor).toBeNull();
    expect(state.historyViewportSyncSuppressUntil).toBeGreaterThan(Date.now() - 1);
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

  it('renders history metadata as timestamp-only text for every row kind', async () => {
    const { formatHistoryMeta, shouldHideStatusInMeta } = await import('./index');

    expect(shouldHideStatusInMeta('user', 'In Progress')).toBe(true);
    expect(shouldHideStatusInMeta('assistant', 'Completed')).toBe(true);
    expect(shouldHideStatusInMeta('tool', 'Running')).toBe(true);
    expect(shouldHideStatusInMeta('request', '1/3')).toBe(true);
    expect(formatHistoryMeta('assistant', 'In Progress', '2026-03-31T02:28:58Z')).toMatch(
      /\d{2}:\d{2}:\d{2}/,
    );
    expect(formatHistoryMeta('assistant', 'In Progress', '2026-03-31T02:28:58Z')).not.toContain(
      'In Progress',
    );
  });

  it('suppresses timestamp meta for command-execution and diff history rows', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      history: [
        {
          entryId: 'tool-1',
          order: 1,
          kind: 'tool',
          status: 'completed',
          itemType: 'command_execution',
          title: 'Tool completed',
          body: 'git status --short',
          attachments: [],
          streaming: false,
          createdAt: '2026-04-04T20:00:00Z',
          updatedAt: '2026-04-04T20:00:00Z',
        },
        {
          entryId: 'diff-1',
          order: 2,
          kind: 'diff',
          status: 'completed',
          itemType: 'diff',
          title: 'Working diff',
          body: 'diff --git a/a.txt b/a.txt\n@@\n-a\n+b',
          attachments: [],
          streaming: false,
          createdAt: '2026-04-04T20:00:01Z',
          updatedAt: '2026-04-04T20:00:01Z',
        },
      ],
    } as any;

    const history = buildAppServerControlHistoryEntries(snapshot);
    expect(history[0]?.meta).toBe('');
    expect(history[1]?.meta).toBe('');
  });

  it('keeps auto-follow pinned when content grows without user scrolling', async () => {
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
          scrollTop: 900,
          clientHeight: 600,
          scrollHeight: 1860,
        },
        userInitiated: false,
        pendingAnchorRestore: false,
      }),
    ).toBe('follow');
  });

  it('stops auto-follow immediately when the user scrolls away from the live edge', async () => {
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
          scrollTop: 760,
          clientHeight: 600,
          scrollHeight: 1860,
        },
        userInitiated: true,
        pendingAnchorRestore: false,
      }),
    ).toBe('browse');
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

  it('stops auto-follow when the viewport moves upward even if the browser missed the explicit scroll-intent marker', async () => {
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
          scrollTop: 760,
          clientHeight: 600,
          scrollHeight: 1860,
        },
        userInitiated: false,
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

  it('does not repin auto-follow just because layout changed while already detached from the live edge', async () => {
    const { resolveHistoryScrollMode } = await import('./index');

    expect(
      resolveHistoryScrollMode({
        previousMode: 'browse',
        previous: {
          scrollTop: 240,
          clientHeight: 600,
          scrollHeight: 2400,
        },
        current: {
          scrollTop: 240,
          clientHeight: 600,
          scrollHeight: 2760,
        },
        userInitiated: false,
        pendingAnchorRestore: false,
      }),
    ).toBe('browse');
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

  it('keeps a dedicated restore-anchor mode while a prepend anchor is pending', async () => {
    const { resolveHistoryScrollMode } = await import('./index');

    expect(
      resolveHistoryScrollMode({
        previousMode: 'browse',
        previous: {
          scrollTop: 240,
          clientHeight: 600,
          scrollHeight: 2400,
        },
        current: {
          scrollTop: 240,
          clientHeight: 600,
          scrollHeight: 2760,
        },
        userInitiated: false,
        pendingAnchorRestore: true,
      }),
    ).toBe('restore-anchor');
  });

  it('keeps debug scenarios isolated from the live AppServerControl attach path', async () => {
    const panel = createPanel();
    getTabPanel.mockReturnValue(panel);

    const { initAgentView, showAppServerControlDebugScenario } = await import('./index');
    initAgentView();

    expect(showAppServerControlDebugScenario('s1', 'workflow')).toBe(true);

    const activate = onTabActivated.mock.calls[0]?.[1] as
      | ((sessionId: string, panel: HTMLDivElement) => void)
      | undefined;

    await activate?.('s1', panel);

    expect(attachSessionAppServerControl).not.toHaveBeenCalled();
    expect(getAppServerControlEvents).not.toHaveBeenCalled();
    expect(getAppServerControlHistoryWindow).not.toHaveBeenCalled();
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
      | ((sessionId: string, panel: HTMLDivElement) => void)
      | undefined;
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
      | ((sessionId: string, panel: HTMLDivElement) => void)
      | undefined;
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
      | ((sessionId: string, panel: HTMLDivElement) => void)
      | undefined;
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

  it('does not show a dev error modal for expected AppServerControl handoff blocks', async () => {
    attachSessionAppServerControl.mockRejectedValue(
      new Error(
        'HTTP 400: Finish or interrupt the terminal Codex turn before opening AppServerControl.',
      ),
    );
    getAppServerControlHistoryWindow.mockRejectedValue(
      new Error('AppServerControl snapshot unavailable'),
    );
    getAppServerControlEvents.mockRejectedValue(new Error('AppServerControl events unavailable'));
    getSessionState.mockResolvedValue({
      session: {
        id: 's1',
        shellType: 'Pwsh',
        supervisor: { profile: 'codex' },
        foregroundDisplayName: 'codex --yolo',
      },
      previews: [],
      bufferByteLength: 20,
      bufferEncoding: 'utf-8',
      bufferText: 'PS> codex --yolo',
      bufferBase64: null,
    });
    getSessionBufferTail.mockResolvedValue('PS> codex --yolo');

    const { initAgentView } = await import('./index');
    initAgentView();

    const activate = onTabActivated.mock.calls[0]?.[1] as
      | ((sessionId: string, panel: HTMLDivElement) => void)
      | undefined;
    expect(activate).toBeTypeOf('function');

    const panel = createPanel();
    activate?.('s1', panel);
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();

    expect(showDevErrorDialog).not.toHaveBeenCalled();
  });

  it('renders interview requests inline in history instead of the composer interruption UI', async () => {
    attachSessionAppServerControl.mockResolvedValue(undefined);
    getAppServerControlHistoryWindow.mockResolvedValue({
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-23T11:00:00Z',
      latestSequence: 1,
      session: {
        state: 'running',
        stateLabel: 'Running',
        reason: 'Waiting for user input.',
        lastError: null,
        lastEventAt: '2026-03-23T11:00:00Z',
      },
      thread: {
        threadId: 'thread-1',
        state: 'active',
        stateLabel: 'Active',
      },
      currentTurn: {
        turnId: 'turn-1',
        state: 'paused',
        stateLabel: 'Paused',
        model: null,
        effort: null,
        startedAt: '2026-03-23T10:59:45Z',
        completedAt: null,
      },
      streams: {
        assistantText: 'Choose a mode before I continue.',
        reasoningText: '',
        reasoningSummaryText: '',
        planText: '',
        commandOutput: '',
        fileChangeOutput: '',
        unifiedDiff: '',
      },
      items: [],
      requests: [
        {
          requestId: 'req-1',
          turnId: 'turn-1',
          kind: 'interview',
          kindLabel: 'Question',
          state: 'open',
          detail: 'Please choose a mode.',
          decision: null,
          questions: [
            {
              id: 'mode',
              header: 'Mode',
              question: 'Choose SAFE or FAST before I continue.',
              options: [
                { label: 'SAFE', description: 'Proceed carefully.' },
                { label: 'FAST', description: 'Move quickly.' },
              ],
            },
          ],
          answers: [],
          updatedAt: '2026-03-23T11:00:00Z',
        },
      ],
      notices: [],
    });
    getAppServerControlEvents.mockResolvedValue({
      sessionId: 's1',
      latestSequence: 1,
      events: [],
    });

    const { initAgentView } = await import('./index');
    initAgentView();

    const activate = onTabActivated.mock.calls[0]?.[1] as
      | ((sessionId: string, panel: HTMLDivElement) => void)
      | undefined;
    expect(activate).toBeTypeOf('function');

    const panel = createPanel();
    activate?.('s1', panel);

    await vi.waitFor(() => {
      expect(getAppServerControlHistoryWindow).toHaveBeenCalledWith(
        's1',
        undefined,
        expect.any(Number),
        expect.any(String),
      );
      expect(getAppServerControlEvents.mock.calls.length).toBeLessThanOrEqual(1);
    });

    const interruptionHost = panel.querySelector(
      '[data-agent-field="composer-interruption"]',
    ) as any;
    expect(interruptionHost).toBeTruthy();
    expect(interruptionHost.hidden).toBe(true);
    expect(showDevErrorDialog).not.toHaveBeenCalled();
  });

  it('keeps AppServerControl attached when the agent tab is deactivated', async () => {
    const { initAgentView } = await import('./index');
    initAgentView();

    const deactivate = onTabDeactivated.mock.calls[0]?.[1] as
      | ((sessionId: string) => void)
      | undefined;
    expect(deactivate).toBeTypeOf('function');

    deactivate?.('s1');
    await Promise.resolve();

    expect(detachSessionAppServerControl).not.toHaveBeenCalled();
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
      | ((sessionId: string, panel: HTMLDivElement) => void)
      | undefined;
    const deactivate = onTabDeactivated.mock.calls[0]?.[1] as
      | ((sessionId: string) => void)
      | undefined;
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

  it('releases hidden history DOM and collapses background history back to a latest window', async () => {
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
      | ((sessionId: string, panel: HTMLDivElement) => void)
      | undefined;
    const deactivate = onTabDeactivated.mock.calls[0]?.[1] as
      | ((sessionId: string) => void)
      | undefined;
    expect(activate).toBeTypeOf('function');
    expect(deactivate).toBeTypeOf('function');

    const panel = createPanel();
    activate?.('s1', panel);

    await vi.waitFor(() => {
      expect(openAppServerControlHistoryStream).toHaveBeenCalledTimes(1);
    });

    const historyHost = panel.querySelector('[data-agent-field="history"]') as any;
    historyHost.replaceChildren.mockClear();

    deactivate?.('s1');

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

  it('re-entering a AppServerControl tab resets follow mode and refreshes the latest history window', async () => {
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
      )
      .mockResolvedValueOnce(
        createSnapshot({
          latestSequence: 45,
          historyCount: 405,
          historyWindowStart: 325,
          historyWindowEnd: 405,
          hasOlderHistory: true,
          hasNewerHistory: false,
          history: Array.from({ length: 80 }, (_value, index) => ({
            entryId: `assistant:${index + 326}`,
            turnId: 'turn-1',
            itemId: `assistant-${index + 326}`,
            requestId: null,
            order: index + 326,
            kind: 'assistant',
            status: 'completed',
            title: 'Assistant',
            body: `Latest row ${index + 326}`,
            updatedAt: '2026-03-28T11:00:05Z',
            streaming: false,
            attachments: [],
            createdAt: '2026-03-28T11:00:05Z',
          })),
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
      | ((sessionId: string, panel: HTMLDivElement) => void)
      | undefined;
    expect(activate).toBeTypeOf('function');

    const panel = createPanel();
    const historyHost = panel.querySelector('[data-agent-field="history"]') as any;
    historyHost.scrollTop = 1337;

    activate?.('s1', panel);

    await vi.waitFor(() => {
      expect(getAppServerControlHistoryWindow).toHaveBeenNthCalledWith(
        1,
        's1',
        undefined,
        expect.any(Number),
        expect.any(String),
      );
      expect(getAppServerControlHistoryWindow).toHaveBeenNthCalledWith(
        2,
        's1',
        undefined,
        expect.any(Number),
        expect.any(String),
      );
    });

    expect(historyHost.scrollTop).toBe(0);
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
      | ((sessionId: string, panel: HTMLDivElement) => void)
      | undefined;
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

  it('returning from browser background snaps the active AppServerControl session back to the live edge', async () => {
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
          historyWindowStart: 325,
          historyWindowEnd: 405,
          hasOlderHistory: true,
          hasNewerHistory: false,
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
      | ((sessionId: string, panel: HTMLDivElement) => void)
      | undefined;
    expect(activate).toBeTypeOf('function');

    const panel = createPanel();
    const historyHost = panel.querySelector('[data-agent-field="history"]') as any;
    historyHost.scrollTop = 1337;

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

    historyHost.scrollTop = 987;
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
        undefined,
        240,
        expect.any(String),
      );
    });

    expect(historyHost.scrollTop).toBe(0);
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
      | ((sessionId: string, panel: HTMLDivElement) => void)
      | undefined;
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
      | ((sessionId: string, panel: HTMLDivElement) => void)
      | undefined;
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

  it('does not request a viewport-centered refetch when a short retained window already covers full history', async () => {
    const { resolveViewportCenteredWindowRequest } = await import('../../utils/virtualizer');

    const request = resolveViewportCenteredWindowRequest({
      items: Array.from({ length: 29 }, (_value, index) => ({
        id: `row-${index + 1}`,
        heightPx: 96,
      })),
      viewportMetrics: {
        scrollTop: 3508,
        clientHeight: 629,
        clientWidth: 920,
      },
      retainedWindow: {
        windowStart: 0,
        windowEnd: 29,
        totalCount: 29,
      },
      fetchAheadItems: 20,
      resolveItemSize: (item) => item.heightPx,
      observedSizes: [],
    });

    expect(request).toBeNull();
  });

  it('preserves the snapshot retained window when runtime history entries are filtered from rendering', async () => {
    const { resolveHistoryRetainedWindowDescriptor } = await import('./historyRender');

    const descriptor = resolveHistoryRetainedWindowDescriptor(
      [
        {
          id: 'system:runtime-ready',
          order: 1,
          kind: 'system',
          tone: 'info',
          label: 'MidTerm',
          title: '',
          body: 'Codex App Server Controller runtime ready.',
          meta: 'Connecting',
        },
      ],
      {
        snapshot: {
          historyWindowStart: 0,
          historyWindowEnd: 5,
          historyCount: 5,
        },
      } as any,
    );

    expect(descriptor).toEqual({
      windowStart: 0,
      windowEnd: 5,
      totalCount: 5,
    });
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
      | ((sessionId: string, panel: HTMLDivElement) => void)
      | undefined;
    expect(activate).toBeTypeOf('function');

    const panel = createPanel();
    activate?.('s1', panel);

    await vi.waitFor(() => {
      expect(openAppServerControlHistoryStream).toHaveBeenCalledTimes(1);
    });

    const historyHost = panel.querySelector('[data-agent-field="history"]') as any;
    historyHost.replaceChildren.mockClear();

    setActiveAppServerControlSession('s2');

    const streamCallbacks = openAppServerControlHistoryStream.mock.calls[0]?.[5] as
      | { onPatch(delta: unknown): void }
      | undefined;
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

  it('detaches AppServerControl when the agent view is destroyed', async () => {
    const { destroyAgentView } = await import('./index');

    destroyAgentView('s1');
    await Promise.resolve();

    expect(detachSessionAppServerControl).toHaveBeenCalledWith('s1');
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
      | ((sessionId: string, panel: HTMLDivElement) => void)
      | undefined;
    expect(activate).toBeTypeOf('function');

    activate?.('s1', createPanel());

    await vi.waitFor(() => {
      expect(openAppServerControlHistoryStream).toHaveBeenCalledTimes(1);
    });

    const streamCallbacks = openAppServerControlHistoryStream.mock.calls[0]?.[5] as
      | { onPatch(delta: unknown): void }
      | undefined;
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

  it('routes off-window live history changes through browse viewport sync', async () => {
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
      | ((sessionId: string, panel: HTMLDivElement) => void)
      | undefined;
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
      | { onPatch(delta: unknown): void }
      | undefined;
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

    await vi.waitFor(() => {
      expect(getAppServerControlHistoryWindow).toHaveBeenCalled();
    });
    expect(getAppServerControlHistoryWindow.mock.calls[0]?.[1]).not.toBe(40);
  });

  it('classifies a busy terminal attach failure into a readonly handoff issue', async () => {
    const { classifyAppServerControlActivationIssue } = await import('./index');

    const issue = classifyAppServerControlActivationIssue(
      new Error(
        'HTTP 400: Finish or interrupt the terminal Codex turn before opening AppServerControl.',
      ),
      true,
    );

    expect(issue.kind).toBe('busy-terminal-turn');
    expect(issue.meta).toBe('Read-only history');
    expect(issue.title).toContain('Terminal owns');
    expect(issue.actions.map((action) => action.id)).toEqual(['retry-appServerControl']);
  });

  it('classifies shell recovery failure as an expected handoff issue', async () => {
    const { classifyAppServerControlActivationIssue } = await import('./index');

    const issue = classifyAppServerControlActivationIssue(
      new Error('HTTP 400: Terminal shell did not recover after stopping Codex.'),
      false,
    );

    expect(issue.kind).toBe('shell-recovery-failed');
    expect(issue.meta).toBe('Terminal recovery failed');
    expect(issue.actions.map((action) => action.id)).toEqual(['retry-appServerControl']);
  });

  it('classifies native runtime unavailability as an expected AppServerControl issue', async () => {
    const { classifyAppServerControlActivationIssue } = await import('./index');

    const issue = classifyAppServerControlActivationIssue(
      new Error('HTTP 400: AppServerControl native runtime is not available for this session.'),
      false,
    );

    expect(issue.kind).toBe('native-runtime-unavailable');
    expect(issue.meta).toBe('Native runtime unavailable');
    expect(issue.actions.map((action) => action.id)).toEqual(['retry-appServerControl']);
  });

  it('prepends a readable AppServerControl issue row ahead of the history', async () => {
    const { withActivationIssueNotice } = await import('./index');

    const entries = withActivationIssueNotice(
      [
        {
          id: 'assistant:1',
          order: 1,
          kind: 'assistant',
          tone: 'info',
          label: 'Assistant',
          title: '',
          body: 'History still visible.',
          meta: '02:00',
        },
      ],
      {
        kind: 'missing-resume-id',
        tone: 'warning',
        meta: 'Read-only history',
        title: 'No resumable Codex thread is known yet',
        body: 'AppServerControl can still show canonical history.',
        actions: [
          {
            id: 'retry-appServerControl',
            label: 'Retry AppServerControl',
            style: 'primary',
            busyLabel: 'Retrying...',
          },
        ],
      },
    );

    expect(entries).toHaveLength(2);
    expect(entries[0]?.kind).toBe('system');
    expect(entries[0]?.title).toBe('No resumable Codex thread is known yet');
    expect(entries[0]?.actions?.map((action) => action.id)).toEqual(['retry-appServerControl']);
    expect(entries[1]?.body).toBe('History still visible.');
  });

  it('compacts duplicate activation failure rows when an expected handoff issue is active', async () => {
    const { buildActivationHistoryEntries } = await import('./index');

    const entries = buildActivationHistoryEntries({
      panel: createPanel(),
      snapshot: null,
      events: [],
      historyViewport: null,
      historyEntries: [],
      disconnectStream: null,
      streamConnected: false,
      refreshScheduled: null,
      refreshInFlight: false,
      requestBusyIds: new Set<string>(),
      historyAutoScrollPinned: true,
      historyRenderScheduled: null,
      activationState: 'failed',
      activationDetail: 'AppServerControl startup failed.',
      activationError:
        'HTTP 400: Finish or interrupt the terminal Codex turn before opening AppServerControl.',
      activationActionBusy: false,
      activationIssue: {
        kind: 'busy-terminal-turn',
        tone: 'warning',
        meta: 'Terminal busy',
        title: 'Terminal owns the live Codex turn',
        body: 'Use Terminal, then retry.',
        actions: [],
      },
      activationTrace: [
        {
          tone: 'info',
          meta: 'Opening • 03:16',
          summary: 'AppServerControl pane opened.',
          detail: 'MidTerm is opening the AppServerControl conversation surface for this session.',
        },
        {
          tone: 'info',
          meta: 'Attaching • 03:16',
          summary: 'Attaching AppServerControl runtime.',
          detail:
            'Starting or reconnecting the backend-owned AppServerControl runtime for this session.',
        },
        {
          tone: 'attention',
          meta: 'Failed • 03:16',
          summary: 'AppServerControl startup failed.',
          detail:
            'HTTP 400: Finish or interrupt the terminal Codex turn before opening AppServerControl.',
        },
      ],
    });

    expect(entries).toHaveLength(2);
    expect(entries.map((entry) => entry.meta)).toEqual(['Opening • 03:16', 'Attaching • 03:16']);
  });

  it('prepends a read-only terminal snapshot when AppServerControl has no canonical history yet', async () => {
    const { buildActivationHistoryEntries } = await import('./index');

    const entries = buildActivationHistoryEntries({
      panel: createPanel(),
      snapshot: null,
      events: [],
      historyViewport: null,
      historyEntries: [],
      disconnectStream: null,
      streamConnected: false,
      refreshScheduled: null,
      refreshInFlight: false,
      requestBusyIds: new Set<string>(),
      historyAutoScrollPinned: true,
      historyRenderScheduled: null,
      activationState: 'failed',
      activationDetail: 'AppServerControl startup failed.',
      activationError:
        'HTTP 400: MidTerm could not determine the Codex resume id for this session.',
      activationActionBusy: false,
      activationIssue: {
        kind: 'missing-resume-id',
        tone: 'warning',
        meta: 'Live attach unavailable',
        title: 'No resumable Codex thread is known yet',
        body: 'Use Terminal for the live lane, or retry later.',
        actions: [],
      },
      activationTrace: [
        {
          tone: 'info',
          meta: 'Opening • 03:16',
          summary: 'AppServerControl pane opened.',
          detail: 'MidTerm is opening the AppServerControl conversation surface for this session.',
        },
      ],
    });

    expect(entries).toHaveLength(1);
    expect(entries[0]?.kind).toBe('system');
    expect(entries[0]?.meta).toBe('Opening • 03:16');
  });

  it('adds optimistic user and assistant rows until canonical AppServerControl entries arrive', async () => {
    const { applyOptimisticAppServerControlTurns } = await import('./index');

    const result = applyOptimisticAppServerControlTurns(
      {
        sessionId: 's1',
        provider: 'codex',
        generatedAt: '2026-03-22T09:00:00Z',
        latestSequence: 10,
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
          turnId: '',
          state: 'running',
          stateLabel: 'Running',
          model: null,
          effort: null,
          startedAt: '2026-03-22T09:00:00Z',
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
        items: [],
        requests: [],
        notices: [],
      },
      [],
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
    expect(result.entries[0]?.kind).toBe('user');
    expect(result.entries[0]?.label).toBe('You');
    expect(result.entries[1]?.kind).toBe('assistant');
    expect(result.entries[1]?.live).toBe(true);
    expect(result.entries[1]?.pending).toBe(false);
    expect(result.optimisticTurns).toHaveLength(1);
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

  it.skip('builds history-first rows from canonical AppServerControl events', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-20T10:00:00Z',
      latestSequence: 8,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-20T10:00:00Z',
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
        model: 'gpt-5',
        effort: 'medium',
        startedAt: '2026-03-20T09:59:00Z',
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
      items: [],
      requests: [
        {
          requestId: 'req-1',
          turnId: 'turn-1',
          kind: 'tool_approval',
          kindLabel: 'Approval',
          state: 'open',
          detail: 'Approve command execution',
          decision: null,
          questions: [],
          answers: [],
          updatedAt: '2026-03-20T10:00:00Z',
        },
      ],
      notices: [],
    } as any;

    const events = [
      {
        sequence: 1,
        eventId: 'e1',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'user-1',
        requestId: null,
        createdAt: '2026-03-20T09:59:00Z',
        type: 'item.completed',
        raw: null,
        sessionState: null,
        threadState: null,
        turnStarted: null,
        turnCompleted: null,
        contentDelta: null,
        planDelta: null,
        planCompleted: null,
        diffUpdated: null,
        item: {
          itemType: 'user_message',
          status: 'completed',
          title: 'You',
          detail: 'Implement the history UI.',
        },
        requestOpened: null,
        requestResolved: null,
        userInputRequested: null,
        userInputResolved: null,
        runtimeMessage: null,
      },
      {
        sequence: 2,
        eventId: 'e2',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'assistant-1',
        requestId: null,
        createdAt: '2026-03-20T09:59:10Z',
        type: 'content.delta',
        raw: null,
        sessionState: null,
        threadState: null,
        turnStarted: null,
        turnCompleted: null,
        contentDelta: {
          streamKind: 'assistant_text',
          delta: 'Working on it.',
        },
        planDelta: null,
        planCompleted: null,
        diffUpdated: null,
        item: null,
        requestOpened: null,
        requestResolved: null,
        userInputRequested: null,
        userInputResolved: null,
        runtimeMessage: null,
      },
      {
        sequence: 3,
        eventId: 'e3',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'tool-1',
        requestId: null,
        createdAt: '2026-03-20T09:59:20Z',
        type: 'item.started',
        raw: null,
        sessionState: null,
        threadState: null,
        turnStarted: null,
        turnCompleted: null,
        contentDelta: null,
        planDelta: null,
        planCompleted: null,
        diffUpdated: null,
        item: {
          itemType: 'command',
          status: 'in_progress',
          title: 'Run tests',
          detail: 'npm run typecheck',
        },
        requestOpened: null,
        requestResolved: null,
        userInputRequested: null,
        userInputResolved: null,
        runtimeMessage: null,
      },
      {
        sequence: 4,
        eventId: 'e4',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: null,
        requestId: 'req-1',
        createdAt: '2026-03-20T09:59:30Z',
        type: 'request.opened',
        raw: null,
        sessionState: null,
        threadState: null,
        turnStarted: null,
        turnCompleted: null,
        contentDelta: null,
        planDelta: null,
        planCompleted: null,
        diffUpdated: null,
        item: null,
        requestOpened: {
          requestType: 'tool_approval',
          requestTypeLabel: 'Approval',
          detail: 'Approve command execution',
        },
        requestResolved: null,
        userInputRequested: null,
        userInputResolved: null,
        runtimeMessage: null,
      },
    ] as any;

    const history = buildAppServerControlHistoryEntries(snapshot, events);

    expect(history.map((entry) => entry.kind)).toContain('user');
    expect(history.map((entry) => entry.kind)).toContain('assistant');
    expect(history.map((entry) => entry.kind)).toContain('tool');
    expect(history.map((entry) => entry.kind)).toContain('request');
    expect(history.find((entry) => entry.kind === 'assistant')?.body).toContain('Working on it.');
    expect(history.find((entry) => entry.kind === 'request')?.requestId).toBe('req-1');
  });

  it.skip('backs current-turn history rows from snapshot items when event history is incomplete', async () => {
    const { buildAppServerControlHistoryEntries, withLiveAssistantState } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-22T02:15:00Z',
      latestSequence: 900,
      session: {
        state: 'running',
        stateLabel: 'Running',
        reason: 'Codex turn started.',
        lastError: null,
        lastEventAt: '2026-03-22T02:15:00Z',
      },
      thread: {
        threadId: 'thread-1',
        state: 'active',
        stateLabel: 'Active',
      },
      currentTurn: {
        turnId: 'turn-2',
        state: 'running',
        stateLabel: 'Running',
        model: null,
        effort: null,
        startedAt: '2026-03-22T02:14:58Z',
        completedAt: null,
      },
      streams: {
        assistantText: 'Streaming answer in progress.',
        reasoningText: '',
        reasoningSummaryText: '',
        planText: '',
        commandOutput: '',
        fileChangeOutput: '',
        unifiedDiff: '',
      },
      items: [
        {
          itemId: 'local-user:turn-2',
          turnId: 'turn-2',
          itemType: 'user_message',
          status: 'completed',
          title: 'User message',
          detail: 'Describe the logo in detail.',
          attachments: [],
          updatedAt: '2026-03-22T02:14:58Z',
        },
        {
          itemId: 'assistant-old',
          turnId: 'turn-1',
          itemType: 'assistant_message',
          status: 'completed',
          title: 'Assistant message',
          detail: 'Earlier completed answer.',
          attachments: [],
          updatedAt: '2026-03-22T02:13:00Z',
        },
      ],
      requests: [],
      notices: [],
    } as any;

    const history = buildAppServerControlHistoryEntries(snapshot);
    const marked = withLiveAssistantState(snapshot, history);

    expect(
      marked.some((entry) => entry.kind === 'user' && entry.body.includes('Describe the logo')),
    ).toBe(true);
    expect(
      marked.some(
        (entry) =>
          entry.kind === 'assistant' && entry.body.includes('Streaming answer in progress.'),
      ),
    ).toBe(true);
    expect(marked.some((entry) => entry.kind === 'assistant' && entry.live)).toBe(true);
  });

  it('prefers canonical backend history rows over rebuilding from raw events', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-27T13:00:00Z',
      latestSequence: 22,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-27T13:00:00Z',
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
        startedAt: '2026-03-27T12:59:00Z',
        completedAt: '2026-03-27T13:00:00Z',
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
          entryId: 'user:turn-1',
          order: 1,
          kind: 'user',
          turnId: 'turn-1',
          itemId: 'local-user:turn-1',
          requestId: null,
          status: 'completed',
          itemType: 'user_message',
          title: null,
          body: 'first question',
          attachments: [],
          streaming: false,
          createdAt: '2026-03-27T12:58:00Z',
          updatedAt: '2026-03-27T12:58:00Z',
        },
        {
          entryId: 'assistant:turn-1',
          order: 2,
          kind: 'assistant',
          turnId: 'turn-1',
          itemId: 'assistant-1',
          requestId: null,
          status: 'completed',
          itemType: 'assistant_message',
          title: null,
          body: 'first answer',
          attachments: [],
          streaming: false,
          createdAt: '2026-03-27T12:58:01Z',
          updatedAt: '2026-03-27T12:58:02Z',
        },
        {
          entryId: 'assistant:turn-2',
          order: 4,
          kind: 'assistant',
          turnId: 'turn-2',
          itemId: 'assistant-2',
          requestId: null,
          status: 'streaming',
          itemType: 'assistant_message',
          title: null,
          body: 'second answer in progress',
          attachments: [],
          streaming: true,
          createdAt: '2026-03-27T12:59:30Z',
          updatedAt: '2026-03-27T12:59:31Z',
        },
      ],
      items: [],
      requests: [],
      notices: [],
    } as any;

    const events = [
      {
        sequence: 1,
        eventId: 'contradictory-event',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-99',
        itemId: 'assistant-99',
        requestId: null,
        createdAt: '2026-03-27T12:59:59Z',
        type: 'content.delta',
        contentDelta: {
          streamKind: 'assistant_text',
          delta: 'wrong answer',
        },
      },
    ] as any;

    const history = buildAppServerControlHistoryEntries(snapshot, events);

    expect(history.map((entry) => entry.id)).toEqual([
      'user:turn-1',
      'assistant:turn-1',
      'assistant:turn-2',
    ]);
    expect(history[2]?.body).toBe('second answer in progress');
    expect(history[2]?.live).toBe(true);
  });

  it('maps canonical history metadata, requests, and attachments into render rows', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-27T13:05:00Z',
      latestSequence: 9,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-27T13:05:00Z',
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
        model: 'gpt-5',
        effort: 'medium',
        startedAt: '2026-03-27T13:04:00Z',
        completedAt: '2026-03-27T13:05:00Z',
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
          entryId: 'user:turn-1',
          order: 1,
          kind: 'user',
          turnId: 'turn-1',
          itemId: 'local-user:turn-1',
          requestId: null,
          status: 'completed',
          itemType: 'user_message',
          title: null,
          body: '',
          attachments: [
            {
              kind: 'image',
              path: 'Q:/repo/.midterm/uploads/example.png',
              mimeType: 'image/png',
              displayName: 'example.png',
            },
          ],
          streaming: false,
          createdAt: '2026-03-27T13:04:01Z',
          updatedAt: '2026-03-27T13:04:01Z',
        },
        {
          entryId: 'request:req-1',
          order: 2,
          kind: 'request',
          turnId: 'turn-1',
          itemId: null,
          requestId: 'req-1',
          status: 'open',
          itemType: 'interview',
          title: 'User input',
          body: 'Choose SAFE or FAST.',
          attachments: [],
          streaming: false,
          createdAt: '2026-03-27T13:04:02Z',
          updatedAt: '2026-03-27T13:04:03Z',
        },
      ],
      items: [],
      requests: [],
      notices: [],
    } as any;

    const history = buildAppServerControlHistoryEntries(snapshot);

    expect(history).toHaveLength(2);
    expect(history[0]).toMatchObject({
      kind: 'user',
    });
    expect(history[0]?.attachments).toHaveLength(1);
    expect(history[1]).toMatchObject({
      kind: 'request',
      requestId: 'req-1',
      body: 'Choose SAFE or FAST.',
    });
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

  it.skip('hides normal state-management events and merges tool updates', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-20T10:00:00Z',
      latestSequence: 6,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-20T10:00:00Z',
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
        model: 'gpt-5',
        effort: 'medium',
        startedAt: '2026-03-20T09:59:00Z',
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
      items: [],
      requests: [],
      notices: [],
    } as any;

    const events = [
      {
        sequence: 1,
        eventId: 'e-state',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: null,
        itemId: null,
        requestId: null,
        createdAt: '2026-03-20T09:59:00Z',
        type: 'session.state.changed',
        raw: null,
        sessionState: {
          state: 'ready',
          stateLabel: 'Ready',
          reason: null,
        },
        threadState: null,
        turnStarted: null,
        turnCompleted: null,
        contentDelta: null,
        planDelta: null,
        planCompleted: null,
        diffUpdated: null,
        item: null,
        requestOpened: null,
        requestResolved: null,
        userInputRequested: null,
        userInputResolved: null,
        runtimeMessage: null,
      },
      {
        sequence: 2,
        eventId: 'e-tool-start',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'tool-1',
        requestId: null,
        createdAt: '2026-03-20T09:59:10Z',
        type: 'item.started',
        raw: null,
        sessionState: null,
        threadState: null,
        turnStarted: null,
        turnCompleted: null,
        contentDelta: null,
        planDelta: null,
        planCompleted: null,
        diffUpdated: null,
        item: {
          itemType: 'command',
          status: 'in_progress',
          title: 'Run tests completed',
          detail: 'npm run typecheck',
        },
        requestOpened: null,
        requestResolved: null,
        userInputRequested: null,
        userInputResolved: null,
        runtimeMessage: null,
      },
      {
        sequence: 3,
        eventId: 'e-tool-out',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'tool-1',
        requestId: null,
        createdAt: '2026-03-20T09:59:11Z',
        type: 'content.delta',
        raw: null,
        sessionState: null,
        threadState: null,
        turnStarted: null,
        turnCompleted: null,
        contentDelta: {
          streamKind: 'command_output',
          delta: 'All green',
        },
        planDelta: null,
        planCompleted: null,
        diffUpdated: null,
        item: null,
        requestOpened: null,
        requestResolved: null,
        userInputRequested: null,
        userInputResolved: null,
        runtimeMessage: null,
      },
      {
        sequence: 4,
        eventId: 'e-reasoning',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'assistant-1',
        requestId: null,
        createdAt: '2026-03-20T09:59:12Z',
        type: 'content.delta',
        raw: null,
        sessionState: null,
        threadState: null,
        turnStarted: null,
        turnCompleted: null,
        contentDelta: {
          streamKind: 'reasoning_text',
          delta: 'Thinking...',
        },
        planDelta: null,
        planCompleted: null,
        diffUpdated: null,
        item: null,
        requestOpened: null,
        requestResolved: null,
        userInputRequested: null,
        userInputResolved: null,
        runtimeMessage: null,
      },
    ] as any;

    const history = buildAppServerControlHistoryEntries(snapshot, events);

    expect(history).toHaveLength(3);
    expect(history[0]).toMatchObject({
      kind: 'tool',
      title: 'Run tests',
    });
    expect(history[0]?.body).toContain('npm run typecheck');
    expect(history[1]).toMatchObject({
      kind: 'tool',
      title: 'Command output',
    });
    expect(history[1]?.body).toContain('All green');
    expect(history[2]).toMatchObject({
      kind: 'reasoning',
      title: 'Reasoning',
    });
    expect(history[2]?.body).toContain('Thinking...');
  });

  it.skip('surfaces generic tool result streams instead of dropping them', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-23T10:00:00Z',
      latestSequence: 2,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-23T10:00:00Z',
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
        startedAt: '2026-03-23T09:59:58Z',
        completedAt: '2026-03-23T10:00:00Z',
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
      items: [],
      requests: [],
      notices: [],
    } as any;

    const events = [
      {
        sequence: 1,
        eventId: 'e-tool-result',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'tool-1',
        requestId: null,
        createdAt: '2026-03-23T09:59:59Z',
        type: 'content.delta',
        raw: null,
        contentDelta: {
          streamKind: 'tool_result',
          delta: 'exit_code: 0',
        },
      },
    ] as any;

    const history = buildAppServerControlHistoryEntries(snapshot, events);

    expect(history).toHaveLength(1);
    expect(history[0]).toMatchObject({
      kind: 'tool',
      title: 'Tool Result',
    });
    expect(history[0]?.body).toContain('exit_code: 0');
  });

  it.skip('keeps distinct tool and reasoning stream kinds in separate history rows', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-23T10:00:00Z',
      latestSequence: 4,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-23T10:00:00Z',
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
        startedAt: '2026-03-23T09:59:58Z',
        completedAt: '2026-03-23T10:00:00Z',
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
      items: [],
      requests: [],
      notices: [],
    } as any;

    const events = [
      {
        sequence: 1,
        eventId: 'e-command-output',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: null,
        requestId: null,
        createdAt: '2026-03-23T09:59:59Z',
        type: 'content.delta',
        raw: null,
        contentDelta: {
          streamKind: 'command_output',
          delta: 'npm test',
        },
      },
      {
        sequence: 2,
        eventId: 'e-file-change-output',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: null,
        requestId: null,
        createdAt: '2026-03-23T09:59:59Z',
        type: 'content.delta',
        raw: null,
        contentDelta: {
          streamKind: 'file_change_output',
          delta: 'M report.md',
        },
      },
      {
        sequence: 3,
        eventId: 'e-reasoning',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: null,
        requestId: null,
        createdAt: '2026-03-23T09:59:59Z',
        type: 'content.delta',
        raw: null,
        contentDelta: {
          streamKind: 'reasoning_text',
          delta: 'Need approval first.',
        },
      },
      {
        sequence: 4,
        eventId: 'e-reasoning-summary',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: null,
        requestId: null,
        createdAt: '2026-03-23T09:59:59Z',
        type: 'content.delta',
        raw: null,
        contentDelta: {
          streamKind: 'reasoning_summary_text',
          delta: 'Waiting for SAFE/FAST.',
        },
      },
    ] as any;

    const history = buildAppServerControlHistoryEntries(snapshot, events);

    expect(history).toHaveLength(4);
    expect(history[0]).toMatchObject({ kind: 'tool', title: 'Command output' });
    expect(history[0]?.body).toContain('npm test');
    expect(history[1]).toMatchObject({ kind: 'tool', title: 'File change output' });
    expect(history[1]?.body).toContain('M report.md');
    expect(history[2]).toMatchObject({ kind: 'reasoning', title: 'Reasoning' });
    expect(history[2]?.body).toContain('Need approval first.');
    expect(history[3]).toMatchObject({ kind: 'reasoning', title: 'Reasoning summary' });
    expect(history[3]?.body).toContain('Waiting for SAFE/FAST.');
  });

  it.skip('renders plan delta and plan completed events as a visible plan row', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-23T10:00:00Z',
      latestSequence: 2,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-23T10:00:00Z',
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
        startedAt: '2026-03-23T09:59:55Z',
        completedAt: '2026-03-23T10:00:00Z',
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
      items: [],
      requests: [],
      notices: [],
    } as any;

    const events = [
      {
        sequence: 1,
        eventId: 'e-plan-delta',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: null,
        requestId: null,
        createdAt: '2026-03-23T09:59:56Z',
        type: 'plan.delta',
        raw: null,
        planDelta: {
          delta: '1. Inspect the workspace.\n',
        },
      },
      {
        sequence: 2,
        eventId: 'e-plan-done',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: null,
        requestId: null,
        createdAt: '2026-03-23T09:59:57Z',
        type: 'plan.completed',
        raw: null,
        planCompleted: {
          planMarkdown: '1. Inspect the workspace.\n2. Apply the change.',
        },
      },
    ] as any;

    const history = buildAppServerControlHistoryEntries(snapshot, events);

    expect(history).toHaveLength(1);
    expect(history[0]).toMatchObject({
      kind: 'plan',
      title: 'Plan',
    });
    expect(history[0]?.body).toContain('2. Apply the change.');
  });

  it.skip('uses snapshot reasoning streams when event history is incomplete', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-23T10:00:00Z',
      latestSequence: 0,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-23T10:00:00Z',
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
        startedAt: '2026-03-23T09:59:50Z',
        completedAt: '2026-03-23T10:00:00Z',
      },
      streams: {
        assistantText: '',
        reasoningText: 'Need to inspect the modified files first.',
        reasoningSummaryText: 'Summary: verify output, then update docs.',
        planText: '',
        commandOutput: '',
        fileChangeOutput: '',
        unifiedDiff: '',
      },
      items: [],
      requests: [],
      notices: [],
    } as any;

    const history = buildAppServerControlHistoryEntries(snapshot);

    expect(history).toHaveLength(2);
    expect(history[0]).toMatchObject({
      kind: 'reasoning',
      title: 'Reasoning',
    });
    expect(history[0]?.body).toContain('inspect the modified files');
    expect(history[1]).toMatchObject({
      kind: 'reasoning',
      title: 'Reasoning summary',
    });
    expect(history[1]?.body).toContain('verify output');
  });

  it.skip('keeps snapshot command output and file change output as separate tool rows', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-23T10:00:00Z',
      latestSequence: 0,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-23T10:00:00Z',
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
        startedAt: '2026-03-23T09:59:50Z',
        completedAt: '2026-03-23T10:00:00Z',
      },
      streams: {
        assistantText: '',
        reasoningText: '',
        reasoningSummaryText: '',
        planText: '',
        commandOutput: 'status: TODO\nowner: codex',
        fileChangeOutput: 'Success. Updated the following files:\nM report.md',
        unifiedDiff: '',
      },
      items: [],
      requests: [],
      notices: [],
    } as any;

    const history = buildAppServerControlHistoryEntries(snapshot);

    expect(history).toHaveLength(2);
    expect(history[0]).toMatchObject({ kind: 'tool', title: 'Command output' });
    expect(history[0]?.body).toContain('status: TODO');
    expect(history[1]).toMatchObject({ kind: 'tool', title: 'File change output' });
    expect(history[1]?.body).toContain('Updated the following files');
  });

  it.skip('places fallback request rows after existing snapshot conversation content', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-23T10:00:00Z',
      latestSequence: 0,
      session: {
        state: 'running',
        stateLabel: 'Running',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-23T10:00:00Z',
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
        startedAt: '2026-03-23T09:59:50Z',
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
      items: [
        {
          itemId: 'user-1',
          turnId: 'turn-1',
          itemType: 'user_message',
          status: 'completed',
          title: 'User message',
          detail: 'Ask for SAFE or FAST before editing files.',
          attachments: [],
          updatedAt: '2026-03-23T09:59:52Z',
        },
      ],
      requests: [
        {
          requestId: 'req-1',
          turnId: 'turn-1',
          kind: 'interview',
          kindLabel: 'Question',
          state: 'open',
          detail: 'The agent needs an operator choice.',
          decision: null,
          questions: [
            {
              id: 'mode',
              header: 'Mode',
              question: 'Choose SAFE or FAST.',
              options: [
                { label: 'SAFE', description: 'Validate carefully.' },
                { label: 'FAST', description: 'Move quickly.' },
              ],
            },
          ],
          answers: [],
          updatedAt: '2026-03-23T09:59:58Z',
        },
      ],
      notices: [],
    } as any;

    const history = buildAppServerControlHistoryEntries(snapshot);

    expect(history).toHaveLength(2);
    expect(history[0]).toMatchObject({ kind: 'user' });
    expect(history[1]).toMatchObject({ kind: 'request' });
  });

  it('suppresses the active open composer request from history rendering', async () => {
    const { suppressActiveComposerRequestEntries } = await import('./index');

    const entries = [
      {
        id: 'user-1',
        order: 1,
        kind: 'user',
        tone: 'info',
        label: 'You',
        title: '',
        body: 'Do the careful path.',
        meta: '11:59:50',
      },
      {
        id: 'request:req-1',
        order: 2,
        kind: 'request',
        tone: 'warning',
        label: 'Request',
        title: 'Question',
        body: 'Choose SAFE or FAST.',
        meta: '11:59:58',
        requestId: 'req-1',
      },
      {
        id: 'diff-1',
        order: 3,
        kind: 'diff',
        tone: 'warning',
        label: 'Diff',
        title: 'Working diff',
        body: '+status: DONE',
        meta: '12:00:00',
      },
    ] as any;

    const requests = [
      {
        requestId: 'req-1',
        state: 'open',
        kind: 'approval',
        updatedAt: '2026-03-23T11:59:58Z',
      },
    ] as any;

    const visible = suppressActiveComposerRequestEntries(entries, requests);

    expect(visible).toHaveLength(2);
    expect(visible.some((entry) => entry.kind === 'request')).toBe(false);
    expect(visible[0]?.kind).toBe('user');
    expect(visible[1]?.kind).toBe('diff');
  });

  it.skip('renders question requests from user-input events into history rows', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-23T10:00:00Z',
      latestSequence: 1,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-23T10:00:00Z',
      },
      thread: {
        threadId: 'thread-1',
        state: 'active',
        stateLabel: 'Active',
      },
      currentTurn: {
        turnId: 'turn-1',
        state: 'paused',
        stateLabel: 'Paused',
        model: null,
        effort: null,
        startedAt: '2026-03-23T09:59:50Z',
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
      items: [],
      requests: [],
      notices: [],
    } as any;

    const events = [
      {
        sequence: 1,
        eventId: 'e-user-input',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: null,
        requestId: 'req-1',
        createdAt: '2026-03-23T09:59:58Z',
        type: 'user-input.requested',
        raw: null,
        userInputRequested: {
          questions: [
            {
              id: 'mode',
              header: 'Mode',
              question: 'Choose SAFE or FAST before I continue.',
              options: [
                { label: 'SAFE', description: 'Proceed cautiously.' },
                { label: 'FAST', description: 'Optimize for speed.' },
              ],
            },
          ],
        },
      },
    ] as any;

    const history = buildAppServerControlHistoryEntries(snapshot, events);

    expect(history).toHaveLength(1);
    expect(history[0]).toMatchObject({
      kind: 'request',
      requestId: 'req-1',
    });
    expect(history[0]?.body).toContain('Choose SAFE or FAST before I continue.');
    expect(history[0]?.body).toContain('[1] SAFE');
    expect(history[0]?.body).toContain('[2] FAST');
  });

  it('exposes the workflow AppServerControl debug scenario for browser-side UX validation', async () => {
    const { getAppServerControlDebugScenarioNames } = await import('./index');

    expect(getAppServerControlDebugScenarioNames()).toContain('workflow');
    expect(getAppServerControlDebugScenarioNames()).toContain('massive');
  });

  it.skip('renders a rich real-Codex event mix into visible history rows', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-22T23:59:24Z',
      latestSequence: 12,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: 'Codex turn completed.',
        lastError: null,
        lastEventAt: '2026-03-22T23:59:24Z',
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
        model: 'gpt-5.4',
        effort: 'high',
        startedAt: '2026-03-22T23:58:54Z',
        completedAt: '2026-03-22T23:59:24Z',
      },
      streams: {
        assistantText:
          'Plan:\n1. Review the workspace.\n2. Summarize the inventory.\n\n| name | count | owner |\n| --- | ---: | --- |\n| alpha | 3 | Ada |',
        reasoningText: '',
        reasoningSummaryText: '',
        planText: '',
        commandOutput: 'status: TODO',
        fileChangeOutput: 'Success. Updated the following files:\nM report.md',
        unifiedDiff: 'diff --git a/report.md b/report.md\n@@\n-status: TODO\n+status: DONE',
      },
      items: [],
      requests: [],
      notices: [],
    } as any;

    const events = [
      {
        sequence: 1,
        eventId: 'e-user-start',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'user-1',
        requestId: null,
        createdAt: '2026-03-22T23:58:54Z',
        type: 'item.started',
        raw: null,
        item: {
          itemType: 'user_message',
          status: 'in_progress',
          title: 'You',
          detail: 'Inspect the repo and update report.md.',
        },
      },
      {
        sequence: 2,
        eventId: 'e-user-done',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'user-1',
        requestId: null,
        createdAt: '2026-03-22T23:58:54Z',
        type: 'item.completed',
        raw: null,
        item: {
          itemType: 'user_message',
          status: 'completed',
          title: 'You',
          detail: 'Inspect the repo and update report.md.',
        },
      },
      {
        sequence: 3,
        eventId: 'e-command-start',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'cmd-1',
        requestId: null,
        createdAt: '2026-03-22T23:58:57Z',
        type: 'item.started',
        raw: null,
        item: {
          itemType: 'command_execution',
          status: 'in_progress',
          title: 'Command started',
          detail: 'Get-Content report.md',
        },
      },
      {
        sequence: 4,
        eventId: 'e-command-out',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'cmd-1',
        requestId: null,
        createdAt: '2026-03-22T23:58:58Z',
        type: 'content.delta',
        raw: null,
        contentDelta: {
          streamKind: 'command_output',
          delta: 'status: TODO',
        },
      },
      {
        sequence: 5,
        eventId: 'e-command-done',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'cmd-1',
        requestId: null,
        createdAt: '2026-03-22T23:58:59Z',
        type: 'item.completed',
        raw: null,
        item: {
          itemType: 'command_execution',
          status: 'completed',
          title: 'Command completed',
          detail: 'Get-Content report.md',
        },
      },
      {
        sequence: 6,
        eventId: 'e-file-start',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'file-1',
        requestId: null,
        createdAt: '2026-03-22T23:59:04Z',
        type: 'item.started',
        raw: null,
        item: {
          itemType: 'file_change',
          status: 'in_progress',
          title: 'File change started',
          detail: 'report.md',
        },
      },
      {
        sequence: 7,
        eventId: 'e-file-out',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'file-1',
        requestId: null,
        createdAt: '2026-03-22T23:59:05Z',
        type: 'content.delta',
        raw: null,
        contentDelta: {
          streamKind: 'file_change_output',
          delta: 'Success. Updated the following files:\nM report.md',
        },
      },
      {
        sequence: 8,
        eventId: 'e-file-done',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'file-1',
        requestId: null,
        createdAt: '2026-03-22T23:59:06Z',
        type: 'item.completed',
        raw: null,
        item: {
          itemType: 'file_change',
          status: 'completed',
          title: 'File change completed',
          detail: 'report.md',
        },
      },
      {
        sequence: 9,
        eventId: 'e-diff',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: null,
        requestId: null,
        createdAt: '2026-03-22T23:59:07Z',
        type: 'diff.updated',
        raw: null,
        diffUpdated: {
          unifiedDiff: 'diff --git a/report.md b/report.md\n@@\n-status: TODO\n+status: DONE',
        },
      },
      {
        sequence: 10,
        eventId: 'e-assistant-final',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'assistant-1',
        requestId: null,
        createdAt: '2026-03-22T23:59:20Z',
        type: 'content.delta',
        raw: null,
        contentDelta: {
          streamKind: 'assistant_text',
          delta:
            'Plan:\n1. Review the workspace.\n2. Summarize the inventory.\n\n| name | count | owner |\n| --- | ---: | --- |\n| alpha | 3 | Ada |',
        },
      },
    ] as any;

    const history = buildAppServerControlHistoryEntries(snapshot, events);
    const userEntry = history.find((entry) => entry.kind === 'user');
    const commandCallEntry = history.find(
      (entry) =>
        entry.kind === 'tool' &&
        (entry.commandText?.includes('Get-Content report.md') ||
          entry.body.includes('Get-Content report.md')),
    );
    const fileChangeEntry = history.find(
      (entry) =>
        entry.kind === 'tool' && entry.body.includes('Success. Updated the following files'),
    );
    const diffEntry = history.find((entry) => entry.kind === 'diff');
    const assistantEntry = history.find((entry) => entry.kind === 'assistant');

    expect(userEntry?.body).toContain('update report.md');
    expect(commandCallEntry?.commandText ?? commandCallEntry?.body).toContain(
      'Get-Content report.md',
    );
    expect(commandCallEntry?.commandOutputTail).toEqual(['status: TODO']);
    expect(commandCallEntry?.body).toBe('');
    expect(history.some((entry) => entry.kind === 'tool' && entry.title === 'Command output')).toBe(
      false,
    );
    expect(fileChangeEntry?.body).toContain('report.md');
    expect(diffEntry?.body).toContain('+status: DONE');
    expect(assistantEntry?.body).toContain('| alpha | 3 | Ada |');
  });

  it('keeps enough tail lines to honor the configurable tool output cap', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      history: [
        {
          entryId: 'tool-command',
          order: 1,
          kind: 'tool',
          status: 'completed',
          itemType: 'command_execution',
          title: 'Tool completed',
          body: 'git log --oneline',
          attachments: [],
          streaming: false,
          createdAt: '2026-04-04T20:00:00Z',
          updatedAt: '2026-04-04T20:00:00Z',
        },
        {
          entryId: 'tool-output',
          order: 2,
          kind: 'tool',
          status: 'completed',
          itemType: 'command_output',
          title: 'Command output',
          body: Array.from({ length: 15 }, (_, index) => `line ${index + 1}`).join('\n'),
          attachments: [],
          streaming: false,
          createdAt: '2026-04-04T20:00:01Z',
          updatedAt: '2026-04-04T20:00:01Z',
        },
      ],
    } as any;

    const history = buildAppServerControlHistoryEntries(snapshot);
    expect(history).toHaveLength(1);
    expect(history[0]?.commandOutputTail).toEqual(
      Array.from({ length: 14 }, (_, index) => `line ${index + 2}`),
    );
  });

  it('renders direct command-output history rows as persistent Ran rows with folded tail output', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      history: [
        {
          entryId: 'tool:cmd-1',
          order: 1,
          kind: 'tool',
          status: 'completed',
          itemType: 'command_output',
          title: 'Run command',
          body: 'git status --short --branch\n\n## dev...origin/dev',
          itemId: null,
          attachments: [],
          streaming: false,
          createdAt: '2026-04-05T00:20:00Z',
          updatedAt: '2026-04-05T00:20:01Z',
        },
      ],
    } as any;

    const history = buildAppServerControlHistoryEntries(snapshot);

    expect(history).toHaveLength(1);
    expect(history[0]?.body).toBe('');
    expect(history[0]?.commandText).toBe('git status --short --branch');
    expect(history[0]?.commandOutputTail).toEqual(['## dev...origin/dev']);
  });

  it('uses canonical commandText for truncated command-output rows instead of treating omission markers as commands', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      history: [
        {
          entryId: 'tool:cmd-omitted',
          order: 1,
          kind: 'tool',
          status: 'completed',
          itemType: 'command_output',
          title: 'Run command',
          commandText: 'codex -m gpt-5.4',
          body: '... earlier output omitted ...\nline 28 xxxxx\nline 29 xxxxx',
          itemId: 'cmd-1',
          attachments: [],
          streaming: false,
          createdAt: '2026-04-08T18:00:00Z',
          updatedAt: '2026-04-08T18:00:01Z',
        },
      ],
    } as any;

    const history = buildAppServerControlHistoryEntries(snapshot);

    expect(history).toHaveLength(1);
    expect(history[0]?.commandText).toBe('codex -m gpt-5.4');
    expect(history[0]?.commandOutputTail).toEqual([
      '... earlier output omitted ...',
      'line 28 xxxxx',
      'line 29 xxxxx',
    ]);
  });

  it('does not synthesize a Ran row from omission markers when command text is unavailable', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      history: [
        {
          entryId: 'tool:cmd-omitted-raw',
          order: 1,
          kind: 'tool',
          status: 'completed',
          itemType: 'command_output',
          title: 'Run command',
          body: '... earlier output omitted ...\nline 28 xxxxx',
          itemId: 'cmd-1',
          attachments: [],
          streaming: false,
          createdAt: '2026-04-08T18:00:00Z',
          updatedAt: '2026-04-08T18:00:01Z',
        },
      ],
    } as any;

    const history = buildAppServerControlHistoryEntries(snapshot);

    expect(history).toHaveLength(1);
    expect(history[0]?.commandText ?? '').toBe('');
    expect(history[0]?.body).toContain('... earlier output omitted ...');
  });

  it('renders normalized command-output rows through the dedicated command presentation', async () => {
    const { resolveHistoryBodyPresentation } = await import('./index');

    const presentation = resolveHistoryBodyPresentation({
      id: 'tool:cmd-1',
      order: 1,
      kind: 'tool',
      tone: 'positive',
      label: 'Tool',
      title: 'Run command',
      body: '',
      meta: '',
      sourceItemType: 'command_output',
      commandText: 'git status --short --branch',
      commandOutputTail: ['## dev...origin/dev'],
    });

    expect(presentation.mode).toBe('command');
    expect(presentation.collapsedByDefault).toBe(false);
    expect(presentation.lineCount).toBe(2);
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

  it('keeps previously shown command rows materialized while they remain inside the active history window', async () => {
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

    const stabilized = preservePersistentCommandEntries([], previousEntries, {
      historyWindowStart: 0,
      historyWindowEnd: 1,
    });

    expect(stabilized).toHaveLength(1);
    expect(stabilized[0]).toMatchObject({
      id: 'tool:cmd-1',
      commandText: 'git status --short --branch',
      commandOutputTail: ['## dev...origin/dev'],
    });
  });

  it.skip('keeps Codex user rows visible and avoids duplicate assistant rows for camelCase item types', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-21T11:59:24Z',
      latestSequence: 12,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: 'Codex turn completed.',
        lastError: null,
        lastEventAt: '2026-03-21T11:59:24Z',
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
        startedAt: '2026-03-21T11:59:14Z',
        completedAt: '2026-03-21T11:59:18Z',
      },
      streams: {
        assistantText: 'HELLO_FROM_CODEX',
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
    } as any;

    const events = [
      {
        sequence: 1,
        eventId: 'e-user-start',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'user-1',
        requestId: null,
        createdAt: '2026-03-21T11:59:14Z',
        type: 'item.started',
        raw: null,
        item: {
          itemType: 'usermessage',
          status: 'in_progress',
          title: 'Tool started',
          detail: 'Reply with exactly HELLO_FROM_CODEX',
        },
      },
      {
        sequence: 2,
        eventId: 'e-user-done',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'user-1',
        requestId: null,
        createdAt: '2026-03-21T11:59:14Z',
        type: 'item.completed',
        raw: null,
        item: {
          itemType: 'usermessage',
          status: 'completed',
          title: 'Tool completed',
          detail: 'Reply with exactly HELLO_FROM_CODEX',
        },
      },
      {
        sequence: 3,
        eventId: 'e-assistant-delta',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'assistant-1',
        requestId: null,
        createdAt: '2026-03-21T11:59:18Z',
        type: 'content.delta',
        raw: null,
        contentDelta: {
          streamKind: 'assistant_text',
          delta: 'HELLO_FROM_CODEX',
        },
      },
      {
        sequence: 4,
        eventId: 'e-assistant-done',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'assistant-1',
        requestId: null,
        createdAt: '2026-03-21T11:59:18Z',
        type: 'item.completed',
        raw: null,
        item: {
          itemType: 'assistant_message',
          status: 'completed',
          title: 'Assistant message',
          detail: 'HELLO_FROM_CODEX',
        },
      },
      {
        sequence: 5,
        eventId: 'e-command-done',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'cmd-1',
        requestId: null,
        createdAt: '2026-03-21T11:59:17Z',
        type: 'item.completed',
        raw: null,
        item: {
          itemType: 'commandexecution',
          status: 'completed',
          title: 'Tool completed',
          detail: '"C:\\Program Files\\PowerShell\\7\\pwsh.exe" -Command pwd',
        },
      },
    ] as any;

    const history = buildAppServerControlHistoryEntries(snapshot, events);
    const userEntries = history.filter((entry) => entry.kind === 'user');
    const assistantEntries = history.filter((entry) => entry.kind === 'assistant');
    const toolEntries = history.filter((entry) => entry.kind === 'tool');

    expect(userEntries).toHaveLength(1);
    expect(userEntries[0]?.body).toContain('Reply with exactly HELLO_FROM_CODEX');
    expect(userEntries[0]?.title).toBe('You');

    expect(assistantEntries).toHaveLength(1);
    expect(assistantEntries[0]?.body).toBe('HELLO_FROM_CODEX');

    expect(toolEntries).toHaveLength(1);
    expect(toolEntries[0]?.title).toContain('pwsh.exe');
    expect(toolEntries[0]?.body).toContain('pwsh.exe');
  });

  it.skip('concatenates assistant stream chunks without paragraph separators or duplicate final text', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-21T12:39:16Z',
      latestSequence: 8,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: 'Codex turn completed.',
        lastError: null,
        lastEventAt: '2026-03-21T12:39:07Z',
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
        startedAt: '2026-03-21T12:39:01Z',
        completedAt: '2026-03-21T12:39:07Z',
      },
      streams: {
        assistantText: 'HELLO_FROM_CODEX',
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
    } as any;

    const events = [
      {
        sequence: 1,
        eventId: 'e-assistant-start',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'assistant-1',
        requestId: null,
        createdAt: '2026-03-21T12:39:07Z',
        type: 'item.started',
        raw: null,
        item: {
          itemType: 'assistant_message',
          status: 'in_progress',
          title: 'Tool started',
          detail: '',
        },
      },
      {
        sequence: 2,
        eventId: 'e-delta-1',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'assistant-1',
        requestId: null,
        createdAt: '2026-03-21T12:39:07Z',
        type: 'content.delta',
        raw: null,
        contentDelta: {
          streamKind: 'assistant_text',
          delta: 'HEL',
        },
      },
      {
        sequence: 3,
        eventId: 'e-delta-2',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'assistant-1',
        requestId: null,
        createdAt: '2026-03-21T12:39:07Z',
        type: 'content.delta',
        raw: null,
        contentDelta: {
          streamKind: 'assistant_text',
          delta: 'LO',
        },
      },
      {
        sequence: 4,
        eventId: 'e-delta-3',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'assistant-1',
        requestId: null,
        createdAt: '2026-03-21T12:39:07Z',
        type: 'content.delta',
        raw: null,
        contentDelta: {
          streamKind: 'assistant_text',
          delta: '_FROM',
        },
      },
      {
        sequence: 5,
        eventId: 'e-delta-4',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'assistant-1',
        requestId: null,
        createdAt: '2026-03-21T12:39:07Z',
        type: 'content.delta',
        raw: null,
        contentDelta: {
          streamKind: 'assistant_text',
          delta: '_CODE',
        },
      },
      {
        sequence: 6,
        eventId: 'e-delta-5',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'assistant-1',
        requestId: null,
        createdAt: '2026-03-21T12:39:07Z',
        type: 'content.delta',
        raw: null,
        contentDelta: {
          streamKind: 'assistant_text',
          delta: 'X',
        },
      },
      {
        sequence: 7,
        eventId: 'e-assistant-done',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'assistant-1',
        requestId: null,
        createdAt: '2026-03-21T12:39:07Z',
        type: 'item.completed',
        raw: null,
        item: {
          itemType: 'assistant_message',
          status: 'completed',
          title: 'Assistant message',
          detail: 'HELLO_FROM_CODEX',
        },
      },
    ] as any;

    const history = buildAppServerControlHistoryEntries(snapshot, events);
    const assistantEntries = history.filter((entry) => entry.kind === 'assistant');

    expect(assistantEntries).toHaveLength(1);
    expect(assistantEntries[0]?.body).toBe('HELLO_FROM_CODEX');
  });

  it.skip('keeps separate assistant updates from the same turn in distinct rows when item ids differ', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-24T19:00:00Z',
      latestSequence: 2,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-24T19:00:00Z',
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
        startedAt: '2026-03-24T18:59:50Z',
        completedAt: '2026-03-24T19:00:00Z',
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
      items: [],
      requests: [],
      notices: [],
    } as any;

    const events = [
      {
        sequence: 1,
        eventId: 'assistant-1',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'assistant-item-1',
        requestId: null,
        createdAt: '2026-03-24T18:59:55Z',
        type: 'item.completed',
        raw: null,
        item: {
          itemType: 'assistant_message',
          status: 'completed',
          title: 'Assistant message',
          detail: 'Ich pruefe kurz die lokale MidTerm-Anweisung.',
        },
      },
      {
        sequence: 2,
        eventId: 'assistant-2',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'assistant-item-2',
        requestId: null,
        createdAt: '2026-03-24T18:59:57Z',
        type: 'item.completed',
        raw: null,
        item: {
          itemType: 'assistant_message',
          status: 'completed',
          title: 'Assistant message',
          detail: 'Die DMI-Kennung ist leer, daher pruefe ich jetzt direkt das ARM-Board-Modell.',
        },
      },
    ] as any;

    const history = buildAppServerControlHistoryEntries(snapshot, events);
    const assistantEntries = history.filter((entry) => entry.kind === 'assistant');

    expect(assistantEntries).toHaveLength(2);
    expect(assistantEntries[0]?.body).toBe('Ich pruefe kurz die lokale MidTerm-Anweisung.');
    expect(assistantEntries[1]?.body).toBe(
      'Die DMI-Kennung ist leer, daher pruefe ich jetzt direkt das ARM-Board-Modell.',
    );
  });

  it.skip('keeps the first-seen order for a live assistant row instead of moving it on completion', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-24T19:10:00Z',
      latestSequence: 3,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-24T19:10:00Z',
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
        startedAt: '2026-03-24T19:09:50Z',
        completedAt: '2026-03-24T19:10:00Z',
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
      items: [],
      requests: [],
      notices: [],
    } as any;

    const events = [
      {
        sequence: 1,
        eventId: 'assistant-delta',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'assistant-1',
        requestId: null,
        createdAt: '2026-03-24T19:09:55Z',
        type: 'content.delta',
        raw: null,
        contentDelta: {
          streamKind: 'assistant_text',
          delta: 'Working',
        },
      },
      {
        sequence: 2,
        eventId: 'tool-completed',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'tool-1',
        requestId: null,
        createdAt: '2026-03-24T19:09:56Z',
        type: 'item.completed',
        raw: null,
        item: {
          itemType: 'command_execution',
          status: 'completed',
          title: 'Command completed',
          detail: 'git status',
        },
      },
      {
        sequence: 3,
        eventId: 'assistant-completed',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'assistant-1',
        requestId: null,
        createdAt: '2026-03-24T19:09:59Z',
        type: 'item.completed',
        raw: null,
        item: {
          itemType: 'assistant_message',
          status: 'completed',
          title: 'Assistant message',
          detail: 'Working response.',
        },
      },
    ] as any;

    const history = buildAppServerControlHistoryEntries(snapshot, events);

    expect(history[0]).toMatchObject({
      kind: 'assistant',
      body: 'Working response.',
    });
    expect(history[1]).toMatchObject({
      kind: 'tool',
      title: 'git status',
    });
  });

  it.skip('prefers the final Codex assistant message when it supersedes streamed chunks', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-21T18:00:00Z',
      latestSequence: 6,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: 'Codex turn completed.',
        lastError: null,
        lastEventAt: '2026-03-21T18:00:00Z',
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
        startedAt: '2026-03-21T17:59:54Z',
        completedAt: '2026-03-21T18:00:00Z',
      },
      streams: {
        assistantText: 'The answer is 42.',
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
    } as any;

    const events = [
      {
        sequence: 1,
        eventId: 'e-delta-1',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'assistant-1',
        requestId: null,
        createdAt: '2026-03-21T17:59:58Z',
        type: 'content.delta',
        raw: null,
        contentDelta: {
          streamKind: 'assistant_text',
          delta: 'The answer',
        },
      },
      {
        sequence: 2,
        eventId: 'e-delta-2',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'assistant-1',
        requestId: null,
        createdAt: '2026-03-21T17:59:59Z',
        type: 'content.delta',
        raw: null,
        contentDelta: {
          streamKind: 'assistant_text',
          delta: ' is',
        },
      },
      {
        sequence: 3,
        eventId: 'e-delta-3',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'assistant-1',
        requestId: null,
        createdAt: '2026-03-21T17:59:59Z',
        type: 'content.delta',
        raw: null,
        contentDelta: {
          streamKind: 'assistant_text',
          delta: ' 42',
        },
      },
      {
        sequence: 4,
        eventId: 'e-assistant-done',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'assistant-1',
        requestId: null,
        createdAt: '2026-03-21T18:00:00Z',
        type: 'item.completed',
        raw: null,
        item: {
          itemType: 'assistant_message',
          status: 'completed',
          title: 'Assistant message',
          detail: 'The answer is 42.',
        },
      },
    ] as any;

    const history = buildAppServerControlHistoryEntries(snapshot, events);
    const assistantEntries = history.filter((entry) => entry.kind === 'assistant');

    expect(assistantEntries).toHaveLength(1);
    expect(assistantEntries[0]?.body).toBe('The answer is 42.');
  });

  it.skip('keeps one user row when Codex emits repeated started/completed message payloads', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-21T18:05:00Z',
      latestSequence: 4,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-21T18:05:00Z',
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
        startedAt: '2026-03-21T18:04:56Z',
        completedAt: '2026-03-21T18:05:00Z',
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
      items: [],
      requests: [],
      notices: [],
    } as any;

    const events = [
      {
        sequence: 1,
        eventId: 'e-user-start',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'user-1',
        requestId: null,
        createdAt: '2026-03-21T18:04:57Z',
        type: 'item.started',
        raw: null,
        item: {
          itemType: 'user_message',
          status: 'in_progress',
          title: 'Tool started',
          detail: 'Explain the recent AppServerControl history bug.',
        },
      },
      {
        sequence: 2,
        eventId: 'e-user-completed',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'user-1',
        requestId: null,
        createdAt: '2026-03-21T18:04:58Z',
        type: 'item.completed',
        raw: null,
        item: {
          itemType: 'user_message',
          status: 'completed',
          title: 'Tool completed',
          detail: 'Explain the recent AppServerControl history bug.',
        },
      },
    ] as any;

    const history = buildAppServerControlHistoryEntries(snapshot, events);
    const userEntries = history.filter((entry) => entry.kind === 'user');

    expect(userEntries).toHaveLength(1);
    expect(userEntries[0]?.body).toBe('Explain the recent AppServerControl history bug.');
  });

  it.skip('merges a local submitted user row with the later provider user item for the same turn', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-22T01:30:00Z',
      latestSequence: 3,
      session: {
        state: 'running',
        stateLabel: 'Running',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-22T01:30:00Z',
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
        startedAt: '2026-03-22T01:29:58Z',
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
      items: [],
      requests: [],
      notices: [],
    } as any;

    const events = [
      {
        sequence: 1,
        eventId: 'e-local-user',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'local-user:turn-1',
        createdAt: '2026-03-22T01:29:58Z',
        type: 'item.completed',
        raw: null,
        item: {
          itemType: 'user_message',
          status: 'completed',
          title: 'User message',
          detail: 'Please inspect this image.',
          attachments: [
            {
              kind: 'image',
              path: 'Q:/repo/.midterm/uploads/screen.png',
              mimeType: 'image/png',
              displayName: 'screen.png',
            },
          ],
        },
      },
      {
        sequence: 2,
        eventId: 'e-provider-user',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'provider-user-1',
        createdAt: '2026-03-22T01:29:59Z',
        type: 'item.completed',
        raw: null,
        item: {
          itemType: 'user_message',
          status: 'completed',
          title: 'User message',
          detail: 'Please inspect this image.',
          attachments: [],
        },
      },
    ] as any;

    const history = buildAppServerControlHistoryEntries(snapshot, events);
    const userEntries = history.filter((entry) => entry.kind === 'user');

    expect(userEntries).toHaveLength(1);
    expect(userEntries[0]?.body).toBe('Please inspect this image.');
    expect(userEntries[0]?.attachments).toHaveLength(1);
    expect(userEntries[0]?.attachments?.[0]?.displayName).toBe('screen.png');
  });

  it('keeps attachment-only user rows visible in the history', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-22T01:40:00Z',
      latestSequence: 1,
      session: {
        state: 'running',
        stateLabel: 'Running',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-22T01:40:00Z',
      },
      thread: {
        threadId: 'thread-1',
        state: 'active',
        stateLabel: 'Active',
      },
      currentTurn: {
        turnId: 'turn-2',
        state: 'running',
        stateLabel: 'Running',
        model: null,
        effort: null,
        startedAt: '2026-03-22T01:39:58Z',
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
      history: [
        {
          entryId: 'user:turn-2',
          order: 1,
          kind: 'user',
          status: 'completed',
          title: '',
          body: '',
          updatedAt: '2026-03-22T01:40:00Z',
          turnId: 'turn-2',
          itemId: 'local-user:turn-2',
          itemType: 'user_message',
          attachments: [
            {
              kind: 'image',
              path: 'Q:/repo/.midterm/uploads/photo.png',
              mimeType: 'image/png',
              displayName: 'photo.png',
            },
          ],
          streaming: false,
        },
      ],
      items: [],
      requests: [],
      notices: [],
    } as any;

    const history = buildAppServerControlHistoryEntries(snapshot);
    const userEntries = history.filter((entry) => entry.kind === 'user');

    expect(userEntries).toHaveLength(1);
    expect(userEntries[0]?.body).toBe('');
    expect(userEntries[0]?.attachments).toHaveLength(1);
  });

  it.skip('keeps user text from item title and still falls back to snapshot assistant text', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-20T10:00:00Z',
      latestSequence: 4,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-20T10:00:00Z',
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
        model: 'gpt-5',
        effort: 'medium',
        startedAt: '2026-03-20T09:59:00Z',
        completedAt: '2026-03-20T10:00:00Z',
      },
      streams: {
        assistantText: 'Final answer from snapshot',
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
    } as any;

    const events = [
      {
        sequence: 1,
        eventId: 'user-title-only',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'user-1',
        requestId: null,
        createdAt: '2026-03-20T09:59:00Z',
        type: 'item.completed',
        raw: null,
        sessionState: null,
        threadState: null,
        turnStarted: null,
        turnCompleted: null,
        contentDelta: null,
        planDelta: null,
        planCompleted: null,
        diffUpdated: null,
        item: {
          itemType: 'user_message',
          status: 'completed',
          title: 'Please summarize the failing test run.',
          detail: '',
        },
        requestOpened: null,
        requestResolved: null,
        userInputRequested: null,
        userInputResolved: null,
        runtimeMessage: null,
      },
      {
        sequence: 2,
        eventId: 'assistant-empty-item',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'assistant-1',
        requestId: null,
        createdAt: '2026-03-20T09:59:20Z',
        type: 'item.completed',
        raw: null,
        sessionState: null,
        threadState: null,
        turnStarted: null,
        turnCompleted: null,
        contentDelta: null,
        planDelta: null,
        planCompleted: null,
        diffUpdated: null,
        item: {
          itemType: 'assistant_message',
          status: 'completed',
          title: 'Assistant',
          detail: '',
        },
        requestOpened: null,
        requestResolved: null,
        userInputRequested: null,
        userInputResolved: null,
        runtimeMessage: null,
      },
    ] as any;

    const history = buildAppServerControlHistoryEntries(snapshot, events);

    expect(history.find((entry) => entry.kind === 'user')?.body).toContain(
      'Please summarize the failing test run.',
    );
    expect(
      history.find(
        (entry) => entry.kind === 'assistant' && entry.body.includes('Final answer from snapshot'),
      ),
    ).toBeTruthy();
  });

  it.skip('hides completed-status noise in normal chat row metadata', async () => {
    const { buildAppServerControlHistoryEntries, formatHistoryMeta, shouldHideStatusInMeta } =
      await import('./index');

    expect(shouldHideStatusInMeta('user', 'Completed')).toBe(true);
    expect(shouldHideStatusInMeta('assistant', 'Assistant Text')).toBe(true);
    expect(shouldHideStatusInMeta('request', 'Completed')).toBe(false);
    expect(formatHistoryMeta('user', 'Completed', '2026-03-21T15:09:20Z')).not.toContain(
      'Completed',
    );

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-03-21T15:09:22Z',
      latestSequence: 4,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: null,
        lastError: null,
        lastEventAt: '2026-03-21T15:09:22Z',
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
        startedAt: '2026-03-21T15:09:15Z',
        completedAt: '2026-03-21T15:09:22Z',
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
      items: [],
      requests: [],
      notices: [],
    } as any;

    const events = [
      {
        sequence: 1,
        eventId: 'user-completed',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'user-1',
        requestId: null,
        createdAt: '2026-03-21T15:09:20Z',
        type: 'item.completed',
        raw: null,
        item: {
          itemType: 'user_message',
          status: 'completed',
          title: 'Tool completed',
          detail: 'Reply with exactly HELLO_FROM_SOURCE_APP_SERVER_CONTROL.',
        },
      },
      {
        sequence: 2,
        eventId: 'assistant-completed',
        sessionId: 's1',
        provider: 'codex',
        threadId: 'thread-1',
        turnId: 'turn-1',
        itemId: 'assistant-1',
        requestId: null,
        createdAt: '2026-03-21T15:09:22Z',
        type: 'item.completed',
        raw: null,
        item: {
          itemType: 'assistant_message',
          status: 'completed',
          title: 'Assistant message',
          detail: 'HELLO_FROM_SOURCE_APP_SERVER_CONTROL',
        },
      },
    ] as any;

    const history = buildAppServerControlHistoryEntries(snapshot, events);
    const userEntry = history.find((entry) => entry.kind === 'user');
    const assistantEntry = history.find((entry) => entry.kind === 'assistant');

    expect(userEntry?.meta).not.toContain('Completed');
    expect(assistantEntry?.meta).not.toContain('Completed');
    expect(userEntry?.meta).toMatch(/\d{2}:\d{2}/);
    expect(assistantEntry?.meta).toMatch(/\d{2}:\d{2}/);
  });

  it('virtualizes older history rows but keeps a visible window', async () => {
    const { computeHistoryVirtualWindow } = await import('./index');

    const entries = Array.from({ length: 120 }, (_, index) => ({
      id: `row-${index}`,
      order: index,
      kind: 'assistant',
      tone: 'info',
      label: 'Assistant',
      title: '',
      body: `Row ${index} `.repeat(8),
      meta: 'now',
    })) as any;

    const windowed = computeHistoryVirtualWindow(entries, 1800, 900);

    expect(windowed.start).toBeGreaterThan(0);
    expect(windowed.end).toBeLessThan(entries.length);
    expect(windowed.topSpacerPx).toBeGreaterThan(0);
    expect(windowed.bottomSpacerPx).toBeGreaterThan(0);
  });

  it('keeps history virtualization active on compact mobile widths too', async () => {
    const { computeHistoryVirtualWindow } = await import('./index');

    const entries = Array.from({ length: 120 }, (_, index) => ({
      id: `row-${index}`,
      order: index,
      kind: 'assistant',
      tone: 'info',
      label: 'Assistant',
      title: '',
      body: `Row ${index} `.repeat(12),
      meta: 'now',
    })) as any;

    const windowed = computeHistoryVirtualWindow(entries, 1800, 900, 375);

    expect(windowed.start).toBeGreaterThan(0);
    expect(windowed.end).toBeLessThan(entries.length);
    expect(windowed.topSpacerPx).toBeGreaterThan(0);
    expect(windowed.bottomSpacerPx).toBeGreaterThan(0);
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

  it('keeps the 10k AppServerControl debug history keyed per retained item', async () => {
    const { buildAppServerControlDebugScenario } = await import('./debugScenario');

    const { snapshot } = buildAppServerControlDebugScenario(
      's1',
      'massive',
      'https://example.test',
    );
    const entryIds = snapshot.history.map((entry) => entry.entryId);

    expect(snapshot.history).toHaveLength(10000);
    expect(new Set(entryIds).size).toBe(entryIds.length);
    expect(entryIds[0]).toBe('user:user-massive-1');
    expect(entryIds[1]).toBe('assistant:assistant-massive-2');
    expect(entryIds.at(-1)).toBe('assistant:assistant-massive-10000');
  });

  it('subtracts older-history top spacer height before resolving the visible window', async () => {
    const { computeHistoryVisibleRange } = await import('./historyViewport');
    const { resolveHistoryWindowViewportMetrics } = await import('./historyRender');

    const entries = Array.from({ length: 80 }, (_, index) => ({
      id: `row-${index}`,
      order: index + 41,
      kind: 'assistant',
      tone: 'info',
      label: 'Assistant',
      title: '',
      body: `Row ${index + 41}`,
      meta: 'now',
    })) as any;

    const viewportMetrics = resolveHistoryWindowViewportMetrics(
      entries,
      {
        snapshot: {
          historyWindowStart: 40,
          historyWindowEnd: 120,
          historyCount: 200,
        },
        historyObservedHeights: new Map(),
      } as any,
      {
        scrollTop: 4350,
        clientHeight: 600,
        clientWidth: 900,
      },
      () => 100,
    );

    expect(viewportMetrics.offWindowTopSpacerPx).toBe(2800);
    expect(viewportMetrics.effectiveOffWindowTopSpacerPx).toBe(2800);
    expect(viewportMetrics.scrollTop).toBe(1550);

    const visibleRange = computeHistoryVisibleRange(
      entries,
      viewportMetrics.scrollTop,
      viewportMetrics.clientHeight,
      viewportMetrics.clientWidth,
      () => 100,
    );
    const unadjustedRange = computeHistoryVisibleRange(entries, 4350, 600, 900, () => 100);

    expect(visibleRange.start).toBe(15);
    expect(unadjustedRange.start).toBe(43);
  });

  it('resolves the actual visible history slice even when the retained window stays below the DOM virtualization threshold', async () => {
    const { computeHistoryVisibleRange } = await import('./historyViewport');

    const entries = Array.from({ length: 35 }, (_, index) => ({
      id: `row-${index}`,
      order: index + 1,
      kind: 'assistant',
      tone: 'info',
      label: 'Assistant',
      title: '',
      body: `Row ${index + 1}`,
      meta: 'now',
    })) as any;

    const visibleRange = computeHistoryVisibleRange(entries, 1800, 600, 900, () => 100);

    expect(visibleRange.start).toBe(18);
    expect(visibleRange.end).toBe(24);
  });

  it('caps the viewport-aligned off-window top spacer when estimates exceed the current scroll offset', async () => {
    const { resolveHistoryWindowViewportMetrics } = await import('./historyRender');

    const entries = Array.from({ length: 80 }, (_, index) => ({
      id: `row-${index}`,
      order: index + 41,
      kind: 'assistant',
      tone: 'info',
      label: 'Assistant',
      title: '',
      body: `Row ${index + 41}`,
      meta: 'now',
    })) as any;

    const viewportMetrics = resolveHistoryWindowViewportMetrics(
      entries,
      {
        snapshot: {
          historyWindowStart: 40,
          historyWindowEnd: 120,
          historyCount: 200,
        },
        historyObservedHeights: new Map(),
      } as any,
      {
        scrollTop: 240,
        clientHeight: 600,
        clientWidth: 900,
      },
      () => 100,
    );

    expect(viewportMetrics.offWindowTopSpacerPx).toBe(2800);
    expect(viewportMetrics.effectiveOffWindowTopSpacerPx).toBe(240);
    expect(viewportMetrics.scrollTop).toBe(0);
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
    expect(scheduleHistoryRender).toHaveBeenCalled();
  });

  it('keeps the progress navigator active for short histories with tall rendered rows', async () => {
    const { createAgentHistoryRender } = await import('./historyRender');

    const historyViewport = createMockDomNode({
      childNodes: [],
      children: [],
      clientHeight: 629,
      clientWidth: 900,
      scrollTop: 0,
      scrollHeight: 0,
      querySelector: vi.fn(() => null),
      getBoundingClientRect: vi.fn(() => ({ top: 0, bottom: 629 })),
    });
    const progressNav = createMockDomNode({
      clientHeight: 629,
      clientWidth: 14,
      hidden: false,
      dataset: {},
      setAttribute: vi.fn(),
      getBoundingClientRect: vi.fn(() => ({ top: 0, height: 629 })),
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
        historyWindowEnd: 10,
        historyCount: 10,
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
      historyNavigatorAnchorIndex: null,
      historyNavigatorDragTargetIndex: null,
      historyNavigatorQueuedTargetIndex: null,
      historyNavigatorQueuedRequestKind: null,
      historyNavigatorPreviewHandle: null,
      historyNavigatorHydrateHandle: null,
      historyNavigatorLastPreviewRequestAt: 0,
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

    const entries = [67, 67, 63, 96, 519, 57, 85, 363, 2890, 35].map((heightPx, index) => ({
      id: `row-${index}`,
      order: index + 1,
      kind: 'assistant',
      tone: 'info',
      label: 'Assistant',
      title: '',
      body: `Row ${index + 1}`,
      meta: 'now',
      estimatedHeightPx: heightPx,
    })) as any;

    render.renderActivationView('s1', panel, state, entries);

    expect(progressNav.hidden).toBe(false);
    expect(progressNav.dataset.ready).toBe('true');
    expect(progressNav.tabIndex).toBe(0);
    expect(progressNav.setAttribute).toHaveBeenCalledWith('aria-disabled', 'false');
    expect(String(progressThumb.style.height)).not.toBe('');
    expect(String(progressThumb.style.top)).toBe('6px');

    historyViewport.scrollTop = 4000;
    render.syncViewportOffset('s1');

    expect(String(progressThumb.style.top)).not.toBe('6px');
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

  it('realigns browse-mode history when every rendered row drifts outside the viewport', async () => {
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
    const state = {
      panel,
      snapshot: {
        historyWindowStart: 0,
        historyWindowEnd: 80,
        historyCount: 80,
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

    expect(historyViewport.scrollTop).toBe(776);
  });

  it('keeps viewport-scroll rerendering enabled for short retained windows when spacers are active', async () => {
    const { createAgentHistoryRender } = await import('./historyRender');

    const historyViewport = createMockDomNode({
      childNodes: [],
      children: [],
      clientHeight: 606,
      clientWidth: 900,
      scrollTop: 21740,
      scrollHeight: 100360,
      querySelector: vi.fn(() => null),
      getBoundingClientRect: vi.fn(() => ({ top: 0, bottom: 606 })),
    });
    const offscreenNode = createMockDomNode({
      getBoundingClientRect: vi.fn(() => ({ top: -1200, bottom: -1100, height: 100 })),
    });
    const state = {
      panel: createMockDomNode(),
      snapshot: {
        historyWindowStart: 200,
        historyWindowEnd: 213,
        historyCount: 520,
        provider: 'codex',
        requests: [],
      },
      historyViewport,
      historyEntries: Array.from({ length: 13 }, (_, index) => ({
        id: `row-${index}`,
        order: 201 + index,
        kind: 'assistant',
        tone: 'info',
        label: 'Assistant',
        title: '',
        body: `Row ${201 + index}`,
        meta: 'now',
      })),
      historyRenderedNodes: new Map([
        [
          'row-0',
          {
            node: offscreenNode,
            signature: 'sig',
            entry: null,
            cluster: null,
            lastMeasuredWidthBucket: null,
          },
        ],
      ]),
      historyMeasuredHeights: new Map(),
      historyObservedHeights: new Map(),
      historyMeasuredHeightsByBucket: new Map(),
      historyObservedHeightsByBucket: new Map(),
      historyObservedHeightSamplesByBucket: new Map(),
      historyMeasuredWidthBucket: 0,
      historyLeadingPlaceholders: [createMockDomNode()],
      historyTrailingPlaceholders: [createMockDomNode()],
      historyEmptyState: null,
      pendingHistoryPrependAnchor: null,
      pendingHistoryLayoutAnchor: null,
      historyLastVirtualWindowKey: '7:19',
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
      createHistoryEntry: vi.fn(() => createMockDomNode()),
      createHistorySpacer: vi.fn(() => createMockDomNode()),
      createRequestActionBlock: vi.fn(() => createMockDomNode()),
      pruneAssistantMarkdownCache: vi.fn(),
      renderRuntimeStats: vi.fn(),
    });

    expect(render.shouldRenderForViewportScroll(state)).toBe(true);
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
    expect(syncViewportHistoryWindow).toHaveBeenCalledWith('s1');
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

    expect(measuredNode.getBoundingClientRect).toHaveBeenCalledTimes(1);
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

  it('estimates taller history rows for narrow viewports', async () => {
    const { estimateHistoryEntryHeight } = await import('./index');

    const entry = {
      id: 'assistant-1',
      order: 1,
      kind: 'assistant',
      tone: 'info',
      label: 'Assistant',
      title: '',
      body: 'A long assistant message '.repeat(20),
      meta: 'now',
    } as any;

    const desktopEstimate = estimateHistoryEntryHeight(entry, 960);
    const mobileEstimate = estimateHistoryEntryHeight(entry, 420);

    expect(mobileEstimate).toBeGreaterThan(desktopEstimate);
  });

  it('marks only the current turn assistant row as live while the current turn is still running', async () => {
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
        body: 'Question',
        meta: 'now',
      },
      {
        id: 'assistant-1',
        order: 2,
        kind: 'assistant',
        tone: 'info',
        label: 'Assistant',
        title: '',
        body: 'Settled answer',
        meta: 'now',
        sourceTurnId: 'turn-1',
      },
      {
        id: 'assistant-2',
        order: 3,
        kind: 'assistant',
        tone: 'info',
        label: 'Assistant',
        title: '',
        body: 'Partial answer',
        meta: 'now',
        sourceTurnId: 'turn-2',
      },
    ] as any;

    const marked = withLiveAssistantState(snapshot, entries);
    expect(marked[0]?.live).toBeUndefined();
    expect(marked[1]?.live).toBeUndefined();
    expect(marked[2]?.live).toBe(true);
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

  it('adds a single trailing busy bubble only while the turn is actively running', async () => {
    const { withTrailingBusyIndicator } = await import('./index');

    const baseEntries = [
      {
        id: 'assistant-1',
        order: 1,
        kind: 'assistant',
        tone: 'info',
        label: 'Assistant',
        title: '',
        body: 'Partial answer',
        meta: 'now',
        live: true,
      },
    ] as any;

    const running = withTrailingBusyIndicator(
      {
        currentTurn: { turnId: 'turn-1', state: 'running' },
        session: { state: 'running' },
        streams: { assistantText: 'Partial answer' },
      } as any,
      baseEntries,
      [],
    );

    expect(running).toHaveLength(2);
    expect(running[1]?.busyIndicator).toBe(true);
    expect(running[1]?.body).toBe('Working');
    expect(running[1]?.busyElapsedText).toBe('0s');

    const settled = withTrailingBusyIndicator(
      {
        currentTurn: { turnId: 'turn-1', state: 'completed' },
        session: { state: 'ready' },
        streams: { assistantText: 'Final answer' },
      } as any,
      running as any,
      [],
    );

    expect(settled).toHaveLength(1);
    expect(settled.some((entry: any) => entry.busyIndicator)).toBe(false);
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

  it('formats turn durations using compact wall-clock units', async () => {
    const { formatAppServerControlTurnDuration } = await import('./index');

    expect(formatAppServerControlTurnDuration(42_000)).toBe('42s');
    expect(formatAppServerControlTurnDuration(65_000)).toBe('1m 5s');
    expect(formatAppServerControlTurnDuration(3_661_000)).toBe('1h 1m 1s');
  });

  it('uses the latest provider in-progress item detail as the busy bubble label before falling back to Working', async () => {
    const { withTrailingBusyIndicator } = await import('./index');

    const running = withTrailingBusyIndicator(
      {
        currentTurn: { turnId: 'turn-2', state: 'running' },
        session: { state: 'running' },
        streams: { assistantText: '' },
        items: [
          {
            itemId: 'user-1',
            turnId: 'turn-2',
            itemType: 'user_message',
            status: 'in_progress',
            title: 'You',
            detail: 'Explain why the busy indicator is wrong.',
            attachments: [],
            updatedAt: '2026-04-04T20:00:11Z',
          },
          {
            itemId: 'reasoning-1',
            turnId: 'turn-2',
            itemType: 'reasoning',
            status: 'in_progress',
            title: 'Reasoning',
            detail: 'Scanning repository layout',
            attachments: [],
            updatedAt: '2026-04-04T20:00:10Z',
          },
        ],
      } as any,
      [],
      [],
    );

    expect(running[0]?.busyIndicator).toBe(true);
    expect(running[0]?.body).toBe('Scanning repository layout');

    const fallback = withTrailingBusyIndicator(
      {
        currentTurn: { turnId: 'turn-2', state: 'running' },
        session: { state: 'running' },
        streams: { assistantText: '' },
        items: [
          {
            itemId: 'reasoning-1',
            turnId: 'turn-2',
            itemType: 'reasoning',
            status: 'in_progress',
            title: 'Reasoning',
            detail: '',
            attachments: [],
            updatedAt: '2026-04-04T20:00:10Z',
          },
        ],
      } as any,
      [],
      [],
    );

    expect(fallback[0]?.body).toBe('Working');
  });

  it('does not let assistant text, user prompts, or command text populate the busy bubble', async () => {
    const { withTrailingBusyIndicator } = await import('./index');

    const running = withTrailingBusyIndicator(
      {
        currentTurn: { turnId: 'turn-3', state: 'running' },
        session: { state: 'running' },
        streams: { assistantText: 'Summarize the latest release changes for me.' },
        history: [
          {
            entryId: 'user:turn-3',
            order: 1,
            kind: 'user',
            turnId: 'turn-3',
            itemId: 'user-3',
            requestId: null,
            status: 'completed',
            itemType: 'user_message',
            title: null,
            body: 'Summarize the latest release changes for me.',
            attachments: [],
            streaming: false,
            createdAt: '2026-04-13T11:20:00Z',
            updatedAt: '2026-04-13T11:20:00Z',
          },
        ],
        items: [
          {
            itemId: 'command-3',
            turnId: 'turn-3',
            itemType: 'command_execution',
            status: 'in_progress',
            title: 'git status --short',
            detail: 'git status --short',
            attachments: [],
            updatedAt: '2026-04-13T11:20:02Z',
          },
          {
            itemId: 'assistant-3',
            turnId: 'turn-3',
            itemType: 'assistant_text',
            status: 'in_progress',
            title: 'Assistant',
            detail: 'Summarize the latest release changes for me.',
            attachments: [],
            updatedAt: '2026-04-13T11:20:01Z',
          },
        ],
      } as any,
      [],
      [],
    );

    expect(running[0]?.busyIndicator).toBe(true);
    expect(running[0]?.body).toBe('Working');
  });

  it('falls back to Working when a generic in-progress tool detail looks like a shell command', async () => {
    const { withTrailingBusyIndicator } = await import('./index');

    const running = withTrailingBusyIndicator(
      {
        currentTurn: { turnId: 'turn-4', state: 'running' },
        session: { state: 'running' },
        streams: { assistantText: '' },
        items: [
          {
            itemId: 'tool-4',
            turnId: 'turn-4',
            itemType: 'tool',
            status: 'in_progress',
            title: 'Tool',
            detail: 'npm run build -- --watch',
            attachments: [],
            updatedAt: '2026-04-13T11:21:00Z',
          },
        ],
      } as any,
      [],
      [],
    );

    expect(running[0]?.busyIndicator).toBe(true);
    expect(running[0]?.body).toBe('Working');
  });

  it('does not persist a phase-offset field on the busy entry so the DOM can phase-lock to wallclock at render time', async () => {
    const { withTrailingBusyIndicator } = await import('./index');
    const dateNowSpy = vi
      .spyOn(Date, 'now')
      .mockReturnValue(Date.parse('2026-04-04T20:00:13.700Z'));

    try {
      const running = withTrailingBusyIndicator(
        {
          currentTurn: {
            turnId: 'turn-2',
            state: 'running',
            startedAt: '2026-04-04T20:00:10.000Z',
          },
          session: { state: 'running' },
          streams: { assistantText: '' },
          items: [],
        } as any,
        [],
        [],
      );

      expect(running[0]?.busyIndicator).toBe(true);
      expect((running[0] as any)?.busyAnimationOffsetMs).toBeUndefined();
    } finally {
      dateNowSpy.mockRestore();
    }
  });

  it('collapses long tool-style history bodies by default while keeping them monospace', async () => {
    const { resolveHistoryBodyPresentation } = await import('./index');

    const presentation = resolveHistoryBodyPresentation({
      id: 'tool-1',
      order: 1,
      kind: 'tool',
      tone: 'info',
      label: 'Tool',
      title: 'git diff --stat',
      body: Array.from({ length: 10 }, (_, index) => `line ${index + 1}: tool output`).join('\n'),
      meta: 'Completed • 11:00:01',
    });

    expect(presentation.mode).toBe('monospace');
    expect(presentation.collapsedByDefault).toBe(true);
    expect(presentation.lineCount).toBe(10);
    expect(presentation.preview).toBe('line 1: tool output');
  });

  it('keeps live assistant rows on markdown presentation while they stream', async () => {
    const { resolveHistoryBodyPresentation } = await import('./index');

    const presentation = resolveHistoryBodyPresentation({
      id: 'assistant-live-1',
      order: 1,
      kind: 'assistant',
      tone: 'info',
      label: 'Assistant',
      title: '',
      body: '# Streaming\n\n- item',
      meta: 'now',
      live: true,
    });

    expect(presentation.mode).toBe('markdown');
    expect(presentation.collapsedByDefault).toBe(false);
    expect(presentation.lineCount).toBe(3);
    expect(presentation.preview).toBe('');
  });

  it('renders command-execution rows with a dedicated command presentation', async () => {
    const { resolveHistoryBodyPresentation } = await import('./index');

    const presentation = resolveHistoryBodyPresentation({
      id: 'tool-command-1',
      order: 1,
      kind: 'tool',
      tone: 'positive',
      label: 'Tool',
      title: 'Tool completed',
      body: 'pwsh -Command Get-Location',
      meta: '20:00:00',
      sourceItemType: 'command_execution',
      commandText: 'pwsh -Command Get-Location',
      commandOutputTail: ['Q:\\repos\\MidTerm'],
    });

    expect(presentation.mode).toBe('command');
    expect(presentation.collapsedByDefault).toBe(false);
    expect(presentation.lineCount).toBe(2);
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

  it('allows hiding command output tails with a zero line limit', async () => {
    const { createAgentHistoryDom, resolveToolCallOutputLineLimit } = await import('./index');
    currentSettings = { showUnknownAgentMessages: true, toolCallOutputLines: 0 };
    const historyDom = createAgentHistoryDom({
      getState: () => undefined,
      refreshAppServerControlSnapshot: vi.fn(),
      renderCurrentAgentView: vi.fn(),
      retryAppServerControlActivation: vi.fn(),
      logWarn: vi.fn(),
    });

    const article = historyDom.createHistoryEntry(
      {
        id: 'tool-command-hidden',
        order: 1,
        kind: 'tool',
        tone: 'positive',
        label: 'Tool',
        title: 'Tool completed',
        body: 'pwsh -Command Get-Content out.log',
        meta: '20:00:00',
        sourceItemType: 'command_execution',
        commandText: 'pwsh -Command Get-Content out.log',
        commandOutputTail: ['line 1', 'line 2'],
      },
      's1',
    ) as any;

    const commandBody = article.children.find((child: any) =>
      String(child.className).includes('agent-history-command-body'),
    );
    const output = commandBody.children.find((child: any) =>
      String(child.className).includes('agent-history-command-output-tail'),
    );

    expect(resolveToolCallOutputLineLimit()).toBe(0);
    expect(output).toBeUndefined();
  });

  it('tokenizes command text into command, parameter, string, operator, and text parts', async () => {
    const { tokenizeCommandText } = await import('./index');

    expect(tokenizeCommandText('pwsh -Command "git status" > out.txt')).toEqual([
      { text: 'pwsh', kind: 'command' },
      { text: ' ', kind: 'whitespace' },
      { text: '-Command', kind: 'parameter' },
      { text: ' ', kind: 'whitespace' },
      { text: '"git status"', kind: 'string' },
      { text: ' ', kind: 'whitespace' },
      { text: '>', kind: 'operator' },
      { text: ' ', kind: 'whitespace' },
      { text: 'out.txt', kind: 'text' },
    ]);
  });

  it('suppresses Codex context and rate-limit notices from history and derives compact runtime stats', async () => {
    const { buildAppServerControlHistoryEntries, buildAppServerControlRuntimeStats } =
      await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-04-04T20:00:00Z',
      latestSequence: 8,
      historyCount: 4,
      historyWindowStart: 0,
      historyWindowEnd: 4,
      hasOlderHistory: false,
      hasNewerHistory: false,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: null,
        lastError: null,
        lastEventAt: '2026-04-04T20:00:00Z',
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
        startedAt: '2026-04-04T19:59:50Z',
        completedAt: '2026-04-04T20:00:00Z',
      },
      quickSettings: {
        model: null,
        effort: null,
        planMode: null,
        permissionMode: null,
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
          entryId: 'system-context',
          order: 1,
          kind: 'system',
          status: 'completed',
          itemType: null,
          title: 'Codex context window updated.',
          body: 'Used 12838 tokens, window 258400, last turn in/out 12630/208',
          attachments: [],
          streaming: false,
          createdAt: '2026-04-04T19:59:58Z',
          updatedAt: '2026-04-04T19:59:58Z',
        },
        {
          entryId: 'system-context-body-only',
          order: 2,
          kind: 'system',
          status: 'completed',
          itemType: null,
          title: '',
          body: 'Codex context window updated.',
          attachments: [],
          streaming: false,
          createdAt: '2026-04-04T19:59:58Z',
          updatedAt: '2026-04-04T19:59:58Z',
        },
        {
          entryId: 'system-rate',
          order: 3,
          kind: 'system',
          status: 'completed',
          itemType: null,
          title: 'Codex rate limits updated.',
          body: '{"rateLimits":{"primary":{"usedPercent":2},"secondary":{"usedPercent":38}}}',
          attachments: [],
          streaming: false,
          createdAt: '2026-04-04T19:59:59Z',
          updatedAt: '2026-04-04T19:59:59Z',
        },
        {
          entryId: 'assistant-1',
          order: 4,
          kind: 'assistant',
          status: 'completed',
          itemType: 'assistant_message',
          title: null,
          body: 'Repo is up to date.',
          attachments: [],
          streaming: false,
          createdAt: '2026-04-04T20:00:00Z',
          updatedAt: '2026-04-04T20:00:00Z',
        },
      ],
      items: [],
      requests: [],
      notices: [
        {
          eventId: 'notice-context-1',
          type: 'codex/contextWindowUpdated',
          message: 'Codex context window updated.',
          detail: 'Used 12838 tokens, window 258400, last turn in/out 12630/208',
          createdAt: '2026-04-04T19:59:58Z',
        },
        {
          eventId: 'notice-context-2',
          type: 'codex/contextWindowUpdated',
          message: 'Codex context window updated.',
          detail: 'Used 27446 tokens, window 258400, last turn in/out 14511/97',
          createdAt: '2026-04-04T20:00:01Z',
        },
        {
          eventId: 'notice-rate-1',
          type: 'account/rateLimits/updated',
          message: 'Codex rate limits updated.',
          detail: '{"rateLimits":{"primary":{"usedPercent":2},"secondary":{"usedPercent":38}}}',
          createdAt: '2026-04-04T20:00:01Z',
        },
      ],
    } as any;

    const history = buildAppServerControlHistoryEntries(snapshot);
    const stats = buildAppServerControlRuntimeStats(snapshot);

    expect(history).toHaveLength(1);
    expect(history[0]?.kind).toBe('assistant');
    expect(stats).toEqual({
      windowUsedTokens: 27446,
      windowTokenLimit: 258400,
      accumulatedInputTokens: 27141,
      accumulatedOutputTokens: 305,
      primaryRateLimitUsedPercent: 2,
      secondaryRateLimitUsedPercent: 38,
    });
  });

  it('renders runtime stats as percent of context limit plus session in/out totals', async () => {
    getAppServerControlHistoryWindow.mockResolvedValue(
      createSnapshot({
        notices: [
          {
            eventId: 'notice-context-1',
            type: 'codex/contextWindowUpdated',
            message: 'Codex context window updated.',
            detail: 'Used 12838 tokens, window 258400, last turn in/out 12630/208',
            createdAt: '2026-04-04T19:59:58Z',
          },
        ],
      }),
    );
    getAppServerControlEvents.mockResolvedValue({
      sessionId: 's1',
      latestSequence: 1,
      events: [],
    });

    const { initAgentView } = await import('./index');
    initAgentView();

    const activate = onTabActivated.mock.calls[0]?.[1] as
      | ((sessionId: string, panel: HTMLDivElement) => void)
      | undefined;
    expect(activate).toBeTypeOf('function');

    const panel = createPanel();
    activate?.('s1', panel);

    await vi.waitFor(() => {
      const host = panel.querySelector('[data-agent-field="runtime-stats"]') as any;
      expect(host.hidden).toBe(false);
      expect(host.childNodes[0]?.textContent).toBe('5% of 258k  in 12.6k  out 208');
    });

    const host = panel.querySelector('[data-agent-field="runtime-stats"]') as any;
    expect(host.title).toBe('Context 5% of 258k (12.8k used)\nSession in 12.6k\nSession out 208');
    expect(host.setAttribute).toHaveBeenCalledWith(
      'aria-label',
      'Context 5% of 258k (12.8k used) | Session in 12.6k | Session out 208',
    );
  });

  it('falls back to a plain window-limit summary when the provider notice reports cumulative totals instead of current context occupancy', async () => {
    getAppServerControlHistoryWindow.mockResolvedValue(
      createSnapshot({
        notices: [
          {
            eventId: 'notice-context-total-1',
            type: 'thread.token-usage.updated',
            message: 'Codex context window updated.',
            detail: 'Session total 2056000 tokens, window 258400, last turn in/out 3645000/10900',
            createdAt: '2026-04-08T12:05:57Z',
          },
        ],
      }),
    );
    getAppServerControlEvents.mockResolvedValue({
      sessionId: 's1',
      latestSequence: 1,
      events: [],
    });

    const { initAgentView } = await import('./index');
    initAgentView();

    const activate = onTabActivated.mock.calls[0]?.[1] as
      | ((sessionId: string, panel: HTMLDivElement) => void)
      | undefined;
    expect(activate).toBeTypeOf('function');

    const panel = createPanel();
    activate?.('s1', panel);

    await vi.waitFor(() => {
      const host = panel.querySelector('[data-agent-field="runtime-stats"]') as any;
      expect(host.hidden).toBe(false);
      expect(host.childNodes[0]?.textContent).toBe('Window 258k  in 3645k  out 10.9k');
    });

    const host = panel.querySelector('[data-agent-field="runtime-stats"]') as any;
    expect(host.title).toBe('Window 258k\nSession in 3645k\nSession out 10.9k');
    expect(host.setAttribute).toHaveBeenCalledWith(
      'aria-label',
      'Window 258k | Session in 3645k | Session out 10.9k',
    );
  });

  it('detects active non-collapsed AppServerControl selections inside the panel', async () => {
    const { hasActiveAppServerControlSelectionInPanel } = await import('./index');

    const selectedTextNode = {} as Node;
    const outsideTextNode = {} as Node;
    const panel = {
      contains: (node: Node | null) => node === selectedTextNode,
    } as ParentNode;

    expect(
      hasActiveAppServerControlSelectionInPanel(panel, {
        rangeCount: 1,
        isCollapsed: false,
        getRangeAt: () =>
          ({
            startContainer: selectedTextNode,
            endContainer: selectedTextNode,
          }) as Range,
      } as Selection),
    ).toBe(true);

    expect(
      hasActiveAppServerControlSelectionInPanel(panel, {
        rangeCount: 1,
        isCollapsed: false,
        getRangeAt: () =>
          ({
            startContainer: outsideTextNode,
            endContainer: outsideTextNode,
          }) as Range,
      } as Selection),
    ).toBe(false);
  });

  it('uses dedicated diff presentation for AppServerControl diff rows', async () => {
    const { resolveHistoryBodyPresentation } = await import('./index');

    const presentation = resolveHistoryBodyPresentation({
      id: 'diff-1',
      order: 1,
      kind: 'diff',
      tone: 'warning',
      label: 'Diff',
      title: 'Working diff',
      body: 'diff --git a/report.md b/report.md\n@@\n-status: TODO\n+status: DONE',
      meta: '02:29:00',
    });

    expect(presentation.mode).toBe('diff');
    expect(presentation.collapsedByDefault).toBe(false);
    expect(presentation.lineCount).toBe(4);
    expect(presentation.preview).toBe('');
  });

  it('keeps long AppServerControl diff rows expanded instead of collapsing them', async () => {
    const { resolveHistoryBodyPresentation } = await import('./index');

    const presentation = resolveHistoryBodyPresentation({
      id: 'diff-2',
      order: 1,
      kind: 'diff',
      tone: 'warning',
      label: 'Diff',
      title: 'Working diff',
      body: Array.from({ length: 240 }, (_, index) => `+line ${index + 1}`).join('\n'),
      meta: '02:29:00',
    });

    expect(presentation.mode).toBe('diff');
    expect(presentation.collapsedByDefault).toBe(false);
    expect(presentation.lineCount).toBe(240);
    expect(presentation.preview).toBe('');
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

  it('derives old and new line numbers from diff hunks', async () => {
    const { buildRenderedDiffLines } = await import('./index');

    const rendered = buildRenderedDiffLines(
      [
        'diff --git a/report.md b/report.md',
        '--- a/report.md',
        '+++ b/report.md',
        '@@ -4,3 +4,4 @@',
        ' line 4',
        '-line 5 old',
        '+line 5 new',
        '+line 6 new',
      ].join('\n'),
    );

    expect(rendered[1]).toEqual({
      text: '@@ -4,3 +4,4 @@',
      className: 'agent-history-diff-line-hunk',
    });
    expect(rendered[2]).toEqual({
      text: ' line 4',
      className: 'agent-history-diff-line-context',
      oldLineNumber: 4,
      newLineNumber: 4,
    });
    expect(rendered[3]).toEqual({
      text: '-line 5 old',
      className: 'agent-history-diff-line-delete',
      oldLineNumber: 5,
    });
    expect(rendered[4]).toEqual({
      text: '+line 5 new',
      className: 'agent-history-diff-line-add',
      newLineNumber: 5,
    });
    expect(rendered[5]).toEqual({
      text: '+line 6 new',
      className: 'agent-history-diff-line-add',
      newLineNumber: 6,
    });
  });

  it('renders diff code lines through one stable old/new gutter shape', async () => {
    const { createAgentHistoryDom } = await import('./historyDom');

    const historyDom = createAgentHistoryDom({
      getState: () => undefined,
      refreshAppServerControlSnapshot: async () => {},
      renderCurrentAgentView: () => {},
      retryAppServerControlActivation: async () => {},
      logWarn: () => {},
    });

    const entry = {
      id: 'diff-structured',
      order: 1,
      kind: 'diff',
      tone: 'warning',
      label: 'Diff',
      title: 'Working diff',
      body: [
        'diff --git a/report.md b/report.md',
        '--- a/report.md',
        '+++ b/report.md',
        '@@ -4,3 +4,4 @@',
        ' line 4',
        '-line 5 old',
        '+line 5 new',
      ].join('\n'),
      meta: '',
    } as const;

    const article = historyDom.createHistoryEntry(entry, 's1') as any;
    const body = article.children.find((child: any) =>
      String(child.className).includes('agent-history-diff-body'),
    );
    const content = body.children.find((child: any) =>
      String(child.className).includes('agent-history-diff-content'),
    );
    const contextRow = content.children[2];
    const deleteRow = content.children[3];
    const addRow = content.children[4];

    expect(contextRow.dataset.hasLineNumbers).toBe('true');
    expect(deleteRow.dataset.hasLineNumbers).toBe('true');
    expect(addRow.dataset.hasLineNumbers).toBe('true');
    expect(contextRow.children[0].className).toContain('agent-history-diff-line-gutter');
    expect(deleteRow.children[0].className).toContain('agent-history-diff-line-gutter');
    expect(addRow.children[0].className).toContain('agent-history-diff-line-gutter');
    expect(deleteRow.children[0].children).toHaveLength(2);
    expect(addRow.children[0].children).toHaveLength(2);
  });

  it('renders live assistant rows through the markdown body instead of raw streaming text', async () => {
    const { createAgentHistoryDom } = await import('./historyDom');

    const historyDom = createAgentHistoryDom({
      getState: () => undefined,
      refreshAppServerControlSnapshot: async () => {},
      renderCurrentAgentView: () => {},
      retryAppServerControlActivation: async () => {},
      logWarn: () => {},
    });

    const article = historyDom.createHistoryEntry(
      {
        id: 'assistant-live-markdown',
        order: 1,
        kind: 'assistant',
        tone: 'info',
        label: 'Assistant',
        title: '',
        body: 'Heading\n\n- streamed bullet',
        meta: '',
        live: true,
      },
      's1',
    );

    const body = article.children.find((child: any) =>
      String(child.className).includes('agent-history-markdown'),
    );
    const content = body?.children.find((child: any) =>
      String(child.className).includes('agent-history-markdown-content'),
    );

    expect(
      article.children.some((child: any) =>
        String(child.className).includes('agent-history-streaming-body'),
      ),
    ).toBe(false);
    expect(content).toBeDefined();
    expect(content.innerHTML).toContain('<ul>');
  });

  it('resolves diff file headers against the session working directory when available', async () => {
    const { buildRenderedDiffLines } = await import('./index');

    const rendered = buildRenderedDiffLines(
      [
        'diff --git a/src/report.md b/src/report.md',
        '--- a/src/report.md',
        '+++ b/src/report.md',
        '@@ -1 +1 @@',
        '-old',
        '+new',
      ].join('\n'),
      'Q:\\repos\\MidTerm',
    );

    expect(rendered[0]).toEqual({
      text: 'Edited Q:\\repos\\MidTerm\\src\\report.md',
      className: 'agent-history-diff-line-file',
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

  it('requests a snapshot refresh when off-window history changes arrive while browsing older history', async () => {
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

    expect(requiresWindowRefresh).toBe(true);
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

    expect(requiresWindowRefresh).toBe(true);
    expect(snapshot.historyWindowStart).toBe(0);
    expect(snapshot.historyWindowEnd).toBe(64);
    expect(snapshot.history[0]?.entryId).toBe('assistant:1');
    expect(snapshot.history).toHaveLength(64);
    expect(snapshot.hasNewerHistory).toBe(true);
  });
  it('shows unknown agent fallback entries by default and hides them when disabled', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's1',
      provider: 'codex',
      generatedAt: '2026-04-08T16:40:00Z',
      latestSequence: 1,
      historyCount: 1,
      estimatedTotalHistoryHeightPx: 84,
      historyWindowStart: 0,
      historyWindowEnd: 1,
      estimatedHistoryBeforeWindowPx: 0,
      estimatedHistoryAfterWindowPx: 0,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: null,
        lastError: null,
        lastEventAt: '2026-04-08T16:40:00Z',
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
        startedAt: '2026-04-08T16:39:00Z',
        completedAt: '2026-04-08T16:40:00Z',
      },
      quickSettings: {
        model: null,
        effort: null,
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
          entryId: 'tool:item-unknown-1',
          order: 1,
          estimatedHeightPx: 84,
          kind: 'tool',
          turnId: 'turn-1',
          itemId: 'item-unknown-1',
          requestId: null,
          status: 'completed',
          itemType: 'unknown_agent_message',
          title: 'Unknown agent message',
          body: 'Method: codex/event/unhandled_notification\n{"msg":{"text":"Unhandled codex event for fallback coverage"}}',
          attachments: [],
          streaming: false,
          createdAt: '2026-04-08T16:40:00Z',
          updatedAt: '2026-04-08T16:40:00Z',
        },
      ],
      items: [],
      requests: [],
      notices: [],
    } as any;

    currentSettings = { showUnknownAgentMessages: true };
    const visibleHistory = buildAppServerControlHistoryEntries(snapshot);
    expect(visibleHistory).toHaveLength(1);
    expect(visibleHistory[0]?.sourceItemType).toBe('unknown_agent_message');

    currentSettings = { showUnknownAgentMessages: false };
    const hiddenHistory = buildAppServerControlHistoryEntries(snapshot);
    expect(hiddenHistory).toHaveLength(0);
  });

  it('labels meaningful agent state and agent error runtime rows distinctly', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's-agent-runtime',
      provider: 'codex',
      generatedAt: '2026-04-09T08:00:00Z',
      latestSequence: 2,
      historyCount: 2,
      estimatedTotalHistoryHeightPx: 120,
      historyWindowStart: 0,
      historyWindowEnd: 2,
      estimatedHistoryBeforeWindowPx: 0,
      estimatedHistoryAfterWindowPx: 0,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: null,
        lastError: null,
        lastEventAt: '2026-04-09T08:00:01Z',
      },
      thread: {
        threadId: 'thread-1',
        state: 'active',
        stateLabel: 'Active',
      },
      currentTurn: {
        turnId: null,
        state: 'idle',
        stateLabel: 'Idle',
        model: null,
        effort: null,
        startedAt: null,
        completedAt: null,
      },
      quickSettings: {
        model: null,
        effort: null,
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
          entryId: 'runtime-agent-state',
          order: 1,
          estimatedHeightPx: 52,
          kind: 'system',
          turnId: null,
          itemId: null,
          requestId: null,
          status: 'info',
          itemType: 'agent_state',
          title: 'Agent state',
          body: 'Agent entered planning mode.',
          attachments: [],
          streaming: false,
          createdAt: '2026-04-09T08:00:00Z',
          updatedAt: '2026-04-09T08:00:00Z',
        },
        {
          entryId: 'runtime-agent-error',
          order: 2,
          estimatedHeightPx: 68,
          kind: 'notice',
          turnId: null,
          itemId: null,
          requestId: null,
          status: 'agent.error',
          itemType: 'agent_error',
          title: 'Agent error',
          body: '[features].collab is deprecated. Use [features].multi_agent instead.',
          attachments: [],
          streaming: false,
          createdAt: '2026-04-09T08:00:01Z',
          updatedAt: '2026-04-09T08:00:01Z',
        },
      ],
      items: [],
      requests: [],
      notices: [],
    } as any;

    const history = buildAppServerControlHistoryEntries(snapshot);

    expect(history).toHaveLength(2);
    expect(history[0]).toMatchObject({
      kind: 'system',
      label: 'Agent State',
      sourceItemType: 'agent_state',
      tone: 'info',
    });
    expect(history[1]).toMatchObject({
      kind: 'notice',
      label: 'Agent Error',
      sourceItemType: 'agent_error',
      tone: 'attention',
    });
  });

  it('suppresses chatty Codex lifecycle agent state rows from canonical history', async () => {
    const { buildAppServerControlHistoryEntries } = await import('./index');

    const snapshot = {
      sessionId: 's-agent-runtime-noise',
      provider: 'codex',
      generatedAt: '2026-04-09T08:00:00Z',
      latestSequence: 5,
      historyCount: 5,
      estimatedTotalHistoryHeightPx: 260,
      historyWindowStart: 0,
      historyWindowEnd: 5,
      estimatedHistoryBeforeWindowPx: 0,
      estimatedHistoryAfterWindowPx: 0,
      session: {
        state: 'ready',
        stateLabel: 'Ready',
        reason: null,
        lastError: null,
        lastEventAt: '2026-04-09T08:00:01Z',
      },
      thread: {
        threadId: 'thread-1',
        state: 'active',
        stateLabel: 'Active',
      },
      currentTurn: {
        turnId: null,
        state: 'idle',
        stateLabel: 'Idle',
        model: null,
        effort: null,
        startedAt: null,
        completedAt: null,
      },
      quickSettings: {
        model: null,
        effort: null,
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
          entryId: 'remote-control',
          order: 1,
          estimatedHeightPx: 52,
          kind: 'system',
          turnId: null,
          itemId: null,
          requestId: null,
          status: 'info',
          itemType: null,
          title: 'System',
          body: 'Codex remote-control status: disabled.',
          attachments: [],
          streaming: false,
          createdAt: '2026-04-09T08:00:00Z',
          updatedAt: '2026-04-09T08:00:00Z',
        },
        {
          entryId: 'mcp-starting',
          order: 2,
          estimatedHeightPx: 52,
          kind: 'system',
          turnId: null,
          itemId: null,
          requestId: null,
          status: 'info',
          itemType: 'agent_state',
          title: 'Agent state',
          body: 'openaiDeveloperDocs starting.',
          attachments: [],
          streaming: false,
          createdAt: '2026-04-09T08:00:00Z',
          updatedAt: '2026-04-09T08:00:00Z',
        },
        {
          entryId: 'mcp-ready',
          order: 3,
          estimatedHeightPx: 52,
          kind: 'system',
          turnId: null,
          itemId: null,
          requestId: null,
          status: 'info',
          itemType: 'agent_state',
          title: 'Agent state',
          body: 'codex_apps ready.',
          attachments: [],
          streaming: false,
          createdAt: '2026-04-09T08:00:00Z',
          updatedAt: '2026-04-09T08:00:00Z',
        },
        {
          entryId: 'skills',
          order: 4,
          estimatedHeightPx: 52,
          kind: 'system',
          turnId: null,
          itemId: null,
          requestId: null,
          status: 'info',
          itemType: null,
          title: 'System',
          body: 'Codex skills changed.\n\n{}',
          attachments: [],
          streaming: false,
          createdAt: '2026-04-09T08:00:00Z',
          updatedAt: '2026-04-09T08:00:00Z',
        },
        {
          entryId: 'assistant',
          order: 5,
          estimatedHeightPx: 52,
          kind: 'assistant',
          turnId: 'turn-1',
          itemId: null,
          requestId: null,
          status: 'completed',
          itemType: null,
          title: '',
          body: 'Ja, ich bin da.',
          attachments: [],
          streaming: false,
          createdAt: '2026-04-09T08:00:01Z',
          updatedAt: '2026-04-09T08:00:01Z',
        },
      ],
      items: [],
      requests: [],
      notices: [],
    } as any;

    const history = buildAppServerControlHistoryEntries(snapshot);

    expect(history).toHaveLength(1);
    expect(history[0]).toMatchObject({
      kind: 'assistant',
      body: 'Ja, ich bin da.',
    });
  });
});
