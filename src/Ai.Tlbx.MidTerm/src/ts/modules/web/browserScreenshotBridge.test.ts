import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

const source = readFileSync(
  new URL('../../../../Services/WebPreview/WebPreviewProxyMiddleware.cs', import.meta.url),
  'utf8',
);
const branch = source.slice(
  source.indexOf('case"screenshot":{'),
  source.indexOf('case"snapshot":{'),
);

async function capture(fullPage: boolean) {
  let options: Record<string, any> = {};
  let restored = false;
  const result = await new Promise((resolve) => {
    new Function(
      'msg',
      'res',
      'window',
      'document',
      'bws',
      'ensureH2c',
      'installComputedStyleColorNormalization',
      `switch(msg.command){${branch}}`,
    )(
      { command: 'screenshot', fullPage },
      { success: true },
      {
        scrollX: 10,
        scrollY: 500,
        innerWidth: 800,
        innerHeight: 600,
        html2canvas: (_: unknown, value: Record<string, any>) => {
          options = value;
          return Promise.resolve({ toDataURL: () => 'data:image/png;base64,ok' });
        },
      },
      { documentElement: {} },
      { send: (value: string) => resolve(JSON.parse(value)) },
      () => Promise.resolve(),
      () => () => {
        restored = true;
      },
    );
  });
  return { options, result, restored };
}

describe('browser screenshot bounds', () => {
  it('captures the scrolled viewport and excludes only content below it', async () => {
    const { options, result, restored } = await capture(false);
    expect(options).toMatchObject({ x: 10, y: 500, width: 800, height: 600, imageTimeout: 1500 });
    const element = (top: number, tagName = 'DIV') => ({
      tagName,
      getBoundingClientRect: () => ({ top, height: 100 }),
    });
    expect(options.ignoreElements(element(800))).toBe(true);
    expect(options.ignoreElements(element(-200))).toBe(false);
    expect(options.ignoreElements(element(800, 'STYLE'))).toBe(false);
    expect(result).toMatchObject({ success: true, result: 'data:image/png;base64,ok' });
    expect(restored).toBe(true);
  });

  it('retains full document capture only when explicitly requested', async () => {
    const { options } = await capture(true);
    expect(options).toMatchObject({ x: 0, y: 0, width: undefined, height: undefined });
    expect(
      options.ignoreElements({
        tagName: 'DIV',
        getBoundingClientRect: () => ({ top: 900, height: 100 }),
      }),
    ).toBe(false);
  });
});
