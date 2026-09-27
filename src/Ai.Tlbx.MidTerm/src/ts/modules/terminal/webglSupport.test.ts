import { describe, expect, it } from 'vitest';

import type { MidTermSettingsPublic } from '../../api/types';
import { shouldOwnWebglContext, shouldUseWebglRenderer } from './webglSupport';

function createSettings(
  partial: Partial<
    Pick<
      MidTermSettingsPublic,
      'terminalTransparency' | 'terminalCellBackgroundTransparency' | 'uiTransparency' | 'useWebGL'
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
  it('honors the explicit WebGL toggle', () => {
    expect(shouldUseWebglRenderer(createSettings({ useWebGL: false }))).toBe(false);
  });
});

describe('WebGL context ownership', () => {
  it('keeps unmanaged auxiliary terminals independent from session visibility', () => {
    expect(shouldOwnWebglContext(false, true, false)).toBe(true);
  });

  it('allows session terminals before the first visibility synchronization', () => {
    expect(shouldOwnWebglContext(true, false, false)).toBe(true);
  });

  it('keeps WebGL only for visible priority sessions after synchronization', () => {
    expect(shouldOwnWebglContext(true, true, true)).toBe(true);
    expect(shouldOwnWebglContext(true, true, false)).toBe(false);
  });
});
