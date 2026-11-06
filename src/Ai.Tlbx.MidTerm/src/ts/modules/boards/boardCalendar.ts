import { reconcileKeyedChildren } from '../../utils/domReconcile';
import { boardRequest, type BoardItem } from './boardApi';
import { t } from '../i18n';

interface CalendarDay {
  date: string;
  day: number;
  inMonth: boolean;
  isoYear: number;
  week: number;
  quarter: number;
}
interface CalendarMonth {
  year: number;
  month: number;
  days: CalendarDay[];
}
interface CalendarState {
  month: string;
  selected: string;
  item: BoardItem;
  data: CalendarMonth | null;
  changed: (item: BoardItem) => void;
  selectedDate: () => void;
}

const states = new WeakMap<HTMLElement, CalendarState>();
const cache = new Map<string, Promise<CalendarMonth>>();

function monthDate(year: number, month: number): Date {
  const date = new Date(0);
  date.setUTCFullYear(year, month, 1);
  return date;
}

function today(): string {
  const date = new Date();
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
}

export function getSelectedCalendarDate(host: HTMLElement): string {
  return states.get(host)?.selected ?? today();
}

export function renderBoardCalendar(
  host: HTMLElement,
  item: BoardItem,
  changed: (item: BoardItem) => void,
  selectedDate: () => void,
): void {
  const month = item.calendarMonth ?? today().slice(0, 7);
  let state = states.get(host);
  if (!state) {
    state = { month: '', selected: today(), item, data: null, changed, selectedDate };
    states.set(host, state);
    createCalendar(host);
  }
  Object.assign(state, { item, changed, selectedDate });
  if (state.month !== month) {
    state.month = month;
    if (!state.selected.startsWith(month)) state.selected = `${month}-01`;
    let request = cache.get(month);
    if (!request) {
      request = boardRequest<CalendarMonth>(`/calendar?month=${month}`);
      cache.set(month, request);
      if (cache.size > 24) {
        const oldest = cache.keys().next().value;
        if (oldest) cache.delete(oldest);
      }
    }
    const current = state;
    void request
      .then((data) => {
        if (current.month !== month || !host.isConnected) return;
        current.data = data;
        patchCalendar(host, current);
        current.selectedDate();
      })
      .catch((error: unknown) => {
        cache.delete(month);
        const title = host.querySelector('.board-calendar-month');
        if (title) title.textContent = error instanceof Error ? error.message : String(error);
      });
  } else patchCalendar(host, state);
}

function createCalendar(host: HTMLElement): void {
  const header = document.createElement('div');
  header.className = 'board-calendar-header';
  for (const offset of [-1, 1]) {
    const button = document.createElement('button');
    button.type = 'button';
    button.textContent = offset === -1 ? '‹' : '›';
    button.setAttribute(
      'aria-label',
      t(offset === -1 ? 'boards.previousMonth' : 'boards.nextMonth'),
    );
    button.addEventListener('click', () => {
      const state = states.get(host);
      if (!state?.data) return;
      const date = monthDate(state.data.year, state.data.month - 1 + offset);
      if (date.getUTCFullYear() < 2 || date.getUTCFullYear() > 9998) return;
      state.item.calendarMonth = `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, '0')}`;
      state.changed(state.item);
      renderBoardCalendar(host, state.item, state.changed, state.selectedDate);
    });
    header.append(button);
  }
  const title = document.createElement('span');
  title.className = 'board-calendar-month';
  header.insertBefore(title, header.lastChild);
  const weekdays = document.createElement('div');
  weekdays.className = 'board-calendar-weekdays';
  const spacer = document.createElement('span');
  spacer.textContent = t('boards.weekShort');
  weekdays.append(spacer);
  for (let index = 0; index < 7; index++) {
    const label = document.createElement('span');
    label.textContent = new Intl.DateTimeFormat(undefined, {
      weekday: 'narrow',
      timeZone: 'UTC',
    }).format(new Date(Date.UTC(2024, 0, 1 + index)));
    weekdays.append(label);
  }
  const grid = document.createElement('div');
  grid.className = 'board-calendar-grid';
  grid.addEventListener('keydown', (event) => {
    const state = states.get(host);
    if (!state?.data) return;
    const offsets: Record<string, number> = {
      ArrowLeft: -1,
      ArrowRight: 1,
      ArrowUp: -7,
      ArrowDown: 7,
    };
    const offset = offsets[event.key];
    if (!offset) return;
    const index = state.data.days.findIndex((day) => day.date === state.selected);
    const day = state.data.days[index + offset];
    if (!day) return;
    event.preventDefault();
    state.selected = day.date;
    patchCalendar(host, state);
    state.selectedDate();
    grid.querySelector<HTMLButtonElement>(`[data-date="${day.date}"]`)?.focus();
  });
  host.append(header, weekdays, grid);
}

function patchCalendar(host: HTMLElement, state: CalendarState): void {
  const title = host.querySelector('.board-calendar-month');
  const grid = host.querySelector<HTMLElement>('.board-calendar-grid');
  if (!state.data || !title || !grid) return;
  title.textContent = new Intl.DateTimeFormat(undefined, {
    month: 'long',
    year: 'numeric',
    timeZone: 'UTC',
  }).format(monthDate(state.data.year, state.data.month - 1));
  reconcileKeyedChildren(grid, state.data.days, {
    key: (day) => day.date,
    create: (day) => {
      const cell = document.createElement('div');
      const week = document.createElement('span');
      const button = document.createElement('button');
      button.type = 'button';
      button.dataset.date = day.date;
      button.addEventListener('click', () => {
        state.selected = day.date;
        patchCalendar(host, state);
        state.selectedDate();
      });
      cell.append(week, button);
      return cell;
    },
    patch: (cell, day, index) => {
      const week = cell.querySelector('span');
      const button = cell.querySelector('button');
      if (!week || !button) return;
      week.textContent = String(day.week);
      week.hidden = index % 7 !== 0;
      cell.className = index % 7 === 0 ? 'board-calendar-week-start' : '';
      button.textContent = String(day.day);
      button.title = day.date;
      button.setAttribute('aria-label', day.date);
      button.setAttribute('aria-pressed', String(day.date === state.selected));
      button.tabIndex = day.date === state.selected ? 0 : -1;
      button.classList.toggle('is-other-month', !day.inMonth);
      button.classList.toggle(
        'has-entries',
        state.item.entries.some((entry) => entry.date === day.date),
      );
    },
  });
}
