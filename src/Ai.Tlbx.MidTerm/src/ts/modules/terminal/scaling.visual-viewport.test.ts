import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { $currentSettings, $isMainBrowser } from '../../stores';
import { dom, sessionTerminals } from '../../state';
import { setupVisualViewport } from './visualViewport';
import { sendResize } from '../comms';

const mocks = vi.hoisted(() => ({
  remeasureTerminalCells: vi.fn((state: any) => {
    const dims = state.terminal?._core?._renderService?.dimensions?.css?.cell;
    if (dims) {
      dims.width = 10;
      dims.height = 20;
    }
  }),
}));

vi.mock('../comms', () => ({
  claimMainBrowser: vi.fn(),
  sendResize: vi.fn(),
}));

vi.mock('../i18n', () => ({
  t: (key: string) => key,
}));

vi.mock('../sidebar/voiceSection', () => ({
  isDevMode: () => false,
}));

vi.mock('../sessionTabs', () => ({
  getTabBarHeight: () => 0,
}));

vi.mock('./manager', () => ({
  focusActiveTerminal: vi.fn(),
  getCalibrationMeasurement: () => null,
  getCalibrationPromise: () => null,
}));

vi.mock('./presentationRefresh', () => ({
  isTerminalVisible: () => true,
  remeasureTerminalCells: mocks.remeasureTerminalCells,
  refreshTerminalRenderer: vi.fn(),
}));

function createHarness() {
  const terminal = {
    cols: 82,
    rows: 24,
    buffer: { active: { viewportY: 0, baseY: 0 } },
    resize: vi.fn((nextCols: number, nextRows: number) => {
      terminal.cols = nextCols;
      terminal.rows = nextRows;
    }),
    _core: {
      _renderService: {
        dimensions: {
          css: {
            cell: { width: 10, height: 20 },
          },
        },
      },
    },
  };

  const xterm = { style: {} as Record<string, string> };
  Object.defineProperties(xterm, {
    offsetWidth: {
      get: () => terminal.cols * 10,
    },
    offsetHeight: {
      get: () => terminal.rows * 20,
    },
  });

  const screen = {};
  Object.defineProperties(screen, {
    offsetWidth: {
      get: () => terminal.cols * 10,
    },
    offsetHeight: {
      get: () => terminal.rows * 20,
    },
  });

  const containerClasses = new Set<string>();
  const container = {
    style: {},
    clientWidth: 818,
    clientHeight: 488,
    scrollTop: 0,
    get scrollHeight() {
      return terminal.rows * 20;
    },
    classList: {
      contains: (name: string) => containerClasses.has(name),
      add: vi.fn((name: string) => {
        containerClasses.add(name);
      }),
      remove: vi.fn((name: string) => {
        containerClasses.delete(name);
      }),
      toggle: vi.fn((name: string, force?: boolean) => {
        const next = force ?? !containerClasses.has(name);
        if (next) {
          containerClasses.add(name);
        } else {
          containerClasses.delete(name);
        }
        return next;
      }),
    },
    closest: () => null,
    querySelector<T>(selector: string): T | null {
      if (selector === '.xterm') return xterm as T;
      if (selector === '.xterm-screen') return screen as T;
      return null;
    },
    getBoundingClientRect: () => ({ width: 818, height: 488 }),
  };

  return {
    state: {
      terminal,
      fitAddon: {
        fit: vi.fn(),
        proposeDimensions: vi.fn(),
      },
      container,
      serverCols: 82,
      serverRows: 24,
      opened: true,
      pendingVisualRefresh: false,
    },
  };
}

function createStyleObject(): CSSStyleDeclaration {
  const style = {} as CSSStyleDeclaration & Record<string, string>;
  style.setProperty = ((name: string, value: string) => {
    (style as Record<string, string>)[name] = value;
  }) as CSSStyleDeclaration['setProperty'];
  return style;
}

describe('setupVisualViewport', () => {
  const host = globalThis as typeof globalThis & {
    window?: typeof globalThis;
    visualViewport?: unknown;
    innerHeight: number;
    innerWidth: number;
    scrollTo: typeof globalThis.scrollTo;
  };
  const originalDocument = globalThis.document;
  const originalLocalStorage = globalThis.localStorage;
  const originalRequestAnimationFrame = globalThis.requestAnimationFrame;
  const originalWindow = host.window;
  const originalVisualViewport = host.visualViewport;
  const originalInnerHeight = host.innerHeight;
  const originalInnerWidth = host.innerWidth;
  const originalScrollTo = host.scrollTo;

  beforeEach(() => {
    sessionTerminals.clear();
    vi.mocked(sendResize).mockReset();
    mocks.remeasureTerminalCells.mockClear();
    $isMainBrowser.set(true);
    $currentSettings.set({
      fontSize: 14,
      fontFamily: 'Cascadia Code',
    } as never);

    const harness = createHarness();
    sessionTerminals.set('s1', harness.state as never);
    dom.terminalsArea = {
      getBoundingClientRect: () => ({ width: 818, height: 488 }),
    } as HTMLElement;

    const bodyClasses = new Set<string>();
    globalThis.document = {
      querySelector: () => null,
      documentElement: { style: createStyleObject() },
      body: {
        style: createStyleObject(),
        classList: {
          contains: (name: string) => bodyClasses.has(name),
          toggle: vi.fn((name: string, force?: boolean) => {
            if (force === undefined) {
              if (bodyClasses.has(name)) {
                bodyClasses.delete(name);
              } else {
                bodyClasses.add(name);
              }
              return;
            }

            if (force) {
              bodyClasses.add(name);
            } else {
              bodyClasses.delete(name);
            }
          }),
        },
      },
      getElementById: () => null,
    } as unknown as Document;

    globalThis.requestAnimationFrame = ((callback: FrameRequestCallback) => {
      callback(0);
      return 1;
    }) as typeof requestAnimationFrame;
    globalThis.localStorage = {
      getItem: () => null,
      setItem: () => undefined,
      removeItem: () => undefined,
      clear: () => undefined,
      key: () => null,
      length: 0,
    } as Storage;

    Object.defineProperty(host, 'window', {
      configurable: true,
      value: host,
    });
    Object.defineProperty(host, 'innerHeight', {
      configurable: true,
      value: 700,
    });
    Object.defineProperty(host, 'innerWidth', {
      configurable: true,
      value: 390,
    });
    Object.defineProperty(host, 'visualViewport', {
      configurable: true,
      value: {
        width: 390,
        height: 600,
        offsetTop: 0,
        addEventListener: vi.fn(),
      },
    });
    host.scrollTo = vi.fn();
  });

  afterEach(() => {
    sessionTerminals.clear();
    dom.terminalsArea = null;
    globalThis.document = originalDocument;
    globalThis.localStorage = originalLocalStorage;
    globalThis.requestAnimationFrame = originalRequestAnimationFrame;
    Object.defineProperty(host, 'window', {
      configurable: true,
      value: originalWindow,
    });
    Object.defineProperty(host, 'visualViewport', {
      configurable: true,
      value: originalVisualViewport,
    });
    Object.defineProperty(host, 'innerHeight', {
      configurable: true,
      value: originalInnerHeight,
    });
    Object.defineProperty(host, 'innerWidth', {
      configurable: true,
      value: originalInnerWidth,
    });
    host.scrollTo = originalScrollTo;
    vi.clearAllMocks();
  });

  it('makes the leading browser resize terminals on visual viewport changes', () => {
    setupVisualViewport();

    expect(mocks.remeasureTerminalCells).not.toHaveBeenCalled();
    expect(sendResize).toHaveBeenCalledWith('s1', 81, 24);
  });

  it('keeps terminal rows stable and pans the terminal on mobile height-only viewport changes', () => {
    const bodyClasses = new Set<string>();
    const resizeCallbacks: Array<() => void> = [];
    const visualViewport = {
      width: 390,
      height: 600,
      offsetTop: 0,
      addEventListener: vi.fn((type: string, callback: () => void) => {
        if (type === 'resize') {
          resizeCallbacks.push(callback);
        }
      }),
    };

    globalThis.document = {
      querySelector: () => null,
      documentElement: { style: createStyleObject() } as unknown as Document['documentElement'],
      body: {
        style: createStyleObject(),
        classList: {
          contains: (name: string) => bodyClasses.has(name),
          toggle: vi.fn((name: string, force?: boolean) => {
            const next = force ?? !bodyClasses.has(name);
            if (next) {
              bodyClasses.add(name);
            } else {
              bodyClasses.delete(name);
            }
            return next;
          }),
        },
      } as unknown as Document['body'],
      getElementById: () => null,
    } as unknown as Document;
    Object.defineProperty(host, 'visualViewport', {
      configurable: true,
      value: visualViewport,
    });

    setupVisualViewport();
    vi.mocked(sendResize).mockClear();
    const state = sessionTerminals.get('s1') as ReturnType<typeof createHarness>['state'];
    state.terminal.resize.mockClear();

    visualViewport.height = 430;
    resizeCallbacks.forEach((callback) => callback());

    expect(state.terminal.resize).not.toHaveBeenCalled();
    expect(sendResize).not.toHaveBeenCalled();
    expect(bodyClasses.has('keyboard-visible')).toBe(true);
    expect(bodyClasses.has('mobile-terminal-vertical-stable')).toBe(true);
    expect(state.container.classList.contains('mobile-terminal-vertical-stable')).toBe(true);
    expect(state.container.scrollTop).toBe(state.container.scrollHeight);
  });

  it('pins the app shell to the visual viewport and marks the keyboard-visible state', () => {
    const bodyClasses = new Set<string>();
    const appEl = { style: createStyleObject() };
    const documentElement = { style: createStyleObject() };
    const body = {
      style: createStyleObject(),
      classList: {
        contains: (name: string) => bodyClasses.has(name),
        toggle: vi.fn((name: string, force?: boolean) => {
          if (force) {
            bodyClasses.add(name);
          } else {
            bodyClasses.delete(name);
          }
        }),
      },
    };

    globalThis.document = {
      querySelector: (selector: string) =>
        selector === '.terminal-page' ? (appEl as unknown as Element) : null,
      documentElement: documentElement as unknown as Document['documentElement'],
      body: body as unknown as Document['body'],
      getElementById: () => null,
    } as unknown as Document;

    Object.defineProperty(host, 'innerHeight', {
      configurable: true,
      value: 700,
    });
    Object.defineProperty(host, 'visualViewport', {
      configurable: true,
      value: {
        height: 500,
        offsetTop: 12,
        addEventListener: vi.fn(),
      },
    });

    setupVisualViewport();

    expect(appEl.style.top).toBe('12px');
    expect(appEl.style.bottom).toBe('auto');
    expect(appEl.style.height).toBe('482px');
    expect(appEl.style.maxHeight).toBe('482px');
    expect(documentElement.style.height).toBe('482px');
    expect(documentElement.style.maxHeight).toBe('482px');
    expect(documentElement.style['--midterm-visual-viewport-height']).toBe('482px');
    expect(documentElement.style['--midterm-visual-viewport-offset-top']).toBe('12px');
    expect(documentElement.style['--midterm-soft-keyboard-bottom-guard']).toBe('18px');
    expect(documentElement.style['--midterm-soft-keyboard-height']).toBe('200px');
    expect(body.style.height).toBe('482px');
    expect(body.style.maxHeight).toBe('482px');
    expect(bodyClasses.has('keyboard-visible')).toBe(true);
    expect(host.scrollTo).toHaveBeenCalledWith(0, 0);
  });

  it('does not force a page scroll while an editable composer already has focus', () => {
    const bodyClasses = new Set<string>();
    const focusedTextarea = { tagName: 'TEXTAREA', isContentEditable: false };
    const body = {
      style: createStyleObject(),
      classList: {
        contains: (name: string) => bodyClasses.has(name),
        toggle: vi.fn((name: string, force?: boolean) => {
          if (force) {
            bodyClasses.add(name);
          } else {
            bodyClasses.delete(name);
          }
        }),
      },
    };

    globalThis.document = {
      querySelector: () => null,
      documentElement: { style: createStyleObject() } as unknown as Document['documentElement'],
      body: body as unknown as Document['body'],
      activeElement: focusedTextarea,
      getElementById: () => null,
    } as unknown as Document;

    Object.defineProperty(host, 'visualViewport', {
      configurable: true,
      value: {
        height: 500,
        offsetTop: 12,
        addEventListener: vi.fn(),
      },
    });

    setupVisualViewport();

    expect(bodyClasses.has('keyboard-visible')).toBe(true);
    expect(host.scrollTo).not.toHaveBeenCalled();
  });
});
