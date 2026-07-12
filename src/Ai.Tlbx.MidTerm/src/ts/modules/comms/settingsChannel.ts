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
import { $currentSettings, $updateInfo, $settingsWsConnected } from '../../stores';
import { applyReceivedSettings } from '../settings/persistence';
import { handleUpdateInfo } from '../updating/checker';

const log = createLogger('settings-ws');
const settingsReconnect = new ReconnectController();

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
  closeWebSocket(settingsWs, (ws) => {
    settingsWs = ws;
  });

  const ws = new WebSocket(createWsUrl('/ws/settings'));
  settingsWs = ws;

  ws.onopen = () => {
    settingsReconnect.reset();
    $settingsWsConnected.set(true);
    log.info(() => 'Settings WebSocket connected');
  };

  ws.onmessage = (event) => {
    try {
      const message = JSON.parse(event.data as string) as SettingsWsMessage;
      handleMessage(message);
    } catch (e: unknown) {
      const message = e instanceof Error ? e.message : String(e);
      log.error(() => `Error parsing settings message: ${message}`);
    }
  };

  ws.onclose = (event) => {
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
}

function handleMessage(message: SettingsWsMessage): void {
  if (message.type === 'settings' && message.settings) {
    $currentSettings.set(message.settings);
    applyReceivedSettings(message.settings);
  } else if (message.type === 'update' && message.update) {
    $updateInfo.set(message.update);
    handleUpdateInfo(message.update);
  }
}

export function isSettingsWsConnected(): boolean {
  return $settingsWsConnected.get();
}
