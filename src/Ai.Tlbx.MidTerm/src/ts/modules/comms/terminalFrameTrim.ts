type TerminalSequenceState = 'normal' | 'escape' | 'csi' | 'osc' | 'string';

const SEQUENCE_MODULUS = 1n << 64n;
const HALF_SEQUENCE_RANGE = 1n << 63n;

export function isSequenceNewer(candidate: bigint, current: bigint): boolean {
  const delta = (candidate - current + SEQUENCE_MODULUS) % SEQUENCE_MODULUS;
  return delta !== 0n && delta < HALF_SEQUENCE_RANGE;
}

export function maxSequence(current: bigint, candidate: bigint): bigint {
  return isSequenceNewer(candidate, current) ? candidate : current;
}

function isUtf8ContinuationByte(value: number | undefined): boolean {
  return value !== undefined && (value & 0xc0) === 0x80;
}

function isCsiFinalByte(value: number): boolean {
  return value >= 0x40 && value <= 0x7e;
}

function isTerminalStringTerminator(data: Uint8Array, index: number): boolean {
  return data[index] === 0x07 || (data[index] === 0x1b && data[index + 1] === 0x5c);
}

// eslint-disable-next-line complexity
function advanceTerminalSequenceState(
  state: TerminalSequenceState,
  data: Uint8Array,
  index: number,
): { state: TerminalSequenceState; index: number } {
  const byte = data[index] as number;

  if (state === 'normal') {
    if (byte === 0x1b) return { state: 'escape', index };
    if (byte === 0x9b) return { state: 'csi', index };
    if (byte === 0x9d) return { state: 'osc', index };
    if (byte === 0x90 || byte === 0x9e || byte === 0x9f) return { state: 'string', index };
    return { state, index };
  }

  if (state === 'escape') {
    if (byte === 0x5b) return { state: 'csi', index };
    if (byte === 0x5d) return { state: 'osc', index };
    if (byte === 0x50 || byte === 0x5e || byte === 0x5f) return { state: 'string', index };
    return { state: 'normal', index };
  }

  if (state === 'csi') {
    return { state: isCsiFinalByte(byte) ? 'normal' : 'csi', index };
  }

  if (state === 'osc') {
    if (!isTerminalStringTerminator(data, index)) {
      return { state, index };
    }

    return { state: 'normal', index: byte === 0x1b ? index + 1 : index };
  }

  if (byte === 0x1b && data[index + 1] === 0x5c) {
    return { state: 'normal', index: index + 1 };
  }

  return { state, index };
}

export function getSafeTerminalFrameTrimOffset(data: Uint8Array, requestedOffset: number): number {
  if (requestedOffset <= 0 || requestedOffset >= data.length) {
    return requestedOffset;
  }

  if (isUtf8ContinuationByte(data[requestedOffset])) {
    return 0;
  }

  let state: TerminalSequenceState = 'normal';
  for (let i = 0; i < requestedOffset; i += 1) {
    const next = advanceTerminalSequenceState(state, data, i);
    state = next.state;
    i = next.index;
  }

  return state === 'normal' ? requestedOffset : 0;
}

export function trimFrameToUnseenSuffix(
  data: Uint8Array,
  sequenceEnd: bigint,
  receivedSeq: bigint,
): Uint8Array {
  if (data.length === 0 || receivedSeq === 0n) {
    return data;
  }

  if (!isSequenceNewer(sequenceEnd, receivedSeq)) {
    return new Uint8Array(0);
  }

  const frameLength = BigInt(data.length);
  const sequenceStart = (sequenceEnd - frameLength + SEQUENCE_MODULUS) % SEQUENCE_MODULUS;
  const overlapBytes = (receivedSeq - sequenceStart + SEQUENCE_MODULUS) % SEQUENCE_MODULUS;

  if (
    isSequenceNewer(receivedSeq, sequenceStart) &&
    overlapBytes > 0n &&
    overlapBytes < frameLength
  ) {
    const safeOffset = getSafeTerminalFrameTrimOffset(data, Number(overlapBytes));
    return safeOffset > 0 ? data.subarray(safeOffset) : data;
  }

  return data;
}
