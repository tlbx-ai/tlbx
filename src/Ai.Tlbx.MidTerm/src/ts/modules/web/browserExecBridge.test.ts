import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

// Execute the shipped bridge branch, including its async response path.
const source = readFileSync(
  new URL('../../../../Services/WebPreview/WebPreviewProxyMiddleware.cs', import.meta.url),
  'utf8',
);
const branch = source.slice(source.indexOf('case"exec":{'), source.indexOf('case"wait":{'));
const dispatch = new Function('msg', 'res', 'bws', 'WebSocket', `switch(msg.command){${branch}}`);

function execute(value: string): Promise<{ success: boolean; result?: string; error?: string }> {
  return new Promise((resolve, reject) => {
    try {
      dispatch(
        { command: 'exec', value },
        { success: true },
        { readyState: 1, send: (message: string) => resolve(JSON.parse(message)) },
        { OPEN: 1 },
      );
    } catch (error) {
      reject(error);
    }
  });
}

describe('browser exec bridge', () => {
  it('returns one structured result from batched async work', async () => {
    expect(
      await execute(
        'Promise.all([Promise.resolve(2), Promise.resolve(3)]).then(values => ({values}))',
      ),
    ).toEqual({ success: true, result: '{"values":[2,3]}' });
  });

  it('keeps synchronous string results unchanged', async () => {
    expect(await execute('JSON.stringify({ready:true})')).toEqual({
      success: true,
      result: '{"ready":true}',
    });
  });

  it('returns async failures instead of an apparent Promise success', async () => {
    expect(await execute('Promise.reject(new Error("query failed"))')).toEqual({
      success: false,
      error: 'query failed',
    });
  });

  it('handles rejection without an Error instance', async () => {
    expect(await execute('Promise.reject(null)')).toEqual({ success: false, error: 'null' });
  });

  it('preserves undefined and null values', async () => {
    expect(await execute('Promise.resolve(undefined)')).toEqual({
      success: true,
      result: 'undefined',
    });
    expect(await execute('null')).toEqual({ success: true, result: 'null' });
  });
});
