import { afterEach, describe, expect, it, vi } from 'vitest';
import { cancelMobileCursorPlacement, createMobileCursorPlacement } from './mobileCursorPlacement';

describe('mobile cursor placement feedback', () => {
  afterEach(() => {
    vi.useRealTimers();
  });
  function setup() {
    vi.useFakeTimers();
    const active = { cursorX: 42, cursorY: 14, baseY: 0, viewportY: 0 };
    let parsed = () => {};
    const terminal = {
      cols: 65,
      rows: 25,
      buffer: { active },
      modes: { mouseTrackingMode: 'none', applicationCursorKeysMode: false },
      onWriteParsed: (callback: () => void) => {
        parsed = callback;
        return { dispose: vi.fn() };
      },
    };
    const focused = vi.fn(() => true);
    const send = vi.fn(() => cancelMobileCursorPlacement('test'));
    const placement = createMobileCursorPlacement('test', terminal as never, send, focused);
    const click = vi.fn(() => cancelMobileCursorPlacement('test'));
    const reply = (x: number, y: number) => {
      active.cursorX = x;
      active.cursorY = y;
      parsed();
      vi.advanceTimersByTime(40);
    };
    return { placement, click, reply, send, active, terminal, focused, parsed: () => parsed() };
  }

  it('corrects the observed Codex 65-column versus 59-column wrap mismatch', () => {
    const s = setup();
    s.placement.request({ x: 42, y: 13 }, s.click);
    s.reply(36, 13);
    expect(s.send).toHaveBeenCalledExactlyOnceWith('\x1b[C'.repeat(6));
    s.reply(42, 13);
    expect(s.send).toHaveBeenCalledTimes(1);
    s.placement.dispose();
  });

  it('queues a rapid second tap until the first movement is reflected', () => {
    const s = setup();
    const second = vi.fn();
    const latest = vi.fn();
    s.placement.request({ x: 36, y: 14 }, s.click);
    s.placement.request({ x: 39, y: 14 }, second);
    s.placement.request({ x: 40, y: 14 }, latest);
    expect(latest).not.toHaveBeenCalled();
    s.reply(36, 14);
    expect(second).not.toHaveBeenCalled();
    expect(latest).toHaveBeenCalledOnce();
    s.reply(40, 14);
    expect(s.send).not.toHaveBeenCalled();
    s.placement.dispose();
  });

  it('waits for the final cursor of a redraw, not its intermediate position', () => {
    const s = setup();
    s.placement.request({ x: 42, y: 13 }, s.click);
    s.active.cursorX = 10;
    s.active.cursorY = 13;
    s.parsed();
    vi.advanceTimersByTime(20);
    s.active.cursorX = 42;
    s.parsed();
    vi.advanceTimersByTime(40);
    expect(s.send).not.toHaveBeenCalled();
    s.placement.dispose();
  });

  it.each(['input', 'blur', 'resize', 'scrollback', 'mouse', 'buffer', 'dispose'])(
    'does not send delayed corrections after %s',
    (reason) => {
      const s = setup();
      s.placement.request({ x: 42, y: 13 }, s.click);
      if (reason === 'input') cancelMobileCursorPlacement('test');
      if (reason === 'blur') s.focused.mockReturnValue(false);
      if (reason === 'resize') s.terminal.cols = 60;
      if (reason === 'scrollback') s.active.viewportY = -1;
      if (reason === 'mouse') s.terminal.modes.mouseTrackingMode = 'vt200';
      if (reason === 'buffer') s.terminal.buffer.active = { ...s.active };
      if (reason === 'dispose') s.placement.dispose();
      s.reply(36, 13);
      expect(s.send).not.toHaveBeenCalled();
      s.placement.dispose();
    },
  );

  it('abandons movement that the application ignores, including queued taps', () => {
    const s = setup();
    const queued = vi.fn();
    s.placement.request({ x: 36, y: 14 }, s.click);
    s.placement.request({ x: 39, y: 14 }, queued);
    s.reply(42, 14);
    vi.advanceTimersByTime(1000);
    s.reply(35, 14);
    expect(s.send).not.toHaveBeenCalled();
    expect(queued).not.toHaveBeenCalled();
    s.placement.dispose();
  });

  it('bounds corrections and preserves application cursor key mode', () => {
    const s = setup();
    s.terminal.modes.applicationCursorKeysMode = true;
    s.placement.request({ x: 42, y: 13 }, s.click);
    s.reply(36, 13);
    s.reply(44, 13);
    s.reply(40, 13);
    expect(s.send.mock.calls).toEqual([['\x1bOC'.repeat(6)], ['\x1bOD'.repeat(2)]]);
    s.placement.dispose();
  });

  it('does not guess additional vertical movement after reaching a different row', () => {
    const s = setup();
    s.placement.request({ x: 42, y: 13 }, s.click);
    s.reply(36, 12);
    expect(s.send).not.toHaveBeenCalled();
    s.placement.dispose();
  });
});
