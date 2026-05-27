import { describe, expect, it } from 'vitest';

import {
  buildProxyUrl,
  sanitizePreviewDisplayUrl,
  stripInternalPreviewQueryParams,
} from './previewProxyUrl';

describe('previewProxyUrl', () => {
  it('removes internal preview parameters from display URLs', () => {
    expect(
      sanitizePreviewDisplayUrl(
        'https://example.com/?foo=1&__mtPreviewId=pid&__mtPreviewToken=ptk&__mtTargetRevision=2&__mtReloadToken=force-1#frag',
      ),
    ).toBe('https://example.com/?foo=1#frag');
    expect(sanitizePreviewDisplayUrl('https://example.com/?foo=1&__mtMobile=1#frag')).toBe(
      'https://example.com/?foo=1#frag',
    );
  });

  it('strips internal parameters from parsed proxy URLs', () => {
    const url = new URL(
      'https://midterm.local/webpreview/route/?foo=1&__mtPreviewId=pid&__mtPreviewToken=ptk&__mtTargetRevision=2&__mtReloadToken=force-1&__mtMobile=1#frag',
    );

    stripInternalPreviewQueryParams(url);

    expect(url.toString()).toBe('https://midterm.local/webpreview/route/?foo=1#frag');
  });

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

  it('adds the mobile emulation flag to proxy URLs when requested', () => {
    const url = buildProxyUrl(
      'https://example.com/?foo=1',
      {
        sessionId: 's1',
        previewName: 'default',
        routeKey: 'route',
        previewId: 'pid',
        previewToken: 'ptk',
      },
      3,
      'https://midterm.local',
      { mobileEmulation: true },
    );

    expect(url).toContain('__mtMobile=1');
  });
});
