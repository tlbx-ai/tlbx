/**
 * Theming Module
 *
 * Theme definitions and application to xterm.js terminals.
 */

import type { MidTermSettingsPublic, ThemeName, TerminalTheme } from '../../types';
import { THEMES } from '../../constants';
import { sessionTerminals } from '../../state';
import { $currentSettings } from '../../stores';
import { applyCssTheme } from './cssThemes';
import { isMobilePresentationContext, shouldRenderBackgroundImage } from './backgroundVisibility';
import { getTerminalThemeByName } from './terminalColorSchemes';

const ANSI_COLOR_KEYS = [
  'black',
  'red',
  'green',
  'yellow',
  'blue',
  'magenta',
  'cyan',
  'white',
  'brightBlack',
  'brightRed',
  'brightGreen',
  'brightYellow',
  'brightBlue',
  'brightMagenta',
  'brightCyan',
  'brightWhite',
] as const satisfies readonly (keyof TerminalTheme)[];

const TEXT_LIGHTNESS_KEYS = [
  'foreground',
  ...ANSI_COLOR_KEYS,
] as const satisfies readonly (keyof TerminalTheme)[];

const DOM_ANSI_OVERRIDE_STYLE_ID = 'midterm-xterm-ansi-overrides';
const LIGHT_TERMINAL_MINIMUM_CONTRAST_RATIO = 4.5;
const LIGHT_TERMINAL_BACKGROUND_LUMINANCE = 0.55;
const THEME_STORAGE_KEY = 'mm-theme';
const UI_THEME_NAMES = new Set<ThemeName>(['dark', 'light', 'solarizedDark', 'solarizedLight']);

type ResolvedTerminalTheme = {
  foregroundTheme: TerminalTheme;
  ansiBackgroundTheme: TerminalTheme;
  theme: TerminalTheme;
  terminalBackgroundAlpha: number;
  cellBackgroundAlpha: number;
  textLightnessBoost: number;
};

export function getEffectiveTerminalBackgroundAlpha(
  settings: MidTermSettingsPublic | null,
): number {
  if (isMobilePresentationContext()) {
    return 1;
  }

  return transparencyToAlpha(settings?.terminalTransparency ?? settings?.uiTransparency ?? 0);
}

export function getEffectiveTerminalCellBackgroundAlpha(
  settings: MidTermSettingsPublic | null,
): number {
  if (isMobilePresentationContext()) {
    return 1;
  }

  return transparencyToAlpha(
    settings?.terminalCellBackgroundTransparency ??
      settings?.terminalTransparency ??
      settings?.uiTransparency ??
      0,
  );
}

/**
 * Resolve the effective xterm color scheme.
 * If terminalColorScheme is 'auto', falls back to the UI theme.
 */
export function getEffectiveXtermTheme(): TerminalTheme {
  const settings = $currentSettings.get();
  syncEffectiveXtermThemeDomOverrides(settings);
  return getEffectiveXtermThemeForSettings(settings);
}

export function getEffectiveXtermThemeForSettings(
  settings: MidTermSettingsPublic | null,
): TerminalTheme {
  return resolveEffectiveXtermTheme(settings).theme;
}

export function resolveEffectiveTerminalMinimumContrastRatio(
  settings: MidTermSettingsPublic | null,
): number {
  const configured = clamp(settings?.minimumContrastRatio ?? 1, 1, 21);
  const theme = resolveEffectiveXtermTheme(settings).theme;
  const background = parseCssColor(theme.background);
  if (!background) {
    return configured;
  }

  const luminance = relativeLuminance(background);
  if (luminance < LIGHT_TERMINAL_BACKGROUND_LUMINANCE) {
    return configured;
  }

  return Math.max(configured, LIGHT_TERMINAL_MINIMUM_CONTRAST_RATIO);
}

export function syncEffectiveXtermThemeDomOverrides(settings: MidTermSettingsPublic | null): void {
  if (typeof document === 'undefined') {
    return;
  }

  const existing = document.getElementById(DOM_ANSI_OVERRIDE_STYLE_ID);
  const { foregroundTheme, ansiBackgroundTheme, cellBackgroundAlpha, textLightnessBoost } =
    resolveEffectiveXtermTheme(settings);
  syncWebglForegroundAnsiOverrides(foregroundTheme, textLightnessBoost);
  if (cellBackgroundAlpha >= 1 && textLightnessBoost <= 0) {
    existing?.remove();
    return;
  }

  const style = existing instanceof HTMLStyleElement ? existing : document.createElement('style');
  style.id = DOM_ANSI_OVERRIDE_STYLE_ID;
  style.textContent = buildDomAnsiOverrideCss(
    foregroundTheme,
    ansiBackgroundTheme,
    textLightnessBoost,
  );

  const parent = document.body;
  if (style.parentElement !== parent) {
    style.remove();
    parent.appendChild(style);
    return;
  }

  parent.appendChild(style);
}

function resolveEffectiveXtermTheme(settings: MidTermSettingsPublic | null): ResolvedTerminalTheme {
  const colorScheme = settings?.terminalColorScheme ?? 'auto';
  const key = colorScheme === 'auto' ? (settings?.theme ?? 'dark') : colorScheme;
  const fallbackTheme = THEMES['dark'];
  if (!fallbackTheme) {
    throw new Error("Theme 'dark' not found");
  }
  const baseTheme = getTerminalThemeByName(settings, key) ?? fallbackTheme;
  const textLightnessBoost = clamp(settings?.terminalThemeLightnessBoost ?? 0, 0, 100);
  const foregroundTheme =
    textLightnessBoost > 0
      ? applyTextLightnessBoostToTheme(baseTheme, textLightnessBoost)
      : baseTheme;
  const theme = buildEffectiveXtermTheme(baseTheme, foregroundTheme, textLightnessBoost);
  const ansiBackgroundTheme: TerminalTheme = Object.assign({}, baseTheme);
  const terminalBackgroundAlpha = getEffectiveTerminalBackgroundAlpha(settings);
  const cellBackgroundAlpha = getEffectiveTerminalCellBackgroundAlpha(settings);
  const hasWallpaper = shouldRenderBackgroundImage(settings);

  if (hasWallpaper || terminalBackgroundAlpha < 1) {
    theme.background = withAlpha(baseTheme.background, terminalBackgroundAlpha);
  }

  if (hasWallpaper || cellBackgroundAlpha < 1) {
    applyAnsiTransparency(theme, cellBackgroundAlpha);
    applyAnsiTransparency(ansiBackgroundTheme, cellBackgroundAlpha);
  }

  return {
    foregroundTheme,
    ansiBackgroundTheme,
    theme,
    terminalBackgroundAlpha,
    cellBackgroundAlpha,
    textLightnessBoost,
  };
}

function buildEffectiveXtermTheme(
  baseTheme: TerminalTheme,
  foregroundTheme: TerminalTheme,
  textLightnessBoost: number,
): TerminalTheme {
  const theme: TerminalTheme = Object.assign({}, baseTheme);
  if (textLightnessBoost > 0 && typeof foregroundTheme.foreground === 'string') {
    theme.foreground = foregroundTheme.foreground;
  }
  return theme;
}

/**
 * Get the current theme based on settings
 */
export function getCurrentTheme(): TerminalTheme {
  return getEffectiveXtermTheme();
}

/**
 * Apply the effective xterm theme to all terminals
 */
export function applyXtermThemeToTerminals(): void {
  const theme = getEffectiveXtermTheme();
  sessionTerminals.forEach((state) => {
    state.terminal.options.theme = theme;
  });
}

/**
 * Apply theme and persist to cookie
 */
export function setTheme(themeName: ThemeName): void {
  cacheTheme(themeName);
  applyXtermThemeToTerminals();
  applyCssTheme(themeName);
}

/**
 * Cache the theme per origin. Unlike cookies, localStorage includes the port,
 * so a source-dev instance cannot leak its theme into the stable supervisor.
 */
export function cacheTheme(themeName: ThemeName): void {
  try {
    window.localStorage.setItem(THEME_STORAGE_KEY, themeName);
  } catch {
    // Storage can be unavailable in hardened/private browser contexts.
  }
}

export function resolveInitialThemeName(savedTheme: string | null | undefined): ThemeName {
  return savedTheme && UI_THEME_NAMES.has(savedTheme as ThemeName)
    ? (savedTheme as ThemeName)
    : 'dark';
}

/** Initialize with a per-origin cached theme; first launch is always dark. */
export function initThemeFromBrowserCache(): void {
  let savedTheme: string | null = null;
  try {
    savedTheme = window.localStorage.getItem(THEME_STORAGE_KEY);
  } catch {
    // Fall through to the dark first-launch default.
  }

  applyCssTheme(resolveInitialThemeName(savedTheme));
}

function applyAnsiTransparency(theme: TerminalTheme, alpha: number): void {
  for (const key of ANSI_COLOR_KEYS) {
    const color = theme[key];
    if (typeof color === 'string' && color.length > 0) {
      theme[key] = withAlpha(color, alpha);
    }
  }
}

function buildDomAnsiOverrideCss(
  baseTheme: TerminalTheme,
  effectiveTheme: TerminalTheme,
  textLightnessBoost: number,
): string {
  const defaultForegroundCss = buildDefaultForegroundOverrideCss(baseTheme, textLightnessBoost);
  const ansiCss = ANSI_COLOR_KEYS.map((key, index) => {
    const opaque = baseTheme[key];
    const transparent = effectiveTheme[key];
    if (typeof opaque !== 'string' || typeof transparent !== 'string') {
      return '';
    }

    return [
      `.xterm .xterm-fg-${String(index)} { color: ${opaque}; }`,
      `.xterm .xterm-fg-${String(index)}.xterm-dim { color: ${textLightnessBoost > 0 ? opaque : withAlpha(opaque, 0.5)}; }`,
      `.xterm .xterm-bg-${String(index)} { background-color: ${transparent}; }`,
    ].join('\n');
  }).join('\n');

  return [defaultForegroundCss, ansiCss].filter(Boolean).join('\n');
}

function buildDefaultForegroundOverrideCss(
  theme: TerminalTheme,
  textLightnessBoost: number,
): string {
  const foreground = theme.foreground;
  if (typeof foreground !== 'string' || foreground.length <= 0) {
    return '';
  }

  return [
    `.xterm .xterm-fg-256 { color: ${foreground}; }`,
    `.xterm .xterm-fg-256.xterm-dim { color: ${textLightnessBoost > 0 ? foreground : withAlpha(foreground, 0.5)}; }`,
    `.xterm .xterm-fg-257 { color: ${foreground}; }`,
    `.xterm .xterm-fg-257.xterm-dim { color: ${textLightnessBoost > 0 ? foreground : withAlpha(foreground, 0.5)}; }`,
  ].join('\n');
}

function syncWebglForegroundAnsiOverrides(theme: TerminalTheme, textLightnessBoost: number): void {
  if (typeof window === 'undefined') {
    return;
  }

  if (textLightnessBoost <= 0) {
    delete window.__MIDTERM_XTERM_WEBGL_FG_ANSI__;
    delete window.__MIDTERM_XTERM_FG_BOOST__;
    return;
  }

  window.__MIDTERM_XTERM_FG_BOOST__ = textLightnessBoost;

  const packed = ANSI_COLOR_KEYS.map((key) => {
    const color = theme[key];
    return typeof color === 'string' ? colorToRgbaNumber(color) : null;
  });

  if (packed.some((color) => color === null)) {
    delete window.__MIDTERM_XTERM_WEBGL_FG_ANSI__;
    return;
  }

  window.__MIDTERM_XTERM_WEBGL_FG_ANSI__ = packed as number[];
}

function withAlpha(color: string, alpha: number): string {
  const trimmed = color.trim();
  if (trimmed.startsWith('#')) {
    const hex = trimmed.slice(1);
    const normalized =
      hex.length === 3
        ? hex
            .split('')
            .map((part) => part + part)
            .join('')
        : hex.slice(0, 6);

    if (normalized.length === 6) {
      const r = Number.parseInt(normalized.slice(0, 2), 16);
      const g = Number.parseInt(normalized.slice(2, 4), 16);
      const b = Number.parseInt(normalized.slice(4, 6), 16);
      return `rgba(${r}, ${g}, ${b}, ${alpha.toFixed(3)})`;
    }
  }

  const rgbMatch = trimmed.match(/rgba?\(([^)]+)\)/i);
  if (rgbMatch) {
    const parts = rgbMatch[1]?.split(',').map((part) => Number.parseFloat(part.trim()));
    if (parts && parts.length >= 3) {
      return `rgba(${parts[0]}, ${parts[1]}, ${parts[2]}, ${alpha.toFixed(3)})`;
    }
  }

  return color;
}

function colorToRgbaNumber(color: string): number | null {
  const parsed = parseHexColor(color);
  if (!parsed) {
    return null;
  }

  const alpha = parsed.a ?? 1;
  return (
    ((Math.round(clamp(parsed.r, 0, 255)) & 0xff) << 24) |
    ((Math.round(clamp(parsed.g, 0, 255)) & 0xff) << 16) |
    ((Math.round(clamp(parsed.b, 0, 255)) & 0xff) << 8) |
    (Math.round(clamp(alpha * 255, 0, 255)) & 0xff)
  );
}

function transparencyToAlpha(transparency: number): number {
  const clampedTransparency = Math.min(Math.max(transparency, 0), 100);
  return Math.max(0, 1 - clampedTransparency / 100);
}

// --- Text brightness boost translation layer ---
// Pure, no external deps. Applied only to foreground/text colors, never pane backgrounds.

function clamp(n: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, n));
}

function parseHexColor(hex: string): { r: number; g: number; b: number; a?: number } | null {
  if (!hex) return null;
  let h = hex.replace('#', '').trim().toLowerCase();
  if (h.length === 3) {
    h = h
      .split('')
      .map((c) => c + c)
      .join('');
  }
  if (h.length === 6 || h.length === 8) {
    const r = Number.parseInt(h.slice(0, 2), 16);
    const g = Number.parseInt(h.slice(2, 4), 16);
    const b = Number.parseInt(h.slice(4, 6), 16);
    if (h.length === 8) {
      const a = Number.parseInt(h.slice(6, 8), 16) / 255;
      return Number.isFinite(r) && Number.isFinite(g) && Number.isFinite(b) ? { r, g, b, a } : null;
    }
    return Number.isFinite(r) && Number.isFinite(g) && Number.isFinite(b) ? { r, g, b } : null;
  }
  return null;
}

function parseCssColor(color: string | undefined): { r: number; g: number; b: number } | null {
  if (!color) return null;
  const parsedHex = parseHexColor(color);
  if (parsedHex) {
    return parsedHex;
  }

  const rgbMatch = color.trim().match(/rgba?\(([^)]+)\)/i);
  if (!rgbMatch) {
    return null;
  }

  const parts = rgbMatch[1]?.split(',').map((part) => Number.parseFloat(part.trim()));
  if (!parts || parts.length < 3) {
    return null;
  }

  const r = parts[0];
  const g = parts[1];
  const b = parts[2];
  if (r === undefined || g === undefined || b === undefined) {
    return null;
  }

  if (!Number.isFinite(r) || !Number.isFinite(g) || !Number.isFinite(b)) {
    return null;
  }

  return {
    r: clamp(r, 0, 255),
    g: clamp(g, 0, 255),
    b: clamp(b, 0, 255),
  };
}

function relativeLuminance(color: { r: number; g: number; b: number }): number {
  const [r, g, b] = [color.r, color.g, color.b].map((channel) => {
    const value = channel / 255;
    return value <= 0.03928 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  });

  return 0.2126 * (r ?? 0) + 0.7152 * (g ?? 0) + 0.0722 * (b ?? 0);
}

function rgbToHex(r: number, g: number, b: number): string {
  return (
    '#' +
    [r, g, b]
      .map((v) =>
        Math.round(clamp(v, 0, 255))
          .toString(16)
          .padStart(2, '0'),
      )
      .join('')
  );
}

export function boostTerminalTextColor(color: string, boost: number): string {
  if (boost <= 0 || !color) return color;
  const parsed = parseHexColor(color);
  if (!parsed) return color;
  const amount = clamp(boost, 0, 100) / 100;
  let hex = rgbToHex(
    parsed.r + (255 - parsed.r) * amount,
    parsed.g + (255 - parsed.g) * amount,
    parsed.b + (255 - parsed.b) * amount,
  );
  if (parsed.a !== undefined) {
    const aHex = Math.round(clamp(parsed.a * 255, 0, 255))
      .toString(16)
      .padStart(2, '0');
    hex += aHex;
  }
  return hex;
}

function applyTextLightnessBoostToTheme(theme: TerminalTheme, boost: number): TerminalTheme {
  if (boost <= 0) return theme;
  const out = { ...theme } as TerminalTheme;
  for (const key of TEXT_LIGHTNESS_KEYS) {
    const val = out[key];
    if (typeof val === 'string' && val.trim().length > 0) {
      (out as unknown as Record<string, string>)[key] = boostTerminalTextColor(val, boost);
    }
  }
  return out;
}
