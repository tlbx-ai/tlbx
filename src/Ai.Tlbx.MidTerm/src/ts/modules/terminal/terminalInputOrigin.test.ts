import { describe, expect, it, vi } from 'vitest';
import { onTerminalInput } from './terminalInputOrigin';

describe('terminal input origin', () => {
  it('keeps parser replies separate from keyboard and paste input', () => {
    let user = () => {};
    let data = (_value: string) => {};
    const disposeUser = vi.fn();
    const disposeData = vi.fn();
    const terminal = {
      _core: {
        coreService: {
          onUserInput: (fn: () => void) => {
            user = fn;
            return { dispose: disposeUser };
          },
        },
      },
      onData: (fn: (value: string) => void) => {
        data = fn;
        return { dispose: disposeData };
      },
    };
    const received = vi.fn();
    const binding = onTerminalInput(terminal as never, received);
    data('\x1b[1;1R');
    user();
    data('a');
    data('\x1b[?1;2c');
    user();
    data('pasted text');
    expect(received.mock.calls).toEqual([
      ['\x1b[1;1R', false],
      ['a', true],
      ['\x1b[?1;2c', false],
      ['pasted text', true],
    ]);
    binding.dispose();
    expect(disposeUser).toHaveBeenCalledOnce();
    expect(disposeData).toHaveBeenCalledOnce();
  });
});
