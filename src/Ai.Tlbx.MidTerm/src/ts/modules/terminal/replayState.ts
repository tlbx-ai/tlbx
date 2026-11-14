/** Persistent modes at the first retained byte, before parsing replay output. */
export function createTerminalReplayPrefix(mode: number, inputModes = 0): string | null {
  let prefix = mode === 47 || mode === 1047 || mode === 1049 ? `\x1b[?${mode}h` : '';
  if ((inputModes & 0x8000) !== 0) {
    const tracking = [0, 9, 1000, 1002, 1003][inputModes & 7] ?? 0;
    const encoding = [0, 0, 1006, 0, 1016][(inputModes >> 3) & 7] ?? 0;
    const enabled = [
      tracking,
      encoding,
      (inputModes & (1 << 6)) !== 0 ? 1 : 0,
      (inputModes & (1 << 7)) !== 0 ? 2004 : 0,
      (inputModes & (1 << 8)) !== 0 ? 1004 : 0,
    ].filter(Boolean);
    if (enabled.length > 0) prefix += `\x1b[?${enabled.join(';')}h`;
  }
  return prefix || null;
}
