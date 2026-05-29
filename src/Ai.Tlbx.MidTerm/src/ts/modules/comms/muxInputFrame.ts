type EncodeSessionId = (buffer: Uint8Array, offset: number, sessionId: string) => void;

export function createMuxInputFrame(
  headerSize: number,
  frameType: number,
  sessionId: string,
  data: string,
  encodeSessionId: EncodeSessionId,
  textEncoder: TextEncoder,
): Uint8Array {
  if (isAsciiOnly(data)) {
    const frame = new Uint8Array(headerSize + data.length);
    frame[0] = frameType;
    encodeSessionId(frame, 1, sessionId);
    for (let i = 0; i < data.length; i++) {
      frame[headerSize + i] = data.charCodeAt(i);
    }
    return frame;
  }

  const payload = textEncoder.encode(data);
  const frame = new Uint8Array(headerSize + payload.length);
  frame[0] = frameType;
  encodeSessionId(frame, 1, sessionId);
  frame.set(payload, headerSize);
  return frame;
}

function isAsciiOnly(data: string): boolean {
  for (let i = 0; i < data.length; i++) {
    if (data.charCodeAt(i) > 0x7f) {
      return false;
    }
  }

  return true;
}
