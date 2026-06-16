import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { $activeSessionId, $currentSettings, $isMainBrowser, $sessions } from '../../stores';
import { dom, sessionTerminals } from '../../state';
import {
  applyTerminalScaling,
  fitSessionToScreen,
  scheduleForegroundResizeRecovery,
} from './scaling';
import { sendResize } from '../comms';
import { focusActiveTerminal, recoverTerminalRendererAfterForeground } from './manager';

const mocks = vi.hoisted(() => ({
  isTerminalVisible: vi.fn((state: any) => !state.container?.classList?.contains('hidden')),
  remeasureTerminalCells: vi.fn((state: any) => {
    const dims = state.terminal?._core?._renderService?.dimensions?.css?.cell;
    if (dims) {
      dims.width = 10;
      dims.height = 20;
    }
  }),
  refreshTerminalRenderer: vi.fn(),
  recoverTerminalRendererAfterForeground: vi.fn(),
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
  recoverTerminalRendererAfterForeground: mocks.recoverTerminalRendererAfterForeground,
}));

vi.mock('./presentationRefresh', () => ({
  isTerminalVisible: mocks.isTerminalVisible,
  remeasureTerminalCells: mocks.remeasureTerminalCells,
  refreshTerminalRenderer: mocks.refreshTerminalRenderer,
}));

type FakeElement = {
  className?: string;
  style: Record<string, string>;
  classList: {
    contains: (name: string) => boolean;
    add: (name: string) => void;
    remove: (name: string) => void;
  };
  querySelector: <T>(selector: string) => T | null;
  appendChild?: (child: FakeElement) => FakeElement;
  setAttribute?: (name: string, value: string) => void;
  getBoundingClientRect: () => { width: number; height: number };
  clientWidth?: number;
  clientHeight?: number;
  offsetWidth?: number;
  offsetHeight?: number;
};

function createClassList(initial: string[] = []) {
  const classes = new Set(initial);
  return {
    contains: (name: string) => classes.has(name),
    add: (name: string) => {
      classes.add(name);
    },
    remove: (name: string) => {
      classes.delete(name);
    },
  };
}

function createElementByClassName(className = ''): FakeElement {
  return {
    className,
    style: {},
    classList: createClassList(className.split(/\s+/).filter(Boolean)),
    querySelector: () => null,
    appendChild: (child) => child,
    setAttribute: () => undefined,
    getBoundingClientRect: () => ({ width: 0, height: 0 }),
  };
}

function fakeElementHasClass(element: FakeElement, className: string): boolean {
  return element.className?.split(/\s+/).includes(className) ?? false;
}

function createFitHarness() {
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

  const xterm = {
    style: {} as Record<string, string>,
  } as FakeElement;
  Object.defineProperties(xterm, {
    offsetWidth: {
      get: () => terminal.cols * 10,
    },
    offsetHeight: {
      get: () => terminal.rows * 20,
    },
  });

  const screen = {} as FakeElement;
  Object.defineProperties(screen, {
    offsetWidth: {
      get: () => terminal.cols * 10,
    },
    offsetHeight: {
      get: () => terminal.rows * 20,
    },
  });

  const children: FakeElement[] = [];
  const container = {
    style: {},
    clientWidth: 818,
    clientHeight: 488,
    classList: createClassList(),
    closest: () => null,
    querySelector<T>(selector: string): T | null {
      if (selector === '.xterm') return xterm as T;
      if (selector === '.xterm-screen') return screen as T;
      if (selector.startsWith('.')) {
        const className = selector.slice(1);
        return (children.find((child) => fakeElementHasClass(child, className)) as T) ?? null;
      }
      return null;
    },
    appendChild(child: FakeElement): FakeElement {
      children.push(child);
      return child;
    },
    getBoundingClientRect: () => ({ width: 818, height: 488 }),
  } as FakeElement;

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
    terminal,
  };
}

describe('fitSessionToScreen', () => {
  const originalDocument = globalThis.document;
  const originalLocalStorage = globalThis.localStorage;
  const originalRequestAnimationFrame = globalThis.requestAnimationFrame;
  let bodyClasses: ReturnType<typeof createClassList>;

  beforeEach(() => {
    sessionTerminals.clear();
    mocks.isTerminalVisible.mockClear();
    mocks.remeasureTerminalCells.mockClear();
    mocks.refreshTerminalRenderer.mockClear();
    mocks.recoverTerminalRendererAfterForeground.mockClear();
    vi.mocked(sendResize).mockReset();
    vi.mocked(focusActiveTerminal).mockReset();
    $isMainBrowser.set(true);
    $currentSettings.set({
      fontSize: 14,
      fontFamily: 'Cascadia Code',
    } as never);
    $sessions.set({});
    $activeSessionId.set('s1');
    dom.terminalsArea = {
      getBoundingClientRect: () => ({ width: 818, height: 488 }),
    } as HTMLElement;
    bodyClasses = createClassList();
    globalThis.document = {
      createElement: () => createElementByClassName(),
      getElementById: () => null,
      body: {
        classList: bodyClasses,
      },
    } as Document;
    globalThis.localStorage = {
      getItem: () => null,
      setItem: () => undefined,
      removeItem: () => undefined,
      clear: () => undefined,
      key: () => null,
      length: 0,
    } as Storage;
    globalThis.requestAnimationFrame = ((callback: FrameRequestCallback) => {
      callback(0);
      return 1;
    }) as typeof requestAnimationFrame;
  });

  afterEach(() => {
    sessionTerminals.clear();
    dom.terminalsArea = null;
    $sessions.set({});
    $activeSessionId.set(null);
    globalThis.document = originalDocument;
    globalThis.localStorage = originalLocalStorage;
    globalThis.requestAnimationFrame = originalRequestAnimationFrame;
    vi.clearAllMocks();
  });

  it('fits the main-browser viewport without forcing a renderer refresh when cell metrics exist', () => {
    const harness = createFitHarness();
    sessionTerminals.set('s1', harness.state as never);

    fitSessionToScreen('s1');

    expect(mocks.remeasureTerminalCells).not.toHaveBeenCalled();
    expect(harness.terminal.resize).toHaveBeenCalledWith(81, 24);
    expect(sendResize).toHaveBeenCalledWith('s1', 81, 24);
    expect(focusActiveTerminal).toHaveBeenCalledTimes(1);
  });

  it('does not reclaim terminal focus while the soft keyboard is visible', () => {
    const harness = createFitHarness();
    sessionTerminals.set('s1', harness.state as never);
    bodyClasses.add('keyboard-visible');

    fitSessionToScreen('s1');

    expect(harness.terminal.resize).toHaveBeenCalledWith(81, 24);
    expect(sendResize).toHaveBeenCalledWith('s1', 81, 24);
    expect(focusActiveTerminal).not.toHaveBeenCalled();
  });

  it('retries a transiently tiny viewport instead of collapsing to minimum dimensions', () => {
    const harness = createFitHarness();
    let measurements = 0;
    sessionTerminals.set('s1', harness.state as never);
    dom.terminalsArea = {
      getBoundingClientRect: () => {
        measurements += 1;
        return measurements === 1 ? { width: 0, height: 0 } : { width: 818, height: 488 };
      },
    } as HTMLElement;

    fitSessionToScreen('s1');

    expect(harness.terminal.resize).toHaveBeenCalledTimes(1);
    expect(harness.terminal.resize).toHaveBeenCalledWith(81, 24);
    expect(sendResize).toHaveBeenCalledWith('s1', 81, 24);
  });

  it('recovers visible terminal renderers without sending a resize on foreground recovery when the viewport already matches', () => {
    const harness = createFitHarness();
    harness.state.terminal.cols = 81;
    harness.state.serverCols = 81;
    sessionTerminals.set('s1', harness.state as never);

    scheduleForegroundResizeRecovery();

    expect(recoverTerminalRendererAfterForeground).toHaveBeenCalledWith('s1', harness.state);
    expect(harness.terminal.resize).not.toHaveBeenCalled();
    expect(sendResize).not.toHaveBeenCalled();
  });

  it('recovers main-browser sizing on foreground recovery when the viewport changed in the background', () => {
    const harness = createFitHarness();
    sessionTerminals.set('s1', harness.state as never);

    scheduleForegroundResizeRecovery();

    expect(recoverTerminalRendererAfterForeground).toHaveBeenCalledWith('s1', harness.state);
    expect(harness.terminal.resize).toHaveBeenCalledWith(81, 24);
    expect(sendResize).toHaveBeenCalledWith('s1', 81, 24);
  });

  it('does not redraw hidden terminals on foreground recovery', () => {
    const harness = createFitHarness();
    harness.state.container.classList.add('hidden');
    sessionTerminals.set('s1', harness.state as never);

    scheduleForegroundResizeRecovery();

    expect(recoverTerminalRendererAfterForeground).not.toHaveBeenCalled();
  });

  it('coalesces repeated scaling requests for the same terminal into one animation frame', () => {
    const terminal = {
      cols: 80,
      rows: 24,
      buffer: { active: { viewportY: 0, baseY: 0 } },
    };
    const xterm = {
      style: {} as Record<string, string>,
      offsetWidth: 800,
      offsetHeight: 480,
    };
    const container = {
      clientWidth: 800,
      clientHeight: 480,
      classList: createClassList(),
      appendChild: vi.fn(),
      closest: () => null,
      querySelector<T>(selector: string): T | null {
        if (selector === '.xterm') return xterm as T;
        return null;
      },
    } as unknown as HTMLDivElement;
    const state = {
      terminal,
      fitAddon: {} as never,
      container,
      serverCols: 80,
      serverRows: 24,
      opened: true,
      pendingVisualRefresh: false,
    };

    const callbacks: FrameRequestCallback[] = [];
    globalThis.requestAnimationFrame = ((callback: FrameRequestCallback) => {
      callbacks.push(callback);
      return callbacks.length;
    }) as typeof requestAnimationFrame;

    applyTerminalScaling('s1', state as never);
    applyTerminalScaling('s1', state as never);

    expect(callbacks).toHaveLength(1);
  });
});
