/**
 * Diagnostics Panel Module
 *
 * Displays file paths, latency measurements, and terminal transport diagnostics.
 */

import { getPaths, getSessionState, reloadSettings, restartServer } from '../../api/client';
import type { SessionStateResponse } from '../../api/types';
import { getBrowserTransportSnapshot, measureLatency, onOutputRtt } from '../comms';
import { $activeSessionId, $browserSessions, getSession } from '../../stores';
import type { BrowserSessionStatus } from '../../types';
import { getSessionDisplayInfo } from '../sidebar/sessionList';
import { enableLatencyOverlay, disableLatencyOverlay } from './latencyOverlay';
import { enableGitStatusOverlay, disableGitStatusOverlay } from './gitStatusOverlay';
import { downloadActiveTerminalBufferDump } from './terminalBufferDump';
import {
  clearTerminalKeyLog,
  getTerminalKeyLogLines,
  isTerminalKeyLogEnabled,
  setTerminalKeyLogEnabled,
  subscribeTerminalKeyLog,
} from './terminalKeyLog';
import { t } from '../i18n';
import { showConfirm } from '../../utils/dialog';
import { createLogger } from '../logging';
import { beginServerRestartLifecycle, reloadAppShell } from '../updating';

const log = createLogger('diagnostics');
type TerminalTransportDiagnostics = NonNullable<SessionStateResponse['terminalTransport']>;

let latencyInterval: ReturnType<typeof setInterval> | null = null;

export function initDiagnosticsPanel(): void {
  void loadPaths();
  bindReloadSettingsButton();
  bindRestartButton();
  bindOverlayToggle();
  bindGitOverlayToggle();
  bindTerminalBufferDumpButton();
  bindTerminalKeyLogControls();
  bindBrowserSessionTree();
}

export function startLatencyMeasurement(): void {
  stopLatencyMeasurement();
  void runLatencyPing();
  latencyInterval = setInterval(() => {
    void runLatencyPing();
  }, 2000);
}

export function stopLatencyMeasurement(): void {
  if (latencyInterval !== null) {
    clearInterval(latencyInterval);
    latencyInterval = null;
  }
}

async function runLatencyPing(): Promise<void> {
  const sessionId = $activeSessionId.get();
  if (!sessionId) {
    clearTerminalTransportDiagnostics();
    return;
  }

  const sessionEl = document.getElementById('diag-ping-session');
  const serverEl = document.getElementById('diag-server-rtt');
  const mthostEl = document.getElementById('diag-mthost-rtt');

  const session = getSession(sessionId);
  if (sessionEl) {
    if (session) {
      const display = getSessionDisplayInfo(session);
      sessionEl.textContent = display.secondary
        ? `${display.primary} — ${display.secondary}`
        : display.primary;
    } else {
      sessionEl.textContent = sessionId;
    }
  }

  const [result, stateResponse] = await Promise.all([
    measureLatency(sessionId),
    getSessionState(sessionId, false).catch((error: unknown) => {
      log.warn(() => `Failed to load terminal transport diagnostics: ${String(error)}`);
      return null;
    }),
  ]);

  if (serverEl) {
    serverEl.textContent =
      result.serverRtt !== null ? `${result.serverRtt.toFixed(1)} ms` : 'timeout';
  }
  if (mthostEl) {
    mthostEl.textContent =
      result.mthostRtt !== null ? `${result.mthostRtt.toFixed(1)} ms` : 'timeout';
  }

  updateTerminalTransportDiagnostics(sessionId, stateResponse?.terminalTransport ?? null);
}

async function loadPaths(): Promise<void> {
  try {
    const { data, response } = await getPaths();
    if (!response.ok || !data) return;

    const settingsEl = document.getElementById('path-settings');
    const secretsEl = document.getElementById('path-secrets');
    const certEl = document.getElementById('path-certificate');
    const logsEl = document.getElementById('path-logs');

    if (settingsEl) settingsEl.textContent = data.settingsFile || '-';
    if (secretsEl) secretsEl.textContent = data.secretsFile || '-';
    if (certEl) certEl.textContent = data.certificateFile || '-';
    if (logsEl) logsEl.textContent = data.logDirectory || '-';
  } catch (e) {
    log.error(() => `Failed to load paths: ${String(e)}`);
  }
}

function bindOverlayToggle(): void {
  const toggle = document.getElementById('diag-overlay-toggle') as HTMLInputElement | null;
  if (!toggle) return;

  const saved = localStorage.getItem('latency-overlay-enabled') === 'true';
  toggle.checked = saved;
  if (saved) {
    enableLatencyOverlay();
  }

  toggle.addEventListener('change', () => {
    if (toggle.checked) {
      enableLatencyOverlay();
      localStorage.setItem('latency-overlay-enabled', 'true');
    } else {
      disableLatencyOverlay();
      localStorage.removeItem('latency-overlay-enabled');
    }
  });

  const outputRttEl = document.getElementById('diag-output-rtt');
  if (outputRttEl) {
    onOutputRtt((_sessionId, rtt) => {
      outputRttEl.textContent = `${rtt.toFixed(1)} ms`;
    });
  }
}

function setBaseTerminalTransportDiagnostics(
  transport: TerminalTransportDiagnostics | null,
  browser: ReturnType<typeof getBrowserTransportSnapshot>,
): void {
  setDiagValue('diag-source-seq', transport?.sourceSeq ?? '-');
  setDiagValue('diag-mux-received-seq', transport?.muxReceivedSeq ?? '-');
  setDiagValue('diag-browser-received-seq', formatSequence(browser?.receivedSeq ?? null));
  setDiagValue('diag-browser-rendered-seq', formatSequence(browser?.renderedSeq ?? null));
  setDiagValue('diag-mthost-ipc-queued-seq', transport?.mthostIpcQueuedSeq ?? '-');
  setDiagValue('diag-mthost-ipc-flushed-seq', transport?.mthostIpcFlushedSeq ?? '-');
}

function setLiveTransportDiagnostics(
  transport: TerminalTransportDiagnostics,
  browser: ReturnType<typeof getBrowserTransportSnapshot>,
): void {
  const backlog = `${transport.ipcBacklogFrames}f, ${transport.ipcBacklogBytes}b, age ${transport.oldestBacklogAgeMs}ms`;
  const replayReason = transport.lastReplayReason ?? 'none';
  const replay = `${transport.lastReplayBytes}b (${replayReason})`;
  const lossReason = browser?.lastDataLossReason ?? transport.lastDataLossReason ?? 'none';
  const lossCount = Math.max(browser?.dataLossCount ?? 0, transport.dataLossCount);

  setDiagValue('diag-ipc-backlog', backlog);
  setDiagValue('diag-last-replay', replay);
  setDiagValue('diag-reconnect-count', `${transport.reconnectCount}`);
  setDiagValue('diag-data-loss', `${lossCount} (${lossReason})`);
}

function setBrowserOnlyTransportDiagnostics(
  browser: ReturnType<typeof getBrowserTransportSnapshot>,
): void {
  setDiagValue('diag-ipc-backlog', '-');
  setDiagValue(
    'diag-last-replay',
    browser?.lastReplayReason ? `browser (${browser.lastReplayReason})` : '-',
  );
  setDiagValue('diag-reconnect-count', '-');
  setDiagValue(
    'diag-data-loss',
    browser ? `${browser.dataLossCount} (${browser.lastDataLossReason ?? 'none'})` : '-',
  );
}

function updateTerminalTransportDiagnostics(
  sessionId: string,
  transport: TerminalTransportDiagnostics | null,
): void {
  const browser = getBrowserTransportSnapshot(sessionId);

  setBaseTerminalTransportDiagnostics(transport, browser);

  if (transport) {
    setLiveTransportDiagnostics(transport, browser);
    return;
  }

  setBrowserOnlyTransportDiagnostics(browser);
}

function clearTerminalTransportDiagnostics(): void {
  [
    'diag-source-seq',
    'diag-mux-received-seq',
    'diag-browser-received-seq',
    'diag-browser-rendered-seq',
    'diag-mthost-ipc-queued-seq',
    'diag-mthost-ipc-flushed-seq',
    'diag-ipc-backlog',
    'diag-last-replay',
    'diag-reconnect-count',
    'diag-data-loss',
  ].forEach((id) => {
    setDiagValue(id, '-');
  });
}

function setDiagValue(id: string, value: string): void {
  const element = document.getElementById(id);
  if (element) {
    element.textContent = value;
  }
}

function bindBrowserSessionTree(): void {
  const root = document.getElementById('diag-browser-session-tree');
  if (!root) {
    return;
  }

  const render = (): void => {
    renderBrowserSessionTree(root, $browserSessions.get());
  };

  render();
  $browserSessions.subscribe(render);
}

function renderBrowserSessionTree(root: HTMLElement, browsers: BrowserSessionStatus[]): void {
  if (browsers.length === 0) {
    root.innerHTML = `<div class="diag-browser-empty">${escapeHtml(
      t('settings.diagnostics.browserSessionTreeEmpty'),
    )}</div>`;
    return;
  }

  root.innerHTML = browsers.map(renderBrowserSessionNode).join('');
}

function renderBrowserSessionNode(browser: BrowserSessionStatus): string {
  const sessionId = browser.activeSessionId;
  const session = sessionId ? getSession(sessionId) : undefined;
  const display = session ? getSessionDisplayInfo(session) : null;
  const sessionLabel = display
    ? display.secondary
      ? `${display.primary} — ${display.secondary}`
      : display.primary
    : (sessionId ?? t('settings.diagnostics.browserSessionNone'));
  const surface = browser.activeSurface ?? session?.surface ?? 'unknown';
  const browserLabel =
    browser.browserId.length > 18 ? browser.browserId.slice(0, 18) : browser.browserId;
  const role = browser.isMain
    ? t('settings.diagnostics.browserSessionLeading')
    : t('settings.diagnostics.browserSessionFollowing');
  const activity = browser.isActive
    ? t('settings.diagnostics.browserSessionActive')
    : t('settings.diagnostics.browserSessionIdle');

  return `
    <article class="diag-browser-node ${browser.isMain ? 'is-leading' : 'is-following'}">
      <div class="diag-browser-node-header">
        <span class="diag-browser-role">${escapeHtml(role)}</span>
        <code>${escapeHtml(browserLabel)}</code>
        <span class="diag-browser-activity">${escapeHtml(activity)}</span>
      </div>
      <div class="diag-browser-children">
        <div><span>${escapeHtml(t('settings.diagnostics.browserSessionActiveSession'))}</span><strong>${escapeHtml(
          sessionLabel,
        )}</strong></div>
        <div><span>${escapeHtml(t('settings.diagnostics.browserSessionSurface'))}</span><code>${escapeHtml(
          surface,
        )}</code></div>
        <div><span>${escapeHtml(t('settings.diagnostics.browserSessionConnections'))}</span><code>${browser.activeConnectionCount}/${browser.connectionCount}</code></div>
      </div>
    </article>`;
}

function escapeHtml(value: string): string {
  return value.replace(/[&<>"']/g, (char) => {
    switch (char) {
      case '&':
        return '&amp;';
      case '<':
        return '&lt;';
      case '>':
        return '&gt;';
      case '"':
        return '&quot;';
      default:
        return '&#39;';
    }
  });
}

function formatSequence(value: bigint | null): string {
  return value === null ? '-' : value.toString();
}

function bindReloadSettingsButton(): void {
  const btn = document.getElementById('btn-reload-settings');
  if (!btn) return;

  btn.addEventListener('click', () => {
    void (async () => {
      btn.classList.add('spinning');
      try {
        const { response } = await reloadSettings();
        if (response.ok) {
          reloadAppShell();
        }
      } catch (e) {
        log.error(() => `Failed to reload settings: ${String(e)}`);
      } finally {
        btn.classList.remove('spinning');
      }
    })();
  });
}

function bindRestartButton(): void {
  const btn = document.getElementById('btn-restart-server') as HTMLButtonElement | null;
  if (!btn) return;

  btn.addEventListener('click', () => {
    void (async () => {
      const confirmed = await showConfirm(t('settings.diagnostics.restartConfirm'), {
        title: t('settings.diagnostics.restartServer'),
        confirmLabel: t('settings.diagnostics.restartServer'),
        danger: true,
      });
      if (!confirmed) return;

      btn.disabled = true;

      try {
        await restartServer();
      } catch {
        // Server may have already shut down before responding — that's expected
      }

      beginServerRestartLifecycle('restart');
    })();
  });
}

function bindGitOverlayToggle(): void {
  const toggle = document.getElementById('diag-git-overlay-toggle') as HTMLInputElement | null;
  if (!toggle) return;

  const saved = localStorage.getItem('git-overlay-enabled') === 'true';
  toggle.checked = saved;
  if (saved) {
    enableGitStatusOverlay();
  }

  toggle.addEventListener('change', () => {
    if (toggle.checked) {
      enableGitStatusOverlay();
      localStorage.setItem('git-overlay-enabled', 'true');
    } else {
      disableGitStatusOverlay();
      localStorage.removeItem('git-overlay-enabled');
    }
  });
}

function bindTerminalBufferDumpButton(): void {
  const btn = document.getElementById('btn-dump-terminal-buffer') as HTMLButtonElement | null;
  const status = document.getElementById('diag-terminal-buffer-dump-status');
  if (!btn) return;

  btn.addEventListener('click', () => {
    void (async () => {
      btn.disabled = true;
      if (status) {
        status.textContent = 'Preparing...';
      }

      try {
        const filename = await downloadActiveTerminalBufferDump();
        if (status) {
          status.textContent = filename;
        }
      } catch (error) {
        log.error(() => `Failed to download terminal buffer dump: ${String(error)}`);
        if (status) {
          status.textContent = 'Download failed';
        }
      } finally {
        btn.disabled = false;
      }
    })();
  });
}

function bindTerminalKeyLogControls(): void {
  const toggle = document.getElementById('diag-terminal-key-log-toggle') as HTMLInputElement | null;
  const clearBtn = document.getElementById(
    'btn-clear-terminal-key-log',
  ) as HTMLButtonElement | null;
  const output = document.getElementById('diag-terminal-key-log');

  if (!toggle || !clearBtn || !output) {
    return;
  }

  toggle.checked = isTerminalKeyLogEnabled();

  const render = (): void => {
    output.textContent = getTerminalKeyLogLines().join('\n');
    output.scrollTop = output.scrollHeight;
  };

  render();
  subscribeTerminalKeyLog(render);

  toggle.addEventListener('change', () => {
    setTerminalKeyLogEnabled(toggle.checked);
    render();
  });

  clearBtn.addEventListener('click', () => {
    clearTerminalKeyLog();
  });
}
