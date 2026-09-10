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
type CursorControlSettings = {
  preserveTerminalCursorControl?: boolean;
};

interface CursorVisibilityMatch {
  visible: boolean;
  endExclusive: number;
}

function tryMatchCursorVisibilityControl(
  data: Uint8Array,
  index: number,
): CursorVisibilityMatch | null {
  const first = data[index];
  let parameter: number;
  if (first === 0x1b && data[index + 1] === 0x5b) {
    parameter = index + 2;
  } else if (first === 0x9b) {
    parameter = index + 1;
  } else if (first === 0xc2 && data[index + 1] === 0x9b) {
    parameter = index + 2;
  } else {
    return null;
  }
  if (data[parameter] !== 0x3f || data[parameter + 1] !== 0x32 || data[parameter + 2] !== 0x35) {
    return null;
  }
  const final = data[parameter + 3];
  return final === 0x68 || final === 0x6c
    ? { visible: final === 0x68, endExclusive: parameter + 4 }
    : null;
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
  let filtered: Uint8Array | null = null;
  let written = 0;
  let copyStart = 0;

  for (let i = 0; i < data.length; i++) {
    const byte = data[i];
    if (byte !== 0x1b && byte !== 0x9b && byte !== 0xc2) continue;
    const match = tryMatchCursorVisibilityControl(data, i);
    if (match === null) {
      continue;
    }

    hadCursorVisibilityControl = true;
    remoteCursorVisible = match.visible;

    if (suppress) {
      filtered ??= new Uint8Array(data.length);
      filtered.set(data.subarray(copyStart, i), written);
      written += i - copyStart;
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

  filtered.set(data.subarray(copyStart), written);
  written += data.length - copyStart;

  return {
    data: filtered.subarray(0, written),
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

  // Never inject a separate VT sequence into xterm's write stream. A native
  // output batch may end midway through CSI/OSC/DCS and xterm deliberately
  // carries that parser state into the next write. An out-of-band DECTCEM
  // write here would then become part of the unfinished application sequence.
  state.burstCursorHidden = true;

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

  state.burstCursorHidden = false;
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

  // This is intentionally state-only. terminal.write() is not a safe side
  // channel for synthetic controls because the app's preceding write can leave
  // xterm inside an unfinished terminal sequence.
  state.syncOutputCursorHidden = true;
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
