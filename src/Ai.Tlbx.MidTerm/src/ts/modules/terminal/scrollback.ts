import type { TerminalState } from '../../types';

export function isTerminalViewingScrollback(state: Pick<TerminalState, 'terminal'>): boolean {
  const buffer = state.terminal.buffer.active;
  return buffer.viewportY < buffer.baseY;
}

/** Refit after leaving history, outside xterm's synchronous scroll/resize stack. */
export function onTerminalScrollbackExit(
  state: Pick<TerminalState, 'terminal'>,
  refit: () => void,
): { dispose: () => void } {
  let wasViewingScrollback = isTerminalViewingScrollback(state);
  let frame: number | null = null;
  const subscription = state.terminal.onScroll(() => {
    const viewingScrollback = isTerminalViewingScrollback(state);
    if (wasViewingScrollback && !viewingScrollback && frame === null) {
      frame = requestAnimationFrame(() => {
        frame = null;
        if (!isTerminalViewingScrollback(state)) refit();
      });
    }
    wasViewingScrollback = viewingScrollback;
  });
  return {
    dispose: () => {
      subscription.dispose();
      if (frame !== null) cancelAnimationFrame(frame);
      frame = null;
    },
  };
}
