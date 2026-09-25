import { beforeEach, describe, expect, it, vi } from 'vitest';

const { fetchHistory } = vi.hoisted(() => ({ fetchHistory: vi.fn() }));

vi.mock('./historyApi', () => ({ fetchHistory }));
vi.mock('../spaces', () => ({ closeSpacesDropdown: vi.fn() }));
vi.mock('../i18n', () => ({ t: (key: string) => key }));
vi.mock('../logging', () => ({ createLogger: () => ({ warn: vi.fn() }) }));

import { initHistoryDropdown } from './historyDropdown';

class FakeElement {
  className = '';
  innerHTML = '';
  style: Record<string, string> = {};
  private classes = new Set<string>();
  private listeners = new Map<string, () => void>();
  classList = {
    add: (name: string) => this.classes.add(name),
    remove: (name: string) => this.classes.delete(name),
    contains: (name: string) => this.classes.has(name),
  };

  appendChild(_child: FakeElement): void {}
  addEventListener(name: string, callback: () => void): void {
    this.listeners.set(name, callback);
  }
  click(): void {
    this.listeners.get('click')?.();
  }
  getBoundingClientRect(): DOMRect {
    return { top: 0, bottom: 500 } as DOMRect;
  }
  querySelector(): null {
    return null;
  }
}

describe('bookmark menu opening', () => {
  beforeEach(() => {
    fetchHistory.mockReset();
  });

  it('opens and closes while the history request is still pending', async () => {
    let finishLoad!: (entries: []) => void;
    fetchHistory.mockReturnValue(
      new Promise<[]>((resolve) => {
        finishLoad = resolve;
      }),
    );
    const button = new FakeElement();
    const sidebar = new FakeElement();
    let menu: FakeElement | null = null;
    vi.stubGlobal('HTMLElement', FakeElement);
    vi.stubGlobal('document', {
      createElement: () => (menu = new FakeElement()),
      getElementById: (id: string) => (id === 'btn-bookmarks' ? button : sidebar),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    });
    vi.stubGlobal('window', { addEventListener: vi.fn() });

    initHistoryDropdown(vi.fn());
    button.click();
    expect(menu?.classList.contains('visible')).toBe(true);
    button.click();
    expect(menu?.classList.contains('visible')).toBe(false);
    finishLoad([]);
    await Promise.resolve();
    expect(menu?.classList.contains('visible')).toBe(false);
    vi.unstubAllGlobals();
  });
});
