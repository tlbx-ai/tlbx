import { beforeEach, describe, expect, it } from 'vitest';
import { clearBracketedPasteScanState, scanBracketedPasteMode } from './bracketedPasteScan';

const bytes = (text: string): Uint8Array => new TextEncoder().encode(text);

describe('streaming paste mode scanner', () => {
  beforeEach(() => clearBracketedPasteScanState());

  it('handles every frame boundary and reports the last complete mode', () => {
    const data = bytes('text\x1b[?2004hmore\x1b[?2004ltail');
    for (let split = 0; split <= data.length; split++) {
      clearBracketedPasteScanState();
      const first = scanBracketedPasteMode(data.subarray(0, split), 'a');
      const second = scanBracketedPasteMode(data.subarray(split), 'a');
      expect(second ?? first).toBe(false);
    }
  });

  it('preserves partial matches across empty and single-byte frames without cross-session state', () => {
    for (const byte of bytes('\x1b[?2004')) {
      expect(scanBracketedPasteMode(new Uint8Array([byte]), 'a')).toBeNull();
      expect(scanBracketedPasteMode(new Uint8Array(), 'a')).toBeNull();
    }
    expect(scanBracketedPasteMode(bytes('h'), 'b')).toBeNull();
    expect(scanBracketedPasteMode(bytes('h'), 'a')).toBe(true);
    expect(scanBracketedPasteMode(bytes('\x1b[?20\x1b[?2004l'), 'a')).toBe(false);
  });

  it('clears incomplete prefixes when a session is reset', () => {
    scanBracketedPasteMode(bytes('\x1b[?2004'), 'a');
    clearBracketedPasteScanState('a');
    expect(scanBracketedPasteMode(bytes('h'), 'a')).toBeNull();
  });
});
