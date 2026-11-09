import MarkdownIt from 'markdown-it';
import type { AppServerControlHistoryEntry } from './types';

const attachmentBlocks = new MarkdownIt('zero', { html: false }).enable([
  'fence',
  'code',
  'list',
  'blockquote',
]);

function isRepresentedImageTail(
  lines: readonly string[],
  representedPaths: ReadonlySet<string>,
  expectedImages: number,
): boolean {
  let images = 0;
  let placeholders = 0;
  for (const line of lines) {
    if (!line.trim()) continue;
    if (line === '[Image attachment]') {
      placeholders += 1;
      continue;
    }
    const path = line.match(/^- \[image\] (.+)$/)?.[1];
    if (placeholders || !path || !representedPaths.has(path)) return false;
    images += 1;
  }
  return images === expectedImages && (placeholders === 0 || placeholders === images);
}

/** Compact repeated image transport only; canonical text and attachment metadata stay intact. */
export function getUserHistoryReadingBody(
  entry: AppServerControlHistoryEntry,
  technicalDetails: boolean,
): string {
  const body = entry.body;
  if (
    entry.kind !== 'user' ||
    technicalDetails ||
    !entry.imagePreviews?.length ||
    !body.includes('Attached resource')
  ) {
    return body;
  }
  const representedPaths = new Set(
    entry.imagePreviews.flatMap((preview) => [preview.displayPath, preview.resolvedPath]),
  );
  const normalized = body.replace(/\r\n?/g, '\n');
  const lines = normalized.split('\n');
  for (const block of attachmentBlocks.parse(normalized, {})) {
    if (block.type !== 'inline' || block.level !== 1 || !block.map) continue;
    const heading = block.content.match(/^Attached resource(?:s \(([1-9]\d*)\))?:$/);
    if (!heading || block.map[1] !== block.map[0] + 1) continue;
    if (
      isRepresentedImageTail(lines.slice(block.map[1]), representedPaths, Number(heading[1] ?? 1))
    ) {
      return lines.slice(0, block.map[0]).join('\n').trimEnd();
    }
  }
  return body;
}
