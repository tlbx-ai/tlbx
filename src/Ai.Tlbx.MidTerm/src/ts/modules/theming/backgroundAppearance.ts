/**
 * Background Appearance Module
 *
 * Applies wallpaper and pane transparency without fading text content.
 */

import type { MidTermSettingsPublic } from '../../types';
import { getCssThemePalette } from './cssThemes';
import {
  isMobileBackgroundSuppressed,
  isMobilePresentationContext,
  shouldRenderBackgroundImage,
} from './backgroundVisibility';
import { getTerminalThemeByName } from './terminalColorSchemes';

const UI_BACKGROUND_VARIABLES: Array<{ name: string; boost?: number }> = [
  { name: '--bg-primary', boost: 0.16 },
  { name: '--bg-elevated', boost: 0.22 },
  { name: '--bg-sidebar', boost: 0.22 },
  { name: '--bg-surface', boost: 0.28 },
  { name: '--bg-input', boost: 0.28 },
  { name: '--bg-dropdown', boost: 0.28 },
  { name: '--bg-hover', boost: 0.34 },
  { name: '--bg-active', boost: 0.4 },
  { name: '--bg-session-hover', boost: 0.32 },
  { name: '--bg-session-active', boost: 0.38 },
  { name: '--bg-settings', boost: 0.22 },
  { name: '--bg-tertiary', boost: 0.22 },
];

const OPAQUE_SURFACE_VARIABLES: Array<{ name: string; source: string }> = [
  { name: '--bg-primary-opaque', source: '--bg-primary' },
  { name: '--bg-elevated-opaque', source: '--bg-elevated' },
  { name: '--bg-sidebar-opaque', source: '--bg-sidebar' },
  { name: '--bg-settings-opaque', source: '--bg-settings' },
  { name: '--bg-dropdown-opaque', source: '--bg-dropdown' },
  { name: '--bg-session-hover-opaque', source: '--bg-session-hover' },
  { name: '--bg-session-active-opaque', source: '--bg-session-active' },
  { name: '--bg-hover-opaque', source: '--bg-hover' },
  { name: '--bg-active-opaque', source: '--bg-active' },
];

const DERIVED_BACKGROUND_VARIABLES: Array<{
  name: string;
  source: string;
  mode: 'ui' | 'terminal';
  response?: number;
}> = [
  { name: '--terminal-canvas-background', source: '--bg-terminal', mode: 'terminal' },
  {
    name: '--command-bay-control-background',
    source: '--bg-terminal',
    mode: 'terminal',
    response: 0.2,
  },
  { name: '--terminal-ui-background', source: '--bg-terminal', mode: 'ui' },
  { name: '--app-chrome-background', source: '--bg-terminal', mode: 'ui', response: 0.25 },
  { name: '--web-preview-pane-background', source: '--bg-primary', mode: 'ui', response: 0.15 },
  {
    name: '--web-preview-pane-chrome-background',
    source: '--bg-elevated',
    mode: 'ui',
    response: 0.15,
  },
  {
    name: '--web-preview-pane-surface-background',
    source: '--bg-surface',
    mode: 'ui',
    response: 0.15,
  },
  { name: '--text-input-background', source: '--bg-input', mode: 'ui', response: 0.2 },
  {
    name: '--sidebar-item-hover-background',
    source: '--bg-session-hover',
    mode: 'ui',
    response: 0.6,
  },
  {
    name: '--sidebar-item-active-background',
    source: '--bg-session-active',
    mode: 'ui',
    response: 0.6,
  },
];

const TERMINAL_SCHEME_BACKGROUND_VARIABLES = new Set([
  '--terminal-canvas-background',
  '--command-bay-control-background',
]);
const BACKGROUND_KEN_BURNS_MIN_SCALE = 1.5;
const BACKGROUND_KEN_BURNS_MAX_SCALE = 3;
const BACKGROUND_KEN_BURNS_MAX_SPEED_PX_PER_SECOND = 120;
const BACKGROUND_KEN_BURNS_REFERENCE_SIZE_PX = 720;
const BACKGROUND_KEN_BURNS_PATH_MULTIPLIER = Math.PI * 2 * 0.46;
const BACKGROUND_KEN_BURNS_PAN_X_FACTOR = 0.24;
const BACKGROUND_KEN_BURNS_PAN_Y_FACTOR = 0.16;

interface RgbColor {
  r: number;
  g: number;
  b: number;
}

export function getBackgroundImageUrl(revision: number): string {
  return `/api/settings/background-image?v=${encodeURIComponent(`${revision}`)}`;
}

export function applyBackgroundAppearance(settings: MidTermSettingsPublic): void {
  const root = document.documentElement;
  const palette = getCssThemePalette(settings.theme);
  const terminalBackgroundColor = getTerminalBackgroundColor(settings, palette);
  const mobilePresentation = isMobilePresentationContext();
  const uiTransparency = mobilePresentation ? 0 : clamp(settings.uiTransparency, 0, 100);
  const terminalTransparency = mobilePresentation
    ? 0
    : clamp(settings.terminalTransparency ?? settings.uiTransparency, 0, 100);
  const uiBaseAlpha = Math.max(0, 1 - uiTransparency / 100);

  for (const variable of OPAQUE_SURFACE_VARIABLES) {
    const value = palette[variable.source];
    if (!value) {
      continue;
    }

    root.style.setProperty(variable.name, value);
  }

  for (const variable of UI_BACKGROUND_VARIABLES) {
    const value = palette[variable.name];
    const rgb = parseColor(value);
    if (!rgb) {
      continue;
    }

    const alpha = clamp(uiBaseAlpha * (1 + (variable.boost ?? 0)), 0, 1);
    root.style.setProperty(variable.name, toRgba(rgb, alpha));
  }

  for (const variable of DERIVED_BACKGROUND_VARIABLES) {
    const value = TERMINAL_SCHEME_BACKGROUND_VARIABLES.has(variable.name)
      ? terminalBackgroundColor
      : palette[variable.source];
    const rgb = parseColor(value);
    if (!rgb) {
      continue;
    }

    const transparency = variable.mode === 'terminal' ? terminalTransparency : uiTransparency;
    root.style.setProperty(
      variable.name,
      toRgba(rgb, transparencyToAlpha(transparency, variable.response ?? 1)),
    );
  }

  const hasImage = shouldRenderBackgroundImage(settings);

  root.style.setProperty(
    '--app-background-image',
    hasImage ? `url("${getBackgroundImageUrl(settings.backgroundImageRevision)}")` : 'none',
  );
  root.style.setProperty('--app-background-size', 'cover');
  root.style.setProperty('--app-background-repeat', 'no-repeat');
  root.style.setProperty('--app-background-position', 'center center');
  syncBackgroundKenBurnsEffect(root, settings, hasImage);
  document.body.classList.toggle('has-app-background', hasImage);
  document.body.classList.toggle(
    'hide-app-background-on-mobile',
    isMobileBackgroundSuppressed(settings),
  );
  document.body.classList.toggle('opaque-terminal-surfaces', terminalTransparency === 0);
}

function getTerminalBackgroundColor(
  settings: MidTermSettingsPublic,
  palette: Record<string, string>,
): string {
  const colorScheme = settings.terminalColorScheme;
  const themeName = colorScheme === 'auto' ? settings.theme : colorScheme;
  const terminalTheme = getTerminalThemeByName(settings, themeName);
  if (terminalTheme) {
    return terminalTheme.background;
  }

  const fallbackBackground = palette['--bg-terminal'];
  return fallbackBackground || '#000000';
}

function parseColor(value: string | undefined): RgbColor | null {
  if (!value) return null;

  const trimmed = value.trim();
  if (trimmed.startsWith('#')) {
    return parseHexColor(trimmed);
  }

  const rgbMatch = trimmed.match(/rgba?\(([^)]+)\)/i);
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
  if (
    r === undefined ||
    g === undefined ||
    b === undefined ||
    ![r, g, b].every((part) => Number.isFinite(part))
  ) {
    return null;
  }

  return { r, g, b };
}

function parseHexColor(value: string): RgbColor | null {
  const hex = value.slice(1);
  if (hex.length === 3) {
    const [r, g, b] = hex.split('');
    if (!r || !g || !b) return null;
    return {
      r: Number.parseInt(r + r, 16),
      g: Number.parseInt(g + g, 16),
      b: Number.parseInt(b + b, 16),
    };
  }

  if (hex.length === 6 || hex.length === 8) {
    return {
      r: Number.parseInt(hex.slice(0, 2), 16),
      g: Number.parseInt(hex.slice(2, 4), 16),
      b: Number.parseInt(hex.slice(4, 6), 16),
    };
  }

  return null;
}

function toRgba(color: RgbColor, alpha: number): string {
  return `rgba(${Math.round(color.r)}, ${Math.round(color.g)}, ${Math.round(color.b)}, ${alpha.toFixed(3)})`;
}

function transparencyToAlpha(transparency: number, response: number): number {
  return clamp(1 - (clamp(transparency, 0, 100) / 100) * response, 0, 1);
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}

function syncBackgroundKenBurnsEffect(
  root: HTMLElement,
  settings: MidTermSettingsPublic,
  hasImage: boolean,
): void {
  const enabled = hasImage && settings.backgroundKenBurnsEnabled;
  const scale = enabled
    ? clamp(
        settings.backgroundKenBurnsZoomPercent / 100,
        BACKGROUND_KEN_BURNS_MIN_SCALE,
        BACKGROUND_KEN_BURNS_MAX_SCALE,
      )
    : 1;
  const speedPxPerSecond = clamp(
    settings.backgroundKenBurnsSpeedPxPerSecond,
    0,
    BACKGROUND_KEN_BURNS_MAX_SPEED_PX_PER_SECOND,
  );
  const overflow = Math.max(0, scale - 1);
  const panXPercent = overflow * BACKGROUND_KEN_BURNS_PAN_X_FACTOR * 100;
  const panYPercent = overflow * BACKGROUND_KEN_BURNS_PAN_Y_FACTOR * 100;

  root.style.setProperty(
    '--app-background-transform',
    `translate3d(0px, 0px, 0) scale(${scale.toFixed(3)})`,
  );
  root.style.setProperty('--app-background-ken-burns-scale', scale.toFixed(3));
  root.style.setProperty('--app-background-ken-burns-pan-x', `${panXPercent.toFixed(3)}%`);
  root.style.setProperty('--app-background-ken-burns-pan-y', `${panYPercent.toFixed(3)}%`);

  if (!enabled || speedPxPerSecond <= 0) {
    root.style.setProperty('--app-background-animation', 'none');
    return;
  }

  root.style.setProperty(
    '--app-background-animation',
    `midterm-app-background-ken-burns ${computeBackgroundKenBurnsDurationSeconds(scale, speedPxPerSecond).toFixed(3)}s linear infinite`,
  );
}

function computeBackgroundKenBurnsDurationSeconds(scale: number, speedPxPerSecond: number): number {
  if (speedPxPerSecond <= 0) {
    return 0;
  }

  const travelDistancePx =
    getBackgroundKenBurnsReferenceSizePx() *
    Math.max(0, scale - 1) *
    BACKGROUND_KEN_BURNS_PATH_MULTIPLIER;
  return clamp(travelDistancePx / speedPxPerSecond, 0.1, 86400);
}

function getBackgroundKenBurnsReferenceSizePx(): number {
  return BACKGROUND_KEN_BURNS_REFERENCE_SIZE_PX;
}
