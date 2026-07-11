import { afterEach, describe, expect, it, vi } from 'vitest';
import { fetchControlPlane, fetchControlPlaneEvents } from './controlPlaneApi';

const emptySnapshot = { workItems: [], sessionStatuses: [], checkpoints: [] };

describe('control plane api', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('loads the local agent-published snapshot', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify(emptySnapshot), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    await fetchControlPlane();

    expect(fetchMock).toHaveBeenCalledWith('/api/control-plane', undefined);
  });

  it('loads a Hub machine through the authenticated local proxy', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify(emptySnapshot), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    await fetchControlPlane('home pc');

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/hub/machines/home%20pc/control-plane',
      undefined,
    );
  });

  it('uses an explicit sequence cursor for exact remote events', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ latestSequence: 42, events: [] }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    await fetchControlPlaneEvents(40, 100, 'work laptop');

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/hub/machines/work%20laptop/control-plane/events?after=40&limit=100',
      undefined,
    );
  });
});
