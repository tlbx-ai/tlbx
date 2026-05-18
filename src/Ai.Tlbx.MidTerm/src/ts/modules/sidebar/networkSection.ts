/**
 * Network Section Module
 *
 * Handles the collapsible "Network & Remote Access" section
 * in the sidebar footer.
 */

import { createLogger } from '../logging';
import { t } from '../i18n';

const log = createLogger('networkSection');

export function initNetworkSection(): void {
  const section = document.getElementById('network-section');
  const toggleBtn = document.getElementById('btn-toggle-network');

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
