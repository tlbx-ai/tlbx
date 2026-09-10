/* eslint-disable max-lines -- terminal manager is a legacy integration hub; this hotfix keeps the xterm/Codex input change scoped. */
/**
 * Terminal Manager Module
 *
 * Handles xterm.js terminal lifecycle, creation, destruction,
 * and event binding for terminal sessions.
 */
import { onTerminalInput } from './terminalInputOrigin';
import type { Session, TerminalState } from '../../types';
import { sendSessionPasteInput } from '../../api/client';
import { syncEffectiveXtermThemeDomOverrides } from '../theming/themes';
import {
  sessionTerminals,
  pendingOutputFrames,
  pendingTerminalReplayModes,
  sessionsNeedingResync,
  fontsReadyPromise,
  dom,
  setFontsReadyPromise,
  MAX_WEBGL_CONTEXTS,
  terminalsWithWebgl,
} from '../../state';
import {
  $activeSessionId,
  $currentSettings,
  $sessions,
  hasTerminalSizeControl,
} from '../../stores';
import { applyTerminalScalingSync, fitSessionToScreen, fitTerminalToContainer } from './scaling';
import { disposeTerminalPresentation } from './terminalPresentation';
import { setupFileDrop, sanitizeCopyContent } from './fileDrop';
import { createAlternateScreenReplayPrefix } from './replayState';
import {
  isBracketedPasteEnabled,
  forgetMuxSession,
  reconcileSynchronizedOutputCursor,
  requestBufferRefresh,
  restartStalledSessionRecovery,
  sendCommand,
  sendInput,
  sendTerminalResponse,
  getBrowserTransportSnapshot,
  writeOutputFrame as writeMuxOutputFrame,
} from '../comms';
import { showPasteIndicator, hidePasteIndicator } from '../badges';

import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import { ImageAddon } from '@xterm/addon-image';
import { WebglAddon } from '@xterm/addon-webgl';
import { WebLinksAddon } from '@xterm/addon-web-links';
import { Unicode11Addon } from '@xterm/addon-unicode11';

import { initSearchForTerminal, isSearchVisible, cleanupSearchForTerminal } from './search';
import { applyTerminalScrollbarStyleClass, normalizeScrollbarStyle } from './scrollbarStyle';
import {
  describeTerminalEnterOverrideDelivery,
  getTerminalEnterOverride,
  getTerminalEnterTarget,
  isTerminalEnterRemapEnabled,
  shouldRouteTerminalEnterOverrideThroughXtermInput,
  type EnterOverrideInput,
  type TerminalEnterTarget,
} from './enterBehavior';
import {
  applyEnterModifierLatch,
  updateEnterModifierLatch,
  type EnterModifierLatchState,
} from './enterModifierLatch';
import * as enterOverrideSuppress from './enterOverrideSuppress';
import { bindTerminalInteractionHandlers } from './interactionBindings';
import { onTerminalScrollbackExit } from './scrollback';
import { shouldReclaimTerminalFocusOnMouseUp } from './focusReclaim';
import { activateTerminalLink } from './linkConfirmation';
import {
  runWithGuaranteedTerminalReplay,
  shouldRequestInitialTerminalReplay,
} from './guaranteedReplay';

import { createLogger } from '../logging';
import { registerFileLinkProvider, clearPathAllowlist } from './fileLinks';
import { getEffectiveTerminalFontSize } from './fontSize';
import {
  buildTerminalFontStack,
  DEFAULT_TERMINAL_FONT_WEIGHT,
  DEFAULT_TERMINAL_FONT_WEIGHT_BOLD,
  DEFAULT_TERMINAL_LETTER_SPACING,
  DEFAULT_TERMINAL_LINE_HEIGHT,
  ensureTerminalFontLoaded,
  getConfiguredTerminalFontFamily,
} from './fontConfig';
import { getTerminalOptions } from './terminalOptions';
import { getForegroundInfo } from '../process';
import { isSmartInputMode, showSmartInput } from '../smartInput';
import {
  disposeTerminalRgbBackgroundTransparency,
  syncTerminalRgbBackgroundTransparency,
} from './rgbBackgroundTransparency';
import { syncWebglTerminalCellBackgroundAlpha } from './webglCellBackgroundAlpha';
import { shouldOwnWebglContext, shouldUseWebglRenderer } from './webglSupport';
import { detachTerminalLigatureState, syncTerminalLigatureState } from './ligatures';
import { isTerminalVisible, refreshTerminalRenderer } from './presentationRefresh';
import { getTerminalStartupPaintAction, inspectTerminalStartupPaint } from './startupPaintHealth';
import type { TerminalKeyLogEntryInput } from '../diagnostics/terminalKeyLog';
import {
  captureTerminalInputData,
  captureTerminalLineBreak,
  captureTerminalPasteText,
  clearTerminalInputCapture,
} from '../history/terminalInputCapture';
const log = createLogger('terminalManager');
import { initTouchScrolling, teardownTouchScrolling, isTouchSelecting } from './touchScrolling';
import {
  revealMobileStableTerminalCursor,
  resumeMobileStableTerminalCursorFollowing,
} from './mobileVerticalStability';
import { handleOsc7Cwd } from '../process';
import { recordTerminalKeyLog } from '../diagnostics';
import { getActiveTab } from '../sessionTabs';
import { setTerminalParseStallHandler } from '../comms/muxChannel';

export function initTerminalRecovery(): void {
  setTerminalParseStallHandler((id, state) => {
    recreateTerminalForRecovery(id, state, 'xterm_parse_stalled');
  });
}

interface TerminalStartupPaintRecoveryState {
  attempt: number;
  consecutiveBlankChecks: number;
  fallbackWhenIndeterminate: boolean;
  timerId: number | null;
}

const terminalStartupPaintRecoveries = new Map<string, TerminalStartupPaintRecoveryState>();
const strictTerminalStartupPaintObligations = new WeakSet<TerminalState>();
const terminalStartupPaintRecreations = new Set<string>();
const TERMINAL_STARTUP_PAINT_INITIAL_DELAY_MS = 700;
const TERMINAL_STARTUP_PAINT_RETRY_DELAY_MS = 450;
const TERMINAL_STARTUP_PAINT_RECHECK_DELAY_MS = 200;
const TERMINAL_STARTUP_PAINT_MAX_ATTEMPTS = 8;

function clearTerminalStartupPaintRecovery(sessionId: string): void {
  const recovery = terminalStartupPaintRecoveries.get(sessionId);
  if (recovery?.timerId !== null && recovery?.timerId !== undefined) {
    window.clearTimeout(recovery.timerId);
  }
  terminalStartupPaintRecoveries.delete(sessionId);
}

function scheduleTerminalStartupPaintRecovery(
  sessionId: string,
  state: TerminalState,
  delayMs = TERMINAL_STARTUP_PAINT_INITIAL_DELAY_MS,
  fallbackWhenIndeterminate = strictTerminalStartupPaintObligations.has(state),
): void {
  clearTerminalStartupPaintRecovery(sessionId);
  const recovery: TerminalStartupPaintRecoveryState = {
    attempt: 0,
    consecutiveBlankChecks: 0,
    fallbackWhenIndeterminate,
    timerId: null,
  };
  terminalStartupPaintRecoveries.set(sessionId, recovery);

  const scheduleNext = (delay: number): void => {
    recovery.timerId = window.setTimeout(() => {
      recovery.timerId = null;
      if (sessionTerminals.get(sessionId) !== state || !state.opened) {
        clearTerminalStartupPaintRecovery(sessionId);
        return;
      }
      if (!isTerminalVisible(state) && !recovery.fallbackWhenIndeterminate) {
        clearTerminalStartupPaintRecovery(sessionId);
        return;
      }

      recovery.attempt += 1;
      const health = inspectTerminalStartupPaint(state.terminal, state.container);
      recovery.consecutiveBlankChecks =
        health === 'blank' ? recovery.consecutiveBlankChecks + 1 : 0;
      const action = getTerminalStartupPaintAction(
        health,
        recovery.consecutiveBlankChecks,
        recovery.attempt,
        TERMINAL_STARTUP_PAINT_MAX_ATTEMPTS,
        recovery.fallbackWhenIndeterminate,
      );

      if (action === 'complete') {
        if (
          recovery.fallbackWhenIndeterminate &&
          recovery.attempt < TERMINAL_STARTUP_PAINT_MAX_ATTEMPTS
        ) {
          scheduleNext(TERMINAL_STARTUP_PAINT_RETRY_DELAY_MS);
          return;
        }
        strictTerminalStartupPaintObligations.delete(state);
        clearTerminalStartupPaintRecovery(sessionId);
        return;
      }
      if (action === 'retry') {
        scheduleNext(TERMINAL_STARTUP_PAINT_RETRY_DELAY_MS);
        return;
      }
      if (action === 'refresh') {
        log.warn(() => `Terminal ${sessionId} startup framebuffer appears blank; repainting`);
        refreshTerminalRenderer(state, { preserveTextureAtlas: true });
        scheduleNext(TERMINAL_STARTUP_PAINT_RECHECK_DELAY_MS);
        return;
      }

      if (!terminalStartupPaintRecreations.has(sessionId)) {
        log.warn(
          () =>
            `Terminal ${sessionId} startup framebuffer remained blank; recreating terminal and replaying`,
        );
        terminalStartupPaintRecreations.add(sessionId);
        recreateTerminalForRecovery(sessionId, state);
        return;
      }

      // Keep recovery bounded if even the replacement renderer fails. Reusing
      // the normal transport restart still releases every browser-owned queue,
      // but avoids an unbounded create/destroy loop for a permanently bad GPU.
      log.warn(
        () =>
          `Terminal ${sessionId} replacement framebuffer remained blank; rebuilding renderer and replaying`,
      );
      state.terminal.reset();
      recoverTerminalRendererAfterForeground(sessionId, state, { preferDomRenderer: true });
      restartStalledSessionRecovery(sessionId, 'startup_framebuffer_blank');
      strictTerminalStartupPaintObligations.delete(state);
      clearTerminalStartupPaintRecovery(sessionId);
    }, delay);
  };

  scheduleNext(delayMs);
}

/**
 * Keeps the startup paint obligation alive when a terminal was opened while
 * hidden and only became visible after its first watchdog check.
 */
export function ensureTerminalStartupPaintRecovery(sessionId: string, state: TerminalState): void {
  strictTerminalStartupPaintObligations.add(state);
  const existing = terminalStartupPaintRecoveries.get(sessionId);
  if (existing) {
    existing.fallbackWhenIndeterminate = true;
    return;
  }

  scheduleTerminalStartupPaintRecovery(
    sessionId,
    state,
    TERMINAL_STARTUP_PAINT_RECHECK_DELAY_MS,
    true,
  );
}

/**
 * Replaces only the browser-side xterm instance while preserving the server
 * session and its position in the current standalone/layout wrapper. This is
 * the session-local equivalent of the F5 recovery that users previously needed
 * when xterm's empty-write replay barrier stopped invoking callbacks.
 */
export function recreateTerminalForRecovery(
  sessionId: string,
  state: TerminalState,
  cause = 'startup_terminal_recreated',
): void {
  if (sessionTerminals.get(sessionId) !== state) return;

  const parent = state.container.parentElement;
  const nextSibling = state.container.nextSibling;
  const wasHidden = state.container.classList.contains('hidden');
  const sessionInfo = $sessions.get()[sessionId];

  destroyTerminalForSession(sessionId);
  // destroyTerminalForSession clears session-scoped recovery bookkeeping. Keep
  // this launch marked so a second failed renderer cannot recreate forever.
  terminalStartupPaintRecreations.add(sessionId);

  const replacement = createTerminalForSession(sessionId, sessionInfo);
  if (parent) {
    parent.insertBefore(
      replacement.container,
      nextSibling?.parentNode === parent ? nextSibling : null,
    );
  }
  replacement.container.classList.toggle('hidden', wasHidden);
  ensureTerminalStartupPaintRecovery(sessionId, replacement);

  // The explicit bypass is safe only for this fresh parser/renderer. It keeps
  // the replay from entering the stale empty-write barrier again if the server
  // responds just after terminal.open().
  restartStalledSessionRecovery(sessionId, cause, true);
}
import { isEmbeddedWebPreviewContext } from '../web/webContext';
import {
  registerTerminalNotificationHandlers,
  type TerminalNotificationSignal,
} from './terminalNotifications';
import { shouldBlinkTerminalCursor } from './cursorActivity';

let showTerminalNotification: (
  sessionId: string,
  signal: TerminalNotificationSignal,
) => void = () => {};
export function setShowTerminalNotificationCallback(
  cb: (sessionId: string, signal: TerminalNotificationSignal) => void,
): void {
  showTerminalNotification = cb;
}

// Debounce timers for auto-rename from shell title
const pendingTitleUpdates = new Map<string, number>();
// Calibration measurement from hidden terminal (accurate cell dimensions)
let calibrationMeasurement: {
  cellWidth: number;
  cellHeight: number;
  fontFamily: string;
  fontSize: number;
  lineHeight: number;
  letterSpacing: number;
  fontWeight: string;
  fontWeightBold: string;
} | null = null;
let calibrationPromise: Promise<void> | null = null;

// Debounce timer for focus operations
let focusDebounceTimer: number | null = null;
const ENTER_MODIFIER_LATCH_MAX_AGE_MS = 1500;
const enterModifierLatches = new Map<string, EnterModifierLatchState>();
let globalTerminalEnterOverrideInstalled = false;
const FOCUS_RECLAIM_EXEMPT_SELECTOR = [
  'button',
  'a[href]',
  'summary',
  '[role="button"]',
  '[role="menuitem"]',
  '.adaptive-footer-dock',
  '.manager-bar',
  '.manager-bar-action-popover',
  '[data-tab-panel="agent"]',
  '[data-tab-panel="files"]',
].join(', ');

type TerminalKeyMatchEvent = {
  key?: string;
  code?: string;
  keyCode?: number;
  which?: number;
};

function isEnterLikeKey(event: TerminalKeyMatchEvent): boolean {
  return (
    event.key === 'Enter' ||
    event.code === 'Enter' ||
    event.code === 'NumpadEnter' ||
    event.keyCode === 13 ||
    event.which === 13
  );
}

function isModifierLikeKey(event: Pick<TerminalKeyMatchEvent, 'key' | 'code'>): boolean {
  const key = event.key?.toLowerCase();
  return (
    key === 'control' ||
    key === 'shift' ||
    key === 'alt' ||
    key === 'meta' ||
    event.code === 'ControlLeft' ||
    event.code === 'ControlRight' ||
    event.code === 'ShiftLeft' ||
    event.code === 'ShiftRight' ||
    event.code === 'AltLeft' ||
    event.code === 'AltRight' ||
    event.code === 'MetaLeft' ||
    event.code === 'MetaRight'
  );
}

function isTerminalKeyLogRelevant(event: TerminalKeyMatchEvent): boolean {
  return isEnterLikeKey(event) || isModifierLikeKey(event);
}

function formatEnterModifierLatchState(latch: EnterModifierLatchState | null | undefined): string {
  if (!latch) {
    return 'none';
  }

  const parts: string[] = [];
  if (latch.ctrlPressed) parts.push('ctrl');
  if (latch.shiftPressed) parts.push('shift');
  return parts.length > 0 ? parts.join('+') : 'none';
}

function describeTerminalKeyLogTarget(
  target: EventTarget | null,
  container?: HTMLDivElement,
): string {
  const element = target as Element | null;
  if (!element) {
    return 'none';
  }

  if (element instanceof HTMLTextAreaElement) {
    if (element.classList.contains('tlbx-terminal-input-proxy')) {
      return 'proxy';
    }
    if (element.classList.contains('xterm-helper-textarea')) {
      return 'xterm';
    }
  }

  if (container?.contains(element)) {
    return element.tagName.toLowerCase();
  }

  return 'outside';
}

type TerminalKeyLogEventLike = Pick<KeyboardEvent, 'ctrlKey' | 'shiftKey' | 'altKey' | 'metaKey'> &
  TerminalKeyMatchEvent &
  Partial<Pick<KeyboardEvent, 'type' | 'target' | 'defaultPrevented' | 'isComposing'>>;

function recordTerminalKeyDebugEvent(
  sessionId: string,
  source: string,
  event: TerminalKeyLogEventLike,
  container?: HTMLDivElement,
  note?: string,
): void {
  if (!isTerminalKeyLogRelevant(event)) {
    return;
  }

  const logEntry: TerminalKeyLogEntryInput = {
    sessionId,
    source,
    type: event.type ?? 'event',
    ctrlKey: event.ctrlKey,
    shiftKey: event.shiftKey,
    altKey: event.altKey,
    metaKey: event.metaKey,
    target: describeTerminalKeyLogTarget(event.target ?? null, container),
  };

  if (event.key !== undefined) {
    logEntry.key = event.key;
  }
  if (event.code !== undefined) {
    logEntry.code = event.code;
  }
  if (event.defaultPrevented !== undefined) {
    logEntry.defaultPrevented = event.defaultPrevented;
  }
  if (event.isComposing !== undefined) {
    logEntry.isComposing = event.isComposing;
  }
  if (note !== undefined) {
    logEntry.note = note;
  }

  recordTerminalKeyLog(logEntry);
}

function getSessionEnterOverride(
  sessionId: string,
  event: EnterOverrideInput,
): [bytes: string, target: TerminalEnterTarget] | null {
  const foreground = getForegroundInfo(sessionId);
  const sessionShellType = $sessions.get()[sessionId]?.shellType ?? null;
  const target = getTerminalEnterTarget(foreground.name, foreground.commandLine, sessionShellType);
  const bytes = getTerminalEnterOverride(
    event,
    $currentSettings.get()?.terminalEnterMode ?? 'shiftEnterLineFeed',
    target,
  );

  return bytes === null ? null : [bytes, target];
}

function isTerminalKeyAuditEnabled(): boolean {
  return isTerminalEnterRemapEnabled(
    $currentSettings.get()?.terminalEnterMode ?? 'shiftEnterLineFeed',
  );
}

function isMacPlatform(): boolean {
  return /\bmac/i.test(navigator.userAgent);
}

function isWindowsPlatform(): boolean {
  return /\bwindows/i.test(navigator.userAgent);
}

function getLegacyKeyboardNumbers(event: KeyboardEvent): {
  charCode: number;
  keyCode: number;
  which: number;
} {
  /* eslint-disable @typescript-eslint/no-deprecated -- KeyboardEvent legacy numeric fields are read only for cross-browser fallback normalization. */
  return {
    charCode: event.charCode,
    keyCode: event.keyCode,
    which: event.which,
  };
  /* eslint-enable @typescript-eslint/no-deprecated */
}

function cancelTerminalInputEvent(event: Event): false {
  event.preventDefault();
  event.stopPropagation();
  if ('stopImmediatePropagation' in event) {
    event.stopImmediatePropagation();
  }
  return false;
}

function deliverTerminalEnterOverride(
  sessionId: string,
  bytes: string,
  routeThroughXtermInput: boolean,
): void {
  captureTerminalLineBreak(sessionId);
  const state = routeThroughXtermInput ? sessionTerminals.get(sessionId) : null;
  if (state?.terminal) {
    state.terminal.input(bytes, true);
  } else {
    sendInput(sessionId, bytes);
  }
}

function updateSessionEnterModifierLatch(
  sessionId: string,
  event: KeyboardEvent,
  container?: HTMLDivElement,
  source = 'latch',
): void {
  if ((event.type !== 'keydown' && event.type !== 'keyup') || event.isComposing) {
    return;
  }

  if (container && !shouldCaptureTerminalKey(container, event.target)) {
    recordTerminalKeyDebugEvent(sessionId, source, event, container, 'skip=not-owner');
    return;
  }

  const eventType = event.type;
  const next = updateEnterModifierLatch(
    enterModifierLatches.get(sessionId),
    {
      type: eventType,
      key: event.key,
      code: event.code,
      ctrlKey: event.ctrlKey,
      shiftKey: event.shiftKey,
    },
    performance.now(),
  );
  if (next) {
    enterModifierLatches.set(sessionId, next);
  } else {
    enterModifierLatches.delete(sessionId);
  }

  recordTerminalKeyDebugEvent(
    sessionId,
    source,
    event,
    container,
    `latch=${formatEnterModifierLatchState(next)}`,
  );
}

function isTerminalInputOwnerElement(element: Element | null): element is HTMLTextAreaElement {
  return (
    element instanceof HTMLTextAreaElement &&
    (element.classList.contains('xterm-helper-textarea') ||
      element.classList.contains('tlbx-terminal-input-proxy'))
  );
}

function getOwnedXtermTextarea(container: HTMLDivElement): HTMLTextAreaElement | null {
  const textarea = container.querySelector('textarea.xterm-helper-textarea');
  return textarea instanceof HTMLTextAreaElement ? textarea : null;
}

function getOwnedTerminalInputProxy(container: HTMLDivElement): HTMLTextAreaElement | null {
  const proxy = container.querySelector('textarea.tlbx-terminal-input-proxy');
  return proxy instanceof HTMLTextAreaElement ? proxy : null;
}

function getOwnedXtermRoot(container: HTMLDivElement): HTMLDivElement | null {
  const root = container.querySelector('.xterm');
  return root instanceof HTMLDivElement ? root : null;
}

type TerminalWithPrivateFocusInternals = TerminalState['terminal'] & {
  _core?: {
    _coreBrowserService?: {
      _isFocused?: boolean;
      _cachedIsFocused: boolean | undefined;
    };
    _onFocus?: { fire: () => void };
    _onBlur?: { fire: () => void };
    element?: HTMLElement;
  };
};

function syncTerminalFocusClasses(
  state: TerminalState,
  focused: boolean,
  privateCore: TerminalWithPrivateFocusInternals['_core'],
): HTMLDivElement | null {
  const xtermRoot = getOwnedXtermRoot(state.container);
  if (xtermRoot) {
    xtermRoot.classList.toggle('focus', focused);
  }

  if (privateCore?.element && privateCore.element !== xtermRoot) {
    privateCore.element.classList.toggle('focus', focused);
  }

  return xtermRoot;
}

function fireTerminalFocusEvent(
  privateCore: TerminalWithPrivateFocusInternals['_core'],
  focused: boolean,
): void {
  try {
    if (focused) {
      privateCore?._onFocus?.fire();
    } else {
      privateCore?._onBlur?.fire();
    }
  } catch {
    // xterm internals may not be ready during initial open.
  }
}

function setTerminalVisualFocus(state: TerminalState, focused: boolean): void {
  const privateTerminal = state.terminal as TerminalWithPrivateFocusInternals;
  const privateCore = privateTerminal._core;
  const coreBrowserService = privateCore?._coreBrowserService;
  const xtermRoot = getOwnedXtermRoot(state.container);
  const hasRootFocus = xtermRoot?.classList.contains('focus') ?? false;
  const hasInternalFocus = coreBrowserService?._isFocused ?? false;

  if (hasRootFocus === focused && hasInternalFocus === focused) {
    return;
  }

  syncTerminalFocusClasses(state, focused, privateCore);

  if (!privateCore || !coreBrowserService) {
    return;
  }

  // xterm's inactive cursor style is driven by its private browser focus service,
  // not only the root .focus class. Mirror proxy focus into that internal state so
  // the renderer paints the active cursor while tlbx owns the actual DOM focus.
  coreBrowserService._isFocused = focused;
  coreBrowserService._cachedIsFocused = undefined;
  fireTerminalFocusEvent(privateCore, focused);
}

function terminalOwnsDocumentFocus(state: TerminalState): boolean {
  const activeElement = document.activeElement;
  return isTerminalInputOwnerElement(activeElement) && state.container.contains(activeElement);
}

function isDocumentFocused(): boolean {
  return typeof document.hasFocus !== 'function' || document.hasFocus();
}

/**
 * Synchronize xterm's visual focus and cursor animation with browser lifecycle.
 * The configured preference remains in the settings store; the xterm option is
 * the effective runtime value so inactive terminals own no blink timer at all.
 */
export function syncTerminalCursorActivity(
  state: TerminalState,
  terminalInputFocused = terminalOwnsDocumentFocus(state),
  configuredToBlink = $currentSettings.get()?.cursorBlink ?? false,
): void {
  const documentVisible = document.visibilityState !== 'hidden';
  const documentFocused = isDocumentFocused();
  const shouldBlink = shouldBlinkTerminalCursor({
    configuredToBlink,
    documentVisible,
    documentFocused,
    terminalInputFocused,
  });
  if (state.terminal.options.cursorBlink !== shouldBlink) {
    state.terminal.options.cursorBlink = shouldBlink;
  }
}

function refreshVisibleTerminalViewport(state: TerminalState): void {
  if (!state.opened || state.container.classList.contains('hidden')) {
    return;
  }

  try {
    state.terminal.refresh(0, Math.max(state.terminal.rows - 1, 0));
  } catch {
    // The terminal may have been disposed between the lifecycle event and paint.
  }
}

function syncAllTerminalCursorActivity(forceInactive = false, refreshVisible = false): void {
  sessionTerminals.forEach((state) => {
    syncTerminalCursorActivity(state, forceInactive ? false : terminalOwnsDocumentFocus(state));
    if (refreshVisible) {
      refreshVisibleTerminalViewport(state);
    }
  });
}

let foregroundCursorRecoveryTimer: number | null = null;

function recoverTerminalCursorsAfterForeground(): void {
  // Chromium may discard a WebGL framebuffer while another application owns
  // the foreground. Cursor blinking used to repaint it accidentally; now that
  // inactive blink timers are stopped, repaint the complete visible viewport
  // deliberately. A coalesced zero-delay pass handles focus events that arrive
  // just before document.hasFocus() reflects the foreground transition.
  // Restore the cursor option immediately, but defer the expensive full paint.
  // Chromium commonly delivers visibilitychange and focus back-to-back; doing
  // the paint before coalescing would redraw every terminal for both events.
  syncAllTerminalCursorActivity();
  if (foregroundCursorRecoveryTimer !== null) {
    return;
  }

  foregroundCursorRecoveryTimer = window.setTimeout(() => {
    foregroundCursorRecoveryTimer = null;
    if (document.visibilityState !== 'hidden') {
      syncAllTerminalCursorActivity(false, true);
    }
  }, 0);
}

function getOwnedTerminalInput(container: HTMLDivElement): HTMLTextAreaElement | null {
  const activeElement = document.activeElement;
  if (isTerminalInputOwnerElement(activeElement) && container.contains(activeElement)) {
    return activeElement;
  }

  if (isTerminalKeyAuditEnabled()) {
    return getOwnedTerminalInputProxy(container) ?? getOwnedXtermTextarea(container);
  }

  return getOwnedXtermTextarea(container) ?? getOwnedTerminalInputProxy(container);
}

function shouldCaptureTerminalKey(container: HTMLDivElement, target: EventTarget | null): boolean {
  if (isTerminalInputOwnerElement(target as Element | null) && container.contains(target as Node)) {
    return true;
  }

  const ownedInput = getOwnedTerminalInput(container);
  return ownedInput !== null && document.activeElement === ownedInput;
}

function getFocusedTerminalSessionMatch(): { sessionId: string; container: HTMLDivElement } | null {
  const activeElement = document.activeElement;
  if (!isTerminalInputOwnerElement(activeElement)) {
    return null;
  }

  for (const [sessionId, state] of sessionTerminals.entries()) {
    if (!state.opened) {
      continue;
    }

    if (state.container.contains(activeElement)) {
      return { sessionId, container: state.container };
    }
  }

  return null;
}

function tryHandleTerminalEnterOverride(
  sessionId: string,
  event: KeyboardEvent,
  container?: HTMLDivElement,
  source = 'enter',
): boolean {
  if (event.type !== 'keydown' || event.defaultPrevented || event.isComposing) {
    recordTerminalKeyDebugEvent(sessionId, source, event, container, 'skip=prehandled');
    return false;
  }

  if (container && !shouldCaptureTerminalKey(container, event.target)) {
    recordTerminalKeyDebugEvent(sessionId, source, event, container, 'skip=not-owner');
    return false;
  }

  const latch = enterModifierLatches.get(sessionId);
  const effectiveEvent = applyEnterModifierLatch(
    event,
    latch,
    performance.now(),
    ENTER_MODIFIER_LATCH_MAX_AGE_MS,
  );
  const effectiveLogEvent: TerminalKeyLogEventLike = {
    ...effectiveEvent,
    type: event.type,
    target: event.target,
    defaultPrevented: event.defaultPrevented,
    isComposing: event.isComposing,
  };
  const enterOverride = getSessionEnterOverride(sessionId, effectiveEvent);
  const effectiveParts: string[] = [`latch=${formatEnterModifierLatchState(latch)}`];
  if (effectiveEvent.ctrlKey !== event.ctrlKey || effectiveEvent.shiftKey !== event.shiftKey) {
    effectiveParts.push(
      `effectiveMods=${effectiveEvent.ctrlKey ? 'C' : '-'}${effectiveEvent.shiftKey ? 'S' : '-'}`,
    );
  }

  if (enterOverride === null) {
    recordTerminalKeyDebugEvent(
      sessionId,
      source,
      effectiveLogEvent,
      container,
      `${effectiveParts.join(' ')} override=none`,
    );
    return false;
  }
  const [enterOverrideBytes, enterOverrideTarget] = enterOverride;
  const routeThroughXtermInput = shouldRouteTerminalEnterOverrideThroughXtermInput(
    enterOverrideTarget,
    enterOverrideBytes,
  );
  const deliveryDescription = describeTerminalEnterOverrideDelivery(
    enterOverrideTarget,
    enterOverrideBytes,
  );

  recordTerminalKeyDebugEvent(
    sessionId,
    source,
    effectiveLogEvent,
    container,
    `${effectiveParts.join(' ')} override=${deliveryDescription}`,
  );

  event.preventDefault();
  event.stopPropagation();
  event.stopImmediatePropagation();
  enterOverrideSuppress.markTerminalEnterOverrideHandled(sessionId);
  deliverTerminalEnterOverride(sessionId, enterOverrideBytes, routeThroughXtermInput);
  recordTerminalKeyDebugEvent(
    sessionId,
    `${source}-sent`,
    effectiveLogEvent,
    container,
    `send=${deliveryDescription}`,
  );
  return true;
}

function focusTerminalInput(state: TerminalState): void {
  if (isTerminalKeyAuditEnabled()) {
    const proxy = state.inputProxy ?? getOwnedTerminalInputProxy(state.container);
    if (proxy) {
      setTerminalVisualFocus(state, true);
      proxy.focus({ preventScroll: true });
      proxy.value = '';
      syncTerminalCursorActivity(state, true);
      refreshCursorBlink(state.terminal);
      return;
    }
  }

  state.terminal.focus();
  setTerminalVisualFocus(state, true);
  syncTerminalCursorActivity(state, true);
  refreshCursorBlink(state.terminal);
}

/**
 * Reset the cursor blink timer on a terminal.
 * Toggling cursorBlink forces xterm.js to reinitialize its blink handler,
 * which fixes the cursor getting stuck in the invisible blink phase.
 */
export function refreshCursorBlink(terminal: Terminal): void {
  if (terminal.options.cursorBlink) {
    terminal.options.cursorBlink = false;
    terminal.options.cursorBlink = true;
  }
}

// WebGL context ownership: browsers cap live WebGL contexts per page and evict
// the oldest one when the cap is exceeded, which used to permanently downgrade
// terminals to the DOM renderer. Visible terminals (active session plus layout
// panes) get priority: they may evict a hidden terminal's context, and a lost
// context is retried with backoff instead of being given up forever.
const webglPrioritySessionIds = new Set<string>();
let webglPriorityKnown = false;
const webglContextLossTimestamps = new Map<string, number[]>();
const foregroundDomRendererRecovery = new WeakSet<TerminalState>();
const WEBGL_REATTACH_BASE_DELAY_MS = 1500;
const WEBGL_REATTACH_MAX_DELAY_MS = 30000;
const WEBGL_LOSS_WINDOW_MS = 60000;
const WEBGL_LOSS_SLOW_RETRY_THRESHOLD = 3;

function isWebglOwnershipManaged(state: TerminalState): boolean {
  // Aux terminals (e.g. the command output panel) live outside session
  // wrappers and manage their renderer through their own lifecycle.
  return Boolean(state.container.closest('.session-wrapper'));
}

function hasWebglPriority(sessionId: string, state: TerminalState): boolean {
  return shouldOwnWebglContext(
    isWebglOwnershipManaged(state),
    webglPriorityKnown,
    webglPrioritySessionIds.has(sessionId),
  );
}

function evictWebglContextForPrioritySession(): boolean {
  for (const candidateId of terminalsWithWebgl) {
    const candidate = sessionTerminals.get(candidateId);
    if (!candidate || !isWebglOwnershipManaged(candidate)) {
      continue;
    }

    if (!webglPrioritySessionIds.has(candidateId)) {
      detachWebglAddon(candidateId, candidate);
      return true;
    }
  }

  return false;
}

function recordWebglContextLoss(sessionId: string): number {
  const now = Date.now();
  const recent = (webglContextLossTimestamps.get(sessionId) ?? []).filter(
    (stamp) => now - stamp < WEBGL_LOSS_WINDOW_MS,
  );
  recent.push(now);
  webglContextLossTimestamps.set(sessionId, recent);
  return recent.length;
}

function scheduleWebglReattach(sessionId: string, state: TerminalState, delayMs: number): void {
  window.setTimeout(() => {
    if (sessionTerminals.get(sessionId) !== state || !state.opened || state.hasWebgl) {
      return;
    }
    if (!shouldUseWebglRenderer($currentSettings.get())) {
      return;
    }
    if (!hasWebglPriority(sessionId, state)) {
      // Hidden terminals get their context back through the ownership sync
      // once they become visible again.
      return;
    }

    if (attachWebglAddon(sessionId, state)) {
      // A fresh renderer re-uploads from the shared atlas; clearing it here
      // would force every terminal sharing the atlas to re-rasterize.
      refreshTerminalRenderer(state, { preserveTextureAtlas: true });
    } else {
      scheduleWebglReattach(sessionId, state, Math.min(delayMs * 2, WEBGL_REATTACH_MAX_DELAY_MS));
    }
  }, delayMs);
}

function detachWebglAddon(sessionId: string, state: TerminalState): void {
  detachTerminalLigatureState(state);
  const addon = state.webglAddon;
  state.webglAddon = null;

  if (state.hasWebgl) {
    terminalsWithWebgl.delete(sessionId);
    state.hasWebgl = false;
  }

  if (addon) {
    try {
      addon.dispose();
    } catch {
      // Renderer was already torn down.
    }
  }
}

function attachWebglAddon(sessionId: string, state: TerminalState): boolean {
  if (!state.opened) {
    return false;
  }
  if (foregroundDomRendererRecovery.has(state)) {
    return false;
  }
  if (!hasWebglPriority(sessionId, state)) {
    return false;
  }
  if (state.hasWebgl) {
    return true;
  }

  if (terminalsWithWebgl.size >= MAX_WEBGL_CONTEXTS) {
    if (!hasWebglPriority(sessionId, state) || !evictWebglContextForPrioritySession()) {
      return false;
    }
  }

  try {
    // Preserve the draw buffer so browser screenshot capture paths (html2canvas)
    // can read terminal pixels when WebGL rendering is enabled.
    const webglAddon = new WebglAddon({
      customGlyphs: state.webglCustomGlyphs,
      preserveDrawingBuffer: true,
    });
    webglAddon.onContextLoss(() => {
      if (state.webglAddon !== webglAddon) {
        return;
      }

      detachWebglAddon(sessionId, state);

      const recentLosses = recordWebglContextLoss(sessionId);
      const delayMs =
        recentLosses >= WEBGL_LOSS_SLOW_RETRY_THRESHOLD
          ? WEBGL_REATTACH_MAX_DELAY_MS
          : WEBGL_REATTACH_BASE_DELAY_MS;
      scheduleWebglReattach(sessionId, state, delayMs);
    });
    state.terminal.loadAddon(webglAddon);
    state.webglAddon = webglAddon;
    terminalsWithWebgl.add(sessionId);
    state.hasWebgl = true;
    return true;
  } catch {
    state.webglAddon = null;
    state.hasWebgl = false;
    return false;
  }
}

export function syncTerminalWebglState(
  sessionId: string,
  state: TerminalState,
  enabled: boolean,
  customGlyphs: boolean = state.webglCustomGlyphs,
): void {
  if (customGlyphs !== state.webglCustomGlyphs) {
    state.webglCustomGlyphs = customGlyphs;
    detachWebglAddon(sessionId, state);
  }

  if (!enabled) {
    foregroundDomRendererRecovery.delete(state);
    detachWebglAddon(sessionId, state);
    return;
  }

  attachWebglAddon(sessionId, state);
}

/**
 * Tell the renderer which sessions are currently visible (active session plus
 * layout panes). Visible sessions missing their WebGL context get it back,
 * evicting hidden holders when the context budget is exhausted.
 */
export function syncWebglSessionPriority(prioritySessionIds: readonly string[]): void {
  webglPriorityKnown = true;
  webglPrioritySessionIds.clear();
  for (const sessionId of prioritySessionIds) {
    webglPrioritySessionIds.add(sessionId);
  }

  if (!shouldUseWebglRenderer($currentSettings.get())) {
    return;
  }

  // Retain visited renderers within MAX_WEBGL_CONTEXTS. Destroying a renderer
  // on every tab switch reallocates its canvases and causes a visible repaint.
  // Existing eviction gives visible sessions priority and removes the least
  // recently used hidden holder only when a new context needs the budget.
  for (const sessionId of prioritySessionIds) {
    if (terminalsWithWebgl.delete(sessionId)) terminalsWithWebgl.add(sessionId);
  }

  webglPrioritySessionIds.forEach((sessionId) => {
    const state = sessionTerminals.get(sessionId);
    if (!state || !state.opened || state.hasWebgl) {
      return;
    }

    if (attachWebglAddon(sessionId, state)) {
      // Session-switch churn must not clear the shared texture atlas.
      refreshTerminalRenderer(state, { preserveTextureAtlas: true });
    }
  });
}

export function recoverTerminalRendererAfterForeground(
  sessionId: string,
  state: TerminalState,
  options: { preferDomRenderer?: boolean } = {},
): void {
  if (!state.opened) {
    return;
  }

  const settings = $currentSettings.get();
  if (options.preferDomRenderer) {
    // requestAnimationFrame itself has been unavailable for five seconds. Do
    // not create another GPU context while the compositor is still stalled;
    // xterm's fallback renderer is rebuilt synchronously from the same buffer.
    if (state.hasWebgl && !foregroundDomRendererRecovery.has(state)) {
      log.warn(
        () =>
          `Terminal ${sessionId} temporarily using DOM renderer after foreground frames stalled`,
      );
      foregroundDomRendererRecovery.add(state);
      detachWebglAddon(sessionId, state);
    }
    syncTerminalLigatureState(state, settings?.terminalLigaturesEnabled ?? true);
    syncTerminalRgbBackgroundTransparency(state, settings);
    refreshTerminalRenderer(state, { preserveTextureAtlas: true });
    return;
  }

  // This was tlbx's reliable pre-v10.2.11 behavior: never trust a WebGL
  // framebuffer across a browser-background lifecycle. Recreate only the
  // visible terminal's renderer and use xterm's buffer as the source of truth.
  // Unlike the old path, preserve the shared glyph atlas so foreground recovery
  // does not invalidate every other terminal's cached glyphs.
  foregroundDomRendererRecovery.delete(state);
  if (state.hasWebgl) {
    detachWebglAddon(sessionId, state);
  }
  const wantsWebgl = shouldUseWebglRenderer(settings) && hasWebglPriority(sessionId, state);
  if (wantsWebgl && !attachWebglAddon(sessionId, state)) {
    scheduleWebglReattach(sessionId, state, WEBGL_REATTACH_BASE_DELAY_MS);
  }
  syncTerminalLigatureState(state, settings?.terminalLigaturesEnabled ?? true);
  syncTerminalRgbBackgroundTransparency(state, settings);
  refreshTerminalRenderer(state, { preserveTextureAtlas: true });

  window.requestAnimationFrame(() => {
    if (sessionTerminals.get(sessionId) !== state || !state.opened) return;
    refreshTerminalRenderer(state, { preserveTextureAtlas: true });
  });
}

/**
 * Focus the active terminal, debounced to prevent rapid focus/blur cycles.
 * Respects search panel - won't focus if search is visible.
 */
export function focusActiveTerminal(): void {
  if (isEmbeddedWebPreviewContext() || isSearchVisible() || hasNonTerminalFocus()) return;

  if (isSmartInputMode()) {
    showSmartInput();
    return;
  }

  if (focusDebounceTimer !== null) {
    window.clearTimeout(focusDebounceTimer);
  }

  focusDebounceTimer = window.setTimeout(() => {
    focusDebounceTimer = null;
    if (hasNonTerminalFocus()) return;

    const activeId = $activeSessionId.get();
    if (!activeId) return;

    const state = sessionTerminals.get(activeId);
    if (state?.opened) {
      focusTerminalInput(state);
    }
  }, 16);
}

function hasNonTerminalFocus(): boolean {
  const el = document.activeElement;
  if (!el) return false;
  if (
    el instanceof HTMLTextAreaElement &&
    (el.classList.contains('xterm-helper-textarea') ||
      el.classList.contains('tlbx-terminal-input-proxy'))
  ) {
    return false;
  }
  const tag = el.tagName;
  return (
    tag === 'INPUT' ||
    tag === 'TEXTAREA' ||
    tag === 'SELECT' ||
    (el as HTMLElement).isContentEditable
  );
}

function hasActiveDocumentSelection(): boolean {
  if (typeof window === 'undefined' || typeof window.getSelection !== 'function') {
    return false;
  }

  const selection = window.getSelection();
  return !!selection && selection.rangeCount > 0 && !selection.isCollapsed;
}

function shouldSkipGlobalFocusReclaim(target: HTMLElement): boolean {
  if (hasActiveDocumentSelection()) {
    return true;
  }

  if (!shouldReclaimTerminalFocusOnMouseUp(target)) {
    return true;
  }

  if (target.closest(FOCUS_RECLAIM_EXEMPT_SELECTOR)) {
    return true;
  }

  const activeSessionId = $activeSessionId.get();
  if (!activeSessionId) {
    return false;
  }

  return getActiveTab(activeSessionId) !== 'terminal';
}
/**
 * Reclaim terminal focus after clicks on non-interactive UI (sidebar, buttons, etc.).
 * Skips refocus when the click lands on an element that needs its own keyboard input.
 */
export function setupGlobalFocusReclaim(): void {
  if (!globalTerminalEnterOverrideInstalled) {
    globalTerminalEnterOverrideInstalled = true;

    window.addEventListener('blur', () => {
      syncAllTerminalCursorActivity(true);
    });
    window.addEventListener('focus', () => {
      recoverTerminalCursorsAfterForeground();
    });
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'hidden') {
        syncAllTerminalCursorActivity(true);
      } else {
        recoverTerminalCursorsAfterForeground();
      }
    });

    document.addEventListener(
      'keydown',
      (event) => {
        const match = getFocusedTerminalSessionMatch();
        if (!match) {
          return;
        }

        updateSessionEnterModifierLatch(match.sessionId, event, match.container, 'document-latch');
        tryHandleTerminalEnterOverride(match.sessionId, event, match.container, 'document-enter');
      },
      true,
    );

    document.addEventListener(
      'keyup',
      (event) => {
        const match = getFocusedTerminalSessionMatch();
        if (!match) {
          return;
        }

        updateSessionEnterModifierLatch(match.sessionId, event, match.container, 'document-latch');
      },
      true,
    );
  }

  document.addEventListener('mouseup', (e) => {
    const target =
      e.target instanceof HTMLElement
        ? e.target
        : e.target instanceof Node
          ? e.target.parentElement
          : null;
    if (!target || shouldSkipGlobalFocusReclaim(target)) {
      return;
    }
    focusActiveTerminal();
  });
}

/**
 * Auto-update session terminalTitle from shell title (with debounce).
 * Always sends to server to update terminalTitle field.
 * Server will only update 'name' if session is not manually named.
 */
function updateSessionNameAuto(sessionId: string, name: string): void {
  const existing = pendingTitleUpdates.get(sessionId);
  if (existing) {
    window.clearTimeout(existing);
  }

  const timer = window.setTimeout(() => {
    pendingTitleUpdates.delete(sessionId);
    sendCommand('session.rename', { sessionId, name, auto: true }).catch(() => {});
  }, 500);

  pendingTitleUpdates.set(sessionId, timer);
}

/**
 * Create a terminal instance for a session.
 * Returns existing state if terminal already exists.
 */
export function createTerminalForSession(
  sessionId: string,
  sessionInfo: Session | undefined,
): TerminalState {
  const existing = sessionTerminals.get(sessionId);
  if (existing) {
    return existing;
  }

  // Create container
  const scrollbarStyle = normalizeScrollbarStyle($currentSettings.get()?.scrollbarStyle);
  const container = document.createElement('div');
  container.className = 'terminal-container hidden';
  applyTerminalScrollbarStyleClass(container, scrollbarStyle);
  container.id = 'terminal-' + sessionId;
  dom.terminalsArea?.appendChild(container);

  // Set up file drop handler for drag-and-drop uploads
  setupFileDrop(container);

  // Initialize xterm.js
  const terminal = new Terminal(getTerminalOptions());
  const fitAddon = new FitAddon();
  terminal.loadAddon(fitAddon);

  // Load Unicode11 addon for proper emoji and CJK character width handling
  const unicode11 = new Unicode11Addon();
  terminal.loadAddon(unicode11);
  terminal.unicode.activeVersion = '11';

  // Get server dimensions from session info (if available)
  const serverCols = sessionInfo?.cols ?? 0;
  const serverRows = sessionInfo?.rows ?? 0;

  const state: TerminalState = {
    terminal: terminal,
    fitAddon: fitAddon,
    container: container,
    inputProxy: null,
    serverCols: serverCols > 0 ? serverCols : 0,
    serverRows: serverRows > 0 ? serverRows : 0,
    opened: false,
    hasWebgl: false,
    webglAddon: null,
    webglCustomGlyphs: $currentSettings.get()?.customGlyphs ?? true,
    ligatureJoinerId: null,
    pendingVisualRefresh: false,
  };

  sessionTerminals.set(sessionId, state);

  // Wait for fonts to be ready before opening terminal
  // This ensures xterm.js measures the correct font for canvas rendering
  void (fontsReadyPromise ?? Promise.resolve()).then(() => {
    if (!sessionTerminals.has(sessionId)) return; // Session was deleted

    try {
      terminal.open(container);
    } catch (e) {
      log.error(() => `Terminal ${sessionId} failed to open: ${String(e)}`);
      container.innerHTML =
        '<div class="terminal-error">Terminal failed to initialize. <button onclick="location.reload()">Reload</button></div>';
      container.classList.remove('hidden');
      return;
    }

    state.opened = true;
    runWithGuaranteedTerminalReplay(
      () => {
        syncEffectiveXtermThemeDomOverrides($currentSettings.get());

        if (getComputedStyle(container).position === 'static') {
          container.style.position = 'relative';
        }
        const inputProxy = document.createElement('textarea');
        inputProxy.className = 'tlbx-terminal-input-proxy';
        inputProxy.tabIndex = -1;
        inputProxy.setAttribute('aria-label', 'Terminal input');
        inputProxy.setAttribute('autocorrect', 'off');
        inputProxy.autocapitalize = 'off';
        inputProxy.spellcheck = false;
        Object.assign(inputProxy.style, {
          position: 'absolute',
          inset: '0',
          opacity: '0',
          pointerEvents: 'none',
          resize: 'none',
          border: '0',
          margin: '0',
          padding: '0',
          background: 'transparent',
          color: 'transparent',
          caretColor: 'transparent',
          outline: 'none',
          overflow: 'hidden',
          zIndex: '5',
        });
        container.appendChild(inputProxy);
        state.inputProxy = inputProxy;

        // Intercept xterm's internal textarea focus when Smart Input is active
        const xtermTextarea = container.querySelector('textarea.xterm-helper-textarea');
        if (xtermTextarea) {
          xtermTextarea.addEventListener('focus', () => {
            setTerminalVisualFocus(state, true);
            syncTerminalCursorActivity(state, true);
            if (isSmartInputMode()) {
              (xtermTextarea as HTMLTextAreaElement).blur();
              showSmartInput();
              return;
            }

            if (isTerminalKeyAuditEnabled() && state.inputProxy) {
              (xtermTextarea as HTMLTextAreaElement).blur();
              state.inputProxy.focus({ preventScroll: true });
            }
          });
          xtermTextarea.addEventListener('blur', () => {
            setTerminalVisualFocus(state, false);
            syncTerminalCursorActivity(state, false);
            enterModifierLatches.delete(sessionId);
          });
        }
        inputProxy.addEventListener('focus', () => {
          setTerminalVisualFocus(state, true);
          syncTerminalCursorActivity(state, true);
        });
        inputProxy.addEventListener('blur', () => {
          setTerminalVisualFocus(state, false);
          syncTerminalCursorActivity(state, false);
          enterModifierLatches.delete(sessionId);
        });
        syncTerminalCursorActivity(state);

        // Register onData immediately to avoid losing keystrokes during font/rAF delay
        // Other event handlers are set up later in setupTerminalEvents
        state.earlyDataDisposable = onTerminalInput(
          terminal,
          (data: string, userInput: boolean) => {
            if (!userInput) {
              sendTerminalResponse(sessionId, data);
              return;
            }
            resumeMobileStableTerminalCursorFollowing(state);
            captureTerminalInputData(sessionId, data);
            sendInput(sessionId, data);
          },
        );

        // Load WebGL addon for GPU-accelerated rendering (with context limit)
        // Browser limits ~6-8 simultaneous WebGL contexts, so we track usage
        const settings = $currentSettings.get();
        syncWebglTerminalCellBackgroundAlpha(settings);
        syncTerminalWebglState(
          sessionId,
          state,
          shouldUseWebglRenderer(settings),
          settings?.customGlyphs ?? true,
        );
        syncTerminalLigatureState(state, settings?.terminalLigaturesEnabled ?? true);
        syncTerminalRgbBackgroundTransparency(state, settings);

        // ImageAddon needs the opened terminal and its active renderer. Loading it
        // after the WebGL/canvas selection keeps the image renderer bound to the
        // renderer that will actually paint the session, including replayed output.
        // Capability discovery itself is answered by mthost so it also works before
        // a browser attaches. Keep decoded memory and individual payloads bounded.
        try {
          terminal.loadAddon(
            new ImageAddon({
              enableSizeReports: true,
              pixelLimit: 4_194_304,
              storageLimit: 32,
              showPlaceholder: true,
              kittySupport: true,
              kittySizeLimit: 16_777_216,
              sixelSupport: true,
              sixelSizeLimit: 16_777_216,
              iipSupport: true,
              iipSizeLimit: 16_777_216,
            }),
          );
        } catch (error) {
          // Graphics are progressive enhancement. A future xterm/addon mismatch
          // must degrade to text instead of making the session unusable.
          log.warn(() => `Terminal ${sessionId} graphics initialization failed: ${String(error)}`);
        }

        // Load Web-Links addon for clickable URLs
        try {
          const webLinksAddon = new WebLinksAddon(activateTerminalLink);
          terminal.loadAddon(webLinksAddon);
        } catch {
          // Web-Links addon failed to load
        }

        // Register file link provider for clickable file paths
        registerFileLinkProvider(terminal, sessionId);

        // Load Search addon for Ctrl+F search
        initSearchForTerminal(sessionId, terminal);
      },
      () => {
        replayPendingFrames(sessionId, state);
      },
      (error) => {
        // Once xterm itself is open, optional input/rendering/link/search setup
        // must never prevent buffered PTY output from becoming visible.
        log.warn(() => `Terminal ${sessionId} optional initialization failed: ${String(error)}`);
      },
    );
    scheduleTerminalStartupPaintRecovery(sessionId, state);

    window.setTimeout(() => {
      const currentState = sessionTerminals.get(sessionId);
      if (!currentState?.opened) return;

      const renderedSequence = getBrowserTransportSnapshot(sessionId)?.renderedSeq ?? null;
      if (shouldRequestInitialTerminalReplay(renderedSequence)) {
        log.warn(() => `Terminal ${sessionId} rendered no startup output; requesting full replay`);
        requestBufferRefresh(sessionId, 'fullReplay', 'terminal_open_without_rendered_output');
      }
    }, 500);

    // Defer resize to next frame - xterm.js needs a frame to fully initialize after open()
    requestAnimationFrame(() => {
      if (!sessionTerminals.has(sessionId)) return; // Session was deleted

      // Sync xterm to server dimensions (local only, no server notification)
      // This ensures the terminal matches what the server has without triggering resize race conditions
      if (state.serverCols > 0 && state.serverRows > 0) {
        try {
          state.terminal.resize(state.serverCols, state.serverRows);
        } catch {
          // Resize may fail if terminal not fully initialized
        }
      }

      // Double-rAF: let the resize paint before measuring for scaling
      requestAnimationFrame(() => {
        if (hasTerminalSizeControl(sessionId)) {
          const layoutPane = container.closest<HTMLElement>('.layout-leaf');
          if (layoutPane) {
            fitTerminalToContainer(sessionId, layoutPane);
          } else if (!container.classList.contains('hidden')) {
            fitSessionToScreen(sessionId);
          } else {
            applyTerminalScalingSync(state);
          }
        } else {
          applyTerminalScalingSync(state);
        }
        setupTerminalEvents(sessionId, terminal, container);
        focusActiveTerminal();
      });
    });
  });

  return state;
}

/**
 * Replay pending output frames that arrived before terminal was opened.
 * If frames overflowed, request a full buffer refresh instead.
 */
function replayPendingFrames(sessionId: string, state: TerminalState): void {
  // Check if this session overflowed and needs a full resync
  if (sessionsNeedingResync.has(sessionId)) {
    sessionsNeedingResync.delete(sessionId);
    pendingOutputFrames.delete(sessionId);
    pendingTerminalReplayModes.delete(sessionId);
    requestBufferRefresh(sessionId);
    return;
  }

  const replayFrames = (): void => {
    const frames = pendingOutputFrames.get(sessionId);
    if (frames && frames.length > 0) {
      frames.forEach((payload) => {
        writeMuxOutputFrame(sessionId, state, payload);
      });
    }
    pendingOutputFrames.delete(sessionId);
  };

  const replayMode = pendingTerminalReplayModes.get(sessionId) ?? 0;
  pendingTerminalReplayModes.delete(sessionId);
  const replayPrefix = createAlternateScreenReplayPrefix(replayMode);
  if (replayPrefix !== null) {
    try {
      state.terminal.write(replayPrefix, replayFrames);
      return;
    } catch {
      // Fall through and replay bytes so opening the terminal cannot deadlock.
    }
  }

  replayFrames();
}

/**
 * Set up terminal event handlers for input, bell, selection, etc.
 */
export function setupTerminalEvents(
  sessionId: string,
  terminal: Terminal,
  container: HTMLDivElement,
): void {
  const canUseAsyncClipboard = (): boolean =>
    window.isSecureContext &&
    typeof navigator.clipboard !== 'undefined' &&
    typeof navigator.clipboard.readText === 'function';
  // Collect disposables for cleanup
  const disposables: Array<{ dispose: () => void }> = [];

  // Dispose early data handler (was registered immediately after terminal.open)
  const termState = sessionTerminals.get(sessionId);
  if (termState?.earlyDataDisposable) {
    termState.earlyDataDisposable.dispose();
    delete termState.earlyDataDisposable;
  }

  // Wire up events - onData replaces the early handler
  if (termState) {
    disposables.push(
      onTerminalScrollbackExit(termState, () => {
        if (sessionTerminals.get(sessionId) !== termState || !isTerminalVisible(termState)) return;
        const pane = container.closest<HTMLElement>('.layout-leaf');
        if (pane) fitTerminalToContainer(sessionId, pane);
        else fitSessionToScreen(sessionId);
      }),
    );
  }

  disposables.push(
    onTerminalInput(terminal, (data: string, userInput: boolean) => {
      if (!userInput) {
        sendTerminalResponse(sessionId, data);
        return;
      }
      const state = sessionTerminals.get(sessionId);
      if (state) {
        resumeMobileStableTerminalCursorFollowing(state);
      }
      captureTerminalInputData(sessionId, data);
      sendInput(sessionId, data);
    }),
  );

  disposables.push(
    ...registerTerminalNotificationHandlers(terminal, (signal) => {
      showTerminalNotification(sessionId, signal);
    }),
  );

  disposables.push(
    terminal.onWriteParsed(() => {
      reconcileSynchronizedOutputCursor(sessionId);
      const state = sessionTerminals.get(sessionId);
      if (state) {
        revealMobileStableTerminalCursor(state);
      }
    }),
  );

  // OSC 52 clipboard: programs in the terminal can set the browser clipboard
  // Format: ESC ] 52 ; <selection> ; <base64-data> BEL/ST
  // This enables remote clipboard for tools like Claude Code, vim, tmux
  disposables.push(
    terminal.parser.registerOscHandler(52, (data: string) => {
      const semicolonIdx = data.indexOf(';');
      if (semicolonIdx < 0) return false;
      const b64 = data.substring(semicolonIdx + 1);
      if (!b64 || b64 === '?') return false;
      try {
        const bytes = Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
        const text = new TextDecoder().decode(bytes);
        navigator.clipboard.writeText(text).catch(() => {});
      } catch {
        // invalid base64 or clipboard unavailable
      }
      return true;
    }),
  );

  // OSC 7: CWD reporting — shells emit file://hostname/path on every prompt
  disposables.push(
    terminal.parser.registerOscHandler(7, (data: string) => {
      const match = data.match(/^file:\/\/[^/]*(\/.*)/);
      if (!match?.[1]) return false;
      let path = decodeURIComponent(match[1]);
      if (/^\/[A-Za-z]:/.test(path)) {
        path = path.substring(1).replace(/\//g, '\\');
      }
      handleOsc7Cwd(sessionId, path);
      return true;
    }),
  );

  disposables.push(
    terminal.onSelectionChange(() => {
      if ($currentSettings.get()?.copyOnSelect && terminal.hasSelection()) {
        navigator.clipboard.writeText(sanitizeCopyContent(terminal.getSelection())).catch(() => {});
      }
    }),
  );

  // Auto-update session name from shell title
  disposables.push(
    terminal.onTitleChange((title: string) => {
      if (title && title.trim()) {
        updateSessionNameAuto(sessionId, title.trim());
      }
    }),
  );

  const isMac = isMacPlatform();
  const isWindows = isWindowsPlatform();
  const macOptionIsMeta = terminal.options.macOptionIsMeta === true;
  const isKeyAuditActive = (): boolean => isTerminalKeyAuditEnabled();
  const {
    contextMenuHandler,
    disposables: interactionDisposables,
    enterOverrideHandler,
    pasteHandler,
  } = bindTerminalInteractionHandlers({
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
    wasEnterOverrideHandledRecently: enterOverrideSuppress.wasTerminalEnterOverrideHandledRecently,
    terminal,
    tryHandleTerminalEnterOverride,
    updateSessionEnterModifierLatch,
  });
  disposables.push(...interactionDisposables);

  // Auto-hide mouse cursor after 2 seconds of inactivity
  const CURSOR_HIDE_DELAY = 2000;

  const mouseMoveHandler = () => {
    container.classList.remove('cursor-hidden');
    const s = sessionTerminals.get(sessionId);
    if (s?.cursorHideTimer != null) {
      window.clearTimeout(s.cursorHideTimer);
    }
    const timer = window.setTimeout(() => {
      container.classList.add('cursor-hidden');
    }, CURSOR_HIDE_DELAY);
    if (s) s.cursorHideTimer = timer;
  };

  const mouseLeaveHandler = () => {
    container.classList.remove('cursor-hidden');
    const s = sessionTerminals.get(sessionId);
    if (s?.cursorHideTimer != null) {
      window.clearTimeout(s.cursorHideTimer);
      s.cursorHideTimer = null;
    }
  };

  container.addEventListener('mousemove', mouseMoveHandler);
  container.addEventListener('pointerleave', mouseLeaveHandler);

  // Store handler references for cleanup
  const state = sessionTerminals.get(sessionId);
  if (state) {
    state.contextMenuHandler = contextMenuHandler;
    state.pasteHandler = pasteHandler;
    state.enterOverrideHandler = enterOverrideHandler;
    state.disposables = disposables;
    state.mouseMoveHandler = mouseMoveHandler;
    state.mouseLeaveHandler = mouseLeaveHandler;
  }

  // Touch scrolling overlay (mobile only — scroll-first, long-press to select)
  initTouchScrolling(sessionId, terminal, container);
}

/**
 * Destroy a terminal for a session and clean up resources
 */
export function destroyTerminalForSession(sessionId: string): void {
  const state = sessionTerminals.get(sessionId);
  if (!state) return;

  clearTerminalStartupPaintRecovery(sessionId);
  terminalStartupPaintRecreations.delete(sessionId);

  enterModifierLatches.delete(sessionId);
  enterOverrideSuppress.clearTerminalEnterOverrideHandled(sessionId);
  clearTerminalInputCapture(sessionId);

  // Clean up xterm event disposables
  if (state.disposables) {
    state.disposables.forEach((d) => {
      d.dispose();
    });
  }

  // Clean up early data handler if terminal was destroyed before setupTerminalEvents ran
  if (state.earlyDataDisposable) {
    state.earlyDataDisposable.dispose();
  }

  // Clean up DOM event listeners
  if (state.contextMenuHandler) {
    state.container.removeEventListener('contextmenu', state.contextMenuHandler);
  }
  if (state.pasteHandler) {
    state.container.removeEventListener('paste', state.pasteHandler, true);
  }
  if (state.enterOverrideHandler) {
    state.container.removeEventListener('keydown', state.enterOverrideHandler, true);
  }
  if (state.mouseMoveHandler) {
    state.container.removeEventListener('mousemove', state.mouseMoveHandler);
  }
  if (state.mouseLeaveHandler) {
    state.container.removeEventListener('pointerleave', state.mouseLeaveHandler);
  }

  // Clean up search addon state
  cleanupSearchForTerminal(sessionId);

  // Clean up cursor hide timer
  if (state.cursorHideTimer != null) {
    clearTimeout(state.cursorHideTimer);
  }
  if (state.burstCursorRestoreTimer != null) {
    clearTimeout(state.burstCursorRestoreTimer);
  }
  state.burstCursorRestoreDueAtMs = null;

  // Clean up pending title update timer
  const titleTimer = pendingTitleUpdates.get(sessionId);
  if (titleTimer) {
    clearTimeout(titleTimer);
    pendingTitleUpdates.delete(sessionId);
  }

  // Clean up touch scrolling overlay
  teardownTouchScrolling(sessionId);

  // Clean up WebGL context tracking
  syncTerminalWebglState(sessionId, state, false);
  disposeTerminalRgbBackgroundTransparency(state);

  // Clean up file path allowlist
  clearPathAllowlist(sessionId);

  state.inputProxy?.remove();
  disposeTerminalPresentation(state);
  state.terminal.dispose();
  state.container.remove();
  sessionTerminals.delete(sessionId);
  forgetMuxSession(sessionId);
}

// WebSocket frame limit - backend MuxProtocol.MaxFrameSize is 64KB, use 32KB for safety margin
const PASTE_INDICATOR_THRESHOLD = 1024;
const MIN_BADGE_DISPLAY_MS = 300;

/**
 * Hide paste indicator after minimum display time.
 */
function hidePasteIndicatorDelayed(startTime: number): void {
  const elapsed = Date.now() - startTime;
  const remaining = MIN_BADGE_DISPLAY_MS - elapsed;
  if (remaining > 0) {
    setTimeout(hidePasteIndicator, remaining);
  } else {
    hidePasteIndicator();
  }
}

/**
 * Paste text to a terminal through the same server-side paste path used by mt_paste.
 * BPM state is tracked in muxChannel from live WebSocket data.
 *
 * The server normalizes clipboard line endings, applies bracketed paste markers,
 * and chunks non-BPM paste input with the same conservative delay.
 *
 * @param isFilePath - If true, wrap content in quotes for file path handling.
 */
export async function pasteToTerminal(
  sessionId: string,
  data: string,
  isFilePath: boolean = false,
  historySource?: string,
): Promise<void> {
  const state = sessionTerminals.get(sessionId);
  if (!state) return;

  const muxBpm = isBracketedPasteEnabled(sessionId);
  const xtermBpm = state.terminal.modes.bracketedPasteMode;
  const bpmEnabled = muxBpm || xtermBpm;

  const showIndicator = data.length > PASTE_INDICATOR_THRESHOLD;
  const startTime = Date.now();
  if (showIndicator) {
    showPasteIndicator();
  }

  captureTerminalPasteText(sessionId, data);
  try {
    await sendSessionPasteInput(sessionId, {
      text: data,
      bracketedPaste: bpmEnabled,
      isFilePath,
      ...(historySource ? { historySource } : {}),
    });
  } finally {
    if (showIndicator) {
      hidePasteIndicatorDelayed(startTime);
    }
  }
}

/**
 * Scroll terminal to bottom - always show most recent output when switching sessions
 */
export function scrollToBottom(sessionId: string): void {
  const state = sessionTerminals.get(sessionId);
  if (!state || !state.opened) return;
  state.terminal.scrollToBottom();
}

/**
 * Refresh the active terminal buffer by clearing and requesting via WebSocket.
 * Using WebSocket ensures the buffer arrives in-order with live terminal data.
 */
export function refreshActiveTerminalBuffer(): void {
  const activeId = $activeSessionId.get();
  if (!activeId) return;
  const state = sessionTerminals.get(activeId);
  if (state && state.opened) {
    state.terminal.clear();
    requestBufferRefresh(activeId);
  }
}

/**
 * Give the configured terminal font a short startup budget. Alternate font
 * choices must never delay xterm creation, and a late font only triggers one
 * renderer remeasure instead of reopening or resizing the terminal.
 */
export function preloadTerminalFont(): Promise<void> {
  const FONT_STARTUP_BUDGET_MS = 250;
  const baseFontSize = $currentSettings.get()?.fontSize ?? 14;
  const fontSize = getEffectiveTerminalFontSize(baseFontSize);
  const fontFamily = getConfiguredTerminalFontFamily();
  let startupBudgetExpired = false;
  let timeoutId: ReturnType<typeof globalThis.setTimeout> | null = null;

  const fontLoadPromise = ensureTerminalFontLoaded(fontFamily, fontSize).then(() => {
    return new Promise<void>((resolve) => {
      requestAnimationFrame(() => {
        resolve();
      });
    });
  });

  const timeoutPromise = new Promise<void>((resolve) => {
    timeoutId = globalThis.setTimeout(() => {
      startupBudgetExpired = true;
      timeoutId = null;
      resolve();
    }, FONT_STARTUP_BUDGET_MS);
  });

  const promise = Promise.race([fontLoadPromise, timeoutPromise]);
  void fontLoadPromise.then(() => {
    if (timeoutId !== null) {
      globalThis.clearTimeout(timeoutId);
      timeoutId = null;
    }
    if (!startupBudgetExpired) return;

    sessionTerminals.forEach((state) => {
      if (!state.opened) return;
      refreshTerminalRenderer(state);
      applyTerminalScalingSync(state);
    });
  });
  setFontsReadyPromise(promise);
  return promise;
}

/**
 * Initialize a hidden calibration terminal to get accurate cell measurements.
 * This creates a real xterm.js terminal, measures its rendered cell dimensions,
 * then disposes it. The measurement is used for sizing new terminals before
 * any real terminals exist.
 */
export function initCalibrationTerminal(): Promise<void> {
  calibrationPromise = new Promise((resolve) => {
    const container = document.createElement('div');
    container.style.cssText = `
      position: absolute;
      visibility: hidden;
      left: -9999px;
      width: 800px;
      height: 600px;
    `;
    document.body.appendChild(container);

    const terminal = new Terminal({
      ...getTerminalOptions(),
      cols: 80,
      rows: 24,
    });

    terminal.open(container);

    let completed = false;
    let frameId: number | null = null;
    let fallbackTimerId: number | null = null;
    const completeCalibration = (): void => {
      if (completed) return;
      completed = true;
      if (frameId !== null) cancelAnimationFrame(frameId);
      if (fallbackTimerId !== null) window.clearTimeout(fallbackTimerId);

      const screen = container.querySelector<HTMLElement>('.xterm-screen');
      if (screen && terminal.cols > 0 && terminal.rows > 0) {
        const cellWidth = screen.offsetWidth / terminal.cols;
        const cellHeight = screen.offsetHeight / terminal.rows;
        if (cellWidth >= 1 && cellHeight >= 1) {
          calibrationMeasurement = {
            cellWidth,
            cellHeight,
            fontFamily: terminal.options.fontFamily ?? buildTerminalFontStack(),
            fontSize: terminal.options.fontSize ?? 14,
            lineHeight: terminal.options.lineHeight ?? DEFAULT_TERMINAL_LINE_HEIGHT,
            letterSpacing: terminal.options.letterSpacing ?? DEFAULT_TERMINAL_LETTER_SPACING,
            fontWeight: String(terminal.options.fontWeight ?? DEFAULT_TERMINAL_FONT_WEIGHT),
            fontWeightBold: String(
              terminal.options.fontWeightBold ?? DEFAULT_TERMINAL_FONT_WEIGHT_BOLD,
            ),
          };
        }
      }

      terminal.dispose();
      container.remove();
      resolve();
    };

    frameId = requestAnimationFrame(completeCalibration);
    // Background and embedded browser frames may throttle requestAnimationFrame
    // indefinitely. Session launch must never wait forever for an optional
    // terminal sizing calibration.
    fallbackTimerId = window.setTimeout(completeCalibration, 250);
  });
  return calibrationPromise;
}

/**
 * Get the calibration measurement from the hidden terminal.
 * Returns null if calibration hasn't run or failed.
 */
export function getCalibrationMeasurement(): {
  cellWidth: number;
  cellHeight: number;
  fontFamily: string;
  fontSize: number;
  lineHeight: number;
  letterSpacing: number;
  fontWeight: string;
  fontWeightBold: string;
} | null {
  return calibrationMeasurement;
}

/**
 * Get the promise that resolves when calibration is complete.
 * Returns null if calibration hasn't been started.
 */
export function getCalibrationPromise(): Promise<void> | null {
  return calibrationPromise;
}
