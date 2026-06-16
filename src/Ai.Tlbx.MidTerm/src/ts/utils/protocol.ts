/**
 * Protocol Utilities
 *
 * Binary protocol helpers for mux WebSocket communication.
 */

import { MAX_FRAME_DIMENSION } from '../constants';
import { createLogger } from '../modules/logging';

const log = createLogger('mux');
type CompressedFrameWorkerResponse =
  | {
      id: number;
      ok: true;
      sequenceEnd: string;
      cols: number;
      rows: number;
      valid: boolean;
      data: ArrayBuffer;
    }
  | {
      id: number;
      ok: false;
      sequenceEnd: string;
      cols: number;
      rows: number;
      error: string;
    };

type PendingCompressedFrame = {
  resolve: (frame: OutputFrame) => void;
  reject: (error: Error) => void;
};

let compressedFrameWorker: Worker | null | undefined;
let compressedFrameWorkerRequestId = 0;
const pendingCompressedFrames = new Map<number, PendingCompressedFrame>();

/** Parsed output frame from server */
export interface OutputFrame {
  sequenceEnd: bigint;
  cols: number;
  rows: number;
  data: Uint8Array;
  valid: boolean;
}

/**
 * Parse output frame from binary payload.
 * Frame format: [sequenceEnd:8][cols:2][rows:2][data]
 */
export function parseOutputFrame(payload: Uint8Array): OutputFrame {
  const view = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
  const sequenceEnd = payload.length >= 8 ? view.getBigUint64(0, true) : 0n;
  const cols = (payload[8] ?? 0) | ((payload[9] ?? 0) << 8);
  const rows = (payload[10] ?? 0) | ((payload[11] ?? 0) << 8);
  // The mux layer already takes ownership of the WebSocket payload before it
  // reaches this parser, so we can return a view here instead of another copy.
  const data = payload.subarray(12);
  const valid = cols > 0 && cols <= MAX_FRAME_DIMENSION && rows > 0 && rows <= MAX_FRAME_DIMENSION;

  return { sequenceEnd, cols, rows, data, valid };
}

/**
 * Parse compressed output frame and decompress.
 * Frame format: [sequenceEnd:8][cols:2][rows:2][uncompressedLen:4][gzip-data...]
 */
export async function parseCompressedOutputFrame(payload: Uint8Array): Promise<OutputFrame> {
  const worker = getOrCreateCompressedFrameWorker();
  if (worker) {
    try {
      return await parseCompressedOutputFrameInWorker(worker, payload);
    } catch (e) {
      log.warn(() => `Worker decompression failed, falling back to main thread: ${String(e)}`);
    }
  }

  const view = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
  const sequenceEnd = payload.length >= 8 ? view.getBigUint64(0, true) : 0n;
  const cols = (payload[8] ?? 0) | ((payload[9] ?? 0) << 8);
  const rows = (payload[10] ?? 0) | ((payload[11] ?? 0) << 8);
  const valid = cols > 0 && cols <= MAX_FRAME_DIMENSION && rows > 0 && rows <= MAX_FRAME_DIMENSION;

  // Skip uncompressedLen (bytes 12-15) - we don't need it, DecompressionStream handles sizing
  const compressedData = payload.subarray(16);

  try {
    const data = await decompressGzip(compressedData);
    return { sequenceEnd, cols, rows, data, valid };
  } catch (e) {
    log.error(() => `Decompression failed: ${String(e)}`);
    return { sequenceEnd, cols, rows, data: new Uint8Array(0), valid: false };
  }
}

/**
 * Decompress GZip data using native DecompressionStream API.
 * Uses Blob/Response pipeline to avoid backpressure deadlock.
 */
export async function decompressGzip(compressed: Uint8Array): Promise<Uint8Array> {
  const blob = new Blob([compressed as BlobPart]);
  const stream = blob.stream().pipeThrough(new DecompressionStream('gzip'));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

function getOrCreateCompressedFrameWorker(): Worker | null {
  if (compressedFrameWorker !== undefined) {
    return compressedFrameWorker;
  }

  if (
    typeof Worker === 'undefined' ||
    typeof Blob === 'undefined' ||
    typeof URL === 'undefined' ||
    typeof URL.createObjectURL !== 'function' ||
    typeof DecompressionStream === 'undefined'
  ) {
    compressedFrameWorker = null;
    return compressedFrameWorker;
  }

  try {
    const source = `
      const MAX_FRAME_DIMENSION = ${MAX_FRAME_DIMENSION};

      async function decompressGzip(compressed) {
        const blob = new Blob([compressed]);
        const stream = blob.stream().pipeThrough(new DecompressionStream('gzip'));
        return new Uint8Array(await new Response(stream).arrayBuffer());
      }

      self.onmessage = async (event) => {
        const { id, payload } = event.data || {};
        const bytes = new Uint8Array(payload || new ArrayBuffer(0));
        const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
        const sequenceEnd = bytes.length >= 8 ? view.getBigUint64(0, true) : 0n;
        const cols = (bytes[8] || 0) | ((bytes[9] || 0) << 8);
        const rows = (bytes[10] || 0) | ((bytes[11] || 0) << 8);
        const valid = cols > 0 && cols <= MAX_FRAME_DIMENSION && rows > 0 && rows <= MAX_FRAME_DIMENSION;

        try {
          const data = await decompressGzip(bytes.subarray(16));
          self.postMessage(
            { id, ok: true, sequenceEnd: sequenceEnd.toString(), cols, rows, valid, data: data.buffer },
            [data.buffer]
          );
        } catch (error) {
          self.postMessage({
            id,
            ok: false,
            sequenceEnd: sequenceEnd.toString(),
            cols,
            rows,
            error: String(error)
          });
        }
      };
    `;
    const url = URL.createObjectURL(new Blob([source], { type: 'text/javascript' }));
    compressedFrameWorker = new Worker(url);
    URL.revokeObjectURL(url);
    compressedFrameWorker.onmessage = (event: MessageEvent<CompressedFrameWorkerResponse>) => {
      const response = event.data;
      const pending = pendingCompressedFrames.get(response.id);
      pendingCompressedFrames.delete(response.id);
      if (!pending) {
        return;
      }

      if (!response.ok) {
        pending.resolve({
          sequenceEnd: BigInt(response.sequenceEnd),
          cols: response.cols,
          rows: response.rows,
          data: new Uint8Array(0),
          valid: false,
        });
        return;
      }

      pending.resolve({
        sequenceEnd: BigInt(response.sequenceEnd),
        cols: response.cols,
        rows: response.rows,
        data: new Uint8Array(response.data),
        valid: response.valid,
      });
    };
    compressedFrameWorker.onerror = (event) => {
      const error = new Error(event.message || 'Compressed frame worker failed');
      for (const pending of pendingCompressedFrames.values()) {
        pending.reject(error);
      }
      pendingCompressedFrames.clear();
      compressedFrameWorker?.terminate();
      compressedFrameWorker = null;
    };
  } catch {
    compressedFrameWorker = null;
  }

  return compressedFrameWorker;
}

function parseCompressedOutputFrameInWorker(
  worker: Worker,
  payload: Uint8Array,
): Promise<OutputFrame> {
  const id = ++compressedFrameWorkerRequestId;
  const payloadCopy = payload.slice();
  return new Promise((resolve, reject) => {
    pendingCompressedFrames.set(id, { resolve, reject });
    worker.postMessage({ id, payload: payloadCopy.buffer }, [payloadCopy.buffer]);
  });
}
