import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

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
