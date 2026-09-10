import { $activeSessionId, $connectionStatus, $stateWsConnected } from '../../stores';
import { MOBILE_PIP_ACTIVE_CHANGED_EVENT } from '../../constants';
import { connectStateWebSocket, reportBrowserActivity } from './stateChannel';
import {
  recoverVisibleTerminalsAfterBrowserResume,
  suspendMuxForBrowserBackground,
} from './muxChannel';

interface BrowserLifecycleRecoveryOptions {
  getVisibleTerminalSessionIds: () => string[];
  syncMuxTerminalVisibility: () => void;
  focusActiveTerminal: () => void;
  applyScrollbackProtection: () => void;
  recoverTerminalPresentationAfterResume: () => void;
  keepTerminalOutputActiveWhileHidden: () => boolean;
  suspendAdditionalTerminalTransport?: () => void;
  recoverAdditionalTerminalTransport?: () => void;
  reconnectSettingsAfterLongResume?: () => void;
  recoverAppServerControlAfterResume?: () => void;
  suspendAppServerControlForBackground?: () => void;
  suspendAncillaryTransportForBackground?: () => void;
  recoverAncillaryTransportAfterResume?: () => void;
}

const LONG_BACKGROUND_TRANSPORT_RESET_MS = 5000;
const FOREGROUND_RECOVERY_COALESCE_MS = 250;
const FOREGROUND_HEARTBEAT_INTERVAL_MS = 1000;
const DISCONNECTED_RECOVERY_INTERVAL_MS = 15000;

export function hasSuspendedForegroundEventLoop(
  lastHeartbeatAtMs: number,
  heartbeatAtMs: number,
): boolean {
  return heartbeatAtMs - lastHeartbeatAtMs >= LONG_BACKGROUND_TRANSPORT_RESET_MS;
}

export function setupBrowserLifecycleRecovery(
  options: BrowserLifecycleRecoveryOptions,
): () => void {
  let hiddenAtMs: number | null = isDocumentHidden() ? Date.now() : null;
  let forceTransportReconnect = false;
  let recoveryTimer: ReturnType<typeof globalThis.setTimeout> | null = null;
  let lastRecoveryAtMs = Number.NEGATIVE_INFINITY;
  let lastForegroundHeartbeatAtMs = Date.now();
  let resumeFromBackgroundPending = hiddenAtMs !== null;
  let backgroundLifecycleApplied = false;
  let disconnectedAtMs: number | null = null;

  const recoverRealtimeAfterBrowserResume = (
    forceReconnect: boolean,
    resumedFromBackground: boolean,
  ): void => {
    const replaceBrowserTransports = forceReconnect;
    if (replaceBrowserTransports || !$stateWsConnected.get()) {
      connectStateWebSocket();
    } else {
      reportBrowserActivity(true);
    }

    if (replaceBrowserTransports) options.reconnectSettingsAfterLongResume?.();
    if (replaceBrowserTransports || resumedFromBackground) {
      options.recoverAppServerControlAfterResume?.();
      options.recoverAncillaryTransportAfterResume?.();
    }

    recoverVisibleTerminalsAfterBrowserResume(
      $activeSessionId.get(),
      options.getVisibleTerminalSessionIds(),
      { forceReconnect: replaceBrowserTransports },
    );
    options.recoverAdditionalTerminalTransport?.();

    options.syncMuxTerminalVisibility();
    options.recoverTerminalPresentationAfterResume();
    options.focusActiveTerminal();
    options.applyScrollbackProtection();
  };

  const cancelScheduledRecovery = (): void => {
    if (recoveryTimer === null) return;
    globalThis.clearTimeout(recoveryTimer);
    recoveryTimer = null;
  };

  const rememberBackgroundStart = (): void => {
    hiddenAtMs ??= Date.now();
    resumeFromBackgroundPending = true;
    cancelScheduledRecovery();
  };

  const enterBrowserBackground = (): void => {
    rememberBackgroundStart();
    if (backgroundLifecycleApplied) return;

    backgroundLifecycleApplied = true;
    reportBrowserActivity(false);
    options.suspendAppServerControlForBackground?.();
    options.suspendAncillaryTransportForBackground?.();
    if (!options.keepTerminalOutputActiveWhileHidden()) {
      suspendMuxForBrowserBackground();
      options.suspendAdditionalTerminalTransport?.();
    }
  };

  const scheduleForegroundRecovery = (): void => {
    const now = Date.now();
    lastForegroundHeartbeatAtMs = now;
    if (hiddenAtMs !== null) {
      forceTransportReconnect ||= now - hiddenAtMs >= LONG_BACKGROUND_TRANSPORT_RESET_MS;
      hiddenAtMs = null;
    }

    if (
      recoveryTimer === null &&
      !forceTransportReconnect &&
      !resumeFromBackgroundPending &&
      now - lastRecoveryAtMs < FOREGROUND_RECOVERY_COALESCE_MS
    ) {
      return;
    }

    if (recoveryTimer !== null) return;
    recoveryTimer = globalThis.setTimeout(() => {
      recoveryTimer = null;
      if (isDocumentHidden()) {
        rememberBackgroundStart();
        return;
      }

      const shouldForceReconnect = forceTransportReconnect;
      const resumedFromBackground = resumeFromBackgroundPending;
      forceTransportReconnect = false;
      resumeFromBackgroundPending = false;
      lastRecoveryAtMs = Date.now();
      recoverRealtimeAfterBrowserResume(shouldForceReconnect, resumedFromBackground);
      backgroundLifecycleApplied = false;
    }, 0);
  };

  const handleVisibilityChange = (): void => {
    if (isDocumentHidden()) {
      enterBrowserBackground();
      return;
    }

    reportBrowserActivity();
    scheduleForegroundRecovery();
  };

  const handleFocus = (): void => {
    scheduleForegroundRecovery();
  };

  const handleOnline = (): void => {
    if (isDocumentHidden()) return;
    forceTransportReconnect = true;
    scheduleForegroundRecovery();
  };

  const handleBlur = (): void => {
    reportBrowserActivity(false);
  };

  const handlePageHide = (): void => {
    enterBrowserBackground();
  };

  const handlePageShow = (): void => {
    scheduleForegroundRecovery();
  };

  const handleResume = (): void => {
    scheduleForegroundRecovery();
  };

  const handleFreeze = (): void => {
    enterBrowserBackground();
  };

  const handleMobilePiPActiveChanged = (): void => {
    if (!isDocumentHidden()) return;
    if (!options.keepTerminalOutputActiveWhileHidden()) {
      suspendMuxForBrowserBackground();
      options.suspendAdditionalTerminalTransport?.();
      return;
    }

    const activeSessionId = $activeSessionId.get();
    recoverVisibleTerminalsAfterBrowserResume(
      activeSessionId,
      activeSessionId === null ? [] : [activeSessionId],
      { forceReconnect: true },
    );
    options.recoverAdditionalTerminalTransport?.();
  };

  document.addEventListener('visibilitychange', handleVisibilityChange);
  window.addEventListener('focus', handleFocus);
  window.addEventListener('online', handleOnline);
  window.addEventListener('blur', handleBlur);
  window.addEventListener('pagehide', handlePageHide);
  window.addEventListener('pageshow', handlePageShow);
  document.addEventListener('resume', handleResume);
  document.addEventListener('freeze', handleFreeze);
  window.addEventListener(MOBILE_PIP_ACTIVE_CHANGED_EVENT, handleMobilePiPActiveChanged);

  // A PWA can be restored or launched while its document is already hidden.
  // Apply the same backpressure immediately, before the initial mux connection
  // is allowed to receive and render output no user can see.
  if (isDocumentHidden()) {
    enterBrowserBackground();
  }

  // Android may freeze a standalone PWA without reliably delivering every
  // visibility/focus event. A suspended event loop makes this lightweight
  // heartbeat arrive late; treat that gap exactly like a long background
  // interval instead of waiting for TCP/WebSocket timeouts.
  const heartbeatTimer = globalThis.setInterval(() => {
    const now = Date.now();
    const previousHeartbeatAtMs = lastForegroundHeartbeatAtMs;
    lastForegroundHeartbeatAtMs = now;

    if (isDocumentHidden()) {
      disconnectedAtMs = null;
      enterBrowserBackground();
      return;
    }
    if (resumeFromBackgroundPending) {
      scheduleForegroundRecovery();
      return;
    }
    // A resumed WebSocket handshake can remain CONNECTING without onclose.
    // Give normal backoff/handshakes time, but never leave the visible app stuck.
    if ($connectionStatus.get() === 'connected') {
      disconnectedAtMs = null;
    } else {
      disconnectedAtMs ??= now;
      if (now - disconnectedAtMs >= DISCONNECTED_RECOVERY_INTERVAL_MS) {
        disconnectedAtMs = now;
        connectStateWebSocket();
        recoverVisibleTerminalsAfterBrowserResume(
          $activeSessionId.get(),
          options.getVisibleTerminalSessionIds(),
          { forceReconnect: true },
        );
      }
    }
    if (!hasSuspendedForegroundEventLoop(previousHeartbeatAtMs, now)) {
      return;
    }

    forceTransportReconnect = true;
    scheduleForegroundRecovery();
  }, FOREGROUND_HEARTBEAT_INTERVAL_MS);

  return () => {
    cancelScheduledRecovery();
    globalThis.clearInterval(heartbeatTimer);
    document.removeEventListener('visibilitychange', handleVisibilityChange);
    window.removeEventListener('focus', handleFocus);
    window.removeEventListener('online', handleOnline);
    window.removeEventListener('blur', handleBlur);
    window.removeEventListener('pagehide', handlePageHide);
    window.removeEventListener('pageshow', handlePageShow);
    document.removeEventListener('resume', handleResume);
    document.removeEventListener('freeze', handleFreeze);
    window.removeEventListener(MOBILE_PIP_ACTIVE_CHANGED_EVENT, handleMobilePiPActiveChanged);
  };
}

function isDocumentHidden(): boolean {
  return document.visibilityState === 'hidden';
}
