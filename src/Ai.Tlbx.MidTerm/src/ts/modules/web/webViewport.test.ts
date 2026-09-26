import { afterEach, describe, expect, it } from 'vitest';
import { applyStoredViewportToFrame } from './webViewport';
import { removeSessionState, setSessionViewport } from './webSessionState';

function frame() {
  const values = new Map<string, string>();
  const style = { setProperty: (key: string, value: string) => values.set(key, value) };
  return {
    values,
    element: {
      dataset: { previewFrameKey: 'background::default' },
      style,
    } as unknown as HTMLIFrameElement,
  };
}
afterEach(() => removeSessionState('background'));
describe('background preview dimensions', () => {
  it('gives a never-visible automation frame a real default viewport', () => {
    const f = frame();
    applyStoredViewportToFrame(f.element);
    expect(f.values.get('--preview-background-width')).toBe('1280px');
    expect(f.values.get('--preview-background-height')).toBe('720px');
  });
  it('uses the background preview viewport independently of the active session', () => {
    setSessionViewport('background', 'default', 390, 844);
    const f = frame();
    applyStoredViewportToFrame(f.element);
    expect(f.element.style.width).toBe('390px');
    expect(f.element.style.height).toBe('844px');
    expect(f.values.get('--preview-background-width')).toBe('390px');
  });
});
