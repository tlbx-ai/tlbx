import { $activeSessionId, $connectionStatus, $stateWsConnected } from '../../stores';
import { MOBILE_PIP_ACTIVE_CHANGED_EVENT } from '../../constants';
import { connectStateWebSocket, reportBrowserActivity, probeStateWebSocket } from './stateChannel';
import {
  probeMuxWebSocket,
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
  stayActiveInBackground?: () => boolean;
  subscribeBackgroundActivity?: (listener: () => void) => () => void;
  suspendAdditionalTerminalTransport?: () => void;
  recoverAdditionalTerminalTransport?: (forceReconnect: boolean) => void;
  reconnectSettingsAfterLongResume?: () => void;
  recoverAppServerControlAfterResume?: () => void;
  suspendAppServerControlForBackground?: () => void;
  suspendAncillaryTransportForBackground?: () => void;
  recoverAncillaryTransportAfterResume?: () => void;
}

const FOREGROUND_EVENT_LOOP_GAP_MS = 5000;
const FOREGROUND_RECOVERY_COALESCE_MS = 250;
const FOREGROUND_HEARTBEAT_INTERVAL_MS = 1000;
const DISCONNECTED_RECOVERY_INTERVAL_MS = 15000;

export function hasSuspendedForegroundEventLoop(
  lastHeartbeatAtMs: number,
  heartbeatAtMs: number,
): boolean {
  return heartbeatAtMs - lastHeartbeatAtMs >= FOREGROUND_EVENT_LOOP_GAP_MS;
}

export function setupBrowserLifecycleRecovery(
  options: BrowserLifecycleRecoveryOptions,
): () => void {
  let pageFrozen = false;
  let recoveryGeneration = 0;
  let forceTransportReconnect = false;
  let recoveryTimer: ReturnType<typeof globalThis.setTimeout> | null = null;
  let lastRecoveryAtMs = Number.NEGATIVE_INFINITY;
  let lastForegroundHeartbeatAtMs = Date.now();
  let resumeFromBackgroundPending = isDocumentHidden();
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
    options.recoverAdditionalTerminalTransport?.(replaceBrowserTransports);

    options.syncMuxTerminalVisibility();
    options.recoverTerminalPresentationAfterResume();
    options.focusActiveTerminal();
    options.applyScrollbackProtection();
    if (resumedFromBackground && !forceReconnect) {
      const generation = recoveryGeneration;
      void Promise.all([probeStateWebSocket(), probeMuxWebSocket()]).then((healthy) => {
        if (generation !== recoveryGeneration || isDocumentHidden() || healthy.every(Boolean))
          return;
        forceTransportReconnect = true;
        scheduleForegroundRecovery();
      });
    }
  };

  const cancelScheduledRecovery = (): void => {
    if (recoveryTimer === null) return;
    globalThis.clearTimeout(recoveryTimer);
    recoveryTimer = null;
  };

  const rememberBackgroundStart = (): void => {
    recoveryGeneration += 1;
    resumeFromBackgroundPending = true;
    cancelScheduledRecovery();
  };

  const enterBrowserBackground = (): void => {
    rememberBackgroundStart();
    if (backgroundLifecycleApplied) return;

    backgroundLifecycleApplied = true;
    reportBrowserActivity(false);
    if (pageFrozen || !options.stayActiveInBackground?.()) {
      options.suspendAppServerControlForBackground?.();
      options.suspendAncillaryTransportForBackground?.();
    }
    if (
      pageFrozen ||
      !(options.stayActiveInBackground?.() || options.keepTerminalOutputActiveWhileHidden())
    ) {
      suspendMuxForBrowserBackground();
      options.suspendAdditionalTerminalTransport?.();
    }
  };

  const scheduleForegroundRecovery = (): void => {
    const now = Date.now();
    lastForegroundHeartbeatAtMs = now;
    // Being hidden alone says nothing about connection health. A real freeze
    // invalidates transports; ordinary tab switches keep healthy sockets alive.
    forceTransportReconnect ||= pageFrozen;
    pageFrozen = false;

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
      if (shouldForceReconnect || resumedFromBackground) recoveryGeneration += 1;
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
    handleFreeze();
  };

  const handlePageShow = (): void => {
    scheduleForegroundRecovery();
  };

  const handleResume = (): void => {
    scheduleForegroundRecovery();
  };

  const handleFreeze = (): void => {
    if (pageFrozen) return;
    pageFrozen = true;
    // Upgrade a merely hidden page to a suspended one, even with stay-active on.
    backgroundLifecycleApplied = false;
    enterBrowserBackground();
  };

  const handleMobilePiPActiveChanged = (): void => {
    if (!isDocumentHidden() || pageFrozen) return;
    const stayActive = options.stayActiveInBackground?.() ?? false;
    reportBrowserActivity(false, true);
    if (stayActive) {
      options.recoverAppServerControlAfterResume?.();
      options.recoverAncillaryTransportAfterResume?.();
    } else {
      options.suspendAppServerControlForBackground?.();
      options.suspendAncillaryTransportForBackground?.();
    }
    if (!(stayActive || options.keepTerminalOutputActiveWhileHidden())) {
      suspendMuxForBrowserBackground();
      options.suspendAdditionalTerminalTransport?.();
      return;
    }
    recoverVisibleTerminalsAfterBrowserResume(
      $activeSessionId.get(),
      options.getVisibleTerminalSessionIds(),
      { forceReconnect: false },
    );
    options.recoverAdditionalTerminalTransport?.(false);
  };

  const unsubscribeBackgroundActivity = options.subscribeBackgroundActivity?.(
    handleMobilePiPActiveChanged,
  );

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
    recoveryGeneration += 1;
    unsubscribeBackgroundActivity?.();
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
