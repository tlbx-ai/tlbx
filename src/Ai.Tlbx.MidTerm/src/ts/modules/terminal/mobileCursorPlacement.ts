import type { Terminal } from '@xterm/xterm';

interface Cell {
  x: number;
  y: number;
}

interface Request {
  target: Cell;
  click: () => void;
}

const cancellations = new Map<string, () => void>();

export function cancelMobileCursorPlacement(sessionId: string): void {
  cancellations.get(sessionId)?.();
}

/** Reconcile xterm's full-width Alt+click estimate with the application's reply. */
export function createMobileCursorPlacement(
  sessionId: string,
  terminal: Terminal,
  send: (data: string) => void,
  isFocused: () => boolean,
) {
  let pending: Request | undefined;
  let queued: Request | undefined;
  let start: Cell;
  let corrections = 0;
  let sending = false;
  let settleTimer: ReturnType<typeof setTimeout> | undefined;
  let expiryTimer: ReturnType<typeof setTimeout> | undefined;
  let buffer = terminal.buffer.active;
  let cols = terminal.cols;
  let rows = terminal.rows;

  const cursor = (): Cell => ({ x: buffer.cursorX, y: buffer.baseY + buffer.cursorY });
  const same = (a: Cell, b: Cell): boolean => a.x === b.x && a.y === b.y;
  const cancel = (): void => {
    if (sending) return;
    clearTimeout(settleTimer);
    clearTimeout(expiryTimer);
    pending = queued = undefined;
  };
  const transmit = (action: () => void): void => {
    sending = true;
    try {
      action();
    } finally {
      sending = false;
    }
  };
  const begin = (request: Request): void => {
    cancel();
    buffer = terminal.buffer.active;
    cols = terminal.cols;
    rows = terminal.rows;
    start = cursor();
    if (same(start, request.target)) return;
    pending = request;
    corrections = 0;
    // If the application ignores cursor keys, abandon the attempt entirely.
    expiryTimer = setTimeout(cancel, 1000);
    transmit(request.click);
  };
  const settle = (): void => {
    if (!pending) return;
    if (
      !isFocused() ||
      terminal.buffer.active !== buffer ||
      terminal.cols !== cols ||
      terminal.rows !== rows ||
      terminal.modes.mouseTrackingMode !== 'none' ||
      buffer.viewportY !== buffer.baseY
    ) {
      cancel();
      return;
    }
    const actual = cursor();
    if (same(actual, start)) return;
    // A rapid second tap waits for the first reply rather than using its old origin.
    if (queued) {
      begin(queued);
      return;
    }
    const target = pending.target;
    if (same(actual, target) || actual.y !== target.y || corrections >= 2) {
      cancel();
      return;
    }
    // Correct only within the reached row. Never guess another vertical route.
    const distance = target.x - actual.x;
    const sequence = `\x1b${terminal.modes.applicationCursorKeysMode ? 'O' : '['}${distance > 0 ? 'C' : 'D'}`;
    start = actual;
    corrections++;
    transmit(() => {
      send(sequence.repeat(Math.min(Math.abs(distance), cols)));
    });
  };
  const parsed = terminal.onWriteParsed(() => {
    if (!pending) return;
    clearTimeout(settleTimer);
    // A TUI redraw can span multiple writes; wait for its cursor to settle.
    settleTimer = setTimeout(settle, 40);
  });
  cancellations.set(sessionId, cancel);
  return {
    request(target: Cell, click: () => void): void {
      if (pending) queued = { target, click };
      else begin({ target, click });
    },
    cancel,
    dispose(): void {
      cancel();
      parsed.dispose();
      if (cancellations.get(sessionId) === cancel) cancellations.delete(sessionId);
    },
  };
}
