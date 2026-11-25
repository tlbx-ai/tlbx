/**
 * File Viewer Rendering Utilities
 *
 * Shared rendering functions, file type detection, editor/viewer surface
 * creation, and formatting helpers used by both the file viewer and file
 * browser modules.
 */

import { $currentSettings } from '../../stores';
import { escapeHtml } from '../../utils';
export { renderMarkdown } from '../../utils/markdown';
import { buildTerminalFontStack } from '../terminal/fontConfig';
import { getEffectiveTerminalFontSize } from '../terminal/fontSize';

export const TEXT_EXTENSIONS = new Set([
  '.txt',
  '.md',
  '.json',
  '.xml',
  '.yaml',
  '.yml',
  '.toml',
  '.ini',
  '.cfg',
  '.conf',
  '.js',
  '.ts',
  '.jsx',
  '.tsx',
  '.css',
  '.scss',
  '.less',
  '.html',
  '.htm',
  '.py',
  '.rb',
  '.go',
  '.rs',
  '.java',
  '.c',
  '.cpp',
  '.h',
  '.hpp',
  '.cs',
  '.sh',
  '.bash',
  '.zsh',
  '.fish',
  '.ps1',
  '.psm1',
  '.bat',
  '.cmd',
  '.sql',
  '.graphql',
  '.proto',
  '.csv',
  '.log',
  '.env',
  '.gitignore',
  '.dockerignore',
  '.editorconfig',
  '.eslintrc',
  '.prettierrc',
  'Makefile',
  'Dockerfile',
  'Vagrantfile',
  '.lua',
  '.dart',
  '.kt',
  '.swift',
  '.scala',
  '.clj',
  '.cljs',
  '.ex',
  '.exs',
  '.erl',
  '.hrl',
  '.r',
  '.R',
  '.m',
  '.mm',
  '.pl',
  '.pm',
  '.tcl',
  '.v',
  '.vh',
  '.sv',
  '.svh',
  '.vhd',
  '.vhdl',
  '.asm',
  '.s',
  '.S',
  '.f',
  '.f90',
  '.f95',
  '.for',
  '.cmake',
  '.make',
  '.mk',
  '.gradle',
  '.groovy',
  '.tf',
  '.hcl',
  '.nix',
  '.dhall',
  '.jsonc',
  '.json5',
  '.zig',
  '.nim',
  '.cr',
  '.jl',
  '.elm',
  '.purs',
  '.ml',
  '.mli',
  '.fs',
  '.fsi',
  '.fsx',
  '.vue',
  '.svelte',
]);

export const IMAGE_EXTENSIONS = new Set(['.png', '.jpg', '.jpeg', '.gif', '.webp', '.svg', '.bmp']);

export const VIDEO_EXTENSIONS = new Set(['.mp4', '.webm', '.mov', '.avi']);

export const AUDIO_EXTENSIONS = new Set(['.mp3', '.wav', '.ogg', '.aac']);

export const IMAGE_MIMES = [
  'image/png',
  'image/jpeg',
  'image/gif',
  'image/webp',
  'image/svg+xml',
  'image/bmp',
];

export const VIDEO_MIMES = ['video/mp4', 'video/webm', 'video/ogg', 'video/quicktime'];
export const AUDIO_MIMES = ['audio/mpeg', 'audio/ogg', 'audio/wav', 'audio/webm', 'audio/aac'];
export const PDF_MIME = 'application/pdf';

const DEFAULT_TERMINAL_FONT_SIZE = 14;
const DEFAULT_VIEWPORT_WIDTH = 1024;
const VIEWER_LINE_HEIGHT = 1.5;
const BINARY_BYTES_PER_LINE = 16;

export function getFileName(path: string): string {
  const parts = path.replace(/\\/g, '/').split('/');
  return parts[parts.length - 1] || path;
}

export function getExtension(path: string): string {
  const name = getFileName(path);
  const dotIndex = name.lastIndexOf('.');
  return dotIndex >= 0 ? name.substring(dotIndex) : '';
}

export function joinPath(base: string, name: string): string {
  const sep = base.includes('\\') ? '\\' : '/';
  return base.endsWith(sep) ? base + name : base + sep + name;
}

export function getFileIcon(name: string, isDir?: boolean): string {
  if (isDir) return '📁';
  const ext = getExtension(name).toLowerCase();
  if (IMAGE_EXTENSIONS.has(ext)) return '🖼️';
  if (VIDEO_EXTENSIONS.has(ext)) return '🎬';
  if (AUDIO_EXTENSIONS.has(ext)) return '🎵';
  if (ext === '.pdf') return '📕';
  if (['.zip', '.tar', '.gz', '.7z', '.rar'].includes(ext)) return '📦';
  if (['.js', '.ts', '.py', '.go', '.rs', '.java', '.c', '.cpp', '.cs'].includes(ext)) return '📝';
  if (['.json', '.xml', '.yaml', '.yml', '.toml'].includes(ext)) return '⚙️';
  return '📄';
}

export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${String(bytes)} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  return `${(bytes / 1024 / 1024 / 1024).toFixed(1)} GB`;
}

export function formatDate(isoDate: string): string {
  try {
    return new Date(isoDate).toLocaleString();
  } catch {
    return isoDate;
  }
}

/**
 * Shared viewer header contract:
 * - The surrounding surface chrome owns the header for every viewer type.
 * - Viewer bodies should start with content, not a renderer-specific top bar.
 * - Variant metadata belongs in the shared subtitle text so text, binary, and
 *   future viewers keep the same top alignment.
 */
export function formatViewerHeaderSubtitle(path: string, metadata?: string | null): string {
  return metadata ? `${path} | ${metadata}` : path;
}

export function isTextFile(ext: string, mime: string, serverIsText?: boolean | null): boolean {
  if (serverIsText != null) return serverIsText;
  if (TEXT_EXTENSIONS.has(ext)) return true;
  if (mime.startsWith('text/')) return true;
  if (mime === 'application/json' || mime === 'application/xml') return true;
  return false;
}

export function isImageFile(name: string, mime?: string): boolean {
  if (mime && IMAGE_MIMES.includes(mime)) return true;
  return IMAGE_EXTENSIONS.has(getExtension(name).toLowerCase());
}

export function isVideoFile(name: string, mime?: string): boolean {
  if (mime && VIDEO_MIMES.includes(mime)) return true;
  return VIDEO_EXTENSIONS.has(getExtension(name).toLowerCase());
}

export function isAudioFile(name: string, mime?: string): boolean {
  if (mime && AUDIO_MIMES.includes(mime)) return true;
  return AUDIO_EXTENSIONS.has(getExtension(name).toLowerCase());
}

export function buildViewUrl(path: string, sessionId: string): string {
  let url = `/api/files/view?path=${encodeURIComponent(path)}`;
  if (sessionId) {
    url += `&sessionId=${encodeURIComponent(sessionId)}`;
  }
  return url;
}

export function buildLineNumberText(lineCount: number, startLine = 1): string {
  const safeLineCount = Math.max(lineCount, 1);
  const lines = new Array<string>(safeLineCount);
  for (let i = 0; i < safeLineCount; i++) {
    lines[i] = String(i + startLine);
  }
  return lines.join('\n');
}

export function getLineCount(text: string): number {
  return Math.max(text.split('\n').length, 1);
}

export function formatBinaryDump(bytes: Uint8Array, startOffset = 0): string {
  if (bytes.length === 0) {
    return '';
  }

  const lines: string[] = [];

  for (let offset = 0; offset < bytes.length; offset += BINARY_BYTES_PER_LINE) {
    const chunk = bytes.slice(offset, offset + BINARY_BYTES_PER_LINE);
    const hex = Array.from(chunk, (value) => value.toString(16).toUpperCase().padStart(2, '0'))
      .join(' ')
      .padEnd(BINARY_BYTES_PER_LINE * 3 - 1, ' ');
    const ascii = Array.from(chunk, (value) =>
      value >= 0x20 && value <= 0x7e ? String.fromCharCode(value) : '.',
    ).join('');

    const absoluteOffset = startOffset + offset;
    lines.push(`${absoluteOffset.toString(16).toUpperCase().padStart(8, '0')}  ${hex}  ${ascii}`);
  }

  return lines.join('\n');
}

function getViewerFontMetrics(lineCount: number): {
  fontFamily: string;
  fontSizePx: number;
  gutterWidthCh: number;
} {
  const settings = $currentSettings.get();
  const viewportWidth = typeof window === 'undefined' ? DEFAULT_VIEWPORT_WIDTH : window.innerWidth;
  const fontSizePx = getEffectiveTerminalFontSize(
    settings?.fontSize ?? DEFAULT_TERMINAL_FONT_SIZE,
    viewportWidth,
  );

  return {
    fontFamily: buildTerminalFontStack(settings?.fontFamily),
    fontSizePx,
    gutterWidthCh: Math.max(String(Math.max(lineCount, 1)).length, 2),
  };
}

function applyViewerFontMetrics(container: HTMLElement, lineCount: number): void {
  const metrics = getViewerFontMetrics(lineCount);
  container.style.setProperty('--file-viewer-font-family', metrics.fontFamily);
  container.style.setProperty('--file-viewer-font-size', `${metrics.fontSizePx}px`);
  container.style.setProperty('--file-viewer-line-height', String(VIEWER_LINE_HEIGHT));
  container.style.setProperty('--file-viewer-gutter-width', `${metrics.gutterWidthCh}ch`);
}

export interface LineNumberedEditorHandle {
  root: HTMLDivElement;
  textarea: HTMLTextAreaElement;
  setText: (text: string) => void;
}

export interface LineNumberedViewerHandle {
  root: HTMLDivElement;
  pre: HTMLPreElement;
  setText: (text: string, html?: string) => void;
}

export function createLineNumberedEditor(
  initialText: string,
  extraClassNames: string[] = [],
): LineNumberedEditorHandle {
  const root = document.createElement('div');
  root.className = ['file-viewer-editor-shell', ...extraClassNames].join(' ');

  const gutter = document.createElement('div');
  gutter.className = 'file-viewer-line-gutter';
  gutter.setAttribute('aria-hidden', 'true');

  const numbers = document.createElement('pre');
  numbers.className = 'file-viewer-line-numbers';
  gutter.appendChild(numbers);

  const textarea = document.createElement('textarea');
  textarea.className = 'file-viewer-textarea';
  textarea.wrap = 'off';
  textarea.spellcheck = false;
  let syncingScroll = false;

  const updateMetrics = (text: string): void => {
    const lineCount = getLineCount(text);
    applyViewerFontMetrics(root, lineCount);
    numbers.textContent = buildLineNumberText(lineCount);
    gutter.scrollTop = textarea.scrollTop;
  };

  textarea.addEventListener('input', () => {
    updateMetrics(textarea.value);
  });
  textarea.addEventListener('scroll', () => {
    if (syncingScroll) {
      return;
    }
    syncingScroll = true;
    gutter.scrollTop = textarea.scrollTop;
    syncingScroll = false;
  });
  gutter.addEventListener('scroll', () => {
    if (syncingScroll) {
      return;
    }
    syncingScroll = true;
    textarea.scrollTop = gutter.scrollTop;
    syncingScroll = false;
  });

  const setText = (text: string): void => {
    textarea.value = text;
    updateMetrics(text);
  };

  setText(initialText);

  root.appendChild(gutter);
  root.appendChild(textarea);

  return { root, textarea, setText };
}

export function createLineNumberedViewer(
  text: string,
  extraClassNames: string[] = [],
  html?: string,
  startLine = 1,
): LineNumberedViewerHandle {
  const root = document.createElement('div');
  root.className = ['file-viewer-readonly-shell', ...extraClassNames].join(' ');

  const gutter = document.createElement('div');
  gutter.className = 'file-viewer-line-gutter';
  gutter.setAttribute('aria-hidden', 'true');

  const numbers = document.createElement('pre');
  numbers.className = 'file-viewer-line-numbers';
  gutter.appendChild(numbers);

  const scroller = document.createElement('div');
  scroller.className = 'file-viewer-text-scroller';

  const pre = document.createElement('pre');
  pre.className = 'file-viewer-text';
  scroller.appendChild(pre);
  let syncingScroll = false;

  const setText = (nextText: string, nextHtml?: string): void => {
    const lineCount = getLineCount(nextText);
    applyViewerFontMetrics(root, lineCount + startLine - 1);
    numbers.textContent = buildLineNumberText(lineCount, startLine);
    if (nextHtml !== undefined) {
      pre.innerHTML = nextHtml;
    } else {
      pre.textContent = nextText;
    }
    gutter.scrollTop = scroller.scrollTop;
  };

  scroller.addEventListener('scroll', () => {
    if (syncingScroll) {
      return;
    }
    syncingScroll = true;
    gutter.scrollTop = scroller.scrollTop;
    syncingScroll = false;
  });
  gutter.addEventListener('scroll', () => {
    if (syncingScroll) {
      return;
    }
    syncingScroll = true;
    scroller.scrollTop = gutter.scrollTop;
    syncingScroll = false;
  });

  setText(text, html);

  root.appendChild(gutter);
  root.appendChild(scroller);

  return { root, pre, setText };
}

// Universal syntax highlighting

const UNIVERSAL_KEYWORDS = [
  'if',
  'else',
  'elif',
  'for',
  'foreach',
  'while',
  'do',
  'switch',
  'case',
  'default',
  'break',
  'continue',
  'return',
  'goto',
  'throw',
  'try',
  'catch',
  'except',
  'finally',
  'with',
  'match',
  'when',
  'then',
  'fi',
  'done',
  'esac',
  'function',
  'func',
  'fn',
  'def',
  'sub',
  'proc',
  'method',
  'lambda',
  'class',
  'struct',
  'interface',
  'trait',
  'impl',
  'enum',
  'union',
  'type',
  'typedef',
  'const',
  'let',
  'var',
  'mut',
  'val',
  'final',
  'public',
  'private',
  'protected',
  'internal',
  'static',
  'readonly',
  'abstract',
  'virtual',
  'async',
  'await',
  'yield',
  'defer',
  'go',
  'import',
  'export',
  'from',
  'use',
  'using',
  'require',
  'include',
  'package',
  'module',
  'namespace',
  'mod',
  'crate',
  'extern',
  'new',
  'delete',
  'extends',
  'implements',
  'override',
  'true',
  'false',
  'True',
  'False',
  'null',
  'nil',
  'None',
  'undefined',
  'void',
  'this',
  'self',
  'super',
  'base',
  'and',
  'or',
  'not',
  'in',
  'is',
  'as',
  'typeof',
  'instanceof',
  'sizeof',
  'exit',
  'local',
  'select',
  'chan',
  'map',
  'range',
  'move',
  'pass',
  'pub',
];

const RE_KEYWORDS = new RegExp(`\\b(${UNIVERSAL_KEYWORDS.join('|')})\\b`, 'g');

const RE_NUMBER = /\b(0x[0-9a-fA-F_]+|0b[01_]+|0o[0-7_]+|\d[\d_]*\.?[\d_]*(?:[eE][+-]?\d+)?)\b/g;
const RE_TYPE = /\b([A-Z][a-z]+[A-Za-z0-9]*)\b(?!\s*\()/g;
const RE_FUNCTION_CALL = /\b([a-zA-Z_][a-zA-Z0-9_]*)\b(?=\s*\()/g;

interface HighlightSegment {
  kind: 'code' | 'comment' | 'string';
  text: string;
}

interface InlineHighlightSegment {
  kind: 'plain' | 'html';
  text: string;
}

function pushHighlightSegment(
  segments: HighlightSegment[],
  kind: HighlightSegment['kind'],
  text: string,
): void {
  if (text.length === 0) {
    return;
  }

  const last = segments[segments.length - 1];
  if (last?.kind === kind) {
    last.text += text;
    return;
  }

  segments.push({ kind, text });
}

function consumeQuotedString(text: string, start: number): number {
  const quote = text[start];
  let index = start + 1;

  while (index < text.length) {
    const current = text[index];
    if (current === '\\') {
      index += index + 1 < text.length ? 2 : 1;
      continue;
    }

    if (current === quote) {
      return index + 1;
    }

    if (quote !== '`' && (current === '\r' || current === '\n')) {
      return index;
    }

    index += 1;
  }

  return text.length;
}

function consumeLineComment(text: string, start: number): number {
  let index = start;
  while (index < text.length && text[index] !== '\n') {
    index += 1;
  }
  return index;
}

function tryConsumeHighlightBoundary(
  text: string,
  cursor: number,
): { kind: HighlightSegment['kind']; end: number } | null {
  const current = text[cursor];
  const next = text[cursor + 1] ?? '';

  if (current === '"' || current === "'" || current === '`') {
    return {
      kind: 'string',
      end: consumeQuotedString(text, cursor),
    };
  }

  if (current === '/' && next === '/') {
    return {
      kind: 'comment',
      end: consumeLineComment(text, cursor),
    };
  }

  if (current === '#' && next !== '[' && next !== '(') {
    return {
      kind: 'comment',
      end: consumeLineComment(text, cursor),
    };
  }

  if (current === '-' && next === '-') {
    return {
      kind: 'comment',
      end: consumeLineComment(text, cursor),
    };
  }

  return null;
}

function isHighlightBoundary(text: string, index: number): boolean {
  return tryConsumeHighlightBoundary(text, index) !== null;
}

function tokenizeHighlightSegments(text: string): HighlightSegment[] {
  const segments: HighlightSegment[] = [];
  let cursor = 0;

  while (cursor < text.length) {
    const boundary = tryConsumeHighlightBoundary(text, cursor);
    if (boundary) {
      pushHighlightSegment(segments, boundary.kind, text.slice(cursor, boundary.end));
      cursor = boundary.end;
      continue;
    }

    let end = cursor + 1;
    while (end < text.length) {
      if (isHighlightBoundary(text, end)) {
        break;
      }
      end += 1;
    }

    pushHighlightSegment(segments, 'code', text.slice(cursor, end));
    cursor = end;
  }

  return segments;
}

function wrapMatches(
  segments: InlineHighlightSegment[],
  pattern: RegExp,
  className: string,
): InlineHighlightSegment[] {
  const nextSegments: InlineHighlightSegment[] = [];

  for (const segment of segments) {
    if (segment.kind === 'html' || segment.text.length === 0) {
      nextSegments.push(segment);
      continue;
    }

    pattern.lastIndex = 0;
    let cursor = 0;
    let match: RegExpExecArray | null;

    while ((match = pattern.exec(segment.text)) !== null) {
      const start = match.index;
      const value = match[0];

      if (start > cursor) {
        nextSegments.push({ kind: 'plain', text: segment.text.slice(cursor, start) });
      }

      nextSegments.push({
        kind: 'html',
        text: `<span class="${className}">${value}</span>`,
      });
      cursor = start + value.length;
    }

    if (cursor < segment.text.length) {
      nextSegments.push({ kind: 'plain', text: segment.text.slice(cursor) });
    }
  }

  return nextSegments;
}

function highlightCodeSegment(text: string): string {
  let segments: InlineHighlightSegment[] = [{ kind: 'plain', text: escapeHtml(text) }];

  RE_NUMBER.lastIndex = 0;
  RE_KEYWORDS.lastIndex = 0;
  RE_TYPE.lastIndex = 0;
  RE_FUNCTION_CALL.lastIndex = 0;

  segments = wrapMatches(segments, RE_NUMBER, 'hl-number');
  segments = wrapMatches(segments, RE_KEYWORDS, 'hl-keyword');
  segments = wrapMatches(segments, RE_TYPE, 'hl-type');
  segments = wrapMatches(segments, RE_FUNCTION_CALL, 'hl-function');

  return segments.map((segment) => segment.text).join('');
}

export function highlightCode(text: string, _ext: string): string {
  return tokenizeHighlightSegments(text)
    .map((segment) => {
      const escaped = escapeHtml(segment.text);
      if (segment.kind === 'comment') {
        return `<span class="hl-comment">${escaped}</span>`;
      }
      if (segment.kind === 'string') {
        return `<span class="hl-string">${escaped}</span>`;
      }
      return highlightCodeSegment(segment.text);
    })
    .join('');
}
