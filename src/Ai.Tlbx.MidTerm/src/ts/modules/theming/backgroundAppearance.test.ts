import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import type { MidTermSettingsPublic } from '../../api/types';
import { applyBackgroundAppearance } from './backgroundAppearance';

class MockStyle {
  private readonly values = new Map<string, string>();

  public setProperty(name: string, value: string): void {
    this.values.set(name, value);
  }

  public getPropertyValue(name: string): string {
    return this.values.get(name) ?? '';
  }
}

class MockClassList {
  private readonly values = new Set<string>();

  public toggle(name: string, force?: boolean): boolean {
    if (force === true) {
      this.values.add(name);
      return true;
    }

    if (force === false) {
      this.values.delete(name);
      return false;
    }

    if (this.values.has(name)) {
      this.values.delete(name);
      return false;
    }

    this.values.add(name);
    return true;
  }

  public contains(name: string): boolean {
    return this.values.has(name);
  }
}

const originalDocument = globalThis.document;
const originalWindow = globalThis.window;
const originalNavigator = globalThis.navigator;

function createSettings(
  partial: Partial<
    Pick<
      MidTermSettingsPublic,
      | 'theme'
      | 'terminalColorScheme'
      | 'terminalColorSchemes'
      | 'uiTransparency'
      | 'terminalTransparency'
      | 'backgroundImageEnabled'
      | 'hideBackgroundImageOnMobile'
      | 'backgroundImageFileName'
      | 'backgroundImageRevision'
      | 'backgroundKenBurnsEnabled'
      | 'backgroundKenBurnsZoomPercent'
      | 'backgroundKenBurnsSpeedPxPerSecond'
    >
  >,
): MidTermSettingsPublic {
  return {
    theme: 'dark',
    terminalColorScheme: 'auto',
    terminalColorSchemes: [],
    uiTransparency: 0,
    terminalTransparency: 0,
    backgroundImageEnabled: false,
    hideBackgroundImageOnMobile: true,
    backgroundImageFileName: null,
    backgroundImageRevision: 0,
    backgroundKenBurnsEnabled: false,
    backgroundKenBurnsZoomPercent: 150,
    backgroundKenBurnsSpeedPxPerSecond: 12,
    ...partial,
  } as MidTermSettingsPublic;
}

function alphaOf(value: string): number {
  const match = value.match(/,\s*([0-9.]+)\)$/);
  if (!match || !match[1]) {
    throw new Error(`Could not extract alpha from "${value}"`);
  }

  return Number.parseFloat(match[1]);
}

let rootStyle: MockStyle;
let bodyClassList: MockClassList;

beforeEach(() => {
  rootStyle = new MockStyle();
  bodyClassList = new MockClassList();

  Object.defineProperty(globalThis, 'document', {
    value: {
      documentElement: { style: rootStyle },
      body: { classList: bodyClassList },
    },
    configurable: true,
    writable: true,
  });

  Object.defineProperty(globalThis, 'window', {
    value: {
      innerWidth: 1280,
      innerHeight: 720,
      matchMedia: () => ({ matches: false }),
      addEventListener: () => undefined,
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
  Object.defineProperty(globalThis, 'document', {
    value: originalDocument,
    configurable: true,
    writable: true,
  });

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

describe('backgroundAppearance', () => {
  it('keeps terminal chrome tokens stable while UI transparency affects surrounding UI', () => {
    applyBackgroundAppearance(
      createSettings({
        theme: 'dark',
        uiTransparency: 30,
        terminalTransparency: 60,
      }),
    );

    const primaryAlpha = alphaOf(rootStyle.getPropertyValue('--bg-primary'));
    const elevatedAlpha = alphaOf(rootStyle.getPropertyValue('--bg-elevated'));
    const dropdownAlpha = alphaOf(rootStyle.getPropertyValue('--bg-dropdown'));
    const terminalCanvasAlpha = alphaOf(rootStyle.getPropertyValue('--terminal-canvas-background'));
    const sidebarAlpha = alphaOf(rootStyle.getPropertyValue('--bg-sidebar'));
    const terminalUiAlpha = alphaOf(rootStyle.getPropertyValue('--terminal-ui-background'));
    const appChromeAlpha = alphaOf(rootStyle.getPropertyValue('--app-chrome-background'));
    const textInputAlpha = alphaOf(rootStyle.getPropertyValue('--text-input-background'));
    const sidebarHoverAlpha = alphaOf(
      rootStyle.getPropertyValue('--sidebar-item-hover-background'),
    );

    expect(rootStyle.getPropertyValue('--bg-terminal')).toBe('');
    expect(rootStyle.getPropertyValue('--terminal-bg')).toBe('');
    expect(primaryAlpha).toBeCloseTo(0.812, 5);
    expect(elevatedAlpha).toBeGreaterThan(primaryAlpha);
    expect(dropdownAlpha).toBeGreaterThan(elevatedAlpha);
    expect(terminalCanvasAlpha).toBeCloseTo(0.4, 5);
    expect(sidebarAlpha).toBeCloseTo(0.854, 5);
    expect(sidebarAlpha).not.toBeCloseTo(terminalCanvasAlpha, 5);
    expect(terminalUiAlpha).toBeCloseTo(0.7, 5);
    expect(appChromeAlpha).toBeCloseTo(0.925, 5);
    expect(textInputAlpha).toBeCloseTo(0.94, 5);
    expect(sidebarHoverAlpha).toBeCloseTo(0.82, 5);
    expect(rootStyle.getPropertyValue('--terminal-canvas-background')).toBe(
      'rgba(12, 12, 12, 0.400)',
    );
    expect(rootStyle.getPropertyValue('--bg-primary-opaque')).toBe('#0D0E14');
    expect(rootStyle.getPropertyValue('--bg-settings-opaque')).toBe('#161821');
    expect(rootStyle.getPropertyValue('--bg-hover-opaque')).toBe('#2D3044');
    expect(rootStyle.getPropertyValue('--bg-active-opaque')).toBe('#363A50');
    expect(rootStyle.getPropertyValue('--bg-dropdown-opaque')).toBe('#242735');
    expect(rootStyle.getPropertyValue('--bg-elevated-opaque')).toBe('#161821');
    expect(rootStyle.getPropertyValue('--bg-session-hover-opaque')).toBe('#1C1E2A');
    expect(rootStyle.getPropertyValue('--bg-session-active-opaque')).toBe('#1C1E2A');
    expect(bodyClassList.contains('opaque-terminal-surfaces')).toBe(false);
  });

  it('keeps shared app chrome 75 percent opaque at full UI transparency', () => {
    applyBackgroundAppearance(
      createSettings({
        theme: 'dark',
        uiTransparency: 100,
      }),
    );

    expect(alphaOf(rootStyle.getPropertyValue('--app-chrome-background'))).toBeCloseTo(0.75, 5);
  });

  it('keeps Dev Browser panes 85 percent opaque at full UI transparency', () => {
    applyBackgroundAppearance(
      createSettings({
        theme: 'dark',
        uiTransparency: 100,
      }),
    );

    expect(alphaOf(rootStyle.getPropertyValue('--web-preview-pane-background'))).toBeCloseTo(0.85, 5);
    expect(alphaOf(rootStyle.getPropertyValue('--web-preview-pane-chrome-background'))).toBeCloseTo(
      0.85,
      5,
    );
    expect(alphaOf(rootStyle.getPropertyValue('--web-preview-pane-surface-background'))).toBeCloseTo(
      0.85,
      5,
    );
  });

  it('uses the selected terminal color scheme for the terminal canvas background', () => {
    applyBackgroundAppearance(
      createSettings({
        theme: 'dark',
        terminalColorScheme: 'dark2',
      }),
    );

    expect(rootStyle.getPropertyValue('--terminal-canvas-background')).toBe('rgba(0, 0, 0, 1.000)');
    expect(rootStyle.getPropertyValue('--terminal-ui-background')).toBe('rgba(5, 5, 10, 1.000)');
  });

  it('publishes wallpaper metadata and keeps popup shells opaque for the selected theme', () => {
    applyBackgroundAppearance(
      createSettings({
        theme: 'light',
        uiTransparency: 35,
        terminalTransparency: 55,
        backgroundImageEnabled: true,
        backgroundImageFileName: 'paper.jpg',
        backgroundImageRevision: 12,
        backgroundKenBurnsEnabled: true,
        backgroundKenBurnsZoomPercent: 180,
        backgroundKenBurnsSpeedPxPerSecond: 24,
      }),
    );

    expect(rootStyle.getPropertyValue('--app-background-image')).toBe(
      'url("/api/settings/background-image?v=12")',
    );
    expect(rootStyle.getPropertyValue('--app-background-size')).toBe('cover');
    expect(rootStyle.getPropertyValue('--app-background-transform')).toBe(
      'translate3d(0px, 0px, 0) scale(1.800)',
    );
    expect(rootStyle.getPropertyValue('--app-background-ken-burns-scale')).toBe('1.800');
    expect(rootStyle.getPropertyValue('--app-background-ken-burns-pan-x')).toBe('19.200%');
    expect(rootStyle.getPropertyValue('--app-background-ken-burns-pan-y')).toBe('12.800%');
    expect(rootStyle.getPropertyValue('--app-background-animation')).toMatch(
      /^midterm-app-background-ken-burns \d+\.\d{3}s linear infinite$/,
    );
    expect(rootStyle.getPropertyValue('--bg-primary-opaque')).toBe('#EAE2D8');
    expect(rootStyle.getPropertyValue('--bg-settings-opaque')).toBe('#FEFCF9');
    expect(rootStyle.getPropertyValue('--bg-elevated-opaque')).toBe('#FEFCF9');
    expect(rootStyle.getPropertyValue('--bg-dropdown-opaque')).toBe('#FEFCF9');
    expect(rootStyle.getPropertyValue('--bg-session-hover-opaque')).toBe('#DDD4C8');
    expect(rootStyle.getPropertyValue('--bg-session-active-opaque')).toBe('#D5CBBD');
    expect(rootStyle.getPropertyValue('--bg-terminal')).toBe('');
    expect(bodyClassList.contains('has-app-background')).toBe(true);
    expect(bodyClassList.contains('opaque-terminal-surfaces')).toBe(false);
  });

  it('does not restart Ken Burns when the same settings are reapplied', () => {
    const settings = createSettings({
      backgroundImageEnabled: true,
      backgroundImageFileName: 'paper.jpg',
      backgroundImageRevision: 12,
      backgroundKenBurnsEnabled: true,
      backgroundKenBurnsZoomPercent: 180,
      backgroundKenBurnsSpeedPxPerSecond: 24,
    });

    applyBackgroundAppearance(settings);

    const firstAnimation = rootStyle.getPropertyValue('--app-background-animation');
    const firstPanX = rootStyle.getPropertyValue('--app-background-ken-burns-pan-x');
    const firstPanY = rootStyle.getPropertyValue('--app-background-ken-burns-pan-y');

    applyBackgroundAppearance(settings);

    expect(rootStyle.getPropertyValue('--app-background-animation')).toBe(firstAnimation);
    expect(rootStyle.getPropertyValue('--app-background-ken-burns-pan-x')).toBe(firstPanX);
    expect(rootStyle.getPropertyValue('--app-background-ken-burns-pan-y')).toBe(firstPanY);
  });

  it('allows the UI transparency slider to reach a fully transparent UI shell', () => {
    applyBackgroundAppearance(
      createSettings({
        theme: 'dark',
        uiTransparency: 100,
      }),
    );

    expect(rootStyle.getPropertyValue('--bg-primary')).toBe('rgba(13, 14, 20, 0.000)');
    expect(rootStyle.getPropertyValue('--bg-elevated')).toBe('rgba(22, 24, 33, 0.000)');
    expect(rootStyle.getPropertyValue('--terminal-ui-background')).toBe('rgba(5, 5, 10, 0.000)');
    expect(rootStyle.getPropertyValue('--text-input-background')).toBe('rgba(36, 39, 53, 0.800)');
    expect(rootStyle.getPropertyValue('--sidebar-item-active-background')).toBe(
      'rgba(28, 30, 42, 0.400)',
    );
    expect(rootStyle.getPropertyValue('--bg-terminal')).toBe('');
    expect(bodyClassList.contains('opaque-terminal-surfaces')).toBe(true);
  });

  it('resets Ken Burns transform tokens when the effect is disabled', () => {
    applyBackgroundAppearance(
      createSettings({
        theme: 'dark',
        backgroundImageEnabled: true,
        backgroundImageFileName: 'paper.jpg',
        backgroundImageRevision: 12,
      }),
    );

    expect(rootStyle.getPropertyValue('--app-background-transform')).toBe(
      'translate3d(0px, 0px, 0) scale(1.000)',
    );
    expect(rootStyle.getPropertyValue('--app-background-animation')).toBe('none');
  });

  it('keeps background image on narrow desktop frames with fine pointer', () => {
    Object.assign(globalThis.window, {
      matchMedia: (query: string) => ({
        matches: query.includes('max-width'),
      }),
    });

    applyBackgroundAppearance(
      createSettings({
        backgroundImageEnabled: true,
        hideBackgroundImageOnMobile: true,
        backgroundImageFileName: 'paper.jpg',
        backgroundImageRevision: 12,
        backgroundKenBurnsEnabled: true,
      }),
    );

    expect(rootStyle.getPropertyValue('--app-background-image')).toBe(
      'url("/api/settings/background-image?v=12")',
    );
    expect(bodyClassList.contains('has-app-background')).toBe(true);
    expect(bodyClassList.contains('hide-app-background-on-mobile')).toBe(false);
  });

  it('suppresses the background image on coarse pointer mobile when the mobile wallpaper toggle is enabled', () => {
    Object.assign(globalThis.window, {
      matchMedia: (query: string) => ({
        matches: query.includes('pointer: coarse') || query.includes('max-width'),
      }),
    });

    applyBackgroundAppearance(
      createSettings({
        backgroundImageEnabled: true,
        hideBackgroundImageOnMobile: true,
        backgroundImageFileName: 'paper.jpg',
        backgroundImageRevision: 12,
        backgroundKenBurnsEnabled: true,
      }),
    );

    expect(rootStyle.getPropertyValue('--app-background-image')).toBe('none');
    expect(rootStyle.getPropertyValue('--app-background-animation')).toBe('none');
    expect(bodyClassList.contains('has-app-background')).toBe(false);
    expect(bodyClassList.contains('hide-app-background-on-mobile')).toBe(true);
  });

  it('forces opaque ui surfaces on mobile even when transparency is enabled in settings', () => {
    Object.assign(globalThis.window, {
      matchMedia: () => ({ matches: true }),
    });

    applyBackgroundAppearance(
      createSettings({
        uiTransparency: 100,
        terminalTransparency: 100,
      }),
    );

    expect(rootStyle.getPropertyValue('--bg-primary')).toBe('rgba(13, 14, 20, 1.000)');
    expect(rootStyle.getPropertyValue('--terminal-ui-background')).toBe('rgba(5, 5, 10, 1.000)');
    expect(rootStyle.getPropertyValue('--terminal-canvas-background')).toBe(
      'rgba(12, 12, 12, 1.000)',
    );
    expect(bodyClassList.contains('opaque-terminal-surfaces')).toBe(true);
  });

  it('does not depend on resize-time keyframe regeneration', () => {
    applyBackgroundAppearance(
      createSettings({
        backgroundImageEnabled: true,
        backgroundImageFileName: 'paper.jpg',
        backgroundImageRevision: 12,
        backgroundKenBurnsEnabled: true,
        backgroundKenBurnsZoomPercent: 180,
        backgroundKenBurnsSpeedPxPerSecond: 24,
      }),
    );

    const firstAnimation = rootStyle.getPropertyValue('--app-background-animation');
    const firstPanX = rootStyle.getPropertyValue('--app-background-ken-burns-pan-x');
    const firstPanY = rootStyle.getPropertyValue('--app-background-ken-burns-pan-y');

    Object.assign(globalThis.window, {
      innerWidth: 900,
      innerHeight: 900,
    });
    applyBackgroundAppearance(
      createSettings({
        backgroundImageEnabled: true,
        backgroundImageFileName: 'paper.jpg',
        backgroundImageRevision: 12,
        backgroundKenBurnsEnabled: true,
        backgroundKenBurnsZoomPercent: 180,
        backgroundKenBurnsSpeedPxPerSecond: 24,
      }),
    );

    expect(rootStyle.getPropertyValue('--app-background-animation')).toBe(firstAnimation);
    expect(rootStyle.getPropertyValue('--app-background-ken-burns-pan-x')).toBe(firstPanX);
    expect(rootStyle.getPropertyValue('--app-background-ken-burns-pan-y')).toBe(firstPanY);
  });
});
