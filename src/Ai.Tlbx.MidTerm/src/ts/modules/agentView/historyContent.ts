import { t } from '../i18n';
import {
  COLLAPSIBLE_HISTORY_BODY_MIN_CHARS,
  COLLAPSIBLE_HISTORY_BODY_MIN_LINES,
  COLLAPSIBLE_HISTORY_BODY_PREVIEW_CHARS,
  MAX_TOOL_CALL_OUTPUT_LINES,
  MAX_VISIBLE_DIFF_LINES,
} from './historyConstants';
import type {
  CommandToken,
  DiffRenderLine,
  HistoryBodyPresentation,
  HistoryKind,
  AppServerControlHistoryEntry,
} from './types';

function appServerControlText(key: string, fallback: string): string {
  const translated = t(key);
  if (!translated || translated === key) {
    return fallback;
  }

  return translated;
}

function appServerControlFormat(
  key: string,
  fallback: string,
  replacements: Record<string, string | number>,
): string {
  return Object.entries(replacements).reduce(
    (text, [name, value]) => text.split(`{${name}}`).join(String(value)),
    appServerControlText(key, fallback),
  );
}

function normalizeComparableHistoryText(value: string | null | undefined): string {
  return (value ?? '').trim().toLowerCase().replace(/\s+/g, ' ');
}

export function normalizeHistoryItemType(value: string | null | undefined): string {
  return (value ?? '')
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '');
}

export function normalizeHistoryBodyLines(body: string): string[] {
  return body.replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n');
}

function countHistoryBodyLines(body: string): number {
  return body.trim() ? normalizeHistoryBodyLines(body).length : 0;
}

function isMachineHistoryKind(kind: HistoryKind): boolean {
  return kind === 'tool' || kind === 'reasoning' || kind === 'plan' || kind === 'diff';
}

export function hasInlineCommandPresentation(
  entry: Pick<AppServerControlHistoryEntry, 'commandOutputTail' | 'commandText'>,
): boolean {
  return (entry.commandText?.trim() ?? '').length > 0 || (entry.commandOutputTail?.length ?? 0) > 0;
}

export function isCommandExecutionHistoryEntry(entry: AppServerControlHistoryEntry): boolean {
  const normalized = normalizeHistoryItemType(entry.sourceItemType);
  return (
    entry.kind === 'tool' &&
    (normalized === 'commandexecution' ||
      normalized === 'command' ||
      normalized === 'commandcall' ||
      normalized === 'commandrun')
  );
}

export function isCommandOutputHistoryEntry(entry: AppServerControlHistoryEntry): boolean {
  return (
    entry.kind === 'tool' &&
    normalizeComparableHistoryText(entry.title) === normalizeComparableHistoryText('Command output')
  );
}

function isToolCommandPresentationEntry(entry: AppServerControlHistoryEntry): boolean {
  return (
    entry.kind === 'tool' &&
    (hasInlineCommandPresentation(entry) ||
      isCommandExecutionHistoryEntry(entry) ||
      isCommandOutputHistoryEntry(entry))
  );
}

function buildHistoryBodyPreview(body: string): string {
  const firstContentLine =
    body
      .replace(/\r\n/g, '\n')
      .replace(/\r/g, '\n')
      .split('\n')
      .map((line) => line.trim())
      .find((line) => line) ?? '';
  const singleLine = firstContentLine.replace(/\s+/g, ' ').trim();
  if (singleLine.length <= COLLAPSIBLE_HISTORY_BODY_PREVIEW_CHARS) {
    return singleLine;
  }

  return `${singleLine.slice(0, COLLAPSIBLE_HISTORY_BODY_PREVIEW_CHARS - 1)}…`;
}

export function extractCommandOutputTail(body: string): string[] {
  const lines = normalizeHistoryBodyLines(body)
    .map((line) => line.replace(/\s+$/g, ''))
    .filter(
      (line, index, array) =>
        line.length > 0 || array.slice(index + 1).some((next) => next.length > 0),
    );
  return lines.slice(Math.max(0, lines.length - MAX_TOOL_CALL_OUTPUT_LINES));
}

export function parseCommandOutputBody(
  body: string,
): { commandText: string; commandOutputTail: string[] } | null {
  const lines = normalizeHistoryBodyLines(body).map((line) => line.replace(/\s+$/g, ''));
  const firstContentIndex = lines.findIndex((line) => line.trim().length > 0);
  if (firstContentIndex < 0) {
    return null;
  }

  const commandText = lines[firstContentIndex]?.trim() ?? '';
  if (!commandText || isCommandOutputOmissionMarker(commandText)) {
    return null;
  }

  let outputStartIndex = firstContentIndex + 1;
  while (outputStartIndex < lines.length && lines[outputStartIndex]?.trim().length === 0) {
    outputStartIndex += 1;
  }

  const outputBody = lines.slice(outputStartIndex).join('\n');
  return {
    commandText,
    commandOutputTail: extractCommandOutputTail(outputBody),
  };
}

function isCommandOutputOmissionMarker(line: string): boolean {
  const normalized = line.trim().toLowerCase();
  return (
    normalized === '... earlier output omitted ...' ||
    /^\.\.\. \d+ earlier lines omitted \.\.\.$/.test(normalized) ||
    /^\.\.\. \d+ more lines omitted \.\.\.$/.test(normalized)
  );
}

function readWhitespaceToken(source: string, start: number): number {
  let end = start + 1;
  while (end < source.length && /\s/.test(source[end] || '')) {
    end += 1;
  }

  return end;
}

function readQuotedToken(source: string, start: number, quote: '"' | "'"): number {
  let end = start + 1;
  while (end < source.length) {
    const value = source[end];
    if (value === '\\') {
      end += 2;
      continue;
    }

    end += 1;
    if (value === quote) {
      break;
    }
  }

  return Math.min(end, source.length);
}

function readWordToken(source: string, start: number): number {
  let end = start + 1;
  while (end < source.length) {
    const value = source[end];
    if (!value || /\s/.test(value) || value === '"' || value === "'") {
      break;
    }

    const nextOperator = source.slice(end).match(/^(?:\|\||&&|>>|<<|[><=|&])/);
    if (nextOperator?.[0]) {
      break;
    }

    end += 1;
  }

  return end;
}

export function tokenizeCommandText(commandText: string): CommandToken[] {
  const source = commandText || '';
  const tokens: CommandToken[] = [];
  let index = 0;
  let firstWordSeen = false;

  while (index < source.length) {
    const current = source[index];
    if (!current) {
      break;
    }

    if (/\s/.test(current)) {
      const end = readWhitespaceToken(source, index);
      tokens.push({ text: source.slice(index, end), kind: 'whitespace' });
      index = end;
      continue;
    }

    if (current === '"' || current === "'") {
      const nextIndex = readQuotedToken(source, index, current);
      tokens.push({ text: source.slice(index, nextIndex), kind: 'string' });
      index = nextIndex;
      continue;
    }

    const operatorMatch = source.slice(index).match(/^(?:\|\||&&|>>|<<|[><=|&])/);
    if (operatorMatch?.[0]) {
      tokens.push({ text: operatorMatch[0], kind: 'operator' });
      index += operatorMatch[0].length;
      continue;
    }

    const end = readWordToken(source, index);
    const text = source.slice(index, end);
    const kind: CommandToken['kind'] = !firstWordSeen
      ? 'command'
      : text.startsWith('-')
        ? 'parameter'
        : 'text';
    tokens.push({ text, kind });
    if (!firstWordSeen && text.trim().length > 0) {
      firstWordSeen = true;
    }
    index = end;
  }

  return tokens;
}

export function resolveHistoryBodyPresentation(
  entry: AppServerControlHistoryEntry,
): HistoryBodyPresentation {
  if (entry.kind === 'assistant') {
    return {
      mode: 'markdown',
      collapsedByDefault: false,
      lineCount: countHistoryBodyLines(entry.body),
      preview: '',
    };
  }

  if (isToolCommandPresentationEntry(entry)) {
    return {
      mode: 'command',
      collapsedByDefault: false,
      lineCount: 1 + (entry.commandOutputTail?.length ?? 0),
      preview: '',
    };
  }

  const mode =
    entry.kind === 'diff' ? 'diff' : isMachineHistoryKind(entry.kind) ? 'monospace' : 'plain';
  const lineCount = countHistoryBodyLines(entry.body);
  const collapsedByDefault =
    mode === 'monospace' &&
    !entry.live &&
    !entry.pending &&
    (lineCount >= COLLAPSIBLE_HISTORY_BODY_MIN_LINES ||
      entry.body.length >= COLLAPSIBLE_HISTORY_BODY_MIN_CHARS);

  return {
    mode,
    collapsedByDefault,
    lineCount,
    preview: collapsedByDefault ? buildHistoryBodyPreview(entry.body) : '',
  };
}

interface ParsedDiffSection {
  oldPath: string;
  newPath: string;
  lines: string[];
  lineState: DiffLineState;
}

interface DiffLineState {
  oldLineNumber: number | null;
  newLineNumber: number | null;
}

function createParsedDiffSection(): ParsedDiffSection {
  return {
    oldPath: '',
    newPath: '',
    lines: [],
    lineState: {
      oldLineNumber: null,
      newLineNumber: null,
    },
  };
}

function isDiffMetadataLine(line: string): boolean {
  return (
    line.startsWith('index ') ||
    line.startsWith('new file mode ') ||
    line.startsWith('deleted file mode ') ||
    line.startsWith('old mode ') ||
    line.startsWith('new mode ') ||
    line.startsWith('similarity index ') ||
    line.startsWith('rename from ') ||
    line.startsWith('rename to ') ||
    line.startsWith('copy from ') ||
    line.startsWith('copy to ')
  );
}

function isDiffBinaryLine(line: string): boolean {
  return (
    line.startsWith('Binary files ') ||
    line.startsWith('GIT binary patch') ||
    line.startsWith('literal ') ||
    line.startsWith('delta ')
  );
}

function resolveDiffLineClassName(line: string): string {
  if (line.startsWith('+++') || line.startsWith('---') || line.startsWith('diff --git')) {
    return 'agent-history-diff-line-header';
  }

  if (line.startsWith('@@')) {
    return 'agent-history-diff-line-hunk';
  }

  if (line.startsWith('+')) {
    return 'agent-history-diff-line-add';
  }

  if (line.startsWith('-')) {
    return 'agent-history-diff-line-delete';
  }

  return 'agent-history-diff-line-context';
}

function buildDiffSections(lines: readonly string[], sessionCwd?: string | null): DiffRenderLine[] {
  const sections: ParsedDiffSection[] = [];
  let current: ParsedDiffSection | null = null;
  let seenHunk = false;

  const ensureCurrent = (): ParsedDiffSection => {
    if (current) {
      return current;
    }

    current = createParsedDiffSection();
    sections.push(current);
    return current;
  };

  for (const line of lines) {
    if (line.startsWith('diff --git ')) {
      current = createParsedDiffSection();
      current.oldPath = extractDiffGitPath(line, 'a/');
      current.newPath = extractDiffGitPath(line, 'b/');
      sections.push(current);
      seenHunk = false;
      continue;
    }

    const section = ensureCurrent();
    if (line.startsWith('--- ')) {
      section.oldPath = normalizeDiffPath(line.slice(4));
      continue;
    }

    if (line.startsWith('+++ ')) {
      section.newPath = normalizeDiffPath(line.slice(4));
      continue;
    }

    if (isDiffMetadataLine(line)) {
      continue;
    }

    if (line.startsWith('@@')) {
      seenHunk = true;
      section.lines.push(line);
      continue;
    }

    if (isDiffBinaryLine(line)) {
      section.lines.push(line);
      continue;
    }

    if (!seenHunk && !section.lines.length) {
      continue;
    }

    section.lines.push(line);
  }

  const rendered: DiffRenderLine[] = [];
  for (const section of sections) {
    if (section.lines.length === 0) {
      continue;
    }

    const sectionHeader = formatDiffSectionHeader(section.oldPath, section.newPath, sessionCwd);
    if (sectionHeader) {
      rendered.push({
        text: sectionHeader,
        className: 'agent-history-diff-line-file',
      });
    }

    for (const line of section.lines) {
      rendered.push(...buildRenderedDiffChunk(section, line));
    }
  }

  return rendered;
}

function buildFallbackDiffLines(lines: readonly string[]): DiffRenderLine[] {
  const trimmedLines = lines.filter(
    (line) =>
      line.trim().length > 0 &&
      !line.startsWith('diff --git ') &&
      !line.startsWith('index ') &&
      !line.startsWith('new file mode ') &&
      !line.startsWith('deleted file mode ') &&
      !line.startsWith('old mode ') &&
      !line.startsWith('new mode '),
  );
  const source = trimmedLines.length > 0 ? trimmedLines : lines;
  return source.map((line) => ({
    text: line || ' ',
    className: resolveDiffLineClassName(line),
  }));
}

function buildRenderedDiffChunk(section: ParsedDiffSection, line: string): DiffRenderLine[] {
  const state = section.lineState;

  if (line.startsWith('@@')) {
    applyDiffHunkHeader(state, line);
    return [createPlainDiffRenderLine(line)];
  }

  if (state.oldLineNumber === null && state.newLineNumber === null) {
    return [createPlainDiffRenderLine(line || ' ')];
  }

  if (line.startsWith('+')) {
    return [createAddedDiffRenderLine(line, state)];
  }

  if (line.startsWith('-')) {
    return [createRemovedDiffRenderLine(line, state)];
  }

  if (line.startsWith('\\')) {
    return [createPlainDiffRenderLine(line)];
  }

  return [createContextDiffRenderLine(line || ' ', state)];
}

function createPlainDiffRenderLine(text: string): DiffRenderLine {
  return {
    text,
    className: resolveDiffLineClassName(text),
  };
}

function createAddedDiffRenderLine(line: string, state: DiffLineState): DiffRenderLine {
  const rendered = createDiffRenderLine(
    line,
    resolveDiffLineClassName(line),
    undefined,
    state.newLineNumber ?? undefined,
  );
  if (state.newLineNumber !== null) {
    state.newLineNumber += 1;
  }
  return rendered;
}

function createRemovedDiffRenderLine(line: string, state: DiffLineState): DiffRenderLine {
  const rendered = createDiffRenderLine(
    line,
    resolveDiffLineClassName(line),
    state.oldLineNumber ?? undefined,
  );
  if (state.oldLineNumber !== null) {
    state.oldLineNumber += 1;
  }
  return rendered;
}

function createContextDiffRenderLine(line: string, state: DiffLineState): DiffRenderLine {
  const rendered = createDiffRenderLine(
    line,
    resolveDiffLineClassName(line),
    state.oldLineNumber ?? undefined,
    state.newLineNumber ?? undefined,
  );
  if (state.newLineNumber !== null) {
    state.newLineNumber += 1;
  }
  if (state.oldLineNumber !== null) {
    state.oldLineNumber += 1;
  }
  return rendered;
}

function applyDiffHunkHeader(state: DiffLineState, line: string): void {
  const match = line.match(/^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/);
  if (!match) {
    state.oldLineNumber = null;
    state.newLineNumber = null;
    return;
  }

  state.oldLineNumber = Number.parseInt(match[1] || '0', 10);
  state.newLineNumber = Number.parseInt(match[2] || '0', 10);
}

function createDiffRenderLine(
  text: string,
  className: string,
  oldLineNumber?: number,
  newLineNumber?: number,
): DiffRenderLine {
  const line: DiffRenderLine = {
    text,
    className,
  };
  if (typeof oldLineNumber === 'number') {
    line.oldLineNumber = oldLineNumber;
  }

  if (typeof newLineNumber === 'number') {
    line.newLineNumber = newLineNumber;
  }

  return line;
}

function extractDiffGitPath(line: string, prefix: 'a/' | 'b/'): string {
  const match = line.match(/^diff --git a\/(.+?) b\/(.+)$/);
  if (!match) {
    return '';
  }

  return normalizeDiffPath(prefix === 'a/' ? `a/${match[1]}` : `b/${match[2]}`);
}

function normalizeDiffPath(value: string): string {
  const trimmed = value.trim();
  if (!trimmed) {
    return '';
  }

  if (trimmed === '/dev/null') {
    return trimmed;
  }

  return trimmed.replace(/^["']|["']$/g, '').replace(/^[ab]\//, '');
}

function formatDiffSectionHeader(
  oldPath: string,
  newPath: string,
  sessionCwd?: string | null,
): string {
  const normalizedOld = normalizeDiffPath(oldPath);
  const normalizedNew = normalizeDiffPath(newPath);
  const preferredPath =
    normalizedNew && normalizedNew !== '/dev/null' ? normalizedNew : normalizedOld;
  const displayPath = resolveDisplayDiffPath(preferredPath, sessionCwd);
  if (!normalizedOld && !normalizedNew) {
    return '';
  }

  if (normalizedOld === '/dev/null') {
    return displayPath ? `Edited ${displayPath}` : '';
  }

  if (normalizedNew === '/dev/null') {
    return appServerControlFormat('appServerControl.diff.deletedFile', 'Edited {path} (deleted)', {
      path: resolveDisplayDiffPath(normalizedOld, sessionCwd),
    });
  }

  if (!normalizedOld || normalizedOld === normalizedNew) {
    return displayPath ? `Edited ${displayPath}` : '';
  }

  return appServerControlFormat('appServerControl.diff.renamedFile', 'Edited {to} (from {from})', {
    from: resolveDisplayDiffPath(normalizedOld, sessionCwd),
    to: resolveDisplayDiffPath(normalizedNew, sessionCwd),
  });
}

function resolveDisplayDiffPath(path: string, sessionCwd?: string | null): string {
  const normalizedPath = normalizeDiffPath(path);
  if (!normalizedPath || normalizedPath === '/dev/null') {
    return normalizedPath;
  }

  if (
    /^[A-Za-z]:[\\/]/.test(normalizedPath) ||
    normalizedPath.startsWith('/') ||
    normalizedPath.startsWith('\\\\')
  ) {
    return normalizedPath;
  }

  const cwd = (sessionCwd ?? '').trim();
  if (!cwd) {
    return normalizedPath;
  }

  return `${cwd.replace(/[\\/]+$/g, '')}\\${normalizedPath.replace(/\//g, '\\')}`;
}

export function buildRenderedDiffLines(
  bodyText: string,
  sessionCwd?: string | null,
): DiffRenderLine[] {
  const normalizedLines = normalizeHistoryBodyLines(bodyText);
  if (normalizedLines.length === 0) {
    return [];
  }

  const rendered = buildDiffSections(normalizedLines, sessionCwd);
  const lines = rendered.length > 0 ? rendered : buildFallbackDiffLines(normalizedLines);
  if (lines.length <= MAX_VISIBLE_DIFF_LINES) {
    return lines;
  }

  return [
    ...lines.slice(0, MAX_VISIBLE_DIFF_LINES),
    {
      text: appServerControlFormat(
        'appServerControl.diff.omittedLines',
        '... {count} more diff lines omitted ...',
        {
          count: lines.length - MAX_VISIBLE_DIFF_LINES,
        },
      ),
      className: 'agent-history-diff-line-ellipsis',
    },
  ];
}

function usesCompactHistoryChrome(kind: HistoryKind): boolean {
  return (
    kind === 'tool' ||
    kind === 'reasoning' ||
    kind === 'plan' ||
    kind === 'diff' ||
    kind === 'request' ||
    kind === 'system' ||
    kind === 'notice'
  );
}

function resolveHistoryHorizontalChrome(kind: HistoryKind): number {
  if (kind === 'user') {
    return 72;
  }

  return usesCompactHistoryChrome(kind) ? 56 : 28;
}

function resolveHistoryAverageCharWidth(kind: HistoryKind): number {
  return usesCompactHistoryChrome(kind) ? 7.4 : 8.1;
}

function estimateWrappedTextLines(body: string, charsPerLine: number): number {
  return Math.max(
    1,
    body
      .split('\n')
      .reduce(
        (sum, line) => sum + Math.max(1, Math.ceil(Math.max(1, line.length) / charsPerLine)),
        0,
      ),
  );
}

function resolveHistoryEntryBaseHeight(kind: HistoryKind): number {
  switch (kind) {
    case 'tool':
    case 'reasoning':
    case 'diff':
    case 'plan':
      return 84;
    case 'request':
      return 108;
    case 'user':
      return 68;
    case 'assistant':
      return 52;
    case 'system':
    case 'notice':
      return 64;
  }
}

export function estimateHistoryEntryHeight(
  entry: AppServerControlHistoryEntry,
  viewportWidth = 960,
): number {
  if (entry.busyIndicator) {
    return 52;
  }

  const effectiveWidth = Math.max(220, Math.min(viewportWidth, 960));
  const horizontalChrome = resolveHistoryHorizontalChrome(entry.kind);
  const contentWidth = Math.max(140, effectiveWidth - horizontalChrome);
  const avgCharWidthPx = resolveHistoryAverageCharWidth(entry.kind);
  const charsPerLine = Math.max(18, Math.floor(contentWidth / avgCharWidthPx));
  const textLines =
    entry.kind === 'diff'
      ? Math.max(1, buildRenderedDiffLines(entry.body).length)
      : estimateWrappedTextLines(entry.body, charsPerLine);
  const presentation = resolveHistoryBodyPresentation(entry);
  const bodyHeight =
    presentation.mode === 'command'
      ? 24 + Math.min(MAX_TOOL_CALL_OUTPUT_LINES, entry.commandOutputTail?.length ?? 0) * 15
      : presentation.collapsedByDefault
        ? 40
        : Math.min(420, 18 * textLines);

  return resolveHistoryEntryBaseHeight(entry.kind) + bodyHeight;
}
