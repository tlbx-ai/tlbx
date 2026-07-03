/**
 * Session List Module
 *
 * Shared session-display helpers (title/subtitle resolution, foreground
 * process indicator) plus the sidebar empty-state and mobile/desktop title
 * bar updates. Session list rendering itself lives in spacesTreeSidebar.ts.
 */

import type { Session } from '../../types';
import { t } from '../i18n';
import { dom } from '../../state';
import {
  $settingsOpen,
  $activeSessionId,
  $currentSettings,
  $sessionList,
  getSession,
} from '../../stores';
import { getHubSession, getHubSidebarSections } from '../hub/runtime';
import { getPrimarySurfaceLabel, isAgentSurfaceSession } from '../sessionSurface';
import { createSessionFilterController } from './sessionFilterController';

// =============================================================================
// Helpers
// =============================================================================

/**
 * Check if a foreground process name is just the session's own shell.
 * Compares basename + extensionless identity of both values, handling
 * full paths, quoted command lines, and command arguments.
 */
function isShellProcess(processName: string, sessionId: string): boolean {
  const session = getSession(sessionId);
  if (!session?.shellType) return false;
  const normalizedProcess = normalizeExecutableName(processName);
  const normalizedShell = normalizeExecutableName(session.shellType);
  return normalizedProcess !== '' && normalizedProcess === normalizedShell;
}

/**
 * Normalize a shell/process identifier to a comparable executable identity.
 * - strips command-line arguments
 * - strips quotes
 * - extracts basename from paths
 * - removes ".exe" extension
 */
function normalizeExecutableName(value: string): string {
  const trimmed = value.trim();
  if (!trimmed) return '';

  let candidate = trimmed;
  const firstChar = candidate[0] ?? '';
  if (firstChar === '"' || firstChar === "'") {
    const quote = firstChar;
    const closingQuote = candidate.indexOf(quote, 1);
    if (closingQuote > 1) {
      candidate = candidate.slice(1, closingQuote);
    }
  }

  const basename = candidate.replace(/\\/g, '/').split('/').pop() ?? candidate;
  const token = basename.trim().split(/\s+/)[0] ?? basename.trim();
  return token.replace(/\.exe$/i, '').toLowerCase();
}

// =============================================================================
// Session Filter
// =============================================================================

const SESSION_FILTER_STORAGE_KEY = 'midterm.sidebar.sessionFilter';

function loadStoredSessionFilter(): string {
  try {
    return (localStorage.getItem(SESSION_FILTER_STORAGE_KEY) ?? '').trim();
  } catch {
    return '';
  }
}

function persistSessionFilter(value: string): void {
  try {
    if (value === '') {
      localStorage.removeItem(SESSION_FILTER_STORAGE_KEY);
    } else {
      localStorage.setItem(SESSION_FILTER_STORAGE_KEY, value);
    }
  } catch {
    // Ignore localStorage failures and keep the filter in memory.
  }
}

function isSidebarSessionFilterEnabled(): boolean {
  return $currentSettings.get()?.showSidebarSessionFilter === true;
}

const sessionFilterController = createSessionFilterController({
  getElements: () => ({
    filterBar: dom.sessionFilterBar,
    filterInput: dom.sessionFilterInput,
    filterClear: dom.sessionFilterClear,
  }),
  isEnabled: isSidebarSessionFilterEnabled,
  areSettingsLoaded: () => $currentSettings.get() !== null,
  loadStoredFilter: loadStoredSessionFilter,
  persistFilter: persistSessionFilter,
  // spacesTreeSidebar owns the live sidebar render; this controller instance
  // is only kept alive here to back isSessionFilterActive().
  render: () => {},
  translate: t,
});

export function isSessionFilterActive(): boolean {
  return sessionFilterController.isActive();
}

// =============================================================================
// Session Display
// =============================================================================

interface SessionDisplayInfo {
  primary: string;
  secondary: string | null;
  useProcessAsTitle?: boolean;
}

/**
 * Get display info for a session (primary title and optional secondary subtitle)
 */
export function getSessionDisplayInfo(session: Session): SessionDisplayInfo {
  const sessionSurfaceLabel = getPrimarySurfaceLabel(session);
  const termTitle = isAgentSurfaceSession(session)
    ? sessionSurfaceLabel
    : session.terminalTitle || session.shellType || t('session.terminal');
  if (session.name) {
    return { primary: session.name, secondary: termTitle };
  }
  if (isAgentSurfaceSession(session)) {
    return { primary: termTitle, secondary: null };
  }
  // Process set a console title — show it as the primary title with process info below
  if (session.terminalTitle && !isShellProcess(session.terminalTitle, session.id)) {
    return { primary: session.terminalTitle, secondary: null };
  }
  // No name, no console title: show cwd + process as the title row
  return { primary: termTitle, secondary: null, useProcessAsTitle: true };
}

/**
 * Get the display name for a session (primary title only, for mobile/island)
 */
export function getSessionDisplayName(session: Session): string {
  const info = getSessionDisplayInfo(session);
  return info.primary;
}

// =============================================================================
// Foreground Process Indicator
// =============================================================================

/**
 * Create the foreground process indicator element
 * Layout: ...directory> process...
 * - Directory ellipsis from left (end of path is most important)
 * - Process ellipsis from right (process name is most important)
 */
export function createForegroundIndicator(
  cwd: string | null | undefined,
  commandLine: string | null | undefined,
  processName: string,
  displayName: string | null | undefined,
): HTMLElement {
  const container = document.createElement('span');
  container.className = 'session-foreground';

  const cmdDisplay = displayName?.trim() || commandLine || processName;
  container.title = `${commandLine ?? processName}\n${cwd ?? ''}`;

  if (cwd) {
    const cwdSpan = document.createElement('span');
    cwdSpan.className = 'fg-cwd';
    cwdSpan.textContent = cwd;
    container.appendChild(cwdSpan);

    const separator = document.createElement('span');
    separator.className = 'fg-separator';
    separator.textContent = '>';
    container.appendChild(separator);
  }

  const processSpan = document.createElement('span');
  processSpan.className = 'fg-process';
  processSpan.textContent = cmdDisplay;
  container.appendChild(processSpan);

  return container;
}

// =============================================================================
// Empty State & Titles
// =============================================================================

/**
 * Update the empty state visibility based on session count
 */
export function updateEmptyState(): void {
  if (!dom.emptyState) return;

  const isSettingsOpen = $settingsOpen.get();
  const sessions = $sessionList.get();
  const hasHubSessions = getHubSidebarSections().some((machine) => machine.sessions.length > 0);
  if (sessions.length === 0 && !hasHubSessions) {
    // Only show empty state if settings panel is not open
    if (!isSettingsOpen) {
      dom.emptyState.classList.remove('hidden');
      if (dom.settingsView) dom.settingsView.classList.add('hidden');
    }
  } else if (!isSettingsOpen) {
    dom.emptyState.classList.add('hidden');
  }
}

/**
 * Update the mobile title bar with current session name.
 * Also updates the desktop collapsed title bar.
 */
export function updateMobileTitle(): void {
  if (!dom.mobileTitle) return;

  const sessions = $sessionList.get();
  const activeSessionId = $activeSessionId.get();
  const session =
    sessions.find((s) => s.id === activeSessionId) ??
    (activeSessionId ? getHubSession(activeSessionId) : null);
  dom.mobileTitle.textContent = session ? getSessionDisplayName(session) : 'MidTerm';

  if (dom.topbarActions) {
    if (session) {
      dom.topbarActions.classList.remove('no-terminal');
    } else {
      dom.topbarActions.classList.add('no-terminal');
    }
  }

  // Also update the desktop collapsed title bar
  updateTitleBar(session ?? undefined);
}

/**
 * Update the collapsed title bar with current session info
 */
function updateTitleBar(session: Session | undefined): void {
  if (!dom.titleBarCustom || !dom.titleBarTerminal || !dom.titleBarSeparator) return;

  if (!session) {
    dom.titleBarCustom.textContent = 'MidTerm';
    dom.titleBarTerminal.textContent = '';
    dom.titleBarSeparator.style.display = 'none';
    return;
  }

  const info = getSessionDisplayInfo(session);

  if (session.name) {
    dom.titleBarCustom.textContent = info.primary;
    dom.titleBarTerminal.textContent = info.secondary || '';
    dom.titleBarSeparator.style.display = info.secondary ? '' : 'none';
  } else {
    dom.titleBarCustom.textContent = info.primary;
    dom.titleBarTerminal.textContent = '';
    dom.titleBarSeparator.style.display = 'none';
  }
}
