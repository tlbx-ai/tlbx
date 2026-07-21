import type { IBufferCell, Terminal } from '@xterm/xterm';
import { getSessionBufferText } from '../../api/client';
import type { SessionBufferTextResponse } from '../../api/types';
import { sessionTerminals } from '../../state';
import { $activeSessionId } from '../../stores';

type RawBufferResult =
  | { ok: true; snapshot: SessionBufferTextResponse }
  | { ok: false; error: string };

type XtermBuffer = Terminal['buffer']['active'];

interface DumpBuildOptions {
  generatedAt: Date;
  rawBuffer: RawBufferResult | null;
  sessionId: string;
  terminal: Terminal | null;
}

interface CellRun {
  endCol: number;
  signature: string;
  startCol: number;
  text: string;
}

const VISIBLE_ESCAPE_NOTE = String.raw`Control bytes are made visible. ESC is written as \x1b, so ANSI SGR such as ESC[31m appears as \x1b[31m.`;

export async function downloadActiveTerminalBufferDump(): Promise<string> {
  const sessionId = $activeSessionId.get();
  if (!sessionId) {
    throw new Error('No active terminal session.');
  }

  const state = sessionTerminals.get(sessionId);
  const rawBuffer = await fetchRawBuffer(sessionId);
  if (!state?.terminal && !rawBuffer.ok) {
    throw new Error(`Terminal buffer is unavailable: ${rawBuffer.error}`);
  }

  const now = new Date();
  const report = buildTerminalBufferDumpText({
    generatedAt: now,
    rawBuffer,
    sessionId,
    terminal: state?.terminal ?? null,
  });
  const filename = buildTerminalBufferDumpFilename(sessionId, now);
  triggerTextDownload(filename, report);
  return filename;
}

export function buildTerminalBufferDumpText(options: DumpBuildOptions): string {
  const lines: string[] = [
    'tlbx terminal buffer diagnostic dump',
    `Generated: ${options.generatedAt.toISOString()}`,
    `Session: ${options.sessionId}`,
    VISIBLE_ESCAPE_NOTE,
    '',
  ];

  appendRenderedBufferSection(lines, options.terminal);
  appendCellRunsSection(lines, options.terminal);
  appendRawBufferSection(lines, options.rawBuffer);

  return `${lines.join('\n')}\n`;
}

export function makeControlSequencesVisible(input: string): string {
  let output = '';
  for (let index = 0; index < input.length; index++) {
    const code = input.charCodeAt(index);
    switch (code) {
      case 0x07:
        output += String.raw`\a`;
        break;
      case 0x08:
        output += String.raw`\b`;
        break;
      case 0x09:
        output += '\t';
        break;
      case 0x0a:
        output += '\n';
        break;
      case 0x0d:
        output += String.raw`\r`;
        break;
      case 0x1b:
        output += String.raw`\x1b`;
        break;
      case 0x7f:
        output += String.raw`\x7f`;
        break;
      default:
        if (code < 0x20) {
          output += String.raw`\x${code.toString(16).padStart(2, '0')}`;
        } else {
          output += input[index] ?? '';
        }
        break;
    }
  }

  return output;
}

function appendRenderedBufferSection(lines: string[], terminal: Terminal | null): void {
  lines.push('===== XTERM RENDERED BUFFER TEXT =====');
  if (!terminal) {
    lines.push('Browser xterm buffer is unavailable for this session.', '');
    return;
  }

  for (const target of getTerminalBufferTargets(terminal)) {
    lines.push(
      `--- ${target.label} buffer ---`,
      `cols=${terminal.cols} rows=${terminal.rows} bufferLength=${target.buffer.length} baseY=${target.buffer.baseY} viewportY=${target.buffer.viewportY} cursor=${target.buffer.cursorX},${target.buffer.cursorY}`,
      '',
    );

    for (let row = 0; row < target.buffer.length; row++) {
      lines.push(target.buffer.getLine(row)?.translateToString(true) ?? '');
    }
    lines.push('');
  }
}

function appendCellRunsSection(lines: string[], terminal: Terminal | null): void {
  lines.push('===== XTERM NON-DEFAULT CELL COLOR/STYLE RUNS =====');
  if (!terminal) {
    lines.push('Browser xterm buffer is unavailable for this session.', '');
    return;
  }

  let emittedAny = false;
  for (const target of getTerminalBufferTargets(terminal)) {
    let emittedTargetHeader = false;
    for (let row = 0; row < target.buffer.length; row++) {
      const line = target.buffer.getLine(row);
      if (!line) {
        continue;
      }

      const runs = getNonDefaultCellRuns(line, target.buffer.getNullCell());
      if (runs.length === 0) {
        continue;
      }

      emittedAny = true;
      if (!emittedTargetHeader) {
        lines.push(`--- ${target.label} buffer ---`);
        emittedTargetHeader = true;
      }
      lines.push(
        `line ${formatLineNumber(row)} wrapped=${line.isWrapped ? 'yes' : 'no'} text=${JSON.stringify(line.translateToString(true))}`,
      );
      for (const run of runs) {
        lines.push(
          `  cols ${run.startCol}-${run.endCol - 1} ${run.signature} text=${JSON.stringify(run.text)}`,
        );
      }
    }
  }

  if (!emittedAny) {
    lines.push('No non-default color or style attributes found in the browser xterm buffer.');
  }
  lines.push('');
}

function appendRawBufferSection(lines: string[], rawBuffer: RawBufferResult | null): void {
  lines.push('===== RAW PTY OUTPUT BUFFER WITH ESCAPES VISIBLE =====');
  if (!rawBuffer) {
    lines.push('Raw PTY output buffer was not requested.', '');
    return;
  }

  if (!rawBuffer.ok) {
    lines.push(`Raw PTY output buffer is unavailable: ${rawBuffer.error}`, '');
    return;
  }

  const snapshot = rawBuffer.snapshot;
  lines.push(
    `encoding=${snapshot.encoding} byteLength=${snapshot.byteLength}`,
    '',
    makeControlSequencesVisible(snapshot.text),
    '',
    '===== RAW PTY OUTPUT BUFFER BASE64 =====',
    snapshot.base64 ?? '(base64 was not returned)',
    '',
  );
}

function getTerminalBufferTargets(
  terminal: Terminal,
): Array<{ label: 'active' | 'normal' | 'alternate'; buffer: XtermBuffer }> {
  const targets: Array<{ label: 'active' | 'normal' | 'alternate'; buffer: XtermBuffer }> = [
    { label: 'active', buffer: terminal.buffer.active },
  ];

  if (terminal.buffer.normal !== terminal.buffer.active) {
    targets.push({ label: 'normal', buffer: terminal.buffer.normal });
  }

  if (
    terminal.buffer.alternate !== terminal.buffer.active &&
    terminal.buffer.alternate !== terminal.buffer.normal
  ) {
    targets.push({ label: 'alternate', buffer: terminal.buffer.alternate });
  }

  return targets;
}

function getNonDefaultCellRuns(
  line: { length: number; getCell: (x: number, cell?: IBufferCell) => IBufferCell | undefined },
  scratch: IBufferCell,
): CellRun[] {
  const runs: CellRun[] = [];
  let active: CellRun | null = null;

  for (let column = 0; column < line.length; column++) {
    const cell = line.getCell(column, scratch);
    if (!cell || cell.getWidth() === 0) {
      continue;
    }

    const signature = getCellSignature(cell);
    const text = cell.getChars() || ' ';
    if (isDefaultSignature(signature)) {
      if (active) {
        runs.push(active);
        active = null;
      }
      continue;
    }

    if (active && active.signature === signature && active.endCol === column) {
      active.endCol = column + Math.max(cell.getWidth(), 1);
      active.text += text;
      continue;
    }

    if (active) {
      runs.push(active);
    }

    active = {
      endCol: column + Math.max(cell.getWidth(), 1),
      signature,
      startCol: column,
      text,
    };
  }

  if (active) {
    runs.push(active);
  }

  return runs;
}

function getCellSignature(cell: IBufferCell): string {
  const attrs = [
    cell.isBold() ? 'bold' : null,
    cell.isDim() ? 'dim' : null,
    cell.isItalic() ? 'italic' : null,
    cell.isUnderline() ? 'underline' : null,
    cell.isBlink() ? 'blink' : null,
    cell.isInverse() ? 'inverse' : null,
    cell.isInvisible() ? 'invisible' : null,
    cell.isStrikethrough() ? 'strikethrough' : null,
    cell.isOverline() ? 'overline' : null,
  ].filter((value): value is string => value !== null);

  return [
    `fg=${formatCellColor('fg', cell)}`,
    `bg=${formatCellColor('bg', cell)}`,
    `attrs=${attrs.length === 0 ? 'none' : attrs.join(',')}`,
  ].join(' ');
}

function isDefaultSignature(signature: string): boolean {
  return signature === 'fg=default bg=default attrs=none';
}

function formatCellColor(kind: 'fg' | 'bg', cell: IBufferCell): string {
  const isDefault = kind === 'fg' ? cell.isFgDefault() : cell.isBgDefault();
  if (isDefault) {
    return 'default';
  }

  const color = kind === 'fg' ? cell.getFgColor() : cell.getBgColor();
  const isPalette = kind === 'fg' ? cell.isFgPalette() : cell.isBgPalette();
  if (isPalette) {
    return `palette:${color}`;
  }

  const isRgb = kind === 'fg' ? cell.isFgRGB() : cell.isBgRGB();
  if (isRgb) {
    return `rgb:#${color.toString(16).padStart(6, '0')}`;
  }

  const mode = kind === 'fg' ? cell.getFgColorMode() : cell.getBgColorMode();
  return `mode:${mode}:${color}`;
}

function formatLineNumber(row: number): string {
  return row.toString().padStart(6, '0');
}

async function fetchRawBuffer(sessionId: string): Promise<RawBufferResult> {
  try {
    return {
      ok: true,
      snapshot: await getSessionBufferText(sessionId, true),
    };
  } catch (error) {
    return {
      ok: false,
      error: String(error),
    };
  }
}

function buildTerminalBufferDumpFilename(sessionId: string, now: Date): string {
  const safeSessionId = sessionId.replace(/[^a-z0-9._-]+/gi, '_').replace(/^_+|_+$/g, '');
  const stamp = now.toISOString().replace(/[:.]/g, '-');
  return `tlbx-terminal-buffer-${safeSessionId || 'session'}-${stamp}.txt`;
}

function triggerTextDownload(filename: string, text: string): void {
  const blob = new Blob([text], { type: 'text/plain;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = filename;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  window.setTimeout(() => {
    URL.revokeObjectURL(url);
  }, 1000);
}
