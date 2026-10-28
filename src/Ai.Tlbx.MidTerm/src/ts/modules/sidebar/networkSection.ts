/**
 * Network Section Module
 *
 * Handles the collapsible "Network & Remote Access" section
 * in the sidebar footer.
 */

import { createLogger } from '../logging';
import { t } from '../i18n';
import { openSettings } from '../settings/panel';
import { getActiveSettingsTab, switchSettingsTab } from '../settings/tabs';
import { initConnectSetup, isLocalConnectSetup } from '../settings/connectSetup';

const log = createLogger('networkSection');

export function initNetworkSection(): void {
  const section = document.getElementById('network-section');
  const toggleBtn = document.getElementById('btn-toggle-network');
  const connectSetup = document.getElementById('connect-setup-link');
  if (connectSetup) {
    connectSetup.hidden = !isLocalConnectSetup();
    connectSetup.addEventListener('click', (event) => {
      event.preventDefault();
      if (!isLocalConnectSetup()) return;
      void openConnectSettings();
    });
    const url = new URL(window.location.href);
    if (!connectSetup.hidden && url.searchParams.get('connect-setup') === '1') {
      connectSetup.click();
      url.searchParams.delete('connect-setup');
      window.history.replaceState(window.history.state, '', url);
    }
  }

  if (!section || !toggleBtn) {
    log.info(() => 'Network section elements not found');
    return;
  }

  const isUntrusted = !window.isSecureContext;

  if (isUntrusted) {
    section.classList.add('untrusted');
    const titleEl = section.querySelector<HTMLElement>('.network-section-title');
    if (titleEl) {
      titleEl.dataset.notTrusted = t('network.notTrusted');
    }

    const trustLink = document.getElementById('trust-link');
    if (trustLink) {
      trustLink.classList.add('trust-warning');
      const helpText = document.createElement('span');
      helpText.className = 'trust-help-text';
      helpText.textContent = t('network.enableClipboard');
      trustLink.parentElement?.insertBefore(helpText, trustLink.nextSibling);
    }
  }

  section.classList.add('collapsed');

  toggleBtn.addEventListener('click', () => {
    const nowCollapsed = section.classList.toggle('collapsed');
    log.info(() => `Network section ${nowCollapsed ? 'collapsed' : 'expanded'}`);
  });

  log.info(() => `Network section initialized (untrusted=${isUntrusted})`);
}

async function openConnectSettings(): Promise<void> {
  openSettings();
  switchSettingsTab('security');
  await initConnectSetup();
  const panel = document.getElementById('security-section');
  if (getActiveSettingsTab() === 'security' && panel?.checkVisibility()) {
    panel.scrollIntoView({ block: 'start' });
  }
}
