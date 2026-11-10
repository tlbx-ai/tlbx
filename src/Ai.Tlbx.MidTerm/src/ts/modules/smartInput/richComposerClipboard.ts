import DOMPurify from 'dompurify';
import { t } from '../i18n';
import { isAppServerControlComposerImageFile } from './appServerControlAttachments';
import { DOMParser as ProseMirrorDOMParser, type Slice } from 'prosemirror-model';
import type { SmartInputComposerPart } from './smartInputComposerDraft';
import { isSafeComposerLink, richComposerSchema } from './richComposerDocument';

export interface RichClipboardData {
  html: string;
  text: string;
  files: File[];
  imageCount?: number;
}

export function captureRichClipboard(
  data: Pick<DataTransfer, 'files' | 'items' | 'getData'> | null,
): RichClipboardData {
  const files = Array.from(data?.files ?? []);
  // files and items expose the same transfer in two forms. Prefer files, preserving
  // distinct files even when name, size and timestamp happen to match.
  if (!files.length) {
    for (const item of Array.from(data?.items ?? [])) {
      const file = item.kind === 'file' ? item.getAsFile() : null;
      if (file) files.push(file);
    }
  }
  return { html: data?.getData('text/html') ?? '', text: data?.getData('text/plain') ?? '', files };
}

export async function readRichClipboard(): Promise<RichClipboardData> {
  const data: RichClipboardData = { html: '', text: '', files: [] };
  const clipboard = navigator.clipboard as Partial<Clipboard> | undefined;
  if (!clipboard?.read) {
    if (!clipboard?.readText) throw new Error('Browser clipboard unavailable');
    data.text = await clipboard.readText();
    return data;
  }
  for (const item of await clipboard.read()) {
    for (const type of item.types) {
      if (type !== 'text/html' && type !== 'text/plain' && !type.startsWith('image/')) continue;
      const blob = await item.getType(type);
      if (type === 'text/html') data.html += await blob.text();
      else if (type === 'text/plain') data.text += await blob.text();
      else data.files.push(new File([blob], `clipboard.${type.split('/')[1] ?? 'png'}`, { type }));
    }
  }
  return data;
}

const allowed = new Set([
  'P',
  'DIV',
  'BR',
  'H1',
  'H2',
  'H3',
  'H4',
  'H5',
  'H6',
  'STRONG',
  'B',
  'EM',
  'I',
  'UL',
  'OL',
  'LI',
  'BLOCKQUOTE',
  'PRE',
  'CODE',
  'TABLE',
  'THEAD',
  'TBODY',
  'TFOOT',
  'TR',
  'TD',
  'TH',
  'A',
  'SPAN',
]);
function clipboardHtmlFragment(html: string): DocumentFragment {
  // Retain image positions but never their URLs. No untrusted markup is assigned
  // to a live DOM sink; ProseMirror receives only this sanitized inert fragment.
  return DOMPurify.sanitize(html, {
    ALLOWED_TAGS: [...Array.from(allowed, (tag) => tag.toLowerCase()), 'img'],
    ALLOWED_ATTR: ['href', 'colspan', 'rowspan', 'data-staging-ref', 'alt'],
    ALLOW_DATA_ATTR: false,
    ALLOW_ARIA_ATTR: false,
    RETURN_DOM_FRAGMENT: true,
  });
}

function cleanElement(element: Element, validIds: ReadonlySet<string>): void {
  for (const attribute of Array.from(element.attributes)) {
    const href =
      attribute.name === 'href' && element.tagName === 'A' && isSafeComposerLink(attribute.value);
    const span =
      ['colspan', 'rowspan'].includes(attribute.name) &&
      ['TD', 'TH'].includes(element.tagName) &&
      /^[1-9]\d{0,2}$/.test(attribute.value);
    const ref = attribute.name === 'data-staging-ref' && validIds.has(attribute.value);
    if (!href && !span && !ref) element.removeAttribute(attribute.name);
  }
}

function referenceElement(id: string): HTMLSpanElement {
  const span = document.createElement('span');
  span.dataset.stagingRef = id;
  span.textContent = '\uFFFC';
  return span;
}

export function plainRichClipboardParts(
  text: string,
  referenceIds: readonly string[],
): SmartInputComposerPart[] {
  const parts: SmartInputComposerPart[] = [];
  let referenceIndex = 0;
  text.split('\uFFFC').forEach((piece, index) => {
    if (index > 0) {
      const referenceId = referenceIds[referenceIndex++];
      if (referenceId) parts.push({ kind: 'reference', referenceId });
      else parts.push({ kind: 'text', text: '\uFFFC' });
    }
    if (piece) parts.push({ kind: 'text', text: piece });
  });
  for (const referenceId of referenceIds.slice(referenceIndex))
    parts.push({ kind: 'reference', referenceId });
  return parts;
}

function plainClipboardContent(text: string, referenceIds: readonly string[]): DocumentFragment {
  const content = document.createDocumentFragment();
  let paragraph = document.createElement('p');
  content.append(paragraph);
  for (const part of plainRichClipboardParts(text, referenceIds)) {
    if (part.kind === 'reference') {
      paragraph.append(referenceElement(part.referenceId));
      continue;
    }
    part.text.split(/\r\n?|\n/).forEach((line, index) => {
      if (index > 0) {
        paragraph = document.createElement('p');
        content.append(paragraph);
      }
      paragraph.append(document.createTextNode(line));
    });
  }
  return content;
}

export function richClipboardSlice(
  data: RichClipboardData,
  imageIds: readonly string[],
  validIds: ReadonlySet<string>,
): Slice {
  const content = data.html
    ? clipboardHtmlFragment(data.html)
    : plainClipboardContent(data.text, imageIds);
  let imageIndex = data.html ? 0 : imageIds.length;
  for (const image of Array.from(content.querySelectorAll('img'))) {
    const id = imageIds[imageIndex++];
    const span = document.createElement('span');
    if (id) {
      span.dataset.stagingRef = id;
      span.textContent = '\uFFFC';
    } else
      span.textContent = `[${t('smartInput.richImageFailed')}${image.getAttribute('alt') ? ` ${image.getAttribute('alt')}` : ''}]`;
    image.replaceWith(span);
  }
  const remainder = imageIds.slice(imageIndex).filter(Boolean);
  if (remainder.length) {
    const paragraph = document.createElement('p');
    for (const id of remainder) {
      paragraph.append(referenceElement(id));
    }
    content.append(paragraph);
  }
  // Apply the composer-specific link, table-span and scoped attachment rules.
  for (const element of Array.from(content.querySelectorAll('*')).reverse())
    cleanElement(element, validIds);
  return ProseMirrorDOMParser.fromSchema(richComposerSchema).parseSlice(content);
}

export function materializeRichClipboardImages(data: RichClipboardData): RichClipboardData {
  if (!data.html || !/<img\b/i.test(data.html)) return data;
  const content = clipboardHtmlFragment(data.html);
  const supplied = data.files.filter(isAppServerControlComposerImageFile);
  // HTML describes placement, never permission to fetch image bytes. Only the
  // trusted paste event's transferred files are upload sources, including data URLs.
  return {
    ...data,
    imageCount: content.querySelectorAll('img').length,
    files: [
      ...supplied,
      ...data.files.filter((file) => !isAppServerControlComposerImageFile(file)),
    ],
  };
}

export function richClipboardReferenceIds(
  data: RichClipboardData,
  uploadedId: (file: File) => string,
): string[] {
  const ids = data.files.map(uploadedId);
  if (data.imageCount === undefined) return ids;
  const supplied = data.files.filter(isAppServerControlComposerImageFile).length;
  // A partial file list carries no reliable URL-to-file association. Preserve all
  // HTML positions as unavailable, then append the actual transferred files.
  if (supplied < data.imageCount) return [...Array<string>(data.imageCount).fill(''), ...ids];
  return ids;
}
