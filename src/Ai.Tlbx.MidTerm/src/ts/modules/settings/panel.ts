/**
 * Settings Panel Module
 *
 * Handles the settings panel UI visibility, system status display,
 * and health check functionality.
 */

import type { FirewallRuleStatusResponse, SystemHealth } from '../../types';
import { sessionTerminals, dom } from '../../state';
import { $settingsOpen, $activeSessionId, $sessionList, $windowsBuildNumber } from '../../stores';
import { fetchSettings, unbindSettingsAutoSave } from './persistence';
import { initSettingsTabs } from './tabs';
import { stopLatencyMeasurement } from '../diagnostics';
import { refreshTerminalPresentation } from '../terminal/scaling';
import { createLogger } from '../logging';
import { bindApiKeyControls, fetchApiKeys } from './apiKeys';
import {
  addFirewallRule,
  getFirewallRuleStatus,
  getHealth,
  getSharePacket,
  regenerateCertificate,
  removeFirewallRule,
} from '../../api/client';
import { showAlert, showConfirm } from '../../utils/dialog';
import { t } from '../i18n';
import { registerBackButtonLayer } from '../navigation/backButtonGuard';
import { beginServerRestartLifecycle } from '../updating';
import { bindTerminalLinkSecuritySetting } from '../terminal/linkConfirmation';

const log = createLogger('settings');
let releaseBackButtonLayer: (() => void) | null = null;

/**
 * Close the mobile sidebar
 */
function closeSidebar(): void {
  const app = dom.app;
  if (app) app.classList.remove('sidebar-open');
}

/**
 * Toggle the settings panel visibility
 */
export function toggleSettings(): void {
  if ($settingsOpen.get()) {
    closeSettings();
  } else {
    openSettings();
  }
}

/**
 * Open the settings panel
 */
export function openSettings(): void {
  if (!releaseBackButtonLayer) {
    releaseBackButtonLayer = registerBackButtonLayer(closeSettings);
  }

  $settingsOpen.set(true);
  if (dom.settingsBtn) dom.settingsBtn.classList.add('active');
  closeSidebar();

  const activeId = $activeSessionId.get();
  if (activeId) {
    const state = sessionTerminals.get(activeId);
    if (state) state.container.classList.add('hidden');
  }

  if (dom.emptyState) dom.emptyState.classList.add('hidden');
  if (dom.settingsView) dom.settingsView.classList.remove('hidden');

  initSettingsTabs();
  bindTerminalLinkSecuritySetting();
  void fetchSettings();
  fetchSystemStatus();
  fetchCertificateInfo();
  bindRegenerateCertButton();
  bindFirewallButtons();
  bindApiKeyControls();
  void fetchFirewallRuleStatus();
  void fetchApiKeys();
}

/**
 * Close the settings panel
 */
export function closeSettings(): void {
  if (!$settingsOpen.get()) return;
  releaseBackButtonLayer?.();
  releaseBackButtonLayer = null;

  unbindSettingsAutoSave();
  stopLatencyMeasurement();
  $settingsOpen.set(false);
  if (dom.settingsBtn) dom.settingsBtn.classList.remove('active');
  if (dom.settingsView) dom.settingsView.classList.add('hidden');

  const activeId = $activeSessionId.get();
  if (activeId) {
    const state = sessionTerminals.get(activeId);
    if (state) {
      state.container.classList.remove('hidden');
      requestAnimationFrame(() => {
        refreshTerminalPresentation(activeId, state);
        state.terminal.focus();
      });
    }
  } else if ($sessionList.get().length === 0 && dom.emptyState) {
    dom.emptyState.classList.remove('hidden');
  }
}

/**
 * Format uptime seconds into human-readable string
 */
export function formatUptime(seconds: number): string {
  if (seconds < 60) return `${seconds}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;

  const hours = Math.floor(seconds / 3600);
  const mins = Math.floor((seconds % 3600) / 60);

  if (hours < 24) return `${hours}h ${mins}m`;

  const days = Math.floor(hours / 24);
  return `${days}d ${hours % 24}h`;
}

/**
 * Update the ttyhost version mismatch warning banner
 */
export function updateTtyHostWarning(health: SystemHealth): void {
  let banner = document.getElementById('ttyhost-warning');

  if (!banner) {
    banner = document.createElement('div');
    banner.id = 'ttyhost-warning';
    banner.className = 'warning-banner';
    const header = document.querySelector('.app-header');
    if (header && header.parentNode) {
      header.parentNode.insertBefore(banner, header.nextSibling);
    }
  }

  if (health.ttyHostVersion !== '' && health.ttyHostCompatible === false) {
    banner.innerHTML = `<strong>Version mismatch:</strong> mmttyhost is ${health.ttyHostVersion ?? ''}, expected ${health.ttyHostExpected ?? ''}. Terminals may not work correctly. Please update mmttyhost.exe or restart the service.`;
    banner.style.display = 'block';
  } else {
    banner.style.display = 'none';
  }
}

/**
 * Fetch and display system status in the settings panel
 */
export function fetchSystemStatus(): void {
  const container = document.getElementById('system-status-content');
  if (!container) return;

  getHealth()
    .then(({ data }) => {
      const health = data as SystemHealth;
      const statusClass = health.healthy ? 'status-healthy' : 'status-error';
      const statusText = health.healthy ? 'Healthy' : 'Unhealthy';
      const uptimeStr = formatUptime(health.uptimeSeconds);

      let ttyHostHtml = '';
      if (health.ttyHostVersion !== '') {
        const versionClass = health.ttyHostCompatible ? '' : 'status-error';
        ttyHostHtml =
          `<div class="status-detail-row">` +
          `<span class="detail-label">mmttyhost</span>` +
          `<span class="detail-value ${versionClass}">` +
          (health.ttyHostVersion ?? '') +
          (health.ttyHostCompatible ? '' : ` expected ${health.ttyHostExpected ?? ''}`) +
          `</span>` +
          `</div>`;
      }

      container.innerHTML =
        `<div class="status-grid">` +
        `<div class="status-item">` +
        `<span class="status-label">Status</span>` +
        `<span class="status-value ${statusClass}">${statusText}</span>` +
        `</div>` +
        `<div class="status-item">` +
        `<span class="status-label">Mode</span>` +
        `<span class="status-value">${health.mode || ''}</span>` +
        `</div>` +
        `<div class="status-item">` +
        `<span class="status-label">Sessions</span>` +
        `<span class="status-value">${String(health.sessionCount)}</span>` +
        `</div>` +
        `<div class="status-item">` +
        `<span class="status-label">Uptime</span>` +
        `<span class="status-value">${uptimeStr}</span>` +
        `</div>` +
        `</div>` +
        `<div class="status-details">` +
        `<div class="status-detail-row">` +
        `<span class="detail-label">Platform</span>` +
        `<span class="detail-value">${health.platform || ''}</span>` +
        `</div>` +
        `<div class="status-detail-row">` +
        `<span class="detail-label">Process ID</span>` +
        `<span class="detail-value">${health.webProcessId || ''}</span>` +
        `</div>` +
        ttyHostHtml +
        `</div>`;

      updateTtyHostWarning(health);
    })
    .catch((err: unknown) => {
      const message = err instanceof Error ? err.message : String(err);
      container.innerHTML =
        '<div class="status-error-msg">Failed to load system status: ' + message + '</div>';
    });
}

/**
 * Check system health on startup for version mismatches
 */
export function checkSystemHealth(): void {
  getHealth()
    .then(({ data }) => {
      const health = data as SystemHealth;
      updateTtyHostWarning(health);
      if (health.windowsBuildNumber != null) {
        $windowsBuildNumber.set(health.windowsBuildNumber);
      }
    })
    .catch((e: unknown) => {
      log.warn(() => `Failed to check system health: ${String(e)}`);
    });
}

/**
 * Fetch and display certificate info in the Security settings tab
 */
export function fetchCertificateInfo(): void {
  const fingerprintEl = document.getElementById('settings-cert-fingerprint');
  const validityEl = document.getElementById('settings-cert-validity');

  if (!fingerprintEl && !validityEl) return;

  getSharePacket()
    .then(({ data, response }) => {
      if (!response.ok || !data) throw new Error('Failed to fetch certificate info');
      if (fingerprintEl && data.certificate.fingerprintFormatted) {
        fingerprintEl.textContent = data.certificate.fingerprintFormatted;
      }
      if (validityEl && data.certificate.notAfter) {
        const notAfter = new Date(data.certificate.notAfter);
        const dateOpts: Intl.DateTimeFormatOptions = {
          year: 'numeric',
          month: 'short',
          day: 'numeric',
        };
        validityEl.textContent = 'expires ' + notAfter.toLocaleDateString(undefined, dateOpts);
      }
    })
    .catch(() => {
      if (fingerprintEl) fingerprintEl.textContent = 'Error loading';
      if (validityEl) validityEl.textContent = '-';
    });
}

let regenerateCertBound = false;
let firewallButtonsBound = false;

function bindRegenerateCertButton(): void {
  const btn = document.getElementById('btn-regenerate-cert') as HTMLButtonElement | null;
  if (!btn || regenerateCertBound) return;
  regenerateCertBound = true;

  btn.addEventListener('click', () => {
    void (async () => {
      const confirmed = await showConfirm(t('settings.security.regenerateConfirm'), {
        title: t('settings.security.regenerateCertificate'),
        confirmLabel: t('settings.security.regenerate'),
        danger: true,
      });
      if (!confirmed) return;

      btn.disabled = true;

      try {
        await regenerateCertificate();
      } catch {
        // Server may shut down before responding
      }

      beginServerRestartLifecycle('certificate');
    })();
  });
}

async function fetchFirewallRuleStatus(): Promise<void> {
  const section = document.getElementById('windows-firewall-section');
  const statusEl = document.getElementById('settings-firewall-status');
  const portEl = document.getElementById('settings-firewall-port');
  const bindEl = document.getElementById('settings-firewall-bind');
  const noteEl = document.getElementById('settings-firewall-note');
  const addBtn = document.getElementById('btn-add-firewall-rule') as HTMLButtonElement | null;
  const removeBtn = document.getElementById('btn-remove-firewall-rule') as HTMLButtonElement | null;

  if (!section || !statusEl || !portEl || !bindEl || !noteEl || !addBtn || !removeBtn) return;

  statusEl.textContent = t('settings.security.checking');
  portEl.textContent = '-';
  bindEl.textContent = '-';

  try {
    const { data, response } = await getFirewallRuleStatus();
    if (!response.ok || !data) {
      throw new Error(t('settings.security.firewallError'));
    }

    applyFirewallStatus(section, data, statusEl, portEl, bindEl, noteEl, addBtn, removeBtn);
  } catch (err) {
    section.classList.remove('hidden');
    statusEl.textContent = t('settings.security.firewallError');
    noteEl.textContent =
      err instanceof Error ? err.message : t('settings.security.firewallActionFailed');
    addBtn.disabled = true;
    removeBtn.disabled = true;
  }
}

function applyFirewallStatus(
  section: HTMLElement,
  status: FirewallRuleStatusResponse,
  statusEl: HTMLElement,
  portEl: HTMLElement,
  bindEl: HTMLElement,
  noteEl: HTMLElement,
  addBtn: HTMLButtonElement,
  removeBtn: HTMLButtonElement,
): void {
  if (!status.supported) {
    section.classList.add('hidden');
    return;
  }

  section.classList.remove('hidden');
  portEl.textContent = String(status.port);
  bindEl.textContent = status.loopbackOnly
    ? t('settings.security.firewallExposureLoopback')
    : t('settings.security.firewallExposureNetwork');

  if (
    status.rulePresent &&
    status.ruleEnabled &&
    status.matchesCurrentPort &&
    status.matchesCurrentProgram
  ) {
    statusEl.textContent = t('settings.security.firewallStatusActive');
  } else if (status.rulePresent) {
    statusEl.textContent = t('settings.security.firewallStatusOutOfDate');
  } else {
    statusEl.textContent = t('settings.security.firewallStatusMissing');
  }

  if (!status.canManage) {
    noteEl.textContent = t('settings.security.firewallAdminRequired');
  } else if (status.loopbackOnly) {
    noteEl.textContent = t('settings.security.firewallLoopbackHint');
  } else {
    noteEl.textContent = t('settings.security.firewallHint');
  }

  addBtn.disabled = !status.canManage;
  removeBtn.disabled = !status.canManage || !status.rulePresent;
}

function bindFirewallButtons(): void {
  if (firewallButtonsBound) return;

  const addBtn = document.getElementById('btn-add-firewall-rule') as HTMLButtonElement | null;
  const removeBtn = document.getElementById('btn-remove-firewall-rule') as HTMLButtonElement | null;
  if (!addBtn || !removeBtn) return;

  firewallButtonsBound = true;

  addBtn.addEventListener('click', () => {
    void (async () => {
      const previousText = addBtn.textContent;
      addBtn.disabled = true;
      addBtn.textContent = t('settings.security.firewallAdding');

      try {
        const { response } = await addFirewallRule();
        if (!response.ok) {
          throw new Error(await response.text());
        }
      } catch (err) {
        const message =
          err instanceof Error ? err.message : t('settings.security.firewallActionFailed');
        await showAlert(message, { title: t('settings.security.firewallTitle') });
      } finally {
        addBtn.textContent = previousText;
        await fetchFirewallRuleStatus();
      }
    })();
  });

  removeBtn.addEventListener('click', () => {
    void (async () => {
      const previousText = removeBtn.textContent;
      removeBtn.disabled = true;
      removeBtn.textContent = t('settings.security.firewallRemoving');

      try {
        const { response } = await removeFirewallRule();
        if (!response.ok) {
          throw new Error(await response.text());
        }
      } catch (err) {
        const message =
          err instanceof Error ? err.message : t('settings.security.firewallActionFailed');
        await showAlert(message, { title: t('settings.security.firewallTitle') });
      } finally {
        removeBtn.textContent = previousText;
        await fetchFirewallRuleStatus();
      }
    })();
  });
}
