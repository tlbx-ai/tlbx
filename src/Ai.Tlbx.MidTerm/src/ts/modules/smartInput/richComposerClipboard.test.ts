import { describe, expect, it, vi } from 'vitest';
import DOMPurify from 'dompurify';
vi.mock('dompurify', () => ({ default: { sanitize: vi.fn() } }));
import { captureRichClipboard, readRichClipboard, materializeRichClipboardImages, plainRichClipboardParts, richClipboardReferenceIds } from './richComposerClipboard';
import { draftFromRichDocument, getRichComposerDocument } from './richComposerDocument';

function transfer(files: File[], items: Array<{ kind: string; getAsFile: () => File | null }>) {
  return { files: files as unknown as FileList, items: items as unknown as DataTransferItemList,
    getData: (type: string) => type === 'text/plain' ? 'before \uFFFC after' : '' };
}

describe('trusted rich clipboard capture', () => {
  it('reads rich browser clipboard formats once and retains image bytes without a text-only fallback', async () => {
    const getType = vi.fn(async (type: string) => new Blob([type === 'text/html' ? '<p><strong>Rich</strong></p>' : type === 'text/plain' ? 'Rich' : 'png-bytes'], { type }));
    const read = vi.fn(async () => [{ types: ['text/html', 'text/plain', 'image/png'], getType }]);
    const readText = vi.fn();
    vi.stubGlobal('navigator', { clipboard: { read, readText } });
    try {
      const data = await readRichClipboard();
      expect(data.html).toBe('<p><strong>Rich</strong></p>');
      expect(data.text).toBe('Rich');
      expect(await data.files[0]!.text()).toBe('png-bytes');
      expect(read).toHaveBeenCalledTimes(1);
      expect(getType).toHaveBeenCalledTimes(3);
      expect(readText).not.toHaveBeenCalled();
    } finally { vi.unstubAllGlobals(); }
  });

  it('preserves denied clipboard access as failure rather than an empty paste or another read', async () => {
    const denied = new Error('permission denied');
    const readText = vi.fn();
    vi.stubGlobal('navigator', { clipboard: { read: vi.fn().mockRejectedValue(denied), readText } });
    try {
      await expect(readRichClipboard()).rejects.toBe(denied);
      expect(readText).not.toHaveBeenCalled();
    } finally { vi.unstubAllGlobals(); }
  });

  it('keeps missing image positions without fetching URLs or discarding the surrounding HTML', () => {
    const fetch = vi.fn(() => { throw new Error('no network'); });
    const html = '<p>before</p><table><tr><td>text<img src="https://foreign.test/image.png"></td></tr></table><p>after</p>';
    vi.stubGlobal('fetch', fetch);
    // Parsing remains a DOM boundary; this contract checks that image discovery
    // consumes only event files and never reads src or starts a network request.
    vi.mocked(DOMPurify.sanitize).mockReturnValue({
      querySelectorAll: () => [{ getAttribute: () => { throw new Error('URL bytes forbidden'); } }],
    } as unknown as DocumentFragment);
    try {
      const data = materializeRichClipboardImages({ html, text: 'before text after', files: [] });
      expect(data.html).toBe(html);
      expect(data.text).toBe('before text after');
      expect(richClipboardReferenceIds(data, () => 'never')).toEqual(['']);
      expect(fetch).not.toHaveBeenCalled();
    } finally { vi.unstubAllGlobals(); }
  });

  it('reserves unavailable image slots before additional transferred non-image files', () => {
    const image = new File(['png'], 'image.png', { type: 'image/png' });
    const file = new File(['txt'], 'text.txt', { type: 'text/plain' });
    expect(richClipboardReferenceIds({ html: '', text: '', files: [image, file], imageCount: 3 },
      (entry) => entry === image ? 'image-id' : 'file-id')).toEqual(['', '', '', 'image-id', 'file-id']);
  });
  it('captures event data once without reading the duplicate item or asynchronous OS clipboard', () => {
    const file = new File(['original'], 'same.png', { type: 'image/png', lastModified: 42 });
    const duplicate = vi.fn(() => file);
    const data = captureRichClipboard(transfer([file], [{ kind: 'file', getAsFile: duplicate }]));
    expect(data.files).toEqual([file]);
    expect(data.text).toBe('before \uFFFC after');
    expect(duplicate).not.toHaveBeenCalled();
  });

  it('keeps distinct equal-name/size/timestamp files and uses items only when files are absent', () => {
    const first = new File(['a'], 'same.png', { type: 'image/png', lastModified: 42 });
    const second = new File(['b'], 'same.png', { type: 'image/png', lastModified: 42 });
    expect(captureRichClipboard(transfer([first, second], [])).files).toEqual([first, second]);
    expect(captureRichClipboard(transfer([], [{ kind: 'file', getAsFile: () => first },
      { kind: 'file', getAsFile: () => second }])).files).toEqual([first, second]);
  });

  it('retains text/image interleaving and attachment-only content through migration and serialization', () => {
    const parts = plainRichClipboardParts('before \uFFFC between\n\uFFFC after', ['A', 'B', 'C']);
    expect(parts).toEqual([{ kind: 'text', text: 'before ' }, { kind: 'reference', referenceId: 'A' },
      { kind: 'text', text: ' between\n' }, { kind: 'reference', referenceId: 'B' },
      { kind: 'text', text: ' after' }, { kind: 'reference', referenceId: 'C' }]);
    const migrated = { parts, nextOrdinalByKind: {} };
    const roundtrip = draftFromRichDocument(migrated, getRichComposerDocument(migrated));
    expect(roundtrip.parts.filter((part) => part.kind === 'reference').map((part) => part.referenceId)).toEqual(['A', 'B', 'C']);
    expect(plainRichClipboardParts('', ['only-image'])).toEqual([{ kind: 'reference', referenceId: 'only-image' }]);
  });
});
