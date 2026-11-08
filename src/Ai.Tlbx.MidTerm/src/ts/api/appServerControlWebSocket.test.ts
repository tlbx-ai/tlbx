import { afterEach, describe, expect, it, vi } from 'vitest';

vi.mock('../utils', () => ({
  createWsUrl: () => 'ws://localhost/ws/app-server-control',
  ReconnectController: class {
    cancel(): void {}
    reset(): void {}
    schedule(): void {}
  },
}));

class FakeWebSocket {
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSING = 2;
  static readonly CLOSED = 3;
  static instances: FakeWebSocket[] = [];
  static autoOpen = true;

  readonly url: string;
  readyState = FakeWebSocket.CONNECTING;
  sent: string[] = [];
  onopen: ((event: Event) => void) | null = null;
  onmessage: ((event: MessageEvent) => void) | null = null;
  onerror: ((event: Event) => void) | null = null;
  onclose: ((event: CloseEvent) => void) | null = null;

  constructor(url: string) {
    this.url = url;
    FakeWebSocket.instances.push(this);
    if (FakeWebSocket.autoOpen) {
      queueMicrotask(() => this.open());
    }
  }

  open(): void {
    this.readyState = FakeWebSocket.OPEN;
    this.onopen?.(new Event('open'));
  }

  send(data: string): void {
    this.sent.push(data);
  }

  close(): void {
    this.readyState = FakeWebSocket.CLOSED;
    this.onclose?.(new CloseEvent('close'));
  }
}

describe('appServerControlWebSocket', () => {
  afterEach(() => {
    FakeWebSocket.instances = [];
    FakeWebSocket.autoOpen = true;
    vi.unstubAllGlobals();
    vi.useRealTimers();
    vi.resetModules();
  });

  it('does not duplicate subscribe messages or resubscribe for an unchanged history window', async () => {
    vi.stubGlobal('WebSocket', FakeWebSocket as unknown as typeof WebSocket);

    const { openAppServerControlHistorySocket, updateAppServerControlHistorySocketWindow } =
      await import('./appServerControlWebSocket');

    const disconnect = openAppServerControlHistorySocket(
      'session-1',
      5,
      0,
      80,
      'rev-1',
      {
        onPatch: vi.fn(),
      },
      960,
    );

    await vi.waitFor(() => {
      expect(FakeWebSocket.instances).toHaveLength(1);
      expect(FakeWebSocket.instances[0]?.sent).toHaveLength(1);
    });

    const socket = FakeWebSocket.instances[0]!;
    const initialSubscribeCount = socket.sent.length;

    updateAppServerControlHistorySocketWindow('session-1', 0, 80, 'rev-1', 960);
    await Promise.resolve();

    expect(socket.sent).toHaveLength(initialSubscribeCount);

    updateAppServerControlHistorySocketWindow('session-1', 10, 80, 'rev-2', 960);
    await vi.waitFor(() => {
      expect(socket.sent).toHaveLength(initialSubscribeCount + 1);
    });

    disconnect();
  });

  it('reports an open connection to a listener subscribing after the socket opened', async () => {
    vi.stubGlobal('WebSocket', FakeWebSocket as unknown as typeof WebSocket);
    const { openAppServerControlHistorySocket } = await import('./appServerControlWebSocket');
    const firstOpen = vi.fn();
    const first = openAppServerControlHistorySocket('session-1', 0, 0, 80, 'rev-1', {
      onPatch: vi.fn(),
      onOpen: firstOpen,
    });
    await vi.waitFor(() => expect(firstOpen).toHaveBeenCalledTimes(1));

    const secondOpen = vi.fn();
    const second = openAppServerControlHistorySocket('session-2', 0, 0, 80, 'rev-2', {
      onPatch: vi.fn(),
      onOpen: secondOpen,
    });
    await vi.waitFor(() => expect(secondOpen).toHaveBeenCalledTimes(1));
    expect(firstOpen).toHaveBeenCalledTimes(1);
    expect(FakeWebSocket.instances).toHaveLength(1);
    expect(
      FakeWebSocket.instances[0]!.sent.map((message) => JSON.parse(message).sessionId),
    ).toEqual(['session-1', 'session-2']);
    second();
    first();
  });

  it('does not notify a listener removed before an existing connection is subscribed', async () => {
    vi.stubGlobal('WebSocket', FakeWebSocket as unknown as typeof WebSocket);
    const { openAppServerControlHistorySocket } = await import('./appServerControlWebSocket');
    const firstOpen = vi.fn();
    const first = openAppServerControlHistorySocket('session-1', 0, 0, 80, 'rev-1', {
      onPatch: vi.fn(),
      onOpen: firstOpen,
    });
    await vi.waitFor(() => expect(firstOpen).toHaveBeenCalledTimes(1));
    const onOpen = vi.fn();
    const disconnect = openAppServerControlHistorySocket('session-2', 0, 0, 80, 'rev-2', {
      onPatch: vi.fn(),
      onOpen,
    });
    disconnect();
    await Promise.resolve();
    expect(onOpen).not.toHaveBeenCalled();
    expect(
      FakeWebSocket.instances[0]!.sent.filter(
        (message) => JSON.parse(message).type === 'subscribe',
      ),
    ).toHaveLength(1);
    first();
  });

  it('ignores subscription history windows that do not match the current browser revision', async () => {
    vi.stubGlobal('WebSocket', FakeWebSocket as unknown as typeof WebSocket);

    const { openAppServerControlHistorySocket } = await import('./appServerControlWebSocket');
    const onHistoryWindow = vi.fn();

    openAppServerControlHistorySocket(
      'session-1',
      0,
      10,
      40,
      'rev-current',
      {
        onPatch: vi.fn(),
        onHistoryWindow,
      },
      960,
    );

    await vi.waitFor(() => {
      expect(FakeWebSocket.instances).toHaveLength(1);
    });

    const socket = FakeWebSocket.instances[0]!;
    socket.onmessage?.(
      new MessageEvent('message', {
        data: JSON.stringify({
          type: 'history.window',
          sessionId: 'session-1',
          windowRevision: 'rev-stale',
          historyWindow: {
            sessionId: 'session-1',
            provider: 'codex',
            generatedAt: '2026-04-13T10:00:00Z',
            latestSequence: 7,
            historyCount: 20,
            historyWindowStart: 0,
            historyWindowEnd: 5,
            hasOlderHistory: false,
            hasNewerHistory: true,
            session: { state: 'ready', stateLabel: 'Ready' },
            thread: { threadId: 'thread-1', state: 'active', stateLabel: 'Active' },
            currentTurn: { state: 'running', stateLabel: 'Running' },
            quickSettings: { planMode: 'off', permissionMode: 'manual' },
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
          },
        }),
      }),
    );
    socket.onmessage?.(
      new MessageEvent('message', {
        data: JSON.stringify({
          type: 'history.window',
          sessionId: 'session-1',
          windowRevision: 'rev-current',
          historyWindow: {
            sessionId: 'session-1',
            provider: 'codex',
            generatedAt: '2026-04-13T10:00:01Z',
            latestSequence: 8,
            historyCount: 20,
            historyWindowStart: 5,
            historyWindowEnd: 10,
            hasOlderHistory: true,
            hasNewerHistory: true,
            session: { state: 'ready', stateLabel: 'Ready' },
            thread: { threadId: 'thread-1', state: 'active', stateLabel: 'Active' },
            currentTurn: { state: 'running', stateLabel: 'Running' },
            quickSettings: { planMode: 'off', permissionMode: 'manual' },
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
          },
        }),
      }),
    );

    expect(onHistoryWindow).toHaveBeenCalledTimes(1);
    expect(onHistoryWindow.mock.calls[0]?.[0]?.historyWindowStart).toBe(5);
    expect(onHistoryWindow.mock.calls[0]?.[0]?.windowRevision).toBe('rev-current');
  });

  it('rejects a connection attempt that closes before opening and permits a clean retry', async () => {
    FakeWebSocket.autoOpen = false;
    vi.stubGlobal('WebSocket', FakeWebSocket as unknown as typeof WebSocket);

    const { attachAppServerControlSession } = await import('./appServerControlWebSocket');
    const firstAttempt = attachAppServerControlSession('session-1');
    const firstSocket = FakeWebSocket.instances[0]!;
    firstSocket.close();

    await expect(firstAttempt).rejects.toThrow('disconnected');

    const retry = attachAppServerControlSession('session-1');
    expect(FakeWebSocket.instances).toHaveLength(2);
    const retrySocket = FakeWebSocket.instances[1]!;
    retrySocket.open();
    await vi.waitFor(() => expect(retrySocket.sent).toHaveLength(1));
    const request = JSON.parse(retrySocket.sent[0] ?? '{}') as { id?: string };
    retrySocket.onmessage?.(
      new MessageEvent('message', {
        data: JSON.stringify({
          type: 'ack',
          id: request.id,
          action: 'attach',
          sessionId: 'session-1',
        }),
      }),
    );

    await expect(retry).resolves.toBeUndefined();
  });

  it('bounds unanswered requests and clears their timeout after rejection', async () => {
    vi.useFakeTimers();
    vi.stubGlobal('WebSocket', FakeWebSocket as unknown as typeof WebSocket);

    const { getAppServerControlHistoryWindowWs } = await import('./appServerControlWebSocket');
    const request = getAppServerControlHistoryWindowWs('session-1', 0, 40);
    const rejection = expect(request).rejects.toThrow('timed out');
    await Promise.resolve();
    await Promise.resolve();

    await vi.advanceTimersByTimeAsync(5000);

    await rejection;
    expect(vi.getTimerCount()).toBe(0);
  });

  it('replaces a stale-open transport exactly once during foreground recovery', async () => {
    vi.stubGlobal('WebSocket', FakeWebSocket as unknown as typeof WebSocket);

    const { openAppServerControlHistorySocket, recoverAppServerControlWebSocket } =
      await import('./appServerControlWebSocket');
    const onOpen = vi.fn();
    const disconnect = openAppServerControlHistorySocket(
      'session-1',
      9,
      0,
      40,
      'rev-9',
      { onPatch: vi.fn(), onOpen },
      390,
    );
    await vi.waitFor(() => expect(onOpen).toHaveBeenCalledTimes(1));

    await recoverAppServerControlWebSocket();

    expect(FakeWebSocket.instances).toHaveLength(2);
    expect(FakeWebSocket.instances[0]?.readyState).toBe(FakeWebSocket.CLOSED);
    expect(onOpen).toHaveBeenCalledTimes(2);
    expect(FakeWebSocket.instances[1]?.sent).toHaveLength(1);
    disconnect();
  });

  it('stops background patches and reconnects existing subscriptions on resume', async () => {
    vi.stubGlobal('WebSocket', FakeWebSocket as unknown as typeof WebSocket);

    const {
      openAppServerControlHistorySocket,
      recoverAppServerControlWebSocket,
      suspendAppServerControlWebSocketForBrowserBackground,
    } = await import('./appServerControlWebSocket');
    const onOpen = vi.fn();
    const disconnect = openAppServerControlHistorySocket(
      'session-1',
      9,
      0,
      40,
      'rev-9',
      { onPatch: vi.fn(), onOpen },
      390,
    );
    await vi.waitFor(() => expect(onOpen).toHaveBeenCalledTimes(1));

    suspendAppServerControlWebSocketForBrowserBackground();
    expect(FakeWebSocket.instances[0]?.readyState).toBe(FakeWebSocket.CLOSED);

    await recoverAppServerControlWebSocket();
    expect(FakeWebSocket.instances).toHaveLength(2);
    expect(onOpen).toHaveBeenCalledTimes(2);
    disconnect();
  });

  it('recovers a cold background-suspended activation with attach/read/subscribe only', async () => {
    vi.stubGlobal('WebSocket', FakeWebSocket as unknown as typeof WebSocket);
    const api = await import('./appServerControlWebSocket');
    const { resumeAppServerControlActivationAfterTransport } =
      await import('../modules/agentView/activationHelpers');
    api.suspendAppServerControlWebSocketForBrowserBackground();
    let initialError: unknown;
    const oldActivation = api.attachAppServerControlSession('session-1').catch((error: unknown) => {
      initialError = error;
    });
    await oldActivation;
    expect(String(initialError)).toContain('suspended in background');
    expect(FakeWebSocket.instances).toHaveLength(0);

    const ready = vi.fn();
    let disconnect = () => {};
    const restored = {
      sessionId: 'session-1',
      latestSequence: 12,
      historyCount: 1,
      history: [{ entryId: 'old-message', order: 1 }],
      thread: { threadId: 'same-native-thread' },
    };
    let history: unknown;
    const recovered = resumeAppServerControlActivationAfterTransport(
      api.recoverAppServerControlWebSocket,
      oldActivation,
      () => true,
      async () => {
        await api.attachAppServerControlSession('session-1');
        history = await api.getAppServerControlHistoryWindowWs('session-1', undefined, 80);
        disconnect = api.openAppServerControlHistorySocket(
          'session-1',
          12,
          undefined,
          80,
          undefined,
          { onPatch: vi.fn(), onOpen: ready },
        );
      },
    );
    await vi.waitFor(() => expect(FakeWebSocket.instances[0]?.sent).toHaveLength(1));
    const socket = FakeWebSocket.instances[0]!;
    const attach = JSON.parse(socket.sent[0]!);
    expect(attach.action).toBe('attach');
    socket.onmessage?.({
      data: JSON.stringify({
        type: 'ack',
        id: attach.id,
        action: 'attach',
        sessionId: 'session-1',
      }),
    } as MessageEvent);
    await vi.waitFor(() => expect(socket.sent).toHaveLength(2));
    const read = JSON.parse(socket.sent[1]!);
    expect(read.action).toBe('history.window.get');
    socket.onmessage?.({
      data: JSON.stringify({
        type: 'history.window',
        id: read.id,
        sessionId: 'session-1',
        historyWindow: restored,
      }),
    } as MessageEvent);
    await recovered;
    await vi.waitFor(() => expect(ready).toHaveBeenCalledOnce());
    expect(history).toMatchObject(restored);
    expect(
      socket.sent.map((message) => {
        const value = JSON.parse(message);
        return value.action ?? value.type;
      }),
    ).toEqual(['attach', 'history.window.get', 'subscribe']);
    disconnect();
  });
  it('sends a guarded configure patch with null intact and waits for the correlated provider receipt', async () => {
    vi.stubGlobal('WebSocket', FakeWebSocket as unknown as typeof WebSocket);
    const { configureAppServerControlRuntimeWs } = await import('./appServerControlWebSocket');
    const request = {
      expectedThreadId: 'thread-A',
      expectedTurnId: 'turn-A',
      settings: { model: 'B', serviceTier: null },
    };
    const resolve = vi.fn();
    const pending = configureAppServerControlRuntimeWs('session-1', request).then(resolve);
    await vi.waitFor(() => expect(FakeWebSocket.instances[0]?.sent.length).toBe(1));
    const socket = FakeWebSocket.instances[0]!;
    const sent = JSON.parse(socket.sent[0]!);
    expect(sent).toMatchObject({
      type: 'request',
      action: 'runtime.configure',
      sessionId: 'session-1',
      configure: request,
    });
    expect(sent).not.toHaveProperty('turn');
    expect(resolve).not.toHaveBeenCalled();
    const receipt = {
      sessionId: 'session-1',
      threadId: 'thread-A',
      turnId: 'turn-A',
      status: 'partial',
      threadStatus: 'applied',
      turnStatus: 'targetUnavailable',
      effectiveSettings: { model: 'provider-B' },
      confirmedSettings: {},
      deferredFields: [],
      unconfirmedFields: [],
      fieldErrors: {},
    };
    socket.onmessage?.({
      data: JSON.stringify({
        type: 'commandAccepted',
        id: sent.id,
        sessionId: 'session-1',
        response: { sessionId: 'session-1', status: 'partial', configuration: receipt },
      }),
    } as MessageEvent);
    await pending;
    expect(resolve).toHaveBeenCalledWith(receipt);
    expect(socket.sent).toHaveLength(1);
  });

  it('preserves targetUnavailable when the host reports its different current thread', async () => {
    vi.stubGlobal('WebSocket', FakeWebSocket as unknown as typeof WebSocket);
    const { configureAppServerControlRuntimeWs } = await import('./appServerControlWebSocket');
    const pending = configureAppServerControlRuntimeWs('session-1', {
      expectedThreadId: 'stale-thread',
      expectedTurnId: 'stale-turn',
      settings: { model: 'B' },
    });
    await vi.waitFor(() => expect(FakeWebSocket.instances[0]?.sent.length).toBe(1));
    const socket = FakeWebSocket.instances[0]!;
    const sent = JSON.parse(socket.sent[0]!);
    const receipt = {
      sessionId: 'session-1',
      threadId: 'actual-thread',
      turnId: 'stale-turn',
      status: 'targetUnavailable',
    };
    socket.onmessage?.({
      data: JSON.stringify({
        type: 'commandAccepted',
        id: sent.id,
        sessionId: 'session-1',
        response: { configuration: receipt },
      }),
    } as MessageEvent);
    expect(await pending).toEqual(receipt);
    expect(socket.sent).toHaveLength(1);
  });

  it('rejects a mismatched configure target without replaying a mutation', async () => {
    vi.stubGlobal('WebSocket', FakeWebSocket as unknown as typeof WebSocket);
    const { configureAppServerControlRuntimeWs } = await import('./appServerControlWebSocket');
    const pending = configureAppServerControlRuntimeWs('session-1', {
      expectedThreadId: 'thread-A',
      expectedTurnId: null,
      settings: { effort: null },
    });
    const rejection = expect(pending).rejects.toThrow('did not match its target');
    await vi.waitFor(() => expect(FakeWebSocket.instances[0]?.sent.length).toBe(1));
    const socket = FakeWebSocket.instances[0]!;
    const sent = JSON.parse(socket.sent[0]!);
    socket.onmessage?.({
      data: JSON.stringify({
        type: 'commandAccepted',
        id: sent.id,
        sessionId: 'session-1',
        response: { configuration: { sessionId: 'session-1', threadId: 'sibling', turnId: null } },
      }),
    } as MessageEvent);
    await rejection;
    expect(socket.sent).toHaveLength(1);
  });
  it.each(['set', 'get', 'clear', 'recover'] as const)(
    'sends native Goal %s with its guard and preserves a pending receipt',
    async (operation) => {
      vi.stubGlobal('WebSocket', FakeWebSocket as unknown as typeof WebSocket);
      const { setAppServerControlGoalWs } = await import('./appServerControlWebSocket');
      const request = {
        operation,
        expectedThreadId: 'thread-A',
        expectedTurnId: 'turn-A',
        objective: 'Keep',
        tokenBudget: null,
      };
      const pending = setAppServerControlGoalWs('session-1', request);
      await vi.waitFor(() => expect(FakeWebSocket.instances[0]?.sent.length).toBe(1));
      const socket = FakeWebSocket.instances[0]!;
      const sent = JSON.parse(socket.sent[0]!);
      expect(sent).toMatchObject({
        action: operation === 'recover' ? 'runtime.recover' : `thread.goal.${operation}`,
        goalSet: request,
      });
      expect(sent).not.toHaveProperty('turn');
      const receipt = {
        sessionId: 'session-1',
        threadId: 'thread-A',
        turnId: 'turn-A',
        status: 'pending',
      };
      socket.onmessage?.({
        data: JSON.stringify({
          type: 'commandAccepted',
          id: sent.id,
          sessionId: 'session-1',
          response: { goal: receipt },
        }),
      } as MessageEvent);
      expect(await pending).toEqual(receipt);
      expect(socket.sent).toHaveLength(1);
    },
  );

  it('does not turn a generic accepted response into native Goal success', async () => {
    vi.stubGlobal('WebSocket', FakeWebSocket as unknown as typeof WebSocket);
    const { setAppServerControlGoalWs } = await import('./appServerControlWebSocket');
    const pending = setAppServerControlGoalWs('session-1', {
      operation: 'get',
      expectedThreadId: 'thread-A',
      expectedTurnId: null,
    });
    const rejection = expect(pending).rejects.toThrow('did not match its target');
    await vi.waitFor(() => expect(FakeWebSocket.instances[0]?.sent.length).toBe(1));
    const socket = FakeWebSocket.instances[0]!;
    const sent = JSON.parse(socket.sent[0]!);
    socket.onmessage?.({
      data: JSON.stringify({
        type: 'commandAccepted',
        id: sent.id,
        sessionId: 'session-1',
        response: { status: 'accepted' },
      }),
    } as MessageEvent);
    await rejection;
    expect(socket.sent).toHaveLength(1);
  });
});
