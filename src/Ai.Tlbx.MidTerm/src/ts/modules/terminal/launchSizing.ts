import { dom } from '../../state';
import {
  getConfiguredTerminalFontFamily,
  normalizeTerminalFontWeight,
  normalizeTerminalLetterSpacing,
} from './fontConfig';
import { getEffectiveTerminalFontSize } from './fontSize';
import { calculateOptimalDimensions } from './scaling';

interface LaunchSizingSettings {
  defaultCols?: number;
  defaultRows?: number;
  fontSize?: number;
  lineHeight?: number;
  letterSpacing?: number;
  fontWeight?: string;
  fontWeightBold?: string;
}

function resolveDefaultLaunchDimensions(settings: LaunchSizingSettings | null | undefined): {
  cols: number;
  rows: number;
} {
  return {
    cols: settings?.defaultCols ?? 120,
    rows: settings?.defaultRows ?? 30,
  };
}

function canMeasureLaunchDimensions(): boolean {
  return !!dom.terminalsArea;
}

export async function resolveLaunchDimensions(
  settings: LaunchSizingSettings | null | undefined,
  logPrefix: string,
): Promise<{ cols: number; rows: number }> {
  const defaults = resolveDefaultLaunchDimensions(settings);
  const terminalsArea = dom.terminalsArea;

  if (!canMeasureLaunchDimensions() || !terminalsArea) {
    return defaults;
  }

  const dims = await calculateOptimalDimensions(
    terminalsArea,
    getEffectiveTerminalFontSize(settings?.fontSize ?? 14),
    getConfiguredTerminalFontFamily(),
    settings?.lineHeight ?? 1,
    normalizeTerminalLetterSpacing(settings?.letterSpacing ?? 0),
    normalizeTerminalFontWeight(settings?.fontWeight, 'normal'),
    normalizeTerminalFontWeight(settings?.fontWeightBold, 'bold'),
    `${logPrefix}-${crypto.randomUUID().slice(0, 8)}`,
  );

  return dims ?? defaults;
}
