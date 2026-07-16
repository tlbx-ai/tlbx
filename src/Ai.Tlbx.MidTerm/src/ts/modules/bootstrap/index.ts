/**
 * Bootstrap Module
 *
 * Handles consolidated startup data fetching from /api/bootstrap.
 * Replaces multiple individual API calls with a single request.
 */

import {
  getBootstrap,
  getBootstrapLogin,
  type BootstrapResponse,
  type BootstrapLoginResponse,
  type NetworkInterfaceDto,
  type ShellInfoDto,
  type UpdateResult,
  type UserInfo,
} from '../../api/client';
import { JS_BUILD_VERSION } from '../../constants';
import { $currentSettings, $authStatus, $serverHostname, $voiceServerPassword } from '../../stores';
import { createLogger } from '../logging';
import {
  populateSettingsForm,
  populateUserDropdown,
  populateVersionInfo,
  applySettingsToTerminals,
  bindDevModeToggle,
} from '../settings/persistence';
import { updateSecurityWarning, updatePasswordStatus } from '../auth/status';
import { setDevMode, setVoiceChatEnabled, setVoiceSectionVisible } from '../sidebar/voiceSection';
import { checkVoiceServerHealth } from '../voice';
import { consumePendingChangelogFlag } from '../updating/checker';
import { showChangelog } from '../updating/changelog';
import { escapeHtml } from '../../utils';
import { t } from '../i18n';

const log = createLogger('bootstrap');

let bootstrapData: BootstrapResponse | null = null;
let shellsList: ShellInfoDto[] = [];

/**
 * Get the cached bootstrap data
 */
export function getBootstrapData(): BootstrapResponse | null {
  return bootstrapData;
}

/**
 * Get the list of available shells
 */
export function getShells(): ShellInfoDto[] {
  return shellsList;
}

/**
 * Fetch bootstrap data and initialize all startup state.
 * Replaces: /api/auth/status, /api/version, /api/health, /api/settings, /api/networks, /api/users, /api/shells, /api/update/result
 */
export async function fetchBootstrap(): Promise<BootstrapResponse | null> {
  try {
    const { data, response } = await getBootstrap();

    if (response.status === 401) {
      window.location.href = '/login.html';
      return null;
    }

    if (!data) {
      throw new Error(`Bootstrap failed: ${response.status}`);
    }

    bootstrapData = data;
    shellsList = data.shells;

    // Initialize settings
    $currentSettings.set(data.settings);
    const users = data.users.map((u: UserInfo) => ({
      username: u.username,
      displayName: u.username,
    }));
    populateUserDropdown(users, data.settings.runAsUser ?? null);
    populateSettingsForm(data.settings);
    populateVersionInfo(
      data.version,
      data.ttyHostVersion || null,
      JS_BUILD_VERSION,
      data.devMode,
      data.codeSigned,
    );
    bindDevModeToggle();

    // Initialize auth status
    $authStatus.set({
      authenticationEnabled: data.auth.authenticationEnabled,
      passwordSet: data.auth.passwordSet,
    });
    $serverHostname.set(data.hostname);
    $voiceServerPassword.set(data.voicePassword ?? null);
    updateSecurityWarning();
    updatePasswordStatus();

    // Render version display
    renderVersion(data.version);

    // Render network interfaces
    renderNetworks(data.networks);

    // Populate shell dropdown
    populateShellDropdown(data.shells, data.settings.defaultShell ?? '');

    // Handle update result if present
    if (data.updateResult?.found) {
      handleUpdateResult(data.updateResult, data.version);
    }

    // Check system health (TtyHost compatibility)
    checkTtyHostHealth(data);

    // Render reinstall hint with platform-specific install command
    renderReinstallHint(data.platform);

    // Feature flags - enable/disable UI features
    setVoiceChatEnabled(data.features.voiceChat);

    // Dev mode - shows sync button in voice section
    setDevMode(data.devMode);

    // Check voice server availability (only relevant if voice chat is enabled)
    if (data.features.voiceChat) {
      void checkVoiceServerHealth().then((available) => {
        setVoiceSectionVisible(available);
      });
    }

    // Apply settings to any terminals that were created before settings loaded
    applySettingsToTerminals();

    log.info(() => 'Bootstrap complete');
    return data;
  } catch (e) {
    log.error(() => `Bootstrap failed: ${String(e)}`);
    return null;
  }
}

/**
 * Render version display in UI
 */
function renderVersion(version: string): void {
  const cleanVersion = version.replace(/[+-][a-f0-9]+$/i, '');
  const el = document.getElementById('app-version');
  if (el) el.textContent = 'v' + cleanVersion;
}

/**
 * Render network interfaces list
 */
function renderNetworks(networks: NetworkInterfaceDto[]): void {
  const list = document.getElementById('network-list');
  if (!list) return;

  const protocol = location.protocol;
  const port = location.port;
  list.innerHTML = networks
    .map((n) => {
      const url = protocol + '//' + n.ip + ':' + port;
      return (
        '<div class="network-item">' +
        '<span class="network-name" title="' +
        escapeHtml(n.name) +
        '">' +
        escapeHtml(n.name) +
        '</span>' +
        '<a class="network-url" href="' +
        url +
        '" target="_blank">' +
        escapeHtml(n.ip) +
        ':' +
        port +
        '</a>' +
        '</div>'
      );
    })
    .join('');
}

/**
 * Populate shell dropdown with available shells
 */
function populateShellDropdown(shells: ShellInfoDto[], defaultShell: string): void {
  const select = document.getElementById('setting-default-shell') as HTMLSelectElement | null;
  if (!select) return;

  select.innerHTML = '';
  shells.forEach((shell) => {
    const option = document.createElement('option');
    option.value = shell.type;
    option.textContent =
      shell.displayName + (shell.isAvailable ? '' : ' ' + t('settings.options.shellNotFound'));
    option.disabled = !shell.isAvailable;
    if (shell.type === defaultShell) {
      option.selected = true;
    }
    select.appendChild(option);
  });
}

/**
 * Handle update result from previous update
 */
function handleUpdateResult(result: UpdateResult, version: string): void {
  const status = result.success ? 'success' : 'failed';
  log.info(() => `Update result: ${status} - ${result.message || 'no error'}`);

  if (result.success && consumePendingChangelogFlag(version)) {
    showChangelog(true);
  }
}

/**
 * Check TtyHost compatibility and show warning if needed
 */
function checkTtyHostHealth(data: BootstrapResponse): void {
  const warning = document.getElementById('ttyhost-warning');
  if (!warning) return;

  if (data.ttyHostVersion !== '' && !data.ttyHostCompatible) {
    warning.classList.remove('hidden');
    const msgEl = warning.querySelector('.warning-message');
    if (msgEl) {
      msgEl.textContent = `TtyHost version mismatch: ${data.ttyHostVersion} (expected compatible version)`;
    }
  } else {
    warning.classList.add('hidden');
  }
}

/**
 * Render the reinstall hint with platform-specific install command.
 */
function renderReinstallHint(platform: string): void {
  const container = document.getElementById('reinstall-hint');
  if (!container) return;

  const isWindows = /win/i.test(platform);
  const cmd = isWindows
    ? 'irm https://get.tlbx.ai/install.ps1 | iex'
    : 'curl -fsSL https://get.tlbx.ai/install.sh | bash';

  container.classList.remove('hidden');
  container.innerHTML =
    '<div class="reinstall-hint-text">If an update fails, re-run the installer (preserves settings):</div>' +
    '<div class="reinstall-hint-cmd">' +
    '<code>' +
    escapeHtml(cmd) +
    '</code>' +
    '<button class="btn-copy-cmd" title="Copy to clipboard">Copy</button>' +
    '</div>';

  container.querySelector('.btn-copy-cmd')?.addEventListener('click', (e) => {
    const btn = e.target as HTMLButtonElement;
    void navigator.clipboard.writeText(cmd);
    btn.textContent = 'Copied!';
    setTimeout(() => {
      btn.textContent = 'Copy';
    }, 1500);
  });
}

/**
 * Fetch minimal bootstrap data for login page.
 */
export async function fetchBootstrapLogin(): Promise<BootstrapLoginResponse | null> {
  try {
    const { data } = await getBootstrapLogin();
    return data ?? null;
  } catch {
    return null;
  }
}
