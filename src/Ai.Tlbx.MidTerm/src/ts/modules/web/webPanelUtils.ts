export function normalizeUrl(raw: string): string {
  if (!raw.includes('://')) {
    const isLocal =
      raw.startsWith('localhost') || raw.startsWith('127.0.0.1') || raw.startsWith('[::1]');
    return `${isLocal ? 'http://' : 'https://'}${raw}`;
  }
  return raw;
}

export function decodeScreenshotDataUrl(dataUrl: string): Blob | null {
  const commaIndex = dataUrl.indexOf(',');
  if (commaIndex < 0) {
    return null;
  }

  const meta = dataUrl.slice(0, commaIndex);
  const mime = /^data:([^;]+)/.exec(meta)?.[1] ?? 'image/png';
  try {
    const binary = atob(dataUrl.slice(commaIndex + 1));
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
      bytes[i] = binary.charCodeAt(i);
    }
    return new Blob([bytes], { type: mime });
  } catch {
    return null;
  }
}
