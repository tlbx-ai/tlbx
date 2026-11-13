import type { IBufferCell, IBufferLine, ITerminalAddon, IDisposable, Terminal } from '@xterm/xterm';
import { kittyPlaceholderMarks } from './kittyPlaceholderMarks';

const PLACEHOLDER = 0x10eeee;
const MAX_ENCODED = 16_777_216;
const MAX_PIXELS = 4_194_304;
const CACHE_PIXELS = 8_388_608;
type Command = Record<string, string>;
interface VirtualImage {
  bitmap: ImageBitmap;
  columns: number;
  rows: number;
}
interface Pending {
  command: Command;
  chunks: string[];
  length: number;
}
interface PreviousCell {
  color: number;
  row: number;
  column: number;
  high: number;
}

/** The missing Kitty Unicode-placement extension. Ordinary images stay in ImageAddon. */
export class KittyVirtualImages implements ITerminalAddon {
  private terminal?: Terminal;
  private canvas?: HTMLCanvasElement;
  private readonly subscriptions: IDisposable[] = [];
  private readonly images = new Map<number, VirtualImage>();
  private pending: Pending | undefined;
  private disposed = false;

  activate(terminal: Terminal): void {
    this.terminal = terminal;
    const screen = terminal.element?.querySelector<HTMLElement>('.xterm-screen');
    if (!screen) throw new Error('Virtual images require an opened terminal');
    this.canvas = document.createElement('canvas');
    this.canvas.dataset.tlbxKittyVirtual = 'true';
    Object.assign(this.canvas.style, {
      position: 'absolute',
      inset: '0',
      pointerEvents: 'none',
      zIndex: '10',
    });
    screen.appendChild(this.canvas);
    this.subscriptions.push(
      terminal.parser.registerApcHandler({ final: 'G' }, (data) => this.command(data)),
      terminal.parser.registerEscHandler({ final: 'c' }, () => {
        this.reset();
        return false;
      }),
      terminal.parser.registerCsiHandler({ intermediates: '!', final: 'p' }, () => {
        this.reset();
        return false;
      }),
      terminal.onRender(() => {
        this.render();
      }),
      terminal.onScroll(() => {
        this.render();
      }),
      terminal.onResize(() => {
        this.render();
      }),
      terminal.buffer.onBufferChange(() => {
        this.render();
      }),
    );
  }

  dispose(): void {
    this.disposed = true;
    for (const subscription of this.subscriptions) subscription.dispose();
    this.reset();
    this.canvas?.remove();
  }

  private reset(): void {
    this.pending = undefined;
    for (const image of this.images.values()) image.bitmap.close();
    this.images.clear();
    this.render();
  }

  private async command(data: string): Promise<boolean> {
    const split = data.indexOf(';');
    const header = split < 0 ? data : data.slice(0, split);
    if (header.length > 512) return false;
    const controls: Command = {};
    for (const item of header.split(',')) {
      const equals = item.indexOf('=');
      if (equals > 0) controls[item.slice(0, equals)] = item.slice(equals + 1);
    }
    if (controls.a === 'd') {
      if (controls.d === 'i' || controls.d === 'I') {
        const id = Number(controls.i);
        this.images.get(id)?.bitmap.close();
        this.images.delete(id);
        this.render();
      }
      return false;
    }
    const continuation = Boolean(this.pending && !controls.a && controls.m !== undefined);
    if (controls.U !== '1' && !continuation) {
      this.pending = undefined;
      return false;
    }
    const complete = this.collect(controls, split < 0 ? '' : data.slice(split + 1), continuation);
    if (complete) await this.decode(complete);
    return true;
  }

  private collect(command: Command, payload: string, continuation: boolean): Pending | undefined {
    if (!continuation) this.pending = { command, chunks: [], length: 0 };
    const pending = this.pending;
    if (!pending) return undefined;
    pending.length += payload.length;
    if (pending.length > MAX_ENCODED) {
      this.pending = undefined;
      return undefined;
    }
    pending.chunks.push(payload);
    if (command.m === '1') return undefined;
    this.pending = undefined;
    return pending;
  }

  private validPlacement(command: Command): boolean {
    const id = Number(command.i);
    const validSize = (value: string | undefined): boolean => {
      const size = Number(value);
      return Number.isInteger(size) && size >= 1 && size <= 4096;
    };
    // Codex sends direct PNG virtual placements; this never enables filesystem access.
    return (
      Number.isInteger(id) &&
      id > 0 &&
      id <= 0xffffffff &&
      command.a === 'T' &&
      (!command.t || command.t === 'd') &&
      command.f === '100' &&
      (!command.p || command.p === '0') &&
      validSize(command.c) &&
      validSize(command.r)
    );
  }

  private png(payload: string): Uint8Array<ArrayBuffer> | undefined {
    const bytes = Uint8Array.from(atob(payload), (char) => char.charCodeAt(0));
    const signature = [137, 80, 78, 71, 13, 10, 26, 10];
    if (bytes.length < 24 || signature.some((value, index) => bytes[index] !== value))
      return undefined;
    const view = new DataView(bytes.buffer);
    const width = view.getUint32(16);
    const height = view.getUint32(20);
    return width && height && width * height <= MAX_PIXELS ? bytes : undefined;
  }

  private async decode(pending: Pending): Promise<void> {
    if (!this.validPlacement(pending.command)) return;
    let bitmap: ImageBitmap | undefined;
    try {
      const bytes = this.png(pending.chunks.join(''));
      if (!bytes) return;
      bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
      if (this.disposed) return;
      this.store(Number(pending.command.i), {
        bitmap,
        columns: Number(pending.command.c),
        rows: Number(pending.command.r),
      });
      bitmap = undefined;
      this.render();
    } catch {
      // Invalid image data must not interrupt terminal text or ordinary graphics.
    } finally {
      bitmap?.close();
    }
  }

  private store(id: number, image: VirtualImage): void {
    this.images.get(id)?.bitmap.close();
    this.images.delete(id);
    let pixels = image.bitmap.width * image.bitmap.height;
    for (const existing of this.images.values())
      pixels += existing.bitmap.width * existing.bitmap.height;
    while ((pixels > CACHE_PIXELS || this.images.size >= 256) && this.images.size) {
      const first = this.images.entries().next().value;
      if (!first) break;
      pixels -= first[1].bitmap.width * first[1].bitmap.height;
      first[1].bitmap.close();
      this.images.delete(first[0]);
    }
    this.images.set(id, image);
  }

  private render(): void {
    const terminal = this.terminal;
    const canvas = this.canvas;
    const dimensions = terminal?.dimensions;
    if (!terminal || !canvas || !dimensions) return;
    const { width, height } = dimensions.device.canvas;
    if (canvas.width !== width) canvas.width = width;
    if (canvas.height !== height) canvas.height = height;
    canvas.style.width = `${dimensions.css.canvas.width}px`;
    canvas.style.height = `${dimensions.css.canvas.height}px`;
    const context = canvas.getContext('2d');
    if (!context) return;
    context.clearRect(0, 0, width, height);
    if (!this.images.size) return;
    const cellWidth = dimensions.device.cell.width;
    const cellHeight = dimensions.device.cell.height;
    const buffer = terminal.buffer.active;
    const workCell = buffer.getNullCell();
    for (let y = 0; y < terminal.rows; y++) {
      const line = buffer.getLine(buffer.viewportY + y);
      if (!line) continue;
      this.renderRow(context, line, y, terminal.cols, cellWidth, cellHeight, workCell);
    }
  }

  private placeholder(
    chars: string,
    color: number,
    previous: PreviousCell | undefined,
  ): PreviousCell | undefined {
    const marks = Array.from(chars)
      .slice(1)
      .map((mark) => kittyPlaceholderMarks.get(mark.codePointAt(0) ?? 0));
    if (marks.some((mark) => mark === undefined)) return undefined;
    let row = marks[0] ?? 0;
    let column = marks[1] ?? 0;
    let high = marks[2] ?? 0;
    if (previous?.color === color) {
      if (marks[0] === undefined) row = previous.row;
      if (row === previous.row) {
        if (marks[1] === undefined) column = previous.column + 1;
        if (marks[2] === undefined && column === previous.column + 1) high = previous.high;
      }
    }
    return { color, row, column, high };
  }

  private renderRow(
    context: CanvasRenderingContext2D,
    line: IBufferLine,
    y: number,
    columns: number,
    cellWidth: number,
    cellHeight: number,
    workCell: IBufferCell,
  ): void {
    let previous: PreviousCell | undefined;
    for (let x = 0; x < columns; x++) {
      const cell = line.getCell(x, workCell);
      const chars = cell?.getChars();
      if (!cell || !chars || chars.codePointAt(0) !== PLACEHOLDER) {
        previous = undefined;
        continue;
      }
      const color = cell.isFgRGB() || cell.isFgPalette() ? cell.getFgColor() : 0;
      previous = this.placeholder(chars, color, previous);
      if (!previous) continue;
      const image = this.images.get(((color & 0xffffff) + (previous.high << 24)) >>> 0);
      if (!image || previous.row >= image.rows || previous.column >= image.columns) continue;
      const scale = Math.min(
        (image.columns * cellWidth) / image.bitmap.width,
        (image.rows * cellHeight) / image.bitmap.height,
      );
      const sourceX = (previous.column * cellWidth) / scale;
      const sourceY = (previous.row * cellHeight) / scale;
      const width = Math.min(cellWidth / scale, image.bitmap.width - sourceX);
      const height = Math.min(cellHeight / scale, image.bitmap.height - sourceY);
      if (width > 0 && height > 0)
        context.drawImage(
          image.bitmap,
          sourceX,
          sourceY,
          width,
          height,
          x * cellWidth,
          y * cellHeight,
          width * scale,
          height * scale,
        );
    }
  }
}
