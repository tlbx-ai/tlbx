import { beforeEach, describe, expect, it, vi } from 'vitest';

describe('appServerControlAttachments', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it('detects pasted image clipboard data from data transfer items', async () => {
    const {
      clipboardDataMayContainAppServerControlComposerImage,
      extractAppServerControlComposerPasteImageFiles,
    } = await import('./appServerControlAttachments');
    const clipboardImage = new File(['png'], 'copied-image.png', { type: 'image/png' });
    const clipboardData = {
      files: [],
      items: [
        {
          kind: 'file',
          type: 'image/png',
          getAsFile: () => clipboardImage,
        },
      ],
      getData: () => '',
    };

    expect(
      clipboardDataMayContainAppServerControlComposerImage(
        clipboardData as unknown as DataTransfer,
      ),
    ).toBe(true);
    await expect(
      extractAppServerControlComposerPasteImageFiles(
        clipboardData as unknown as DataTransfer,
        null,
      ),
    ).resolves.toEqual([clipboardImage]);
  });

  it('extracts pasted image files from copied html image markup', async () => {
    const fetchMock = vi.fn(async () => ({
      ok: true,
      blob: async () => new Blob(['img'], { type: 'image/webp' }),
    }));
    vi.stubGlobal('fetch', fetchMock);

    const {
      clipboardDataMayContainAppServerControlComposerImage,
      extractAppServerControlComposerPasteImageFiles,
    } = await import('./appServerControlAttachments');
    const clipboardData = {
      files: [],
      items: [],
      getData: (type: string) =>
        type === 'text/html' ? '<img src="https://cdn.example.com/photo">' : '',
    };

    expect(
      clipboardDataMayContainAppServerControlComposerImage(
        clipboardData as unknown as DataTransfer,
      ),
    ).toBe(true);

    const files = await extractAppServerControlComposerPasteImageFiles(
      clipboardData as unknown as DataTransfer,
      null,
    );

    expect(fetchMock).toHaveBeenCalledWith('https://cdn.example.com/photo');
    expect(files).toHaveLength(1);
    expect(files[0]?.type).toBe('image/webp');
    expect(files[0]?.name).toBe('photo.webp');
  });

  it('falls back to navigator clipboard image blobs when paste data lacks files', async () => {
    const { extractAppServerControlComposerPasteImageFiles } =
      await import('./appServerControlAttachments');
    const files = await extractAppServerControlComposerPasteImageFiles(null, async () => [
      {
        types: ['image/avif'],
        getType: async () => new Blob(['avif'], { type: 'image/avif' }),
      },
    ]);

    expect(files).toHaveLength(1);
    expect(files[0]?.type).toBe('image/avif');
    expect(files[0]?.name).toMatch(/\.avif$/);
  });

  it('probes the async clipboard for data-poor Android image paste events only on mobile', async () => {
    const { shouldProbeMobileClipboardForComposerImage } =
      await import('./appServerControlAttachments');
    const emptyClipboardData = {
      files: [],
      items: [],
      getData: () => '',
    };
    const placeholderClipboardData = {
      files: [],
      items: [],
      getData: (type: string) => (type === 'text/plain' ? '\ufffc' : ''),
    };

    expect(
      shouldProbeMobileClipboardForComposerImage(
        emptyClipboardData as unknown as DataTransfer,
        true,
      ),
    ).toBe(true);
    expect(
      shouldProbeMobileClipboardForComposerImage(
        placeholderClipboardData as unknown as DataTransfer,
        true,
      ),
    ).toBe(true);
    expect(
      shouldProbeMobileClipboardForComposerImage(
        emptyClipboardData as unknown as DataTransfer,
        false,
      ),
    ).toBe(false);
  });

  it('turns a data-poor mobile paste into an image part through the async clipboard fallback', async () => {
    const { extractAppServerControlComposerPasteParts } =
      await import('./appServerControlAttachments');
    const clipboardData = {
      files: [],
      items: [],
      getData: (type: string) => (type === 'text/plain' ? '\ufffc' : ''),
    };

    await expect(
      extractAppServerControlComposerPasteParts(
        clipboardData as unknown as DataTransfer,
        async () => [
          {
            types: ['image/png'],
            getType: async () => new Blob(['png'], { type: 'image/png' }),
          },
        ],
      ),
    ).resolves.toMatchObject([{ kind: 'image', file: { type: 'image/png' } }]);
  });

  it('interleaves plain text object placeholders with clipboard images in paste order', async () => {
    const { extractAppServerControlComposerPasteParts } =
      await import('./appServerControlAttachments');
    const firstImage = new File(['one'], 'one.png', { type: 'image/png' });
    const secondImage = new File(['two'], 'two.png', { type: 'image/png' });
    const clipboardData = {
      files: [firstImage, secondImage],
      items: [],
      getData: (type: string) => (type === 'text/plain' ? `alpha\ufffcbeta\ufffcgamma` : ''),
    };

    await expect(
      extractAppServerControlComposerPasteParts(clipboardData as unknown as DataTransfer, null),
    ).resolves.toEqual([
      { kind: 'text', text: 'alpha' },
      { kind: 'image', file: firstImage },
      { kind: 'text', text: 'beta' },
      { kind: 'image', file: secondImage },
      { kind: 'text', text: 'gamma' },
    ]);
  });

  it('preserves copied html image ordering with surrounding text', async () => {
    const { extractAppServerControlComposerPasteParts } =
      await import('./appServerControlAttachments');
    const firstImage = new File(['one'], 'one.png', { type: 'image/png' });
    const secondImage = new File(['two'], 'two.png', { type: 'image/png' });
    const clipboardData = {
      files: [firstImage, secondImage],
      items: [],
      getData: (type: string) =>
        type === 'text/html'
          ? '<div>before</div><img src="cid:one"><div>middle</div><img src="cid:two"><div>after</div>'
          : '',
    };

    await expect(
      extractAppServerControlComposerPasteParts(clipboardData as unknown as DataTransfer, null),
    ).resolves.toEqual([
      { kind: 'text', text: 'before\n' },
      { kind: 'image', file: firstImage },
      { kind: 'text', text: 'middle\n' },
      { kind: 'image', file: secondImage },
      { kind: 'text', text: 'after\n' },
    ]);
  });

  it('releases preview URLs when drafts are discarded', async () => {
    const revokeObjectURL = vi.fn();
    Object.defineProperty(URL, 'revokeObjectURL', {
      configurable: true,
      value: revokeObjectURL,
    });

    const { releaseAppServerControlComposerDraftAttachmentPreviews } =
      await import('./appServerControlAttachments');
    releaseAppServerControlComposerDraftAttachmentPreviews([
      {
        id: 'a1',
        kind: 'image',
        file: new File(['png'], 'screen.png', { type: 'image/png' }),
        uploadedPath: null,
        displayName: 'screen.png',
        mimeType: 'image/png',
        referenceCharCount: null,
        referenceKind: 'image',
        referenceLabel: 'Image 1',
        referenceLineCount: null,
        referenceOrdinal: 1,
        sizeBytes: 3,
        previewUrl: 'blob:preview',
      },
      {
        id: 'a2',
        kind: 'file',
        file: null,
        uploadedPath: 'Q:/repo/.midterm/uploads/note.txt',
        displayName: 'note.txt',
        mimeType: 'text/plain',
        referenceCharCount: null,
        referenceKind: null,
        referenceLabel: null,
        referenceLineCount: null,
        referenceOrdinal: null,
        sizeBytes: 4,
        previewUrl: null,
      },
      {
        id: 'a3',
        kind: 'image',
        file: null,
        uploadedPath: 'Q:/repo/.midterm/uploads/screen.png',
        displayName: 'screen.png',
        mimeType: 'image/png',
        referenceCharCount: null,
        referenceKind: 'image',
        referenceLabel: 'Image 1',
        referenceLineCount: null,
        referenceOrdinal: 1,
        sizeBytes: 3,
        previewUrl:
          '/api/files/view?path=Q%3A%2Frepo%2F.midterm%2Fuploads%2Fscreen.png&sessionId=s1',
      },
    ]);

    expect(revokeObjectURL).toHaveBeenCalledTimes(1);
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:preview');
  });

  it('keeps copied html text paste alone when it does not carry image content', async () => {
    const {
      clipboardDataMayContainAppServerControlComposerImage,
      extractAppServerControlComposerPasteImageFiles,
    } = await import('./appServerControlAttachments');
    const clipboardData = {
      files: [],
      items: [],
      getData: (type: string) =>
        type === 'text/html' ? '<p>regular copied text</p>' : 'regular copied text',
    };

    expect(
      clipboardDataMayContainAppServerControlComposerImage(
        clipboardData as unknown as DataTransfer,
      ),
    ).toBe(false);
    await expect(
      extractAppServerControlComposerPasteImageFiles(
        clipboardData as unknown as DataTransfer,
        null,
      ),
    ).resolves.toEqual([]);
  });
});
