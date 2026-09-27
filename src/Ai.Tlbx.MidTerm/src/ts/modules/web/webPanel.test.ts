import { describe, expect, it } from 'vitest';
import { shouldReloadPreviewFrame } from './previewLoadToken';

describe('webPanel preview reload decision', () => {
  it('reloads when the upstream target revision changes even if the proxy URL stays the same', () => {
    const frame = {
      src: 'https://localhost:2000/webpreview/route/',
      dataset: {
        mtPreviewLoadToken: '1:https://example.com/',
      },
    } as Pick<HTMLIFrameElement, 'src' | 'dataset'>;

    expect(
      shouldReloadPreviewFrame(
        frame,
        'https://localhost:2000/webpreview/route/',
        'https://example.org/',
        2,
      ),
    ).toBe(true);
  });

  it('does not reload when the proxy URL and target revision token are unchanged', () => {
    const frame = {
      src: 'https://localhost:2000/webpreview/route/',
      dataset: {
        mtPreviewLoadToken: '2:https://example.org/',
      },
    } as Pick<HTMLIFrameElement, 'src' | 'dataset'>;

    expect(
      shouldReloadPreviewFrame(
        frame,
        'https://localhost:2000/webpreview/route/',
        'https://example.org/',
        2,
      ),
    ).toBe(false);
  });
});
