import { describe, expect, it, vi } from 'vitest';

vi.mock('../../stores', () => ({ $activeSessionId: { get: vi.fn() } }));
vi.mock('../auth/sessionLifetime', () => ({ isAuthRedirectPending: vi.fn(() => false) }));
vi.mock('../i18n', () => ({ t: (key: string) => key }));
vi.mock('../logging', () => ({
  createLogger: () => ({ warn: vi.fn() }),
}));
vi.mock('../sessionTabs/tabBar', () => ({ setInputHistoryClickHandler: vi.fn() }));
vi.mock('./inputHistoryApi', () => ({
  deleteInputHistory: vi.fn(),
  fetchInputHistory: vi.fn(),
  replayInputHistory: vi.fn(),
}));
vi.mock('./inputHistoryPanel', () => ({ renderInputHistoryPanel: vi.fn() }));

import { shouldDismissSessionInputHistoryMenu } from './sessionInputHistoryMenu';

function element(left: number, top: number, right: number, bottom: number): HTMLElement {
  return {
    getBoundingClientRect: () => ({ left, top, right, bottom }),
  } as unknown as HTMLElement;
}

function pointer(
  clientX: number,
  clientY: number,
  path: EventTarget[] = [],
): { clientX: number; clientY: number; composedPath: () => EventTarget[] } {
  return { clientX, clientY, composedPath: () => path };
}

describe('session input history dismissal', () => {
  const popover = element(100, 50, 560, 610);
  const trigger = element(430, 10, 520, 50);

  it('keeps the menu open for descendants reported in the event path', () => {
    expect(
      shouldDismissSessionInputHistoryMenu(pointer(200, 100, [popover]), popover, trigger),
    ).toBe(false);
  });

  it('keeps the menu open when a scrollbar gesture is retargeted outside the popover', () => {
    expect(shouldDismissSessionInputHistoryMenu(pointer(558, 300), popover, trigger)).toBe(false);
  });

  it('keeps the menu open for its trigger and dismisses only an outside pointer', () => {
    expect(shouldDismissSessionInputHistoryMenu(pointer(480, 30), popover, trigger)).toBe(false);
    expect(shouldDismissSessionInputHistoryMenu(pointer(40, 700), popover, trigger)).toBe(true);
  });
});
