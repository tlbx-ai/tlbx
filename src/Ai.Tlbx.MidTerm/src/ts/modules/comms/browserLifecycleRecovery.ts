import { $activeSessionId, $stateWsConnected } from '../../stores';
import { connectStateWebSocket, reportBrowserActivity } from './stateChannel';
import { recoverVisibleTerminalsAfterBrowserResume } from './muxChannel';

interface BrowserLifecycleRecoveryOptions {
  getVisibleTerminalSessionIds: () => string[];
  syncMuxTerminalVisibility: () => void;
  focusActiveTerminal: () => void;
  applyScrollbackProtection: () => void;
}

export function setupBrowserLifecycleRecovery(options: BrowserLifecycleRecoveryOptions): void {
  const recoverRealtimeAfterBrowserResume = (): void => {
    reportBrowserActivity(true);

    if (!$stateWsConnected.get()) {
      connectStateWebSocket();
    }

    recoverVisibleTerminalsAfterBrowserResume(
      $activeSessionId.get(),
      options.getVisibleTerminalSessionIds(),
    );

    options.syncMuxTerminalVisibility();
    options.focusActiveTerminal();
    options.applyScrollbackProtection();
  };

  document.addEventListener('visibilitychange', () => {
    reportBrowserActivity();

    if (isDocumentHidden()) {
      return;
    }

    recoverRealtimeAfterBrowserResume();
  });

  window.addEventListener('focus', () => {
    recoverRealtimeAfterBrowserResume();
  });

  window.addEventListener('blur', () => {
    reportBrowserActivity(false);
  });

  window.addEventListener('pagehide', () => {
    reportBrowserActivity(false);
  });

  window.addEventListener('pageshow', () => {
    recoverRealtimeAfterBrowserResume();
  });

  document.addEventListener('resume', () => {
    recoverRealtimeAfterBrowserResume();
  });
}

function isDocumentHidden(): boolean {
  return document.visibilityState === 'hidden';
}
