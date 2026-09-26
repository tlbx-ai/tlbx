import { describe, expect, it } from 'vitest';

import { normalizeTerminalFontWeight, normalizeTerminalLetterSpacing } from './fontConfig';

describe('fontConfig', () => {
  it('preserves numeric font weights while keeping named fallbacks canonical', () => {
    expect(normalizeTerminalFontWeight('100')).toBe('100');
    expect(normalizeTerminalFontWeight('500')).toBe('500');
    expect(normalizeTerminalFontWeight('700')).toBe('700');
    expect(normalizeTerminalFontWeight('900')).toBe('900');
    expect(normalizeTerminalFontWeight(' bold ')).toBe('bold');
    expect(normalizeTerminalFontWeight('invalid')).toBe('normal');
  });

  it('preserves fractional letter spacing within xterm bounds', () => {
    expect(normalizeTerminalLetterSpacing(0.49)).toBe(0.49);
    expect(normalizeTerminalLetterSpacing(0.5)).toBe(0.5);
    expect(normalizeTerminalLetterSpacing(-2.4)).toBe(-2);
    expect(normalizeTerminalLetterSpacing(10.2)).toBe(10);
  });
});
