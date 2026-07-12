import {
  MOBILE_DEVICE_STATE_EVENT,
  controlMobileDevice,
  getMobileDeviceState,
  initMobileDeviceController,
  isMobileDeviceConnected,
} from './mobileDeviceController';
import type { MobileDeviceAction } from './mobileDeviceBridge';

export interface MobileDeviceUiOptions {
  closeMenu: () => void;
  setMessage: (severity: 'info' | 'error', message: string | null) => void;
}

const DEVICE_MENU_ACTIONS = [
  'web-preview-device-rotate',
  'web-preview-device-keyboard',
  'web-preview-device-background',
  'web-preview-device-close',
] as const;

const MOBILE_DEVICE_BRIDGE_ARCHIVE = '/midterm-mobile-device-bridge.zip';
const MOBILE_DEVICE_BRIDGE_STORE =
  'https://chromewebstore.google.com/detail/mipkpmmedaoighaadeedfedimiaaekcn';

function openMobileDeviceBridgeStore(): void {
  window.open(MOBILE_DEVICE_BRIDGE_STORE, '_blank', 'noopener,noreferrer');
}

function downloadMobileDeviceBridge(): void {
  const link = document.createElement('a');
  link.href = MOBILE_DEVICE_BRIDGE_ARCHIVE;
  link.download = 'midterm-mobile-device-bridge.zip';
  link.click();
}

function updateControls(): void {
  const state = getMobileDeviceState();
  const open = state?.open === true;
  const connected = isMobileDeviceConnected();
  const deviceButton = document.getElementById('web-preview-mobile-device');
  deviceButton?.setAttribute('aria-pressed', String(open));
  if (deviceButton instanceof HTMLButtonElement) {
    deviceButton.dataset.bridgeConnected = String(connected);
    const label = connected
      ? open
        ? 'Focus Pixel 8 Chrome device'
        : 'Open Pixel 8 Chrome device'
      : 'Install Chrome device bridge';
    deviceButton.title = label;
    deviceButton.setAttribute('aria-label', label);
  }

  for (const id of DEVICE_MENU_ACTIONS) {
    const button = document.getElementById(id);
    if (button instanceof HTMLButtonElement) button.disabled = !connected || !open;
  }

  const label = document.querySelector<HTMLElement>('[data-device-label]');
  if (label) label.textContent = state?.background ? 'Device: Foreground' : 'Device: Background';
}

async function runAction(
  action: MobileDeviceAction,
  options: MobileDeviceUiOptions,
): Promise<void> {
  try {
    await controlMobileDevice(action);
    options.setMessage('info', null);
  } catch (error) {
    options.setMessage('error', `Chrome device failed: ${String(error)}`);
  } finally {
    updateControls();
  }
}

function wireMenuAction(
  id: string,
  getAction: () => MobileDeviceAction,
  options: MobileDeviceUiOptions,
): void {
  document.getElementById(id)?.addEventListener('click', () => {
    options.closeMenu();
    void runAction(getAction(), options);
  });
}

export function initMobileDeviceUi(options: MobileDeviceUiOptions): void {
  initMobileDeviceController();
  document.getElementById('web-preview-mobile-device')?.addEventListener('click', () => {
    if (isMobileDeviceConnected()) {
      void runAction('open', options);
    } else {
      openMobileDeviceBridgeStore();
    }
  });
  wireMenuAction('web-preview-device-rotate', () => 'rotate', options);
  wireMenuAction('web-preview-device-keyboard', () => 'keyboard', options);
  wireMenuAction(
    'web-preview-device-background',
    () => (getMobileDeviceState()?.background ? 'foreground' : 'background'),
    options,
  );
  wireMenuAction('web-preview-device-close', () => 'close', options);
  document.getElementById('web-preview-device-download')?.addEventListener('click', () => {
    options.closeMenu();
    downloadMobileDeviceBridge();
  });
  window.addEventListener(MOBILE_DEVICE_STATE_EVENT, updateControls);
  updateControls();
}

export function refreshMobileDeviceUi(): void {
  updateControls();
}
