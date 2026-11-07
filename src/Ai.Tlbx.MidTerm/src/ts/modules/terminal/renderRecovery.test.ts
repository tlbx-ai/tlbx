import { describe, expect, it, vi } from 'vitest';
import type { Terminal } from '@xterm/xterm';
import { paintTerminalForCapture } from './renderRecovery';

describe('terminal capture paint boundary', () => {
  it('draws synchronously and restores the renderer pause without writing protocol bytes', () => {
    const renderer = {
      _isPaused: true,
      _renderDebouncer: { dispose: vi.fn() },
      _handleIntersectionChange: vi.fn(),
      refreshRows: vi.fn(),
    };
    const terminal = {
      rows: 30,
      modes: { synchronizedOutputMode: false },
      _core: { _renderService: renderer },
      write: vi.fn(),
      reset: vi.fn(),
    };
    paintTerminalForCapture(terminal as unknown as Terminal);
    expect(renderer._handleIntersectionChange.mock.calls).toEqual([
      [{ isIntersecting: true, intersectionRatio: 1 }],
      [{ isIntersecting: false, intersectionRatio: 0 }],
    ]);
    expect(renderer._renderDebouncer.dispose).toHaveBeenCalledOnce();
    expect(renderer.refreshRows).toHaveBeenCalledExactlyOnceWith(0, 29, true);
    expect(terminal.write).not.toHaveBeenCalled();
    expect(terminal.reset).not.toHaveBeenCalled();
    terminal.modes.synchronizedOutputMode = true;
    expect(() => paintTerminalForCapture(terminal as unknown as Terminal)).toThrow('incomplete');
    expect(renderer.refreshRows).toHaveBeenCalledOnce();
  });
});
