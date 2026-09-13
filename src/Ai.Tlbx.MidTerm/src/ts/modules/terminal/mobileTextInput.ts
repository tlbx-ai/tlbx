/// <reference lib="es2022.intl" />
import { hasPrecisePointer, isTouchDevice } from '../touchController/detection';

const resets = new Map<string, () => void>();
const segmenter =
  typeof Intl.Segmenter === 'function'
    ? new Intl.Segmenter(undefined, { granularity: 'grapheme' })
    : null;

export function usesMobileTerminalTextInput(): boolean {
  return isTouchDevice() && !hasPrecisePointer();
}

/** Input from paste, touch controls, history navigation, or another producer ends this tail. */
export function resetMobileTerminalTextInput(sessionId: string): void {
  resets.get(sessionId)?.();
}

/** Replace only a suffix we authored, while the remote cursor is still at its end. */
export function mobileTextEdit(previous: string, next: string): string | null {
  if (Array.from(next).some((char) => char.charCodeAt(0) < 32 || char.charCodeAt(0) === 127))
    return null;
  const before = Array.from(previous);
  const after = Array.from(next);
  let prefix = 0;
  while (prefix < before.length && before[prefix] === after[prefix]) prefix++;
  const removed = before.slice(prefix).join('');
  // A remote line editor may erase a grapheme or a scalar. Do not guess for clusters.
  const offset = before.slice(0, prefix).join('').length;
  if (
    segmenter &&
    [...segmenter.segment(previous)].some(
      (part) => part.index + part.segment.length > offset && Array.from(part.segment).length !== 1,
    )
  )
    return null;
  if (!segmenter && Array.from(removed).some((char) => char.charCodeAt(0) > 126)) return null;
  return '\x7f'.repeat(before.length - prefix) + after.slice(prefix).join('');
}

interface MobileTextInputArgs {
  sessionId: string;
  container: HTMLElement;
  textarea: HTMLTextAreaElement;
  send: (data: string) => void;
  enter: () => void;
  paste: (text: string) => void;
  context: () => string;
  unsupportedEdit: () => void;
}

function isCompositionKey(event: KeyboardEvent): boolean {
  // Android keyboards still use 229 before isComposing becomes true.
  return event.isComposing || (event as unknown as { keyCode?: number }).keyCode === 229;
}

export function bindMobileTerminalTextInput({
  sessionId,
  container,
  textarea,
  send,
  enter,
  paste,
  context,
  unsupportedEdit,
}: MobileTextInputArgs): { dispose: () => void } {
  let sent = '';
  let sending = false;
  let composing = false;
  let contextKey = context();
  let disposed = false;
  let focused = typeof document !== 'undefined' && document.activeElement === textarea;
  let revision = 0;
  const listeners: Array<() => void> = [];
  const reset = (): void => {
    if (sending) return;
    revision++;
    sent = '';
    composing = false;
    textarea.value = '';
    contextKey = context();
  };
  resets.set(sessionId, reset);
  const emit = (data: string): void => {
    if (!data) return;
    sending = true;
    try {
      send(data);
    } finally {
      sending = false;
    }
  };
  const owns = (event: Event): boolean => {
    if (!usesMobileTerminalTextInput() || event.target !== textarea) return false;
    if (!focused) event.stopImmediatePropagation();
    return focused;
  };
  const apply = (): void => {
    const value = textarea.value;
    const delta = mobileTextEdit(sent, value);
    if (
      delta === null ||
      textarea.selectionStart !== value.length ||
      textarea.selectionEnd !== value.length
    ) {
      reset();
      unsupportedEdit();
      return;
    }
    sent = value;
    emit(delta);
    // Bound keyboard context without rewriting its value during a composition.
    if (!composing && sent.length > 2048) reset();
  };
  const listen = <K extends keyof HTMLElementEventMap>(
    target: HTMLElement,
    type: K,
    handler: (event: HTMLElementEventMap[K]) => void,
  ): void => {
    target.addEventListener(type, handler, true);
    listeners.push(() => {
      target.removeEventListener(type, handler, true);
    });
  };
  listen(container, 'keydown', (event) => {
    if (!owns(event)) return;
    if (contextKey !== context()) reset();
    const textKey =
      isCompositionKey(event) ||
      event.key === 'Dead' ||
      (((!event.ctrlKey && !event.metaKey && !event.altKey) ||
        event.getModifierState('AltGraph')) &&
        (Array.from(event.key).length === 1 || event.key === 'Backspace'));
    if (textKey) {
      // Leave the browser's default editing intact; xterm/audit must not also send the key.
      event.stopImmediatePropagation();
      if (event.key === 'Backspace' && !sent && !isCompositionKey(event)) {
        event.preventDefault();
        emit('\x7f');
      }
    } else if (!['Shift', 'Control', 'Alt', 'Meta', 'AltGraph'].includes(event.key)) {
      reset();
    }
  });
  listen(container, 'keypress', (event) => {
    if (owns(event) && (event.isComposing || event.key.length === 1))
      event.stopImmediatePropagation();
  });
  listen(container, 'beforeinput', (event) => {
    if (!owns(event)) return;
    if (contextKey !== context()) reset();
    event.stopImmediatePropagation();
    if (
      (event.inputType === 'insertLineBreak' || event.inputType === 'insertParagraph') &&
      event.cancelable
    ) {
      event.preventDefault();
      reset();
      enter();
    } else if (event.inputType === 'deleteContentBackward' && !sent && !textarea.value) {
      event.preventDefault();
      emit('\x7f');
    }
  });
  listen(container, 'input', (event) => {
    if (!owns(event)) return;
    event.stopImmediatePropagation();
    const inputType = (event as InputEvent).inputType;
    if (contextKey !== context()) {
      reset();
      return;
    }
    if (inputType === 'insertLineBreak' || inputType === 'insertParagraph') {
      reset();
      enter();
      return;
    }
    if (inputType === 'insertFromPaste' && textarea.value.startsWith(sent)) {
      const text = textarea.value.slice(sent.length);
      reset();
      if (text) paste(text);
      return;
    }
    if (
      inputType === 'deleteContentBackward' &&
      mobileTextEdit(sent, textarea.value) === null &&
      sent.startsWith(textarea.value)
    ) {
      // Preserve an explicit Backspace even when the remote Unicode erase unit is unknown.
      reset();
      emit('\x7f');
      return;
    }
    apply();
  });
  listen(container, 'compositionstart', (event) => {
    if (!owns(event)) return;
    composing = true;
    event.stopImmediatePropagation();
  });
  listen(container, 'compositionupdate', (event) => {
    if (owns(event)) event.stopImmediatePropagation();
  });
  listen(container, 'compositionend', (event) => {
    if (!owns(event)) return;
    event.stopImmediatePropagation();
    composing = false;
    const currentRevision = revision;
    // Some keyboards commit the final textarea mutation after compositionend.
    queueMicrotask(() => {
      if (!disposed && revision === currentRevision && contextKey === context()) apply();
    });
  });
  listen(textarea, 'blur', () => {
    focused = false;
    reset();
  });
  listen(textarea, 'focus', () => {
    focused = true;
    reset();
  });
  return {
    dispose: () => {
      disposed = true;
      listeners.forEach((remove) => {
        remove();
      });
      if (resets.get(sessionId) === reset) resets.delete(sessionId);
      reset();
    },
  };
}
