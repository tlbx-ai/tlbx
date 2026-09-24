import type { Terminal } from '@xterm/xterm';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createCodexLocalEcho } from './codexLocalEcho';

type Listener = () => void;

function eventSource() {
  const listeners = new Set<Listener>();
  return {
    register: (listener: Listener) => {
      listeners.add(listener);
      return { dispose: () => listeners.delete(listener) };
    },
    fire: () => listeners.forEach((listener) => listener()),
  };
}

function fixture() {
  const parsed = eventSource();
  const rendered = eventSource();
  const resized = eventSource();
  const scrolled = eventSource();
  const listeners = new Map<string, Listener>();
  const overlays: Array<{ textContent: string; removed: boolean; style: Record<string, string> }> = [];
  const screen = {
    offsetWidth: 800,
    offsetHeight: 480,
    appendChild: (overlay: (typeof overlays)[number]) => overlays.push(overlay),
  };
  const container = {
    querySelector: (selector: string) => (selector === '.xterm-screen' ? screen : null),
    addEventListener: (name: string, listener: Listener) => listeners.set(name, listener),
    removeEventListener: (name: string) => listeners.delete(name),
  };
  const documentListeners = new Map<string, Listener>();
  const windowListeners = new Map<string, Listener>();
  vi.stubGlobal('document', {
    visibilityState: 'visible',
    createElement: () => {
      const overlay = {
        textContent: '',
        removed: false,
        style: {},
        setAttribute: () => {},
        remove() {
          this.removed = true;
        },
      };
      return overlay;
    },
    addEventListener: (name: string, listener: Listener) => documentListeners.set(name, listener),
    removeEventListener: (name: string) => documentListeners.delete(name),
  });
  vi.stubGlobal('window', {
    addEventListener: (name: string, listener: Listener) => windowListeners.set(name, listener),
    removeEventListener: (name: string) => windowListeners.delete(name),
  });

  let cellText = '';
  let lineText = '› ';
  const active = {
    baseY: 0,
    viewportY: 0,
    cursorX: 2,
    cursorY: 20,
    getLine: () => ({
      getCell: () => ({ getChars: () => cellText }),
      translateToString: () => lineText,
    }),
  };
  const terminal = {
    cols: 80,
    rows: 24,
    buffer: { active },
    modes: { mouseTrackingMode: 'none' },
    options: { theme: { foreground: '#fff' }, fontFamily: 'monospace', fontSize: 14, fontWeight: 400 },
    hasSelection: () => false,
    onWriteParsed: parsed.register,
    onRender: rendered.register,
    onResize: resized.register,
    onScroll: scrolled.register,
  };
  return {
    active,
    container: container as unknown as HTMLElement,
    events: { parsed, rendered, resized, scrolled, listeners, documentListeners, windowListeners },
    overlays,
    setCellText: (value: string) => {
      cellText = value;
    },
    setLineText: (value: string) => {
      lineText = value;
    },
    terminal: terminal as unknown as Terminal,
  };
}

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('Codex terminal local echo', () => {
  it('shows printable typing immediately and removes it only after real output is rendered', () => {
    const f = fixture();
    const echo = createCodexLocalEcho(f.terminal, f.container, () => true);
    echo.onInput('a');
    echo.onInput('b');
    expect(f.overlays).toHaveLength(1);
    expect(f.overlays[0]?.textContent).toBe('ab');
    f.events.rendered.fire();
    expect(f.overlays[0]?.removed).toBe(false);
    f.events.parsed.fire();
    f.events.rendered.fire();
    expect(f.overlays[0]?.removed).toBe(true);
    echo.dispose();
  });

  it('leaves paste, control keys, IME text, and occupied cells untouched', () => {
    const f = fixture();
    const echo = createCodexLocalEcho(f.terminal, f.container, () => true);
    echo.onInput('\r');
    echo.onInput('ä');
    f.setCellText('x');
    echo.onInput('a');
    expect(f.overlays).toHaveLength(0);
    f.setCellText('');
    echo.onInput('a');
    f.events.listeners.get('paste')?.();
    expect(f.overlays[0]?.removed).toBe(true);
    echo.dispose();
  });

  it('previews over the known empty Codex placeholder but never outside its composer line', () => {
    const f = fixture();
    const echo = createCodexLocalEcho(f.terminal, f.container, () => true);
    f.setCellText('A');
    f.setLineText('› Ask Codex to do anything');
    echo.onInput('a');
    echo.onInput('b');
    expect(f.overlays[0]?.textContent).toBe('ab');
    expect(f.overlays[0]?.style.backgroundColor).toBe('var(--terminal-canvas-background, var(--bg-terminal))');
    echo.clear();
    f.setLineText('Password: ');
    echo.onInput('b');
    expect(f.overlays).toHaveLength(1);
    echo.dispose();
  });

  it('expires unconfirmed text and cancels on resize, scroll, and disabled state', () => {
    vi.useFakeTimers();
    const f = fixture();
    let enabled = true;
    const echo = createCodexLocalEcho(f.terminal, f.container, () => enabled);
    echo.onInput('a');
    vi.advanceTimersByTime(120);
    expect(f.overlays[0]?.removed).toBe(true);
    echo.onInput('b');
    f.events.resized.fire();
    expect(f.overlays[1]?.removed).toBe(true);
    echo.onInput('c');
    f.events.scrolled.fire();
    expect(f.overlays[2]?.removed).toBe(true);
    enabled = false;
    echo.onInput('d');
    expect(f.overlays).toHaveLength(3);
    echo.dispose();
  });
});
