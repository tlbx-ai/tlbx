import { createLogger } from '../logging';
import {
  PDF_MIME,
  buildViewUrl,
  getExtension,
  getFileName,
  isAudioFile,
  isImageFile,
  isTextFile,
  isVideoFile,
} from './rendering';

const log = createLogger('fileViewerShared');

export const FILE_CLIPBOARD_TEXT_LIMIT_BYTES = 1024 * 1024;
export const FILE_HEX_PREVIEW_TEXT_LIMIT_BYTES = 1024 * 1024;

const BINARY_PREVIEW_BYTES_PER_LINE = 16;
const BINARY_PREVIEW_MAX_CHARS_PER_LINE_WITH_NEWLINE = 76;

export const FILE_HEX_PREVIEW_PAGE_BYTES = Math.max(
  BINARY_PREVIEW_BYTES_PER_LINE,
  Math.floor(
    (FILE_HEX_PREVIEW_TEXT_LIMIT_BYTES + 1) / BINARY_PREVIEW_MAX_CHARS_PER_LINE_WITH_NEWLINE,
  ) * BINARY_PREVIEW_BYTES_PER_LINE,
);

export type FilePreviewKind = 'image' | 'video' | 'audio' | 'pdf' | 'text' | 'binary';

export interface BinaryPreviewPage {
  bytes: Uint8Array;
  startOffset: number;
  endOffsetExclusive: number;
  hasMore: boolean;
}

export interface CopyFileToClipboardOptions {
  path: string;
  sessionId?: string | null | undefined;
  mimeType?: string | null | undefined;
  size?: number | null | undefined;
  isText?: boolean | null | undefined;
  currentText?: string | null | undefined;
  currentTextIsPartial?: boolean;
  currentTextIsDirty?: boolean;
}

export function resolveFilePreviewKind(
  path: string,
  mimeType?: string | null,
  serverIsText?: boolean | null,
): FilePreviewKind {
  const mime = mimeType ?? '';
  const ext = getExtension(path).toLowerCase();

  if (isImageFile(path, mime)) {
    return 'image';
  }

  if (isVideoFile(path, mime)) {
    return 'video';
  }

  if (isAudioFile(path, mime)) {
    return 'audio';
  }

  if (mime === PDF_MIME || ext === '.pdf') {
    return 'pdf';
  }

  if (isTextFile(ext, mime, serverIsText)) {
    return 'text';
  }

  return 'binary';
}

export function buildDownloadUrl(path: string, sessionId?: string | null): string {
  let url = `/api/files/download?path=${encodeURIComponent(path)}`;
  if (sessionId) {
    url += `&sessionId=${encodeURIComponent(sessionId)}`;
  }
  return url;
}

export function downloadFile(path: string, sessionId?: string | null): void {
  const link = document.createElement('a');
  link.href = buildDownloadUrl(path, sessionId);
  link.download = getFileName(path);
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
}

export async function loadBinaryPreviewPage(options: {
  viewUrl: string;
  startOffset?: number;
  fileSize?: number | null;
}): Promise<BinaryPreviewPage> {
  const startOffset = options.startOffset ?? 0;
  const endOffsetInclusive = startOffset + FILE_HEX_PREVIEW_PAGE_BYTES - 1;
  const response = await fetch(options.viewUrl, {
    headers: {
      Range: `bytes=${startOffset}-${endOffsetInclusive}`,
    },
  });

  if (!response.ok && response.status !== 206) {
    throw new Error(`Failed to load binary preview page (${response.status})`);
  }

  let bytes = new Uint8Array(await response.arrayBuffer());
  if (bytes.length > FILE_HEX_PREVIEW_PAGE_BYTES) {
    bytes = bytes.slice(0, FILE_HEX_PREVIEW_PAGE_BYTES);
  }

  const endOffsetExclusive = startOffset + bytes.length;

  return {
    bytes,
    startOffset,
    endOffsetExclusive,
    hasMore:
      bytes.length > 0 &&
      (options.fileSize != null
        ? endOffsetExclusive < options.fileSize
        : bytes.length === FILE_HEX_PREVIEW_PAGE_BYTES),
  };
}

export async function copyFileToClipboard(
  options: CopyFileToClipboardOptions,
): Promise<'image' | 'text' | 'path' | null> {
  const { path, sessionId, mimeType, size, isText, currentText } = options;
  const kind = resolveFilePreviewKind(path, mimeType, isText);

  if (kind === 'image') {
    return copyImageFileToClipboard(path, sessionId);
  }

  if (kind === 'text') {
    return copyTextFileToClipboard(path, sessionId, size, currentText, options);
  }

  return (await writePathToClipboard(path)) ? 'path' : null;
}

function getUtf8ByteLength(value: string): number {
  return new TextEncoder().encode(value).length;
}

async function writeTextOrPathToClipboard(text: string, fallbackPath: string): Promise<boolean> {
  if (getUtf8ByteLength(text) <= FILE_CLIPBOARD_TEXT_LIMIT_BYTES) {
    return writeTextToClipboard(text);
  }

  return writePathToClipboard(fallbackPath);
}

async function writePathToClipboard(path: string): Promise<boolean> {
  return writeTextToClipboard(path);
}

async function copyImageFileToClipboard(
  path: string,
  sessionId?: string | null,
): Promise<'image' | 'path' | null> {
  const blob = await fetchFileBlob(path, sessionId);
  if (blob && (await writeImageBlobToClipboard(blob))) {
    return 'image';
  }

  return (await writePathToClipboard(path)) ? 'path' : null;
}

async function copyTextFileToClipboard(
  path: string,
  sessionId: string | null | undefined,
  size: number | null | undefined,
  currentText: string | null | undefined,
  options: CopyFileToClipboardOptions,
): Promise<'text' | 'path' | null> {
  if (size != null && size > FILE_CLIPBOARD_TEXT_LIMIT_BYTES) {
    return (await writePathToClipboard(path)) ? 'path' : null;
  }

  if (
    typeof currentText === 'string' &&
    (!options.currentTextIsPartial || options.currentTextIsDirty)
  ) {
    return (await writeTextOrPathToClipboard(currentText, path)) ? 'text' : null;
  }

  const fetchedText = await fetchFileText(path, sessionId);
  if (fetchedText !== null) {
    return (await writeTextOrPathToClipboard(fetchedText, path)) ? 'text' : null;
  }

  if (typeof currentText === 'string') {
    return (await writeTextOrPathToClipboard(currentText, path)) ? 'text' : null;
  }

  return (await writePathToClipboard(path)) ? 'path' : null;
}

function getClipboardApi(): Clipboard | null {
  if (!('clipboard' in navigator)) {
    return null;
  }

  return (navigator as Navigator & { clipboard?: Clipboard }).clipboard;
}

async function writeTextToClipboard(text: string): Promise<boolean> {
  const clipboard = getClipboardApi();
  if (clipboard === null || typeof clipboard.writeText !== 'function') {
    return false;
  }

  try {
    await clipboard.writeText(text);
    return true;
  } catch (error) {
    log.warn(() => `Clipboard text write failed: ${String(error)}`);
    return false;
  }
}

async function writeImageBlobToClipboard(blob: Blob): Promise<boolean> {
  const clipboard = getClipboardApi();
  if (
    typeof ClipboardItem === 'undefined' ||
    clipboard === null ||
    typeof clipboard.write !== 'function' ||
    !blob.type.startsWith('image/')
  ) {
    return false;
  }

  try {
    await clipboard.write([
      new ClipboardItem({
        [blob.type]: blob,
      }),
    ]);
    return true;
  } catch (error) {
    log.warn(() => `Clipboard image write failed: ${String(error)}`);
    return false;
  }
}

async function fetchFileText(path: string, sessionId?: string | null): Promise<string | null> {
  try {
    const response = await fetch(buildViewUrl(path, sessionId ?? ''));
    if (!response.ok) {
      return null;
    }

    return await response.text();
  } catch (error) {
    log.warn(() => `Failed to fetch file text for clipboard copy: ${String(error)}`);
    return null;
  }
}

async function fetchFileBlob(path: string, sessionId?: string | null): Promise<Blob | null> {
  try {
    const response = await fetch(buildViewUrl(path, sessionId ?? ''));
    if (!response.ok) {
      return null;
    }

    return await response.blob();
  } catch (error) {
    log.warn(() => `Failed to fetch file blob for clipboard copy: ${String(error)}`);
    return null;
  }
}
