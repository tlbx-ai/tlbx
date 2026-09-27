import { afterAll, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  FILE_CLIPBOARD_TEXT_LIMIT_BYTES,
  FILE_HEX_PREVIEW_PAGE_BYTES,
  copyFileToClipboard,
  loadBinaryPreviewPage,
} from './shared';

vi.mock('../logging', () => ({
  createLogger: () => ({
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    verbose: vi.fn(),
  }),
}));

const originalNavigator = globalThis.navigator;
const originalFetch = globalThis.fetch;

describe('fileViewer shared helpers', () => {
  beforeEach(() => {
    Object.defineProperty(globalThis, 'navigator', {
      configurable: true,
      value: {
        clipboard: {
          writeText: vi.fn().mockResolvedValue(undefined),
        },
      },
    });
    globalThis.fetch = vi.fn();
  });

  afterAll(() => {
    Object.defineProperty(globalThis, 'navigator', {
      configurable: true,
      value: originalNavigator,
    });
    Object.assign(globalThis, {
      fetch: originalFetch,
    });
  });

  it('falls back to the file path when text exceeds the clipboard size limit', async () => {
    const result = await copyFileToClipboard({
      path: 'Q:\\repo\\large.log',
      mimeType: 'text/plain',
      size: FILE_CLIPBOARD_TEXT_LIMIT_BYTES + 1,
      isText: true,
      currentText: 'ignored',
    });

    expect(globalThis.navigator.clipboard.writeText).toHaveBeenCalledWith('Q:\\repo\\large.log');
    expect(result).toBe('path');
  });

  it('loads binary preview pages through a bounded range request', async () => {
    vi.mocked(globalThis.fetch).mockResolvedValueOnce({
      ok: true,
      status: 206,
      arrayBuffer: async () => new Uint8Array([0x41, 0x42]).buffer,
    } as Response);

    const page = await loadBinaryPreviewPage({
      viewUrl: '/api/files/view?path=test.bin',
      fileSize: FILE_HEX_PREVIEW_PAGE_BYTES + 1,
    });

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/files/view?path=test.bin', {
      headers: {
        Range: `bytes=0-${FILE_HEX_PREVIEW_PAGE_BYTES - 1}`,
      },
    });
    expect(page.startOffset).toBe(0);
    expect(page.endOffsetExclusive).toBe(2);
    expect(page.hasMore).toBe(true);
    expect(Array.from(page.bytes)).toEqual([0x41, 0x42]);
  });
});
