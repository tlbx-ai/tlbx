import { describe, expect, it, vi } from 'vitest';

describe('smartInputOutboundReferences', () => {
  it('keeps reference markers in the prompt while appending full text-reference content for AppServerControl', async () => {
    const { prepareSmartInputOutboundPrompt } = await import('./smartInputOutboundReferences');

    const result = await prepareSmartInputOutboundPrompt({
      sessionId: 's1',
      draft: {
        nextOrdinalByKind: { image: 2, text: 2 },
        parts: [
          { kind: 'text', text: 'Make it look like ' },
          { kind: 'reference', referenceId: 'img-1' },
          { kind: 'text', text: ' and fix ' },
          { kind: 'reference', referenceId: 'txt-1' },
          { kind: 'text', text: '.' },
        ],
      },
      attachments: [
        {
          id: 'img-1',
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
          previewUrl: null,
        },
        {
          id: 'txt-1',
          kind: 'file',
          file: new File(['line 1\nline 2'], 'pasted-text.txt', { type: 'text/plain' }),
          uploadedPath: 'Q:/repo/.midterm/uploads/pasted-text.txt',
          displayName: 'pasted-text.txt',
          mimeType: 'text/plain',
          referenceCharCount: 13,
          referenceKind: 'text',
          referenceLabel: 'Text 1',
          referenceLineCount: 2,
          referenceOrdinal: 1,
          sizeBytes: 13,
          previewUrl: null,
        },
      ],
      uploadFailureMessage: 'upload failed',
      attachmentReadFailureMessage: 'read failed',
      uploadFile: vi.fn(),
    });

    expect(result).toEqual({
      text: 'Make it look like [Image 1] and fix [Text 1].\n\n[Text 1]\nline 1\nline 2',
      attachments: [
        {
          kind: 'image',
          path: 'Q:/repo/.midterm/uploads/screen.png',
          mimeType: 'image/png',
          displayName: 'screen.png',
        },
      ],
    });
  });

  it('builds terminal replay steps that preserve inline text and image order', async () => {
    const { prepareSmartInputTerminalTurn } = await import('./smartInputOutboundReferences');

    const result = await prepareSmartInputTerminalTurn({
      sessionId: 's1',
      draft: {
        nextOrdinalByKind: { image: 3 },
        parts: [
          { kind: 'text', text: 'Compare ' },
          { kind: 'reference', referenceId: 'img-1' },
          { kind: 'text', text: ' with ' },
          { kind: 'reference', referenceId: 'img-2' },
          { kind: 'text', text: '.' },
        ],
      },
      attachments: [
        {
          id: 'img-1',
          kind: 'image',
          file: null,
          uploadedPath: 'Q:/repo/.midterm/uploads/one.png',
          displayName: 'one.png',
          mimeType: 'image/png',
          referenceCharCount: null,
          referenceKind: 'image',
          referenceLabel: 'Image 1',
          referenceLineCount: null,
          referenceOrdinal: 1,
          sizeBytes: 3,
          previewUrl: null,
        },
        {
          id: 'img-2',
          kind: 'image',
          file: null,
          uploadedPath: 'Q:/repo/.midterm/uploads/two.png',
          displayName: 'two.png',
          mimeType: 'image/png',
          referenceCharCount: null,
          referenceKind: 'image',
          referenceLabel: 'Image 2',
          referenceLineCount: null,
          referenceOrdinal: 2,
          sizeBytes: 3,
          previewUrl: null,
        },
      ],
      bracketedPasteModeEnabled: false,
      uploadFailureMessage: 'upload failed',
      uploadFile: vi.fn(),
    });

    expect(result).toEqual({
      text: 'Compare [Image 1] with [Image 2].',
      attachments: [],
      terminalReplay: [
        { kind: 'text', text: 'Compare ' },
        { kind: 'image', path: 'Q:/repo/.midterm/uploads/one.png', mimeType: 'image/png' },
        { kind: 'text', text: ' with ' },
        { kind: 'image', path: 'Q:/repo/.midterm/uploads/two.png', mimeType: 'image/png' },
        { kind: 'text', text: '.' },
      ],
    });
  });

  it('reloads referenced text content from the uploaded file path when the local file is gone', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      text: async () => 'persisted text',
    });
    vi.stubGlobal('fetch', fetchMock);

    try {
      const { prepareSmartInputOutboundPrompt } = await import('./smartInputOutboundReferences');

      const result = await prepareSmartInputOutboundPrompt({
        sessionId: 's1',
        draft: {
          nextOrdinalByKind: { text: 2 },
          parts: [{ kind: 'reference', referenceId: 'txt-1' }],
        },
        attachments: [
          {
            id: 'txt-1',
            kind: 'file',
            file: null,
            uploadedPath: 'Q:/repo/.midterm/uploads/pasted-text.txt',
            displayName: 'pasted-text.txt',
            mimeType: 'text/plain',
            referenceCharCount: 14,
            referenceKind: 'text',
            referenceLabel: 'Text 1',
            referenceLineCount: 1,
            referenceOrdinal: 1,
            sizeBytes: 14,
            previewUrl: null,
          },
        ],
        uploadFailureMessage: 'upload failed',
        attachmentReadFailureMessage: 'read failed',
        uploadFile: vi.fn(),
      });

      expect(fetchMock).toHaveBeenCalledWith(
        '/api/files/view?path=Q%3A%2Frepo%2F.midterm%2Fuploads%2Fpasted-text.txt&sessionId=s1',
      );
      expect(result.text).toBe('[Text 1]\n\n[Text 1]\npersisted text');
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('replays inline text references as pasted text content in terminal mode', async () => {
    const { prepareSmartInputTerminalTurn } = await import('./smartInputOutboundReferences');

    const result = await prepareSmartInputTerminalTurn({
      sessionId: 's1',
      draft: {
        nextOrdinalByKind: { text: 2 },
        parts: [
          { kind: 'text', text: 'Before ' },
          { kind: 'reference', referenceId: 'txt-1' },
          { kind: 'text', text: ' after' },
        ],
      },
      attachments: [
        {
          id: 'txt-1',
          kind: 'file',
          file: new File(['alpha\nbeta'], 'pasted-text.txt', { type: 'text/plain' }),
          uploadedPath: 'Q:/repo/.midterm/uploads/pasted-text.txt',
          displayName: 'pasted-text.txt',
          mimeType: 'text/plain',
          referenceCharCount: 10,
          referenceKind: 'text',
          referenceLabel: 'Text 1',
          referenceLineCount: 2,
          referenceOrdinal: 1,
          sizeBytes: 10,
          previewUrl: null,
        },
      ],
      bracketedPasteModeEnabled: true,
      uploadFailureMessage: 'upload failed',
      uploadFile: vi.fn(),
    });

    expect(result).toEqual({
      text: 'Before [Text 1] after',
      attachments: [],
      terminalReplay: [
        { kind: 'text', text: 'Before ' },
        {
          kind: 'textFile',
          path: 'Q:/repo/.midterm/uploads/pasted-text.txt',
          useBracketedPaste: true,
        },
        { kind: 'text', text: ' after' },
      ],
    });
  });

  it('always uses bracketed paste for terminal text references so multiline content stays one paste payload', async () => {
    const { prepareSmartInputTerminalTurn } = await import('./smartInputOutboundReferences');

    const result = await prepareSmartInputTerminalTurn({
      sessionId: 's1',
      draft: {
        nextOrdinalByKind: { text: 3 },
        parts: [
          { kind: 'reference', referenceId: 'txt-1' },
          { kind: 'text', text: ' ' },
          { kind: 'reference', referenceId: 'txt-2' },
        ],
      },
      attachments: [
        {
          id: 'txt-1',
          kind: 'file',
          file: new File(['one\ntwo'], 'one.txt', { type: 'text/plain' }),
          uploadedPath: 'Q:/repo/.midterm/uploads/one.txt',
          displayName: 'one.txt',
          mimeType: 'text/plain',
          referenceCharCount: 7,
          referenceKind: 'text',
          referenceLabel: 'Text 1',
          referenceLineCount: 2,
          referenceOrdinal: 1,
          sizeBytes: 7,
          previewUrl: null,
        },
        {
          id: 'txt-2',
          kind: 'file',
          file: new File(['three\nfour'], 'two.txt', { type: 'text/plain' }),
          uploadedPath: 'Q:/repo/.midterm/uploads/two.txt',
          displayName: 'two.txt',
          mimeType: 'text/plain',
          referenceCharCount: 10,
          referenceKind: 'text',
          referenceLabel: 'Text 2',
          referenceLineCount: 2,
          referenceOrdinal: 2,
          sizeBytes: 10,
          previewUrl: null,
        },
      ],
      bracketedPasteModeEnabled: false,
      uploadFailureMessage: 'upload failed',
      uploadFile: vi.fn(),
    });

    expect(result.terminalReplay).toEqual([
      {
        kind: 'textFile',
        path: 'Q:/repo/.midterm/uploads/one.txt',
        useBracketedPaste: true,
      },
      { kind: 'text', text: ' ' },
      {
        kind: 'textFile',
        path: 'Q:/repo/.midterm/uploads/two.txt',
        useBracketedPaste: true,
      },
    ]);
  });
});
