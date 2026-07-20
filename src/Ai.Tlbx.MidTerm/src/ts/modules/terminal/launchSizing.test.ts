import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const calculateOptimalDimensionsSpy = vi.fn();
const terminalsArea = {} as HTMLElement;

vi.mock('../../state', () => ({
  dom: {
    terminalsArea,
  },
}));

vi.mock('./fontConfig', () => ({
  getConfiguredTerminalFontFamily: () => 'Cascadia Code',
  normalizeTerminalFontWeight: (value: string | undefined, fallback: string) => value ?? fallback,
  normalizeTerminalLetterSpacing: (value: number | undefined) => value ?? 0,
}));

vi.mock('./fontSize', () => ({
  getEffectiveTerminalFontSize: (size: number) => size,
}));

vi.mock('./scaling', () => ({
  calculateOptimalDimensions: calculateOptimalDimensionsSpy,
}));

describe('resolveLaunchDimensions', () => {
  const originalCrypto = globalThis.crypto;

  beforeEach(() => {
    calculateOptimalDimensionsSpy.mockReset();
    Object.defineProperty(globalThis, 'crypto', {
      configurable: true,
      value: {
        randomUUID: () => '12345678-1234-1234-1234-1234567890ab',
      } as Crypto,
    });
  });

  afterEach(() => {
    Object.defineProperty(globalThis, 'crypto', {
      configurable: true,
      value: originalCrypto,
    });
  });

  it('uses configured defaults when viewport measurement is unavailable', async () => {
    const { resolveLaunchDimensions } = await import('./launchSizing');

    const dims = await resolveLaunchDimensions({ defaultCols: 120, defaultRows: 30 }, 'launcher');

    expect(dims).toEqual({ cols: 120, rows: 30 });
    expect(calculateOptimalDimensionsSpy).toHaveBeenCalledOnce();
  });

  it('uses the creating browser viewport for launch sizing', async () => {
    calculateOptimalDimensionsSpy.mockResolvedValue({ cols: 81, rows: 24 });
    const { resolveLaunchDimensions } = await import('./launchSizing');

    const dims = await resolveLaunchDimensions({ defaultCols: 120, defaultRows: 30 }, 'launcher');

    expect(dims).toEqual({ cols: 81, rows: 24 });
    expect(calculateOptimalDimensionsSpy).toHaveBeenCalledOnce();
  });

  it('accepts exact minimum viewport dimensions instead of falling back to defaults', async () => {
    calculateOptimalDimensionsSpy.mockResolvedValue({ cols: 10, rows: 5 });
    const { resolveLaunchDimensions } = await import('./launchSizing');

    const dims = await resolveLaunchDimensions({ defaultCols: 120, defaultRows: 30 }, 'launcher');

    expect(dims).toEqual({ cols: 10, rows: 5 });
  });
});
