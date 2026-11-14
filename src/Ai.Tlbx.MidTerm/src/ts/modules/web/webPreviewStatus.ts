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
  if (client && (client.isDocumentVisible === null || client.isDocumentVisible === undefined)) {
    return {
      severity: 'info',
      message:
        'This bridge has not reported document visibility. Reload the preview to obtain its current visibility state.',
    };
  }
  if (client?.isDocumentVisible === false) {
    return {
      severity: 'info',
      message:
        'The browser document is hidden. DOM commands remain available; media and scheduling may be throttled even when the preview panel is shown.',
    };
  }
  if (client?.isPanelVisible === false) {
    return {
      severity: 'info',
      message: 'The preview panel is hidden. The document bridge remains available for DOM commands.',
    };
  }
  if (client && !client.isVisible) {
    return {
      severity: 'info',
      message:
        'The preview is not effectively visible. Panel or document visibility may be unknown; DOM commands remain available.',
    };
  }
  if (client && !client.hasFocus) {
    return { severity: 'info', message: 'The preview is visible but does not have keyboard focus.' };
  }

  return null;
}
