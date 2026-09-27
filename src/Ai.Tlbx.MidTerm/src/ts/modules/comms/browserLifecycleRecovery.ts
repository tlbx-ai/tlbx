import {
  $activeSessionId,
  $browserResuming,
  $stateWsConnected,
  $muxWsConnected,
} from '../../stores';
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
  applyScrollbackProtection: () => void;
  recoverTerminalPresentationAfterResume: () => void | Promise<void>;
  keepTerminalOutputActiveWhileHidden: () => boolean;
  stayActiveInBackground?: () => boolean;
  subscribeBackgroundActivity?: (listener: () => void) => () => void;
  suspendAdditionalTerminalTransport?: () => void;
  recoverAdditionalTerminalTransport?: (forceReconnect: boolean) => void;
  recoverSettingsAfterResume?: (forceReconnect: boolean) => void;
  recoverAppServerControlAfterResume?: () => void;
  suspendAppServerControlForBackground?: () => void;
  suspendAncillaryTransportForBackground?: () => void;
  recoverAncillaryTransportAfterResume?: () => void;
}

const FOREGROUND_EVENT_LOOP_GAP_MS = 5000;
const FOREGROUND_RECOVERY_COALESCE_MS = 250;
const FOREGROUND_HEARTBEAT_INTERVAL_MS = 1000;

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
  let probesPending = false;
  let finishingGeneration = -1;
  const finishRecovery = (): void => {
    if (
      isDocumentHidden() ||
      probesPending ||
      !$browserResuming.get() ||
      !$stateWsConnected.get() ||
      !$muxWsConnected.get() ||
      finishingGeneration === recoveryGeneration
    )
      return;
    const generation = recoveryGeneration;
    finishingGeneration = generation;
    void Promise.resolve(options.recoverTerminalPresentationAfterResume()).then(() => {
      if (generation !== recoveryGeneration || isDocumentHidden()) return;
      if (!$stateWsConnected.get() || !$muxWsConnected.get()) {
        finishingGeneration = -1;
        return;
      }
      options.applyScrollbackProtection();
      $browserResuming.set(false);
    });
  };
  const stopStateReadiness = $stateWsConnected.listen(finishRecovery);
  const stopMuxReadiness = $muxWsConnected.listen(finishRecovery);

  const probeReusedTransports = (): Promise<void>[] => {
    const probes: Promise<void>[] = [];
    const generation = recoveryGeneration;
    const stillForeground = (): boolean => generation === recoveryGeneration && !isDocumentHidden();
    // New connections have their own handshake/synchronization deadline.
    // Probe only reused connections; a failed channel must not retire its peers.
    if ($stateWsConnected.get()) {
      probes.push(
        probeStateWebSocket().then((healthy) => {
          if (!healthy && stillForeground()) connectStateWebSocket();
        }),
      );
    }
    if ($muxWsConnected.get()) {
      probes.push(
        probeMuxWebSocket().then((healthy) => {
          if (!healthy && stillForeground()) {
            recoverVisibleTerminalsAfterBrowserResume(
              $activeSessionId.get(),
              options.getVisibleTerminalSessionIds(),
              { forceReconnect: true },
            );
          }
        }),
      );
    }
    return probes;
  };

  const recoverRealtimeAfterBrowserResume = (
    forceReconnect: boolean,
    resumedFromBackground: boolean,
  ): void => {
    const replaceBrowserTransports = forceReconnect;
    const recovering = forceReconnect || resumedFromBackground;
    if (recovering) {
      probesPending = true;
      $browserResuming.set(true);
    }
    if (replaceBrowserTransports || !$stateWsConnected.get()) {
      connectStateWebSocket();
    } else {
      reportBrowserActivity(true);
    }

    if (recovering) {
      options.recoverSettingsAfterResume?.(replaceBrowserTransports);
    }
    if (recovering) {
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
    if (!recovering) {
      void options.recoverTerminalPresentationAfterResume();
      options.applyScrollbackProtection();
    }
    const probes = resumedFromBackground && !forceReconnect ? probeReusedTransports() : [];
    if (recovering) {
      const generation = recoveryGeneration;
      void Promise.all(probes).then(() => {
        if (generation !== recoveryGeneration) return;
        probesPending = false;
        finishRecovery();
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
      enterBrowserBackground();
      return;
    }
    if (resumeFromBackgroundPending) {
      scheduleForegroundRecovery();
      return;
    }
    if (!hasSuspendedForegroundEventLoop(previousHeartbeatAtMs, now)) {
      return;
    }

    forceTransportReconnect = true;
    scheduleForegroundRecovery();
  }, FOREGROUND_HEARTBEAT_INTERVAL_MS);

  return () => {
    recoveryGeneration += 1;
    stopStateReadiness();
    stopMuxReadiness();
    $browserResuming.set(false);
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
