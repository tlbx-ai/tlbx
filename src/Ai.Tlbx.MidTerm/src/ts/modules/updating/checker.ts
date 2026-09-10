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
import { showAlert, showConfirm } from '../../utils/dialog';
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
  syncUpdateButtons();
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
let updateRequestPending = false;

function syncUpdateButtons(): void {
  for (const button of document.querySelectorAll<HTMLButtonElement>(
    '#update-panel .update-btn, #update-cards .btn-update, #btn-full-update',
  )) {
    button.disabled = updateRequestPending;
    button.textContent = updateRequestPending
      ? t('update.updating')
      : button.id === 'btn-full-update'
        ? t('update.fullUpdate')
        : t('sidebar.updateRestart');
  }
}

function updateFailureDetails(error: unknown, response: Response): string {
  // The API client has already consumed the response body.
  if (typeof error === 'string') return error;
  const problem = error as { detail?: string; title?: string } | undefined;
  return problem?.detail || problem?.title || `HTTP ${response.status} ${response.statusText}`;
}

async function runUpdate(source?: string, forceFull = false): Promise<void> {
  if (updateRequestPending) return;
  updateRequestPending = true;
  syncUpdateButtons();
  try {
    if (
      forceFull &&
      !(await showConfirm(t('update.fullUpdateConfirm'), {
        title: t('update.fullUpdate'),
        confirmLabel: t('sidebar.updateRestart'),
      }))
    )
      return;

    const { response, error } = await apiApplyUpdate(source, forceFull);
    if (!response.ok) {
      throw new Error(updateFailureDetails(error, response));
    }
    setPendingChangelogFlag();
    const info = $updateInfo.get();
    const target = source === 'local' ? info?.localUpdate : info;
    waitForServerAndReload(forceFull ? 'full' : (target?.type ?? null));
  } catch (error: unknown) {
    log.error(() => `Update failed: ${String(error)}`);
    const details =
      error instanceof Error && error.name === 'TimeoutError'
        ? t('update.requestTimeout')
        : String(error);
    await showAlert(t('update.failed'), { details });
  } finally {
    updateRequestPending = false;
    syncUpdateButtons();
  }
}

export function applyUpdate(): Promise<void> {
  // The server refreshes discovery; a stale browser snapshot must not swallow clicks.
  return runUpdate();
}

/** Reinstall the newest release in the selected channel, including both host runtimes. */
export function applyFullUpdate(): Promise<void> {
  return runUpdate(undefined, true);
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
        if (data.lastResult?.found) {
          lastUpdateResult = data.lastResult;
          renderUpdateResult();
        }
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
    onApply: () => void applyUpdate(),
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
    onApply: () => void applyLocalUpdate(),
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
  syncUpdateButtons();
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
    opts.onApply();
  });

  return card;
}

/**
 * Apply local update from C:\temp\mtlocalrelease
 */
export function applyLocalUpdate(): Promise<void> {
  return runUpdate('local');
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

  void applyUpdate();
}

const PENDING_CHANGELOG_KEY = 'mt-pending-changelog';
const CHANGELOG_SHOWN_KEY = 'mt-changelog-shown-version';

function setPendingChangelogFlag(): void {
  const settings = $currentSettings.get();
  if (settings?.showChangelogAfterUpdate !== false) {
    try {
      localStorage.setItem(PENDING_CHANGELOG_KEY, '1');
    } catch {
      // Optional changelog storage must never prevent update/restart recovery.
    }
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
    ${lastUpdateResult.details ? `<div class="update-result-message">${escapeHtml(lastUpdateResult.details)}</div>` : ''}
    ${lastUpdateResult.rollbackAttempted ? `<div class="update-result-message">${t('update.rollbackAttempted')}</div>` : ''}
    <button class="btn-secondary btn-view-log">${t('update.viewUpdateLog')}</button>
    <button class="btn-secondary btn-dismiss-result">${t('sidebar.updateDismiss')}</button>
  `;

  container.querySelector('.btn-view-log')?.addEventListener('click', () => {
    void showUpdateLog();
  });
  container.querySelector('.btn-dismiss-result')?.addEventListener('click', () => {
    deleteUpdateResult()
      .then(() => {
        lastUpdateResult = null;
        renderUpdateResult();
      })
      .catch((e: unknown) => {
        log.warn(() => `Failed to dismiss update result: ${String(e)}`);
      });
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
