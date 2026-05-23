/**
 * Theming Module
 *
 * Theme definitions and application to xterm.js terminals.
 */

import type { MidTermSettingsPublic, ThemeName, TerminalTheme } from '../../types';
import { THEMES } from '../../constants';
import { sessionTerminals } from '../../state';
import { $currentSettings } from '../../stores';
import { setCookie } from '../../utils';
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
  style.textContent = buildDomAnsiOverrideCss(foregroundTheme, ansiBackgroundTheme);

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
  const textLightnessBoost = clamp(settings?.terminalThemeLightnessBoost ?? 0, 0, 50);
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
  setCookie('mm-theme', themeName);
  applyXtermThemeToTerminals();
  applyCssTheme(themeName);
}

/**
 * Initialize theme from saved cookie
 */
export function initThemeFromCookie(): void {
  const savedTheme = document.cookie.match(/mm-theme=([^;]+)/)?.[1] as ThemeName | undefined;
  if (savedTheme && THEMES[savedTheme]) {
    applyCssTheme(savedTheme);
  }
}

function applyAnsiTransparency(theme: TerminalTheme, alpha: number): void {
  for (const key of ANSI_COLOR_KEYS) {
    const color = theme[key];
    if (typeof color === 'string' && color.length > 0) {
      theme[key] = withAlpha(color, alpha);
    }
  }
}

function buildDomAnsiOverrideCss(baseTheme: TerminalTheme, effectiveTheme: TerminalTheme): string {
  const defaultForegroundCss = buildDefaultForegroundOverrideCss(baseTheme);
  const ansiCss = ANSI_COLOR_KEYS.map((key, index) => {
    const opaque = baseTheme[key];
    const transparent = effectiveTheme[key];
    if (typeof opaque !== 'string' || typeof transparent !== 'string') {
      return '';
    }

    return [
      `.xterm .xterm-fg-${String(index)} { color: ${opaque}; }`,
      `.xterm .xterm-fg-${String(index)}.xterm-dim { color: ${withAlpha(opaque, 0.5)}; }`,
      `.xterm .xterm-bg-${String(index)} { background-color: ${transparent}; }`,
    ].join('\n');
  }).join('\n');

  return [defaultForegroundCss, ansiCss].filter(Boolean).join('\n');
}

function buildDefaultForegroundOverrideCss(theme: TerminalTheme): string {
  const foreground = theme.foreground;
  if (typeof foreground !== 'string' || foreground.length <= 0) {
    return '';
  }

  return [
    `.xterm .xterm-fg-256 { color: ${foreground}; }`,
    `.xterm .xterm-fg-256.xterm-dim { color: ${withAlpha(foreground, 0.5)}; }`,
    `.xterm .xterm-fg-257 { color: ${foreground}; }`,
    `.xterm .xterm-fg-257.xterm-dim { color: ${withAlpha(foreground, 0.5)}; }`,
  ].join('\n');
}

function syncWebglForegroundAnsiOverrides(theme: TerminalTheme, textLightnessBoost: number): void {
  if (typeof window === 'undefined') {
    return;
  }

  if (textLightnessBoost <= 0) {
    delete window.__MIDTERM_XTERM_WEBGL_FG_ANSI__;
    return;
  }

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

// --- Text lightness boost translation layer (HSL) ---
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

function rgbToHsl(r: number, g: number, b: number): { h: number; s: number; l: number } {
  r = clamp(r, 0, 255) / 255;
  g = clamp(g, 0, 255) / 255;
  b = clamp(b, 0, 255) / 255;
  const max = Math.max(r, g, b);
  const min = Math.min(r, g, b);
  let h = 0;
  let s = 0;
  const l = (max + min) / 2;
  if (max !== min) {
    const d = max - min;
    s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
    switch (max) {
      case r:
        h = (g - b) / d + (g < b ? 6 : 0);
        break;
      case g:
        h = (b - r) / d + 2;
        break;
      case b:
        h = (r - g) / d + 4;
        break;
    }
    h /= 6;
  }
  return { h: h * 360, s: s * 100, l: l * 100 };
}

function hslToRgb(h: number, s: number, l: number): { r: number; g: number; b: number } {
  h = (((h % 360) + 360) % 360) / 360;
  s = clamp(s, 0, 100) / 100;
  l = clamp(l, 0, 100) / 100;
  let r: number;
  let g: number;
  let b: number;
  if (s === 0) {
    r = g = b = l;
  } else {
    const hue2rgb = (p: number, q: number, t: number) => {
      if (t < 0) t += 1;
      if (t > 1) t -= 1;
      if (t < 1 / 6) return p + (q - p) * 6 * t;
      if (t < 1 / 2) return q;
      if (t < 2 / 3) return p + (q - p) * (2 / 3 - t) * 6;
      return p;
    };
    const q = l < 0.5 ? l * (1 + s) : l + s - l * s;
    const p = 2 * l - q;
    r = hue2rgb(p, q, h + 1 / 3);
    g = hue2rgb(p, q, h);
    b = hue2rgb(p, q, h - 1 / 3);
  }
  return {
    r: Math.round(clamp(r * 255, 0, 255)),
    g: Math.round(clamp(g * 255, 0, 255)),
    b: Math.round(clamp(b * 255, 0, 255)),
  };
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

function boostColorLightness(color: string, boost: number): string {
  if (boost <= 0 || !color) return color;
  const parsed = parseHexColor(color);
  if (!parsed) return color;
  const hsl = rgbToHsl(parsed.r, parsed.g, parsed.b);
  const boostRatio = clamp(boost, 0, 50) / 50;
  hsl.l = clamp(hsl.l + (100 - hsl.l) * boostRatio, 0, 100);
  const rgb = hslToRgb(hsl.h, hsl.s, hsl.l);
  let hex = rgbToHex(rgb.r, rgb.g, rgb.b);
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
      (out as unknown as Record<string, string>)[key] = boostColorLightness(val, boost);
    }
  }
  return out;
}
