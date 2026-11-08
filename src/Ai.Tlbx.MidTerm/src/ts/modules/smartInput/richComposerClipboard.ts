import { t } from '../i18n';
import { isAppServerControlComposerImageFile } from './appServerControlAttachments';
import { DOMParser as ProseMirrorDOMParser, type Slice } from 'prosemirror-model';
import type { SmartInputComposerPart } from './smartInputComposerDraft';
import { isSafeComposerLink, richComposerSchema } from './richComposerDocument';

export interface RichClipboardData { html: string; text: string; files: File[]; imageCount?: number }

export function captureRichClipboard(data: Pick<DataTransfer, 'files' | 'items' | 'getData'> | null): RichClipboardData {
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

const allowed = new Set(['P', 'DIV', 'BR', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'STRONG', 'B', 'EM', 'I',
  'UL', 'OL', 'LI', 'BLOCKQUOTE', 'PRE', 'CODE', 'TABLE', 'THEAD', 'TBODY', 'TFOOT', 'TR', 'TD', 'TH', 'A', 'SPAN']);
const removed = new Set(['SCRIPT', 'STYLE', 'IFRAME', 'OBJECT', 'EMBED', 'META', 'LINK', 'FORM', 'INPUT', 'SVG', 'MATH']);

function cleanElement(element: Element, validIds: ReadonlySet<string>): void {
  if (removed.has(element.tagName)) { element.remove(); return; }
  if (!allowed.has(element.tagName)) { element.replaceWith(...Array.from(element.childNodes)); return; }
  for (const attribute of Array.from(element.attributes)) {
    const href = attribute.name === 'href' && element.tagName === 'A' && isSafeComposerLink(attribute.value);
    const span = ['colspan', 'rowspan'].includes(attribute.name) && ['TD', 'TH'].includes(element.tagName) &&
      /^[1-9]\d{0,2}$/.test(attribute.value);
    const ref = attribute.name === 'data-staging-ref' && validIds.has(attribute.value);
    if (!href && !span && !ref) element.removeAttribute(attribute.name);
  }
}

function referenceElement(id: string): HTMLSpanElement {
  const span = document.createElement('span');
  span.dataset.stagingRef = id; span.textContent = '\uFFFC';
  return span;
}

export function plainRichClipboardParts(text: string, referenceIds: readonly string[]): SmartInputComposerPart[] {
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
  for (const referenceId of referenceIds.slice(referenceIndex)) parts.push({ kind: 'reference', referenceId });
  return parts;
}

function plainClipboardContent(text: string, referenceIds: readonly string[]): DocumentFragment {
  const content = document.createDocumentFragment();
  let paragraph = document.createElement('p'); content.append(paragraph);
  for (const part of plainRichClipboardParts(text, referenceIds)) {
    if (part.kind === 'reference') { paragraph.append(referenceElement(part.referenceId)); continue; }
    part.text.split(/\r\n?|\n/).forEach((line, index) => {
      if (index > 0) { paragraph = document.createElement('p'); content.append(paragraph); }
      paragraph.append(document.createTextNode(line));
    });
  }
  return content;
}

export function richClipboardSlice(data: RichClipboardData, imageIds: readonly string[], validIds: ReadonlySet<string>): Slice {
  const template = document.createElement('template');
  let imageIndex = 0;
  if (data.html) template.innerHTML = data.html;
  else { template.content.append(plainClipboardContent(data.text, imageIds)); imageIndex = imageIds.length; }
  for (const image of Array.from(template.content.querySelectorAll('img'))) {
    const id = imageIds[imageIndex++];
    const span = document.createElement('span');
    if (id) { span.dataset.stagingRef = id; span.textContent = '\uFFFC'; }
    else span.textContent = `[${t('smartInput.richImageFailed')}${image.getAttribute('alt') ? ` ${image.getAttribute('alt')}` : ''}]`;
    image.replaceWith(span);
  }
  const remainder = imageIds.slice(imageIndex).filter(Boolean);
  if (remainder.length) {
    const paragraph = document.createElement('p');
    for (const id of remainder) {
      paragraph.append(referenceElement(id));
    }
    template.content.append(paragraph);
  }
  // Work from descendants outward so unwrapping an unknown tag cannot bypass sanitization.
  for (const element of Array.from(template.content.querySelectorAll('*')).reverse()) cleanElement(element, validIds);
  return ProseMirrorDOMParser.fromSchema(richComposerSchema).parseSlice(template.content);
}

export function materializeRichClipboardImages(data: RichClipboardData): RichClipboardData {
  if (!data.html || !/<img\b/i.test(data.html)) return data;
  const template = document.createElement('template'); template.innerHTML = data.html;
  const supplied = data.files.filter(isAppServerControlComposerImageFile);
  // HTML describes placement, never permission to fetch image bytes. Only the
  // trusted paste event's transferred files are upload sources, including data URLs.
  return { ...data, imageCount: template.content.querySelectorAll('img').length,
    files: [...supplied, ...data.files.filter((file) => !isAppServerControlComposerImageFile(file))] };
}

export function richClipboardReferenceIds(
  data: RichClipboardData, uploadedId: (file: File) => string,
): string[] {
  const ids = data.files.map(uploadedId);
  if (data.imageCount === undefined) return ids;
  const supplied = data.files.filter(isAppServerControlComposerImageFile).length;
  // A partial file list carries no reliable URL-to-file association. Preserve all
  // HTML positions as unavailable, then append the actual transferred files.
  if (supplied < data.imageCount) return [...Array<string>(data.imageCount).fill(''), ...ids];
  return ids;
}
