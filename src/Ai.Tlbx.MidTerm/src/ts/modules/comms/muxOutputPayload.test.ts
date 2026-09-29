import { describe, expect, it } from 'vitest';
import { tryMergeOutputPayloads } from './muxOutputPayload';

function frame(sequenceEnd: bigint, bytes: Uint8Array, cols = 80): Uint8Array {
  const payload = new Uint8Array(12 + bytes.length);
  const view = new DataView(payload.buffer);
  view.setBigUint64(0, sequenceEnd, true);
  view.setUint16(8, cols, true);
  view.setUint16(10, 24, true);
  payload.set(bytes, 12);
  return payload;
}

describe('queued output payload growth', () => {
  it('retains every byte of a tiny-frame burst without repeatedly copying the accumulated output', () => {
    const bytes = new TextEncoder().encode('€🙂ANSI\x1b[31m'.repeat(300));
    const original = frame(1n, bytes.subarray(0, 1));
    const saved = original.slice();
    let merged = original;
    const allocations = new Set<ArrayBufferLike>();
    for (let i = 1; i < bytes.length; i++) {
      merged = tryMergeOutputPayloads(merged, frame(BigInt(i + 1), bytes.subarray(i, i + 1)))!;
      allocations.add(merged.buffer);
    }
    expect(merged.subarray(12)).toEqual(bytes);
    expect(new DataView(merged.buffer).getBigUint64(0, true)).toBe(BigInt(bytes.length));
    expect(original).toEqual(saved);
    expect(allocations.size).toBeLessThanOrEqual(5);
  });

  it('keeps geometry and sequence barriers intact and bounds batch size', () => {
    let previous = frame(1n, new Uint8Array([65]));
    previous = tryMergeOutputPayloads(previous, frame(2n, new Uint8Array([66])))!;
    const saved = previous.slice();
    expect(tryMergeOutputPayloads(previous, frame(4n, new Uint8Array([67])))).toBeNull();
    expect(tryMergeOutputPayloads(previous, frame(3n, new Uint8Array([67]), 81))).toBeNull();
    expect(previous).toEqual(saved);
    expect(tryMergeOutputPayloads(previous, frame(65539n, new Uint8Array(65537)))).toBeNull();
  });
});
