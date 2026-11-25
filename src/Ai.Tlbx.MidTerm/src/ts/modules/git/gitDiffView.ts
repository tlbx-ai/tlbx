import type { GitDiffFileView, GitDiffLine } from './types';
import { escapeHtml } from '../../utils';
import { t } from '../i18n';
import type { GitDiffUi } from './gitDiffControls';

interface GitDiffOptions {
  ui?: GitDiffUi;
  selectable?: boolean;
  staged?: boolean;
  busy?: boolean;
  historical?: boolean;
  tabStop?: string;
  hideFileHeader?: boolean;
}

function renderNewFileMetadata(file: GitDiffFileView): string {
  if (!file.isNewFile) return '';
  const size = file.sizeBytes == null ? '' : `${new Intl.NumberFormat().format(file.sizeBytes)} B`;
  return `<div class="git-new-file-metadata">
    <p>${t('git.newFileHint')}</p>
    <p>${escapeHtml([size, file.mimeType, file.encoding].filter(Boolean).join(' · '))}</p>
    ${file.mimeType === 'inode/symlink' ? '' : `<button class="git-action-button" data-action="open-file-viewer" data-path="${escapeHtml(file.path)}" type="button">${t('git.openFilePreview')}</button>`}
  </div>`;
}

export function renderPatchFile(file: GitDiffFileView, options: GitDiffOptions = {}): string {
  const firstHunk = file.hunks.findIndex((hunk) =>
    hunk.lines.some((line) => line.kind === 'add' || line.kind === 'del'),
  );
  const firstLine =
    file.hunks[firstHunk]?.lines.findIndex((line) => line.kind === 'add' || line.kind === 'del') ??
    -1;
  options = { ...options, tabStop: options.ui?.anchor ?? `${firstHunk}:${firstLine}` };
  const body = file.isBinary
    ? `<div class="git-patch-empty">${t(file.isNewFile ? 'git.newFileBinary' : 'git.binaryChanged')}</div>`
    : file.hunks.length === 0
      ? `<div class="git-patch-empty">${t(file.isNewFile ? 'git.newFileEmpty' : 'git.noPatch')}</div>`
      : file.hunks
          .map((hunk, hunkIndex) => {
            const lines = options.ui?.sideBySide
              ? renderSideRows(hunk.lines, hunkIndex, options)
              : hunk.lines
                  .map((line, lineIndex) => renderDiffLine(line, hunkIndex, lineIndex, options))
                  .join('');
            const lastLine = hunk.lines.reduce(
              (maximum, line) => Math.max(maximum, line.oldLine ?? 0, line.newLine ?? 0),
              0,
            );
            const gutterWidth = Math.max(3, String(lastLine).length + 1);
            return `<section class="git-diff-hunk-block" style="--git-diff-number-width:${gutterWidth}ch">
          <div class="git-diff-hunk-header"><span>${escapeHtml(hunk.header)}</span>${renderHunkAction(hunkIndex, options)}</div>
          ${renderSideLabels(options)}
          <pre class="git-diff-lines" tabindex="0" role="region" aria-label="${escapeHtml(`${t('git.diffHunk')}: ${file.path} ${hunk.header}`)}"><code class="git-diff-table${options.ui?.sideBySide ? ' git-diff-table-side' : ''}"${options.selectable ? ` role="listbox" aria-multiselectable="true" aria-label="${t('git.selectChangedLines')}"` : ''}>${lines}</code></pre>
        </section>`;
          })
          .join('');

  const truncatedHtml = file.isTruncated
    ? `<div class="git-panel-hint">${t(file.isNewFile ? 'git.newFileTruncated' : 'git.patchTruncated')}</div>`
    : '';

  return `<section class="git-inspector-patch">
    ${renderPatchHeader(file, options)}
    ${renderNewFileMetadata(file)}
    ${body}
    ${truncatedHtml}
  </section>`;
}

function renderDiffLine(
  line: GitDiffLine,
  hunk: number,
  index: number,
  options: GitDiffOptions,
  side?: 'old' | 'new',
): string {
  const changeLine = ['add', 'del', 'context'].includes(line.kind);
  const code = renderCode(line, changeLine);
  const content = changeLine
    ? `${renderNumbers(line, side)}<span class="git-diff-sign">${escapeHtml(line.text.slice(0, 1))}</span><span class="git-diff-code">${code}</span>`
    : `<span class="git-diff-code">${code}</span>`;
  const id = `${hunk}:${index}`;
  const selected = options.ui?.selected.has(id) ?? false;
  return `<span class="git-diff-line git-diff-line-${escapeHtml(line.kind)}${side ? ' git-diff-cell' : ''}${selected ? ' git-diff-line-selected' : ''}"${renderLineInteraction(line, id, options, selected)}>${content}</span>`;
}

function renderCode(line: GitDiffLine, changeLine: boolean): string {
  return line.segments?.length
    ? line.segments
        .map(
          (segment) =>
            `<span${segment.changed ? ' class="git-diff-word-change"' : ''}>${escapeHtml(segment.text)}</span>`,
        )
        .join('')
    : escapeHtml(changeLine ? line.text.slice(1) : line.text);
}

function renderNumbers(line: GitDiffLine, side?: 'old' | 'new'): string {
  return `${side === 'new' ? '' : `<span class="git-diff-number git-diff-number-old" aria-hidden="true">${line.oldLine ?? ''}</span>`}${side === 'old' ? '' : `<span class="git-diff-number git-diff-number-new" aria-hidden="true">${line.newLine ?? ''}</span>`}`;
}

function renderLineInteraction(
  line: GitDiffLine,
  id: string,
  options: GitDiffOptions,
  selected: boolean,
): string {
  const selectable = options.selectable && (line.kind === 'add' || line.kind === 'del');
  return selectable
    ? ` data-action="select-diff-line" data-diff-line="${id}" role="option" tabindex="${options.tabStop === id ? 0 : -1}" aria-selected="${selected}" aria-label="${escapeHtml(`${t(line.kind === 'add' ? 'git.fileStatus.added' : 'git.fileStatus.deleted')} ${line.newLine ?? line.oldLine ?? ''}: ${line.text.slice(1)}`)}"`
    : '';
}

function renderHunkAction(index: number, options: GitDiffOptions): string {
  if (!options.selectable) return '';
  const label = t(options.staged ? 'git.unstageHunk' : 'git.stageHunk');
  return `<button class="git-action-button git-diff-hunk-action" data-action="apply-diff-hunk" data-hunk="${index}" title="${label}" aria-label="${label}"${options.busy ? ' disabled' : ''} type="button">${options.staged ? '−' : '+'}</button>`;
}

function renderSideLabels(options: GitDiffOptions): string {
  if (!options.ui?.sideBySide) return '';
  const old = options.historical
    ? 'git.previousVersion'
    : options.staged
      ? 'git.headVersion'
      : 'git.indexVersion';
  const current = options.historical
    ? 'git.currentVersion'
    : options.staged
      ? 'git.indexVersion'
      : 'git.workingVersion';
  return `<div class="git-diff-sides-header"><span>${t(old)}</span><span>${t(current)}</span></div>`;
}

function renderSideRows(lines: GitDiffLine[], hunk: number, options: GitDiffOptions): string {
  const rows = new Map<
    number,
    {
      old?: { line: GitDiffLine; index: number };
      current?: { line: GitDiffLine; index: number };
      meta?: { line: GitDiffLine; index: number };
    }
  >();
  for (const [index, line] of lines.entries()) {
    const position = line.visualRow ?? index;
    const row = rows.get(position) ?? {};
    if (line.kind === 'del' || line.kind === 'context') row.old = { line, index };
    if (line.kind === 'add' || line.kind === 'context') row.current = { line, index };
    if (line.kind === 'meta') row.meta = { line, index };
    rows.set(position, row);
  }
  return [...rows.values()]
    .map((row) => {
      if (row.meta)
        return `<span class="git-diff-pair git-diff-pair-meta">${renderDiffLine(row.meta.line, hunk, row.meta.index, options)}</span>`;
      const old = !row.old
        ? '<span class="git-diff-cell-empty" aria-hidden="true"></span>'
        : renderDiffLine(row.old.line, hunk, row.old.index, options, 'old');
      const current = !row.current
        ? '<span class="git-diff-cell-empty" aria-hidden="true"></span>'
        : renderDiffLine(row.current.line, hunk, row.current.index, options, 'new');
      return `<span class="git-diff-pair">${old}${current}</span>`;
    })
    .join('');
}

function renderPatchHeader(file: GitDiffFileView, options: GitDiffOptions): string {
  if (options.hideFileHeader) return '';
  const subtitle = file.originalPath
    ? `<div class="git-inspector-subtitle">${t('git.renamedFrom')}: ${escapeHtml(file.originalPath)}</div>`
    : '';
  const stats =
    file.hasLineStats === false || file.isBinary
      ? ''
      : `<div class="git-file-loc"><span class="git-loc-add">+${file.additions}</span> <span class="git-loc-del">-${file.deletions}</span></div>`;
  return `<div class="git-inspector-file-header"><div><div class="git-inspector-file-title">${escapeHtml(file.path)}</div>${subtitle}</div>${stats}</div>`;
}
