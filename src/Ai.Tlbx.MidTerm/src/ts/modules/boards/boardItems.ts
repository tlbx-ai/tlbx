import { reconcileKeyedChildren } from '../../utils/domReconcile';
import type { BoardEntry, BoardItem } from './boardApi';
import { t } from '../i18n';
import { createBoardBrowserFrame, mountBoardBrowser, resetBoardBrowser } from './boardBrowser';
import { getSelectedCalendarDate, renderBoardCalendar } from './boardCalendar';

const itemContext = new WeakMap<HTMLElement, BoardItem>();
const entryContext = new WeakMap<HTMLElement, { entry: BoardEntry; item: BoardItem }>();

export interface BoardItemActions {
  changed: (item: BoardItem) => void;
  remove: (item: BoardItem) => void;
  launch: (item: BoardItem, entryId?: string) => void;
  settings: (item: BoardItem) => void;
  claim: (item: BoardItem) => void;
}

function required<T>(value: T | null | undefined): T {
  if (value == null) throw new Error('Board element is no longer available.');
  return value;
}

function button(label: string, action: () => void): HTMLButtonElement {
  const element = document.createElement('button');
  element.type = 'button';
  element.textContent = label;
  element.addEventListener('click', action);
  return element;
}

function patchInput(input: HTMLInputElement | HTMLTextAreaElement, value: string): void {
  if (document.activeElement !== input && input.value !== value) input.value = value;
}

export function renderBoardItems(
  plane: HTMLElement,
  items: BoardItem[],
  boardId: string,
  actions: BoardItemActions,
): void {
  reconcileKeyedChildren(plane, items, {
    key: (item) => item.id,
    create: (item) => createItem(item, actions),
    patch: (element, item) => {
      patchItem(element, item, boardId, actions);
    },
  });
}

function createItem(item: BoardItem, actions: BoardItemActions): HTMLElement {
  const tile = document.createElement('article');
  tile.className = 'board-item';
  tile.tabIndex = 0;
  tile.setAttribute('role', 'group');
  tile.dataset.id = item.id;
  const header = document.createElement('header');
  header.className = 'board-item-grip';
  const kind = document.createElement('span');
  kind.className = 'board-item-kind';
  header.append(kind);
  header.append(
    button('▷', () => {
      actions.launch(required(itemContext.get(tile)));
    }),
    button('⌖', () => {
      actions.claim(required(itemContext.get(tile)));
    }),
    button('⋯', () => {
      actions.settings(required(itemContext.get(tile)));
    }),
    button('×', () => {
      actions.remove(required(itemContext.get(tile)));
    }),
  );
  header.querySelectorAll('button').forEach((element, index) => {
    element.title = t(
      ['boards.launch', 'boards.controlHere', 'boards.itemSettings', 'boards.deleteItem'][index] ??
        'boards.itemSettings',
    );
    element.setAttribute('aria-label', element.title);
    if (index === 1) element.classList.add('board-control-here');
    element.addEventListener('pointerdown', (event) => {
      event.stopPropagation();
    });
  });
  const title = document.createElement('input');
  title.className = 'board-item-title';
  title.placeholder = t('boards.titlePlaceholder');
  title.setAttribute('aria-label', t('boards.itemTitle'));
  title.maxLength = 256;
  title.addEventListener('input', () => {
    const current = required(itemContext.get(tile));
    current.title = title.value;
    actions.changed(current);
  });
  const text = document.createElement('textarea');
  text.className = 'board-item-text';
  text.placeholder = t('boards.notePlaceholder');
  text.setAttribute('aria-label', t('boards.noteText'));
  text.addEventListener('input', () => {
    const current = required(itemContext.get(tile));
    current.text = text.value;
    actions.changed(current);
  });
  const entries = document.createElement('div');
  entries.className = 'board-entry-list';
  const calendar = document.createElement('div');
  calendar.className = 'board-calendar';
  const add = document.createElement('input');
  add.className = 'board-entry-add';
  add.placeholder = t('boards.addEntry');
  add.setAttribute('aria-label', t('boards.addEntry'));
  title.addEventListener('keydown', (event) => {
    if (event.key !== 'Enter' || event.isComposing || itemContext.get(tile)?.kind !== 'todo')
      return;
    event.preventDefault();
    event.stopPropagation();
    add.focus();
  });
  add.addEventListener('keydown', (event) => {
    if (event.key !== 'Enter' || event.isComposing || !add.value.trim()) return;
    event.preventDefault();
    event.stopPropagation();
    const current = required(itemContext.get(tile));
    current.entries.push({
      id: crypto.randomUUID(),
      text: add.value.trim(),
      done: false,
      ...(current.kind === 'calendar' ? { date: getSelectedCalendarDate(calendar) } : {}),
    });
    add.value = '';
    actions.changed(current);
    patchEntries(entries, current, actions);
  });
  const notice = document.createElement('div');
  notice.className = 'board-browser-notice';
  const description = document.createElement('p');
  description.textContent = t('boards.configureBrowser');
  notice.append(
    description,
    button(t('boards.itemSettings'), () => {
      actions.settings(required(itemContext.get(tile)));
    }),
    button(t('boards.retry'), () => {
      const current = itemContext.get(tile);
      const frame = tile.querySelector<HTMLIFrameElement>('iframe');
      if (!current || !frame) return;
      resetBoardBrowser(frame);
      void mountBoardBrowser(frame, tile.dataset.boardId ?? '', current);
    }),
  );
  const resize = document.createElement('div');
  resize.className = 'board-resize';
  resize.setAttribute('aria-hidden', 'true');
  tile.append(header, title, text, calendar, entries, add, notice, resize);
  return tile;
}

function patchItem(
  tile: HTMLElement,
  item: BoardItem,
  boardId: string,
  actions: BoardItemActions,
): void {
  itemContext.set(tile, item);
  tile.dataset.boardId = boardId;
  tile.dataset.kind = item.kind;
  tile.setAttribute('aria-label', item.title || t(`boards.${item.kind}`));
  tile.dataset.color = item.color;
  tile.style.transform = `translate(${item.x}px, ${item.y}px)`;
  tile.style.width = `${item.width}px`;
  tile.style.height = `${item.height}px`;
  tile.style.zIndex = String(item.z);
  required(tile.querySelector<HTMLElement>('.board-item-kind')).textContent = t(
    `boards.${item.kind}`,
  );
  patchInput(required(tile.querySelector<HTMLInputElement>('.board-item-title')), item.title);
  patchInput(required(tile.querySelector<HTMLTextAreaElement>('.board-item-text')), item.text);
  patchEntries(required(tile.querySelector<HTMLElement>('.board-entry-list')), item, actions);
  let frame = tile.querySelector<HTMLIFrameElement>('iframe');
  if (item.kind === 'browser') {
    if (!frame) {
      frame = createBoardBrowserFrame();
      tile.insertBefore(frame, tile.querySelector('.board-resize'));
    }
    void mountBoardBrowser(frame, boardId, item);
  } else frame?.remove();
}

function patchEntries(parent: HTMLElement, item: BoardItem, actions: BoardItemActions): void {
  const calendar = parent.parentElement?.querySelector<HTMLElement>('.board-calendar');
  if (item.kind === 'calendar' && calendar)
    renderBoardCalendar(calendar, item, actions.changed, () => {
      patchEntries(parent, item, actions);
    });
  const entries =
    item.kind === 'calendar' && calendar
      ? item.entries.filter(
          (entry) => !entry.date || entry.date === getSelectedCalendarDate(calendar),
        )
      : item.entries;
  reconcileKeyedChildren(parent, entries, {
    key: (entry) => entry.id,
    create: () => {
      const row = document.createElement('div');
      row.className = 'board-entry';
      const done = document.createElement('input');
      done.type = 'checkbox';
      done.setAttribute('aria-label', t('boards.done'));
      const text = document.createElement('textarea');
      text.rows = 1;
      text.className = 'board-entry-text';
      text.setAttribute('aria-label', t('boards.entryText'));
      const date = document.createElement('input');
      date.type = 'date';
      date.className = 'board-entry-date';
      date.setAttribute('aria-label', t('boards.date'));
      const period = document.createElement('input');
      period.className = 'board-entry-period';
      period.placeholder = '2027 W12 / Q1';
      period.setAttribute('aria-label', t('boards.period'));
      const sender = document.createElement('input');
      sender.className = 'board-mail-sender';
      sender.placeholder = t('boards.sender');
      sender.setAttribute('aria-label', t('boards.sender'));
      sender.addEventListener('input', () => {
        const current = required(entryContext.get(row));
        current.entry.sender = sender.value || null;
        actions.changed(current.item);
      });
      const url = document.createElement('input');
      url.className = 'board-mail-url';
      url.type = 'url';
      url.placeholder = t('boards.mailLink');
      url.setAttribute('aria-label', t('boards.mailLink'));
      url.addEventListener('change', () => {
        const current = required(entryContext.get(row));
        current.entry.url = url.value || null;
        actions.changed(current.item);
      });
      const open = document.createElement('a');
      open.className = 'board-mail-open';
      open.textContent = '↗';
      open.target = '_blank';
      open.rel = 'noopener noreferrer';
      open.setAttribute('aria-label', t('boards.openMail'));
      done.addEventListener('change', () => {
        required(entryContext.get(row)).entry.done = done.checked;
        row.classList.toggle('is-done', done.checked);
        actions.changed(required(entryContext.get(row)).item);
      });
      text.addEventListener('keydown', (event) => {
        if (event.key !== 'Enter' || event.isComposing) return;
        const current = required(entryContext.get(row));
        if (current.item.kind !== 'todo') return;
        event.preventDefault();
        event.stopPropagation();
        const start = text.selectionStart;
        const end = text.selectionEnd;
        const entry: BoardEntry = {
          id: crypto.randomUUID(),
          text: text.value.slice(end),
          done: false,
        };
        current.entry.text = text.value.slice(0, start);
        text.value = current.entry.text;
        current.item.entries.splice(current.item.entries.indexOf(current.entry) + 1, 0, entry);
        patchEntries(parent, current.item, actions);
        actions.changed(current.item);
        const next = parent.querySelector<HTMLTextAreaElement>(
          `[data-entry-id="${entry.id}"] .board-entry-text`,
        );
        next?.focus();
        next?.setSelectionRange(0, 0);
      });
      text.addEventListener('input', () => {
        required(entryContext.get(row)).entry.text = text.value;
        actions.changed(required(entryContext.get(row)).item);
      });
      date.addEventListener('change', () => {
        required(entryContext.get(row)).entry.date = date.value || null;
        actions.changed(required(entryContext.get(row)).item);
      });
      period.addEventListener('change', () => {
        const entry = required(entryContext.get(row)).entry;
        const match = /^(\d{4})(?:\s+(?:W(\d{1,2})|Q([1-4])))?$/i.exec(period.value.trim());
        if (!match && period.value.trim()) {
          period.setCustomValidity(t('boards.invalidPeriod'));
          period.reportValidity();
          return;
        }
        period.setCustomValidity('');
        entry.year = match ? Number(match[1]) : null;
        entry.week = match?.[2] ? Number(match[2]) : null;
        entry.quarter = match?.[3] ? Number(match[3]) : null;
        actions.changed(required(entryContext.get(row)).item);
      });
      const remove = button('×', () => {
        const entry = required(entryContext.get(row)).entry;
        const current = required(entryContext.get(row)).item;
        current.entries = current.entries.filter((candidate) => candidate.id !== entry.id);
        actions.changed(required(entryContext.get(row)).item);
        patchEntries(parent, required(entryContext.get(row)).item, actions);
      });
      remove.setAttribute('aria-label', t('boards.deleteEntry'));
      const launch = button('▷', () => {
        const current = required(entryContext.get(row));
        actions.launch(current.item, current.entry.id);
      });
      launch.setAttribute('aria-label', t('boards.launch'));
      launch.className = 'board-entry-launch';
      row.append(done, text, date, period, sender, url, open, launch, remove);
      return row;
    },
    patch: (row, entry) => {
      entryContext.set(row, { entry, item });
      row.dataset.entryId = entry.id;
      const inputs = row.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>(
        'input, textarea',
      );
      required(row.querySelector<HTMLInputElement>('input[type="checkbox"]')).checked = entry.done;
      patchInput(required(inputs[1]), entry.text);
      patchInput(required(inputs[2]), entry.date ?? '');
      patchInput(
        required(inputs[3]),
        entry.year
          ? `${entry.year}${entry.week ? ` W${entry.week}` : entry.quarter ? ` Q${entry.quarter}` : ''}`
          : '',
      );
      row.classList.toggle('is-done', entry.done);
      const sender = row.querySelector<HTMLInputElement>('.board-mail-sender');
      const url = row.querySelector<HTMLInputElement>('.board-mail-url');
      const open = row.querySelector<HTMLAnchorElement>('.board-mail-open');
      if (sender) patchInput(sender, entry.sender ?? '');
      if (url) patchInput(url, entry.url ?? '');
      if (open) {
        open.hidden = !entry.url;
        if (entry.url) open.href = entry.url;
        else open.removeAttribute('href');
      }
      required(inputs[0]).setAttribute(
        'aria-label',
        t(item.kind === 'mail' ? 'boards.read' : 'boards.done'),
      );
    },
  });
}
