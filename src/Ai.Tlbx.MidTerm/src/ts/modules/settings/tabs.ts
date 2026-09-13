/**
 * Settings Tab Navigation
 *
 * Manages the tabbed interface for the settings panel.
 */

import { startLatencyMeasurement, stopLatencyMeasurement } from '../diagnostics';

export type SettingsTab =
  | 'hotkeys'
  | 'updates'
  | 'sessions'
  | 'appearance'
  | 'workflow'
  | 'terminal'
  | 'ai-agents'
  | 'security'
  | 'connected-hosts'
  | 'advanced';

const STORAGE_KEY = 'settings-tab';
const DEFAULT_TAB: SettingsTab = 'updates';
const VALID_TABS: SettingsTab[] = [
  'hotkeys',
  'updates',
  'sessions',
  'appearance',
  'workflow',
  'terminal',
  'ai-agents',
  'security',
  'connected-hosts',
  'advanced',
];

let activeTab: SettingsTab = DEFAULT_TAB;
let initialized = false;

export function initSettingsTabs(): void {
  if (initialized) {
    restoreLastTab();
    return;
  }

  bindTabEvents();
  restoreLastTab();
  initialized = true;
}

export function switchSettingsTab(tab: SettingsTab): void {
  if (!isValidTab(tab)) return;

  activeTab = tab;
  localStorage.setItem(STORAGE_KEY, tab);

  document.querySelectorAll('.settings-tab').forEach((btn) => {
    const isActive = btn.getAttribute('data-tab') === tab;
    btn.classList.toggle('active', isActive);
    btn.setAttribute('aria-selected', String(isActive));
  });

  document.querySelectorAll('.settings-panel').forEach((panel) => {
    const isActive = panel.getAttribute('data-panel') === tab;
    panel.classList.toggle('hidden', !isActive);
  });

  if (tab === 'advanced') {
    startLatencyMeasurement();
  } else {
    stopLatencyMeasurement();
  }
}

export function getActiveSettingsTab(): SettingsTab {
  return activeTab;
}

function bindTabEvents(): void {
  document.querySelectorAll('.settings-tab').forEach((btn) => {
    btn.addEventListener('click', () => {
      const tab = btn.getAttribute('data-tab');
      if (tab) switchSettingsTab(tab as SettingsTab);
    });
  });
}

function restoreLastTab(): void {
  const saved = normalizeStoredSettingsTab(localStorage.getItem(STORAGE_KEY));
  if (saved) {
    switchSettingsTab(saved);
  } else {
    switchSettingsTab(DEFAULT_TAB);
  }
}

function isValidTab(tab: string): tab is SettingsTab {
  return VALID_TABS.includes(tab as SettingsTab);
}

export function normalizeStoredSettingsTab(tab: string | null): SettingsTab | null {
  switch (tab) {
    case null:
      return null;
    case 'general':
      return 'updates';
    case 'hub':
      return 'connected-hosts';
    case 'appearance':
      return 'appearance';
    case 'command-bay':
      return 'workflow';
    case 'terminal':
      return 'terminal';
    case 'agent':
      return 'ai-agents';
    case 'security':
      return 'security';
    case 'diagnostics':
      return 'advanced';
    case 'behavior':
      return 'workflow';
    case 'agent-ui':
      return 'ai-agents';
    default:
      return tab && isValidTab(tab) ? tab : null;
  }
}
