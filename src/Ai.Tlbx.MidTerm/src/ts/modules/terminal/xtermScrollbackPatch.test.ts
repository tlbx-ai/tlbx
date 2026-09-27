import { createRequire } from 'node:module';
import { describe, expect, it } from 'vitest';

interface BufferLineLike {
  translateToString(trimRight?: boolean): string;
}

interface TerminalLike {
  buffer: {
    active: {
      baseY: number;
      length: number;
      viewportY: number;
      getLine(index: number): BufferLineLike | undefined;
    };
  };
  dispose(): void;
  scrollLines(amount: number): void;
  write(data: string, callback: () => void): void;
}

type TerminalConstructor = new (options: {
  cols: number;
  rows: number;
  scrollback: number;
}) => TerminalLike;

const require = createRequire(import.meta.url);
const { Terminal } = require('@xterm/xterm') as { Terminal: TerminalConstructor };

function write(terminal: TerminalLike, data: string): Promise<void> {
  return new Promise((resolve) => terminal.write(data, resolve));
}

function markerNumbers(terminal: TerminalLike): number[] {
  const markers = new Set<number>();
  const buffer = terminal.buffer.active;

  for (let index = 0; index < buffer.length; index++) {
    const match = buffer
      .getLine(index)
      ?.translateToString(true)
      .match(/LINE_(\d{3})/);
    if (match) {
      markers.add(Number(match[1]));
    }
  }

  return [...markers].sort((left, right) => left - right);
}

describe('patched xterm scroll-up handling', () => {
  it('preserves top-anchored scroll-region lines while the user is viewing scrollback', async () => {
    const terminal = new Terminal({ cols: 80, rows: 24, scrollback: 1000 });

    try {
      let output = '\x1b[1;20r\x1b[20;1H';
      for (let index = 1; index <= 30; index++) {
        output += `LINE_${String(index).padStart(3, '0')}\r\n`;
      }
      await write(terminal, output);

      terminal.scrollLines(-5);
      const viewportBeforeScrollUp = terminal.buffer.active.viewportY;
      const baseBeforeScrollUp = terminal.buffer.active.baseY;

      await write(terminal, '\x1b[2S\x1b[r');

      expect(markerNumbers(terminal)).toEqual(Array.from({ length: 30 }, (_, index) => index + 1));
      expect(terminal.buffer.active.baseY).toBe(baseBeforeScrollUp + 2);
      expect(terminal.buffer.active.viewportY).toBe(viewportBeforeScrollUp);
    } finally {
      terminal.dispose();
    }
  });
});
