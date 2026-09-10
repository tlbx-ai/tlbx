import type { UpdateType } from '../../api/types';
import { JS_BUILD_VERSION } from '../../constants';
import { $frontendRefreshState, $muxWsConnected, $stateWsConnected } from '../../stores';
import { createLogger } from '../logging';
import { t } from '../i18n';
import { reloadAppShell } from './appShellState';

type RestartFlowKind = 'update' | 'restart' | 'certificate';

interface RestartLifecycleState {
  kind: RestartFlowKind;
  updateType: UpdateType | null;
  expectedServerVersion: string | null;
  observedServerVersion: string | null;
  overlay: HTMLDivElement;
  pollTimer: number | null;
  timeoutTimer: number | null;
  httpReady: boolean;
  restartObserved: boolean;
  versionReady: boolean;
  reloadScheduled: boolean;
  completionTimer: number | null;
}

const log = createLogger('update-runtime');
const HEALTH_POLL_INTERVAL_MS = 1500;
const RESTART_TIMEOUT_MS = 60000;
const OVERLAY_DISMISS_DELAY_MS = 700;

let lifecycle: RestartLifecycleState | null = null;
let initialized = false;

function translate(key: string, fallback: string): string {
  const value = t(key);
  return value === key ? fallback : value;
}

function normalizeVersion(version: string | null | undefined): string | null {
  const trimmed = version?.trim();
  return trimmed ? trimmed : null;
}

function createOverlay(message: string, spinner = true): HTMLDivElement {
  const overlay = document.createElement('div');
  overlay.className = 'restart-overlay';
  overlay.innerHTML = `
    ${spinner ? '<div class="spinner"></div>' : ''}
    <div class="restart-overlay-message">${message}</div>
  `;
  document.body.appendChild(overlay);
  return overlay;
}

function setOverlayMessage(message: string, spinner = true): void {
  if (!lifecycle) {
    return;
  }

  lifecycle.overlay.innerHTML = `
    ${spinner ? '<div class="spinner"></div>' : ''}
    <div class="restart-overlay-message">${message}</div>
  `;
}

function clearLifecycle(): void {
  if (!lifecycle) {
    return;
  }

  if (lifecycle.pollTimer !== null) {
    clearInterval(lifecycle.pollTimer);
  }
  if (lifecycle.timeoutTimer !== null) {
    clearTimeout(lifecycle.timeoutTimer);
  }
  if (lifecycle.completionTimer !== null) {
    clearTimeout(lifecycle.completionTimer);
  }

  lifecycle.overlay.remove();
  lifecycle = null;
}

async function fetchServerVersion(): Promise<string | null> {
  const response = await fetch('/api/version', { cache: 'no-store' });
  if (!response.ok) {
    return null;
  }

  return normalizeVersion(await response.text());
}

function canAutoReload(): boolean {
  if (!lifecycle) {
    return false;
  }

  if (lifecycle.kind !== 'update') {
    return lifecycle.updateType === 'full';
  }

  if (lifecycle.expectedServerVersion) {
    return lifecycle.versionReady;
  }

  return lifecycle.restartObserved && lifecycle.httpReady;
}

function scheduleReload(): void {
  if (!lifecycle || lifecycle.reloadScheduled) {
    return;
  }

  lifecycle.reloadScheduled = true;
  setOverlayMessage(translate('update.refreshingUi', 'Refreshing UI...'));
  window.setTimeout(() => {
    reloadAppShell();
  }, 150);
}

function showFailureState(): void {
  if (!lifecycle) {
    return;
  }

  if (lifecycle.pollTimer !== null) {
    clearInterval(lifecycle.pollTimer);
    lifecycle.pollTimer = null;
  }

  const buttonLabel = translate('update.refreshUi', 'Refresh UI');
  const message = lifecycle.httpReady
    ? translate('update.restoringConnections', 'Restoring live connections...')
    : translate(
        'settings.diagnostics.restartFailed',
        'Server may not have restarted. Try refreshing the page.',
      );

  lifecycle.overlay.innerHTML = `
    <div class="restart-overlay-message">${message}</div>
    <button class="btn-primary restart-overlay-btn" type="button">${buttonLabel}</button>
  `;
  lifecycle.overlay
    .querySelector<HTMLButtonElement>('.restart-overlay-btn')
    ?.addEventListener('click', () => {
      reloadAppShell();
    });
}

function finishIfRecovered(): void {
  if (!lifecycle) {
    return;
  }

  if (canAutoReload()) {
    scheduleReload();
    return;
  }

  if (lifecycle.kind === 'update' && !lifecycle.expectedServerVersion) {
    return;
  }

  const socketsReady = $stateWsConnected.get() && $muxWsConnected.get();
  if (!socketsReady) {
    return;
  }

  const refreshState = $frontendRefreshState.get();
  if (refreshState?.status === 'required') {
    scheduleReload();
    return;
  }

  setOverlayMessage(
    refreshState
      ? translate(
          'update.refreshWhenConvenient',
          'Refresh when convenient. Terminals stay connected.',
        )
      : translate('update.serverBack', 'Server reconnected.'),
    false,
  );

  if (lifecycle.completionTimer !== null) {
    clearTimeout(lifecycle.completionTimer);
  }

  lifecycle.completionTimer = window.setTimeout(() => {
    clearLifecycle();
  }, OVERLAY_DISMISS_DELAY_MS);
}

async function pollServerHealth(): Promise<void> {
  if (!lifecycle || lifecycle.reloadScheduled) {
    return;
  }

  try {
    const [healthResponse, serverVersion] = await Promise.all([
      fetch('/api/health', { cache: 'no-store' }),
      lifecycle.expectedServerVersion ? fetchServerVersion() : Promise.resolve<string | null>(null),
    ]);
    if (!healthResponse.ok) {
      lifecycle.restartObserved = true;
      lifecycle.httpReady = false;
      return;
    }

    lifecycle.httpReady = true;
    if (lifecycle.expectedServerVersion) {
      lifecycle.observedServerVersion = serverVersion;
      lifecycle.versionReady = serverVersion === lifecycle.expectedServerVersion;
      if (!lifecycle.versionReady) {
        setOverlayMessage(
          translate('update.waitingForUpdatedServer', 'Waiting for the updated server...'),
        );
        return;
      }
    }

    if (!($stateWsConnected.get() && $muxWsConnected.get())) {
      setOverlayMessage(translate('update.restoringConnections', 'Restoring live connections...'));
    }

    finishIfRecovered();
  } catch {
    lifecycle.restartObserved = true;
    lifecycle.httpReady = false;
    // Server is still down or the new process is not ready yet.
  }
}

function syncLifecycleFromConnections(): void {
  if (!lifecycle || lifecycle.reloadScheduled) {
    return;
  }

  if (!$stateWsConnected.get() || !$muxWsConnected.get()) {
    lifecycle.restartObserved = true;
    lifecycle.httpReady = false;
  }

  if (lifecycle.httpReady) {
    finishIfRecovered();
    return;
  }

  const reconnecting = !$stateWsConnected.get() || !$muxWsConnected.get();
  if (reconnecting) {
    setOverlayMessage(
      translate('settings.diagnostics.restartingServer', 'Server is restarting...'),
    );
  }
}

export function initUpdateRuntime(): void {
  if (initialized) {
    return;
  }

  initialized = true;
  $stateWsConnected.subscribe(() => {
    syncLifecycleFromConnections();
  });
  $muxWsConnected.subscribe(() => {
    syncLifecycleFromConnections();
  });
}

export function beginServerRestartLifecycle(
  kind: RestartFlowKind,
  options?: { updateType?: UpdateType | null; expectedServerVersion?: string | null },
): void {
  clearLifecycle();

  const expectedServerVersion = normalizeVersion(options?.expectedServerVersion ?? null);

  lifecycle = {
    kind,
    updateType: options?.updateType ?? null,
    expectedServerVersion,
    observedServerVersion: null,
    overlay: createOverlay(
      translate('settings.diagnostics.restartingServer', 'Server is restarting...'),
    ),
    pollTimer: null,
    timeoutTimer: null,
    httpReady: false,
    restartObserved: false,
    versionReady: expectedServerVersion === null,
    reloadScheduled: false,
    completionTimer: null,
  };

  lifecycle.pollTimer = window.setInterval(() => {
    void pollServerHealth();
  }, HEALTH_POLL_INTERVAL_MS);

  lifecycle.timeoutTimer = window.setTimeout(() => {
    showFailureState();
  }, RESTART_TIMEOUT_MS);

  void pollServerHealth();
  log.info(
    () =>
      `Server restart lifecycle started (${kind}, updateType=${options?.updateType ?? 'none'}, expectedVersion=${expectedServerVersion ?? 'none'})`,
  );
}

export function setFrontendRefreshState(
  serverVersion: string,
  options?: {
    status?: 'available' | 'required';
    updateType?: UpdateType | 'unknown';
  },
): void {
  const current = $frontendRefreshState.get();
  const nextStatus = options?.status ?? 'available';
  const nextType = options?.updateType ?? 'unknown';

  if (current !== null && current.serverVersion === serverVersion) {
    if (current.status === nextStatus && current.updateType === nextType) {
      return;
    }
  }

  if (current === null) {
    $frontendRefreshState.set({
      clientVersion: JS_BUILD_VERSION,
      serverVersion,
      updateType: nextType,
      status: nextStatus,
      reason: 'server-update',
    });
    return;
  }

  $frontendRefreshState.set({
    clientVersion: current.clientVersion || JS_BUILD_VERSION,
    serverVersion,
    updateType: nextType,
    status: nextStatus,
    reason: 'server-update',
  });
}

export function clearFrontendRefreshState(): void {
  $frontendRefreshState.set(null);
}

export function requestFrontendRefresh(): void {
  reloadAppShell();
}
