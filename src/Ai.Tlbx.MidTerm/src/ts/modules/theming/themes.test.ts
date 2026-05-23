import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import type { MidTermSettingsPublic } from '../../api/types';
import {
  boostTerminalTextColor,
  getEffectiveTerminalBackgroundAlpha,
  getEffectiveTerminalCellBackgroundAlpha,
  getEffectiveXtermThemeForSettings,
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
  it('uses terminal transparency for the xterm background alpha', () => {
    const theme = getEffectiveXtermThemeForSettings(
      createSettings({
        uiTransparency: 10,
        terminalTransparency: 60,
      }),
    );

    expect(theme.background).toBe('rgba(12, 12, 12, 0.400)');
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

  it('allows the terminal transparency slider to reach a fully transparent xterm background', () => {
    const theme = getEffectiveXtermThemeForSettings(
      createSettings({
        terminalTransparency: 100,
      }),
    );

    expect(theme.background).toBe('rgba(12, 12, 12, 0.000)');
  });

  it('falls back to ui transparency when terminal transparency is absent', () => {
    const theme = getEffectiveXtermThemeForSettings(
      createSettings({
        uiTransparency: 35,
        terminalTransparency: null,
        terminalCellBackgroundTransparency: null,
      }),
    );

    expect(theme.background).toBe('rgba(12, 12, 12, 0.650)');
  });

  it('resolves the mac terminal dark palette', () => {
    const theme = getEffectiveXtermThemeForSettings(
      createSettings({
        terminalColorScheme: 'macTerminalDark',
      }),
    );

    expect(theme.background).toBe('#000000');
    expect(theme.foreground).toBe('#FFFFFF');
    expect(theme.blue).toBe('#6444ED');
    expect(theme.brightBlue).toBe('#D09AF9');
  });

  it('resolves the dark2 direct color palette', () => {
    const theme = getEffectiveXtermThemeForSettings(
      createSettings({
        terminalColorScheme: 'dark2',
      }),
    );

    expect(theme.background).toBe('#000000');
    expect(theme.foreground).toBe('#FFFFFF');
    expect(theme.black).toBe('#000000');
    expect(theme.red).toBe('#FF0000');
    expect(theme.green).toBe('#00FF00');
    expect(theme.yellow).toBe('#FFFF00');
    expect(theme.blue).toBe('#0000FF');
    expect(theme.magenta).toBe('#FF00FF');
    expect(theme.cyan).toBe('#00FFFF');
    expect(theme.white).toBe('#FFFFFF');
    expect(theme.brightRed).toBe('#FF0000');
    expect(theme.brightWhite).toBe('#FFFFFF');
  });

  it('resolves built-in terminal color scheme names case-insensitively', () => {
    const theme = getEffectiveXtermThemeForSettings(
      createSettings({
        terminalColorScheme: 'Dark2',
      }),
    );

    expect(theme.background).toBe('#000000');
    expect(theme.foreground).toBe('#FFFFFF');
    expect(theme.red).toBe('#FF0000');
  });

  it('resolves the campbell palette', () => {
    const theme = getEffectiveXtermThemeForSettings(
      createSettings({
        terminalColorScheme: 'campbell',
      }),
    );

    expect(theme.background).toBe('#0C0C0C');
    expect(theme.foreground).toBe('#CCCCCC');
    expect(theme.blue).toBe('#0037DA');
    expect(theme.brightCyan).toBe('#61D6D6');
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
    expect(theme.selectionBackground).toBe('#2D3044');
    expect(theme.scrollbarSliderBackground).toBe('rgba(58, 62, 82, 0.5)');
    expect(theme.foreground).toBe('#ffffff');
    expect(theme.black).toBe('#0C0C0C');
    expect(theme.brightBlack).toBe('#767676');
    expect(theme.red).toBe('#FF4055');
  });

  it('lets terminal text brightness boost visibly brighten ANSI foreground colors', () => {
    const theme = getEffectiveXtermThemeForSettings(
      createSettings({
        terminalThemeLightnessBoost: 50,
      }),
    );

    expect(theme.background).toBe('#0C0C0C');
    expect(theme.black).toBe('#0C0C0C');
    expect(theme.brightBlack).toBe('#767676');
    expect(theme.foreground).toBe('#ffffff');

    expect(boostTerminalTextColor('#f0f0f0', 50)).toBe('#ffffff');
    expect(boostTerminalTextColor('#2B65FF', 50)).toBe('#5d97ff');
    expect(boostTerminalTextColor('#767676', 50)).toBe('#a8a8a8');
  });

  it('resolves the mac terminal light palette', () => {
    const theme = getEffectiveXtermThemeForSettings(
      createSettings({
        terminalColorScheme: 'macTerminalLight',
      }),
    );

    expect(theme.background).toBe('#FFFFFF');
    expect(theme.foreground).toBe('#000000');
    expect(theme.blue).toBe('#0000B2');
    expect(theme.brightBlue).toBe('#0000FF');
  });

  it('resolves a saved custom palette by name', () => {
    const theme = getEffectiveXtermThemeForSettings(
      createSettings({
        terminalColorScheme: 'Ocean Copy',
        terminalColorSchemes: [
          {
            name: 'Ocean Copy',
            background: '#101820',
            foreground: '#F2F7FF',
            cursor: '#F2F7FF',
            cursorAccent: '#101820',
            selectionBackground: '#2A4C66',
            scrollbarSliderBackground: 'rgba(242, 247, 255, 0.2)',
            scrollbarSliderHoverBackground: 'rgba(242, 247, 255, 0.35)',
            scrollbarSliderActiveBackground: 'rgba(242, 247, 255, 0.5)',
            black: '#18242E',
            red: '#FF6B6B',
            green: '#7EE787',
            yellow: '#F9E27D',
            blue: '#66B3FF',
            magenta: '#D2A8FF',
            cyan: '#7DE3FF',
            white: '#D8E7F5',
            brightBlack: '#5A7288',
            brightRed: '#FF8E8E',
            brightGreen: '#9CF0A4',
            brightYellow: '#FFEEA8',
            brightBlue: '#90CCFF',
            brightMagenta: '#E2C0FF',
            brightCyan: '#A1EEFF',
            brightWhite: '#F2F7FF',
          },
        ],
      }),
    );

    expect(theme.background).toBe('#101820');
    expect(theme.foreground).toBe('#F2F7FF');
    expect(theme.blue).toBe('#66B3FF');
    expect(theme.brightCyan).toBe('#A1EEFF');
  });

  it('applies transparency to custom ANSI palette colors too', () => {
    const theme = getEffectiveXtermThemeForSettings(
      createSettings({
        terminalTransparency: 25,
        terminalCellBackgroundTransparency: 25,
        terminalColorScheme: 'Ocean Copy',
        terminalColorSchemes: [
          {
            name: 'Ocean Copy',
            background: '#101820',
            foreground: '#F2F7FF',
            cursor: '#F2F7FF',
            cursorAccent: '#101820',
            selectionBackground: '#2A4C66',
            scrollbarSliderBackground: 'rgba(242, 247, 255, 0.2)',
            scrollbarSliderHoverBackground: 'rgba(242, 247, 255, 0.35)',
            scrollbarSliderActiveBackground: 'rgba(242, 247, 255, 0.5)',
            black: '#18242E',
            red: '#FF6B6B',
            green: '#7EE787',
            yellow: '#F9E27D',
            blue: '#66B3FF',
            magenta: '#D2A8FF',
            cyan: '#7DE3FF',
            white: '#D8E7F5',
            brightBlack: '#5A7288',
            brightRed: '#FF8E8E',
            brightGreen: '#9CF0A4',
            brightYellow: '#FFEEA8',
            brightBlue: '#90CCFF',
            brightMagenta: '#E2C0FF',
            brightCyan: '#A1EEFF',
            brightWhite: '#F2F7FF',
          },
        ],
      }),
    );

    expect(theme.background).toBe('rgba(16, 24, 32, 0.750)');
    expect(theme.blue).toBe('rgba(102, 179, 255, 0.750)');
    expect(theme.brightWhite).toBe('rgba(242, 247, 255, 0.750)');
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

  it('treats the wallpaper as disabled on mobile when mobile wallpaper suppression is enabled', () => {
    Object.assign(globalThis.window, {
      matchMedia: () => ({ matches: true }),
    });

    const theme = getEffectiveXtermThemeForSettings(
      createSettings({
        hideBackgroundImageOnMobile: true,
        backgroundImageEnabled: true,
        backgroundImageFileName: 'paper.jpg',
      }),
    );

    expect(theme.background).toBe('#0C0C0C');
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
