/**
 * Touch Scrolling Module
 *
 * Fixes mobile touch behavior on xterm.js terminals.
 * Default: single-finger drag scrolls the terminal viewport.
 * Long-press (500ms): switches to xterm text selection mode.
 * Quick tap: focuses terminal / sends mouse click for TUI interaction.
 * Horizontal swipe: sends Ctrl+A (start) / Ctrl+E (end of line).
 *
 * Uses a transparent overlay (z-index: 20, above xterm internals) with
 * stopPropagation to fully isolate from xterm.js v6's document-level
 * gesture system, which otherwise converts touch scroll into cursor keys.
 */

import type { Terminal } from '@xterm/xterm';
import { isTouchDevice, hasPrecisePointer } from '../touchController/detection';
import { sendInput } from '../comms/muxChannel';
import { $currentSettings } from '../../stores';

const LONG_PRESS_MS = 500;
const MOVE_THRESHOLD = 10;
const TAP_MAX_DURATION = 300;
const SWIPE_THRESHOLD = 80;
const SWIPE_MAX_VERTICAL = 40;
const SWIPE_MAX_TIME = 300;
const KINETIC_MIN_VELOCITY_PX_PER_MS = 0.02;
const KINETIC_MAX_VELOCITY_PX_PER_MS = 8;
const KINETIC_FRICTION_PX_PER_MS2 = 0.0012;
const KINETIC_MAX_FRAME_MS = 32;
const VELOCITY_SAMPLE_SMOOTHING = 0.7;

type TouchMode = 'idle' | 'pending' | 'scrolling' | 'selecting' | 'horizontal';

interface TouchScrollState {
  overlay: HTMLDivElement;
  viewport: HTMLElement;
  screen: HTMLElement;
  terminal: Terminal;
  mode: TouchMode;
  longPressTimer: number | null;
  startX: number;
  startY: number;
  lastY: number;
  startTime: number;
  lastMoveTime: number;
  kineticFrame: number | null;
  kineticLastFrameTime: number;
  velocityY: number;
  scrollAccumulator: number;
  cellHeight: number;
  handlers: {
    touchstart: (e: TouchEvent) => void;
    touchmove: (e: TouchEvent) => void;
    touchend: (e: TouchEvent) => void;
    touchcancel: (e: TouchEvent) => void;
  };
  documentTouchEnd: ((e: TouchEvent) => void) | null;
}

const states = new Map<string, TouchScrollState>();

export function initTouchScrolling(
  sessionId: string,
  terminal: Terminal,
  container: HTMLDivElement,
): void {
  if (!isTouchDevice() || hasPrecisePointer()) return;

  const viewport = container.querySelector<HTMLElement>('.xterm-viewport');
  const screen = container.querySelector<HTMLElement>('.xterm-screen');
  if (!viewport || !screen) return;

  const overlay = document.createElement('div');
  overlay.style.cssText = `
    position: absolute;
    inset: 0;
    z-index: 20;
    touch-action: none;
    background: transparent;
  `;
  container.appendChild(overlay);

  const touchState: TouchScrollState = {
    overlay,
    viewport,
    screen,
    terminal,
    mode: 'idle',
    longPressTimer: null,
    startX: 0,
    startY: 0,
    lastY: 0,
    startTime: 0,
    lastMoveTime: 0,
    kineticFrame: null,
    kineticLastFrameTime: 0,
    velocityY: 0,
    scrollAccumulator: 0,
    cellHeight: 0,
    handlers: {
      touchstart: (e) => {
        handleTouchStart(sessionId, e);
      },
      touchmove: (e) => {
        handleTouchMove(sessionId, e);
      },
      touchend: (e) => {
        handleTouchEnd(sessionId, e);
      },
      touchcancel: (e) => {
        handleTouchCancel(sessionId, e);
      },
    },
    documentTouchEnd: null,
  };

  overlay.addEventListener('touchstart', touchState.handlers.touchstart, { passive: false });
  overlay.addEventListener('touchmove', touchState.handlers.touchmove, { passive: false });
  overlay.addEventListener('touchend', touchState.handlers.touchend, { passive: false });
  overlay.addEventListener('touchcancel', touchState.handlers.touchcancel, { passive: true });

  states.set(sessionId, touchState);
}

export function teardownTouchScrolling(sessionId: string): void {
  const s = states.get(sessionId);
  if (!s) return;

  cancelLongPress(s);
  cancelKineticScroll(s);
  removeDocumentListener(s);

  s.overlay.removeEventListener('touchstart', s.handlers.touchstart);
  s.overlay.removeEventListener('touchmove', s.handlers.touchmove);
  s.overlay.removeEventListener('touchend', s.handlers.touchend);
  s.overlay.removeEventListener('touchcancel', s.handlers.touchcancel);
  s.overlay.remove();

  states.delete(sessionId);
}

export function isTouchSelecting(sessionId: string): boolean {
  const s = states.get(sessionId);
  return s?.mode === 'selecting';
}

function handleTouchStart(sessionId: string, e: TouchEvent): void {
  const s = states.get(sessionId);
  if (!s) return;

  // Only handle single-finger touches; multi-touch goes to gesture system
  if (e.touches.length !== 1) return;

  // Stop propagation to prevent xterm.js v6's document-level gesture system
  // from intercepting this touch and converting scroll into cursor key sequences
  e.stopPropagation();

  const touch = e.touches[0];
  if (!touch) return;

  s.mode = 'pending';
  cancelKineticScroll(s);
  s.startX = touch.clientX;
  s.startY = touch.clientY;
  s.lastY = touch.clientY;
  s.startTime = Date.now();
  s.lastMoveTime = Date.now();
  s.velocityY = 0;
  s.scrollAccumulator = 0;

  // Account for CSS transform scaling (terminal may be scaled down to fit viewport)
  const xterm = s.overlay.parentElement?.querySelector('.xterm') as HTMLElement | null;
  const transform = xterm?.style.transform ?? '';
  const scaleMatch = transform.match(/scale\(([^)]+)\)/);
  const scale = scaleMatch?.[1] ? parseFloat(scaleMatch[1]) : 1;
  const naturalCellHeight = s.terminal.rows > 0 ? s.viewport.clientHeight / s.terminal.rows : 0;
  s.cellHeight = naturalCellHeight * scale;

  s.longPressTimer = window.setTimeout(() => {
    enterSelectionMode(s, touch.clientX, touch.clientY);
  }, LONG_PRESS_MS);
}

function handleTouchMove(sessionId: string, e: TouchEvent): void {
  const s = states.get(sessionId);
  if (!s || e.touches.length !== 1) return;

  e.stopPropagation();

  const touch = e.touches[0];
  if (!touch) return;
  const dx = touch.clientX - s.startX;
  const dy = touch.clientY - s.startY;

  if (s.mode === 'pending') {
    const absDx = Math.abs(dx);
    const absDy = Math.abs(dy);

    if (absDy > MOVE_THRESHOLD) {
      // Vertical movement dominant — enter scroll mode
      cancelLongPress(s);
      s.mode = 'scrolling';
      e.preventDefault();
    } else if (absDx > MOVE_THRESHOLD && absDx > absDy * 1.5) {
      // Horizontal movement dominant — track for swipe detection
      cancelLongPress(s);
      s.mode = 'horizontal';
      return;
    }
  }

  if (s.mode === 'scrolling') {
    e.preventDefault();
    const deltaY = s.lastY - touch.clientY;
    const now = Date.now();
    const dt = Math.max(1, now - s.lastMoveTime);
    const instantVelocity = deltaY / dt;
    s.velocityY =
      s.velocityY === 0
        ? instantVelocity
        : s.velocityY * (1 - VELOCITY_SAMPLE_SMOOTHING) +
          instantVelocity * VELOCITY_SAMPLE_SMOOTHING;
    s.lastY = touch.clientY;
    s.lastMoveTime = now;
    scrollViewport(s, deltaY);
  }
}

function handleTouchEnd(sessionId: string, e: TouchEvent): void {
  const s = states.get(sessionId);
  if (!s) return;

  e.stopPropagation();

  const mode = s.mode;
  cancelLongPress(s);

  if (mode === 'pending') {
    // Quick tap — focus terminal and dispatch click for TUI support
    const touch = e.changedTouches[0];
    if (touch) {
      const duration = Date.now() - s.startTime;
      if (duration < TAP_MAX_DURATION) {
        e.preventDefault();
        s.terminal.focus();
        dispatchSyntheticClick(s, touch.clientX, touch.clientY);
      }
    }
    s.mode = 'idle';
  } else if (mode === 'scrolling') {
    e.preventDefault();
    if (isMobileKineticTerminalScrollEnabled()) {
      startKineticScroll(s);
    } else {
      cancelKineticScroll(s);
    }
    s.mode = 'idle';
  } else if (mode === 'horizontal') {
    // Check for horizontal swipe (Ctrl+A / Ctrl+E)
    const touch = e.changedTouches[0];
    if (touch) {
      const dx = touch.clientX - s.startX;
      const dy = touch.clientY - s.startY;
      const dt = Date.now() - s.startTime;
      if (
        Math.abs(dx) >= SWIPE_THRESHOLD &&
        Math.abs(dy) <= SWIPE_MAX_VERTICAL &&
        dt <= SWIPE_MAX_TIME
      ) {
        e.preventDefault();
        sendInput(sessionId, dx > 0 ? '\x05' : '\x01');
      }
    }
    s.mode = 'idle';
  }
  // 'selecting' mode is handled by the document touchend listener
}

function handleTouchCancel(sessionId: string, e: TouchEvent): void {
  const s = states.get(sessionId);
  if (!s) return;
  e.stopPropagation();
  cancelLongPress(s);
  cancelKineticScroll(s);
  s.mode = 'idle';
}

function isMobileKineticTerminalScrollEnabled(): boolean {
  return $currentSettings.get()?.mobileKineticTerminalScroll !== false;
}

function enterSelectionMode(s: TouchScrollState, clientX: number, clientY: number): void {
  s.mode = 'selecting';
  s.longPressTimer = null;

  // Haptic feedback
  navigator.vibrate(30);

  // Hide overlay so touches reach xterm for selection
  s.overlay.style.pointerEvents = 'none';

  // Dispatch synthetic mousedown to xterm screen to start selection
  const mousedown = new MouseEvent('mousedown', {
    bubbles: true,
    cancelable: true,
    clientX,
    clientY,
    button: 0,
  });
  s.screen.dispatchEvent(mousedown);

  // Re-enable overlay when the selection gesture ends (touchend or touchcancel)
  const restoreOverlay = (): void => {
    s.overlay.style.pointerEvents = '';
    s.mode = 'idle';
    document.removeEventListener('touchend', restoreOverlay, { capture: true });
    document.removeEventListener('touchcancel', restoreOverlay, { capture: true });
    s.documentTouchEnd = null;
  };
  s.documentTouchEnd = restoreOverlay;
  document.addEventListener('touchend', restoreOverlay, { once: true, capture: true });
  document.addEventListener('touchcancel', restoreOverlay, { once: true, capture: true });
}

export function scrollViewport(s: TouchScrollState, deltaY: number): void {
  panMobileStableTerminalShellScroll(s, deltaY);
  if (Math.abs(deltaY) < 0.5) return;
  if (s.cellHeight <= 0) return;
  s.scrollAccumulator += deltaY / s.cellHeight;
  const lines = Math.trunc(s.scrollAccumulator);
  if (lines !== 0) {
    s.terminal.scrollLines(lines);
    s.scrollAccumulator -= lines;
  }
}

export function computeKineticScrollStep(
  velocityY: number,
  elapsedMs: number,
): { deltaY: number; nextVelocityY: number; active: boolean } {
  const frameMs = Math.max(0, Math.min(KINETIC_MAX_FRAME_MS, elapsedMs));
  const clampedVelocity =
    Math.sign(velocityY) * Math.min(Math.abs(velocityY), KINETIC_MAX_VELOCITY_PX_PER_MS);
  if (frameMs <= 0 || Math.abs(clampedVelocity) < KINETIC_MIN_VELOCITY_PX_PER_MS) {
    return { deltaY: 0, nextVelocityY: 0, active: false };
  }

  const direction = clampedVelocity > 0 ? 1 : -1;
  const deceleration = KINETIC_FRICTION_PX_PER_MS2 * frameMs;
  const nextSpeed = Math.max(0, Math.abs(clampedVelocity) - deceleration);
  const averageVelocity = direction * (Math.abs(clampedVelocity) + nextSpeed) * 0.5;
  return {
    deltaY: averageVelocity * frameMs,
    nextVelocityY: nextSpeed * direction,
    active: nextSpeed >= KINETIC_MIN_VELOCITY_PX_PER_MS,
  };
}

function startKineticScroll(s: TouchScrollState): void {
  cancelKineticScroll(s);

  const clampedVelocity =
    Math.sign(s.velocityY) * Math.min(Math.abs(s.velocityY), KINETIC_MAX_VELOCITY_PX_PER_MS);
  if (Math.abs(clampedVelocity) < KINETIC_MIN_VELOCITY_PX_PER_MS) {
    s.velocityY = 0;
    return;
  }

  s.velocityY = clampedVelocity;
  s.kineticLastFrameTime = performance.now();

  const step = (timestamp: number): void => {
    const elapsedMs = timestamp - s.kineticLastFrameTime;
    s.kineticLastFrameTime = timestamp;
    const next = computeKineticScrollStep(s.velocityY, elapsedMs);
    if (next.deltaY !== 0) {
      scrollViewport(s, next.deltaY);
    }
    s.velocityY = next.nextVelocityY;
    if (next.active) {
      s.kineticFrame = requestAnimationFrame(step);
    } else {
      s.kineticFrame = null;
      s.velocityY = 0;
    }
  };

  s.kineticFrame = requestAnimationFrame(step);
}

export function panMobileStableTerminalShellScroll(
  s: Pick<TouchScrollState, 'overlay'>,
  deltaY: number,
): number {
  const container = s.overlay.parentElement;
  if (!container?.classList.contains('mobile-terminal-vertical-stable')) {
    return 0;
  }

  const maxScrollTop = Math.max(0, container.scrollHeight - container.clientHeight);
  if (maxScrollTop <= 0) {
    return 0;
  }

  const previousScrollTop = container.scrollTop;
  const nextScrollTop = Math.max(0, Math.min(maxScrollTop, previousScrollTop + deltaY));
  container.scrollTop = nextScrollTop;
  return container.scrollTop - previousScrollTop;
}

function cancelLongPress(s: TouchScrollState): void {
  if (s.longPressTimer !== null) {
    window.clearTimeout(s.longPressTimer);
    s.longPressTimer = null;
  }
}

function cancelKineticScroll(s: TouchScrollState): void {
  if (s.kineticFrame !== null) {
    cancelAnimationFrame(s.kineticFrame);
    s.kineticFrame = null;
  }
  s.velocityY = 0;
}

function removeDocumentListener(s: TouchScrollState): void {
  if (s.documentTouchEnd) {
    document.removeEventListener('touchend', s.documentTouchEnd, { capture: true });
    document.removeEventListener('touchcancel', s.documentTouchEnd, { capture: true });
    s.documentTouchEnd = null;
  }
}

function dispatchSyntheticClick(s: TouchScrollState, clientX: number, clientY: number): void {
  const opts: MouseEventInit = {
    bubbles: true,
    cancelable: true,
    clientX,
    clientY,
    button: 0,
  };

  // Briefly let events through to xterm
  s.overlay.style.pointerEvents = 'none';
  s.screen.dispatchEvent(new MouseEvent('mousedown', opts));
  s.screen.dispatchEvent(new MouseEvent('mouseup', opts));
  // Restore overlay after a microtask so xterm processes the events
  queueMicrotask(() => {
    s.overlay.style.pointerEvents = '';
  });
}
