import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { renderPreview } from './filePreview';

class FakeElement {
  public readonly tagName: string;
  public className = '';
  public textContent = '';
  public disabled = false;
  public type = '';
  public value = '';
  public spellcheck = true;
  public readonly style: Record<string, string> = {};
  public children: FakeElement[] = [];
  public readonly classList = {
    add: (...tokens: string[]) => {
      const values = new Set(this.className.split(/\s+/).filter(Boolean));
      for (const token of tokens) {
        values.add(token);
      }
      this.className = Array.from(values).join(' ');
    },
  };
  private _innerHTML = '';
  private readonly listeners = new Map<string, Array<(event?: any) => void>>();

  public constructor(tagName: string) {
    this.tagName = tagName.toUpperCase();
  }

  public set innerHTML(value: string) {
    this._innerHTML = value;
    this.children = [];
  }

  public get innerHTML(): string {
    return this._innerHTML;
  }

  public appendChild<T extends FakeElement>(child: T): T {
    this.children.push(child);
    return child;
  }

  public addEventListener(type: string, handler: (event?: any) => void): void {
    const handlers = this.listeners.get(type) ?? [];
    handlers.push(handler);
    this.listeners.set(type, handlers);
  }

  public setAttribute(_name: string, _value: string): void {}

  public dispatchEvent(event: { type: string }): boolean {
    const handlers = this.listeners.get(event.type) ?? [];
    for (const handler of handlers) {
      handler(event);
    }
    return true;
  }

  public click(): void {
    this.dispatchEvent({ type: 'click' });
  }

  public querySelector(selector: string): FakeElement | null {
    return findMatchingElement(this.children, selector);
  }
}

function findMatchingElement(elements: FakeElement[], selector: string): FakeElement | null {
  for (const element of elements) {
    if (matchesSelector(element, selector)) {
      return element;
    }

    const nested = findMatchingElement(element.children, selector);
    if (nested) {
      return nested;
    }
  }

  return null;
}

function matchesSelector(element: FakeElement, selector: string): boolean {
  if (selector.startsWith('.')) {
    const className = selector.slice(1);
    return element.className.split(/\s+/).includes(className);
  }

  return element.tagName.toLowerCase() === selector.toLowerCase();
}

const originalDocument = globalThis.document;
const originalFetch = globalThis.fetch;
const renderingMocks = vi.hoisted(() => {
  const createElement = (tagName: string) => {
    const listeners = new Map<string, Array<(event?: any) => void>>();
    const element: any = {
      tagName: tagName.toUpperCase(),
      className: '',
      textContent: '',
      disabled: false,
      type: '',
      value: '',
      spellcheck: true,
      style: {},
      children: [] as any[],
      classList: {
        add: (...tokens: string[]) => {
          const values = new Set(
            String(element.className ?? '')
              .split(/\s+/)
              .filter(Boolean),
          );
          for (const token of tokens) {
            values.add(token);
          }
          element.className = Array.from(values).join(' ');
        },
      },
      appendChild(child: any) {
        element.children.push(child);
        return child;
      },
      addEventListener(type: string, handler: (event?: any) => void) {
        const handlers = listeners.get(type) ?? [];
        handlers.push(handler);
        listeners.set(type, handlers);
      },
      dispatchEvent(event: { type: string }) {
        const handlers = listeners.get(event.type) ?? [];
        for (const handler of handlers) {
          handler(event);
        }
        return true;
      },
      click() {
        element.dispatchEvent({ type: 'click' });
      },
    };
    return element;
  };

  const isTextFileMock = vi.fn(() => true);
  const createLineNumberedEditorMock = vi.fn((text: string, extraClassNames: string[] = []) => {
    const root = createElement('div');
    root.className = ['file-viewer-editor-shell', ...extraClassNames].join(' ');

    const textarea = createElement('textarea');
    textarea.className = 'file-viewer-textarea';
    textarea.value = text;
    root.appendChild(textarea);

    return {
      root,
      textarea,
      setText: (nextText: string) => {
        textarea.value = nextText;
      },
    };
  });

  const createLineNumberedViewerMock = vi.fn((text: string) => {
    const root = createElement('div');
    root.className = 'file-viewer-readonly-shell';

    const pre = createElement('pre');
    pre.className = 'file-viewer-text';
    pre.textContent = text;
    root.appendChild(pre);

    return {
      root,
      pre,
      setText: (nextText: string) => {
        pre.textContent = nextText;
      },
    };
  });

  const formatBinaryDumpMock = vi.fn((bytes: Uint8Array) => `binary:${bytes.length}`);

  return {
    isTextFileMock,
    createLineNumberedEditorMock,
    createLineNumberedViewerMock,
    formatBinaryDumpMock,
  };
});

const sharedMocks = vi.hoisted(() => ({
  resolveFilePreviewKindMock: vi.fn(() => 'text' as const),
  copyFileToClipboardMock: vi.fn(),
  downloadFileMock: vi.fn(),
  loadBinaryPreviewPageMock: vi.fn(),
}));

const imageViewMocks = vi.hoisted(() => ({
  createImageViewMock: vi.fn(() => ({
    tagName: 'DIV',
    className: 'file-viewer-image-stage',
    children: [],
  })),
}));

vi.mock('../logging', () => ({
  createLogger: () => ({
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    verbose: vi.fn(),
  }),
}));

vi.mock('../i18n', () => ({
  t: (key: string) => key,
}));

vi.mock('../fileViewer/rendering', () => ({
  formatSize: (size: number) => `${size}`,
  getExtension: (name: string) => name.slice(name.lastIndexOf('.')).toLowerCase(),
  formatViewerHeaderSubtitle: (path: string, metadata?: string | null) =>
    metadata ? `${path} | ${metadata}` : path,
  highlightCode: (text: string) => `highlight:${text}`,
  buildViewUrl: (path: string, sessionId: string) =>
    `/api/files/view?path=${encodeURIComponent(path)}&sessionId=${encodeURIComponent(sessionId)}`,
  createLineNumberedEditor: renderingMocks.createLineNumberedEditorMock,
  createLineNumberedViewer: renderingMocks.createLineNumberedViewerMock,
  formatBinaryDump: renderingMocks.formatBinaryDumpMock,
}));

vi.mock('../fileViewer/shared', () => ({
  copyFileToClipboard: sharedMocks.copyFileToClipboardMock,
  downloadFile: sharedMocks.downloadFileMock,
  loadBinaryPreviewPage: sharedMocks.loadBinaryPreviewPageMock,
  resolveFilePreviewKind: sharedMocks.resolveFilePreviewKindMock,
}));

vi.mock('../fileViewer/imageView', () => ({
  createImageView: imageViewMocks.createImageViewMock,
}));

async function flushPromises(): Promise<void> {
  await Promise.resolve();
  await Promise.resolve();
}

describe('filePreview', () => {
  beforeAll(() => {
    Object.assign(globalThis, {
      document: {
        createElement: (tagName: string) => new FakeElement(tagName),
      },
    });
  });

  afterAll(() => {
    Object.assign(globalThis, {
      document: originalDocument,
      fetch: originalFetch,
    });
  });

  beforeEach(() => {
    globalThis.fetch = vi.fn();
    renderingMocks.isTextFileMock.mockReturnValue(true);
    renderingMocks.createLineNumberedEditorMock.mockClear();
    renderingMocks.createLineNumberedViewerMock.mockClear();
    renderingMocks.formatBinaryDumpMock.mockClear();
    sharedMocks.resolveFilePreviewKindMock.mockReset();
    sharedMocks.resolveFilePreviewKindMock.mockReturnValue('text');
    sharedMocks.copyFileToClipboardMock.mockReset();
    sharedMocks.downloadFileMock.mockReset();
    sharedMocks.loadBinaryPreviewPageMock.mockReset();
    sharedMocks.loadBinaryPreviewPageMock.mockResolvedValue({
      bytes: new Uint8Array([0x41, 0x42]),
      startOffset: 0,
      endOffsetExclusive: 2,
      hasMore: false,
    });
  });

  it('opens markdown files in editor mode with a save button', async () => {
    vi.mocked(globalThis.fetch).mockResolvedValueOnce({
      ok: true,
      text: async () => '# Title',
    } as Response);

    const container = new FakeElement('div');
    const entry = {
      name: 'README.md',
      fullPath: 'Q:\\repos\\MidTerm\\README.md',
      isDirectory: false,
    };

    renderPreview(container as unknown as HTMLElement, entry, 'session-1');
    await flushPromises();

    const textarea = container.querySelector('textarea');
    const saveBtn = container.querySelector('.preview-save-btn');
    const editBtn = container.querySelector('.preview-editor-btn');
    const actionBtn = container.querySelector('.preview-toolbar-action-btn');

    expect(textarea).not.toBeNull();
    expect(saveBtn?.style.display).toBe('');
    expect(saveBtn?.disabled).toBe(true);
    expect(editBtn?.style.display).toBe('none');
    expect(actionBtn).not.toBeNull();
  });

  it('saves edited markdown content through the file save endpoint', async () => {
    vi.mocked(globalThis.fetch)
      .mockResolvedValueOnce({
        ok: true,
        text: async () => '# Title',
      } as Response)
      .mockResolvedValueOnce({
        ok: true,
        text: async () => '',
      } as Response);

    const container = new FakeElement('div');
    const entry = {
      name: 'README.md',
      fullPath: 'Q:\\repos\\MidTerm\\README.md',
      isDirectory: false,
    };

    renderPreview(container as unknown as HTMLElement, entry, 'session-1');
    await flushPromises();

    const textarea = container.querySelector('textarea');
    const saveBtn = container.querySelector('.preview-save-btn');

    expect(textarea).not.toBeNull();
    expect(saveBtn).not.toBeNull();

    textarea!.value = '# Updated';
    textarea!.dispatchEvent({ type: 'input' });
    expect(saveBtn!.disabled).toBe(false);

    saveBtn!.click();
    await flushPromises();

    expect(globalThis.fetch).toHaveBeenNthCalledWith(
      2,
      '/api/files/save?sessionId=session-1',
      expect.objectContaining({
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          path: 'Q:\\repos\\MidTerm\\README.md',
          content: '# Updated',
        }),
      }),
    );
    expect(saveBtn!.disabled).toBe(true);
  });

  it('renders binary files through the shared line-numbered viewer', async () => {
    sharedMocks.resolveFilePreviewKindMock.mockReturnValue('binary');

    const container = new FakeElement('div');
    const entry = {
      name: 'archive.bin',
      fullPath: 'Q:\\repos\\MidTerm\\archive.bin',
      isDirectory: false,
      mimeType: 'application/octet-stream',
      size: 2,
    };

    renderPreview(container as unknown as HTMLElement, entry, 'session-1');
    await flushPromises();

    expect(sharedMocks.loadBinaryPreviewPageMock).toHaveBeenCalledWith({
      viewUrl: '/api/files/view?path=Q%3A%5Crepos%5CMidTerm%5Carchive.bin&sessionId=session-1',
      fileSize: 2,
    });
    expect(renderingMocks.formatBinaryDumpMock).toHaveBeenCalledWith(
      new Uint8Array([0x41, 0x42]),
      0,
    );
    expect(renderingMocks.createLineNumberedViewerMock).toHaveBeenCalledWith('binary:2', [
      'file-viewer-binary-shell',
    ]);
    expect(container.querySelector('.preview-toolbar-name')?.textContent).toBe('archive.bin');
    expect(container.querySelector('.preview-toolbar-subtitle')?.textContent).toBe(
      'Q:\\repos\\MidTerm\\archive.bin | application/octet-stream | 2',
    );
  });

  it('shows a load-more button for paged binary previews and replaces the dump on click', async () => {
    sharedMocks.resolveFilePreviewKindMock.mockReturnValue('binary');
    sharedMocks.loadBinaryPreviewPageMock
      .mockResolvedValueOnce({
        bytes: new Uint8Array([0x41, 0x42]),
        startOffset: 0,
        endOffsetExclusive: 2,
        hasMore: true,
      })
      .mockResolvedValueOnce({
        bytes: new Uint8Array([0x43, 0x44]),
        startOffset: 2,
        endOffsetExclusive: 4,
        hasMore: false,
      });

    const container = new FakeElement('div');
    const entry = {
      name: 'archive.bin',
      fullPath: 'Q:\\repos\\MidTerm\\archive.bin',
      isDirectory: false,
      mimeType: 'application/octet-stream',
      size: 4,
    };

    renderPreview(container as unknown as HTMLElement, entry, 'session-1');
    await flushPromises();

    const loadMoreBtn = container.querySelector('.file-viewer-load-more');
    expect(loadMoreBtn?.textContent).toBe('fileViewer.loadMore (4)');

    loadMoreBtn?.click();
    await flushPromises();

    expect(sharedMocks.loadBinaryPreviewPageMock).toHaveBeenNthCalledWith(2, {
      viewUrl: '/api/files/view?path=Q%3A%5Crepos%5CMidTerm%5Carchive.bin&sessionId=session-1',
      startOffset: 2,
      fileSize: 4,
    });
    expect(renderingMocks.formatBinaryDumpMock).toHaveBeenNthCalledWith(
      2,
      new Uint8Array([0x43, 0x44]),
      2,
    );
    expect(renderingMocks.createLineNumberedViewerMock.mock.results[0]?.value.pre.textContent).toBe(
      'binary:2',
    );
  });

  it('renders image previews inside the shared shell with toolbar actions', async () => {
    sharedMocks.resolveFilePreviewKindMock.mockReturnValue('image');

    const container = new FakeElement('div');
    const entry = {
      name: 'diagram.png',
      fullPath: 'Q:\\repos\\MidTerm\\diagram.png',
      isDirectory: false,
      mimeType: 'image/png',
      size: 128,
    };

    renderPreview(container as unknown as HTMLElement, entry, 'session-1');

    expect(container.querySelector('.preview-toolbar-name')?.textContent).toBe('diagram.png');
    expect(container.querySelector('.file-viewer-image-stage')).not.toBeNull();
    expect(imageViewMocks.createImageViewMock).toHaveBeenCalledWith(
      '/api/files/view?path=Q%3A%5Crepos%5CMidTerm%5Cdiagram.png&sessionId=session-1',
      'diagram.png',
    );
    expect(container.querySelector('.preview-toolbar-action-btn')).not.toBeNull();
  });
});
