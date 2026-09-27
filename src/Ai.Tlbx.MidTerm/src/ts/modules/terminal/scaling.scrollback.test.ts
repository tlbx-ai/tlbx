import { afterEach, describe, expect, it, vi } from 'vitest';
import type { TerminalState } from '../../types';
import { onTerminalScrollbackExit } from './scrollback';

function makeState(viewportY: number, baseY: number): Pick<TerminalState, 'terminal'> {
  return {
    terminal: {
      buffer: {
        active: {
          viewportY,
          baseY,
        },
      },
    },
  } as unknown as Pick<TerminalState, 'terminal'>;
}

describe('onTerminalScrollbackExit', () => {
  afterEach(() => vi.unstubAllGlobals());

  function setup() {
    const state = makeState(180, 180);
    let scroll = () => {};
    let frame: FrameRequestCallback | null = null;
    const dispose = vi.fn();
    state.terminal.onScroll = vi.fn((callback) => {
      scroll = () => callback(state.terminal.buffer.active.viewportY);
      return { dispose };
    });
    vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => {
      frame = callback;
      return 1;
    });
    vi.stubGlobal('cancelAnimationFrame', () => {
      frame = null;
    });
    const refit = vi.fn();
    const subscription = onTerminalScrollbackExit(state, refit);
    return {
      refit,
      dispose,
      subscription,
      scrollTo(viewportY: number) {
        Object.assign(state.terminal.buffer.active, { viewportY });
        scroll();
      },
      flush() {
        const callback = frame;
        frame = null;
        callback?.(0);
      },
    };
  }

  it('refits once on return to live output, not during reading or ordinary output', () => {
    const s = setup();
    s.scrollTo(180);
    s.scrollTo(100);
    s.scrollTo(120);
    s.flush();
    expect(s.refit).not.toHaveBeenCalled();
    s.scrollTo(180);
    s.scrollTo(180);
    expect(s.refit).not.toHaveBeenCalled();
    s.flush();
    expect(s.refit).toHaveBeenCalledTimes(1);
  });

  it('preserves history if the user scrolls back before the queued refit', () => {
    const s = setup();
    s.scrollTo(100);
    s.scrollTo(180);
    s.scrollTo(100);
    s.flush();
    expect(s.refit).not.toHaveBeenCalled();
  });

  it('cancels a queued refit when the terminal is disposed', () => {
    const s = setup();
    s.scrollTo(100);
    s.scrollTo(180);
    s.subscription.dispose();
    s.flush();
    expect(s.dispose).toHaveBeenCalledOnce();
    expect(s.refit).not.toHaveBeenCalled();
  });
});
