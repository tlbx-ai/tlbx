import { describe, expect, it } from 'vitest';

import type { MidTermSettingsPublic } from '../../api/types';
import { shouldUseWebglRenderer } from './webglSupport';

function createSettings(
  partial: Partial<
    Pick<
      MidTermSettingsPublic,
      | 'terminalTransparency'
      | 'terminalCellBackgroundTransparency'
      | 'uiTransparency'
      | 'useWebGL'
    >
  >,
): MidTermSettingsPublic {
  return {
    terminalTransparency: 0,
    terminalCellBackgroundTransparency: 0,
    uiTransparency: 0,
    useWebGL: true,
    ...partial,
  } as MidTermSettingsPublic;
}

describe('webglSupport', () => {
  it('keeps WebGL enabled by default', () => {
    expect(shouldUseWebglRenderer(createSettings({}))).toBe(true);
  });

  it('honors the explicit WebGL toggle', () => {
    expect(shouldUseWebglRenderer(createSettings({ useWebGL: false }))).toBe(false);
  });

  it('keeps WebGL enabled while terminal text brightness boost is active', () => {
    expect(
      shouldUseWebglRenderer({
        ...createSettings({}),
        terminalThemeLightnessBoost: 50,
      } as MidTermSettingsPublic),
    ).toBe(true);
  });

  it('keeps WebGL enabled when only terminal-controlled cell backgrounds are transparent', () => {
    expect(
      shouldUseWebglRenderer(
        createSettings({
          terminalTransparency: 0,
          terminalCellBackgroundTransparency: 35,
        }),
      ),
    ).toBe(true);
  });

  it('keeps WebGL enabled when terminal-controlled cell backgrounds inherit terminal transparency', () => {
    expect(
      shouldUseWebglRenderer({
        ...createSettings({}),
        terminalTransparency: 35,
        terminalCellBackgroundTransparency: 35,
        uiTransparency: 25,
        backgroundImageEnabled: true,
        backgroundImageFileName: 'wallpaper.png',
      } as MidTermSettingsPublic),
    ).toBe(true);
  });

  it('keeps WebGL enabled when only the terminal surface is transparent', () => {
    expect(
      shouldUseWebglRenderer(
        createSettings({
          terminalTransparency: 35,
          terminalCellBackgroundTransparency: 0,
        }),
      ),
    ).toBe(true);
  });
});
