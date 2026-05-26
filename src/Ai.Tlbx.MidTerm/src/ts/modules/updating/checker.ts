/**
 * Update Checker Module
 *
 * Handles checking for updates, rendering the update panel,
 * and applying updates with server restart.
 */

import type { UpdateInfo, UpdateResult, UpdateType } from '../../api/types';
import { $frontendRefreshState, $updateInfo, $currentSettings } from '../../stores';
import { createLogger } from '../logging';
import { escapeHtml } from '../../utils';
import { t } from '../i18n';
import {
  applyUpdate as apiApplyUpdate,
  checkUpdate,
  getUpdateResult,
  deleteUpdateResult,
  getUpdateLog,
} from '../../api/client';
import { openSettings, switchSettingsTab } from '../settings';
import { registerBackButtonLayer } from '../navigation/backButtonGuard';
import { beginServerRestartLifecycle, requestFrontendRefresh } from './runtime';

const log = createLogger('updating');

const DISMISSED_VERSION_KEY = 'mt-dismissed-update-version';
let updateUiInitialized = false;

export function initUpdateUi(): void {
  if (updateUiInitialized) {
    return;
  }

  updateUiInitialized = true;
  $updateInfo.subscribe(() => {
    renderUpdatePanel();
  });
  $currentSettings.subscribe(() => {
    renderUpdatePanel();
  });
  $frontendRefreshState.subscribe(() => {
    renderUpdatePanel();
  });
}

function renderRefreshStatePanel(panel: HTMLElement): void {
  const refreshState = $frontendRefreshState.get();
  if (!refreshState) {
    return;
  }

  panel.classList.remove('hidden');

  const dismissBtn = panel.querySelector<HTMLButtonElement>('#update-dismiss-btn');
  const headerEl = panel.querySelector('.update-header');
  const currentEl = panel.querySelector('.update-current');
  const latestEl = panel.querySelector('.update-latest');
  const noteEl = panel.querySelector('.update-note');
  const btn = panel.querySelector<HTMLButtonElement>('.update-btn');
  const changelogEl = panel.querySelector<HTMLElement>('#update-changelog-link');

  if (dismissBtn) {
    dismissBtn.hidden = refreshState.status === 'required';
  }
  if (headerEl) {
    headerEl.textContent =
      refreshState.status === 'required' ? t('update.refreshRequired') : t('update.refreshReady');
  }
  if (currentEl) currentEl.textContent = refreshState.clientVersion;
  if (latestEl) latestEl.textContent = refreshState.serverVersion;
  if (btn) {
    btn.disabled = false;
    btn.textContent = t('update.refreshUi');
  }
  if (noteEl) {
    noteEl.textContent =
      refreshState.status === 'required'
        ? t('update.refreshRequiredNote')
        : t('update.refreshWhenConvenient');
    noteEl.classList.add('update-note-safe');
    noteEl.classList.remove('update-note-warning');
  }
  if (changelogEl) {
    changelogEl.hidden = true;
  }
}

function shouldHideUpdatePanel(info: UpdateInfo | null): boolean {
  if (!info || !info.available) {
    return true;
  }

  const settings = $currentSettings.get();
  if (settings?.showUpdateNotification === false) {
    return true;
  }

  const dismissedVersion = localStorage.getItem(DISMISSED_VERSION_KEY);
  return dismissedVersion === info.latestVersion;
}

function renderAvailableUpdatePanel(panel: HTMLElement, info: UpdateInfo): void {
  panel.classList.remove('hidden');

  const currentEl = panel.querySelector('.update-current');
  const latestEl = panel.querySelector('.update-latest');
  const noteEl = panel.querySelector('.update-note');
  const headerEl = panel.querySelector('.update-header');
  const dismissBtn = panel.querySelector<HTMLButtonElement>('#update-dismiss-btn');
  const changelogEl = panel.querySelector<HTMLElement>('#update-changelog-link');
  const btn = panel.querySelector<HTMLButtonElement>('.update-btn');

  if (dismissBtn) {
    dismissBtn.hidden = false;
  }
  if (changelogEl) {
    changelogEl.hidden = false;
  }
  if (btn) {
    btn.disabled = false;
    btn.textContent = t('sidebar.updateRestart');
  }

  if (currentEl) currentEl.textContent = info.currentVersion;
  if (latestEl) latestEl.textContent = info.latestVersion;

  if (info.sessionsPreserved) {
    if (headerEl) headerEl.textContent = t('sidebar.quickUpdate');
    if (noteEl) {
      noteEl.textContent = t('sidebar.terminalsStayConnected');
      noteEl.classList.add('update-note-safe');
      noteEl.classList.remove('update-note-warning');
    }
    return;
  }

  if (headerEl) headerEl.textContent = t('sidebar.updateAvailable');
  if (noteEl) {
    noteEl.textContent = t('sidebar.saveWorkTerminalsClose');
    noteEl.classList.add('update-note-warning');
    noteEl.classList.remove('update-note-safe');
  }
}

/**
 * Render the update panel based on current update info
 */
export function renderUpdatePanel(): void {
  const panel = document.getElementById('update-panel');
  if (!panel) return;

  if ($frontendRefreshState.get()) {
    renderRefreshStatePanel(panel);
    renderUpdateFooterHint();
    return;
  }

  const info = $updateInfo.get();
  if (shouldHideUpdatePanel(info)) {
    panel.classList.add('hidden');
    renderUpdateFooterHint();
    return;
  }
  if (!info) {
    panel.classList.add('hidden');
    renderUpdateFooterHint();
    return;
  }

  renderAvailableUpdatePanel(panel, info);
  renderUpdateFooterHint();
}

function renderUpdateFooterHint(): void {
  const hint = document.getElementById('footer-update-hint');
  const link = document.getElementById('footer-update-link');
  if (!hint) return;

  const refreshState = $frontendRefreshState.get();
  if (refreshState) {
    if (link) {
      link.textContent = t('update.refreshUi');
    }
    hint.classList.add('hidden');
    return;
  }

  const info = $updateInfo.get();
  const settings = $currentSettings.get();
  const masterDisabled = settings?.showUpdateNotification === false;
  const dismissed = localStorage.getItem(DISMISSED_VERSION_KEY);
  const versionDismissed = dismissed === info?.latestVersion;

  // Show footer hint when update exists but panel is hidden (dismissed or master off)
  if (info?.available && (masterDisabled || versionDismissed)) {
    if (link) {
      link.textContent = t('sidebar.footerUpdateAvailable');
    }
    hint.classList.remove('hidden');
  } else {
    hint.classList.add('hidden');
  }
}

let footerLinkBound = false;

function openUpdateSettings(): void {
  openSettings();
  switchSettingsTab('updates');
}

export function bindFooterUpdateLink(): void {
  if (footerLinkBound) return;
  footerLinkBound = true;

  const link = document.getElementById('footer-update-link');
  if (link) {
    link.addEventListener('click', () => {
      if ($frontendRefreshState.get()) {
        requestFrontendRefresh();
        return;
      }
      openUpdateSettings();
    });
  }

  const version = document.getElementById('app-version');
  if (version) {
    version.addEventListener('click', openUpdateSettings);
    version.addEventListener('keydown', (event) => {
      if (event.key !== 'Enter' && event.key !== ' ') {
        return;
      }

      event.preventDefault();
      openUpdateSettings();
    });
  }
}

export function dismissUpdateNotification(): void {
  const panel = document.getElementById('update-panel');
  if (panel) panel.classList.add('hidden');

  // Save the dismissed version so new versions still show notifications
  const info = $updateInfo.get();
  if (info?.latestVersion) {
    localStorage.setItem(DISMISSED_VERSION_KEY, info.latestVersion);
  }

  renderUpdateFooterHint();
}

/**
 * Apply the available update and restart the server
 */
export function applyUpdate(): void {
  const info = $updateInfo.get();
  if (!info || !info.available) return;

  const panel = document.getElementById('update-panel');
  const btn = panel?.querySelector('.update-btn') as HTMLButtonElement | null;

  if (btn) {
    btn.disabled = true;
    btn.textContent = t('update.updating');
  }

  setPendingChangelogFlag();

  apiApplyUpdate()
    .then(({ response }) => {
      if (response.ok) {
        if (btn) btn.textContent = t('update.restarting');
        waitForServerAndReload(info.type, info.latestVersion);
      } else {
        if (btn) {
          btn.disabled = false;
          btn.textContent = t('sidebar.updateRestart');
        }
        log.error(() => 'Update failed');
      }
    })
    .catch((e: unknown) => {
      if (btn) {
        btn.disabled = false;
        btn.textContent = t('sidebar.updateRestart');
      }
      log.error(() => `Update error: ${String(e)}`);
    });
}

/**
 * Start the coordinated server restart lifecycle after an update.
 */
export function waitForServerAndReload(
  updateType: UpdateType | null = null,
  expectedServerVersion: string | null = null,
): void {
  beginServerRestartLifecycle('update', { updateType, expectedServerVersion });
}

/**
 * Manually check for updates and update the UI
 */
export function checkForUpdates(e?: MouseEvent): void {
  // Prevent event bubbling that could trigger unintended handlers
  if (e) {
    e.stopPropagation();
  }

  const btn = document.getElementById('btn-check-updates') as HTMLButtonElement | null;

  if (btn) {
    btn.disabled = true;
    btn.textContent = t('update.checking');
  }

  checkUpdate()
    .then(({ data }) => {
      if (btn) {
        btn.disabled = false;
        btn.textContent = t('settings.general.checkForUpdates');
      }

      if (data) {
        $updateInfo.set(data);
        renderUpdatePanel();
        renderUpdateCards(data);
      } else {
        renderUpdateCards(null, t('update.failed'));
      }
    })
    .catch((e: unknown) => {
      if (btn) {
        btn.disabled = false;
        btn.textContent = t('settings.general.checkForUpdates');
      }
      renderUpdateCards(null, t('update.failed'));
      log.error(() => `Update check error: ${String(e)}`);
    });
}

function setNoUpdatesStatusVisibility(statusNone: HTMLElement | null, hidden: boolean): void {
  statusNone?.classList.toggle('hidden', hidden);
}

function appendUpdateCard(container: HTMLElement, options: UpdateCardOptions | null): void {
  if (!options) {
    return;
  }

  container.appendChild(createUpdateCard(options));
}

function createGitHubUpdateCard(update: UpdateInfo | null): UpdateCardOptions | null {
  if (!update?.available) {
    return null;
  }

  return {
    type: 'github',
    title: 'GitHub Release',
    version: update.latestVersion,
    sessionsPreserved: update.sessionsPreserved,
    onApply: applyUpdate,
  };
}

function createLocalUpdateCard(update: UpdateInfo | null): UpdateCardOptions | null {
  if (!update?.environment || !update.localUpdate?.available) {
    return null;
  }

  return {
    type: 'local',
    title: 'Local Build',
    version: update.localUpdate.version,
    sessionsPreserved: update.localUpdate.sessionsPreserved,
    onApply: applyLocalUpdate,
  };
}

/**
 * Render both GitHub and Local update cards
 */
function renderUpdateCards(update: UpdateInfo | null, error?: string): void {
  const container = document.getElementById('update-cards');
  const statusNone = document.getElementById('update-status-none');
  if (!container) return;

  container.innerHTML = '';

  // Error state
  if (error) {
    setNoUpdatesStatusVisibility(statusNone, true);
    container.innerHTML = `<div class="update-status-error">${error}</div>`;
    return;
  }

  const gitHubCard = createGitHubUpdateCard(update);
  const localCard = createLocalUpdateCard(update);

  if (!gitHubCard && !localCard) {
    setNoUpdatesStatusVisibility(statusNone, false);
    return;
  }

  setNoUpdatesStatusVisibility(statusNone, true);
  appendUpdateCard(container, gitHubCard);
  appendUpdateCard(container, localCard);
}

interface UpdateCardOptions {
  type: 'github' | 'local';
  title: string;
  version: string;
  sessionsPreserved: boolean;
  onApply: () => void;
}

/**
 * Create an update card element
 */
function createUpdateCard(opts: UpdateCardOptions): HTMLElement {
  const card = document.createElement('div');
  card.className = `update-card ${opts.type}`;
  card.id = `update-card-${opts.type}`;

  const warningClass = opts.sessionsPreserved ? 'safe' : 'warn';
  const warningText = opts.sessionsPreserved
    ? t('sidebar.terminalsStayConnected')
    : t('sidebar.saveWorkTerminalsClose');

  card.innerHTML = `
    <div class="update-card-header">
      <span class="update-card-title">${opts.title}</span>
      <span class="update-card-version">v${opts.version}</span>
    </div>
    <div class="update-card-footer">
      <span class="update-card-warning ${warningClass}">${warningText}</span>
      <button class="btn-update">Apply</button>
    </div>
  `;

  const btn = card.querySelector('.btn-update') as HTMLButtonElement;
  btn.addEventListener('click', () => {
    btn.disabled = true;
    btn.textContent = t('update.applying');
    opts.onApply();
  });

  return card;
}

/**
 * Apply local update from C:\temp\mtlocalrelease
 */
export function applyLocalUpdate(): void {
  setPendingChangelogFlag();
  apiApplyUpdate('local')
    .then(({ response }) => {
      const btn = document.querySelector<HTMLButtonElement>('#update-card-local .btn-update');
      const updateType = $updateInfo.get()?.localUpdate?.type ?? null;
      const expectedServerVersion = $updateInfo.get()?.localUpdate?.version ?? null;
      if (response.ok) {
        if (btn) btn.textContent = 'Restarting...';
        waitForServerAndReload(updateType, expectedServerVersion);
      } else {
        if (btn) {
          btn.disabled = false;
          btn.textContent = 'Apply';
        }
        log.error(() => 'Local update failed');
      }
    })
    .catch((e: unknown) => {
      const btn = document.querySelector<HTMLButtonElement>('#update-card-local .btn-update');
      if (btn) {
        btn.disabled = false;
        btn.textContent = 'Apply';
      }
      log.error(() => `Local update error: ${String(e)}`);
    });
}

/**
 * Handle incoming update info from WebSocket
 */
export function handleUpdateInfo(update: UpdateInfo): void {
  $updateInfo.set(update);
  renderUpdatePanel();
  renderUpdateCards(update);
}

export function handlePrimaryUpdateAction(): void {
  if ($frontendRefreshState.get()) {
    requestFrontendRefresh();
    return;
  }

  applyUpdate();
}

const PENDING_CHANGELOG_KEY = 'mt-pending-changelog';
const CHANGELOG_SHOWN_KEY = 'mt-changelog-shown-version';

function setPendingChangelogFlag(): void {
  const settings = $currentSettings.get();
  if (settings?.showChangelogAfterUpdate !== false) {
    localStorage.setItem(PENDING_CHANGELOG_KEY, '1');
  }
}

export function consumePendingChangelogFlag(currentVersion: string): boolean {
  const flag = localStorage.getItem(PENDING_CHANGELOG_KEY);
  if (flag) {
    localStorage.removeItem(PENDING_CHANGELOG_KEY);
    const shownVersion = localStorage.getItem(CHANGELOG_SHOWN_KEY);
    if (shownVersion === currentVersion) return false;
    localStorage.setItem(CHANGELOG_SHOWN_KEY, currentVersion);
    return true;
  }
  return false;
}

let lastUpdateResult: UpdateResult | null = null;

/**
 * Get the last update result for display in settings panel
 */
export function getLastUpdateResult(): UpdateResult | null {
  return lastUpdateResult;
}

/**
 * Check for update results on startup and store for display.
 */
export function checkUpdateResult(): void {
  getUpdateResult()
    .then(({ data }) => {
      if (!data?.found) return;

      // Store for settings panel display
      lastUpdateResult = data;
      renderUpdateResult();

      // Clear the result file after storing
      deleteUpdateResult().catch((e: unknown) => {
        log.verbose(() => `Failed to clear update result: ${String(e)}`);
      });
    })
    .catch((e: unknown) => {
      log.warn(() => `Failed to check update result: ${String(e)}`);
    });
}

/**
 * Render the last update result in the settings panel
 */
export function renderUpdateResult(): void {
  const container = document.getElementById('update-result');
  if (!container) return;

  if (!lastUpdateResult) {
    container.classList.add('hidden');
    return;
  }

  container.classList.remove('hidden');
  const statusClass = lastUpdateResult.success ? 'update-result-success' : 'update-result-failed';
  const statusText = lastUpdateResult.success ? t('update.success') : t('update.failed');
  const timestamp = new Date(lastUpdateResult.timestamp).toLocaleString();

  container.className = `update-result ${statusClass}`;
  container.innerHTML = `
    <div class="update-result-header">
      <span class="update-result-status">${t('update.lastUpdate')} ${statusText}</span>
      <span class="update-result-time">${timestamp}</span>
    </div>
    ${!lastUpdateResult.success ? `<div class="update-result-message">${escapeHtml(lastUpdateResult.message)}</div>` : ''}
    <button class="btn-secondary btn-view-log">${t('update.viewUpdateLog')}</button>
  `;

  container.querySelector('.btn-view-log')?.addEventListener('click', () => {
    void showUpdateLog();
  });
}

/**
 * Show the update log in a modal
 */
export async function showUpdateLog(): Promise<void> {
  const modal = document.createElement('div');
  modal.className = 'modal-overlay';
  let releaseBackButtonLayer: (() => void) | null = null;
  modal.innerHTML = `
    <div class="modal update-log-modal">
      <div class="modal-header">
        <span>${t('settings.general.updateLog')}</span>
        <button class="modal-close">&times;</button>
      </div>
      <div class="modal-body">
        <pre class="update-log-content">${t('settings.general.loading')}</pre>
      </div>
      <div class="modal-footer">
        <button class="btn-secondary btn-copy-log">${t('update.copyLog')}</button>
      </div>
    </div>
  `;

  document.body.appendChild(modal);

  const close = (): void => {
    releaseBackButtonLayer?.();
    releaseBackButtonLayer = null;
    modal.remove();
  };

  const logContent = modal.querySelector('.update-log-content') as HTMLPreElement;
  try {
    const { data, response } = await getUpdateLog();
    if (response.ok && data) {
      logContent.textContent = data;
    } else {
      logContent.textContent = t('update.noLogFound');
    }
  } catch (e) {
    logContent.textContent = `Failed to load log: ${String(e)}`;
  }

  modal.querySelector('.modal-close')?.addEventListener('click', () => {
    close();
  });
  modal.querySelector('.btn-copy-log')?.addEventListener('click', () => {
    void navigator.clipboard.writeText(logContent.textContent || '');
    const btn = modal.querySelector('.btn-copy-log') as HTMLButtonElement;
    const originalText = btn.textContent;
    btn.textContent = 'Copied!';
    setTimeout(() => {
      btn.textContent = originalText;
    }, 1500);
  });
  modal.addEventListener('click', (e) => {
    if (e.target === modal) close();
  });
  releaseBackButtonLayer = registerBackButtonLayer(close);
}
