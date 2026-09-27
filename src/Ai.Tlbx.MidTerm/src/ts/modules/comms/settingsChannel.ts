/**
 * Settings Channel Module
 *
 * Manages the settings WebSocket connection for real-time settings and update sync.
 * When settings are changed on any client, all connected clients receive the update.
 */

import type { MidTermSettingsPublic, UpdateInfo } from '../../types';
import { ReconnectController, createWsUrl, closeWebSocket } from '../../utils';
import { handleAuthenticatedWebSocketClose } from '../auth/sessionLifetime';
import { createLogger } from '../logging';
import {
  $currentSettings,
  $updateInfo,
  $settingsWsConnected,
  areJsonLikeEqual,
} from '../../stores';
import { applyReceivedSettings } from '../settings/persistence';
import { handleUpdateInfo } from '../updating/checker';
import { RESUME_PROBE_TIMEOUT_MS, SocketReadiness } from './socketReadiness';

const log = createLogger('settings-ws');
const settingsReconnect = new ReconnectController();
const settingsReadiness = new SocketReadiness();

/** Message wrapper from server */
interface SettingsWsMessage {
  type: 'settings' | 'update';
  settings?: MidTermSettingsPublic;
  update?: UpdateInfo;
}

let settingsWs: WebSocket | null = null;

/**
 * Connect to the settings WebSocket for real-time settings sync.
 * Automatically reconnects with exponential backoff on disconnect.
 */
export function connectSettingsWebSocket(): void {
  settingsReconnect.cancel();
  closeWebSocket(settingsWs, (ws) => {
    settingsWs = ws;
  });
  $settingsWsConnected.set(false);

  const ws = new WebSocket(createWsUrl('/ws/settings'));
  settingsWs = ws;

  ws.onopen = () => {
    if (settingsWs !== ws) return;
    settingsReconnect.reset();
    log.info(() => 'Settings WebSocket connected');
  };

  ws.onmessage = (event) => {
    if (settingsWs !== ws) return;
    try {
      const message = JSON.parse(event.data as string) as SettingsWsMessage;
      handleMessage(message);
      if (message.type === 'settings' && message.settings) $settingsWsConnected.set(true);
    } catch (e: unknown) {
      const message = e instanceof Error ? e.message : String(e);
      log.error(() => `Error parsing settings message: ${message}`);
    }
  };

  ws.onclose = (event) => {
    if (settingsWs !== ws) return;
    $settingsWsConnected.set(false);
    log.info(() => 'Settings WebSocket disconnected');
    if (handleAuthenticatedWebSocketClose(event)) {
      return;
    }
    settingsReconnect.schedule(connectSettingsWebSocket);
  };

  ws.onerror = (e) => {
    log.error(() => `Settings WebSocket error: ${e.type}`);

    if (settingsWs !== ws) {
      return;
    }

    if (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING) {
      ws.close();
    }
  };
  settingsReadiness.watch(
    ws,
    () => settingsWs === ws,
    () => $settingsWsConnected.get(),
    connectSettingsWebSocket,
  );
}

/** Refresh settings over the existing channel; only replace it if it fails. */
export function recoverSettingsAfterBrowserResume(forceReconnect: boolean): void {
  const socket = settingsWs;
  if (forceReconnect || !socket || socket.readyState === WebSocket.CLOSED) {
    connectSettingsWebSocket();
    return;
  }
  // Initial synchronization already has its own deadline.
  if (!$settingsWsConnected.get()) return;
  const finish = (): void => {
    clearTimeout(timeout);
    socket.removeEventListener('message', received);
    socket.removeEventListener('close', finish);
  };
  const received = (event: MessageEvent): void => {
    try {
      const message = JSON.parse(event.data as string) as SettingsWsMessage;
      if (message.type === 'settings' && message.settings) finish();
    } catch {
      /* The normal handler reports malformed messages. */
    }
  };
  const timeout = setTimeout(() => {
    finish();
    if (settingsWs === socket && document.visibilityState !== 'hidden') connectSettingsWebSocket();
  }, RESUME_PROBE_TIMEOUT_MS);
  socket.addEventListener('message', received);
  socket.addEventListener('close', finish, { once: true });
  try {
    socket.send('?');
  } catch {
    finish();
    connectSettingsWebSocket();
  }
}

function handleMessage(message: SettingsWsMessage): void {
  if (message.type === 'settings' && message.settings) {
    if (areJsonLikeEqual(message.settings, $currentSettings.get())) {
      return;
    }
    applyReceivedSettings(message.settings);
  } else if (message.type === 'update' && message.update) {
    if (areJsonLikeEqual(message.update, $updateInfo.get())) {
      return;
    }
    handleUpdateInfo(message.update);
  }
}

export function isSettingsWsConnected(): boolean {
  return $settingsWsConnected.get();
}
