import { describe, expect, it } from 'vitest';
import {
  createBracketedPasteContent,
  normalizeTerminalPasteLineEndings,
  sanitizeTerminalPasteContent,
} from './terminalPaste';

describe('terminal paste text handling', () => {
  it('normalizes browser clipboard newlines to terminal carriage returns for raw shells', () => {
    expect(normalizeTerminalPasteLineEndings('alpha\nbeta')).toBe('alpha\rbeta');
    expect(normalizeTerminalPasteLineEndings('alpha\r\nbeta')).toBe('alpha\rbeta');
    expect(normalizeTerminalPasteLineEndings('alpha\rbeta')).toBe('alpha\rbeta');
  });

  it('keeps multiline TUI paste content inside one bracketed paste payload', () => {
    const content = normalizeTerminalPasteLineEndings('codex\nclaude\nedit\nnano');
    expect(createBracketedPasteContent(content)).toBe(
      '\x1b[200~codex\rclaude\redit\rnano\x1b[201~',
    );
  });

  it('sanitizes terminal control sequences before bracketed paste is re-applied', () => {
    expect(sanitizeTerminalPasteContent('\x1b[200~alpha\nbeta\x1b[201~')).toBe('alpha\nbeta');
    expect(sanitizeTerminalPasteContent('\x1b[?2004halpha\x1b[?2004l')).toBe('alpha');
    expect(sanitizeTerminalPasteContent('\x1b[31mred\x1b[0m')).toBe('red');
  });
});
