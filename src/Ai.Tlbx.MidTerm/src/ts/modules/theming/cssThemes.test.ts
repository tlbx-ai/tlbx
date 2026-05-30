import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { applyCssTheme, CSS_THEMES, getNativeColorScheme } from './cssThemes';

class MockStyle {
  private readonly values = new Map<string, string>();

  public colorScheme = '';

  public setProperty(name: string, value: string): void {
    this.values.set(name, value);
  }

  public getPropertyValue(name: string): string {
    return this.values.get(name) ?? '';
  }
}

const originalDocument = globalThis.document;

let rootStyle: MockStyle;
let dataset: Record<string, string>;
let metaThemeColor = '';

beforeEach(() => {
  rootStyle = new MockStyle();
  dataset = {};
  metaThemeColor = '';

  Object.defineProperty(globalThis, 'document', {
    value: {
      documentElement: {
        style: rootStyle,
        dataset,
      },
      querySelector: (selector: string) =>
        selector === 'meta[name="theme-color"]'
          ? {
              setAttribute: (name: string, value: string) => {
                if (name === 'content') {
                  metaThemeColor = value;
                }
              },
            }
          : null,
    },
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
});

const requiredCtaTokens = [
  '--cta-primary',
  '--cta-primary-muted',
  '--cta-primary-text',
  '--cta-primary-25',
  '--cta-primary-40',
] as const;

describe('CSS_THEMES CTA tokens', () => {
  it('defines CTA tokens for every theme palette', () => {
    for (const [themeName, palette] of Object.entries(CSS_THEMES)) {
      for (const token of requiredCtaTokens) {
        expect(palette[token], `Missing token ${token} in theme ${themeName}`).toBeTruthy();
      }
    }
  });

  it('uses terracotta CTA tokens in light theme', () => {
    const light = CSS_THEMES.light;
    expect(light['--cta-primary']).toBe('#A7694D');
    expect(light['--cta-primary-muted']).toBe('#935B40');
    expect(light['--cta-primary-text']).toBe('#FFFEFA');
    expect(light['--cta-primary-25']).toBe('rgba(167, 105, 77, 0.25)');
    expect(light['--cta-primary-40']).toBe('rgba(167, 105, 77, 0.4)');
  });

  it('keeps non-light themes aligned with their existing gold CTA treatment', () => {
    expect(CSS_THEMES.dark['--cta-primary']).toBe(CSS_THEMES.dark['--accent-gold']);
    expect(CSS_THEMES.solarizedDark['--cta-primary']).toBe(
      CSS_THEMES.solarizedDark['--accent-gold'],
    );
    expect(CSS_THEMES.solarizedLight['--cta-primary']).toBe(
      CSS_THEMES.solarizedLight['--accent-gold'],
    );
  });

  it('maps browser-native color scheme to the active UI brightness', () => {
    expect(getNativeColorScheme('dark')).toBe('dark');
    expect(getNativeColorScheme('solarizedDark')).toBe('dark');
    expect(getNativeColorScheme('light')).toBe('light');
    expect(getNativeColorScheme('solarizedLight')).toBe('light');
  });

  it('keeps sidebar readability colors aligned with light theme surfaces', () => {
    expect(CSS_THEMES.light['--sidebar-readable-text-color']).toBe(
      CSS_THEMES.light['--text-primary'],
    );
    expect(CSS_THEMES.light['--sidebar-readable-muted-text-color']).toBe(
      CSS_THEMES.light['--text-secondary'],
    );
    expect(CSS_THEMES.light['--sidebar-readable-text-shadow']).toBe('none');

    expect(CSS_THEMES.solarizedLight['--sidebar-readable-text-color']).toBe(
      CSS_THEMES.solarizedLight['--text-primary'],
    );
    expect(CSS_THEMES.solarizedLight['--sidebar-readable-muted-text-color']).toBe(
      CSS_THEMES.solarizedLight['--text-secondary'],
    );
    expect(CSS_THEMES.solarizedLight['--sidebar-readable-text-shadow']).toBe('none');
  });

  it('publishes the active native color scheme for browser-rendered controls', () => {
    applyCssTheme('solarizedDark');
    expect(rootStyle.colorScheme).toBe('dark');
    expect(dataset.nativeColorScheme).toBe('dark');
    expect(metaThemeColor).toBe(CSS_THEMES.solarizedDark['--bg-primary']);

    applyCssTheme('light');
    expect(rootStyle.colorScheme).toBe('light');
    expect(dataset.nativeColorScheme).toBe('light');
    expect(metaThemeColor).toBe(CSS_THEMES.light['--bg-primary']);
  });
});
