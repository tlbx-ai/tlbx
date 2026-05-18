import type { Session } from '../../types';
import { dom } from '../../state';
import { getSessionDisplayInfo } from './sessionList';

export function syncSidebarSessionDisplayText(session: Session): boolean {
  const host = dom.sessionList;
  if (!host) {
    return false;
  }

  const items = Array.from(
    host.querySelectorAll<HTMLElement>('.session-item[data-session-id]'),
  ).filter((item) => item.dataset.sessionId === session.id);

  if (items.length === 0) {
    return true;
  }

  const displayInfo = getSessionDisplayInfo(session);
  for (const item of items) {
    const title = item.querySelector<HTMLElement>('.session-title');
    const titleRow = item.querySelector<HTMLElement>('.session-title-row');
    if (!title || !titleRow) {
      return false;
    }

    if (title.textContent !== displayInfo.primary) {
      title.textContent = displayInfo.primary;
    }

    let subtitle = item.querySelector<HTMLElement>('.session-subtitle');
    if (displayInfo.secondary) {
      if (!subtitle) {
        subtitle = document.createElement('div');
        subtitle.className = 'session-subtitle';
        titleRow.appendChild(subtitle);
      }
      if (subtitle.textContent !== displayInfo.secondary) {
        subtitle.textContent = displayInfo.secondary;
      }
    } else {
      subtitle?.remove();
    }

    syncSidebarSessionTopic(item, session.topic);
    syncSidebarSessionNotes(item, session.notes);
  }

  return true;
}

function syncSidebarSessionTopic(item: HTMLElement, value: string | null | undefined): void {
  const topic = normalizeSessionText(value);
  let topicEl = item.querySelector<HTMLDivElement>('.session-topic');
  const info = item.querySelector<HTMLElement>('.session-info');

  if (!topic) {
    topicEl?.remove();
    return;
  }

  if (!topicEl && info) {
    topicEl = document.createElement('div');
    topicEl.className = 'session-topic';
    const processInfo = info.querySelector<HTMLElement>('.session-process-info');
    if (processInfo) {
      info.insertBefore(topicEl, processInfo);
    } else {
      info.appendChild(topicEl);
    }
  }

  if (topicEl && topicEl.textContent !== topic) {
    topicEl.textContent = topic;
    topicEl.title = topic;
  }
}

function syncSidebarSessionNotes(item: HTMLElement, value: string | null | undefined): void {
  const notes = normalizeSessionText(value);
  const notesInput = item.querySelector<HTMLTextAreaElement>('.session-notes-input');
  if (notesInput && document.activeElement !== notesInput && notesInput.value !== (notes ?? '')) {
    notesInput.value = notes ?? '';
  }
  item
    .querySelector<HTMLButtonElement>('.session-notes-toggle')
    ?.classList.toggle('has-notes', notes !== null);
}

function normalizeSessionText(value: string | null | undefined): string | null {
  const normalized = value?.trim();
  return normalized ? normalized : null;
}

export function syncSidebarActiveSessionState(activeSessionId: string | null): boolean {
  const host = dom.sessionList;
  if (!host) {
    return false;
  }

  const items = host.querySelectorAll<HTMLElement>('.session-item[data-session-id]');
  for (const item of items) {
    const isActive = item.dataset.sessionId === activeSessionId;
    item.classList.toggle('active', isActive);
    item.setAttribute('aria-current', isActive ? 'true' : 'false');
  }

  return true;
}
