import { afterEach, describe, expect, it, vi } from 'vitest';
import { createRemoteHistoryEntry, setRemoteSessionBookmark } from './api';

describe('hub api', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('creates bookmark history on the selected remote machine', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ id: 'history-1' }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    const response = await createRemoteHistoryEntry('home pc', {
      shellType: 'Pwsh',
      executable: 'pwsh',
      commandLine: 'pwsh -NoLogo',
      workingDirectory: 'Q:/repos/MidTerm',
      isStarred: true,
      label: 'MidTerm',
      notes: null,
      dedupeKey: 'pwsh|Q:/repos/MidTerm',
      launchMode: 'terminal',
      profile: null,
      launchOrigin: 'adhoc',
      surfaceType: 'trm',
      foregroundProcessName: 'pwsh',
      foregroundProcessCommandLine: 'pwsh -NoLogo',
      foregroundProcessDisplayName: 'pwsh',
      foregroundProcessIdentity: 'pwsh',
    });

    expect(response.id).toBe('history-1');
    expect(fetchMock).toHaveBeenCalledWith('/api/hub/machines/home%20pc/history', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: expect.stringContaining('"workingDirectory":"Q:/repos/MidTerm"'),
    });
  });

  it('links the remote session bookmark on the owning machine', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    await setRemoteSessionBookmark('home pc', 'session/1', 'history-1');

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/hub/machines/home%20pc/sessions/session%2F1/bookmark',
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ bookmarkId: 'history-1' }),
      },
    );
  });
});
