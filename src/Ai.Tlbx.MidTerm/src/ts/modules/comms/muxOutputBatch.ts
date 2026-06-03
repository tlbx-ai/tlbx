import type { TerminalState } from '../../types';
import { maxSequence } from './terminalFrameTrim';

export interface TerminalOutputDelivery {
  sessionId: string;
  state: TerminalState;
  sequenceEnd: bigint;
  cols: number;
  rows: number;
  data: Uint8Array;
}

export interface TerminalWriteBatch {
  sessionId: string;
  state: TerminalState;
  sequenceEnd: bigint;
  cols: number;
  rows: number;
  chunks: Uint8Array[];
  bytes: number;
}

export function canAppendTerminalWriteBatch(
  batch: TerminalWriteBatch | null,
  delivery: TerminalOutputDelivery,
): boolean {
  return (
    batch !== null &&
    batch.sessionId === delivery.sessionId &&
    batch.state === delivery.state &&
    batch.cols === delivery.cols &&
    batch.rows === delivery.rows
  );
}

export function appendTerminalWriteBatch(
  batch: TerminalWriteBatch | null,
  delivery: TerminalOutputDelivery,
): TerminalWriteBatch {
  if (!batch) {
    return {
      sessionId: delivery.sessionId,
      state: delivery.state,
      sequenceEnd: delivery.sequenceEnd,
      cols: delivery.cols,
      rows: delivery.rows,
      chunks: [delivery.data],
      bytes: delivery.data.byteLength,
    };
  }

  batch.sequenceEnd = maxSequence(batch.sequenceEnd, delivery.sequenceEnd);
  batch.chunks.push(delivery.data);
  batch.bytes += delivery.data.byteLength;
  return batch;
}

export function combineTerminalWriteChunks(chunks: Uint8Array[], byteLength: number): Uint8Array {
  if (chunks.length === 1) {
    return chunks[0] ?? new Uint8Array();
  }

  const combined = new Uint8Array(byteLength);
  let offset = 0;
  for (const chunk of chunks) {
    combined.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return combined;
}
