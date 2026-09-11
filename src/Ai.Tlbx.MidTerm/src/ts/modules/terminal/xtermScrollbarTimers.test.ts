import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SmoothScrollableElement } from '@xterm/xterm/src/browser/scrollable/scrollableElement';
import { TimeoutTimer } from '@xterm/xterm/src/common/Async';

interface ScrollbarHarness {
  _hideTimeout: TimeoutTimer;
  _hideAt: number;
  _mouseIsOver: boolean;
  _isDragging: boolean;
  _hide: ReturnType<typeof vi.fn>;
  _scheduleHide(): void;
}

describe('patched xterm scrollbar hide timer', () => {
  let scrollbar: ScrollbarHarness;

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(0);
    scrollbar = Object.assign(Object.create(SmoothScrollableElement.prototype), {
      _hideTimeout: new TimeoutTimer(),
      _hideAt: 0,
      _mouseIsOver: false,
      _isDragging: false,
      _hide: vi.fn(),
    }) as ScrollbarHarness;
  });

  afterEach(() => {
    scrollbar._hideTimeout.dispose();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('keeps one timer during output and hides 500 ms after the last scroll', () => {
    const setTimer = vi.spyOn(globalThis, 'setTimeout');
    const clearTimer = vi.spyOn(globalThis, 'clearTimeout');
    for (let i = 0; i < 10000; i++) scrollbar._scheduleHide();
    expect(setTimer).toHaveBeenCalledTimes(1);
    expect(clearTimer).not.toHaveBeenCalled();
    vi.advanceTimersByTime(400);
    scrollbar._scheduleHide();
    vi.advanceTimersByTime(499);
    expect(scrollbar._hide).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(1);
    vi.advanceTimersByTime(1);
    expect(scrollbar._hide).toHaveBeenCalledTimes(1);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('does not schedule while hovered or dragging and cancels on disposal', () => {
    scrollbar._mouseIsOver = true;
    scrollbar._scheduleHide();
    scrollbar._mouseIsOver = false;
    scrollbar._isDragging = true;
    scrollbar._scheduleHide();
    expect(vi.getTimerCount()).toBe(0);
    scrollbar._isDragging = false;
    scrollbar._scheduleHide();
    scrollbar._hideTimeout.dispose();
    vi.advanceTimersByTime(1000);
    expect(scrollbar._hide).not.toHaveBeenCalled();
  });
});
