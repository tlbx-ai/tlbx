// Retain only the matched prefix length across frames, never a copy of output.
const PREFIX = new Uint8Array([0x1b, 0x5b, 0x3f, 0x32, 0x30, 0x30, 0x34]);
const matchedPrefixes = new Map<string, number>();

export function clearBracketedPasteScanState(sessionId?: string): void {
  if (sessionId !== undefined) matchedPrefixes.delete(sessionId);
  else matchedPrefixes.clear();
}

export function scanBracketedPasteMode(data: Uint8Array, sessionId: string): boolean | null {
  let matched = matchedPrefixes.get(sessionId) ?? 0;
  let latestMode: boolean | null = null;
  for (let i = 0; i < data.length; i++) {
    if (matched === 0) {
      const start = data.indexOf(0x1b, i);
      if (start < 0) break;
      i = start;
    }
    const byte = data[i];
    if (matched === PREFIX.length) {
      if (byte === 0x68) latestMode = true;
      else if (byte === 0x6c) latestMode = false;
      matched = byte === 0x1b ? 1 : 0;
    } else if (byte === PREFIX[matched]) {
      matched++;
    } else {
      matched = byte === 0x1b ? 1 : 0;
    }
  }
  if (matched > 0) matchedPrefixes.set(sessionId, matched);
  else matchedPrefixes.delete(sessionId);
  return latestMode;
}
