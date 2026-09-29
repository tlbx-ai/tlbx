import { afterEach, describe, expect, it, vi } from 'vitest';
import { WriteBuffer } from '../../../../node_modules/@xterm/xterm/src/common/input/WriteBuffer';
import { Terminal } from '@xterm/xterm';

describe('patched xterm write buffer recovery barriers', () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('uses browser tasks for parser continuation and leaves disposed queues inert', async () => {
    const tasks: Array<() => void> = [];
    const postTask = vi.fn((run: () => void, _options: { priority: string }) => {
      tasks.push(run);
      return Promise.resolve();
    });
    vi.stubGlobal('scheduler', { postTask });
    const parsed = vi.fn();
    const buffer = new WriteBuffer(parsed);
    buffer.write('first');
    buffer.write('second');
    expect(postTask).toHaveBeenCalledTimes(1);
    expect(postTask.mock.calls[0]?.[1]).toEqual({ priority: 'user-visible' });
    tasks.shift()?.();
    expect(parsed.mock.calls.map(([data]) => data)).toEqual(['first', 'second']);
    buffer.write('retired');
    buffer.dispose();
    tasks.shift()?.();
    expect(parsed).toHaveBeenCalledTimes(2);
  });

  it('falls back to timers when task scheduling rejects before parsing', async () => {
    vi.useFakeTimers();
    vi.stubGlobal('scheduler', {
      postTask: () => Promise.reject(new Error('scheduler unavailable')),
    });
    const parsed = vi.fn();
    const buffer = new WriteBuffer(parsed);
    try {
      buffer.write('retained');
      await Promise.resolve();
      vi.runAllTimers();
      expect(parsed.mock.calls.map(([data]) => data)).toEqual(['retained']);
    } finally {
      buffer.dispose();
    }
  });

  it('yields a flooded parser while retaining ordered callbacks and all output', () => {
    vi.useFakeTimers();
    let now = 100;
    vi.spyOn(performance, 'now').mockImplementation(() => now);
    const parsed: string[] = [];
    const completed: number[] = [];
    const buffer = new WriteBuffer((data) => {
      parsed.push(String(data));
      now += 8;
    });
    try {
      for (let i = 0; i < 6; i++) buffer.write(String(i), () => completed.push(i));
      vi.runOnlyPendingTimers();
      expect(parsed).toEqual(['0', '1']);
      expect(completed).toEqual([0, 1]);
      vi.runAllTimers();
      expect(parsed.join('')).toBe('012345');
      expect(completed).toEqual([0, 1, 2, 3, 4, 5]);
    } finally {
      buffer.dispose();
    }
  });

  it('resumes a real asynchronous ANSI handler without repeating text or callbacks', async () => {
    const terminal = new Terminal({ cols: 80, rows: 24, allowProposedApi: true });
    let calls = 0;
    const completed: string[] = [];
    terminal.parser.registerCsiHandler({ final: 'm' }, async () => {
      calls++;
      return false;
    });
    try {
      await new Promise<void>((resolve) => {
        terminal.write('before\x1b[31mafter', () => completed.push('first'));
        terminal.write('tail', () => {
          completed.push('second');
          resolve();
        });
      });
      expect(terminal.buffer.active.getLine(0)?.translateToString(true)).toBe('beforeaftertail');
      expect(calls).toBe(1);
      expect(completed).toEqual(['first', 'second']);
    } finally {
      terminal.dispose();
    }
  });

  it('ships the fix through the public terminal bundle and real resize path', () => {
    vi.useFakeTimers();
    const terminal = new Terminal({ cols: 80, rows: 24 });
    const completed: string[] = [];
    try {
      terminal.write('', () => completed.push('barrier'));
      terminal.write('replayed output', () => completed.push('output'));
      terminal.resize(81, 24);
      expect(completed).toEqual(['barrier', 'output']);
      expect(terminal.buffer.active.getLine(0)?.translateToString(true)).toBe('replayed output');
    } finally {
      terminal.dispose();
    }
  });

  it('preserves an empty recovery barrier and following output during a synchronous resize flush', () => {
    vi.useFakeTimers();
    const parsed: string[] = [];
    const completed: string[] = [];
    const buffer = new WriteBuffer((data) => {
      parsed.push(String(data));
    });
    try {
      buffer.write('', () => completed.push('barrier'));
      buffer.write('retained terminal output', () => completed.push('output'));
      buffer.flushSync();
      expect(completed).toEqual(['barrier', 'output']);
      expect(parsed).toEqual(['', 'retained terminal output']);
    } finally {
      buffer.dispose();
    }
  });

  it('preserves empty barriers when synchronous writes drain an existing queue', () => {
    vi.useFakeTimers();
    const parsed: string[] = [];
    const completed = vi.fn();
    const buffer = new WriteBuffer((data) => {
      parsed.push(String(data));
    });
    try {
      buffer.write('', completed);
      buffer.writeSync('new output');
      expect(completed).toHaveBeenCalledOnce();
      expect(parsed).toEqual(['', 'new output']);
    } finally {
      buffer.dispose();
    }
  });
});
