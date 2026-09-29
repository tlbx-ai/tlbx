import { parseOutputFrame } from '../../utils';

const MAX_COALESCED_BYTES = 64 * 1024;
const HEADER_BYTES = 12;
// Only queue-owned merged views are mutable. Incoming transport frames are
// copied once; growing batches copy again only when their capacity doubles.
const mergedBuffers = new WeakMap<Uint8Array, Uint8Array>();

export function tryMergeOutputPayloads(
  previousPayload: Uint8Array,
  incomingPayload: Uint8Array,
): Uint8Array | null {
  const previous = parseOutputFrame(previousPayload);
  const incoming = parseOutputFrame(incomingPayload);
  const incomingLength = BigInt(incoming.data.byteLength);
  const dataLength = previous.data.byteLength + incoming.data.byteLength;
  if (
    !previous.valid ||
    !incoming.valid ||
    previous.cols !== incoming.cols ||
    previous.rows !== incoming.rows ||
    incoming.sequenceEnd < incomingLength ||
    previous.sequenceEnd !== incoming.sequenceEnd - incomingLength ||
    dataLength > MAX_COALESCED_BYTES
  ) {
    return null;
  }

  const length = HEADER_BYTES + dataLength;
  let storage = mergedBuffers.get(previousPayload);
  if (!storage || storage.length < length) {
    const capacity = Math.min(
      MAX_COALESCED_BYTES,
      Math.max(1024, 2 ** Math.ceil(Math.log2(dataLength))),
    );
    storage = new Uint8Array(HEADER_BYTES + capacity);
    storage.set(previousPayload);
  }
  storage.set(incoming.data, previousPayload.byteLength);
  new DataView(storage.buffer).setBigUint64(0, incoming.sequenceEnd, true);
  const merged = storage.subarray(0, length);
  mergedBuffers.delete(previousPayload);
  mergedBuffers.set(merged, storage);
  return merged;
}
