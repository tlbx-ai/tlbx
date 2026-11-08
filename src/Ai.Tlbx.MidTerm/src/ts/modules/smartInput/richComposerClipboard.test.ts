import { describe, expect, it, vi } from 'vitest';
import { captureRichClipboard, plainRichClipboardParts } from './richComposerClipboard';
import { draftFromRichDocument, getRichComposerDocument } from './richComposerDocument';

function transfer(files: File[], items: Array<{ kind: string; getAsFile: () => File | null }>) {
  return { files: files as unknown as FileList, items: items as unknown as DataTransferItemList,
    getData: (type: string) => type === 'text/plain' ? 'before \uFFFC after' : '' };
}

describe('trusted rich clipboard capture', () => {
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
