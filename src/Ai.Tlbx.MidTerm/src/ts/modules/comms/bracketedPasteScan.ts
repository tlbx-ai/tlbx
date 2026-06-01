const BRACKETED_PASTE_MODE_SEQUENCE_LENGTH = 8;
const BRACKETED_PASTE_MODE_TAIL_LENGTH = BRACKETED_PASTE_MODE_SEQUENCE_LENGTH - 1;
const bracketedPasteScanTail = new Map<string, Uint8Array>();

export function clearBracketedPasteScanState(): void {
  bracketedPasteScanTail.clear();
}

export function scanBracketedPasteMode(data: Uint8Array, sessionId: string): boolean | null {
  const priorTail = bracketedPasteScanTail.get(sessionId);
  const scanData =
    priorTail && priorTail.length > 0 ? new Uint8Array(priorTail.length + data.length) : data;
  if (scanData !== data && priorTail) {
    scanData.set(priorTail, 0);
    scanData.set(data, priorTail.length);
  }

  let latestMode: boolean | null = null;
  for (let i = 0, end = scanData.length - BRACKETED_PASTE_MODE_SEQUENCE_LENGTH; i <= end; i++) {
    if (
      scanData[i] === 0x1b &&
      scanData[i + 1] === 0x5b &&
      scanData[i + 2] === 0x3f &&
      scanData[i + 3] === 0x32 &&
      scanData[i + 4] === 0x30 &&
      scanData[i + 5] === 0x30 &&
      scanData[i + 6] === 0x34
    ) {
      const mode = scanData[i + 7];
      if (mode === 0x68) latestMode = true;
      else if (mode === 0x6c) latestMode = false;
      i += 7;
    }
  }

  bracketedPasteScanTail.set(sessionId, scanData.slice(-BRACKETED_PASTE_MODE_TAIL_LENGTH));
  return latestMode;
}
