import { afterEach, describe, expect, it, vi } from 'vitest';

describe('closeSession error reporting', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.resetModules();
  });
  it('rejects a refused close with its server explanation', async () => {
    const BrowserRequest = Request;
    vi.stubGlobal(
      'Request',
      class extends BrowserRequest {
        constructor(input: RequestInfo | URL, init?: RequestInit) {
          super(typeof input === 'string' ? new URL(input, 'https://localhost') : input, init);
        }
      },
    );
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValue(
          new Response(JSON.stringify({ title: 'Close failed', detail: 'Host still running' }), {
            status: 409,
            headers: { 'Content-Type': 'application/problem+json' },
          }),
        ),
    );
    const { deleteSession } = await import('./client');
    await expect(deleteSession('a')).rejects.toThrow('Host still running');
  });
});
