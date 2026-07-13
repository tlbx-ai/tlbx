/**
 * Clipboard Shortcut Resolver
 *
 * Resolves copy/paste keyboard shortcuts for terminal input handling.
 */

/**
 * Minimal keyboard event shape needed for shortcut resolution.
 */
export interface ShortcutInput {
  key: string;
  ctrlKey: boolean;
  shiftKey: boolean;
  altKey: boolean;
  metaKey: boolean;
}

export type CopyShortcutAction = 'copy' | 'sendKey' | 'ignore';

type ClipboardCopyEvent = Pick<ClipboardEvent, 'clipboardData' | 'preventDefault'>;

/**
 * Resolve whether the event is a copy shortcut for the current style.
 */
export function isCopyShortcut(input: ShortcutInput, style: 'windows' | 'unix'): boolean {
  const key = input.key.toLowerCase();
  if (key !== 'c') return false;

  // Cmd+C is the browser-native copy gesture on macOS regardless of whether
  // terminal shortcuts are configured in Windows or Unix mode.
  if (input.metaKey && !input.ctrlKey && !input.shiftKey && !input.altKey) {
    return true;
  }

  if (style === 'windows') {
    return input.ctrlKey && !input.shiftKey && !input.altKey && !input.metaKey;
  }

  return input.ctrlKey && input.shiftKey && !input.altKey && !input.metaKey;
}

/**
 * Resolve whether a copy shortcut should copy the selection locally or pass
 * through to the terminal as normal input (for example Ctrl+C => SIGINT).
 */
export function resolveCopyShortcutAction(
  input: ShortcutInput,
  style: 'windows' | 'unix',
  hasSelection: boolean,
): CopyShortcutAction {
  if (!isCopyShortcut(input, style)) {
    return 'ignore';
  }

  if (hasSelection) {
    return 'copy';
  }

  // Cmd+C without a selection is a browser shortcut, not terminal input.
  return input.metaKey ? 'ignore' : 'sendKey';
}

/**
 * Populate the synchronous browser copy event. Unlike navigator.clipboard,
 * this path works for a trusted keyboard copy gesture on origins whose local
 * HTTPS certificate has not yet been trusted.
 */
export function writeTextToClipboardEvent(event: ClipboardCopyEvent, text: string): boolean {
  if (!event.clipboardData) {
    return false;
  }

  event.clipboardData.setData('text/plain', text);
  event.preventDefault();
  return true;
}

/**
 * Resolve whether the event should trigger clipboard paste handling.
 *
 * Unified aliases:
 * - Ctrl+V
 * - Cmd+V
 * - Ctrl+Shift+V
 *
 * Note: Alt+V is handled separately by isNativeImagePasteShortcut()
 * for clipboard image injection into terminal apps like Codex CLI.
 */
export function isPasteShortcut(input: ShortcutInput): boolean {
  const key = input.key.toLowerCase();
  if (key !== 'v') return false;

  // Cmd+V
  if (input.metaKey && !input.ctrlKey && !input.shiftKey && !input.altKey) {
    return true;
  }

  // Ctrl+V and Ctrl+Shift+V
  if (input.ctrlKey && !input.metaKey && !input.altKey) {
    return true;
  }

  return false;
}

/**
 * Resolve whether the event is Alt+V (native clipboard image paste).
 * Used to inject clipboard images into terminal apps (e.g. Codex CLI)
 * that read the OS clipboard directly via Alt+V.
 */
export function isNativeImagePasteShortcut(input: ShortcutInput): boolean {
  return (
    input.key.toLowerCase() === 'v' &&
    input.altKey &&
    !input.ctrlKey &&
    !input.shiftKey &&
    !input.metaKey
  );
}
