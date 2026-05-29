import { afterEach, describe, expect, it, vi } from 'vitest';
import { createPrintableInputBurstCoalescer } from './printableInputBurst';

describe('printable input burst coalescer', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it('does not coalesce when the configured window is zero', () => {
    const sendNow = vi.fn();
    const coalescer = createPrintableInputBurstCoalescer(() => 0, sendNow);

    expect(coalescer.enqueue('s1', 'a', 10)).toBe(false);

    expect(sendNow).not.toHaveBeenCalled();
  });

  it('sends the first printable input immediately and flushes the rest after the window', () => {
    vi.useFakeTimers();
    const sendNow = vi.fn();
    const coalescer = createPrintableInputBurstCoalescer(() => 60, sendNow);

    expect(coalescer.enqueue('s1', 'a', 10)).toBe(true);
    expect(coalescer.enqueue('s1', 'b', 20)).toBe(true);

    expect(sendNow).toHaveBeenCalledTimes(1);
    expect(sendNow).toHaveBeenCalledWith('s1', 'a', 10);

    vi.advanceTimersByTime(60);

    expect(sendNow).toHaveBeenCalledTimes(2);
    expect(sendNow).toHaveBeenLastCalledWith('s1', 'b', 10);
  });

  it('flushes buffered printable input before control input', () => {
    const sendNow = vi.fn();
    const coalescer = createPrintableInputBurstCoalescer(() => 60, sendNow);

    expect(coalescer.enqueue('s1', 'a', 10)).toBe(true);
    expect(coalescer.enqueue('s1', 'b', 20)).toBe(true);
    expect(coalescer.enqueue('s1', '\r', 30)).toBe(false);

    expect(sendNow).toHaveBeenCalledTimes(2);
    expect(sendNow).toHaveBeenNthCalledWith(1, 's1', 'a', 10);
    expect(sendNow).toHaveBeenNthCalledWith(2, 's1', 'b', 10);
  });
});
