import {
  BoardApiError,
  boardRequest,
  type BoardDocument,
  type BoardItem,
  type BoardLaunch,
} from './boardApi';
import { t } from '../i18n';

function button(label: string, action: () => void): HTMLButtonElement {
  const element = document.createElement('button');
  element.type = 'button';
  element.textContent = label;
  element.addEventListener('click', action);
  return element;
}

export function renderBoardSettings(
  host: HTMLElement,
  board: BoardDocument,
  save: (patch: object) => Promise<void>,
  close: () => void,
  error: (error: unknown) => void,
  remove: () => void,
): void {
  const heading = document.createElement('strong');
  heading.textContent = t('boards.boardSettings');
  const hint = document.createElement('p');
  hint.textContent = t('boards.sessionDefaultsHint');
  const form = document.createElement('form');
  form.className = 'board-session-form';
  const values: Record<string, string | boolean> = {};
  for (const field of ['workingDirectory', 'launchCommand', 'sessionProfile'] as const) {
    const label = document.createElement('label');
    label.textContent = t(`boards.${field}`);
    const input = document.createElement('input');
    input.name = field;
    input.value = board[field] ?? '';
    values[field] = input.value;
    input.addEventListener('input', () => {
      values[field] = input.value;
    });
    label.append(input);
    form.append(label);
  }
  const label = document.createElement('label');
  const control = document.createElement('input');
  control.type = 'checkbox';
  control.name = 'appServerControlOnly';
  control.checked = board.appServerControlOnly ?? false;
  values.appServerControlOnly = control.checked;
  control.addEventListener('change', () => {
    values.appServerControlOnly = control.checked;
  });
  label.append(control, document.createTextNode(t('boards.appServerControlOnly')));
  const submit = document.createElement('button');
  submit.type = 'submit';
  submit.textContent = t('boards.saveSettings');
  form.append(label, submit);
  form.addEventListener('submit', (event) => {
    event.preventDefault();
    submit.disabled = true;
    void save(values)
      .then(close)
      .catch(error)
      .finally(() => {
        submit.disabled = false;
      });
  });
  host.replaceChildren(
    heading,
    button('×', close),
    hint,
    form,
    button(t('boards.deleteBoard'), remove),
  );
  host.hidden = false;
  form.querySelector('input')?.focus();
}

export class BoardSessions {
  private readonly requests = new Map<
    string,
    { id: string; revision: number; entryId: string | undefined }
  >();
  private readonly active = new Set<string>();

  async launch(
    board: BoardDocument,
    item: BoardItem,
    entryId?: string,
  ): Promise<BoardLaunch | null> {
    const key = `${board.id}/${item.id}/${entryId ?? ''}`;
    if (this.active.has(key)) return null;
    const request = this.requests.get(key) ?? {
      id: crypto.randomUUID().replace(/-/g, ''),
      revision: item.revision,
      entryId,
    };
    this.requests.set(key, request);
    this.active.add(key);
    try {
      const launch = await boardRequest<BoardLaunch>(
        `/${board.id}/items/${item.id}/launch`,
        'POST',
        {
          requestId: request.id,
          expectedRevision: request.revision,
          entryId: request.entryId,
        },
      );
      item.launches = [launch, ...(item.launches ?? []).filter((entry) => entry.id !== launch.id)];
      this.requests.delete(key);
      return launch;
    } catch (error) {
      if (error instanceof BoardApiError && [400, 404, 409].includes(error.status))
        this.requests.delete(key);
      throw error;
    } finally {
      this.active.delete(key);
    }
  }
}

export function hasBoardSessionSettings(board: BoardDocument, item: BoardItem): boolean {
  return (
    !!(item.workingDirectory ?? board.workingDirectory) &&
    !!(
      (item.launchCommand ?? board.launchCommand) ||
      (item.appServerControlOnly ?? board.appServerControlOnly)
    )
  );
}

export function renderBoardLaunches(
  host: HTMLElement,
  item: BoardItem,
  start: () => void,
  close: () => void,
): void {
  const heading = document.createElement('strong');
  heading.textContent = t('boards.sessions');
  const list = document.createElement('div');
  list.className = 'board-launch-list';
  for (const launch of item.launches ?? []) {
    const row = document.createElement('div');
    const state = document.createElement('p');
    state.textContent = `${launch.name} · ${t(`boards.launchState.${launch.state}`)}`;
    row.append(state);
    if (launch.error) {
      const error = document.createElement('p');
      error.textContent = launch.error;
      row.append(error);
    }
    if (launch.sessionId) {
      const link = document.createElement('a');
      link.href = `/?session=${encodeURIComponent(launch.sessionId)}`;
      link.target = '_blank';
      link.rel = 'noopener';
      link.textContent = t('boards.openSession');
      row.append(link);
    }
    list.append(row);
  }
  host.replaceChildren(heading, button('×', close), list, button(t('boards.newSession'), start));
  host.hidden = false;
}
