import { beforeEach, describe, expect, it, vi } from 'vitest';

const sendAppServerControlTurn = vi.fn();
const pasteToTerminal = vi.fn();
const isAppServerControlActiveSession = vi.fn<(sessionId: string | null | undefined) => boolean>();
const fetchMock = vi.fn();
const submitAppServerControlTurn = vi.fn((sessionId: string, request: unknown) =>
  sendAppServerControlTurn(sessionId, request),
);

class FakeFileReader {
  public result: string | ArrayBuffer | null = null;
  public error: Error | null = null;
  public onload: (() => void) | null = null;
  public onerror: (() => void) | null = null;

  public readAsText(file: Blob): void {
    void file
      .text()
      .then((text) => {
        this.result = text;
        this.onload?.();
      })
      .catch((error: unknown) => {
        this.error = error instanceof Error ? error : new Error(String(error));
        this.onerror?.();
      });
  }
}

vi.mock('../../stores', () => ({
  $activeSessionId: {
    get: () => 's1',
  },
}));

vi.mock('../../api/client', () => ({
  sendAppServerControlTurn,
}));

vi.mock('../appServerControl/input', () => ({
  isAppServerControlActiveSession,
  submitAppServerControlTurn,
  createAppServerControlTurnRequest: (
    text: string,
    attachments: unknown[] = [],
    sessionId?: string,
  ) => ({
    text,
    attachments,
    sessionId,
  }),
}));

vi.mock('./manager', () => ({
  pasteToTerminal,
}));

vi.mock('../sidebar/sessionDrag', () => ({
  isSessionDragActive: () => false,
}));

vi.mock('../i18n', () => ({
  t: (key: string) => key,
}));

vi.mock('../logging', () => ({
  createLogger: () => ({
    error: vi.fn(),
  }),
}));

describe('fileDrop', () => {
  beforeEach(() => {
    sendAppServerControlTurn.mockReset();
    submitAppServerControlTurn.mockClear();
    pasteToTerminal.mockReset();
    isAppServerControlActiveSession.mockReset();
    fetchMock.mockReset();
    isAppServerControlActiveSession.mockReturnValue(false);
    vi.stubGlobal('HTMLElement', class HTMLElement {});
    vi.stubGlobal('FileReader', FakeFileReader as unknown as typeof FileReader);
    vi.stubGlobal('document', {
      getElementById: () => null,
      querySelector: () => null,
      body: {
        appendChild: vi.fn(),
      },
    } as unknown as Document);
    vi.stubGlobal(
      'fetch',
      fetchMock.mockImplementation(async () => ({
        ok: true,
        json: async () => ({ path: 'Q:/repo/uploads/pic.png' }),
      })),
    );
    vi.stubGlobal('window', { isSecureContext: true } as Window & typeof globalThis);
    vi.stubGlobal('navigator', {
      clipboard: {
        read: vi.fn(async () => []),
        readText: vi.fn(async () => ''),
      },
    } as Navigator);
  });

  it('routes Smart Input attachments through the prompt API while AppServerControl is active', async () => {
    isAppServerControlActiveSession.mockReturnValue(true);
    const { handleFileDrop } = await import('./fileDrop');

    const files = [
      new File(['hello from note'], 'note.txt', { type: 'text/plain' }),
      new File(['png'], 'pic.png', { type: 'image/png' }),
    ] as unknown as FileList;

    await handleFileDrop(files);

    expect(sendAppServerControlTurn).toHaveBeenCalledTimes(1);
    expect(sendAppServerControlTurn).toHaveBeenCalledWith(
      's1',
      expect.objectContaining({
        text: expect.stringContaining('File "note.txt":\nhello from note'),
        attachments: [
          expect.objectContaining({
            kind: 'image',
            path: 'Q:/repo/uploads/pic.png',
            mimeType: 'image/png',
            displayName: 'pic.png',
          }),
        ],
      }),
    );
    expect(pasteToTerminal).not.toHaveBeenCalled();
  });

  it('keeps terminal paste behavior when AppServerControl is not active', async () => {
    const { handleFileDrop } = await import('./fileDrop');

    const files = [
      new File(['plain text'], 'note.txt', { type: 'text/plain' }),
      new File(['png'], 'pic.png', { type: 'image/png' }),
    ] as unknown as FileList;

    await handleFileDrop(files);

    expect(sendAppServerControlTurn).not.toHaveBeenCalled();
    expect(pasteToTerminal).toHaveBeenNthCalledWith(1, 's1', 'plain text', false);
    expect(pasteToTerminal).toHaveBeenNthCalledWith(
      2,
      's1',
      'Q:/repo/uploads/pic.png',
      true,
      'uploadPath',
    );
  });

  it('uses upload-plus-path paste for clipboard images even for codex-like foreground apps', async () => {
    const { handleClipboardPaste } = await import('./fileDrop');
    const read = vi.fn(async () => [
      {
        types: ['image/png'],
        getType: vi.fn(async () => new Blob(['png'], { type: 'image/png' })),
      },
    ]);
    vi.stubGlobal('navigator', {
      clipboard: {
        read,
        readText: vi.fn(async () => ''),
      },
    } as Navigator);

    const result = await handleClipboardPaste('s1', {
      foregroundName: 'codex',
      foregroundCommandLine: 'codex',
    });

    expect(result).toBe('image');
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/sessions/s1/upload?source=clipboard',
      expect.objectContaining({
        method: 'POST',
      }),
    );
    expect(pasteToTerminal).toHaveBeenCalledWith(
      's1',
      'Q:/repo/uploads/pic.png',
      true,
      'uploadPath',
    );
  });
});
