import type { ITerminalOptions } from '@xterm/xterm';
import { $currentSettings, $windowsBuildNumber } from '../../stores';
import {
  getEffectiveXtermTheme,
  resolveEffectiveTerminalMinimumContrastRatio,
} from '../theming/themes';
import {
  buildTerminalFontStack,
  DEFAULT_TERMINAL_FONT_WEIGHT,
  DEFAULT_TERMINAL_FONT_WEIGHT_BOLD,
  DEFAULT_TERMINAL_LETTER_SPACING,
  DEFAULT_TERMINAL_LINE_HEIGHT,
  getConfiguredTerminalFontFamily,
  normalizeTerminalFontWeight,
  normalizeTerminalLetterSpacing,
} from './fontConfig';
import { getEffectiveTerminalFontSize } from './fontSize';

type TerminalFontWeight = NonNullable<ITerminalOptions['fontWeight']>;

function resolveWindowsPtyOptions(): ITerminalOptions['windowsPty'] | undefined {
  const windowsBuildNumber = $windowsBuildNumber.get();
  const isWindows = /Windows|Win32|Win64/i.test(navigator.userAgent);
  if (windowsBuildNumber !== null) {
    return {
      backend: 'conpty',
      buildNumber: windowsBuildNumber,
    };
  }

  if (!isWindows) {
    return undefined;
  }

  return {
    backend: 'conpty',
    buildNumber: 19041,
  };
}

function resolveTerminalTypographyOptions(
  currentSettings: ReturnType<typeof $currentSettings.get>,
): Pick<
  ITerminalOptions,
  'fontFamily' | 'fontSize' | 'fontWeight' | 'fontWeightBold' | 'letterSpacing' | 'lineHeight'
> {
  const baseFontSize = currentSettings?.fontSize ?? 14;
  const fontSize = getEffectiveTerminalFontSize(baseFontSize);
  const fontFamily = getConfiguredTerminalFontFamily();
  const lineHeight = currentSettings?.lineHeight ?? DEFAULT_TERMINAL_LINE_HEIGHT;
  const letterSpacing = normalizeTerminalLetterSpacing(
    currentSettings?.letterSpacing ?? DEFAULT_TERMINAL_LETTER_SPACING,
  );
  const fontWeight = normalizeTerminalFontWeight(
    currentSettings?.fontWeight,
    DEFAULT_TERMINAL_FONT_WEIGHT,
  ) as TerminalFontWeight;
  const fontWeightBold = normalizeTerminalFontWeight(
    currentSettings?.fontWeightBold,
    DEFAULT_TERMINAL_FONT_WEIGHT_BOLD,
  ) as TerminalFontWeight;

  return {
    fontFamily: buildTerminalFontStack(fontFamily),
    fontSize,
    letterSpacing,
    lineHeight,
    fontWeight,
    fontWeightBold,
  };
}

function resolveGeneralTerminalOptions(
  currentSettings: ReturnType<typeof $currentSettings.get>,
): Pick<
  ITerminalOptions,
  | 'allowProposedApi'
  | 'allowTransparency'
  | 'cursorBlink'
  | 'cursorInactiveStyle'
  | 'cursorStyle'
  | 'customGlyphs'
  | 'minimumContrastRatio'
  | 'rescaleOverlappingGlyphs'
  | 'scrollback'
  | 'smoothScrollDuration'
  | 'theme'
  | 'windowsPty'
> {
  const options: Pick<
    ITerminalOptions,
    | 'allowProposedApi'
    | 'allowTransparency'
    | 'cursorBlink'
    | 'cursorInactiveStyle'
    | 'cursorStyle'
    | 'customGlyphs'
    | 'minimumContrastRatio'
    | 'rescaleOverlappingGlyphs'
    | 'scrollback'
    | 'smoothScrollDuration'
    | 'theme'
    | 'windowsPty'
  > = {
    cursorBlink: currentSettings?.cursorBlink ?? false,
    cursorStyle: currentSettings?.cursorStyle ?? 'block',
    cursorInactiveStyle: currentSettings?.cursorInactiveStyle ?? 'none',
    scrollback: currentSettings?.scrollbackLines ?? 2000,
    smoothScrollDuration: currentSettings?.smoothScrolling ? 50 : 0,
    allowProposedApi: true,
    allowTransparency: true,
    customGlyphs: currentSettings?.customGlyphs ?? true,
    minimumContrastRatio: resolveEffectiveTerminalMinimumContrastRatio(currentSettings),
    rescaleOverlappingGlyphs: true,
    theme: getEffectiveXtermTheme(),
  };

  const windowsPty = resolveWindowsPtyOptions();
  if (windowsPty) {
    options.windowsPty = windowsPty;
  }

  return options;
}

export function getTerminalOptions(): ITerminalOptions {
  const currentSettings = $currentSettings.get();
  return {
    ...resolveTerminalTypographyOptions(currentSettings),
    ...resolveGeneralTerminalOptions(currentSettings),
  };
}
