import type { MidTermSettingsPublic } from '../../types';
import { getEffectiveTerminalCellBackgroundAlpha } from '../theming/themes';

const DEFAULT_TERMINAL_CELL_BACKGROUND_ALPHA = 1;

type MidTermWindow = Window &
  typeof globalThis & {
    __MIDTERM_XTERM_WEBGL_CELL_BG_ALPHA__?: number;
  };

export function getWebglTerminalCellBackgroundAlpha(
  settings: MidTermSettingsPublic | null | undefined,
): number {
  return getEffectiveTerminalCellBackgroundAlpha(settings ?? null);
}

export function syncWebglTerminalCellBackgroundAlpha(
  settings: MidTermSettingsPublic | null | undefined,
): void {
  if (typeof window === 'undefined') {
    return;
  }

  window.__MIDTERM_XTERM_WEBGL_CELL_BG_ALPHA__ = getWebglTerminalCellBackgroundAlpha(settings);
}

export function getWindowWebglTerminalCellBackgroundAlpha(win: Window | null | undefined): number {
  const alpha = (win as MidTermWindow | null | undefined)?.__MIDTERM_XTERM_WEBGL_CELL_BG_ALPHA__;
  if (typeof alpha !== 'number' || !Number.isFinite(alpha)) {
    return DEFAULT_TERMINAL_CELL_BACKGROUND_ALPHA;
  }

  return Math.min(1, Math.max(0, alpha));
}
