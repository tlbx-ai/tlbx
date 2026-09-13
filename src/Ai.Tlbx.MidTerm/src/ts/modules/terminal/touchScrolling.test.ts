import { afterEach, describe, expect, it, vi } from 'vitest';

const { settings } = vi.hoisted(() => ({
  settings: vi.fn(() => ({ mobileKineticTerminalScroll: true })),
}));
vi.mock('../../stores', () => ({ $currentSettings: { get: settings } }));

vi.mock('../touchController/detection', () => ({
  hasPrecisePointer: () => false,
  isTouchDevice: () => true,
}));

vi.mock('../comms/muxChannel', () => ({
  sendInput: vi.fn(),
}));

import {
  initTouchScrolling,
  teardownTouchScrolling,
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

describe('touch release momentum lifecycle', () => {
  afterEach(() => {
    teardownTouchScrolling('fling');
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    settings.mockReturnValue({ mobileKineticTerminalScroll: true });
  });

  function gesture() {
    let now = 1000;
    let frameId = 0;
    const frames = new Map<number, FrameRequestCallback>();
    const listeners = new Map<string, (event: TouchEvent) => void>();
    const overlay = {
      style: {},
      parentElement: null,
      addEventListener: (type: string, handler: (event: TouchEvent) => void) =>
        listeners.set(type, handler),
      removeEventListener: (type: string) => listeners.delete(type),
      remove: vi.fn(),
    };
    vi.stubGlobal('document', { createElement: () => overlay });
    vi.stubGlobal('window', { setTimeout: () => 1, clearTimeout: vi.fn() });
    vi.spyOn(Date, 'now').mockImplementation(() => now);
    vi.spyOn(performance, 'now').mockImplementation(() => now);
    vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => {
      frames.set(++frameId, callback);
      return frameId;
    });
    vi.stubGlobal('cancelAnimationFrame', (id: number) => frames.delete(id));
    const terminal = { rows: 10, scrollLines: vi.fn(), focus: vi.fn() };
    const container = { querySelector: () => ({ clientHeight: 200 }), appendChild: vi.fn() };
    initTouchScrolling('fling', terminal as never, container as never);
    const touch = (type: string, y: number) => {
      now += 16;
      const point = { clientX: 50, clientY: y };
      listeners.get(type)?.({
        touches: type === 'touchend' ? [] : [point],
        changedTouches: [point],
        preventDefault: vi.fn(),
        stopPropagation: vi.fn(),
      } as unknown as TouchEvent);
    };
    const frame = () => {
      now += 16;
      const pending = [...frames.values()];
      frames.clear();
      pending.forEach((callback) => callback(now));
    };
    return { touch, frame, frames, terminal };
  }

  it.each([1, -1])(
    'keeps scrolling after release in direction %s, then comes to rest',
    (direction) => {
      const g = gesture();
      g.touch('touchstart', 200);
      g.touch('touchmove', 200 - direction * 80);
      g.touch('touchend', 200 - direction * 80);
      g.terminal.scrollLines.mockClear();
      expect(g.frames.size).toBe(1);
      g.frame();
      expect(g.terminal.scrollLines).toHaveBeenCalled();
      expect(Math.sign(g.terminal.scrollLines.mock.calls[0]![0])).toBe(direction);
      for (let i = 0; i < 500 && g.frames.size; i++) g.frame();
      expect(g.frames.size).toBe(0);
    },
  );

  it.each(['touchstart', 'touchcancel', 'teardown'])('stops momentum on %s', (action) => {
    const g = gesture();
    g.touch('touchstart', 200);
    g.touch('touchmove', 120);
    g.touch('touchend', 120);
    expect(g.frames.size).toBe(1);
    if (action === 'teardown') teardownTouchScrolling('fling');
    else g.touch(action, 120);
    g.terminal.scrollLines.mockClear();
    g.frame();
    expect(g.frames.size).toBe(0);
    expect(g.terminal.scrollLines).not.toHaveBeenCalled();
  });

  it('respects the disabled momentum setting while retaining drag scrolling', () => {
    settings.mockReturnValue({ mobileKineticTerminalScroll: false });
    const g = gesture();
    g.touch('touchstart', 200);
    g.touch('touchmove', 120);
    expect(g.terminal.scrollLines).toHaveBeenCalled();
    g.touch('touchend', 120);
    expect(g.frames.size).toBe(0);
  });
});
