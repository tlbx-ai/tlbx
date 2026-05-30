import type { AppServerControlAttachmentReference } from '../../api/types';
import type { SmartInputComposerReferenceKind } from './smartInputComposerDraft';

export const MAX_APP_SERVER_CONTROL_IMAGE_BYTES = 10 * 1024 * 1024;

export interface PersistedAppServerControlComposerDraftAttachment {
  id: string;
  kind: 'image' | 'file';
  uploadedPath: string;
  displayName: string;
  mimeType: string | null;
  referenceCharCount: number | null;
  referenceKind: SmartInputComposerReferenceKind | null;
  referenceLabel: string | null;
  referenceLineCount: number | null;
  referenceOrdinal: number | null;
  sizeBytes: number;
}

export interface AppServerControlComposerDraftAttachment {
  id: string;
  kind: 'image' | 'file';
  file: File | null;
  uploadedPath: string | null;
  displayName: string;
  mimeType: string | null;
  referenceCharCount: number | null;
  referenceKind: SmartInputComposerReferenceKind | null;
  referenceLabel: string | null;
  referenceLineCount: number | null;
  referenceOrdinal: number | null;
  sizeBytes: number;
  previewUrl: string | null;
}

export interface ClipboardReadImageItem {
  readonly types: readonly string[];
  getType(type: string): Promise<Blob>;
}

export type AppServerControlComposerPastePart =
  | { kind: 'image'; file: File }
  | { kind: 'text'; text: string };

type ClipboardReadImageProvider = () => Promise<readonly ClipboardReadImageItem[]>;

type ClipboardTransferItem = Pick<DataTransferItem, 'kind' | 'type' | 'getAsFile'>;
type ClipboardTransferData = Pick<DataTransfer, 'files' | 'items' | 'getData'>;

const HTML_IMAGE_SRC_PATTERN = /<img\b[^>]*\bsrc\s*=\s*(?:"([^"]+)"|'([^']+)'|([^\s>]+))/gi;
const IMAGE_URL_PATH_PATTERN = /\.(png|jpe?g|gif|bmp|webp|svg|tiff?|heic|heif|avif|ico)(?:$|[?#])/i;
const CLIPBOARD_OBJECT_REPLACEMENT = '\ufffc';
const HTML_BLOCK_ELEMENTS = new Set([
  'ADDRESS',
  'ARTICLE',
  'ASIDE',
  'BLOCKQUOTE',
  'DD',
  'DIV',
  'DL',
  'DT',
  'FIGCAPTION',
  'FIGURE',
  'FOOTER',
  'H1',
  'H2',
  'H3',
  'H4',
  'H5',
  'H6',
  'HEADER',
  'LI',
  'MAIN',
  'NAV',
  'OL',
  'P',
  'PRE',
  'SECTION',
  'TABLE',
  'TBODY',
  'TD',
  'TFOOT',
  'TH',
  'THEAD',
  'TR',
  'UL',
]);

const IMAGE_EXTENSION_BY_MIME_TYPE: Record<string, string> = {
  'image/png': '.png',
  'image/jpeg': '.jpg',
  'image/gif': '.gif',
  'image/bmp': '.bmp',
  'image/webp': '.webp',
  'image/svg+xml': '.svg',
  'image/tiff': '.tiff',
  'image/heic': '.heic',
  'image/heif': '.heif',
  'image/avif': '.avif',
  'image/x-icon': '.ico',
  'image/vnd.microsoft.icon': '.ico',
};

function createDraftAttachmentId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }

  return `appServerControl-attachment-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

function hasImageExtension(fileName: string): boolean {
  return /\.(png|jpe?g|gif|bmp|webp|svg|tiff?|heic|heif|avif|ico)$/i.test(fileName);
}

export function isAppServerControlComposerImageFile(file: Pick<File, 'type' | 'name'>): boolean {
  return file.type.toLowerCase().startsWith('image/') || hasImageExtension(file.name);
}

function normalizeClipboardImageMimeType(mimeType: string | null | undefined): string | null {
  if (typeof mimeType !== 'string') {
    return null;
  }

  const normalized = mimeType.trim().toLowerCase();
  if (!normalized.startsWith('image/')) {
    return null;
  }

  return normalized === 'image/jpg' ? 'image/jpeg' : normalized;
}

function getClipboardImageExtension(mimeType: string | null): string {
  if (!mimeType) {
    return '.png';
  }

  return IMAGE_EXTENSION_BY_MIME_TYPE[mimeType] ?? '.png';
}

function decodeClipboardHtmlEntity(value: string): string {
  return value
    .replace(/&amp;/gi, '&')
    .replace(/&quot;/gi, '"')
    .replace(/&#39;|&apos;/gi, "'")
    .replace(/&lt;/gi, '<')
    .replace(/&gt;/gi, '>');
}

function trimClipboardUrl(value: string): string {
  return decodeClipboardHtmlEntity(value.trim());
}

function extractClipboardImageUrlsFromHtml(html: string): string[] {
  const matches: string[] = [];
  for (const match of html.matchAll(HTML_IMAGE_SRC_PATTERN)) {
    const candidate = trimClipboardUrl(match[1] ?? match[2] ?? match[3] ?? '');
    if (candidate) {
      matches.push(candidate);
    }
  }

  return matches;
}

function extractClipboardImageUrlsFromUriList(value: string): string[] {
  return value
    .split(/\r?\n/)
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0 && !entry.startsWith('#'));
}

function isClipboardImageDataUrl(value: string): boolean {
  return /^data:image\/[a-z0-9.+-]+(?:;charset=[^;,]+)?(?:;base64)?,/i.test(value);
}

function looksLikeClipboardImageUrl(value: string): boolean {
  return isClipboardImageDataUrl(value) || IMAGE_URL_PATH_PATTERN.test(value);
}

function getClipboardTransferImageFiles(
  clipboardData: ClipboardTransferData | null | undefined,
): File[] {
  const files: File[] = [];
  for (const file of Array.from(clipboardData?.files ?? [])) {
    if (isAppServerControlComposerImageFile(file)) {
      files.push(file);
    }
  }

  for (const item of Array.from(clipboardData?.items ?? []) as ClipboardTransferItem[]) {
    if (item.kind !== 'file') {
      continue;
    }

    if (!normalizeClipboardImageMimeType(item.type)) {
      continue;
    }

    const file = item.getAsFile();
    if (file && isAppServerControlComposerImageFile(file)) {
      files.push(file);
    }
  }

  return files;
}

function dedupeClipboardFiles(files: readonly File[]): File[] {
  const seen = new Set<string>();
  const deduped: File[] = [];

  for (const file of files) {
    const key = `${file.name}\u0000${file.size.toString(10)}\u0000${file.type}`;
    if (seen.has(key)) {
      continue;
    }

    seen.add(key);
    deduped.push(file);
  }

  return deduped;
}

function buildClipboardImageFileName(
  mimeType: string | null,
  sourceUrl: string | null,
  fallbackIndex: number,
): string {
  if (sourceUrl) {
    try {
      const url = new URL(sourceUrl, 'https://midterm.invalid');
      const candidate = decodeURIComponent(url.pathname.split('/').pop() ?? '').trim();
      if (candidate.length > 0) {
        return hasImageExtension(candidate)
          ? candidate
          : `${candidate}${getClipboardImageExtension(mimeType)}`;
      }
    } catch {
      // Keep the generated fallback name when the clipboard source is not a URL.
    }
  }

  return `clipboard-image-${fallbackIndex.toString(10)}${getClipboardImageExtension(mimeType)}`;
}

async function buildClipboardImageFileFromUrl(
  sourceUrl: string,
  fallbackIndex: number,
): Promise<File | null> {
  if (!sourceUrl || /^javascript:/i.test(sourceUrl)) {
    return null;
  }

  try {
    const response = await fetch(sourceUrl);
    if (!response.ok) {
      return null;
    }

    const blob = await response.blob();
    const mimeType = normalizeClipboardImageMimeType(blob.type);
    const file = new File(
      [blob],
      buildClipboardImageFileName(mimeType, sourceUrl, fallbackIndex),
      mimeType ? { type: mimeType } : undefined,
    );
    return isAppServerControlComposerImageFile(file) ? file : null;
  } catch {
    return null;
  }
}

function getDefaultClipboardReadImageProvider(): ClipboardReadImageProvider | null {
  if (
    typeof navigator === 'undefined' ||
    typeof navigator.clipboard === 'undefined' ||
    typeof navigator.clipboard.read !== 'function'
  ) {
    return null;
  }

  return () => navigator.clipboard.read();
}

async function getClipboardReadImageFiles(
  readClipboardItems: ClipboardReadImageProvider | null,
): Promise<File[]> {
  if (!readClipboardItems) {
    return [];
  }

  try {
    const files: File[] = [];
    const clipboardItems = await readClipboardItems();
    let index = 0;
    for (const item of clipboardItems) {
      for (const type of item.types) {
        const mimeType = normalizeClipboardImageMimeType(type);
        if (!mimeType) {
          continue;
        }

        const blob = await item.getType(type);
        index += 1;
        files.push(
          new File([blob], buildClipboardImageFileName(mimeType, null, index), { type: mimeType }),
        );
      }
    }

    return files;
  } catch {
    return [];
  }
}

function getClipboardImageSourceUrls(
  clipboardData: ClipboardTransferData | null | undefined,
): string[] {
  if (!clipboardData) {
    return [];
  }

  const html = clipboardData.getData('text/html');
  const htmlUrls = html ? extractClipboardImageUrlsFromHtml(html) : [];
  const uriList = clipboardData.getData('text/uri-list');
  const uriListUrls = uriList ? extractClipboardImageUrlsFromUriList(uriList) : [];
  const plainText = clipboardData.getData('text/plain').trim();

  if (isClipboardImageDataUrl(plainText)) {
    return [...htmlUrls, ...uriListUrls, plainText];
  }

  return [...htmlUrls, ...uriListUrls];
}

export function clipboardDataMayContainAppServerControlComposerImage(
  clipboardData: ClipboardTransferData | null | undefined,
): boolean {
  if (getClipboardTransferImageFiles(clipboardData).length > 0) {
    return true;
  }

  const html = clipboardData?.getData('text/html') ?? '';
  if (html && extractClipboardImageUrlsFromHtml(html).length > 0) {
    return true;
  }

  return getClipboardImageSourceUrls(clipboardData).some((url) => looksLikeClipboardImageUrl(url));
}

export async function extractAppServerControlComposerPasteImageFiles(
  clipboardData: ClipboardTransferData | null | undefined,
  readClipboardItems: ClipboardReadImageProvider | null = getDefaultClipboardReadImageProvider(),
): Promise<File[]> {
  const transferFiles = getClipboardTransferImageFiles(clipboardData);
  if (transferFiles.length > 0) {
    return dedupeClipboardFiles(transferFiles);
  }

  const filesFromUrls: File[] = [];
  let sourceIndex = 0;
  for (const sourceUrl of getClipboardImageSourceUrls(clipboardData)) {
    if (!looksLikeClipboardImageUrl(sourceUrl) && !/^https?:\/\//i.test(sourceUrl)) {
      continue;
    }

    sourceIndex += 1;
    const file = await buildClipboardImageFileFromUrl(sourceUrl, sourceIndex);
    if (file) {
      filesFromUrls.push(file);
    }
  }

  if (filesFromUrls.length > 0) {
    return dedupeClipboardFiles(filesFromUrls);
  }

  return dedupeClipboardFiles(await getClipboardReadImageFiles(readClipboardItems));
}

export async function extractAppServerControlComposerPasteParts(
  clipboardData: ClipboardTransferData | null | undefined,
  readClipboardItems: ClipboardReadImageProvider | null = getDefaultClipboardReadImageProvider(),
): Promise<AppServerControlComposerPastePart[]> {
  const plainText = clipboardData?.getData('text/plain') ?? '';
  const transferFiles = dedupeClipboardFiles(getClipboardTransferImageFiles(clipboardData));
  const html = clipboardData?.getData('text/html') ?? '';

  if (plainText.includes(CLIPBOARD_OBJECT_REPLACEMENT)) {
    const imageFiles =
      transferFiles.length > 0
        ? transferFiles
        : await extractAppServerControlComposerPasteImageFiles(clipboardData, readClipboardItems);
    const parts = interleavePlainTextObjectPlaceholders(plainText, imageFiles);
    if (parts.some((part) => part.kind === 'image')) {
      return parts;
    }
  }

  if (html && extractClipboardImageUrlsFromHtml(html).length > 0) {
    const htmlParts = await extractOrderedPastePartsFromHtml(html, transferFiles);
    if (htmlParts.some((part) => part.kind === 'image')) {
      return htmlParts;
    }
  }

  const imageFiles = await extractAppServerControlComposerPasteImageFiles(
    clipboardData,
    readClipboardItems,
  );
  return [
    ...(plainText ? [{ kind: 'text' as const, text: plainText }] : []),
    ...imageFiles.map((file) => ({ kind: 'image' as const, file })),
  ];
}

export function buildAppServerControlComposerAttachmentFileUrl(
  sessionId: string,
  path: string,
): string {
  return `/api/files/view?path=${encodeURIComponent(path)}&sessionId=${encodeURIComponent(sessionId)}`;
}

export function buildAppServerControlComposerAttachmentPreviewUrl(
  sessionId: string,
  path: string,
): string {
  return buildAppServerControlComposerAttachmentFileUrl(sessionId, path);
}

function interleavePlainTextObjectPlaceholders(
  text: string,
  imageFiles: readonly File[],
): AppServerControlComposerPastePart[] {
  const parts: AppServerControlComposerPastePart[] = [];
  const textParts = text.split(CLIPBOARD_OBJECT_REPLACEMENT);
  let imageIndex = 0;
  textParts.forEach((textPart, index) => {
    appendPasteTextPart(parts, textPart);
    if (index < textParts.length - 1 && imageIndex < imageFiles.length) {
      parts.push({ kind: 'image', file: imageFiles[imageIndex] as File });
      imageIndex += 1;
    }
  });

  for (; imageIndex < imageFiles.length; imageIndex++) {
    parts.push({ kind: 'image', file: imageFiles[imageIndex] as File });
  }

  return parts;
}

async function extractOrderedPastePartsFromHtml(
  html: string,
  transferFiles: readonly File[],
): Promise<AppServerControlComposerPastePart[]> {
  if (typeof DOMParser === 'undefined') {
    return extractOrderedPastePartsFromHtmlMarkup(html, transferFiles);
  }

  const document = new DOMParser().parseFromString(html, 'text/html');
  const parts: AppServerControlComposerPastePart[] = [];
  let bufferedText = '';
  let transferFileIndex = 0;
  let sourceUrlIndex = 0;

  const flushText = () => {
    appendPasteTextPart(parts, normalizeHtmlPasteText(bufferedText));
    bufferedText = '';
  };

  const appendBlockBoundary = () => {
    if (bufferedText && !bufferedText.endsWith('\n')) {
      bufferedText += '\n';
    }
  };

  const visit = async (node: Node): Promise<void> => {
    if (node.nodeType === 3) {
      bufferedText += node.textContent ?? '';
      return;
    }

    if (node.nodeType !== 1) {
      for (const child of Array.from(node.childNodes)) {
        await visit(child);
      }
      return;
    }

    const element = node as Element;
    if (element.tagName === 'BR') {
      bufferedText += '\n';
      return;
    }

    if (element.tagName === 'IMG') {
      flushText();
      const file =
        transferFiles[transferFileIndex] ??
        (await buildImageFileFromHtmlElement(element, ++sourceUrlIndex));
      transferFileIndex += transferFiles[transferFileIndex] ? 1 : 0;
      if (file) {
        parts.push({ kind: 'image', file });
      }
      return;
    }

    const block = HTML_BLOCK_ELEMENTS.has(element.tagName);
    if (block) {
      appendBlockBoundary();
    }

    for (const child of Array.from(node.childNodes)) {
      await visit(child);
    }

    if (block) {
      appendBlockBoundary();
    }
  };

  for (const child of Array.from(document.body.childNodes)) {
    await visit(child);
  }
  flushText();

  for (; transferFileIndex < transferFiles.length; transferFileIndex++) {
    parts.push({ kind: 'image', file: transferFiles[transferFileIndex] as File });
  }

  return coalescePasteTextParts(parts);
}

async function extractOrderedPastePartsFromHtmlMarkup(
  html: string,
  transferFiles: readonly File[],
): Promise<AppServerControlComposerPastePart[]> {
  const parts: AppServerControlComposerPastePart[] = [];
  const imagePattern = /<img\b[^>]*\bsrc\s*=\s*(?:"([^"]+)"|'([^']+)'|([^\s>]+))[^>]*>/gi;
  let cursor = 0;
  let transferFileIndex = 0;
  let sourceUrlIndex = 0;

  for (const match of html.matchAll(imagePattern)) {
    appendPasteTextPart(
      parts,
      normalizeHtmlPasteText(stripClipboardHtmlTags(html.slice(cursor, match.index))),
    );
    const source = trimClipboardUrl(match[1] ?? match[2] ?? match[3] ?? '');
    const file =
      transferFiles[transferFileIndex] ??
      (source ? await buildClipboardImageFileFromUrl(source, ++sourceUrlIndex) : null);
    transferFileIndex += transferFiles[transferFileIndex] ? 1 : 0;
    if (file) {
      parts.push({ kind: 'image', file });
    }
    cursor = match.index + match[0].length;
  }

  appendPasteTextPart(parts, normalizeHtmlPasteText(stripClipboardHtmlTags(html.slice(cursor))));
  for (; transferFileIndex < transferFiles.length; transferFileIndex++) {
    parts.push({ kind: 'image', file: transferFiles[transferFileIndex] as File });
  }

  return coalescePasteTextParts(parts);
}

function stripClipboardHtmlTags(html: string): string {
  return decodeClipboardHtmlEntity(
    html
      .replace(/<br\s*\/?>/gi, '\n')
      .replace(/<\/(div|p|li|tr|h[1-6]|blockquote|section|article|pre)>/gi, '\n')
      .replace(/<[^>]+>/g, ''),
  );
}

async function buildImageFileFromHtmlElement(
  image: Element,
  fallbackIndex: number,
): Promise<File | null> {
  const source = trimClipboardUrl(image.getAttribute('src') ?? '');
  if (!source || (!looksLikeClipboardImageUrl(source) && !/^https?:\/\//i.test(source))) {
    return null;
  }

  return buildClipboardImageFileFromUrl(source, fallbackIndex);
}

function appendPasteTextPart(parts: AppServerControlComposerPastePart[], text: string): void {
  if (!text) {
    return;
  }

  const last = parts[parts.length - 1];
  if (last?.kind === 'text') {
    last.text += text;
    return;
  }

  parts.push({ kind: 'text', text });
}

function coalescePasteTextParts(
  parts: readonly AppServerControlComposerPastePart[],
): AppServerControlComposerPastePart[] {
  const coalesced: AppServerControlComposerPastePart[] = [];
  for (const part of parts) {
    if (part.kind === 'text') {
      appendPasteTextPart(coalesced, part.text);
    } else {
      coalesced.push(part);
    }
  }
  return coalesced;
}

function normalizeHtmlPasteText(text: string): string {
  return text
    .replace(/\r\n/g, '\n')
    .replace(/\r/g, '\n')
    .replace(/\u00a0/g, ' ')
    .replace(/[ \t]+\n/g, '\n')
    .replace(/\n[ \t]+/g, '\n')
    .replace(/\n{3,}/g, '\n\n');
}

export function createAppServerControlComposerDraftAttachment(
  sessionId: string,
  file: Pick<File, 'name' | 'size' | 'type'>,
  uploadedPath: string,
  localFile: File | null = null,
): AppServerControlComposerDraftAttachment {
  const image = isAppServerControlComposerImageFile(file);
  return {
    id: createDraftAttachmentId(),
    kind: image ? 'image' : 'file',
    file: localFile,
    uploadedPath,
    displayName: file.name || 'attachment',
    mimeType: file.type || null,
    referenceCharCount: null,
    referenceKind: null,
    referenceLabel: null,
    referenceLineCount: null,
    referenceOrdinal: null,
    sizeBytes: file.size,
    previewUrl: image
      ? buildAppServerControlComposerAttachmentFileUrl(sessionId, uploadedPath)
      : null,
  };
}

export function hydrateAppServerControlComposerDraftAttachment(
  sessionId: string,
  attachment: PersistedAppServerControlComposerDraftAttachment,
): AppServerControlComposerDraftAttachment {
  return {
    ...attachment,
    file: null,
    previewUrl:
      attachment.kind === 'image'
        ? buildAppServerControlComposerAttachmentFileUrl(sessionId, attachment.uploadedPath)
        : null,
  };
}

export function cloneAppServerControlComposerDraftAttachments(
  attachments: readonly AppServerControlComposerDraftAttachment[],
): AppServerControlComposerDraftAttachment[] {
  return attachments.map((attachment) => ({ ...attachment }));
}

export function toPersistedAppServerControlComposerDraftAttachment(
  attachment: AppServerControlComposerDraftAttachment,
): PersistedAppServerControlComposerDraftAttachment | null {
  if (!attachment.uploadedPath) {
    return null;
  }

  return {
    id: attachment.id,
    kind: attachment.kind,
    uploadedPath: attachment.uploadedPath,
    displayName: attachment.displayName,
    mimeType: attachment.mimeType,
    referenceCharCount: attachment.referenceCharCount,
    referenceKind: attachment.referenceKind,
    referenceLabel: attachment.referenceLabel,
    referenceLineCount: attachment.referenceLineCount,
    referenceOrdinal: attachment.referenceOrdinal,
    sizeBytes: attachment.sizeBytes,
  };
}

export function releaseAppServerControlComposerDraftAttachmentPreviews(
  attachments: readonly AppServerControlComposerDraftAttachment[],
): void {
  for (const attachment of attachments) {
    if (attachment.previewUrl?.startsWith('blob:')) {
      URL.revokeObjectURL(attachment.previewUrl);
    }
  }
}

export function toAppServerControlAttachmentReference(
  attachment: AppServerControlComposerDraftAttachment,
  path: string,
): AppServerControlAttachmentReference {
  return {
    kind: attachment.kind,
    path,
    mimeType: attachment.mimeType,
    displayName: attachment.displayName,
  };
}
