/**
 * Normalizes text before it is injected into a terminal paste path.
 *
 * Browser clipboard text uses LF/CRLF, but terminal paste input follows xterm:
 * paste newlines are sent as carriage returns, optionally wrapped in bracketed
 * paste markers by the caller.
 */
export function normalizeTerminalPasteLineEndings(text: string): string {
  return text.replace(/\r\n/g, '\r').replace(/[\r\n]/g, '\r');
}

export function createBracketedPasteContent(content: string): string {
  return '\x1b[200~' + content + '\x1b[201~';
}

export function sanitizeTerminalPasteContent(text: string): string {
  return (
    text
      .replace(/\r\n/g, '\n')
      .replace(/\r(?!\n)/g, '\n')
      // eslint-disable-next-line no-control-regex
      .replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, '')
      // eslint-disable-next-line no-control-regex
      .replace(/\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)/g, '')
      // eslint-disable-next-line no-control-regex
      .replace(/\x1b[PX^_][^\x1b]*\x1b\\/g, '')
      // eslint-disable-next-line no-control-regex
      .replace(/\x1b[\x20-\x2F]*[\x30-\x7E]/g, '')
  );
}
