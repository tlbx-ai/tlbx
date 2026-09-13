import { toggleSmartInputVoiceRecording, canToggleSmartInputVoiceRecording } from '../smartInput';
import { reconcileKeyedChildren } from '../../utils/domReconcile';
import { openHistoryDropdown } from '../history';
import { triggerAddAutomation, triggerAutomationOverflow } from '../managerBar/managerBar';
import { getCurrentLocale } from '../i18n';
import { $activeSessionId, $sessionList } from '../../stores';
import { sessionTerminals } from '../../state';
import { openSettings, closeSettings } from '../settings/panel';
import { switchSettingsTab, type SettingsTab } from '../settings/tabs';
import { switchTab, isTabAvailable } from '../sessionTabs';
import { showSearch } from '../terminal/search';
import { dockSession, undockSession, getLayoutSessionIds, focusLayoutSession } from '../layout';
import { showAlert } from '../../utils/dialog';
import { registerBackButtonLayer } from '../navigation/backButtonGuard';
import { bindingProblem, matchesSearch, normalizeBinding } from './keybindings';
import { hasTransientUi, isVisible } from './uiContext';

interface Command {
  id: string;
  label: string;
  keywords: string;
  run: () => void | Promise<void>;
  available?: (() => boolean) | undefined;
  binding?: string | undefined;
}
interface Result {
  keywords?: string;
  id: string;
  label: string;
  detail: string;
  run: () => void | Promise<void>;
}
interface Dependencies {
  createSession: () => Promise<void>;
  selectSession: (id: string) => void;
  renameSession(id: string): Promise<void>;
  inlineRename(id: string): void;
  bookmarkSession(id: string): Promise<void>;
  toggleSidebar: () => void;
}
const words = (en: string, de: string): string => (getCurrentLocale().startsWith('de') ? de : en);
const STORAGE = `tlbx-hotkeys-v1:${/Mac|iPhone|iPad/.test(navigator.userAgent) ? 'mac' : 'other'}`;
const primary = /Mac|iPhone|iPad/.test(navigator.userAgent) ? 'Shift+Meta' : 'Ctrl+Shift';
let overrides: Record<string, string | null> = {};
const commands: Command[] = [];
let palette: HTMLDialogElement;
let input: HTMLInputElement;
let resultsElement: HTMLElement;
let results: Result[] = [];
let selected = 0;
let previousFocus: HTMLElement | null = null;
let releaseBack: (() => void) | undefined;
let mode: 'all' | 'sessions' | 'settings' = 'all';
let recording: string | null = null;
let lastSessions: string[] = [];
let launchPending = false;
let settingsSearch: HTMLInputElement;
let settingsResults: HTMLElement;

function binding(command: Command | undefined): string | null {
  if (!command) return null;
  return Object.prototype.hasOwnProperty.call(overrides, command.id)
    ? (overrides[command.id] ?? null)
    : (command.binding ?? null);
}
function consume(event: KeyboardEvent): void {
  event.preventDefault();
  event.stopImmediatePropagation();
}
function button(text: string, action: () => void): HTMLButtonElement {
  const element = document.createElement('button');
  element.type = 'button';
  element.textContent = text;
  element.addEventListener('click', action);
  return element;
}
function activeId(): string | null {
  return $activeSessionId.get();
}
function focusWork(): void {
  const id = activeId();
  if (!id) return;
  if (isTabAvailable(id, 'agent')) {
    switchTab(id, 'agent');
  } else {
    switchTab(id, 'terminal');
    sessionTerminals.get(id)?.terminal.focus();
  }
}
function visibleButton(selector: string): HTMLButtonElement | undefined {
  return Array.from(document.querySelectorAll<HTMLButtonElement>(selector)).find(
    (el) => isVisible(el) && !el.disabled,
  );
}
function persist(): boolean {
  try {
    localStorage.setItem(STORAGE, JSON.stringify(overrides));
    return true;
  } catch {
    void showAlert(
      words(
        'Could not save shortcuts in this browser.',
        'Tastenkürzel konnten in diesem Browser nicht gespeichert werden.',
      ),
    );
    return false;
  }
}

export function initShortcuts(deps: Dependencies): void {
  try {
    overrides = readOverrides(JSON.parse(localStorage.getItem(STORAGE) ?? '{}'));
  } catch {
    /* Client preferences must never prevent terminal startup. */
  }
  const cmd = (
    id: string,
    en: string,
    de: string,
    run: Command['run'],
    available?: Command['available'],
    key?: string,
  ): void => {
    commands.push({
      id,
      label: words(en, de),
      keywords: `${en} ${de}`,
      run: () => {
        if (!['search', 'sessions', 'settings', 'hotkeys', 'sidebar'].includes(id)) closeSettings();
        return run();
      },
      available,
      binding: key,
    });
  };
  const hasSession = (): boolean => !!activeId();
  cmd(
    'search',
    'Search and commands',
    'Suchen und Befehle',
    () => {
      openPalette();
    },
    undefined,
    `${primary}+Space`,
  );
  cmd('sessions', 'Switch session', 'Session wechseln', () => {
    openPalette('sessions');
  });
  cmd(
    'new',
    'New session / choose session type',
    'Neue Session / Sessiontyp auswählen',
    async () => {
      if (launchPending) return;
      launchPending = true;
      try {
        await deps.createSession();
      } finally {
        launchPending = false;
      }
    },
  );
  const move = (direction: number): void => {
    const list = $sessionList.get();
    if (!list.length) return;
    const index = list.findIndex((s) => s.id === activeId());
    const target = list[(index + direction + list.length) % list.length];
    if (target) deps.selectSession(target.id);
  };
  cmd(
    'next',
    'Next session',
    'Nächste Session',
    () => {
      move(1);
    },
    hasSession,
  );
  cmd(
    'previous',
    'Previous session',
    'Vorherige Session',
    () => {
      move(-1);
    },
    hasSession,
  );
  cmd(
    'last',
    'Last used session',
    'Zuletzt verwendete Session',
    () => {
      const id = lastSessions.find(
        (candidate) =>
          candidate !== activeId() && $sessionList.get().some((s) => s.id === candidate),
      );
      if (id) deps.selectSession(id);
    },
    () =>
      lastSessions.some((id) => id !== activeId() && $sessionList.get().some((s) => s.id === id)),
  );
  cmd(
    'rename',
    'Rename session',
    'Session umbenennen',
    async () => {
      const id = activeId();
      if (id) await deps.renameSession(id);
    },
    hasSession,
    'F2',
  );
  cmd(
    'bookmark',
    'Bookmark session',
    'Session als Lesezeichen speichern',
    async () => {
      const id = activeId();
      if (id) await deps.bookmarkSession(id);
    },
    hasSession,
  );
  cmd(
    'attention',
    'Session needing attention',
    'Session mit Handlungsbedarf',
    () => {
      const session = $sessionList
        .get()
        .find((s) => s.id !== activeId() && s.supervisor?.needsAttention);
      if (session) deps.selectSession(session.id);
    },
    () => $sessionList.get().some((s) => s.id !== activeId() && s.supervisor?.needsAttention),
  );
  cmd('focus', 'Focus session', 'Session fokussieren', focusWork, hasSession);
  cmd(
    'input',
    'Focus prompt input',
    'Prompteingabe fokussieren',
    () => {
      const field = Array.from(
        document.querySelectorAll<HTMLTextAreaElement>('.smart-input-textarea'),
      ).find((el) => isVisible(el) && !el.classList.contains('xterm-helper-textarea'));
      if (field) field.focus();
      else focusWork();
    },
    hasSession,
  );
  cmd('find', 'Find in terminal output', 'Terminalausgabe durchsuchen', showSearch, () => {
    const id = activeId();
    return !!id && sessionTerminals.has(id);
  });
  cmd(
    'bottom',
    'Scroll to latest output',
    'Zur aktuellen Ausgabe',
    () => {
      const id = activeId();
      if (id) sessionTerminals.get(id)?.terminal.scrollToBottom();
    },
    hasSession,
  );
  cmd(
    'files',
    'Open files',
    'Dateien öffnen',
    () => {
      const id = activeId();
      if (id) switchTab(id, 'files');
    },
    hasSession,
  );
  cmd('sidebar', 'Toggle sidebar', 'Seitenleiste umschalten', deps.toggleSidebar);
  for (const [id, en, de] of [
    ['git', 'Git and changes', 'Git und Änderungen'],
    ['web', 'Dev Browser', 'Dev Browser'],
    ['commands', 'Saved commands', 'Gespeicherte Befehle'],
    ['inputHistory', 'Input history', 'Eingabehistorie'],
    ['share', 'Share session…', 'Session teilen…'],
  ] as const) {
    const selector = `.ide-bar-actions [data-action="${id}"]`;
    cmd(
      `view.${id}`,
      en,
      de,
      () => visibleButton(selector)?.click(),
      () => !!visibleButton(selector),
    );
  }
  for (const [direction, en, de] of [
    ['right', 'Split side by side', 'Sessions nebeneinander'],
    ['bottom', 'Split above and below', 'Sessions untereinander'],
  ] as const) {
    cmd(
      `split.${direction}`,
      en,
      de,
      () => {
        openSessionSelection((id) => {
          const target = activeId();
          if (target) dockSession(target, id, direction);
        }, true);
      },
      () => $sessionList.get().length > 1,
    );
  }
  cmd(
    'pane.next',
    'Focus next pane',
    'Nächstes Pane fokussieren',
    () => {
      const ids = getLayoutSessionIds();
      const next = ids[(ids.indexOf(activeId() ?? '') + 1) % ids.length];
      if (next) focusLayoutSession(next);
    },
    () => getLayoutSessionIds().length > 1,
  );
  cmd(
    'undock',
    'Remove session from split',
    'Session aus Aufteilung lösen',
    () => {
      const id = activeId();
      if (id) undockSession(id);
    },
    () => getLayoutSessionIds().includes(activeId() ?? ''),
  );
  cmd(
    'voice',
    'Start / stop voice input',
    'Spracheingabe starten / stoppen',
    toggleSmartInputVoiceRecording,
    canToggleSmartInputVoiceRecording,
  );
  cmd('bookmarks', 'Open bookmarks', 'Lesezeichen öffnen', openHistoryDropdown);
  cmd(
    'automation.add',
    'Add reusable action',
    'Wiederverwendbare Aktion anlegen',
    triggerAddAutomation,
    hasSession,
  );
  cmd(
    'automation.menu',
    'Actions and scheduled follow-ups',
    'Aktionen und geplante Folgeeingaben',
    () => {
      triggerAutomationOverflow(null);
    },
    hasSession,
  );
  for (const [id, en, de, selector] of [
    ['spaces', 'Spaces and workspaces', 'Spaces und Arbeitsbereiche', '#btn-spaces'],
    ['preview.reload', 'Reload preview', 'Vorschau neu laden', '#web-preview-refresh'],
    [
      'preview.screenshot',
      'Capture preview screenshot',
      'Screenshot der Vorschau',
      '#web-preview-screenshot',
    ],
    ['preview.detach', 'Detach preview', 'Vorschau ablösen', '#web-preview-detach'],
    [
      'preview.viewport',
      'Responsive preview',
      'Responsive Vorschau',
      '#web-preview-responsive-frame',
    ],
  ] as const)
    cmd(
      id,
      en,
      de,
      () => visibleButton(selector)?.click(),
      () => !!visibleButton(selector),
    );
  document.addEventListener('click', (event) => {
    if (recording && !(event.target as Element).closest('.hotkey-row')) {
      recording = null;
      renderHotkeys();
    }
  });
  cmd('settings', 'Search settings', 'Einstellungen durchsuchen', () => {
    openPalette('settings');
  });
  cmd('hotkeys', 'Keyboard shortcuts', 'Tastenkürzel', () => {
    openSettings();
    switchSettingsTab('hotkeys');
  });

  $activeSessionId.subscribe((id) => {
    if (id) lastSessions = [id, ...lastSessions.filter((s) => s !== id)].slice(0, 50);
  });
  selectResultSession = deps.selectSession;
  buildPalette();
  buildSettings();
  const trigger = button('⌕', () => {
    openPalette();
  });
  trigger.id = 'btn-command-search';
  trigger.className = 'command-search-trigger';
  trigger.setAttribute('aria-label', words('Search and commands', 'Suchen und Befehle'));
  trigger.title = words('Search and commands', 'Suchen und Befehle');
  document.querySelector('.sidebar-header-buttons')?.prepend(trigger);
  // Also reachable when the sidebar is collapsed, without a permanent search field.
  const compact = trigger.cloneNode(true) as HTMLButtonElement;
  compact.id = 'btn-command-search-compact';
  compact.addEventListener('click', () => {
    openPalette();
  });
  document.querySelector('#app')?.append(compact);
  document.getElementById('session-list')?.addEventListener('keydown', (event) => {
    const target = event.target as HTMLElement;
    if (
      !target.matches('.session-item') ||
      event.isComposing ||
      event.ctrlKey ||
      event.metaKey ||
      event.altKey
    )
      return;
    if (event.key === 'Enter' || event.key === ' ') {
      const id = target.dataset.sessionId;
      if (id) {
        consume(event);
        deps.selectSession(id);
      }
    } else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      const rows = Array.from(
        document.querySelectorAll<HTMLElement>('#session-list .session-item'),
      ).filter(isVisible);
      const next = rows[rows.indexOf(target) + (event.key === 'ArrowDown' ? 1 : -1)];
      if (next) {
        consume(event);
        next.focus();
      }
    }
  });
  window.addEventListener(
    'keydown',
    (event) => {
      const hotkeyList = document.getElementById('hotkey-list');
      if (recording && (!hotkeyList || !isVisible(hotkeyList))) {
        recording = null;
        renderHotkeys();
      }
      if (recording) {
        recordBinding(event);
        return;
      }
      if (event.defaultPrevented || event.isComposing || event.repeat || palette.open) return;
      const key = normalizeBinding(event);
      if (!key) return;
      const command = commands.find((entry) => binding(entry) === key);
      if (!command) return;
      if (hasTransientUi()) return;
      if (key === 'F2') {
        handleInlineRename(event, deps);
        return;
      }
      if (command.available && !command.available()) return;
      consume(event);
      execute(command.run);
    },
    true,
  );
  window.addEventListener('blur', () => {
    if (recording) {
      recording = null;
      renderHotkeys();
    }
  });
  window.addEventListener('storage', (event) => {
    if (event.key === STORAGE) {
      try {
        const saved: unknown = JSON.parse(event.newValue ?? '{}');
        overrides = readOverrides(saved);
        renderHotkeys();
      } catch {
        /* retain valid local state */
      }
    }
  });
}

function execute(run: () => void | Promise<void>): void {
  void Promise.resolve()
    .then(run)
    .catch((error: unknown) => showAlert(String(error)));
}
function closePalette(restore = true): void {
  palette.close();
  sessionPicker = null;
  releaseBack?.();
  releaseBack = undefined;
  if (restore && previousFocus?.isConnected) previousFocus.focus({ preventScroll: true });
}
function openPalette(nextMode: typeof mode = 'all'): void {
  if (palette.open) {
    input.focus();
    return;
  }
  mode = nextMode;
  previousFocus = document.activeElement as HTMLElement;
  input.value = '';
  selected = 0;
  palette.showModal();
  releaseBack = registerBackButtonLayer(() => {
    closePalette();
  });
  refreshResults();
  input.focus();
}
function openSessionSelection(run: (id: string) => void, excludeActive: boolean): void {
  openPalette('sessions');
  const snapshot = sessionResults(run).filter(
    (r) => !excludeActive || r.id !== `session:${activeId()}`,
  );
  sessionPicker = snapshot;
  refreshResults();
}
let sessionPicker: Result[] | null = null;
function sessionResults(run: (id: string) => void): Result[] {
  return $sessionList.get().map((s) => ({
    id: `session:${s.id}`,
    label: s.name || s.terminalTitle || s.shellType,
    detail: `${s.shellType} · ${s.currentDirectory ?? ''} · ${s.id}`,
    run: () => {
      if (!$sessionList.get().some((current) => current.id === s.id))
        throw new Error(words('This session has closed.', 'Diese Session wurde geschlossen.'));
      run(s.id);
    },
  }));
}
let selectResultSession: (id: string) => void;

function buildPalette(): void {
  palette = document.createElement('dialog');
  palette.id = 'command-palette';
  palette.className = 'command-palette';
  palette.setAttribute('aria-label', words('Search and commands', 'Suchen und Befehle'));
  const top = document.createElement('div');
  top.className = 'command-palette-input';
  input = document.createElement('input');
  input.type = 'search';
  input.autocomplete = 'off';
  input.placeholder = words(
    'Search sessions, commands, settings…',
    'Sessions, Befehle, Einstellungen suchen…',
  );
  input.setAttribute('aria-label', input.placeholder);
  input.setAttribute('role', 'combobox');
  input.setAttribute('aria-controls', 'command-results');
  input.setAttribute('aria-expanded', 'true');
  input.setAttribute('aria-autocomplete', 'list');
  const close = button('Esc', () => {
    closePalette();
  });
  close.setAttribute('aria-label', words('Close search', 'Suche schließen'));
  top.append(input, close);
  resultsElement = document.createElement('div');
  resultsElement.id = 'command-results';
  resultsElement.setAttribute('role', 'listbox');
  palette.append(top, resultsElement);
  document.body.append(palette);
  input.addEventListener('input', () => {
    selected = 0;
    refreshResults();
  });
  palette.addEventListener('cancel', (event) => {
    event.preventDefault();
    closePalette();
  });
  palette.addEventListener('click', (event) => {
    if (event.target === palette) closePalette();
  });
  palette.addEventListener('keydown', (event) => {
    if (event.isComposing) return;
    if (event.key === 'Escape') {
      consume(event);
      closePalette();
    } else if (['ArrowDown', 'ArrowUp'].includes(event.key)) {
      consume(event);
      selected =
        (selected + (event.key === 'ArrowDown' ? 1 : -1) + results.length) %
        Math.max(1, results.length);
      renderResults();
    } else if (event.key === 'Enter' && event.target === input) {
      consume(event);
      chooseResult();
    }
  });
}
function refreshResults(): void {
  const query = input.value.trim();
  const targetId = activeId();
  const actions = commands
    .filter((c) => c.id !== 'search' && (!c.available || c.available()))
    .map((c) => ({
      id: c.id,
      label: c.label,
      detail: binding(c) ?? '',
      keywords: c.keywords,
      run: () => {
        if (c.available && (!c.available() || activeId() !== targetId))
          throw new Error(
            words(
              'The session context changed. Search again.',
              'Der Sessionkontext hat sich geändert. Bitte erneut suchen.',
            ),
          );
        return c.run();
      },
    }));
  const sessions = sessionPicker ?? sessionResults(selectResultSession);
  const candidates =
    mode === 'sessions'
      ? sessions
      : mode === 'settings'
        ? settingResults()
        : [...actions, ...sessions, ...settingResults()];
  results = candidates.filter((r) => matchesSearch(query, r.label, r.detail, r.keywords ?? ''));
  if (query)
    results.sort(
      (a, b) =>
        Number(b.label.toLocaleLowerCase().includes(query.toLocaleLowerCase())) -
        Number(a.label.toLocaleLowerCase().includes(query.toLocaleLowerCase())),
    );
  if (!query && mode === 'all')
    results = results.filter((r) => !r.id.startsWith('setting:')).slice(0, 8);
  selected = Math.min(selected, Math.max(0, results.length - 1));
  renderResults();
}
function renderResults(): void {
  reconcileKeyedChildren(resultsElement, results, {
    key: (result) => result.id,
    create: (result) => {
      const row = document.createElement('div');
      row.className = 'command-result';
      row.setAttribute('role', 'option');
      row.append(document.createElement('span'), document.createElement('small'));
      row.addEventListener('mousedown', (event) => {
        event.preventDefault();
      });
      row.addEventListener('click', () => {
        selected = results.findIndex((current) => current.id === result.id);
        chooseResult();
      });
      return row;
    },
    patch: (row, result, index) => {
      row.id = `command-result-${index}`;
      row.setAttribute('aria-selected', String(index === selected));
      const title = row.querySelector('span');
      const detail = row.querySelector('small');
      if (title) title.textContent = result.label;
      if (detail) {
        detail.textContent = result.detail;
        detail.hidden = !result.detail;
      }
    },
  });
  if (!results.length) {
    const empty = document.createElement('p');
    empty.textContent = words('No results', 'Keine Treffer');
    resultsElement.append(empty);
    input.removeAttribute('aria-activedescendant');
    return;
  }
  input.setAttribute('aria-activedescendant', `command-result-${selected}`);
  resultsElement.children[selected]?.scrollIntoView({ block: 'nearest' });
}
function chooseResult(): void {
  const result = results[selected];
  if (!result) return;
  closePalette(false);
  execute(result.run);
}

function settingResults(): Result[] {
  return Array.from(document.querySelectorAll<HTMLElement>('.settings-panel .setting-row')).flatMap(
    (row, index) => {
      const panel = row.closest<HTMLElement>('[data-panel]');
      const label = row.querySelector('label')?.textContent.trim();
      if (!panel || !label) return [];
      const tab = panel.dataset.panel as SettingsTab;
      const control = row.querySelector<HTMLElement>('input,select,textarea,button');
      const description = Array.from(
        row.querySelectorAll('small,.setting-description,.setting-hint'),
      )
        .map((el) => el.textContent)
        .join(' ');
      return [
        {
          id: `setting:${control?.id || index}`,
          label,
          detail: `${panel.querySelector('h2')?.textContent ?? tab} · ${description}`,
          keywords: control?.id ?? '',
          run: () => {
            openSettings();
            switchSettingsTab(tab);
            settingsSearch.value = '';
            settingsResults.replaceChildren();
            requestAnimationFrame(() => {
              row.scrollIntoView({ block: 'center' });
              control?.focus({ preventScroll: true });
            });
          },
        },
      ];
    },
  );
}
function buildSettings(): void {
  const nav = document.querySelector('.settings-tabs');
  const content = document.querySelector('.settings-content');
  if (!nav || !content) return;
  const tab = button(words('Keyboard shortcuts', 'Tastenkürzel'), () => {
    switchSettingsTab('hotkeys');
  });
  tab.className = 'settings-tab';
  tab.dataset.tab = 'hotkeys';
  tab.setAttribute('role', 'tab');
  tab.setAttribute('aria-selected', 'false');
  nav.append(tab);
  const panel = document.createElement('section');
  panel.className = 'settings-panel hidden';
  panel.dataset.panel = 'hotkeys';
  const heading = document.createElement('h2');
  heading.textContent = words('Keyboard shortcuts', 'Tastenkürzel');
  const hint = document.createElement('p');
  hint.textContent = words(
    'Saved in this browser. Browser shortcuts are protected. F2 applies only to a focused session in the sidebar.',
    'In diesem Browser gespeichert. Browserkürzel sind geschützt. F2 gilt nur für eine fokussierte Session in der Seitenleiste.',
  );
  const filter = document.createElement('input');
  filter.type = 'search';
  filter.id = 'hotkey-search';
  filter.placeholder = words('Filter shortcuts…', 'Tastenkürzel filtern…');
  filter.setAttribute('aria-label', filter.placeholder);
  filter.addEventListener('input', renderHotkeys);
  const status = document.createElement('p');
  status.id = 'hotkey-status';
  status.setAttribute('role', 'status');
  const list = document.createElement('div');
  list.id = 'hotkey-list';
  panel.append(heading, hint, filter, status, list);
  content.append(panel);
  const bar = document.createElement('div');
  bar.className = 'settings-search-bar';
  settingsSearch = document.createElement('input');
  settingsSearch.id = 'settings-search';
  settingsSearch.type = 'search';
  settingsSearch.placeholder = words('Search all settings…', 'Alle Einstellungen durchsuchen…');
  settingsSearch.setAttribute('aria-label', settingsSearch.placeholder);
  settingsResults = document.createElement('div');
  settingsResults.id = 'settings-search-results';
  settingsSearch.addEventListener('input', () => {
    settingsResults.replaceChildren();
    const query = settingsSearch.value.trim();
    if (!query) return;
    const found = settingResults().filter((r) =>
      matchesSearch(query, r.label, r.detail, r.keywords ?? ''),
    );
    if (!found.length)
      settingsResults.textContent = words('No settings found', 'Keine Einstellungen gefunden');
    found.forEach((r) => {
      const item = button(r.label, () => {
        execute(r.run);
      });
      item.className = 'settings-search-result';
      settingsResults.append(item);
    });
  });
  settingsSearch.addEventListener('keydown', (event) => {
    if (event.isComposing) return;
    if (event.key === 'Escape') {
      consume(event);
      settingsSearch.value = '';
      settingsResults.replaceChildren();
    }
    if (event.key === 'ArrowDown' || event.key === 'Enter') {
      event.preventDefault();
      settingsResults.querySelector('button')?.focus();
    }
  });
  bar.append(settingsSearch, settingsResults);
  document.querySelector('#settings-view')?.prepend(bar);
  renderHotkeys();
}
function renderHotkeys(): void {
  const list = document.getElementById('hotkey-list');
  if (!list) return;
  const query = (document.getElementById('hotkey-search') as HTMLInputElement).value;
  reconcileKeyedChildren(
    list,
    commands.filter((c) => matchesSearch(query, c.keywords, binding(c) ?? '')),
    {
      key: (command) => command.id,
      create: (command) => {
        const row = document.createElement('div');
        row.className = 'hotkey-row';
        row.dataset.command = command.id;
        const label = document.createElement('span');
        label.textContent = command.label;
        const edit = button('', () => {
          recording = command.id;
          renderHotkeys();
        });
        edit.setAttribute(
          'aria-label',
          `${command.label}: ${words('Change shortcut', 'Kürzel ändern')}`,
        );
        const clear = button(words('Clear', 'Entfernen'), () => {
          saveBinding(command.id, null);
        });
        const reset = button(words('Reset', 'Zurücksetzen'), () => {
          saveBinding(command.id, undefined);
        });
        row.append(label, edit, clear, reset);
        return row;
      },
      patch: (row, command) => {
        const edit = row.querySelector('button');
        if (edit)
          edit.textContent =
            recording === command.id
              ? words('Press shortcut…', 'Kürzel drücken…')
              : (binding(command) ?? words('Unassigned', 'Nicht belegt'));
      },
    },
  );
}
function saveBinding(id: string, next: string | null | undefined): void {
  const command = commands.find((c) => c.id === id);
  const effective = next === undefined ? command?.binding : next;
  const duplicate = effective && commands.find((c) => c.id !== id && binding(c) === effective);
  const status = document.getElementById('hotkey-status');
  if (duplicate) {
    if (status)
      status.textContent = `${words('Already assigned:', 'Bereits belegt:')} ${duplicate.label}`;
    return;
  }
  const previous = overrides;
  overrides = Object.fromEntries(Object.entries(overrides).filter(([key]) => key !== id));
  if (next !== undefined) overrides[id] = next;
  if (!persist()) overrides = previous;
  else if (status) status.textContent = words('Shortcut saved.', 'Tastenkürzel gespeichert.');
  recording = null;
  renderHotkeys();
}
function recordBinding(event: KeyboardEvent): void {
  if (event.isComposing || event.repeat) return;
  consume(event);
  if (event.key === 'Escape') {
    recording = null;
    renderHotkeys();
    return;
  }
  const candidate = normalizeBinding(event);
  if (!candidate) return;
  const error = bindingProblem(candidate);
  const duplicate = commands.find((c) => c.id !== recording && binding(c) === candidate);
  const status = document.getElementById('hotkey-status');
  if (error || duplicate || (candidate === 'F2' && recording !== 'rename')) {
    if (status)
      status.textContent = duplicate
        ? `${words('Already assigned:', 'Bereits belegt:')} ${duplicate.label}`
        : words(
            'Reserved or unsafe shortcut. Use Ctrl+Shift (Mac: Cmd+Shift) with Space, Enter or arrows. Escape cancels.',
            'Reserviertes oder ungeeignetes Kürzel. Strg+Umschalt (Mac: Cmd+Umschalt) mit Leertaste, Enter oder Pfeilen verwenden. Escape bricht ab.',
          );
    return;
  }
  if (recording) saveBinding(recording, candidate);
}

function readOverrides(value: unknown): Record<string, string | null> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return {};
  return Object.fromEntries(
    Object.entries(value as Record<string, unknown>).filter(
      (entry): entry is [string, string | null] =>
        entry[1] === null ||
        (typeof entry[1] === 'string' &&
          !bindingProblem(entry[1]) &&
          (entry[1] !== 'F2' || entry[0] === 'rename')),
    ),
  );
}
function handleInlineRename(event: KeyboardEvent, deps: Dependencies): void {
  const target = event.target as Element;
  const item = target.closest('#session-list [data-session-id]');
  if (!item || target.closest('input,textarea,[contenteditable="true"]')) return;
  const id = item.getAttribute('data-session-id');
  if (id) {
    consume(event);
    deps.inlineRename(id);
  }
}
