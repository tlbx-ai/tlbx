import { decodeNativeHistory } from './nativeHistory';

/** One worker per ordered history socket; binary buffers transfer without copying. */
export class NativeHistoryDecoder {
  private worker: Worker | null = null;
  private nextId = 0;
  private readonly pending = new Map<
    number,
    { resolve(value: unknown): void; reject(error: Error): void }
  >();

  constructor() {
    if (typeof Worker === 'undefined') return;
    const source = `const decode = ${decodeNativeHistory.toString()};
      self.onmessage = ({data}) => {
        try { self.postMessage({id:data.id, value:decode(data.packet)}); }
        catch(error) { self.postMessage({id:data.id, error:String(error)}); }
      };`;
    const url = URL.createObjectURL(new Blob([source], { type: 'text/javascript' }));
    try {
      this.worker = new Worker(url);
      this.worker.onmessage = (
        event: MessageEvent<{ id: number; value?: unknown; error?: string }>,
      ) => {
        const result = this.pending.get(event.data.id);
        this.pending.delete(event.data.id);
        if (event.data.error) result?.reject(new Error(event.data.error));
        else result?.resolve(event.data.value);
      };
      this.worker.onerror = () => {
        this.dispose();
      };
    } finally {
      URL.revokeObjectURL(url);
    }
  }

  decode(packet: ArrayBuffer): Promise<unknown> {
    const worker = this.worker;
    if (!worker) return Promise.resolve(decodeNativeHistory(packet));
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      worker.postMessage({ id, packet }, [packet]);
    });
  }

  dispose(): void {
    this.worker?.terminate();
    this.worker = null;
    for (const request of this.pending.values())
      request.reject(new Error('Native history decoder closed.'));
    this.pending.clear();
  }
}
