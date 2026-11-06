import { $activeSessionId, getSession } from '../../stores';
import { t } from '../i18n';
import { closeBoardsView } from '../boards';
import { getAgentSurfaceLabel, resolveSessionSurfaceMode } from '../sessionSurface';
import { getActiveTab, getTabLabelForSession, isTabAvailable, switchTab } from './tabManager';

type MobileSessionTab = 'terminal' | 'agent' | 'files';

export function syncMobileTabActionState(): void {
  const activeSessionId = $activeSessionId.get();
  const activeTab = activeSessionId ? getActiveTab(activeSessionId) : null;
  const activeSession = activeSessionId ? getSession(activeSessionId) : null;
  const agentVisible =
    activeSessionId !== null &&
    resolveSessionSurfaceMode(activeSession) === 'agent' &&
    isTabAvailable(activeSessionId, 'agent');
  const strip = document.getElementById('mobile-tab-strip');
  const topbar = document.getElementById('mobile-topbar');
  const title = document.getElementById('mobile-title');
  const terminalLabel = activeSessionId
    ? getTabLabelForSession(activeSessionId, 'terminal')
    : t('session.terminal');
  const agentLabel = activeSession ? getAgentSurfaceLabel(activeSession) : t('sessionTabs.agent');

  strip?.toggleAttribute('hidden', !activeSessionId);
  title?.toggleAttribute('hidden', Boolean(activeSessionId));
  topbar?.classList.toggle('has-mobile-tabs', Boolean(activeSessionId));
  syncMobileActionButton('btn-mobile-tab-terminal', {
    active: activeTab === 'terminal',
    hidden: activeSessionId ? !isTabAvailable(activeSessionId, 'terminal') : true,
    label: terminalLabel,
  });
  syncMobileActionButton('btn-mobile-tab-agent', {
    active: activeTab === 'agent',
    hidden: !agentVisible,
    label: agentLabel,
  });
  syncMobileActionButton('btn-mobile-tab-files', {
    active: activeTab === 'files',
    label: t('sessionTabs.files'),
  });
  syncMobileActionButton('btn-mobile-strip-terminal', {
    active: activeTab === 'terminal',
    hidden: activeSessionId ? !isTabAvailable(activeSessionId, 'terminal') : true,
    label: terminalLabel,
  });
  syncMobileActionButton('btn-mobile-strip-agent', {
    active: activeTab === 'agent',
    hidden: !agentVisible,
    label: agentLabel,
  });
  syncMobileActionButton('btn-mobile-strip-files', {
    active: activeTab === 'files',
    label: t('sessionTabs.files'),
  });
}

export function activateMobileTab(tab: MobileSessionTab): void {
  const activeId = $activeSessionId.get();
  if (!activeId) {
    return;
  }

  if (tab === 'agent' && !isTabAvailable(activeId, 'agent')) {
    return;
  }

  closeBoardsView();
  switchTab(activeId, tab);
  syncMobileTabActionState();
}

export function closeMobileActionsMenu(): void {
  const toggleBtn = document.getElementById('btn-mobile-actions-menu');
  const dropdown = document.getElementById('mobile-actions-dropdown');
  if (!toggleBtn || !dropdown) {
    return;
  }

  dropdown.setAttribute('hidden', '');
  toggleBtn.setAttribute('aria-expanded', 'false');
}

export function bindMobileActionsMenu(): void {
  const toggleBtn = document.getElementById('btn-mobile-actions-menu');
  const dropdown = document.getElementById('mobile-actions-dropdown');
  const actions = document.getElementById('topbar-actions');
  if (!toggleBtn || !dropdown || !actions) {
    return;
  }

  closeMobileActionsMenu();

  toggleBtn.addEventListener('click', (event) => {
    event.stopPropagation();
    toggleMobileActionsMenu();
  });

  dropdown.addEventListener('click', (event) => {
    const target = event.target as HTMLElement | null;
    if (target?.closest('button')) {
      closeMobileActionsMenu();
    }
  });

  document.addEventListener('click', (event) => {
    const target = event.target as Node | null;
    if (target && !actions.contains(target)) {
      closeMobileActionsMenu();
    }
  });

  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') {
      closeMobileActionsMenu();
    }
  });

  window.addEventListener('orientationchange', closeMobileActionsMenu);
}

function syncMobileActionButton(
  elementId: string,
  options: {
    active: boolean;
    hidden?: boolean;
    label?: string;
  },
): void {
  const button = document.getElementById(elementId) as HTMLButtonElement | null;
  if (!button) {
    return;
  }

  button.classList.toggle('active', options.active);
  if (typeof options.hidden === 'boolean') {
    button.toggleAttribute('hidden', options.hidden);
  }

  if (typeof options.label !== 'string') {
    return;
  }

  button.title = options.label;
  button.setAttribute('aria-label', options.label);
  const labelNode = button.querySelector<HTMLElement>('.mobile-actions-label, span');
  if (labelNode) {
    labelNode.textContent = options.label;
  }
}

function toggleMobileActionsMenu(): void {
  const toggleBtn = document.getElementById('btn-mobile-actions-menu');
  const dropdown = document.getElementById('mobile-actions-dropdown');
  if (!toggleBtn || !dropdown) {
    return;
  }

  if (!dropdown.hasAttribute('hidden')) {
    closeMobileActionsMenu();
    return;
  }

  syncMobileTabActionState();
  dropdown.removeAttribute('hidden');
  toggleBtn.setAttribute('aria-expanded', 'true');
}
