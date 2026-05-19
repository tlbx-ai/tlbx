/**
 * Cursor Visibility Helpers
 *
 * Tracks and optionally suppresses DECTCEM cursor visibility control sequences.
 */

import type { TerminalState } from '../../types';
import { $currentSettings } from '../../stores';

export interface CursorVisibilityControlResult {
  data: Uint8Array;
  remoteCursorVisible: boolean | null;
  hadCursorVisibilityControl: boolean;
}

const CURSOR_BURST_WINDOW_MS = 180;
const CURSOR_BURST_MIN_BYTES = 12;
const CURSOR_IDLE_SHOW_MS = 650;
const CURSOR_LOCAL_INPUT_GRACE_MS = 250;
const SHOW_CURSOR_SEQ = '\x1b[?25h';
const HIDE_CURSOR_SEQ = '\x1b[?25l';

type CursorControlSettings = {
  preserveTerminalCursorControl?: boolean;
};

interface CursorVisibilityMatch {
  visible: boolean;
  endExclusive: number;
}

function isCursorVisibilityFinalByte(value: number | undefined): value is 0x68 | 0x6c {
  return value === 0x68 || value === 0x6c;
}

function matchCursorVisibilitySequence(
  data: Uint8Array,
  index: number,
  prefix: readonly number[],
): CursorVisibilityMatch | null {
  const finalIndex = index + prefix.length;
  if (finalIndex >= data.length) {
    return null;
  }

  for (let offset = 0; offset < prefix.length; offset += 1) {
    if (data[index + offset] !== prefix[offset]) {
      return null;
    }
  }

  const final = data[finalIndex];
  if (!isCursorVisibilityFinalByte(final)) {
    return null;
  }

  return {
    visible: final === 0x68,
    endExclusive: finalIndex + 1,
  };
}

function tryMatchCursorVisibilityControl(
  data: Uint8Array,
  index: number,
): CursorVisibilityMatch | null {
  const prefixes = [
    [0x1b, 0x5b, 0x3f, 0x32, 0x35],
    [0x9b, 0x3f, 0x32, 0x35],
    [0xc2, 0x9b, 0x3f, 0x32, 0x35],
  ] as const;

  for (const prefix of prefixes) {
    const match = matchCursorVisibilitySequence(data, index, prefix);
    if (match) {
      return match;
    }
  }

  return null;
}

export function shouldPreserveTerminalCursorControl(): boolean {
  const settings = $currentSettings.get() as CursorControlSettings | null;
  return settings?.preserveTerminalCursorControl !== false;
}

export function processCursorVisibilityControls(
  data: Uint8Array,
  suppress: boolean,
): CursorVisibilityControlResult {
  let remoteCursorVisible: boolean | null = null;
  let hadCursorVisibilityControl = false;
  let filtered: number[] | null = null;
  let copyStart = 0;

  for (let i = 0; i < data.length; i++) {
    const match = tryMatchCursorVisibilityControl(data, i);
    if (match === null) {
      continue;
    }

    hadCursorVisibilityControl = true;
    remoteCursorVisible = match.visible;

    if (suppress) {
      filtered ??= [];
      for (let j = copyStart; j < i; j++) {
        filtered.push(data[j] as number);
      }
      copyStart = match.endExclusive;
    }

    i = match.endExclusive - 1;
  }

  if (!suppress || !hadCursorVisibilityControl || filtered === null) {
    return {
      data: data,
      remoteCursorVisible: remoteCursorVisible,
      hadCursorVisibilityControl: hadCursorVisibilityControl,
    };
  }

  for (let i = copyStart; i < data.length; i++) {
    filtered.push(data[i] as number);
  }

  return {
    data: Uint8Array.from(filtered),
    remoteCursorVisible: remoteCursorVisible,
    hadCursorVisibilityControl: true,
  };
}

function containsImmediateHideTerminalControl(data: Uint8Array): boolean {
  for (let i = 0; i < data.length; i++) {
    const byte = data[i];
    if (
      byte === 0x1b ||
      byte === 0x90 ||
      byte === 0x9b ||
      byte === 0x9d ||
      byte === 0x9e ||
      byte === 0x9f
    ) {
      return true;
    }

    if (byte === 0xc2 && i + 1 < data.length) {
      const next = data[i + 1];
      if (next === 0x90 || next === 0x9b || next === 0x9d || next === 0x9e || next === 0x9f) {
        return true;
      }
    }
  }

  return false;
}

function clearBurstCursorRestoreSchedule(state: TerminalState): void {
  if (state.burstCursorRestoreTimer != null) {
    clearTimeout(state.burstCursorRestoreTimer);
    state.burstCursorRestoreTimer = null;
  }

  state.burstCursorRestoreDueAtMs = null;
}

function armBurstCursorRestoreTimer(state: TerminalState): void {
  const dueAt = state.burstCursorRestoreDueAtMs;
  if (dueAt == null) {
    state.burstCursorRestoreTimer = null;
    return;
  }

  const delayMs = Math.max(0, dueAt - performance.now());
  state.burstCursorRestoreTimer = window.setTimeout(() => {
    state.burstCursorRestoreTimer = null;

    const currentDueAt = state.burstCursorRestoreDueAtMs;
    if (currentDueAt != null && currentDueAt - performance.now() > 1) {
      armBurstCursorRestoreTimer(state);
      return;
    }

    state.burstCursorRestoreDueAtMs = null;
    showBurstCursor(state);
  }, delayMs);
}

export function hideBurstCursor(state: TerminalState): void {
  if (shouldPreserveTerminalCursorControl()) {
    state.burstCursorHidden = false;
    clearBurstCursorRestoreSchedule(state);
    return;
  }

  if (!state.burstCursorHidden) {
    if (!state.syncOutputCursorHidden) {
      state.terminal.write(HIDE_CURSOR_SEQ);
    }
    state.burstCursorHidden = true;
  }

  clearBurstCursorRestoreSchedule(state);
}

export function showBurstCursor(state: TerminalState): void {
  if (shouldPreserveTerminalCursorControl()) {
    state.burstCursorHidden = false;
    clearBurstCursorRestoreSchedule(state);
    return;
  }

  if (state.remoteCursorVisible === false || state.syncOutputCursorHidden === true) {
    return;
  }

  clearBurstCursorRestoreSchedule(state);

  if (state.burstCursorHidden) {
    state.burstCursorHidden = false;
    state.terminal.write(SHOW_CURSOR_SEQ);
  }
}

export function scheduleBurstCursorShow(state: TerminalState): void {
  if (shouldPreserveTerminalCursorControl()) {
    state.burstCursorHidden = false;
    clearBurstCursorRestoreSchedule(state);
    return;
  }

  if (state.remoteCursorVisible === false || state.syncOutputCursorHidden === true) {
    return;
  }

  state.burstCursorRestoreDueAtMs = performance.now() + CURSOR_IDLE_SHOW_MS;
  if (state.burstCursorRestoreTimer == null) {
    armBurstCursorRestoreTimer(state);
  }
}

export function shouldHideCursorForOutput(state: TerminalState, data: Uint8Array): boolean {
  if (shouldPreserveTerminalCursorControl()) {
    state.lastBurstOutputAtMs = performance.now();
    return false;
  }

  if (data.length <= 0) {
    return false;
  }

  const now = performance.now();
  const lastLocalInputAtMs = state.lastLocalInputAtMs ?? null;
  if (lastLocalInputAtMs !== null && now - lastLocalInputAtMs <= CURSOR_LOCAL_INPUT_GRACE_MS) {
    return false;
  }

  if (containsImmediateHideTerminalControl(data) || state.burstCursorHidden) {
    return true;
  }

  const last = state.lastBurstOutputAtMs ?? 0;
  state.lastBurstOutputAtMs = now;

  return (
    data.length >= CURSOR_BURST_MIN_BYTES || (last > 0 && now - last <= CURSOR_BURST_WINDOW_MS)
  );
}

export function hideSynchronizedOutputCursor(state: TerminalState): void {
  if (shouldPreserveTerminalCursorControl()) {
    state.syncOutputCursorHidden = false;
    return;
  }

  if (state.syncOutputCursorHidden) {
    return;
  }

  state.syncOutputCursorHidden = true;
  state.terminal.write(HIDE_CURSOR_SEQ);
}

export function showSynchronizedOutputCursor(state: TerminalState): void {
  if (shouldPreserveTerminalCursorControl()) {
    state.syncOutputCursorHidden = false;
    return;
  }

  if (!state.syncOutputCursorHidden) {
    return;
  }

  state.syncOutputCursorHidden = false;
  if (!state.burstCursorHidden && state.remoteCursorVisible !== false) {
    state.terminal.write(SHOW_CURSOR_SEQ);
  }
}

export function reconcileSynchronizedOutputCursorState(state: TerminalState): void {
  if (shouldPreserveTerminalCursorControl()) {
    state.syncOutputCursorHidden = false;
    return;
  }

  if (state.terminal.modes.synchronizedOutputMode) {
    hideSynchronizedOutputCursor(state);
  } else {
    showSynchronizedOutputCursor(state);
  }
}
