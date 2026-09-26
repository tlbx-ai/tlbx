import { readFileSync } from 'node:fs';
import { afterEach, describe, expect, it, vi } from 'vitest';

const source = readFileSync(
  new URL('../../../../Services/WebPreview/WebPreviewProxyMiddleware.cs', import.meta.url),
  'utf8',
);
const helpers = source.slice(
  source.indexOf('function clampByte('),
  source.indexOf('function rewriteEl('),
);
const wrap = new Function(`${helpers}; return createNormalizedStyleReader;`)();

describe('browser capture computed styles', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('converts and caches nested modern colors in gradients without changing layout values', () => {
    const paint = vi.fn();
    vi.stubGlobal('CSS', { supports: () => true });
    vi.stubGlobal('document', {
      createElement: () => ({
        getContext: () => ({
          clearRect: vi.fn(),
          fillRect: paint,
          fillStyle: '',
          getImageData: () => ({ data: [20, 40, 60, 128] }),
        }),
      }),
    });
    const normalize = new Function(`${helpers}; return normalizeCssColorFunctions;`)();
    const color = 'color-mix(in oklch, oklch(0.5 0.1 20), blue)';
    expect(normalize(`linear-gradient(${color}, oklch(0.8 0.1 40))`)).toBe(
      'linear-gradient(rgba(20, 40, 60, 0.502), rgba(20, 40, 60, 0.502))',
    );
    expect(normalize(color)).toBe('rgba(20, 40, 60, 0.502)');
    expect(paint).toHaveBeenCalledTimes(2);
    expect(normalize('calc(100% - 2px)')).toBe('calc(100% - 2px)');
  });

  it('copies resolved properties without duplicating inherited design tokens', () => {
    const names = ['color', 'width', ...Array.from({ length: 2000 }, (_, i) => `--token-${i}`)];
    const values: Record<string, string> = {
      color: 'color(srgb 1 0 0)',
      width: '120px',
      '--token-0': 'blue',
    };
    const style = Object.assign(Object.fromEntries(names.map((name, i) => [i, name])), {
      length: names.length,
      item: (i: number) => names[i] ?? '',
      getPropertyValue: (name: string) => values[name] ?? '',
      color: values.color,
    });
    const capture = wrap(style);
    expect([...capture]).toEqual(['color', 'width']);
    expect(capture.length).toBe(2);
    expect(capture[0]).toBe('color');
    expect(capture.item(1)).toBe('width');
    expect(capture.item(2)).toBe('');
    expect(capture.getPropertyValue('--token-0')).toBe('blue');
    expect(capture.getPropertyValue('width')).toBe('120px');
    expect(capture.color).toBe('rgb(255, 0, 0)');
    expect(style.length).toBe(2002);
  });
});
