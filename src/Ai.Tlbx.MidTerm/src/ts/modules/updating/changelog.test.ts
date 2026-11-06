import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../../utils', () => ({ escapeHtml: (value: string) => value }));
vi.mock('../../stores', () => ({ $currentSettings: { get: vi.fn(), set: vi.fn() } }));
vi.mock('../../api/client', () => ({ updateSettings: vi.fn() }));
vi.mock('../i18n', () => ({ t: (key: string) => key }));
vi.mock('../logging', () => ({ createLogger: () => ({ error: vi.fn() }) }));
vi.mock('../navigation/backButtonGuard', () => ({ registerBackButtonLayer: vi.fn() }));

import { fetchChangelogReleases } from './changelog';

const catalog = (tags: string[]) =>
  tags.map((tag_name) => ({ tag_name, body: `Notes ${tag_name}` }));
const response = (value: unknown, ok = true, status = 200) => ({
  ok,
  status,
  json: vi.fn().mockResolvedValue(value),
});

describe('changelog catalog protocol', () => {
  const fetchMock = vi.fn();
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal('fetch', fetchMock);
  });
  afterEach(() => vi.unstubAllGlobals());

  it('reads two bounded public catalogs without credentials or GitHub pagination', async () => {
    fetchMock.mockImplementation((url: string) =>
      Promise.resolve(
        response(
          url.includes('/dev/')
            ? catalog(['v10.25.9-dev', 'v10.25.10-dev', 'v10.25.8-dev', 'v10.25.7-dev'])
            : catalog(['v10.22.0', 'v10.19.3', 'v10.19.2', 'v10.19.1']),
        ),
      ),
    );
    const releases = await fetchChangelogReleases();
    expect(releases.map((release) => release.tag_name)).toEqual([
      'v10.25.10-dev',
      'v10.25.9-dev',
      'v10.25.8-dev',
      'v10.22.0',
      'v10.19.3',
      'v10.19.2',
    ]);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock.mock.calls.map(([url]) => url)).toEqual([
      'https://builds.tlbx.ai/v1/channels/dev/releases.json',
      'https://builds.tlbx.ai/v1/channels/main/releases.json',
    ]);
    for (const [, options] of fetchMock.mock.calls) {
      expect(options.credentials).toBe('omit');
      expect(options.signal).toBeInstanceOf(AbortSignal);
    }
  });

  it('preserves release notes and optional dates without inventing publication dates', async () => {
    fetchMock
      .mockResolvedValueOnce(
        response([{ tag_name: 'v2.0.0-dev', body: '## Fixes\n- bytes unchanged' }]),
      )
      .mockResolvedValueOnce(
        response([{ tag_name: 'v2.0.0', body: 'Stable', published_at: '2026-11-01T00:00:00Z' }]),
      );
    const releases = await fetchChangelogReleases();
    expect(releases[0]?.tag_name).toBe('v2.0.0');
    expect(releases[0]?.published_at).toBe('2026-11-01T00:00:00Z');
    expect(releases[1]?.body).toBe('## Fixes\n- bytes unchanged');
    expect(releases[1]?.published_at).toBeUndefined();
  });

  it('accepts an empty retained channel without loading older releases', async () => {
    fetchMock.mockResolvedValue(response([]));
    await expect(fetchChangelogReleases()).resolves.toEqual([]);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it.each([
    { message: 'Unavailable' },
    [null],
    [{ tag_name: 'v1.2.3' }],
    [{ tag_name: 'v1.2.3-dev', draft: true }],
    [{ tag_name: '<script>unsafe</script>' }],
  ])('rejects invalid catalog metadata without falling back to another API (%j)', async (value) => {
    fetchMock.mockResolvedValueOnce(response(value)).mockResolvedValueOnce(response([]));
    await expect(fetchChangelogReleases()).rejects.toThrow('Invalid');
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('reports unsuccessful HTTP responses without rendering their JSON as releases', async () => {
    const failed = response(catalog(['v1.2.3-dev']), false, 503);
    fetchMock.mockResolvedValueOnce(failed).mockResolvedValueOnce(response([]));
    await expect(fetchChangelogReleases()).rejects.toThrow('Build catalog HTTP 503');
    expect(failed.json).not.toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('reports network failures without GitHub API fallback requests', async () => {
    fetchMock.mockRejectedValue(new Error('Network unavailable'));
    await expect(fetchChangelogReleases()).rejects.toThrow('Network unavailable');
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(
      fetchMock.mock.calls.every(([url]) => String(url).startsWith('https://builds.tlbx.ai/')),
    ).toBe(true);
  });
});
