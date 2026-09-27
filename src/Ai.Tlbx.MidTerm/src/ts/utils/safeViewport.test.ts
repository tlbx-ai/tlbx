import { afterEach, expect, it, vi } from 'vitest';
import { getSafeViewportBounds } from './safeViewport';

afterEach(() => vi.unstubAllGlobals());

it('keeps floating controls inside the keyboard viewport and display insets', () => {
  vi.stubGlobal('window', {
    innerWidth: 390,
    innerHeight: 844,
    visualViewport: { offsetTop: 12, offsetLeft: 0, width: 390, height: 420 },
  });
  vi.stubGlobal('document', { documentElement: {} });
  const insets: Record<string, string> = {
    top: '24px',
    right: '16px',
    bottom: '34px',
    left: '16px',
  };
  vi.stubGlobal('getComputedStyle', () => ({
    getPropertyValue: (name: string) => insets[name.replace('--safe-area-inset-', '')],
  }));
  expect(getSafeViewportBounds()).toEqual({ top: 36, right: 374, bottom: 398, left: 16 });
});
