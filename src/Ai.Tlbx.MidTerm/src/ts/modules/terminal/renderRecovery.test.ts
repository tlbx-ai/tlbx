import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Terminal } from '@xterm/xterm';
import { setupTerminalRenderRecovery } from './renderRecovery';

function fixture() {
  let parse = () => {};
  let paint = () => {};
  const renderer = {
    _isPaused: false,
    _needsFullRefresh: false,
    _renderDebouncer: { _rowStart: undefined as number | undefined, dispose: vi.fn() },
    _handleIntersectionChange: vi.fn(() => {
      renderer._isPaused = false;
      renderer._needsFullRefresh = false;
      renderer._renderDebouncer._rowStart = 0;
    }),
    refreshRows: vi.fn(() => {
      renderer._renderDebouncer._rowStart = undefined;
      paint();
    }),
  };
  const terminal = {
    rows: 24,
    modes: { synchronizedOutputMode: false },
    _core: { _renderService: renderer },
    onWriteParsed: (cb: () => void) => {
      parse = cb;
      return {
        dispose: () => {
          parse = () => {};
        },
      };
    },
    onRender: (cb: () => void) => {
      paint = cb;
      return {
        dispose: () => {
          paint = () => {};
        },
      };
    },
  };
  const rect = { width: 800, height: 500, top: 0, left: 0, bottom: 500, right: 800 };
  const container = { isConnected: true, getBoundingClientRect: () => rect };
  const recovered = vi.fn();
  const watcher = setupTerminalRenderRecovery(
    terminal as unknown as Terminal,
    container as unknown as HTMLElement,
    recovered,
  );
  return {
    terminal,
    renderer,
    container,
    rect,
    recovered,
    watcher,
    parse: () => parse(),
    paint: () => paint(),
  };
}

describe('visible terminal render recovery', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.stubGlobal('document', { visibilityState: 'visible' });
    vi.stubGlobal('window', { innerWidth: 1400, innerHeight: 900 });
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('resumes a stale visibility pause even while output continuously arrives', () => {
    const f = fixture();
    f.renderer._isPaused = f.renderer._needsFullRefresh = true;
    for (let i = 0; i < 15; i++) {
      f.parse();
      vi.advanceTimersByTime(100);
    }
    expect(f.renderer._handleIntersectionChange).toHaveBeenCalledOnce();
    expect(f.renderer.refreshRows).toHaveBeenCalledWith(0, 23, true);
    expect(f.recovered).toHaveBeenCalledOnce();
    expect(vi.getTimerCount()).toBe(0);
    f.watcher.dispose();
  });

  it('cancels a lost queued frame and paints without resetting data or the renderer', () => {
    const f = fixture();
    f.renderer._renderDebouncer._rowStart = 4;
    f.parse();
    vi.advanceTimersByTime(1500);
    expect(f.renderer._renderDebouncer.dispose).toHaveBeenCalledOnce();
    expect(f.renderer.refreshRows).toHaveBeenCalledWith(0, 23, true);
    expect(f.renderer._handleIntersectionChange).not.toHaveBeenCalled();
    f.watcher.dispose();
  });

  it('does no recovery work for healthy paints, idle terminals, or nonvisual output', () => {
    const f = fixture();
    f.renderer._renderDebouncer._rowStart = 0;
    f.parse();
    f.paint();
    vi.advanceTimersByTime(10000);
    expect(f.recovered).not.toHaveBeenCalled();
    f.renderer._renderDebouncer._rowStart = undefined;
    f.parse();
    vi.advanceTimersByTime(10000);
    expect(f.recovered).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);
    f.watcher.dispose();
  });

  it.each(['hidden page', 'hidden pane', 'offscreen', 'detached'])('leaves %s paused', (kind) => {
    const f = fixture();
    f.renderer._isPaused = f.renderer._needsFullRefresh = true;
    if (kind === 'hidden page') vi.stubGlobal('document', { visibilityState: 'hidden' });
    if (kind === 'hidden pane') f.rect.height = 0;
    if (kind === 'offscreen') f.rect.top = 1000;
    if (kind === 'detached') f.container.isConnected = false;
    f.parse();
    vi.advanceTimersByTime(10000);
    expect(f.recovered).not.toHaveBeenCalled();
    expect(f.renderer._isPaused).toBe(true);
    f.watcher.dispose();
  });

  it('preserves synchronized output until the application ends its frame', () => {
    const f = fixture();
    f.renderer._isPaused = f.renderer._needsFullRefresh = true;
    f.terminal.modes.synchronizedOutputMode = true;
    f.parse();
    vi.advanceTimersByTime(1500);
    expect(f.renderer._isPaused).toBe(false);
    expect(f.renderer.refreshRows).not.toHaveBeenCalled();
    expect(f.terminal.modes.synchronizedOutputMode).toBe(true);
    f.terminal.modes.synchronizedOutputMode = false;
    f.paint();
    vi.advanceTimersByTime(1500);
    expect(f.recovered).not.toHaveBeenCalled();
    f.watcher.dispose();
  });

  it('removes pending recovery and subscriptions when a terminal is destroyed', () => {
    const f = fixture();
    f.renderer._needsFullRefresh = true;
    f.parse();
    f.watcher.dispose();
    f.parse();
    vi.advanceTimersByTime(10000);
    expect(vi.getTimerCount()).toBe(0);
    expect(f.recovered).not.toHaveBeenCalled();
  });
});
