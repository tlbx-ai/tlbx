import type { Terminal } from '@xterm/xterm';
import { $currentSettings } from '../../stores';
import { getClipboardStyle } from '../../utils';
import { sendInput } from '../comms';
import { showSearch, hideSearch, isSearchVisible } from './search';
import {
  resolveCopyShortcutAction,
  isPasteShortcut,
  isNativeImagePasteShortcut,
  writeTextToClipboardEvent,
  type CopyShortcutAction,
} from './clipboardShortcuts';
import {
  handleClipboardPaste,
  handleNativeImagePaste,
  sanitizeCopyContent,
  sanitizePasteContent,
} from './fileDrop';
import { getForegroundInfo } from '../process';
import { evaluateTerminalKeyAudit, isModifierKeyOnlyEvent, isThirdLevelShift } from './keyAudit';
import {
  classifyTerminalEnterIntent,
  captureTerminalInputData,
  captureTerminalLineBreak,
  expectTerminalSubmission,
} from '../history/terminalInputCapture';

export interface TerminalInteractionBindings {
  contextMenuHandler: (event: MouseEvent) => void;
  disposables: Array<{ dispose: () => void }>;
  enterOverrideHandler: (event: KeyboardEvent) => void;
  pasteHandler: (event: ClipboardEvent) => void;
}

interface TerminalInteractionBindingArgs {
  canUseAsyncClipboard: () => boolean;
  cancelTerminalInputEvent: (event: Event) => false;
  container: HTMLDivElement;
  getLegacyKeyboardNumbers: (event: KeyboardEvent) => {
    charCode: number;
    keyCode: number;
    which: number;
  };
  isKeyAuditActive: () => boolean;
  isMac: boolean;
  isTouchSelecting: (sessionId: string) => boolean;
  isWindows: boolean;
  macOptionIsMeta: boolean;
  pasteToTerminal: (sessionId: string, text: string, bracketedPasteMode?: boolean) => Promise<void>;
  sessionId: string;
  shouldCaptureTerminalKey: (container: HTMLDivElement, target: EventTarget | null) => boolean;
  wasEnterOverrideHandledRecently: (sessionId: string) => boolean;
  terminal: Terminal;
  tryHandleTerminalEnterOverride: (
    sessionId: string,
    event: KeyboardEvent,
    container?: HTMLDivElement,
    source?: string,
  ) => boolean;
  updateSessionEnterModifierLatch: (
    sessionId: string,
    event: KeyboardEvent,
    container?: HTMLDivElement,
    source?: string,
  ) => void;
}

export function bindTerminalInteractionHandlers({
  canUseAsyncClipboard,
  cancelTerminalInputEvent,
  container,
  getLegacyKeyboardNumbers,
  isKeyAuditActive,
  isMac,
  isTouchSelecting,
  isWindows,
  macOptionIsMeta,
  pasteToTerminal,
  sessionId,
  shouldCaptureTerminalKey,
  wasEnterOverrideHandledRecently,
  terminal,
  tryHandleTerminalEnterOverride,
  updateSessionEnterModifierLatch,
}: TerminalInteractionBindingArgs): TerminalInteractionBindings {
  const disposables: Array<{ dispose: () => void }> = [];
  const enterOverrideHandler = (event: KeyboardEvent) => {
    tryHandleTerminalEnterOverride(sessionId, event, container, 'container-enter');
  };
  const enterModifierKeydownHandler = (event: KeyboardEvent) => {
    updateSessionEnterModifierLatch(sessionId, event, container, 'container-latch');
  };
  const enterModifierKeyupHandler = (event: KeyboardEvent) => {
    updateSessionEnterModifierLatch(sessionId, event, container, 'container-latch');
  };

  let keyDownHandled = false;
  let keyDownSeen = false;
  let keyPressHandled = false;
  let unprocessedDeadKey = false;

  const captureEnterIntent = (event: KeyboardEvent, directInputDelivery: boolean): void => {
    const intent = classifyTerminalEnterIntent(event);
    if (intent === 'lineBreak') {
      captureTerminalLineBreak(sessionId, directInputDelivery);
    } else if (intent === 'submit') {
      expectTerminalSubmission(sessionId);
    }
  };

  const resetKeyAuditState = (): void => {
    keyDownHandled = false;
    keyDownSeen = false;
    keyPressHandled = false;
    unprocessedDeadKey = false;
  };

  const tryHandleCopyShortcut = (event: KeyboardEvent): CopyShortcutAction => {
    const style = getClipboardStyle($currentSettings.get()?.clipboardShortcuts ?? 'auto');
    const action = resolveCopyShortcutAction(event, style, terminal.hasSelection());
    if (
      action === 'sendKey' &&
      event.key.toLowerCase() === 'c' &&
      event.ctrlKey &&
      !event.altKey &&
      !event.metaKey
    ) {
      captureTerminalInputData(sessionId, '\x03');
      sendInput(sessionId, '\x03');
    }

    return action;
  };

  const tryHandleImagePasteShortcut = (event: KeyboardEvent): boolean => {
    if (!isNativeImagePasteShortcut(event)) {
      return false;
    }

    const foreground = getForegroundInfo(sessionId);
    void handleNativeImagePaste(sessionId, {
      foregroundName: foreground.name,
      foregroundCommandLine: foreground.commandLine,
    }).then((result) => {
      if (result === 'none') {
        sendInput(sessionId, '\x1bv');
      }
    });
    return true;
  };

  const tryHandlePasteShortcut = (event: KeyboardEvent): boolean => {
    if (!isPasteShortcut(event)) {
      return false;
    }

    if (canUseAsyncClipboard()) {
      const foreground = getForegroundInfo(sessionId);
      void handleClipboardPaste(sessionId, {
        foregroundName: foreground.name,
        foregroundCommandLine: foreground.commandLine,
      });
      return true;
    }

    return false;
  };

  const tryHandleSearchShortcut = (event: KeyboardEvent): boolean => {
    if ((event.ctrlKey || event.metaKey) && event.key === 'f') {
      showSearch();
      return true;
    }

    if (event.key === 'Escape' && isSearchVisible()) {
      hideSearch();
      return true;
    }

    return false;
  };

  const markHandled = (event: Event): void => {
    keyDownHandled = true;
    cancelTerminalInputEvent(event);
  };

  const buildTerminalKeyAuditInput = (event: KeyboardEvent) => {
    const legacyNumbers = getLegacyKeyboardNumbers(event);
    return {
      input: {
        key: event.key,
        code: event.code,
        keyCode: legacyNumbers.keyCode,
        which: legacyNumbers.which,
        charCode: legacyNumbers.charCode,
        ctrlKey: event.ctrlKey,
        shiftKey: event.shiftKey,
        altKey: event.altKey,
        metaKey: event.metaKey,
      },
      legacyNumbers,
    };
  };

  const handleTerminalKeyAuditResult = (
    event: KeyboardEvent,
    result: ReturnType<typeof evaluateTerminalKeyAudit>,
  ): boolean => {
    if (result.type === 'pageUp' || result.type === 'pageDown') {
      const scrollCount = terminal.rows - 1;
      terminal.scrollLines(result.type === 'pageUp' ? -scrollCount : scrollCount);
      markHandled(event);
      return true;
    }

    if (result.type === 'selectAll') {
      terminal.selectAll();
      markHandled(event);
      return true;
    }

    if (!result.key) {
      if (isModifierKeyOnlyEvent({ key: event.key })) {
        cancelTerminalInputEvent(event);
      }
      return true;
    }

    if (event.key && !event.ctrlKey && !event.altKey && !event.metaKey && event.key.length === 1) {
      const charCode = event.key.charCodeAt(0);
      if (charCode >= 65 && charCode <= 90) {
        return true;
      }
    }

    if (unprocessedDeadKey) {
      unprocessedDeadKey = false;
      return true;
    }

    captureTerminalInputData(sessionId, result.key);
    sendInput(sessionId, result.key);
    markHandled(event);
    return true;
  };

  const terminalKeyAuditKeydownHandler = (event: KeyboardEvent) => {
    if (
      !isKeyAuditActive() ||
      !shouldCaptureTerminalKey(container, event.target) ||
      event.isComposing
    ) {
      return;
    }

    keyDownHandled = false;
    keyDownSeen = true;

    if (event.key === 'F12') {
      return;
    }

    const copyAction = tryHandleCopyShortcut(event);
    if (copyAction !== 'ignore') {
      // Do not cancel a copy action. The browser must emit its trusted `copy`
      // event so clipboardData can be populated without navigator.clipboard.
      if (copyAction === 'sendKey') {
        markHandled(event);
      }
      return;
    }

    if (tryHandleImagePasteShortcut(event) || tryHandlePasteShortcut(event)) {
      markHandled(event);
      return;
    }

    if (tryHandleSearchShortcut(event)) {
      markHandled(event);
      return;
    }

    if (tryHandleTerminalEnterOverride(sessionId, event, container, 'audit-enter')) {
      keyDownHandled = true;
      return;
    }

    captureEnterIntent(event, true);

    if (event.key === 'Dead' || event.key === 'AltGraph') {
      unprocessedDeadKey = true;
    }

    const { input } = buildTerminalKeyAuditInput(event);
    const result = evaluateTerminalKeyAudit(
      input,
      terminal.modes.applicationCursorKeysMode,
      isMac,
      macOptionIsMeta,
    );

    if (isThirdLevelShift(event, isMac, isWindows, macOptionIsMeta)) {
      return;
    }

    handleTerminalKeyAuditResult(event, result);
  };

  const terminalKeyAuditKeypressHandler = (event: KeyboardEvent) => {
    if (!isKeyAuditActive() || !shouldCaptureTerminalKey(container, event.target)) {
      return;
    }

    keyPressHandled = false;

    if (keyDownHandled) {
      cancelTerminalInputEvent(event);
      return;
    }

    const legacyNumbers = getLegacyKeyboardNumbers(event);
    const keyCode =
      legacyNumbers.charCode !== 0
        ? legacyNumbers.charCode
        : legacyNumbers.which !== 0
          ? legacyNumbers.which
          : legacyNumbers.keyCode;

    if (
      keyCode === 0 ||
      ((event.altKey || event.ctrlKey || event.metaKey) &&
        !isThirdLevelShift(
          {
            altKey: event.altKey,
            ctrlKey: event.ctrlKey,
            metaKey: event.metaKey,
            keyCode: legacyNumbers.keyCode,
            type: 'keypress',
            getModifierState: event.getModifierState.bind(event),
          },
          isMac,
          isWindows,
          macOptionIsMeta,
        ))
    ) {
      cancelTerminalInputEvent(event);
      return;
    }

    const data = String.fromCharCode(keyCode);
    captureTerminalInputData(sessionId, data);
    sendInput(sessionId, data);
    keyPressHandled = true;
    unprocessedDeadKey = false;
    cancelTerminalInputEvent(event);
  };

  const terminalKeyAuditInputHandler = (event: Event) => {
    if (!isKeyAuditActive() || !shouldCaptureTerminalKey(container, event.target)) {
      return;
    }

    const inputEvent = event as InputEvent;
    const clearTerminalInputValue = (): void => {
      if (event.target instanceof HTMLTextAreaElement) {
        event.target.value = '';
      }
    };
    const buildSyntheticEnterKeydown = (): KeyboardEvent => {
      const synthetic = Object.create(inputEvent) as KeyboardEvent;
      Object.defineProperties(synthetic, {
        altKey: { value: false },
        charCode: { value: 13 },
        code: { value: 'Enter' },
        ctrlKey: { value: false },
        key: { value: 'Enter' },
        keyCode: { value: 13 },
        metaKey: { value: false },
        shiftKey: { value: false },
        type: { value: 'keydown' },
        which: { value: 13 },
      });
      return synthetic;
    };

    if (inputEvent.inputType === 'insertLineBreak' || inputEvent.inputType === 'insertParagraph') {
      if (keyDownHandled || wasEnterOverrideHandledRecently(sessionId)) {
        clearTerminalInputValue();
        cancelTerminalInputEvent(event);
        return;
      }

      if (
        tryHandleTerminalEnterOverride(
          sessionId,
          buildSyntheticEnterKeydown(),
          container,
          'audit-input-enter',
        )
      ) {
        unprocessedDeadKey = false;
        clearTerminalInputValue();
        cancelTerminalInputEvent(event);
        return;
      }

      clearTerminalInputValue();
      return;
    }

    if (
      inputEvent.data &&
      inputEvent.inputType === 'insertText' &&
      (!inputEvent.isComposing || !keyDownSeen)
    ) {
      if (keyPressHandled) {
        cancelTerminalInputEvent(event);
        return;
      }

      unprocessedDeadKey = false;
      captureTerminalInputData(sessionId, inputEvent.data);
      sendInput(sessionId, inputEvent.data);
      cancelTerminalInputEvent(event);
      return;
    }

    clearTerminalInputValue();
  };

  const terminalKeyAuditKeyupHandler = (event: KeyboardEvent) => {
    if (!isKeyAuditActive() || !shouldCaptureTerminalKey(container, event.target)) {
      return;
    }

    keyDownSeen = false;
    keyPressHandled = false;

    if (isModifierKeyOnlyEvent({ key: event.key })) {
      cancelTerminalInputEvent(event);
    }
  };

  const addManagedListener = <K extends keyof HTMLElementEventMap>(
    type: K,
    listener: (event: HTMLElementEventMap[K]) => void,
    options?: boolean | AddEventListenerOptions,
  ): void => {
    container.addEventListener(type, listener as EventListener, options);
    disposables.push({
      dispose: () => {
        container.removeEventListener(type, listener as EventListener, options);
      },
    });
  };

  addManagedListener('keydown', enterModifierKeydownHandler, true);
  addManagedListener('keyup', enterModifierKeyupHandler, true);
  addManagedListener('keydown', terminalKeyAuditKeydownHandler, true);
  addManagedListener('keypress', terminalKeyAuditKeypressHandler, true);
  addManagedListener('input', terminalKeyAuditInputHandler, true);
  addManagedListener('keyup', terminalKeyAuditKeyupHandler, true);
  addManagedListener('copy', (event) => {
    if (!terminal.hasSelection()) {
      return;
    }

    writeTextToClipboardEvent(event, sanitizeCopyContent(terminal.getSelection()));
  });
  container.addEventListener('keydown', enterOverrideHandler, true);

  terminal.attachCustomKeyEventHandler((event: KeyboardEvent) => {
    if (isKeyAuditActive()) {
      return event.type === 'keydown' && event.key === 'F12';
    }

    resetKeyAuditState();

    if (event.type !== 'keydown') {
      return true;
    }

    if (event.key === 'F12') {
      return false;
    }

    if (tryHandleTerminalEnterOverride(sessionId, event, undefined, 'xterm-enter')) {
      return false;
    }

    captureEnterIntent(event, true);

    if (tryHandleCopyShortcut(event) !== 'ignore') {
      return false;
    }

    if (tryHandleImagePasteShortcut(event)) {
      return false;
    }

    if (isPasteShortcut(event)) {
      if (canUseAsyncClipboard()) {
        const foreground = getForegroundInfo(sessionId);
        void handleClipboardPaste(sessionId, {
          foregroundName: foreground.name,
          foregroundCommandLine: foreground.commandLine,
        });
        return false;
      }

      return true;
    }

    if (tryHandleSearchShortcut(event)) {
      return false;
    }

    return true;
  });

  const pasteHandler = (event: ClipboardEvent) => {
    event.preventDefault();
    event.stopPropagation();
    event.stopImmediatePropagation();

    if (event.clipboardData) {
      const text = event.clipboardData.getData('text/plain');
      if (text) {
        void pasteToTerminal(sessionId, sanitizePasteContent(text));
      }
    }
  };
  container.addEventListener('paste', pasteHandler, true);

  const contextMenuHandler = (event: MouseEvent) => {
    if (isTouchSelecting(sessionId)) {
      return;
    }

    const settings = $currentSettings.get();
    if (!settings || settings.rightClickPaste) {
      event.preventDefault();
      const foreground = getForegroundInfo(sessionId);
      void handleClipboardPaste(sessionId, {
        foregroundName: foreground.name,
        foregroundCommandLine: foreground.commandLine,
      });
    }
  };
  container.addEventListener('contextmenu', contextMenuHandler);

  return {
    contextMenuHandler,
    disposables,
    enterOverrideHandler,
    pasteHandler,
  };
}
