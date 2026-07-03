import { beforeEach, describe, expect, it, vi } from 'vitest';

type ListenerMap = Record<string, (event: any) => void>;

const sessionListListeners: ListenerMap = {};
const documentListeners: ListenerMap = {};
const showDockOverlay = vi.fn();
const reorderSessions = vi.fn();
const persistSessionOrder = vi.fn();

const layoutRootClasses = new Set<string>(['hidden']);
const standaloneClasses = new Set<string>();
let layoutActive = true;
let mockSessions: Array<{ id: string }> = [];

const sessionList = {
  addEventListener(type: string, listener: (event: any) => void): void {
    sessionListListeners[type] = listener;
  },
};

const terminalsArea = {
  getBoundingClientRect(): DOMRect {
    return {
      left: 100,
      top: 100,
      right: 500,
      bottom: 400,
      width: 400,
      height: 300,
      x: 100,
      y: 100,
      toJSON(): Record<string, never> {
        return {};
      },
    } as DOMRect;
  },
};

const layoutRoot = {
  classList: {
    contains(name: string): boolean {
      return layoutRootClasses.has(name);
    },
    remove(name: string): void {
      layoutRootClasses.delete(name);
    },
    add(name: string): void {
      layoutRootClasses.add(name);
    },
  },
};

const sessionItem = {
  dataset: {
    sessionId: 'dragged',
    controlMode: 'human',
  },
  classList: {
    add: vi.fn(),
    remove: vi.fn(),
  },
  offsetWidth: 240,
  offsetHeight: 48,
  cloneNode(): any {
    return {
      style: {},
      classList: {
        remove: vi.fn(),
      },
      remove: vi.fn(),
    };
  },
  closest(selector: string): any {
    return selector === '.session-item' ? sessionItem : null;
  },
};

vi.mock('../../state', () => ({
  dom: {
    sessionList,
    terminalsArea,
  },
  sessionTerminals: new Map([
    [
      'layout-a',
      {
        container: {
          classList: {
            add: vi.fn(),
            remove: vi.fn(),
          },
        },
      },
    ],
    [
      'solo',
      {
        container: {
          classList: {
            add(name: string): void {
              standaloneClasses.add(name);
            },
            remove(name: string): void {
              standaloneClasses.delete(name);
            },
          },
        },
      },
    ],
  ]),
}));

vi.mock('../../stores', () => ({
  reorderSessions,
  $sessionList: {
    get: () => mockSessions,
  },
  $activeSessionId: {
    get: () => 'solo',
  },
}));

vi.mock('../comms', () => ({
  persistSessionOrder,
}));

vi.mock('./spacesTreeSidebar', () => ({
  isSessionFilterActive: () => false,
}));

vi.mock('../layout/dockOverlay', () => ({
  showDockOverlay,
  hideDockOverlay: vi.fn(),
  getDockTarget: vi.fn(() => null),
  isDockOverlayVisible: vi.fn(() => false),
}));

vi.mock('../layout/layoutStore', () => ({
  dockSession: vi.fn(),
  isLayoutActive: () => layoutActive,
  isSessionInLayout: (sessionId: string) => sessionId === 'layout-a',
}));

vi.mock('../layout/layoutRenderer', () => ({
  getLayoutRoot: () => layoutRoot,
}));

describe('sessionDrag', () => {
  beforeEach(() => {
    for (const key of Object.keys(sessionListListeners)) {
      delete sessionListListeners[key];
    }
    for (const key of Object.keys(documentListeners)) {
      delete documentListeners[key];
    }
    layoutRootClasses.clear();
    layoutRootClasses.add('hidden');
    standaloneClasses.clear();
    layoutActive = true;
    mockSessions = [];
    showDockOverlay.mockReset();
    reorderSessions.mockReset();
    persistSessionOrder.mockReset();

    class FakeHTMLElement {}
    vi.stubGlobal('HTMLElement', FakeHTMLElement);
    Object.setPrototypeOf(sessionItem, FakeHTMLElement.prototype);

    vi.stubGlobal('document', {
      addEventListener(type: string, listener: (event: any) => void): void {
        documentListeners[type] = listener;
      },
      body: {
        appendChild: vi.fn(),
      },
    });
  });

  it('reveals a hidden layout preview before dock hit-testing over the terminals area', async () => {
    vi.resetModules();
    const { initSessionDrag } = await import('./sessionDrag');

    initSessionDrag();

    sessionListListeners.dragstart?.({
      target: sessionItem,
      dataTransfer: {
        effectAllowed: '',
        setData: vi.fn(),
        setDragImage: vi.fn(),
      },
    });

    const preventDefault = vi.fn();
    documentListeners.dragover?.({
      clientX: 160,
      clientY: 180,
      preventDefault,
      dataTransfer: {
        dropEffect: 'none',
      },
    });

    expect(preventDefault).toHaveBeenCalled();
    expect(layoutRootClasses.has('hidden')).toBe(false);
    expect(standaloneClasses.has('hidden')).toBe(true);
    expect(showDockOverlay).toHaveBeenCalledWith(160, 180, 'dragged');
  });

  it('does not reorder across different sidebar reorder scopes', async () => {
    layoutActive = false;
    mockSessions = [{ id: 'dragged' }, { id: 'target' }];
    const targetItem = {
      dataset: {
        sessionId: 'target',
        controlMode: 'human',
        reorderScope: '',
      },
      classList: {
        add: vi.fn(),
        remove: vi.fn(),
      },
      getBoundingClientRect(): DOMRect {
        return {
          left: 0,
          top: 0,
          right: 240,
          bottom: 48,
          width: 240,
          height: 48,
          x: 0,
          y: 0,
          toJSON(): Record<string, never> {
            return {};
          },
        } as DOMRect;
      },
      closest(selector: string): any {
        return selector === '.session-item' ? targetItem : null;
      },
    };

    sessionItem.dataset.reorderScope = 'adhoc';

    Object.setPrototypeOf(targetItem, HTMLElement.prototype);

    vi.resetModules();
    const { initSessionDrag } = await import('./sessionDrag');

    initSessionDrag();

    sessionListListeners.dragstart?.({
      target: sessionItem,
      dataTransfer: {
        effectAllowed: '',
        setData: vi.fn(),
        setDragImage: vi.fn(),
      },
    });

    sessionListListeners.dragover?.({
      preventDefault: vi.fn(),
      clientY: 12,
      target: targetItem,
      dataTransfer: {
        dropEffect: 'none',
      },
    });

    sessionListListeners.drop?.({
      preventDefault: vi.fn(),
      target: targetItem,
    });

    expect(targetItem.classList.add).not.toHaveBeenCalled();
    expect(reorderSessions).not.toHaveBeenCalled();
    expect(persistSessionOrder).not.toHaveBeenCalled();
  });

  it('keeps unscoped space sessions dock-draggable without enabling sidebar reorder', async () => {
    layoutActive = false;
    mockSessions = [{ id: 'dragged' }, { id: 'target' }];
    const targetItem = {
      dataset: {
        sessionId: 'target',
        controlMode: 'human',
      },
      classList: {
        add: vi.fn(),
        remove: vi.fn(),
      },
      getBoundingClientRect(): DOMRect {
        return {
          left: 0,
          top: 0,
          right: 240,
          bottom: 48,
          width: 240,
          height: 48,
          x: 0,
          y: 0,
          toJSON(): Record<string, never> {
            return {};
          },
        } as DOMRect;
      },
      closest(selector: string): any {
        return selector === '.session-item' ? targetItem : null;
      },
    };

    delete sessionItem.dataset.reorderScope;

    Object.setPrototypeOf(targetItem, HTMLElement.prototype);

    vi.resetModules();
    const { initSessionDrag } = await import('./sessionDrag');

    initSessionDrag();

    sessionListListeners.dragstart?.({
      target: sessionItem,
      dataTransfer: {
        effectAllowed: '',
        setData: vi.fn(),
        setDragImage: vi.fn(),
      },
    });

    sessionListListeners.dragover?.({
      preventDefault: vi.fn(),
      clientY: 12,
      target: targetItem,
      dataTransfer: {
        dropEffect: 'none',
      },
    });
    sessionListListeners.drop?.({
      preventDefault: vi.fn(),
      target: targetItem,
    });

    expect(targetItem.classList.add).not.toHaveBeenCalled();
    expect(reorderSessions).not.toHaveBeenCalled();
    expect(persistSessionOrder).not.toHaveBeenCalled();

    documentListeners.dragover?.({
      clientX: 160,
      clientY: 180,
      preventDefault: vi.fn(),
      dataTransfer: {
        dropEffect: 'none',
      },
    });
    expect(showDockOverlay).toHaveBeenCalledWith(160, 180, 'dragged');
  });

  it('does not start a session drag from notes or other interactive controls', async () => {
    vi.resetModules();
    const { initSessionDrag } = await import('./sessionDrag');

    initSessionDrag();

    const preventDefault = vi.fn();
    sessionListListeners.dragstart?.({
      target: {
        closest: (selector: string) => (selector.includes('textarea') ? {} : null),
      },
      preventDefault,
      dataTransfer: {
        effectAllowed: '',
        setData: vi.fn(),
        setDragImage: vi.fn(),
      },
    });

    documentListeners.dragover?.({
      clientX: 160,
      clientY: 180,
      preventDefault: vi.fn(),
      dataTransfer: {
        dropEffect: 'none',
      },
    });

    expect(preventDefault).toHaveBeenCalled();
    expect(showDockOverlay).not.toHaveBeenCalled();
  });
});
