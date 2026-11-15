/** ACH1 native history values. No JSON text or provider-specific projection. */
export function decodeNativeHistory(packet: ArrayBuffer): unknown {
  if (packet.byteLength < 5 || packet.byteLength > 64 * 1024 * 1024) {
    throw new Error('Invalid native history packet size.');
  }
  const bytes = new Uint8Array(packet);
  const view = new DataView(packet);
  const utf8 = new TextDecoder('utf-8', { fatal: true });
  const strings: string[] = [];
  const dates: string[] = [];
  const shapes: string[][] = [];
  let offset = 4;
  let decodedBudget = 64 * 1024 * 1024;
  const charge = (bytes: number): void => {
    decodedBudget -= bytes;
    if (decodedBudget < 0) throw new Error('Native history decoded size limit exceeded.');
  };
  if (view.getUint32(0, true) !== 0x31484341) throw new Error('Unknown native history format.');
  const take = (count: number): number => {
    if (count < 0 || count > bytes.length - offset) throw new Error('Truncated native history.');
    const start = offset;
    offset += count;
    return start;
  };
  const byte = (): number => bytes[take(1)] ?? 0;
  const variable = (): number => {
    let value = 0;
    let factor = 1;
    for (let n = 0; n < 10; n++) {
      const part = byte();
      if (n === 9 && part > 1) throw new Error('Overflowing native history integer.');
      value += (part & 127) * factor;
      if (part < 128) return value;
      factor *= 128;
    }
    throw new Error('Invalid native history integer.');
  };
  const signed = (): number => {
    const negative = ((bytes[offset] ?? 0) & 1) !== 0;
    const value = variable();
    return negative ? -Math.floor(value / 2) - 1 : Math.floor(value / 2);
  };
  const signedTicks = (): bigint => {
    let value = 0n;
    for (let n = 0; n < 10; n++) {
      const part = byte();
      if (n === 9 && part > 1) throw new Error('Overflowing native history date.');
      value |= BigInt(part & 127) << BigInt(n * 7);
      if (part < 128) return (value >> 1n) ^ -(value & 1n);
    }
    throw new Error('Invalid native history date.');
  };
  const count = (): number => {
    const n = variable();
    if (n > 1_000_000 || n > bytes.length - offset)
      throw new Error('Invalid native history count.');
    return n;
  };
  const string = (tag: number): string => {
    if (tag === 8) {
      const index = variable();
      const value = strings[index];
      if (value === undefined) throw new Error('Invalid native history string reference.');
      charge(value.length * 2);
      return value;
    }
    if (tag !== 7) throw new Error('Invalid native history property name.');
    const length = variable();
    const start = take(length);
    const value = utf8.decode(bytes.subarray(start, start + length));
    strings.push(value);
    charge(value.length * 2);
    return value;
  };
  // eslint-disable-next-line complexity -- One bounded recursive wire visitor keeps cursor and depth checks together.
  const value = (depth: number): unknown => {
    if (depth > 64) throw new Error('Native history nesting limit exceeded.');
    charge(8);
    const tag = byte();
    switch (tag) {
      case 0:
        return null;
      case 1:
        return false;
      case 2:
        return true;
      case 3:
      case 4:
        return signed();
      case 5: {
        const number = view.getFloat64(take(8), true);
        if (!Number.isFinite(number)) throw new Error('Invalid native history number.');
        return number;
      }
      case 6: {
        const at = take(16);
        const lo = view.getUint32(at, true);
        const mid = view.getUint32(at + 4, true);
        const hi = view.getUint32(at + 8, true);
        const flags = view.getUint32(at + 12, true);
        const scale = (flags >>> 16) & 255;
        if ((flags & 0x7f00ffff) !== 0 || scale > 28)
          throw new Error('Invalid native history decimal.');
        return (
          ((lo + mid * 4294967296 + hi * 18446744073709551616) / 10 ** scale) *
          (flags >>> 31 ? -1 : 1)
        );
      }
      case 7:
      case 8:
        return string(tag);
      case 9: {
        const ticks = signedTicks();
        const minutes = signed();
        if (minutes < -840 || minutes > 840) throw new Error('Invalid native history offset.');
        const wholeSeconds = ticks >= 0n ? ticks / 10000000n : (ticks - 9999999n) / 10000000n;
        const fraction = (ticks - wholeSeconds * 10000000n)
          .toString()
          .padStart(7, '0')
          .replace(/0+$/, '');
        const date = new Date(Number(wholeSeconds) * 1000).toISOString().slice(0, 19);
        const abs = Math.abs(minutes);
        const zone = `${minutes < 0 ? '-' : '+'}${Math.floor(abs / 60)
          .toString()
          .padStart(2, '0')}:${(abs % 60).toString().padStart(2, '0')}`;
        const result = `${date}${fraction ? `.${fraction}` : ''}${zone}`;
        dates.push(result);
        charge(result.length * 2);
        return result;
      }
      case 10: {
        const result = dates[variable()];
        if (result === undefined) throw new Error('Invalid native history date reference.');
        charge(result.length * 2);
        return result;
      }
      case 11: {
        const length = count();
        charge(32 + length * 8);
        const result: unknown[] = new Array<unknown>(length);
        for (let i = 0; i < length; i++) result[i] = value(depth + 1);
        return result;
      }
      case 16: {
        const number = string(byte());
        if (!/^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$/.test(number))
          throw new Error('Invalid native history JSON number.');
        return Number(number);
      }
      case 13:
      case 14: {
        let fields: string[];
        if (tag === 13) {
          const fieldCount = count();
          charge(32 + fieldCount * 8);
          fields = new Array<string>(fieldCount);
          for (let i = 0; i < fields.length; i++) fields[i] = string(byte());
          shapes.push(fields);
        } else {
          const shape = shapes[variable()];
          if (!shape) throw new Error('Invalid native history shape reference.');
          fields = shape;
        }
        charge(32 + fields.length * 16);
        const result: Record<string, unknown> = {};
        for (const name of fields) {
          if (bytes[offset] === 15) {
            offset++;
            continue;
          }
          const field = value(depth + 1);
          if (name === '__proto__')
            Object.defineProperty(result, name, {
              value: field,
              enumerable: true,
              writable: true,
              configurable: true,
            });
          else result[name] = field;
        }
        return result;
      }
      case 12: {
        const length = count();
        charge(32 + length * 16);
        const result: Record<string, unknown> = {};
        for (let i = 0; i < length; i++) {
          const name = string(byte());
          const field = value(depth + 1);
          if (name === '__proto__')
            Object.defineProperty(result, name, {
              value: field,
              enumerable: true,
              writable: true,
              configurable: true,
            });
          else result[name] = field;
        }
        return result;
      }
      default:
        throw new Error('Unknown native history token.');
    }
  };
  const result = value(0);
  if (offset !== bytes.length) throw new Error('Trailing native history data.');
  return result;
}
