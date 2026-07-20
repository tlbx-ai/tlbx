import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { $isMainBrowser, $terminalSizeControls } from '../../stores';
import { dom, sessionTerminals } from '../../state';
import { applyTerminalScalingSync } from './scaling';
import { claimEligibleVisibleTerminalSizes } from './sizeControlAutomation';
import { sendResize } from '../comms';

const commMocks = vi.hoisted(() => ({
  requestTerminalSizeControl: vi.fn(),
}));

vi.mock('../comms', () => ({
  requestTerminalSizeControl: commMocks.requestTerminalSizeControl,
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

type FakeElement = {
  id?: string;
  className?: string;
  type?: string;
  title?: string;
  innerHTML?: string;
  disabled?: boolean;
  parentElement?: FakeElement | null;
  style: Record<string, string>;
  classList: {
    contains: (name: string) => boolean;
    add: (name: string) => void;
    remove: (name: string) => void;
  };
  querySelector: <T>(selector: string) => T | null;
  appendChild: (child: FakeElement) => FakeElement;
  remove: () => void;
  setAttribute: (name: string, value: string) => void;
  addEventListener: (type: string, handler: () => void) => void;
  click?: () => void;
  closest: <T>(selector: string) => T | null;
  getElementsByClassName?: (className: string) => { item: (index: number) => FakeElement | null };
  getBoundingClientRect: () => { width: number; height: number };
  getClientRects?: () => Array<{ width: number; height: number }>;
  isConnected?: boolean;
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
  const attrs = new Map<string, string>();
  const listeners = new Map<string, () => void>();
  return {
    className,
    style: {},
    classList: createClassList(className.split(/\s+/).filter(Boolean)),
    querySelector: () => null,
    appendChild: (child) => child,
    remove() {
      this.parentElement = null;
    },
    setAttribute(name, value) {
      attrs.set(name, value);
    },
    addEventListener(type, handler) {
      listeners.set(type, handler);
    },
    click() {
      listeners.get('click')?.();
    },
    closest: () => null,
    getBoundingClientRect: () => ({ width: 0, height: 0 }),
  };
}

function fakeElementHasClass(element: FakeElement, className: string): boolean {
  return element.className?.split(/\s+/).includes(className) ?? false;
}

function createTerminalHarness(
  cols: number,
  rows: number,
  xtermDimensions?: { width: number; height: number },
) {
  const children: FakeElement[] = [];
  const terminal = {
    cols,
    rows,
    buffer: { active: { viewportY: 0, baseY: 0 } },
    options: {},
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

  const screen = {} as FakeElement;
  Object.defineProperties(screen, {
    offsetWidth: {
      get: () => terminal.cols * 10,
    },
    offsetHeight: {
      get: () => terminal.rows * 20,
    },
  });

  const xterm = {
    style: {} as Record<string, string>,
    querySelector<T>(selector: string): T | null {
      if (selector === '.xterm-screen') return screen as T;
      return null;
    },
    getElementsByClassName(className: string) {
      return {
        item: (index: number) => (className === 'xterm-screen' && index === 0 ? screen : null),
      };
    },
  } as FakeElement;

  Object.defineProperties(xterm, {
    offsetWidth: {
      get: () => xtermDimensions?.width ?? terminal.cols * 10,
    },
    offsetHeight: {
      get: () => xtermDimensions?.height ?? terminal.rows * 20,
    },
  });

  const container = {
    id: 'terminal-s1',
    style: {},
    clientWidth: 818,
    clientHeight: 488,
    classList: createClassList(),
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
      child.parentElement = this;
      child.remove = () => {
        const index = children.indexOf(child);
        if (index >= 0) {
          children.splice(index, 1);
        }
        child.parentElement = null;
      };
      return child;
    },
    remove(): void {
      children.length = 0;
    },
    setAttribute(): void {},
    addEventListener(): void {},
    closest: () => null,
    getBoundingClientRect: () => ({ width: 818, height: 488 }),
    getClientRects: () => [{ width: 818, height: 488 }],
    isConnected: true,
  } as FakeElement;

  const state = {
    terminal,
    fitAddon: {
      fit: vi.fn(),
      proposeDimensions: vi.fn(),
    },
    container,
    serverCols: cols,
    serverRows: rows,
    opened: true,
    pendingVisualRefresh: false,
  };

  return {
    state,
    terminal,
    xterm,
    container,
    getOverlay: () =>
      children.find((child) => fakeElementHasClass(child, 'scaled-overlay')) ?? null,
    getGapFillers: () =>
      children.filter((child) => fakeElementHasClass(child, 'terminal-gap-fill')),
  };
}

describe('terminal scaling badge thresholds', () => {
  const originalDocument = globalThis.document;
  const originalLocalStorage = globalThis.localStorage;
  const originalRequestAnimationFrame = globalThis.requestAnimationFrame;

  beforeEach(() => {
    sessionTerminals.clear();
    $isMainBrowser.set(false);
    setSizeControl(false);
    dom.terminalsArea = {
      getBoundingClientRect: () => ({ width: 818, height: 488 }),
    } as HTMLElement;
    globalThis.document = {
      createElement: () => createElementByClassName(),
      getElementById: () => null,
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
    vi.mocked(sendResize).mockReset();
  });

  afterEach(() => {
    sessionTerminals.clear();
    $terminalSizeControls.set({});
    dom.terminalsArea = null;
    globalThis.document = originalDocument;
    globalThis.localStorage = originalLocalStorage;
    globalThis.requestAnimationFrame = originalRequestAnimationFrame;
    vi.clearAllMocks();
  });

  it('shows the follower badge on a one-column oversized mismatch', () => {
    const harness = createTerminalHarness(82, 24);

    applyTerminalScalingSync(harness.state as never);

    expect(harness.getOverlay()?.innerHTML).toContain('terminal.sizeControlledElsewhere');
    expect(harness.getOverlay()?.innerHTML).toContain('terminal.continueHere');
    expect(harness.getOverlay()?.innerHTML).toContain('terminal.scaledViewExplanation');
    expect(harness.xterm.style.transform).toContain('scale(');
    expect(harness.xterm.style.transformOrigin).toBe('top left');
    expect(harness.getGapFillers()).toHaveLength(3);
    expect(harness.container.style['--terminal-gap-content-width']).toBe('818px');
    expect(harness.container.style['--terminal-gap-bottom-height']).toBe('9.171px');
  });

  it('shows the follower badge on a one-column undersized mismatch', () => {
    const harness = createTerminalHarness(80, 24);

    applyTerminalScalingSync(harness.state as never);

    expect(harness.getOverlay()?.innerHTML).toContain('terminal.sizeControlledElsewhere');
    expect(harness.getOverlay()?.innerHTML).toContain('terminal.continueHere');
    expect(harness.xterm.style.transform ?? '').toBe('');
  });

  it('shows the follower claim badge even when the terminal already fits', () => {
    const harness = createTerminalHarness(81, 24);

    applyTerminalScalingSync(harness.state as never);

    expect(harness.getOverlay()?.innerHTML).toContain('terminal.sizeControlledElsewhere');
    expect(harness.getOverlay()?.innerHTML).toContain('terminal.continueHereHint');
    expect(harness.xterm.style.transform ?? '').toBe('');
  });

  it('names the browser device that currently owns the terminal size', () => {
    const harness = createTerminalHarness(81, 24);
    setSizeControl(false, true, 'Windows PC · Chrome');

    applyTerminalScalingSync(harness.state as never);

    expect(harness.getOverlay()?.innerHTML).toContain('terminal.takeControlFrom');
    expect(harness.getOverlay()?.innerHTML).toContain('Windows PC · Chrome');
    expect(harness.getOverlay()?.innerHTML).not.toContain('terminal.continueHereHint');
  });

  it('escapes the server-projected owner label before rendering it', () => {
    const harness = createTerminalHarness(81, 24);
    setSizeControl(false, false, '<img src=x>');

    applyTerminalScalingSync(harness.state as never);

    expect(harness.getOverlay()?.innerHTML).toContain('&lt;img src=x&gt;');
    expect(harness.getOverlay()?.innerHTML).not.toContain('<img src=x>');
  });

  it('places the takeover action in a sufficiently wide empty terminal gap', () => {
    const harness = createTerminalHarness(40, 10);
    setSizeControl(false, false, 'iPad · Safari');

    applyTerminalScalingSync(harness.state as never);

    expect(harness.getOverlay()?.classList.contains('terminal-gap-right')).toBe(true);
    expect(harness.xterm.style.transform ?? '').toBe('');
  });

  it('automatically claims an already eligible visible terminal without forcing', async () => {
    const harness = createTerminalHarness(81, 24);
    sessionTerminals.set('s1', harness.state as never);
    setSizeControl(false, true, 'Work PC · Chrome');
    commMocks.requestTerminalSizeControl.mockResolvedValueOnce({
      status: {
        sessionId: 's1',
        isOwner: true,
        hasOwner: true,
        ownerOnline: true,
        canTakeOverAutomatically: true,
        ownerLabel: 'Home PC · Chrome',
        epoch: 2,
      },
      ownershipChanged: true,
      resizeApplied: false,
      cols: 0,
      rows: 0,
    });

    claimEligibleVisibleTerminalSizes();

    expect(commMocks.requestTerminalSizeControl).toHaveBeenCalledWith('s1', false);
    await Promise.resolve();
  });

  it('does not automatically steal from an online sibling tab in the same browser', () => {
    const harness = createTerminalHarness(81, 24);
    sessionTerminals.set('s1', harness.state as never);
    setSizeControl(false, true, 'Windows PC · Chrome', 42, true);

    claimEligibleVisibleTerminalSizes();

    expect(commMocks.requestTerminalSizeControl).not.toHaveBeenCalled();
  });

  it('immediately starts an explicit forced takeover from the follower notice', async () => {
    let resolveClaim: ((value: unknown) => void) | undefined;
    commMocks.requestTerminalSizeControl.mockReturnValueOnce(
      new Promise((resolve) => {
        resolveClaim = resolve;
      }),
    );
    const harness = createTerminalHarness(81, 24);
    applyTerminalScalingSync(harness.state as never);
    const overlay = harness.getOverlay();

    overlay?.click?.();

    expect(commMocks.requestTerminalSizeControl).toHaveBeenCalledWith('s1', true);
    expect(overlay?.disabled).toBe(true);
    expect(overlay?.classList.contains('claiming')).toBe(true);

    resolveClaim?.({
      status: {
        sessionId: 's1',
        isOwner: true,
        hasOwner: true,
        ownerOnline: true,
        canTakeOverAutomatically: true,
        epoch: 2,
      },
      ownershipChanged: true,
      resizeApplied: false,
      cols: 0,
      rows: 0,
    });
    await Promise.resolve();
    await Promise.resolve();

    expect(overlay?.disabled).toBe(false);
    expect(overlay?.classList.contains('claiming')).toBe(false);
  });

  it('fills natural-fit main-browser gaps from the rendered terminal grid', () => {
    const harness = createTerminalHarness(81, 24, { width: 818, height: 488 });
    setSizeControl(true);

    applyTerminalScalingSync(harness.state as never);

    expect(harness.getOverlay()).toBeNull();
    expect(harness.xterm.style.transform ?? '').toBe('');
    expect(harness.getGapFillers()).toHaveLength(3);
    expect(harness.container.style['--terminal-gap-content-width']).toBe('810px');
    expect(harness.container.style['--terminal-gap-content-height']).toBe('480px');
    expect(harness.container.style['--terminal-gap-right-width']).toBe('8px');
    expect(harness.container.style['--terminal-gap-bottom-height']).toBe('8px');
  });

  it('keeps passive scaling free of resize side effects after the browser becomes main', () => {
    const harness = createTerminalHarness(80, 24);
    sessionTerminals.set('s1', harness.state as never);
    setSizeControl(true);

    applyTerminalScalingSync(harness.state as never);

    expect(harness.terminal.resize).not.toHaveBeenCalled();
    expect(sendResize).not.toHaveBeenCalled();
    expect(harness.getOverlay()).toBeNull();
    expect(harness.xterm.style.transform ?? '').toBe('');
  });
});

function setSizeControl(
  isOwner: boolean,
  canTakeOverAutomatically = isOwner,
  ownerLabel?: string,
  epoch = 1,
  ownerInSameBrowserProfile = false,
): void {
  $terminalSizeControls.set({
    s1: {
      sessionId: 's1',
      isOwner,
      hasOwner: true,
      ownerOnline: true,
      ownerInSameBrowserProfile,
      canTakeOverAutomatically,
      ownerLabel,
      epoch,
    },
  });
}
