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
  let pageWasHidden = isDocumentHidden();

  const recoverRealtimeAfterBrowserResume = (quickRefresh: boolean): void => {
    reportBrowserActivity(true);

    if (!$stateWsConnected.get()) {
      connectStateWebSocket();
    }

    recoverVisibleTerminalsAfterBrowserResume(
      $activeSessionId.get(),
      options.getVisibleTerminalSessionIds(),
      { quickRefresh },
    );

    options.syncMuxTerminalVisibility();
    options.focusActiveTerminal();
    options.applyScrollbackProtection();
  };

  document.addEventListener('visibilitychange', () => {
    reportBrowserActivity();

    if (isDocumentHidden()) {
      pageWasHidden = true;
      return;
    }

    const quickRefresh = pageWasHidden;
    pageWasHidden = false;
    recoverRealtimeAfterBrowserResume(quickRefresh);
  });

  window.addEventListener('focus', () => {
    recoverRealtimeAfterBrowserResume(false);
  });

  window.addEventListener('blur', () => {
    reportBrowserActivity(false);
  });

  window.addEventListener('pagehide', () => {
    pageWasHidden = true;
    reportBrowserActivity(false);
  });

  window.addEventListener('pageshow', (event: PageTransitionEvent) => {
    const quickRefresh = pageWasHidden || event.persisted;
    pageWasHidden = false;
    recoverRealtimeAfterBrowserResume(quickRefresh);
  });

  document.addEventListener('resume', () => {
    pageWasHidden = false;
    recoverRealtimeAfterBrowserResume(true);
  });
}

function isDocumentHidden(): boolean {
  return document.visibilityState === 'hidden';
}
