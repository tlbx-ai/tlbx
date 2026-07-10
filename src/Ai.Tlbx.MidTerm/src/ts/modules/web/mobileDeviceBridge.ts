const PAGE_SOURCE = 'midterm-mobile-device-page';
const EXTENSION_SOURCE = 'midterm-mobile-device-extension';
const BRIDGE_READY_EVENT = 'midterm:mobile-device-bridge-ready';
const DEFAULT_TIMEOUT_MS = 15000;

export type MobileDeviceAction =
  | 'status'
  | 'open'
  | 'rotate'
  | 'keyboard'
  | 'background'
  | 'foreground'
  | 'reload'
  | 'screenshot'
  | 'close';

export interface MobileDeviceState {
  connected: boolean;
  open?: boolean;
  profileId?: string;
  profileLabel?: string;
  orientation?: 'portrait' | 'landscape';
  keyboard?: boolean;
  background?: boolean;
  width?: number;
  height?: number;
  deviceScaleFactor?: number;
  dataUrl?: string;
}

interface BridgeMessage {
  source: typeof EXTENSION_SOURCE;
  protocol: 1;
  type: 'ready' | 'result';
  requestId?: string;
  success?: boolean;
  result?: MobileDeviceState;
  error?: string;
}

interface PendingRequest {
  resolve: (value: MobileDeviceState) => void;
  reject: (error: Error) => void;
  timeout: number;
}

const pending = new Map<string, PendingRequest>();
let initialized = false;
let ready = false;

function createRequestId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

function handleBridgeMessage(event: MessageEvent<unknown>): void {
  if (event.source !== window || typeof event.data !== 'object' || event.data === null) return;
  const message = event.data as Partial<BridgeMessage>;
  if (message.source !== EXTENSION_SOURCE || message.protocol !== 1) return;

  if (message.type === 'ready') {
    ready = true;
    window.dispatchEvent(new CustomEvent(BRIDGE_READY_EVENT));
    return;
  }

  if (message.type !== 'result' || typeof message.requestId !== 'string') return;
  const request = pending.get(message.requestId);
  if (!request) return;
  window.clearTimeout(request.timeout);
  pending.delete(message.requestId);
  if (message.success && message.result) {
    ready = true;
    request.resolve(message.result);
  } else {
    request.reject(new Error(message.error || 'Chrome device bridge command failed.'));
  }
}

function request(
  command: MobileDeviceAction | 'ping',
  payload: Record<string, unknown>,
  timeoutMs = DEFAULT_TIMEOUT_MS,
): Promise<MobileDeviceState> {
  const requestId = createRequestId();
  return new Promise((resolve, reject) => {
    const timeout = window.setTimeout(() => {
      pending.delete(requestId);
      reject(new Error('Chrome device bridge is not connected.'));
    }, timeoutMs);
    pending.set(requestId, { resolve, reject, timeout });
    window.postMessage(
      { source: PAGE_SOURCE, protocol: 1, requestId, command, payload },
      window.location.origin,
    );
  });
}

export function initMobileDeviceBridge(): void {
  if (initialized) return;
  initialized = true;
  window.addEventListener('message', handleBridgeMessage);
  void request('ping', {}, 700).catch(() => {
    ready = false;
  });
}

export function isMobileDeviceBridgeReady(): boolean {
  return ready;
}

export function onMobileDeviceBridgeReady(listener: () => void): () => void {
  window.addEventListener(BRIDGE_READY_EVENT, listener);
  return () => {
    window.removeEventListener(BRIDGE_READY_EVENT, listener);
  };
}

export async function invokeMobileDevice(
  command: MobileDeviceAction,
  payload: Record<string, unknown>,
): Promise<MobileDeviceState> {
  if (!initialized) initMobileDeviceBridge();
  return request(command, payload, command === 'screenshot' ? 30000 : DEFAULT_TIMEOUT_MS);
}
