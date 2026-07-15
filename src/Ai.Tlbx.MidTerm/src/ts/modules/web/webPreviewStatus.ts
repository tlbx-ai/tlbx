import type { BrowserStatusResponse } from './webApi';

export interface BrowserPreviewStatusIndicatorState {
  severity: 'info' | 'warn' | 'error';
  message: string;
}

export function buildBrowserPreviewStatusIndicatorState(
  status: BrowserStatusResponse,
): BrowserPreviewStatusIndicatorState | null {
  if (!status.hasUiClient) {
    return {
      severity: 'error',
      message:
        'No tlbx browser tab is connected to /ws/state. The dev browser cannot work until a live tlbx tab is open.',
    };
  }

  if (!status.controllable) {
    return {
      severity: 'warn',
      message:
        status.statusMessage ??
        'The preview target is configured, but no attached browser preview is controllable yet.',
    };
  }

  const client = status.defaultClient;
  if (client && (!client.isVisible || !client.hasFocus)) {
    return {
      severity: 'info',
      message:
        'The attached browser preview is currently in a background tab or window. Automation may be slower or throttled there.',
    };
  }

  return null;
}
