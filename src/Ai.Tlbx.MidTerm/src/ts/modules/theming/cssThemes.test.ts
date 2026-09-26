import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { applyCssTheme, CSS_THEMES } from './cssThemes';

class MockStyle {
  private readonly values = new Map<string, string>();

  public colorScheme = '';

  public setProperty(name: string, value: string): void {
    this.values.set(name, value);
  }

  public removeProperty(name: string): string {
    const previous = this.values.get(name) ?? '';
    this.values.delete(name);
    return previous;
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

  it('restores dark high-contrast sidebar colors after a light theme', () => {
    applyCssTheme('solarizedLight');
    applyCssTheme('dark');

    expect(rootStyle.getPropertyValue('--sidebar-readable-text-color')).toBe(
      CSS_THEMES.dark['--sidebar-readable-text-color'],
    );
    expect(rootStyle.getPropertyValue('--sidebar-readable-muted-text-color')).toBe(
      CSS_THEMES.dark['--sidebar-readable-muted-text-color'],
    );
    expect(rootStyle.getPropertyValue('--sidebar-readable-text-shadow')).toContain(
      '--sidebar-readable-shadow-core',
    );
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
