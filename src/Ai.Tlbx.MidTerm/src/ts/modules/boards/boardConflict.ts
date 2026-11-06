import type { BoardItem } from './boardApi';
import { t } from '../i18n';

interface ConflictActions {
  keepMine: () => Promise<void>;
  useServer: () => Promise<void>;
  saveCopy?: () => Promise<void>;
  dismiss: () => void;
  titleKey?: string;
  hintKey?: string;
}

type ConflictVersion = Pick<BoardItem, 'title' | 'text' | 'entries'>;

function describe(item: ConflictVersion): string {
  const entries = item.entries
    .map(
      (entry) => `${entry.done ? '✓ ' : '• '}${entry.text}${entry.date ? ` · ${entry.date}` : ''}`,
    )
    .join('\n');
  return [item.title, item.text, entries].filter(Boolean).join('\n\n');
}

/** A conflicting revision always leaves the user's text available until an explicit choice. */
export function showBoardConflict(
  root: HTMLElement,
  local: ConflictVersion,
  remote: ConflictVersion | null,
  actions: ConflictActions,
): () => void {
  const overlay = document.createElement('div');
  overlay.className = 'boards-conflict-overlay';
  const panel = document.createElement('section');
  panel.className = 'boards-conflict';
  panel.setAttribute('role', 'alertdialog');
  panel.setAttribute('aria-modal', 'true');
  const title = document.createElement('h2');
  title.id = `board-conflict-${crypto.randomUUID()}`;
  title.textContent = t(actions.titleKey ?? 'boards.conflictTitle');
  panel.setAttribute('aria-labelledby', title.id);
  const hint = document.createElement('p');
  hint.textContent = t(actions.hintKey ?? (remote ? 'boards.conflictHint' : 'boards.deletedHint'));
  const comparison = document.createElement('div');
  comparison.className = 'boards-conflict-comparison';
  for (const [labelKey, item] of [
    ['boards.yourDraft', local],
    ['boards.serverVersion', remote],
  ] as const) {
    const label = document.createElement('label');
    label.textContent = t(labelKey);
    const preview = document.createElement('textarea');
    preview.readOnly = true;
    preview.value = item ? describe(item) : t('boards.itemDeleted');
    label.append(preview);
    comparison.append(label);
  }
  const error = document.createElement('p');
  error.setAttribute('role', 'status');
  const buttons = document.createElement('div');
  buttons.className = 'boards-conflict-actions';
  const add = (label: string, action: () => Promise<void>): HTMLButtonElement => {
    const button = document.createElement('button');
    button.type = 'button';
    button.textContent = label;
    button.addEventListener('click', () => {
      for (const entry of buttons.querySelectorAll('button')) entry.disabled = true;
      void action().catch((failure: unknown) => {
        error.textContent = failure instanceof Error ? failure.message : String(failure);
        for (const entry of buttons.querySelectorAll('button')) entry.disabled = false;
      });
    });
    buttons.append(button);
    return button;
  };
  if (remote) add(t('boards.keepMine'), actions.keepMine).dataset.conflictAction = 'mine';
  add(
    t(remote ? 'boards.useServer' : 'boards.discardDraft'),
    actions.useServer,
  ).dataset.conflictAction = 'server';
  if (actions.saveCopy) add(t('boards.saveCopy'), actions.saveCopy).dataset.conflictAction = 'copy';
  const later = document.createElement('button');
  later.type = 'button';
  later.textContent = t('boards.later');
  later.addEventListener('click', actions.dismiss);
  buttons.append(later);
  panel.append(title, hint, comparison, error, buttons);
  overlay.append(panel);
  overlay.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') {
      event.stopPropagation();
      actions.dismiss();
      return;
    }
    if (event.key !== 'Tab') return;
    const focusable = [...panel.querySelectorAll<HTMLElement>('textarea,button:not(:disabled)')];
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last?.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first?.focus();
    }
  });
  root.append(overlay);
  buttons.querySelector('button')?.focus();
  return () => {
    overlay.remove();
  };
}
