import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import type { MidTermSettingsPublic } from '../../api/types';
import {
  getEffectiveTerminalBackgroundAlpha,
  getEffectiveTerminalCellBackgroundAlpha,
  getEffectiveXtermThemeForSettings,
  resolveEffectiveTerminalMinimumContrastRatio,
} from './themes';

const originalWindow = globalThis.window;
const originalNavigator = globalThis.navigator;

function createSettings(
  partial: Partial<
    Pick<
      MidTermSettingsPublic,
      | 'theme'
      | 'terminalColorScheme'
      | 'uiTransparency'
      | 'terminalTransparency'
      | 'terminalCellBackgroundTransparency'
      | 'terminalThemeLightnessBoost'
      | 'minimumContrastRatio'
      | 'backgroundImageEnabled'
      | 'hideBackgroundImageOnMobile'
      | 'backgroundImageFileName'
    >
  >,
): MidTermSettingsPublic {
  return {
    theme: 'dark',
    terminalColorScheme: 'auto',
    terminalColorSchemes: [],
    uiTransparency: 0,
    terminalTransparency: 0,
    terminalCellBackgroundTransparency: 0,
    terminalThemeLightnessBoost: 0,
    minimumContrastRatio: 1,
    backgroundImageEnabled: false,
    hideBackgroundImageOnMobile: true,
    backgroundImageFileName: null,
    ...partial,
  } as MidTermSettingsPublic;
}

describe('themes', () => {
  beforeEach(() => {
    Object.defineProperty(globalThis, 'window', {
      value: {
        matchMedia: () => ({ matches: false }),
      },
      configurable: true,
      writable: true,
    });

    Object.defineProperty(globalThis, 'navigator', {
      value: { maxTouchPoints: 0 },
      configurable: true,
      writable: true,
    });
  });

  afterEach(() => {
    Object.defineProperty(globalThis, 'window', {
      value: originalWindow,
      configurable: true,
      writable: true,
    });

    Object.defineProperty(globalThis, 'navigator', {
      value: originalNavigator,
      configurable: true,
      writable: true,
    });
  });

  it('applies terminal transparency to ANSI background palette colors', () => {
    const theme = getEffectiveXtermThemeForSettings(
      createSettings({
        terminalCellBackgroundTransparency: 60,
      }),
    );

    expect(theme.red).toBe('rgba(255, 64, 85, 0.400)');
    expect(theme.brightBlue).toBe('rgba(125, 166, 255, 0.400)');
  });

  it('boosts terminal text brightness without brightening terminal background surfaces', () => {
    const theme = getEffectiveXtermThemeForSettings(
      createSettings({
        terminalThemeLightnessBoost: 20,
      }),
    );

    expect(theme.background).toBe('#0C0C0C');
    expect(theme.cursor).toBe('#F2F2F2');
    expect(theme.cursorAccent).toBe('#0C0C0C');
    expect(theme.selectionBackground).toBe('#7BA2F780');
    expect(theme.scrollbarSliderBackground).toBe('rgba(58, 62, 82, 0.5)');
    expect(theme.foreground).toBe('#f5f5f5');
    expect(theme.black).toBe('#0C0C0C');
    expect(theme.brightBlack).toBe('#767676');
    expect(theme.red).toBe('#FF4055');
  });

  it('raises terminal contrast automatically on light terminal backgrounds', () => {
    expect(
      resolveEffectiveTerminalMinimumContrastRatio(
        createSettings({
          theme: 'light',
          terminalColorScheme: 'auto',
          minimumContrastRatio: 1,
        }),
      ),
    ).toBe(4.5);

    expect(
      resolveEffectiveTerminalMinimumContrastRatio(
        createSettings({
          terminalColorScheme: 'macTerminalLight',
          minimumContrastRatio: 1,
        }),
      ),
    ).toBe(4.5);
  });

  it('keeps ANSI backgrounds opaque when the cell background slider is off', () => {
    const theme = getEffectiveXtermThemeForSettings(
      createSettings({
        terminalTransparency: 60,
        terminalCellBackgroundTransparency: 0,
      }),
    );

    expect(theme.background).toBe('rgba(12, 12, 12, 0.400)');
    expect(theme.red).toBe('#FF4055');
  });

  it('forces terminal background opacity on mobile even when transparency is enabled', () => {
    Object.assign(globalThis.window, {
      matchMedia: () => ({ matches: true }),
    });

    const settings = createSettings({
      uiTransparency: 80,
      terminalTransparency: 60,
      terminalCellBackgroundTransparency: 40,
    });

    expect(getEffectiveTerminalBackgroundAlpha(settings)).toBe(1);
    expect(getEffectiveTerminalCellBackgroundAlpha(settings)).toBe(1);

    const theme = getEffectiveXtermThemeForSettings(settings);
    expect(theme.background).toBe('#0C0C0C');
    expect(theme.red).toBe('#FF4055');
  });
});
