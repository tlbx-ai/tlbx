/**
 * CSS Theme Palettes
 *
 * Complete CSS variable palettes for all UI themes.
 * Applied to :root via style.setProperty() to theme
 * the entire UI chrome (sidebar, settings, buttons, etc).
 */

import type { ThemeName } from '../../types';

export type CssThemePalette = Record<string, string>;

const dark: CssThemePalette = {
  '--bg-terminal': '#05050A',
  '--terminal-bg': '#05050A',
  '--bg-primary': '#0D0E14',
  '--bg-elevated': '#161821',
  '--bg-sidebar': '#05050A',
  '--bg-surface': '#242735',
  '--bg-input': '#242735',
  '--bg-dropdown': '#242735',
  '--bg-hover': '#2D3044',
  '--bg-active': '#363A50',
  '--bg-session-hover': '#1C1E2A',
  '--bg-session-active': '#1C1E2A',
  '--bg-settings': '#161821',
  '--bg-tertiary': '#05050A',

  '--border-color': '#1E202B',
  '--border-default': '#1E202B',
  '--border-subtle': '#282B3A',
  '--border-emphasis': '#3A3E52',

  '--text-primary': '#D4D7E8',
  '--text-terminal': '#E0E2F0',
  '--text-secondary': '#8B8FA6',
  '--text-muted': '#767B94',

  '--accent-blue': '#7BA2F7',
  '--accent-blue-hover': '#8FB5FF',
  '--accent-cyan': '#7DCFFF',
  '--accent-green': '#8FD694',
  '--accent-orange': '#F5A962',
  '--accent-red': '#F07A8D',
  '--accent-violet': '#9D8CFF',

  '--accent-gold': '#E8B44C',
  '--accent-gold-muted': '#C9A04A',
  '--cta-primary': '#E8B44C',
  '--cta-primary-muted': '#C9A04A',
  '--cta-primary-text': '#0D0E14',

  '--btn-primary': '#7BA2F7',
  '--btn-primary-hover': '#8FB5FF',
  '--btn-secondary': '#3A3E52',
  '--btn-secondary-hover': '#4A4F68',

  '--bg-success': '#152A20',
  '--border-success': '#2B5A42',
  '--bg-error': '#2A1519',
  '--border-error': '#5A3538',
  '--bg-warning': '#2A2215',
  '--border-warning': '#5A4A2A',
  '--accent-warning': '#F5A962',

  '--diag-exception': '#F07A8D',
  '--progress-bar': '#7BA2F7',
  '--progress-warning': '#F5A962',
  '--warning-badge': '#F5A962',

  '--accent-blue-08': 'rgba(123, 162, 247, 0.08)',
  '--accent-blue-10': 'rgba(123, 162, 247, 0.1)',
  '--accent-blue-15': 'rgba(123, 162, 247, 0.15)',
  '--accent-blue-25': 'rgba(123, 162, 247, 0.25)',
  '--accent-blue-40': 'rgba(123, 162, 247, 0.4)',

  '--accent-gold-10': 'rgba(232, 180, 76, 0.1)',
  '--accent-gold-15': 'rgba(232, 180, 76, 0.15)',
  '--accent-gold-25': 'rgba(232, 180, 76, 0.25)',
  '--accent-gold-40': 'rgba(232, 180, 76, 0.4)',
  '--cta-primary-25': 'rgba(232, 180, 76, 0.25)',
  '--cta-primary-40': 'rgba(232, 180, 76, 0.4)',

  '--accent-orange-08': 'rgba(245, 169, 98, 0.08)',
  '--accent-orange-10': 'rgba(245, 169, 98, 0.1)',
  '--accent-orange-15': 'rgba(245, 169, 98, 0.15)',

  '--accent-red-10': 'rgba(240, 122, 141, 0.1)',
  '--accent-red-15': 'rgba(240, 122, 141, 0.15)',

  '--accent-green-10': 'rgba(143, 214, 148, 0.1)',
  '--accent-green-15': 'rgba(143, 214, 148, 0.15)',

  '--accent-purple': '#9D8CFF',

  '--tool-yellow-bg': 'rgba(232, 180, 76, 0.1)',
  '--tool-yellow-border': 'rgba(232, 180, 76, 0.2)',
  '--tool-yellow-border-strong': 'rgba(232, 180, 76, 0.3)',
  '--tool-yellow-border-emphasis': 'rgba(232, 180, 76, 0.4)',
  '--tool-yellow-gradient':
    'linear-gradient(135deg, rgba(232, 180, 76, 0.15), rgba(201, 160, 74, 0.15))',
  '--tool-purple-bg': 'rgba(157, 140, 255, 0.08)',
  '--tool-purple-bg-light': 'rgba(157, 140, 255, 0.12)',
  '--tool-purple-border': 'rgba(157, 140, 255, 0.15)',
  '--tool-purple-border-strong': 'rgba(157, 140, 255, 0.35)',
  '--tool-purple-gradient':
    'linear-gradient(135deg, rgba(157, 140, 255, 0.12), rgba(123, 162, 247, 0.12))',

  '--shadow-color': 'rgba(0, 0, 0, 0.3)',
  '--shadow-color-md': 'rgba(0, 0, 0, 0.4)',
  '--shadow-color-lg': 'rgba(0, 0, 0, 0.5)',
  '--overlay-bg': 'rgba(0, 0, 0, 0.6)',
  '--overlay-bg-dark': 'rgba(0, 0, 0, 0.7)',

  '--white-03': 'rgba(255, 255, 255, 0.03)',
  '--white-60': 'rgba(255, 255, 255, 0.6)',

  '--logo-filter': 'none',
  '--text-on-accent': '#FFFFFF',
};

const light: CssThemePalette = {
  '--bg-terminal': '#F5F0E8',
  '--terminal-bg': '#F5F0E8',
  '--bg-primary': '#EAE2D8',
  '--bg-elevated': '#FEFCF9',
  '--bg-sidebar': '#F5F0E8',
  '--bg-surface': '#FEFCF9',
  '--bg-input': '#FEFCF9',
  '--bg-dropdown': '#FEFCF9',
  '--bg-hover': '#DDD4C8',
  '--bg-active': '#D5CBBD',
  '--bg-session-hover': '#DDD4C8',
  '--bg-session-active': '#D5CBBD',
  '--bg-settings': '#FEFCF9',
  '--bg-tertiary': '#F5F0E8',

  '--border-color': '#D2C8BA',
  '--border-default': '#D2C8BA',
  '--border-subtle': '#E2D9CE',
  '--border-emphasis': '#BAB0A0',

  '--text-primary': '#2E2720',
  '--text-terminal': '#2E2720',
  '--text-secondary': '#5C5044',
  '--text-muted': '#978B7E',
  '--sidebar-readable-text-color': '#2E2720',
  '--sidebar-readable-muted-text-color': '#5C5044',
  '--sidebar-readable-text-shadow': 'none',
  '--sidebar-readable-icon-shadow': 'none',

  '--accent-blue': '#A7694D',
  '--accent-blue-hover': '#935B40',
  '--accent-cyan': '#5B8F7D',
  '--accent-green': '#6B8F4A',
  '--accent-orange': '#D97908',
  '--accent-red': '#DC2828',
  '--accent-violet': '#8B5F9E',

  '--accent-gold': '#B6550B',
  '--accent-gold-muted': '#944210',
  '--cta-primary': '#A7694D',
  '--cta-primary-muted': '#935B40',
  '--cta-primary-text': '#FFFEFA',

  '--btn-primary': '#A7694D',
  '--btn-primary-hover': '#935B40',
  '--btn-secondary': '#D2C8BA',
  '--btn-secondary-hover': '#BAB0A0',

  '--bg-success': '#DDFCE5',
  '--border-success': '#88EFAA',
  '--bg-error': '#FEE3E1',
  '--border-error': '#FCA7A3',
  '--bg-warning': '#FEF3C5',
  '--border-warning': '#FCD44F',
  '--accent-warning': '#D97908',

  '--diag-exception': '#DC2828',
  '--progress-bar': '#A7694D',
  '--progress-warning': '#D97908',
  '--warning-badge': '#D97908',

  '--accent-blue-08': 'rgba(167, 105, 77, 0.08)',
  '--accent-blue-10': 'rgba(167, 105, 77, 0.1)',
  '--accent-blue-15': 'rgba(167, 105, 77, 0.15)',
  '--accent-blue-25': 'rgba(167, 105, 77, 0.25)',
  '--accent-blue-40': 'rgba(167, 105, 77, 0.4)',

  '--accent-gold-10': 'rgba(182, 85, 11, 0.1)',
  '--accent-gold-15': 'rgba(182, 85, 11, 0.15)',
  '--accent-gold-25': 'rgba(182, 85, 11, 0.25)',
  '--accent-gold-40': 'rgba(182, 85, 11, 0.4)',
  '--cta-primary-25': 'rgba(167, 105, 77, 0.25)',
  '--cta-primary-40': 'rgba(167, 105, 77, 0.4)',

  '--accent-orange-08': 'rgba(217, 121, 8, 0.08)',
  '--accent-orange-10': 'rgba(217, 121, 8, 0.1)',
  '--accent-orange-15': 'rgba(217, 121, 8, 0.15)',

  '--accent-red-10': 'rgba(220, 40, 40, 0.1)',
  '--accent-red-15': 'rgba(220, 40, 40, 0.15)',

  '--accent-green-10': 'rgba(107, 143, 74, 0.1)',
  '--accent-green-15': 'rgba(107, 143, 74, 0.15)',

  '--accent-purple': '#8B5F9E',

  '--tool-yellow-bg': 'rgba(182, 85, 11, 0.1)',
  '--tool-yellow-border': 'rgba(182, 85, 11, 0.2)',
  '--tool-yellow-border-strong': 'rgba(182, 85, 11, 0.3)',
  '--tool-yellow-border-emphasis': 'rgba(182, 85, 11, 0.4)',
  '--tool-yellow-gradient':
    'linear-gradient(135deg, rgba(182, 85, 11, 0.1), rgba(148, 66, 16, 0.1))',
  '--tool-purple-bg': 'rgba(139, 95, 158, 0.08)',
  '--tool-purple-bg-light': 'rgba(139, 95, 158, 0.12)',
  '--tool-purple-border': 'rgba(139, 95, 158, 0.15)',
  '--tool-purple-border-strong': 'rgba(139, 95, 158, 0.35)',
  '--tool-purple-gradient':
    'linear-gradient(135deg, rgba(139, 95, 158, 0.1), rgba(167, 105, 77, 0.1))',

  '--shadow-color': 'rgba(0, 0, 0, 0.08)',
  '--shadow-color-md': 'rgba(0, 0, 0, 0.12)',
  '--shadow-color-lg': 'rgba(0, 0, 0, 0.18)',
  '--overlay-bg': 'rgba(0, 0, 0, 0.3)',
  '--overlay-bg-dark': 'rgba(0, 0, 0, 0.4)',

  '--white-03': 'rgba(0, 0, 0, 0.03)',
  '--white-60': 'rgba(0, 0, 0, 0.45)',

  '--logo-filter': 'invert(1)',
  '--text-on-accent': '#FFFEFA',
};

const solarizedDark: CssThemePalette = {
  '--bg-terminal': '#002B36',
  '--terminal-bg': '#002B36',
  '--bg-primary': '#04313D',
  '--bg-elevated': '#073642',
  '--bg-sidebar': '#002B36',
  '--bg-surface': '#0A3F4C',
  '--bg-input': '#0A3F4C',
  '--bg-dropdown': '#0A3F4C',
  '--bg-hover': '#0D4A58',
  '--bg-active': '#115564',
  '--bg-session-hover': '#0D4A58',
  '--bg-session-active': '#115564',
  '--bg-settings': '#073642',
  '--bg-tertiary': '#002B36',

  '--border-color': '#0D4A58',
  '--border-default': '#0D4A58',
  '--border-subtle': '#0A3F4C',
  '--border-emphasis': '#2D7A8A',

  '--text-primary': '#93A1A1',
  '--text-terminal': '#839496',
  '--text-secondary': '#657B83',
  '--text-muted': '#586E75',
  '--sidebar-readable-text-color': '#D6E6E3',
  '--sidebar-readable-muted-text-color': '#B7CAC7',
  '--sidebar-readable-shadow-core': 'rgba(0, 25, 31, 0.92)',
  '--sidebar-readable-shadow-soft': 'rgba(0, 43, 54, 0.78)',
  '--sidebar-readable-shadow-wide': 'rgba(0, 43, 54, 0.58)',
  '--sidebar-readable-text-shadow':
    '0 0 2px var(--sidebar-readable-shadow-core), 0 1px 12px var(--sidebar-readable-shadow-soft), 0 2px 26px var(--sidebar-readable-shadow-wide)',
  '--sidebar-readable-icon-shadow':
    'drop-shadow(0 0 2px var(--sidebar-readable-shadow-core)) drop-shadow(0 1px 16px var(--sidebar-readable-shadow-soft)) drop-shadow(0 2px 34px var(--sidebar-readable-shadow-wide))',

  '--accent-blue': '#268BD2',
  '--accent-blue-hover': '#3A9BE0',
  '--accent-cyan': '#2AA198',
  '--accent-green': '#859900',
  '--accent-orange': '#CB4B16',
  '--accent-red': '#DC322F',
  '--accent-violet': '#6C71C4',

  '--accent-gold': '#B58900',
  '--accent-gold-muted': '#9A7500',
  '--cta-primary': '#B58900',
  '--cta-primary-muted': '#9A7500',
  '--cta-primary-text': '#0D0E14',

  '--btn-primary': '#268BD2',
  '--btn-primary-hover': '#3A9BE0',
  '--btn-secondary': '#0D4A58',
  '--btn-secondary-hover': '#115564',

  '--bg-success': '#0A2E1A',
  '--border-success': '#1A5A35',
  '--bg-error': '#2A0E0E',
  '--border-error': '#5A2525',
  '--bg-warning': '#2A2005',
  '--border-warning': '#5A4510',
  '--accent-warning': '#B58900',

  '--diag-exception': '#DC322F',
  '--progress-bar': '#268BD2',
  '--progress-warning': '#B58900',
  '--warning-badge': '#CB4B16',

  '--accent-blue-08': 'rgba(38, 139, 210, 0.08)',
  '--accent-blue-10': 'rgba(38, 139, 210, 0.1)',
  '--accent-blue-15': 'rgba(38, 139, 210, 0.15)',
  '--accent-blue-25': 'rgba(38, 139, 210, 0.25)',
  '--accent-blue-40': 'rgba(38, 139, 210, 0.4)',

  '--accent-gold-10': 'rgba(181, 137, 0, 0.1)',
  '--accent-gold-15': 'rgba(181, 137, 0, 0.15)',
  '--accent-gold-25': 'rgba(181, 137, 0, 0.25)',
  '--accent-gold-40': 'rgba(181, 137, 0, 0.4)',
  '--cta-primary-25': 'rgba(181, 137, 0, 0.25)',
  '--cta-primary-40': 'rgba(181, 137, 0, 0.4)',

  '--accent-orange-08': 'rgba(203, 75, 22, 0.08)',
  '--accent-orange-10': 'rgba(203, 75, 22, 0.1)',
  '--accent-orange-15': 'rgba(203, 75, 22, 0.15)',

  '--accent-red-10': 'rgba(220, 50, 47, 0.1)',
  '--accent-red-15': 'rgba(220, 50, 47, 0.15)',

  '--accent-green-10': 'rgba(133, 153, 0, 0.1)',
  '--accent-green-15': 'rgba(133, 153, 0, 0.15)',

  '--accent-purple': '#6C71C4',

  '--tool-yellow-bg': 'rgba(181, 137, 0, 0.1)',
  '--tool-yellow-border': 'rgba(181, 137, 0, 0.2)',
  '--tool-yellow-border-strong': 'rgba(181, 137, 0, 0.3)',
  '--tool-yellow-border-emphasis': 'rgba(181, 137, 0, 0.4)',
  '--tool-yellow-gradient':
    'linear-gradient(135deg, rgba(181, 137, 0, 0.15), rgba(154, 117, 0, 0.15))',
  '--tool-purple-bg': 'rgba(108, 113, 196, 0.08)',
  '--tool-purple-bg-light': 'rgba(108, 113, 196, 0.12)',
  '--tool-purple-border': 'rgba(108, 113, 196, 0.15)',
  '--tool-purple-border-strong': 'rgba(108, 113, 196, 0.35)',
  '--tool-purple-gradient':
    'linear-gradient(135deg, rgba(108, 113, 196, 0.12), rgba(38, 139, 210, 0.12))',

  '--shadow-color': 'rgba(0, 0, 0, 0.3)',
  '--shadow-color-md': 'rgba(0, 0, 0, 0.4)',
  '--shadow-color-lg': 'rgba(0, 0, 0, 0.5)',
  '--overlay-bg': 'rgba(0, 0, 0, 0.6)',
  '--overlay-bg-dark': 'rgba(0, 0, 0, 0.7)',

  '--white-03': 'rgba(255, 255, 255, 0.03)',
  '--white-60': 'rgba(255, 255, 255, 0.6)',

  '--logo-filter': 'none',
  '--text-on-accent': '#FFFFFF',
};

const solarizedLight: CssThemePalette = {
  '--bg-terminal': '#FDF6E3',
  '--terminal-bg': '#FDF6E3',
  '--bg-primary': '#F7F0DC',
  '--bg-elevated': '#EEE8D5',
  '--bg-sidebar': '#FDF6E3',
  '--bg-surface': '#E6DFC8',
  '--bg-input': '#E6DFC8',
  '--bg-dropdown': '#E6DFC8',
  '--bg-hover': '#DDD6C1',
  '--bg-active': '#D3CCB7',
  '--bg-session-hover': '#DDD6C1',
  '--bg-session-active': '#D3CCB7',
  '--bg-settings': '#EEE8D5',
  '--bg-tertiary': '#FDF6E3',

  '--border-color': '#D3CCB7',
  '--border-default': '#D3CCB7',
  '--border-subtle': '#DDD6C1',
  '--border-emphasis': '#B8B2A0',

  '--text-primary': '#586E75',
  '--text-terminal': '#657B83',
  '--text-secondary': '#657B83',
  '--text-muted': '#93A1A1',
  '--sidebar-readable-text-color': '#586E75',
  '--sidebar-readable-muted-text-color': '#657B83',
  '--sidebar-readable-text-shadow': 'none',
  '--sidebar-readable-icon-shadow': 'none',

  '--accent-blue': '#268BD2',
  '--accent-blue-hover': '#1A7ABD',
  '--accent-cyan': '#2AA198',
  '--accent-green': '#859900',
  '--accent-orange': '#CB4B16',
  '--accent-red': '#DC322F',
  '--accent-violet': '#6C71C4',

  '--accent-gold': '#B58900',
  '--accent-gold-muted': '#9A7500',
  '--cta-primary': '#B58900',
  '--cta-primary-muted': '#9A7500',
  '--cta-primary-text': '#0D0E14',

  '--btn-primary': '#268BD2',
  '--btn-primary-hover': '#1A7ABD',
  '--btn-secondary': '#D3CCB7',
  '--btn-secondary-hover': '#C5BDA8',

  '--bg-success': '#E6F2E6',
  '--border-success': '#A8D8A8',
  '--bg-error': '#FCE4E4',
  '--border-error': '#F0AAAA',
  '--bg-warning': '#FDF2D0',
  '--border-warning': '#E8D48A',
  '--accent-warning': '#B58900',

  '--diag-exception': '#DC322F',
  '--progress-bar': '#268BD2',
  '--progress-warning': '#B58900',
  '--warning-badge': '#CB4B16',

  '--accent-blue-08': 'rgba(38, 139, 210, 0.08)',
  '--accent-blue-10': 'rgba(38, 139, 210, 0.1)',
  '--accent-blue-15': 'rgba(38, 139, 210, 0.15)',
  '--accent-blue-25': 'rgba(38, 139, 210, 0.25)',
  '--accent-blue-40': 'rgba(38, 139, 210, 0.4)',

  '--accent-gold-10': 'rgba(181, 137, 0, 0.1)',
  '--accent-gold-15': 'rgba(181, 137, 0, 0.15)',
  '--accent-gold-25': 'rgba(181, 137, 0, 0.25)',
  '--accent-gold-40': 'rgba(181, 137, 0, 0.4)',
  '--cta-primary-25': 'rgba(181, 137, 0, 0.25)',
  '--cta-primary-40': 'rgba(181, 137, 0, 0.4)',

  '--accent-orange-08': 'rgba(203, 75, 22, 0.08)',
  '--accent-orange-10': 'rgba(203, 75, 22, 0.1)',
  '--accent-orange-15': 'rgba(203, 75, 22, 0.15)',

  '--accent-red-10': 'rgba(220, 50, 47, 0.1)',
  '--accent-red-15': 'rgba(220, 50, 47, 0.15)',

  '--accent-green-10': 'rgba(133, 153, 0, 0.1)',
  '--accent-green-15': 'rgba(133, 153, 0, 0.15)',

  '--accent-purple': '#6C71C4',

  '--tool-yellow-bg': 'rgba(181, 137, 0, 0.1)',
  '--tool-yellow-border': 'rgba(181, 137, 0, 0.2)',
  '--tool-yellow-border-strong': 'rgba(181, 137, 0, 0.3)',
  '--tool-yellow-border-emphasis': 'rgba(181, 137, 0, 0.4)',
  '--tool-yellow-gradient':
    'linear-gradient(135deg, rgba(181, 137, 0, 0.1), rgba(154, 117, 0, 0.1))',
  '--tool-purple-bg': 'rgba(108, 113, 196, 0.08)',
  '--tool-purple-bg-light': 'rgba(108, 113, 196, 0.12)',
  '--tool-purple-border': 'rgba(108, 113, 196, 0.15)',
  '--tool-purple-border-strong': 'rgba(108, 113, 196, 0.35)',
  '--tool-purple-gradient':
    'linear-gradient(135deg, rgba(108, 113, 196, 0.1), rgba(38, 139, 210, 0.1))',

  '--shadow-color': 'rgba(0, 0, 0, 0.06)',
  '--shadow-color-md': 'rgba(0, 0, 0, 0.1)',
  '--shadow-color-lg': 'rgba(0, 0, 0, 0.15)',
  '--overlay-bg': 'rgba(0, 0, 0, 0.3)',
  '--overlay-bg-dark': 'rgba(0, 0, 0, 0.4)',

  '--white-03': 'rgba(0, 0, 0, 0.03)',
  '--white-60': 'rgba(0, 0, 0, 0.45)',

  '--logo-filter': 'invert(1)',
  '--text-on-accent': '#FFFDF6',
};

export const CSS_THEMES: Record<string, CssThemePalette> = {
  dark,
  light,
  solarizedDark,
  solarizedLight,
};

export function applyCssTheme(themeName: ThemeName): void {
  const root = document.documentElement;
  const palette = CSS_THEMES[themeName];
  if (palette) {
    const nativeColorScheme = getNativeColorScheme(themeName);
    for (const [prop, value] of Object.entries(palette)) {
      root.style.setProperty(prop, value);
    }
    root.style.colorScheme = nativeColorScheme;
    root.dataset.nativeColorScheme = nativeColorScheme;
    updateThemeColor(palette['--bg-primary'] ?? '#0D0E14');
  } else {
    // Dark is the base theme in app.css — clear any overrides
    const anyPalette = Object.values(CSS_THEMES)[0];
    if (anyPalette) {
      for (const prop of Object.keys(anyPalette)) {
        root.style.removeProperty(prop);
      }
    }
    root.style.colorScheme = 'dark';
    root.dataset.nativeColorScheme = 'dark';
    updateThemeColor('#0D0E14');
  }
}

export function getCssThemePalette(themeName: ThemeName): CssThemePalette {
  const palette = CSS_THEMES[themeName];
  if (palette) {
    return palette;
  }

  const fallback = CSS_THEMES['dark'];
  if (!fallback) {
    throw new Error("Theme palette 'dark' not found");
  }

  return fallback;
}

export function getNativeColorScheme(themeName: ThemeName): 'light' | 'dark' {
  return themeName === 'light' || themeName === 'solarizedLight' ? 'light' : 'dark';
}

function updateThemeColor(color: string): void {
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) {
    meta.setAttribute('content', color);
  }
}
