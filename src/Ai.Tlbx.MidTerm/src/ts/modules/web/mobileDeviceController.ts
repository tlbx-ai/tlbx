import { $activeSessionId } from '../../stores';
import { pasteToTerminal } from '../terminal';
import {
  createBrowserPreviewClient,
  getWebPreviewTarget,
  type BrowserPreviewClientResponse,
} from './webApi';
import {
  initMobileDeviceBridge,
  invokeMobileDevice,
  isMobileDeviceBridgeReady,
  onMobileDeviceBridgeReady,
  type MobileDeviceAction,
  type MobileDeviceState,
} from './mobileDeviceBridge';
import { decodeScreenshotDataUrl } from './webPanelUtils';
import { buildProxyUrl } from './previewProxyUrl';
import { getActivePreviewName, getSessionPreview } from './webSessionState';

export const MOBILE_DEVICE_STATE_EVENT = 'midterm:mobile-device-state';

const clients = new Map<string, BrowserPreviewClientResponse>();
const states = new Map<string, MobileDeviceState>();
let initialized = false;

function getDeviceKey(sessionId: string, previewName: string): string {
  return `${window.location.origin}|${sessionId}|${previewName}`;
}

function resolveTarget(
  requestedSessionId?: string,
  requestedPreviewName?: string,
): {
  sessionId: string;
  previewName: string;
} {
  const sessionId = requestedSessionId?.trim() || $activeSessionId.get();
  const previewName = requestedPreviewName?.trim() || getActivePreviewName();
  if (!sessionId) throw new Error('There is no active session.');
  return { sessionId, previewName };
}

function publishState(): void {
  window.dispatchEvent(new CustomEvent(MOBILE_DEVICE_STATE_EVENT));
}

async function ensureClient(
  sessionId: string,
  previewName: string,
): Promise<BrowserPreviewClientResponse> {
  const deviceKey = getDeviceKey(sessionId, previewName);
  const existing = clients.get(deviceKey);
  if (existing) return existing;
  const created = await createBrowserPreviewClient(sessionId, previewName);
  if (!created) throw new Error('tlbx could not register the device preview.');
  clients.set(deviceKey, created);
  return created;
}

async function addOpenPayload(
  payload: Record<string, unknown>,
  sessionId: string,
  previewName: string,
): Promise<void> {
  const preview = getSessionPreview(sessionId, previewName);
  if (!preview?.url) throw new Error('This preview has no URL.');
  const client = await ensureClient(sessionId, previewName);
  payload.url = buildProxyUrl(
    preview.url,
    client,
    preview.targetRevision,
    client.origin ?? window.location.origin,
  );
}

async function pasteScreenshot(dataUrl: string, sessionId: string): Promise<void> {
  const blob = decodeScreenshotDataUrl(dataUrl);
  if (!blob) throw new Error('Chrome device screenshot data could not be decoded.');
  const ts = new Date().toISOString().replace(/[:.]/g, '-');
  const file = new File([blob], `screenshot_${ts}.png`, { type: 'image/png' });
  const formData = new FormData();
  formData.append('file', file);
  const response = await fetch(`/api/sessions/${sessionId}/upload`, {
    method: 'POST',
    body: formData,
  });
  if (!response.ok) throw new Error(`Screenshot upload failed (${response.status}).`);
  const result = (await response.json()) as { path?: string };
  if (!result.path) throw new Error('Screenshot upload returned no file path.');
  await pasteToTerminal(sessionId, result.path, true);
}

export function initMobileDeviceController(): void {
  if (initialized) return;
  initialized = true;
  initMobileDeviceBridge();
  onMobileDeviceBridgeReady(() => {
    void refreshActiveMobileDeviceState();
  });
}

export function isMobileDeviceConnected(): boolean {
  return isMobileDeviceBridgeReady();
}

export function getMobileDeviceState(
  requestedSessionId?: string,
  requestedPreviewName?: string,
): MobileDeviceState | null {
  try {
    const target = resolveTarget(requestedSessionId, requestedPreviewName);
    return states.get(getDeviceKey(target.sessionId, target.previewName)) ?? null;
  } catch {
    return null;
  }
}

export async function refreshActiveMobileDeviceState(): Promise<MobileDeviceState | null> {
  if (!isMobileDeviceBridgeReady()) {
    publishState();
    return null;
  }
  let target: { sessionId: string; previewName: string };
  try {
    target = resolveTarget();
  } catch {
    publishState();
    return null;
  }
  const deviceKey = getDeviceKey(target.sessionId, target.previewName);
  try {
    const state = await invokeMobileDevice('status', { deviceKey });
    states.set(deviceKey, state);
    publishState();
    return state;
  } catch {
    states.delete(deviceKey);
    publishState();
    return null;
  }
}

export async function controlMobileDevice(
  action: MobileDeviceAction,
  requestedSessionId?: string,
  requestedPreviewName?: string,
  profileId = 'pixel-8',
  options: { pasteScreenshot?: boolean; expectedTargetRevision?: number } = {},
): Promise<MobileDeviceState> {
  const target = resolveTarget(requestedSessionId, requestedPreviewName);
  if (action !== 'status' && !isMobileDeviceBridgeReady()) {
    throw new Error(
      'Chrome device bridge is not connected. Install it from the preview menu, then click its extension icon in this tlbx tab.',
    );
  }

  const deviceKey = getDeviceKey(target.sessionId, target.previewName);
  const payload: Record<string, unknown> = { deviceKey, profileId };
  if (action === 'open') {
    payload.language = navigator.language;
    await addOpenPayload(payload, target.sessionId, target.previewName);
  }
  const current = getSessionPreview(target.sessionId, target.previewName);
  if (
    options.expectedTargetRevision !== undefined &&
    current &&
    current.targetRevision !== options.expectedTargetRevision
  ) {
    throw new Error(
      'Preview target changed before mobile device dispatch. Inspect before retrying.',
    );
  }
  const state = await invokeMobileDevice(action, payload);
  states.set(deviceKey, state);
  publishState();
  if (options.pasteScreenshot && action === 'screenshot' && state.dataUrl) {
    await pasteScreenshot(state.dataUrl, target.sessionId);
  }
  return state;
}

export type MobileDeviceCommandState = Omit<MobileDeviceState, 'dataUrl'> & {
  available: boolean;
  error?: string;
};

export interface MobileDeviceCommandResult {
  mobileDevice: MobileDeviceCommandState;
  screenshotDataUrl?: string;
}

export async function controlMobileDeviceCommand(
  action: MobileDeviceAction,
  sessionId: string,
  previewName: string,
  targetRevision: number,
  profileId?: string,
): Promise<MobileDeviceCommandResult> {
  async function assertRevision(): Promise<void> {
    const target = await getWebPreviewTarget(sessionId, previewName);
    if (
      !target ||
      target.sessionId !== sessionId ||
      target.previewName !== previewName ||
      target.targetRevision !== targetRevision
    ) {
      throw new Error(
        'Preview target changed during mobile device control. Inspect before retrying.',
      );
    }
  }
  await assertRevision();
  let state: MobileDeviceState;
  try {
    state = await controlMobileDevice(action, sessionId, previewName, profileId, {
      expectedTargetRevision: targetRevision,
    });
  } catch (error) {
    await assertRevision();
    if (action !== 'status') throw error;
    states.delete(getDeviceKey(sessionId, previewName));
    publishState();
    return {
      mobileDevice: {
        available: false,
        connected: isMobileDeviceBridgeReady(),
        error: error instanceof Error ? error.message : String(error),
      },
    };
  }
  await assertRevision();
  const { dataUrl, ...device } = state;
  if (!device.connected && action !== 'status') {
    throw new Error('Chrome device bridge reported disconnected.');
  }
  const result: MobileDeviceCommandResult = {
    mobileDevice: { ...device, available: device.connected },
  };
  if (action === 'screenshot') {
    if (!dataUrl?.startsWith('data:image/png;base64,')) {
      throw new Error('Chrome device command returned no PNG screenshot.');
    }
    result.screenshotDataUrl = dataUrl;
  }
  return result;
}

export async function captureMobileDeviceScreenshot(
  sessionId: string,
  previewName: string,
): Promise<string | null> {
  const deviceKey = getDeviceKey(sessionId, previewName);
  if (!isMobileDeviceBridgeReady() || states.get(deviceKey)?.open !== true) return null;
  try {
    const state = await invokeMobileDevice('screenshot', { deviceKey });
    states.set(deviceKey, state);
    publishState();
    return state.dataUrl ?? null;
  } catch {
    states.delete(deviceKey);
    publishState();
    return null;
  }
}
