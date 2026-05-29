type SendInputNow = (sessionId: string, data: string, inputAtMs: number) => void;

interface PrintableInputBurst {
  data: string;
  firstInputAtMs: number;
  timer: ReturnType<typeof setTimeout>;
}

export interface PrintableInputBurstCoalescer {
  enqueue(sessionId: string, data: string, inputAtMs: number): boolean;
  flush(sessionId: string): void;
  flushAll(): void;
}

export function createPrintableInputBurstCoalescer(
  windowMs: number,
  sendNow: SendInputNow,
): PrintableInputBurstCoalescer {
  const bursts = new Map<string, PrintableInputBurst>();

  const clear = (sessionId: string): Omit<PrintableInputBurst, 'timer'> | null => {
    const burst = bursts.get(sessionId);
    if (!burst) {
      return null;
    }

    clearTimeout(burst.timer);
    bursts.delete(sessionId);
    return { data: burst.data, firstInputAtMs: burst.firstInputAtMs };
  };

  const flush = (sessionId: string): void => {
    const burst = clear(sessionId);
    if (!burst || burst.data.length === 0) {
      return;
    }

    sendNow(sessionId, burst.data, burst.firstInputAtMs);
  };

  return {
    enqueue(sessionId: string, data: string, inputAtMs: number): boolean {
      if (!isPrintableBurstInput(data)) {
        flush(sessionId);
        return false;
      }

      const existing = bursts.get(sessionId);
      if (!existing) {
        sendNow(sessionId, data, inputAtMs);
        const timer = setTimeout(() => {
          flush(sessionId);
        }, windowMs);
        bursts.set(sessionId, { data: '', firstInputAtMs: inputAtMs, timer });
        return true;
      }

      existing.data += data;
      return true;
    },

    flush,

    flushAll(): void {
      for (const sessionId of [...bursts.keys()]) {
        flush(sessionId);
      }
    },
  };
}

function isPrintableBurstInput(data: string): boolean {
  if (data.length === 0) {
    return false;
  }

  for (let i = 0; i < data.length; i++) {
    const code = data.charCodeAt(i);
    if (code < 0x20 || code === 0x7f || code === 0x1b) {
      return false;
    }
  }

  return true;
}
