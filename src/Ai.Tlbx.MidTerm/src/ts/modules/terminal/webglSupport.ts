import type { MidTermSettingsPublic } from '../../types';

export function shouldUseWebglRenderer(
  settings: MidTermSettingsPublic | null | undefined,
): boolean {
  if (settings?.useWebGL === false) {
    return false;
  }

  if ((settings?.terminalThemeLightnessBoost ?? 0) > 0) {
    return false;
  }

  return true;
}
