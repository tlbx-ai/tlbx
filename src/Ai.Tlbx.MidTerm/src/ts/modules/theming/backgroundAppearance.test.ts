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

let rootStyle: MockStyle;
let bodyClassList: MockClassList;
let documentListeners: Map<string, Array<() => void>>;
let windowListeners: Map<string, Array<() => void>>;
let documentHasFocus: boolean;

function emitDocumentEvent(name: string): void {
  documentListeners.get(name)?.forEach((listener) => listener());
}

function emitWindowEvent(name: string): void {
  windowListeners.get(name)?.forEach((listener) => listener());
}

beforeEach(() => {
  rootStyle = new MockStyle();
  bodyClassList = new MockClassList();
  documentListeners = new Map();
  windowListeners = new Map();
  documentHasFocus = true;

  Object.defineProperty(globalThis, 'document', {
    value: {
      documentElement: { style: rootStyle },
      body: { classList: bodyClassList },
      hidden: false,
      visibilityState: 'visible',
      hasFocus: () => documentHasFocus,
      addEventListener: (name: string, listener: () => void) => {
        const listeners = documentListeners.get(name) ?? [];
        listeners.push(listener);
        documentListeners.set(name, listeners);
      },
    },
    configurable: true,
    writable: true,
  });

  Object.defineProperty(globalThis, 'window', {
    value: {
      innerWidth: 1280,
      innerHeight: 720,
      matchMedia: () => ({ matches: false }),
      addEventListener: (name: string, listener: () => void) => {
        const listeners = windowListeners.get(name) ?? [];
        listeners.push(listener);
        windowListeners.set(name, listeners);
      },
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

  it('keeps Ken Burns active while the browser tab is hidden or the window is inactive', () => {
    const settings = createSettings({
      backgroundImageEnabled: true,
      backgroundImageFileName: 'paper.jpg',
      backgroundImageRevision: 12,
      backgroundKenBurnsEnabled: true,
      backgroundKenBurnsZoomPercent: 180,
      backgroundKenBurnsSpeedPxPerSecond: 24,
    });

    applyBackgroundAppearance(settings);

    expect(bodyClassList.contains('app-background-animation-paused')).toBe(false);
    expect(documentListeners.get('visibilitychange')?.length ?? 0).toBe(0);
    expect(windowListeners.get('blur')?.length ?? 0).toBe(0);
    expect(windowListeners.get('focus')?.length ?? 0).toBe(0);

    Object.assign(globalThis.document, {
      hidden: true,
      visibilityState: 'hidden',
    });
    emitDocumentEvent('visibilitychange');

    expect(bodyClassList.contains('app-background-animation-paused')).toBe(false);

    Object.assign(globalThis.document, {
      hidden: false,
      visibilityState: 'visible',
    });
    emitDocumentEvent('visibilitychange');

    expect(bodyClassList.contains('app-background-animation-paused')).toBe(false);

    documentHasFocus = false;
    emitWindowEvent('blur');

    expect(bodyClassList.contains('app-background-animation-paused')).toBe(false);

    documentHasFocus = true;
    emitWindowEvent('focus');

    expect(bodyClassList.contains('app-background-animation-paused')).toBe(false);
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
    expect(rootStyle.getPropertyValue('--command-bay-control-background')).toBe(
      'rgba(12, 12, 12, 1.000)',
    );
    expect(bodyClassList.contains('opaque-terminal-surfaces')).toBe(true);
  });
});
