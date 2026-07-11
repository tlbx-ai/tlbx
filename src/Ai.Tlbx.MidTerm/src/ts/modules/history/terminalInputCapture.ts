import { createLogger } from '../logging';
import { recordTerminalInputHistory } from './inputHistoryApi';

const MAX_CAPTURE_CHARACTERS = 64 * 1024;
const ESCAPE = '\x1b';
const CURSOR_SEQUENCE = new RegExp(`^${ESCAPE}(?:\\[|O)(?:1;\\d+)?([CDFH])`);
const DELETE_SEQUENCE = new RegExp(`^${ESCAPE}\\[3(?:;\\d+)?~`);
const CSI_SEQUENCE = new RegExp(`^${ESCAPE}\\[[0-9;?]*[ -/]*[@-~]`);
const log = createLogger('terminal-input-history');

interface TerminalInputCaptureState {
  buffer: TerminalInputBuffer;
  ignoreNextData: boolean;
  submissionPending: boolean;
}

export interface TerminalEnterIntentInput {
  altKey: boolean;
  code: string;
  ctrlKey: boolean;
  key: string;
  metaKey: boolean;
  shiftKey: boolean;
}

export type TerminalEnterIntent = 'lineBreak' | 'submit' | null;

export function classifyTerminalEnterIntent(input: TerminalEnterIntentInput): TerminalEnterIntent {
  const isEnter = input.key === 'Enter' || input.code === 'Enter' || input.code === 'NumpadEnter';
  if (!isEnter) return null;
  return input.ctrlKey || input.shiftKey || input.altKey || input.metaKey ? 'lineBreak' : 'submit';
}

export class TerminalInputBuffer {
  private text = '';
  private cursor = 0;

  public appendText(value: string): void {
    this.insert(value);
  }

  public appendLineBreak(): void {
    this.insert('\n');
  }

  public applyData(data: string): void {
    for (let index = 0; index < data.length; ) {
      const sequenceLength = this.applyEscapeSequence(data, index);
      if (sequenceLength > 0) {
        index += sequenceLength;
        continue;
      }

      const codePoint = data.codePointAt(index);
      if (codePoint === undefined) break;
      const value = String.fromCodePoint(codePoint);
      index += value.length;

      switch (codePoint) {
        case 0x03: // Ctrl+C cancels the authored line.
          this.clear();
          break;
        case 0x01: // Ctrl+A
          this.cursor = 0;
          break;
        case 0x05: // Ctrl+E
          this.cursor = this.text.length;
          break;
        case 0x08: // Backspace
        case 0x7f: // DEL emitted by xterm for Backspace
          this.deleteBeforeCursor();
          break;
        case 0x0b: // Ctrl+K
          this.text = this.text.slice(0, this.cursor);
          break;
        case 0x15: // Ctrl+U
          this.text = this.text.slice(this.cursor);
          this.cursor = 0;
          break;
        case 0x17: // Ctrl+W
          this.deletePreviousWord();
          break;
        default:
          if (codePoint >= 0x20 && codePoint !== 0x7f) {
            this.insert(value);
          }
          break;
      }
    }
  }

  public consume(): string {
    const value = this.text;
    this.clear();
    return value;
  }

  public clear(): void {
    this.text = '';
    this.cursor = 0;
  }

  public value(): string {
    return this.text;
  }

  private applyEscapeSequence(data: string, index: number): number {
    if (data.charCodeAt(index) !== 0x1b) return 0;

    const remainder = data.slice(index);
    const cursorMatch = CURSOR_SEQUENCE.exec(remainder);
    if (cursorMatch) {
      const direction = cursorMatch[1];
      if (direction === 'C') {
        this.cursor = Math.min(this.text.length, this.cursor + 1);
      } else if (direction === 'D') {
        this.cursor = Math.max(0, this.cursor - 1);
      } else if (direction === 'F') {
        this.cursor = this.text.length;
      } else if (direction === 'H') {
        this.cursor = 0;
      }
      return cursorMatch[0].length;
    }

    const deleteMatch = DELETE_SEQUENCE.exec(remainder);
    if (deleteMatch) {
      this.deleteAtCursor();
      return deleteMatch[0].length;
    }

    const csiMatch = CSI_SEQUENCE.exec(remainder);
    if (csiMatch) return csiMatch[0].length;
    return Math.min(2, remainder.length);
  }

  private insert(value: string): void {
    if (!value || this.text.length >= MAX_CAPTURE_CHARACTERS) return;
    const available = MAX_CAPTURE_CHARACTERS - this.text.length;
    const bounded = value.slice(0, available);
    this.text = `${this.text.slice(0, this.cursor)}${bounded}${this.text.slice(this.cursor)}`;
    this.cursor += bounded.length;
  }

  private deleteBeforeCursor(): void {
    if (this.cursor === 0) return;
    const trailing = this.text.charCodeAt(this.cursor - 1);
    const hasSurrogatePair =
      trailing >= 0xdc00 &&
      trailing <= 0xdfff &&
      this.cursor > 1 &&
      this.text.charCodeAt(this.cursor - 2) >= 0xd800 &&
      this.text.charCodeAt(this.cursor - 2) <= 0xdbff;
    const width = hasSurrogatePair ? 2 : 1;
    const start = Math.max(0, this.cursor - width);
    this.text = `${this.text.slice(0, start)}${this.text.slice(this.cursor)}`;
    this.cursor = start;
  }

  private deleteAtCursor(): void {
    if (this.cursor >= this.text.length) return;
    const codePoint = this.text.codePointAt(this.cursor);
    const width = codePoint !== undefined && codePoint > 0xffff ? 2 : 1;
    this.text = `${this.text.slice(0, this.cursor)}${this.text.slice(this.cursor + width)}`;
  }

  private deletePreviousWord(): void {
    let start = this.cursor;
    while (start > 0 && /\s/.test(this.text[start - 1] ?? '')) start -= 1;
    while (start > 0 && !/\s/.test(this.text[start - 1] ?? '')) start -= 1;
    this.text = `${this.text.slice(0, start)}${this.text.slice(this.cursor)}`;
    this.cursor = start;
  }
}

const states = new Map<string, TerminalInputCaptureState>();

function getState(sessionId: string): TerminalInputCaptureState {
  let state = states.get(sessionId);
  if (!state) {
    state = {
      buffer: new TerminalInputBuffer(),
      ignoreNextData: false,
      submissionPending: false,
    };
    states.set(sessionId, state);
  }
  return state;
}

export function captureTerminalInputData(sessionId: string, data: string): void {
  const state = getState(sessionId);
  if (state.ignoreNextData) {
    state.ignoreNextData = false;
    return;
  }

  if (state.submissionPending) {
    state.submissionPending = false;
    if (data === '\r') {
      persistSubmission(sessionId, state.buffer.consume());
      return;
    }
  }

  state.buffer.applyData(data);
}

export function captureTerminalPasteText(sessionId: string, text: string): void {
  getState(sessionId).buffer.appendText(text);
}

export function captureTerminalLineBreak(sessionId: string, ignoreNextData = false): void {
  const state = getState(sessionId);
  state.submissionPending = false;
  state.ignoreNextData = ignoreNextData;
  state.buffer.appendLineBreak();
}

export function expectTerminalSubmission(sessionId: string): void {
  const state = getState(sessionId);
  state.ignoreNextData = false;
  state.submissionPending = true;
}

export function clearTerminalInputCapture(sessionId: string): void {
  states.delete(sessionId);
}

export function resetTerminalInputCapturesForTest(): void {
  states.clear();
}

function persistSubmission(sessionId: string, text: string): void {
  if (!text.trim()) return;
  void recordTerminalInputHistory(sessionId, text).catch((error: unknown) => {
    log.warn(() => `Failed to record terminal input history: ${String(error)}`);
  });
}
