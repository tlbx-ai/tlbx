import { t } from '../i18n';
import type { InputHistoryEntry } from './inputHistoryApi';

const REPLAY_ICON =
  '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m9 7-4 4 4 4"/><path d="M5 11h9a5 5 0 0 1 5 5v1"/></svg>';
const DELETE_ICON =
  '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h16"/><path d="M9 7V4h6v3"/><path d="m7 7 1 13h8l1-13"/><path d="M10 11v5M14 11v5"/></svg>';

export interface InputHistoryPanelActions {
  onDelete: (entry: InputHistoryEntry) => void;
  onReplay: (entry: InputHistoryEntry) => void;
  includeSessionName?: boolean;
}

export function renderInputHistoryPanel(
  container: HTMLElement,
  entries: readonly InputHistoryEntry[],
  actions: InputHistoryPanelActions,
): void {
  const fragment = document.createDocumentFragment();
  const list = document.createElement('div');
  list.className = 'input-history-timeline';

  for (const entry of entries) {
    list.appendChild(createInputHistoryItem(entry, actions));
  }

  fragment.appendChild(list);
  container.replaceChildren(fragment);
}

function createInputHistoryItem(
  entry: InputHistoryEntry,
  actions: InputHistoryPanelActions,
): HTMLDivElement {
  const item = document.createElement('div');
  item.className = 'input-history-event';
  item.dataset.id = entry.id;
  item.tabIndex = 0;
  item.setAttribute('role', 'button');
  item.setAttribute(
    'aria-label',
    `${t('inputHistory.replay')}: ${formatInputHistoryPreview(entry)}`,
  );

  const marker = document.createElement('span');
  marker.className = 'input-history-marker';
  marker.setAttribute('aria-hidden', 'true');
  item.appendChild(marker);

  const info = document.createElement('div');
  info.className = 'input-history-info';

  const timestamp = document.createElement('time');
  timestamp.className = 'input-history-timestamp';
  timestamp.dateTime = entry.createdAt;
  timestamp.textContent = formatInputHistoryTimestamp(entry.createdAt);
  info.appendChild(timestamp);

  if (entry.kind === 'imagePaste' && entry.path) {
    const thumbnail = document.createElement('img');
    thumbnail.className = 'input-history-thumbnail';
    thumbnail.alt = entry.displayName?.trim() || t('inputHistory.kindImage');
    thumbnail.loading = 'lazy';
    thumbnail.decoding = 'async';
    thumbnail.src = `/api/input-history/${encodeURIComponent(entry.id)}/content`;
    info.appendChild(thumbnail);
  } else {
    const text = document.createElement('div');
    text.className = 'input-history-text';
    text.textContent = formatInputHistoryText(entry);
    info.appendChild(text);
  }

  if (actions.includeSessionName) {
    const session = document.createElement('div');
    session.className = 'input-history-session';
    session.textContent = entry.sessionName?.trim() || entry.sessionId;
    info.appendChild(session);
  }
  item.appendChild(info);

  const itemActions = document.createElement('div');
  itemActions.className = 'input-history-actions';

  const replay = document.createElement('button');
  replay.type = 'button';
  replay.className = 'input-history-action input-history-replay';
  replay.title = t('inputHistory.replay');
  replay.setAttribute('aria-label', t('inputHistory.replay'));
  replay.innerHTML = REPLAY_ICON;
  replay.addEventListener('click', (event) => {
    event.stopPropagation();
    actions.onReplay(entry);
  });
  itemActions.appendChild(replay);

  const remove = document.createElement('button');
  remove.type = 'button';
  remove.className = 'input-history-action input-history-delete';
  remove.title = t('inputHistory.remove');
  remove.setAttribute('aria-label', t('inputHistory.remove'));
  remove.innerHTML = DELETE_ICON;
  remove.addEventListener('click', (event) => {
    event.stopPropagation();
    actions.onDelete(entry);
  });
  itemActions.appendChild(remove);
  item.appendChild(itemActions);

  const replayEntry = (): void => {
    actions.onReplay(entry);
  };
  item.addEventListener('click', replayEntry);
  item.addEventListener('keydown', (event) => {
    if (event.target !== item) {
      return;
    }
    if (event.key !== 'Enter' && event.key !== ' ') {
      return;
    }
    event.preventDefault();
    replayEntry();
  });
  return item;
}

export function formatInputHistoryPreview(entry: InputHistoryEntry): string {
  const text = entry.text?.replace(/\s+/g, ' ').trim();
  if (text) {
    return text.length > 140 ? `${text.slice(0, 139)}…` : text;
  }

  return entry.displayName?.trim() || fileNameFromPath(entry.path) || t('inputHistory.untitled');
}

export function formatInputHistoryText(entry: InputHistoryEntry): string {
  return (
    entry.text ??
    entry.displayName?.trim() ??
    fileNameFromPath(entry.path) ??
    t('inputHistory.untitled')
  );
}

export function formatInputHistoryTimestamp(value: string): string {
  const timestamp = new Date(value);
  if (!Number.isFinite(timestamp.getTime())) return t('inputHistory.timeUnknown');

  const date = timestamp.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
  const time = timestamp.toLocaleTimeString(undefined, {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  });
  return `${date} · ${time}`;
}

export function formatInputHistoryMeta(
  entry: InputHistoryEntry,
  now = Date.now(),
  includeSessionName = true,
): string {
  const session = entry.sessionName?.trim() || entry.sessionId;
  const age = formatRelativeAge(entry.createdAt, now);
  return includeSessionName ? `${session} · ${age}` : age;
}

export function formatRelativeAge(value: string, now = Date.now()): string {
  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) {
    return t('inputHistory.timeUnknown');
  }

  const elapsedSeconds = Math.max(0, Math.floor((now - timestamp) / 1000));
  if (elapsedSeconds < 60) {
    return t('inputHistory.timeNow');
  }
  if (elapsedSeconds < 3600) {
    return formatCount('inputHistory.timeMinutes', Math.floor(elapsedSeconds / 60));
  }
  if (elapsedSeconds < 86400) {
    return formatCount('inputHistory.timeHours', Math.floor(elapsedSeconds / 3600));
  }
  return formatCount('inputHistory.timeDays', Math.floor(elapsedSeconds / 86400));
}

function formatCount(key: string, count: number): string {
  return t(key).replace('{count}', count.toString());
}

function fileNameFromPath(path: string | null): string | null {
  if (!path) {
    return null;
  }
  const segments = path.replace(/\\/g, '/').split('/');
  return segments[segments.length - 1] || path;
}
