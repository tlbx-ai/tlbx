import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
const source = readFileSync(
  new URL('../../../../Services/WebPreview/WebPreviewProxyMiddleware.cs', import.meta.url),
  'utf8',
);
const branch = source.slice(source.indexOf('case"wheel":{'), source.indexOf('case"fill":{'));
const dispatch = new Function(
  'msg',
  'res',
  'bws',
  'document',
  'WheelEvent',
  'setTimeout',
  'requestAnimationFrame',
  `switch(msg.command){${branch}}`,
);

describe('background proxy wheel', () => {
  it('completes with correct scroll metrics even when animation frames never run', async () => {
    const element = {
      scrollTop: 0,
      scrollLeft: 0,
      scrollHeight: 2000,
      scrollWidth: 100,
      clientHeight: 500,
      clientWidth: 100,
      dispatchEvent: () => true,
    };
    const result = await new Promise<{ success: boolean; result: string }>((resolve) => {
      dispatch(
        { command: 'wheel', selector: '#scroll', deltaY: 120, steps: 3 },
        { success: true },
        { send: (json: string) => resolve(JSON.parse(json)) },
        { querySelector: () => element },
        class {
          static DOM_DELTA_PIXEL = 0;
        },
        (done: () => void) => done(),
        () => {},
      );
    });
    expect(result.success).toBe(true);
    expect(JSON.parse(result.result).after.scrollTop).toBe(360);
    expect(JSON.parse(result.result).samples).toHaveLength(3);
  });
});
