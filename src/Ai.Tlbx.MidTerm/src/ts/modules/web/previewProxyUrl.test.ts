import { describe, expect, it } from 'vitest';

import { buildProxyUrl } from './previewProxyUrl';

describe('previewProxyUrl', () => {
  it('adds the forced reload token to proxy URLs when requested', () => {
    const url = buildProxyUrl(
      'https://example.com/app.js?foo=1',
      {
        sessionId: 's1',
        previewName: 'default',
        routeKey: 'route',
        previewId: 'pid',
        previewToken: 'ptk',
      },
      3,
      'https://midterm.local',
      { reloadToken: 'force-1' },
    );

    expect(url).toContain('__mtReloadToken=force-1');
  });
});
