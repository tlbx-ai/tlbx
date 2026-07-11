import { icon } from '../../constants';
import { t } from '../i18n';
import type { InputHistoryEntry, InputHistoryKind } from './inputHistoryApi';

export interface InputHistoryPanelActions {
  onDelete: (entry: InputHistoryEntry) => void;
  onReplay: (entry: InputHistoryEntry) => void;
  includeSessionName?: boolean;
}

const KIND_LABEL_KEYS: Record<InputHistoryKind, string> = {
  prompt: 'inputHistory.kindPrompt',
  textPaste: 'inputHistory.kindPaste',
  imagePaste: 'inputHistory.kindImage',
  fileUpload: 'inputHistory.kindFile',
};

export function renderInputHistoryPanel(
  container: HTMLElement,
  entries: readonly InputHistoryEntry[],
  actions: InputHistoryPanelActions,
): void {
  const fragment = document.createDocumentFragment();
  const list = document.createElement('div');
  list.className = 'history-entry-list input-history-list';

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
  item.className = 'history-item input-history-item';
  item.dataset.id = entry.id;
  item.tabIndex = 0;
  item.setAttribute('role', 'button');
  item.setAttribute(
    'aria-label',
    `${t('inputHistory.replay')}: ${formatInputHistoryPreview(entry)}`,
  );

  if (entry.kind === 'imagePaste' && entry.path) {
    const thumbnail = document.createElement('img');
    thumbnail.className = 'input-history-thumbnail';
    thumbnail.alt = '';
    thumbnail.loading = 'lazy';
    thumbnail.decoding = 'async';
    thumbnail.src = `/api/input-history/${encodeURIComponent(entry.id)}/content`;
    item.appendChild(thumbnail);
  }

  const info = document.createElement('div');
  info.className = 'history-item-info input-history-info';

  const primary = document.createElement('div');
  primary.className = 'history-item-primary';
  const mode = document.createElement('span');
  mode.className = `history-item-mode input-history-kind input-history-kind-${entry.kind}`;
  mode.textContent = t(KIND_LABEL_KEYS[entry.kind]);
  primary.appendChild(mode);

  const label = document.createElement('span');
  label.className = 'history-item-label input-history-preview';
  label.textContent = formatInputHistoryPreview(entry);
  label.title = entry.text ?? entry.path ?? entry.displayName ?? '';
  primary.appendChild(label);
  info.appendChild(primary);

  const secondary = document.createElement('div');
  secondary.className = 'history-item-secondary input-history-meta';
  secondary.textContent = formatInputHistoryMeta(entry, Date.now(), actions.includeSessionName);
  info.appendChild(secondary);
  item.appendChild(info);

  const itemActions = document.createElement('div');
  itemActions.className = 'history-item-actions input-history-actions';

  const replay = document.createElement('button');
  replay.type = 'button';
  replay.className = 'history-item-rename input-history-replay';
  replay.title = t('inputHistory.replay');
  replay.setAttribute('aria-label', t('inputHistory.replay'));
  replay.innerHTML = icon('update');
  replay.addEventListener('click', (event) => {
    event.stopPropagation();
    actions.onReplay(entry);
  });
  itemActions.appendChild(replay);

  const remove = document.createElement('button');
  remove.type = 'button';
  remove.className = 'history-item-delete input-history-delete';
  remove.title = t('inputHistory.remove');
  remove.setAttribute('aria-label', t('inputHistory.remove'));
  remove.innerHTML = icon('close');
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
