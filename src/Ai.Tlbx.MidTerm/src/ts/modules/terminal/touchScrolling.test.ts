import { describe, expect, it, vi } from 'vitest';

vi.mock('../touchController/detection', () => ({
  hasPrecisePointer: () => false,
  isTouchDevice: () => true,
}));

vi.mock('../comms/muxChannel', () => ({
  sendInput: vi.fn(),
}));

import {
  computeKineticScrollStep,
  panMobileStableTerminalShellScroll,
  scrollViewport,
} from './touchScrolling';

function createShell(scrollTop: number, scrollHeight: number, clientHeight: number) {
  const classes = new Set(['mobile-terminal-vertical-stable']);
  const container = {
    scrollTop,
    scrollHeight,
    clientHeight,
    dataset: { mobileCursorFollowing: 'true' },
    classList: {
      contains: (name: string) => classes.has(name),
    },
  };

  return {
    container,
    state: {
      overlay: {
        parentElement: container,
      },
    },
  };
}

describe('mobile terminal touch scrolling', () => {
  it('pans the stable terminal shell without consuming xterm scrollback movement', () => {
    const { container, state } = createShell(20, 220, 100);

    const panned = panMobileStableTerminalShellScroll(state as never, 50);

    expect(container.scrollTop).toBe(70);
    expect(container.dataset.mobileCursorFollowing).toBe('false');
    expect(panned).toBe(50);
  });

  it('reports only the shell pan at the shell edge while xterm keeps the full drag delta', () => {
    const { container, state } = createShell(105, 220, 100);

    const panned = panMobileStableTerminalShellScroll(state as never, 50);

    expect(container.scrollTop).toBe(120);
    expect(panned).toBe(15);
  });

  it('passes the full drag delta through to xterm even when shell panning absorbs it', () => {
    const { container, state } = createShell(20, 220, 100);
    const terminal = { scrollLines: vi.fn() };
    const scrollState = {
      ...state,
      terminal,
      cellHeight: 10,
      scrollAccumulator: 0,
    };

    scrollViewport(scrollState as never, 50);

    expect(container.scrollTop).toBe(70);
    expect(terminal.scrollLines).toHaveBeenCalledWith(5);
  });

  it('continues a fast touch drag with a decaying kinetic scroll step', () => {
    const first = computeKineticScrollStep(1, 16);
    const second = computeKineticScrollStep(first.nextVelocityY, 16);

    expect(first.active).toBe(true);
    expect(first.deltaY).toBeGreaterThan(0);
    expect(first.nextVelocityY).toBeLessThan(1);
    expect(second.deltaY).toBeGreaterThan(0);
    expect(second.nextVelocityY).toBeLessThan(first.nextVelocityY);
  });

  it('carries a fling far enough to make terminal history reachable', () => {
    let velocity = 1;
    let distance = 0;
    let active = true;
    let frames = 0;

    while (active && frames < 500) {
      const step = computeKineticScrollStep(velocity, 16);
      distance += step.deltaY;
      velocity = step.nextVelocityY;
      active = step.active;
      frames++;
    }

    expect(distance).toBeGreaterThan(360);
    expect(frames).toBeGreaterThan(40);
  });

  it('clamps very fast flings to a bounded but long travel distance', () => {
    const step = computeKineticScrollStep(40, 16);

    expect(step.deltaY).toBeLessThan(130);
    expect(step.nextVelocityY).toBeLessThan(8);
  });

  it('stops kinetic scrolling below the velocity threshold', () => {
    const step = computeKineticScrollStep(0.01, 16);

    expect(step.active).toBe(false);
    expect(step.deltaY).toBe(0);
    expect(step.nextVelocityY).toBe(0);
  });
});
