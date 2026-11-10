import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { setViewportSize } from './webDock';
import {
  getSessionPreview,
  getSessionSelectedPreviewName,
  removeSessionState,
  setSessionSelectedPreviewName,
} from './webSessionState';

const stores = vi.hoisted(() => ({
  active: {
    value: 'active' as string | null,
    get(): string | null {
      return this.value;
    },
  },
  viewport: { get: vi.fn(() => null), set: vi.fn() },
}));
vi.mock('../../stores', () => ({
  $activeSessionId: stores.active,
  $webPreviewViewport: stores.viewport,
  $webPreviewDocked: { get: () => false, set: vi.fn() },
}));
vi.mock('../terminal/scaling', () => ({ autoResizeAllTerminalsImmediate: vi.fn() }));
vi.mock('../sessionTabs', () => ({ setActionButtonActive: vi.fn() }));
vi.mock('./webPanel', () => ({
  hideIframe: vi.fn(),
  renderPreviewTabs: vi.fn(),
  restoreLastUrl: vi.fn(),
  showIframe: vi.fn(),
  unloadIframe: vi.fn(),
}));
vi.mock('./webDetach', () => ({ isDetachedOpenForSession: vi.fn(() => false) }));
vi.mock('./webApi', () => ({ clearWebPreviewTarget: vi.fn() }));
vi.mock('../logging', () => ({ createLogger: () => ({ info: vi.fn() }) }));

function frame(sessionId: string, previewName: string) {
  const properties = new Map<string, string>();
  return {
    dataset: { previewFrameKey: `${sessionId}::${previewName}` },
    style: {
      width: '1280px',
      height: '900px',
      maxWidth: '',
      maxHeight: '',
      left: '',
      top: '',
      transform: '',
      setProperty: (name: string, value: string) => {
        properties.set(name, value);
      },
    },
    properties,
    focus: vi.fn(),
    classList: { toggle: vi.fn() },
  };
}

beforeEach(() => {
  vi.clearAllMocks();
  stores.active.value = 'active';
  setSessionSelectedPreviewName('active', 'default');
});
afterEach(() => {
  removeSessionState('active');
  removeSessionState('background');
  vi.unstubAllGlobals();
});

describe('named existing-frame viewport contract', () => {
  it.each(['active', 'background'])('sizes a hidden %s-session frame without changing active chrome or focus', (sessionId) => {
    const selected = frame('active', 'default');
    const hidden = frame(sessionId, 'acp-source');
    const focused = {};
    vi.stubGlobal('document', {
      activeElement: focused,
      querySelectorAll: () => [selected, hidden],
      querySelector: vi.fn(),
      getElementById: vi.fn(),
    });
    expect(setViewportSize(390, 844, { sessionId, previewName: 'acp-source' })).toBe(true);
    expect(hidden.style.width).toBe('390px');
    expect(hidden.style.height).toBe('844px');
    expect(hidden.properties.get('--preview-background-width')).toBe('390px');
    expect(hidden.properties.get('--preview-background-height')).toBe('844px');
    expect(selected.style.width).toBe('1280px');
    expect(selected.style.height).toBe('900px');
    expect(hidden.focus).not.toHaveBeenCalled();
    expect(hidden.classList.toggle).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(focused);
    expect(getSessionSelectedPreviewName('active')).toBe('default');
    expect(stores.active.get()).toBe('active');
    expect(stores.viewport.set).not.toHaveBeenCalled();
    expect(getSessionPreview(sessionId, 'acp-source')?.viewport).toEqual({
      width: 390, height: 844,
    });
    expect(setViewportSize(0, 0, { sessionId, previewName: 'acp-source' })).toBe(true);
    expect(hidden.style.width).toBe('');
    expect(getSessionPreview(sessionId, 'acp-source')?.viewport).toBeNull();
  });

  it('fails for an absent named frame rather than sizing the current active frame', () => {
    const selected = frame('active', 'default');
    vi.stubGlobal('document', { querySelectorAll: () => [selected] });
    expect(setViewportSize(390, 844, {
      sessionId: 'background', previewName: 'acp-source',
    })).toBe(false);
    expect(selected.style.width).toBe('1280px');
    expect(stores.viewport.set).not.toHaveBeenCalled();
    expect(getSessionPreview('background', 'acp-source')?.viewport ?? null).toBeNull();
  });

  it('updates the selected frame even when the dock body is absent, without opening it', () => {
    const selected = frame('active', 'default');
    vi.stubGlobal('document', {
      querySelectorAll: () => [selected],
      querySelector: () => null,
      getElementById: () => null,
    });
    expect(setViewportSize(390, 844, {
      sessionId: 'active', previewName: 'default',
    })).toBe(true);
    expect(selected.style.width).toBe('390px');
    expect(stores.viewport.set).toHaveBeenCalledWith({ width: 390, height: 844 });
    expect(selected.focus).not.toHaveBeenCalled();
  });
});
