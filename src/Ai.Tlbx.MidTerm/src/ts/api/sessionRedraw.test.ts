import { afterEach, describe, expect, it, vi } from 'vitest';

describe('redrawSession', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.resetModules();
  });

  it('posts to the encoded per-session redraw endpoint without a terminal resize payload', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetchMock);

    const { redrawSession } = await import('./client');
    await redrawSession('session/one');

    expect(fetchMock).toHaveBeenCalledWith('/api/sessions/session%2Fone/redraw', {
      method: 'POST',
    });
  });
});
