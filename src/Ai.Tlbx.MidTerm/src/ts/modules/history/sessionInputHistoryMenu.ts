import { $activeSessionId } from '../../stores';
import { isAuthRedirectPending } from '../auth/sessionLifetime';
import { t } from '../i18n';
import { createLogger } from '../logging';
import { setInputHistoryClickHandler } from '../sessionTabs/tabBar';
import {
  deleteInputHistory,
  fetchInputHistory,
  replayInputHistory,
  type InputHistoryEntry,
} from './inputHistoryApi';
import { renderInputHistoryPanel } from './inputHistoryPanel';

const log = createLogger('session-input-history');
let initialized = false;
let menu: HTMLElement | null = null;
let anchor: HTMLElement | null = null;
let ownerSessionId: string | null = null;
let entries: InputHistoryEntry[] = [];
let loadController: AbortController | null = null;
let loadGeneration = 0;

export function initSessionInputHistoryMenus(): void {
  if (initialized) return;
  initialized = true;

  setInputHistoryClickHandler(toggleSessionInputHistoryMenu);
  document.getElementById('btn-mobile-input-history')?.addEventListener('click', (event) => {
    const sessionId = $activeSessionId.get();
    if (sessionId && event.currentTarget instanceof HTMLElement) {
      toggleSessionInputHistoryMenu(sessionId, event.currentTarget);
    }
  });
  document.addEventListener('click', handleOutsideClick);
  document.addEventListener('keydown', handleKeydown);
  window.addEventListener('resize', closeSessionInputHistoryMenu);
  window.addEventListener('orientationchange', closeSessionInputHistoryMenu);
  window.addEventListener('scroll', closeSessionInputHistoryMenu, true);
}

export function toggleSessionInputHistoryMenu(sessionId: string, nextAnchor: HTMLElement): void {
  if (menu && ownerSessionId === sessionId && anchor === nextAnchor) {
    closeSessionInputHistoryMenu();
    return;
  }

  closeSessionInputHistoryMenu();
  ownerSessionId = sessionId;
  anchor = nextAnchor;
  entries = [];
  menu = createMenu();
  document.body.appendChild(menu);
  anchor.classList.add('sidebar-active');
  anchor.setAttribute('aria-expanded', 'true');
  positionMenu();
  void loadSessionEntries(sessionId);
}

export function closeSessionInputHistoryMenu(): void {
  loadController?.abort();
  loadController = null;
  loadGeneration += 1;
  anchor?.classList.remove('sidebar-active');
  anchor?.setAttribute('aria-expanded', 'false');
  menu?.remove();
  menu = null;
  anchor = null;
  ownerSessionId = null;
  entries = [];
}

function createMenu(): HTMLDivElement {
  const element = document.createElement('div');
  element.className = 'manager-bar-action-popover session-input-history-menu';
  element.setAttribute('role', 'menu');

  const header = document.createElement('div');
  header.className = 'history-dropdown-header';
  header.textContent = t('sidebar.inputHistory');
  element.appendChild(header);

  const content = document.createElement('div');
  content.className = 'session-input-history-content';
  element.appendChild(content);
  renderStatus(t('inputHistory.loading'));
  return element;
}

async function loadSessionEntries(sessionId: string): Promise<void> {
  const generation = ++loadGeneration;
  const controller = new AbortController();
  loadController = controller;
  try {
    const response = await fetchInputHistory({ sessionId, limit: 100 }, controller.signal);
    if (generation !== loadGeneration || ownerSessionId !== sessionId) return;
    entries = response.entries.filter(
      (entry) => entry.sessionId === sessionId && entry.surface === 'terminal',
    );
    renderEntries();
    positionMenu();
  } catch (error) {
    if (generation !== loadGeneration || controller.signal.aborted) return;
    if (isAuthRedirectPending()) return;
    log.warn(() => `Failed to load session input history: ${String(error)}`);
    renderStatus(t('inputHistory.empty'));
  }
}

function renderEntries(): void {
  const content = menu?.querySelector<HTMLElement>('.session-input-history-content');
  if (!content) return;
  if (entries.length === 0) {
    renderStatus(t('inputHistory.empty'));
    return;
  }

  renderInputHistoryPanel(content, entries, {
    includeSessionName: false,
    onDelete: (entry) => {
      void deleteInputHistory(entry.id)
        .then(() => {
          entries = entries.filter((candidate) => candidate.id !== entry.id);
          renderEntries();
        })
        .catch((error: unknown) => {
          log.warn(() => `Failed to delete input history: ${String(error)}`);
        });
    },
    onReplay: (entry) => {
      const sessionId = ownerSessionId;
      if (!sessionId || entry.sessionId !== sessionId) return;
      void replayInputHistory(entry.id, sessionId)
        .then(closeSessionInputHistoryMenu)
        .catch((error: unknown) => {
          log.warn(() => `Failed to replay input history: ${String(error)}`);
        });
    },
  });
}

function renderStatus(message: string): void {
  const content = menu?.querySelector<HTMLElement>('.session-input-history-content');
  if (!content) return;
  const status = document.createElement('div');
  status.className = 'history-dropdown-empty';
  status.textContent = message;
  content.replaceChildren(status);
}

function positionMenu(): void {
  if (!menu || !anchor) return;
  const anchorRect = anchor.getBoundingClientRect();
  const margin = 8;
  const width = Math.min(360, window.innerWidth - margin * 2);
  menu.style.width = `${width}px`;
  menu.style.left = `${Math.max(margin, Math.min(anchorRect.right - width, window.innerWidth - width - margin))}px`;
  const menuHeight = Math.min(menu.scrollHeight, window.innerHeight - margin * 2);
  const below = anchorRect.bottom + margin;
  const top =
    below + menuHeight <= window.innerHeight - margin
      ? below
      : Math.max(margin, anchorRect.top - menuHeight - margin);
  menu.style.top = `${top}px`;
}

function handleOutsideClick(event: MouseEvent): void {
  if (!menu || !(event.target instanceof Node)) return;
  if (!menu.contains(event.target) && !anchor?.contains(event.target)) {
    closeSessionInputHistoryMenu();
  }
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && menu) {
    closeSessionInputHistoryMenu();
    return;
  }
  if (
    !event.altKey ||
    event.ctrlKey ||
    event.metaKey ||
    event.shiftKey ||
    event.key.toLowerCase() !== 'h'
  ) {
    return;
  }

  const sessionId = $activeSessionId.get();
  const sessionAnchor = sessionId
    ? document.querySelector<HTMLElement>(
        `.session-tab-bar[data-session-id="${CSS.escape(sessionId)}"] .ide-bar-input-history`,
      )
    : null;
  if (!sessionId || !sessionAnchor) return;
  event.preventDefault();
  toggleSessionInputHistoryMenu(sessionId, sessionAnchor);
}
