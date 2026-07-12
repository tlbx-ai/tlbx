import {
  type AppServerControlCommandAcceptedResponse,
  type AppServerControlHistoryDelta,
  type AppServerControlHistoryPatch,
  type AppServerControlHistorySnapshot,
  type AppServerControlGoalSetRequest,
  type AppServerControlHistoryWindowResponse,
  type AppServerControlInterruptRequest,
  type AppServerControlRequestDecisionRequest,
  type AppServerControlTurnRequest,
  type AppServerControlTurnStartResponse,
  type AppServerControlUserInputAnswerRequest,
} from './types';
import { AppServerControlHttpError } from './errors';
import { ReconnectController, createWsUrl } from '../utils';
import { handleAuthenticatedWebSocketClose } from '../modules/auth/sessionLifetime';

type AppServerControlWsRequestAction =
  | 'attach'
  | 'detach'
  | 'history.window.get'
  | 'turn.submit'
  | 'turn.interrupt'
  | 'thread.goal.set'
  | 'request.approve'
  | 'request.decline'
  | 'request.resolve'
  | 'userInput.resolve';

type AppServerControlWsPending =
  | { resolve: () => void; reject: (error: unknown) => void; kind: 'ack' }
  | {
      resolve: (value: AppServerControlHistorySnapshot) => void;
      reject: (error: unknown) => void;
      kind: 'historyWindow';
    }
  | {
      resolve: (value: AppServerControlTurnStartResponse) => void;
      reject: (error: unknown) => void;
      kind: 'turnStarted';
    }
  | {
      resolve: (value: AppServerControlCommandAcceptedResponse) => void;
      reject: (error: unknown) => void;
      kind: 'commandAccepted';
    };

type AppServerControlSubscriptionCallbacks = {
  onPatch(patch: AppServerControlHistoryDelta): void;
  onHistoryWindow?(historyWindow: AppServerControlHistorySnapshot): void;
  onOpen?(): void;
  onError?(error: Event): void;
};

type AppServerControlSessionSubscription = {
  afterSequence: number;
  historyWindow?: {
    startIndex?: number;
    count?: number;
    viewportWidth?: number;
    windowRevision?: string;
  };
  listeners: Set<AppServerControlSubscriptionCallbacks>;
};

type AppServerControlServerMessage =
  | { type: 'ack'; id: string; action: string; sessionId: string }
  | { type: 'error'; id?: string; action?: string; sessionId?: string; message: string }
  | {
      type: 'history.window';
      id?: string;
      sessionId: string;
      windowRevision?: string | null;
      historyWindow: AppServerControlHistoryWindowResponse;
    }
  | { type: 'history.patch'; sessionId: string; patch: AppServerControlHistoryPatch }
  | {
      type: 'turnStarted';
      id: string;
      sessionId: string;
      response: AppServerControlTurnStartResponse;
    }
  | {
      type: 'commandAccepted';
      id: string;
      sessionId: string;
      response: AppServerControlCommandAcceptedResponse;
    };
type PendingAppServerControlServerMessage = Exclude<
  AppServerControlServerMessage,
  { type: 'history.patch' }
>;

const reconnect = new ReconnectController();
const subscriptions = new Map<string, AppServerControlSessionSubscription>();
const pending = new Map<string, AppServerControlWsPending>();
let ws: WebSocket | null = null;
let connectPromise: Promise<void> | null = null;

function createAppServerControlWsError(detail: string): Error {
  return new AppServerControlHttpError(400, detail);
}

function createRequestId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }

  return `appServerControl-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

function buildHistoryWindow(
  startIndex: number | undefined,
  count: number | undefined,
  viewportWidth: number | undefined,
  windowRevision: string | undefined,
): AppServerControlSessionSubscription['historyWindow'] | undefined {
  if (
    startIndex === undefined &&
    count === undefined &&
    viewportWidth === undefined &&
    !windowRevision
  ) {
    return undefined;
  }

  return {
    ...(startIndex === undefined ? {} : { startIndex }),
    ...(count === undefined ? {} : { count }),
    ...(viewportWidth === undefined ? {} : { viewportWidth }),
    ...(windowRevision ? { windowRevision } : {}),
  };
}

function historyWindowsEqual(
  left: AppServerControlSessionSubscription['historyWindow'] | undefined,
  right: AppServerControlSessionSubscription['historyWindow'] | undefined,
): boolean {
  return (
    left?.startIndex === right?.startIndex &&
    left?.count === right?.count &&
    left?.viewportWidth === right?.viewportWidth &&
    left?.windowRevision === right?.windowRevision
  );
}

function normalizeHistoryWindowResponse(
  historyWindow: AppServerControlHistoryWindowResponse,
  windowRevision: string | null | undefined,
): AppServerControlHistorySnapshot {
  return {
    ...historyWindow,
    windowRevision: windowRevision ?? null,
  };
}

function rejectAllPending(error: Error): void {
  for (const request of pending.values()) {
    request.reject(error);
  }

  pending.clear();
}

function dispatchSubscriptionOpen(): void {
  for (const subscription of subscriptions.values()) {
    for (const listener of subscription.listeners) {
      listener.onOpen?.();
    }
  }
}

function dispatchSubscriptionError(error: Event): void {
  for (const subscription of subscriptions.values()) {
    for (const listener of subscription.listeners) {
      listener.onError?.(error);
    }
  }
}

function resubscribeAll(): void {
  for (const [sessionId, subscription] of subscriptions) {
    sendRaw({
      type: 'subscribe',
      sessionId,
      afterSequence: subscription.afterSequence,
      historyWindow: subscription.historyWindow,
    });
  }
}

function resolvePendingRequest<TKind extends AppServerControlWsPending['kind']>(
  id: string,
  kind: TKind,
): Extract<AppServerControlWsPending, { kind: TKind }> | null {
  const request = pending.get(id);
  if (!request || request.kind !== kind) {
    return null;
  }

  pending.delete(id);
  return request as Extract<AppServerControlWsPending, { kind: TKind }>;
}

function handleHistoryWindowMessage(
  message: Extract<AppServerControlServerMessage, { type: 'history.window' }>,
): void {
  if (!message.id) {
    const subscription = subscriptions.get(message.sessionId);
    if (!subscription) {
      return;
    }

    subscription.afterSequence = Math.max(
      subscription.afterSequence,
      message.historyWindow.latestSequence,
    );
    if (
      subscription.historyWindow?.windowRevision &&
      message.windowRevision &&
      subscription.historyWindow.windowRevision !== message.windowRevision
    ) {
      return;
    }
    for (const listener of subscription.listeners) {
      listener.onHistoryWindow?.(
        normalizeHistoryWindowResponse(message.historyWindow, message.windowRevision),
      );
    }
    return;
  }

  resolvePendingRequest(message.id, 'historyWindow')?.resolve(
    normalizeHistoryWindowResponse(message.historyWindow, message.windowRevision),
  );
}

function handleSubscriptionSequenceUpdate(
  sessionId: string,
  nextSequence: number,
  onFound?: (subscription: AppServerControlSessionSubscription) => void,
): void {
  const subscription = subscriptions.get(sessionId);
  if (!subscription) {
    return;
  }

  subscription.afterSequence = Math.max(subscription.afterSequence, nextSequence);
  onFound?.(subscription);
}

function handleErrorMessage(
  message: Extract<AppServerControlServerMessage, { type: 'error' }>,
): void {
  if (!message.id) {
    return;
  }

  const request = pending.get(message.id);
  if (!request) {
    return;
  }

  pending.delete(message.id);
  request.reject(createAppServerControlWsError(message.message));
}

const pendingMessageHandlers: {
  [TType in PendingAppServerControlServerMessage['type']]: (
    message: Extract<PendingAppServerControlServerMessage, { type: TType }>,
  ) => void;
} = {
  ack: (message) => {
    resolvePendingRequest(message.id, 'ack')?.resolve();
  },
  error: handleErrorMessage,
  'history.window': handleHistoryWindowMessage,
  turnStarted: (message) => {
    resolvePendingRequest(message.id, 'turnStarted')?.resolve(message.response);
  },
  commandAccepted: (message) => {
    resolvePendingRequest(message.id, 'commandAccepted')?.resolve(message.response);
  },
};

function isSubscriptionServerMessage(
  message: AppServerControlServerMessage,
): message is Extract<AppServerControlServerMessage, { type: 'history.patch' }> {
  return message.type === 'history.patch';
}

function handlePendingServerMessage(message: PendingAppServerControlServerMessage): void {
  const handler = pendingMessageHandlers[message.type] as (
    pendingMessage: PendingAppServerControlServerMessage,
  ) => void;
  handler(message);
}

function emitSubscriptionPatch(sessionId: string, patch: AppServerControlHistoryDelta): void {
  handleSubscriptionSequenceUpdate(sessionId, patch.latestSequence, (subscription) => {
    for (const listener of subscription.listeners) {
      listener.onPatch(patch);
    }
  });
}

function handleServerMessage(message: AppServerControlServerMessage): void {
  if (!isSubscriptionServerMessage(message)) {
    handlePendingServerMessage(message);
    return;
  }

  emitSubscriptionPatch(message.sessionId, message.patch);
}

function sendRaw(payload: unknown): void {
  if (ws?.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify(payload));
  }
}

async function ensureConnected(): Promise<void> {
  if (ws?.readyState === WebSocket.OPEN) {
    return;
  }

  if (connectPromise) {
    return connectPromise;
  }

  connectPromise = new Promise<void>((resolve) => {
    const socket = new WebSocket(createWsUrl('/ws/app-server-control'));
    ws = socket;

    socket.onopen = () => {
      reconnect.reset();
      connectPromise = null;
      dispatchSubscriptionOpen();
      resubscribeAll();
      resolve();
    };

    socket.onmessage = (event) => {
      handleServerMessage(JSON.parse(event.data as string) as AppServerControlServerMessage);
    };

    socket.onerror = (event) => {
      dispatchSubscriptionError(event);
    };

    socket.onclose = (event) => {
      const shouldReconnect = subscriptions.size > 0;
      ws = null;
      connectPromise = null;
      rejectAllPending(createAppServerControlWsError('AppServerControl WebSocket disconnected.'));
      if (handleAuthenticatedWebSocketClose(event)) {
        return;
      }
      if (shouldReconnect) {
        reconnect.schedule(() => {
          void ensureConnected();
        });
      }
    };
  });

  return connectPromise;
}

async function requestAck(
  action: AppServerControlWsRequestAction,
  sessionId: string,
  extra: Record<string, unknown> = {},
): Promise<void> {
  await ensureConnected();
  const id = createRequestId();
  const request = new Promise<void>((resolve, reject) => {
    pending.set(id, { resolve, reject, kind: 'ack' });
  });
  sendRaw({ type: 'request', id, action, sessionId, ...extra });
  return request;
}

async function requestHistoryWindow(
  sessionId: string,
  startIndex?: number,
  count?: number,
  windowRevision?: string,
  viewportWidth?: number,
): Promise<AppServerControlHistorySnapshot> {
  await ensureConnected();
  const id = createRequestId();
  const request = new Promise<AppServerControlHistorySnapshot>((resolve, reject) => {
    pending.set(id, { resolve, reject, kind: 'historyWindow' });
  });
  sendRaw({
    type: 'request',
    id,
    action: 'history.window.get',
    sessionId,
    historyWindow:
      startIndex === undefined && count === undefined && viewportWidth === undefined
        ? undefined
        : {
            ...(startIndex === undefined ? {} : { startIndex }),
            ...(count === undefined ? {} : { count }),
            ...(viewportWidth === undefined ? {} : { viewportWidth }),
            ...(windowRevision ? { windowRevision } : {}),
          },
  });
  return request;
}

async function requestTurnStarted(
  action: 'turn.submit',
  sessionId: string,
  turn: AppServerControlTurnRequest,
): Promise<AppServerControlTurnStartResponse> {
  await ensureConnected();
  const id = createRequestId();
  const request = new Promise<AppServerControlTurnStartResponse>((resolve, reject) => {
    pending.set(id, { resolve, reject, kind: 'turnStarted' });
  });
  sendRaw({
    type: 'request',
    id,
    action,
    sessionId,
    turn,
  });
  return request;
}

async function requestCommandAccepted(
  action: Exclude<
    AppServerControlWsRequestAction,
    'attach' | 'detach' | 'history.window.get' | 'turn.submit'
  >,
  sessionId: string,
  extra: Record<string, unknown> = {},
): Promise<AppServerControlCommandAcceptedResponse> {
  await ensureConnected();
  const id = createRequestId();
  const request = new Promise<AppServerControlCommandAcceptedResponse>((resolve, reject) => {
    pending.set(id, { resolve, reject, kind: 'commandAccepted' });
  });
  sendRaw({
    type: 'request',
    id,
    action,
    sessionId,
    ...extra,
  });
  return request;
}

export async function attachAppServerControlSession(sessionId: string): Promise<void> {
  return requestAck('attach', sessionId);
}

export async function detachAppServerControlSession(sessionId: string): Promise<void> {
  return requestAck('detach', sessionId);
}

export async function getAppServerControlHistoryWindowWs(
  sessionId: string,
  startIndex?: number,
  count?: number,
  windowRevision?: string,
  viewportWidth?: number,
): Promise<AppServerControlHistorySnapshot> {
  return requestHistoryWindow(sessionId, startIndex, count, windowRevision, viewportWidth);
}

export async function submitAppServerControlTurnWs(
  sessionId: string,
  request: AppServerControlTurnRequest,
): Promise<AppServerControlTurnStartResponse> {
  return requestTurnStarted('turn.submit', sessionId, request);
}

export async function interruptAppServerControlTurnWs(
  sessionId: string,
  request: AppServerControlInterruptRequest,
): Promise<AppServerControlCommandAcceptedResponse> {
  return requestCommandAccepted('turn.interrupt', sessionId, { interrupt: request });
}

export async function setAppServerControlGoalWs(
  sessionId: string,
  request: AppServerControlGoalSetRequest,
): Promise<AppServerControlCommandAcceptedResponse> {
  return requestCommandAccepted('thread.goal.set', sessionId, { goalSet: request });
}

export async function approveAppServerControlRequestWs(
  sessionId: string,
  requestId: string,
): Promise<AppServerControlCommandAcceptedResponse> {
  return requestCommandAccepted('request.approve', sessionId, { requestId });
}

export async function declineAppServerControlRequestWs(
  sessionId: string,
  requestId: string,
  request: AppServerControlRequestDecisionRequest,
): Promise<AppServerControlCommandAcceptedResponse> {
  return requestCommandAccepted('request.decline', sessionId, {
    requestId,
    requestDecision: request,
  });
}

export async function resolveAppServerControlUserInputWs(
  sessionId: string,
  requestId: string,
  request: AppServerControlUserInputAnswerRequest,
): Promise<AppServerControlCommandAcceptedResponse> {
  return requestCommandAccepted('userInput.resolve', sessionId, {
    requestId,
    userInputAnswer: request,
  });
}

export function openAppServerControlHistorySocket(
  sessionId: string,
  afterSequence: number,
  startIndex: number | undefined,
  count: number | undefined,
  windowRevision: string | undefined,
  callbacks: AppServerControlSubscriptionCallbacks,
  viewportWidth?: number,
): () => void {
  let subscription = subscriptions.get(sessionId);
  if (!subscription) {
    subscription = {
      afterSequence,
      listeners: new Set<AppServerControlSubscriptionCallbacks>(),
    };
  }
  subscription.afterSequence = Math.max(subscription.afterSequence, afterSequence);
  const nextHistoryWindow = buildHistoryWindow(startIndex, count, viewportWidth, windowRevision);
  if (nextHistoryWindow) {
    subscription.historyWindow = nextHistoryWindow;
  }
  subscription.listeners.add(callbacks);
  subscriptions.set(sessionId, subscription);

  const shouldSendSubscribeImmediately = ws?.readyState === WebSocket.OPEN;
  void ensureConnected()
    .then(() => {
      if (!shouldSendSubscribeImmediately) {
        return;
      }

      const current = subscriptions.get(sessionId);
      if (!current || !current.listeners.has(callbacks)) {
        return;
      }

      sendRaw({
        type: 'subscribe',
        sessionId,
        afterSequence: current.afterSequence,
        historyWindow: current.historyWindow,
      });
    })
    .catch(() => {
      callbacks.onError?.(new Event('error'));
    });

  return () => {
    const current = subscriptions.get(sessionId);
    if (!current) {
      return;
    }

    current.listeners.delete(callbacks);
    if (current.listeners.size > 0) {
      return;
    }

    subscriptions.delete(sessionId);
    sendRaw({
      type: 'unsubscribe',
      sessionId,
      afterSequence: current.afterSequence,
    });
  };
}

export function updateAppServerControlHistorySocketWindow(
  sessionId: string,
  startIndex: number | undefined,
  count: number | undefined,
  windowRevision: string | undefined,
  viewportWidth?: number,
): void {
  const subscription = subscriptions.get(sessionId);
  if (!subscription) {
    return;
  }

  const nextHistoryWindow = buildHistoryWindow(startIndex, count, viewportWidth, windowRevision);
  if (historyWindowsEqual(subscription.historyWindow, nextHistoryWindow)) {
    return;
  }

  if (nextHistoryWindow) {
    subscription.historyWindow = nextHistoryWindow;
  } else {
    delete subscription.historyWindow;
  }

  const shouldSendSubscribeImmediately = ws?.readyState === WebSocket.OPEN;
  void ensureConnected().then(() => {
    if (!shouldSendSubscribeImmediately) {
      return;
    }

    const current = subscriptions.get(sessionId);
    if (!current) {
      return;
    }

    sendRaw({
      type: 'subscribe',
      sessionId,
      afterSequence: current.afterSequence,
      historyWindow: current.historyWindow,
    });
  });
}
