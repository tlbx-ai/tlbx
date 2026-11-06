/**
 * Dialog Utility
 *
 * Theme-aware modal dialogs to replace native browser confirm() and alert().
 * Reuses existing .modal CSS classes for consistent styling.
 */

import { t } from '../modules/i18n';
import { escapeHtml } from './dom';
import { registerBackButtonLayer } from '../modules/navigation/backButtonGuard';

interface ConfirmOptions {
  title?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  danger?: boolean;
}

interface AlertOptions {
  title?: string;
  okLabel?: string;
  details?: string;
  detailsLabel?: string;
}

interface TextPromptOptions {
  title?: string;
  message?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  placeholder?: string;
  initialValue?: string;
  validate?: (value: string) => string | null;
}

interface TextPromptElements {
  errorEl: HTMLElement;
  inputEl: HTMLInputElement;
}

/**
 * Show a themed confirmation dialog. Returns a promise that resolves to true (confirm) or false (cancel).
 */
export function showConfirm(message: string, options?: ConfirmOptions): Promise<boolean> {
  return new Promise((resolve) => {
    const previousFocus = document.activeElement as HTMLElement | null;
    const id = `confirm-${crypto.randomUUID()}`;
    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    let releaseBackButtonLayer: (() => void) | null = null;

    const confirmClass = options?.danger ? 'btn-danger' : 'btn-primary';
    const titleText = options?.title ?? t('dialog.confirm');
    const confirmText = options?.confirmLabel ?? t('dialog.ok');
    const cancelText = options?.cancelLabel ?? t('dialog.cancel');

    overlay.innerHTML = `
      <div class="modal dialog-modal">
        <div class="modal-content dialog-content" role="alertdialog" aria-modal="true" aria-labelledby="${id}-title" aria-describedby="${id}-message">
          <div class="modal-header">
            <h3 id="${id}-title">${escapeHtml(titleText)}</h3>
            <button class="modal-close" data-role="cancel" aria-label="${escapeHtml(cancelText)}">&times;</button>
          </div>
          <div class="modal-body">
            <p id="${id}-message" class="dialog-message">${escapeHtml(message)}</p>
          </div>
          <div class="modal-footer">
            <button class="btn-secondary" data-role="cancel">${escapeHtml(cancelText)}</button>
            <button class="${confirmClass}" data-role="confirm">${escapeHtml(confirmText)}</button>
          </div>
        </div>
      </div>
    `;

    function close(result: boolean): void {
      document.removeEventListener('keydown', onKey);
      releaseBackButtonLayer?.();
      releaseBackButtonLayer = null;
      overlay.remove();
      previousFocus?.focus({ preventScroll: true });
      resolve(result);
    }

    function onKey(e: KeyboardEvent): void {
      if (e.key === 'Escape') {
        e.preventDefault();
        close(false);
      } else if (e.key === 'Tab') {
        trapConfirmFocus(overlay, e);
      }
    }

    overlay.addEventListener('click', (e) => {
      const target = e.target as HTMLElement;
      if (target === overlay) {
        close(false);
        return;
      }
      const role = target.closest('[data-role]')?.getAttribute('data-role');
      if (role === 'confirm') close(true);
      else if (role === 'cancel') close(false);
    });

    document.addEventListener('keydown', onKey);
    document.body.appendChild(overlay);
    releaseBackButtonLayer = registerBackButtonLayer(() => {
      close(false);
    });

    const initialRole = options?.danger ? 'cancel' : 'confirm';
    overlay.querySelector<HTMLButtonElement>(`.modal-footer [data-role="${initialRole}"]`)?.focus();
  });
}

function trapConfirmFocus(overlay: HTMLElement, event: KeyboardEvent): void {
  const buttons = [...overlay.querySelectorAll<HTMLButtonElement>('button:not(:disabled)')];
  const first = buttons[0];
  const last = buttons[buttons.length - 1];
  if (event.shiftKey && document.activeElement === first) {
    event.preventDefault();
    last?.focus();
  } else if (!event.shiftKey && document.activeElement === last) {
    event.preventDefault();
    first?.focus();
  }
}

/**
 * Show a themed alert dialog. Returns a promise that resolves when dismissed.
 */
export function showAlert(message: string, options?: AlertOptions): Promise<void> {
  return new Promise((resolve) => {
    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    let releaseBackButtonLayer: (() => void) | null = null;

    const titleText = options?.title ?? t('dialog.info');
    const okText = options?.okLabel ?? t('dialog.ok');
    const detailsText = options?.details?.trim() ?? '';
    const detailsLabel = options?.detailsLabel?.trim() || 'Details';
    const detailsMarkup = detailsText
      ? `
          <details class="dialog-details">
            <summary>${escapeHtml(detailsLabel)}</summary>
            <pre class="dialog-details-pre">${escapeHtml(detailsText)}</pre>
          </details>
        `
      : '';

    overlay.innerHTML = `
      <div class="modal dialog-modal">
        <div class="modal-content dialog-content">
          <div class="modal-header">
            <h3>${escapeHtml(titleText)}</h3>
            <button class="modal-close" data-role="ok">&times;</button>
          </div>
          <div class="modal-body">
            <p class="dialog-message">${escapeHtml(message)}</p>
            ${detailsMarkup}
          </div>
          <div class="modal-footer">
            <button class="btn-primary" data-role="ok">${escapeHtml(okText)}</button>
          </div>
        </div>
      </div>
    `;

    function close(): void {
      document.removeEventListener('keydown', onKey);
      releaseBackButtonLayer?.();
      releaseBackButtonLayer = null;
      overlay.remove();
      resolve();
    }

    function onKey(e: KeyboardEvent): void {
      if (e.key === 'Escape' || e.key === 'Enter') {
        e.preventDefault();
        close();
      }
    }

    overlay.addEventListener('click', (e) => {
      const target = e.target as HTMLElement;
      if (target === overlay) {
        close();
        return;
      }
      const role = target.closest('[data-role]')?.getAttribute('data-role');
      if (role === 'ok') close();
    });

    document.addEventListener('keydown', onKey);
    document.body.appendChild(overlay);
    releaseBackButtonLayer = registerBackButtonLayer(close);

    const okBtn = overlay.querySelector<HTMLButtonElement>('[data-role="ok"]');
    okBtn?.focus();
  });
}

export function showTextPrompt(options?: TextPromptOptions): Promise<string | null> {
  return new Promise((resolve) => {
    openTextPromptDialog(resolve, options);
  });
}

function openTextPromptDialog(
  resolve: (value: string | null) => void,
  options?: TextPromptOptions,
): void {
  const overlay = buildTextPromptOverlay(options);
  const elements = getTextPromptElements(overlay);
  if (!elements) {
    overlay.remove();
    resolve(null);
    return;
  }

  let releaseBackButtonLayer: (() => void) | null = null;

  const setError = (message: string | null): void => {
    const next = message?.trim() ?? '';
    elements.errorEl.hidden = next.length === 0;
    elements.errorEl.textContent = next;
  };

  const close = (result: string | null): void => {
    document.removeEventListener('keydown', onKey);
    releaseBackButtonLayer?.();
    releaseBackButtonLayer = null;
    overlay.remove();
    resolve(result);
  };

  const submit = (): void => {
    const value = getValidatedTextPromptValue(elements.inputEl, setError, options?.validate);
    if (value !== null) {
      close(value);
    }
  };

  const onKey = (event: KeyboardEvent): void => {
    if (event.key === 'Escape') {
      event.preventDefault();
      close(null);
      return;
    }

    if (event.key === 'Enter') {
      event.preventDefault();
      submit();
    }
  };

  elements.inputEl.addEventListener('input', () => {
    setError(null);
  });

  overlay.addEventListener('click', (event) => {
    const role = getTextPromptClickRole(event, overlay);
    if (role === 'confirm') {
      submit();
    } else if (role === 'cancel') {
      close(null);
    }
  });

  document.addEventListener('keydown', onKey);
  document.body.appendChild(overlay);
  releaseBackButtonLayer = registerBackButtonLayer(() => {
    close(null);
  });

  elements.inputEl.focus();
  elements.inputEl.select();
}

function buildTextPromptOverlay(options?: TextPromptOptions): HTMLDivElement {
  const overlay = document.createElement('div');
  overlay.className = 'modal-overlay';

  const titleText = options?.title ?? t('dialog.info');
  const messageText = options?.message?.trim() ?? '';
  const confirmText = options?.confirmLabel ?? t('dialog.ok');
  const cancelText = options?.cancelLabel ?? t('dialog.cancel');
  const placeholderText = options?.placeholder ?? '';
  const initialValue = options?.initialValue ?? '';
  const messageMarkup = messageText
    ? `<p class="dialog-message">${escapeHtml(messageText)}</p>`
    : '';

  overlay.innerHTML = `
    <div class="modal dialog-modal">
      <div class="modal-content dialog-content">
        <div class="modal-header">
          <h3>${escapeHtml(titleText)}</h3>
          <button class="modal-close" data-role="cancel">&times;</button>
        </div>
        <div class="modal-body">
          ${messageMarkup}
          <input
            type="text"
            class="dialog-input"
            data-role="input"
            value="${escapeHtml(initialValue)}"
            placeholder="${escapeHtml(placeholderText)}"
            spellcheck="false"
            autocomplete="off"
          />
          <div class="dialog-input-error" data-role="error" hidden></div>
        </div>
        <div class="modal-footer">
          <button class="btn-secondary" data-role="cancel">${escapeHtml(cancelText)}</button>
          <button class="btn-primary" data-role="confirm">${escapeHtml(confirmText)}</button>
        </div>
      </div>
    </div>
  `;

  return overlay;
}

function getTextPromptElements(overlay: HTMLDivElement): TextPromptElements | null {
  const inputEl = overlay.querySelector<HTMLInputElement>('[data-role="input"]');
  const errorEl = overlay.querySelector<HTMLElement>('[data-role="error"]');
  if (!inputEl || !errorEl) {
    return null;
  }

  return {
    errorEl,
    inputEl,
  };
}

function getValidatedTextPromptValue(
  inputEl: HTMLInputElement,
  setError: (message: string | null) => void,
  validate: TextPromptOptions['validate'],
): string | null {
  const value = inputEl.value.trim();
  const validationError = validate?.(value) ?? null;
  if (validationError) {
    setError(validationError);
    return null;
  }

  setError(null);
  return value;
}

function getTextPromptClickRole(
  event: MouseEvent,
  overlay: HTMLDivElement,
): 'cancel' | 'confirm' | null {
  const target = event.target as HTMLElement;
  if (target === overlay) {
    return 'cancel';
  }

  const role = target.closest('[data-role]')?.getAttribute('data-role');
  if (role === 'confirm' || role === 'cancel') {
    return role;
  }

  return null;
}
