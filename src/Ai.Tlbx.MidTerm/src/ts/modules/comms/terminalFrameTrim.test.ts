import { describe, expect, it } from 'vitest';
import { classifyTerminalFrameSequence, trimFrameToUnseenSuffix } from './terminalFrameTrim';

describe('terminalFrameTrim', () => {
  it('distinguishes contiguous, overlapping, duplicate, and forward-gap frames', () => {
    expect(classifyTerminalFrameSequence(3, 13n, 10n).kind).toBe('contiguous');
    expect(classifyTerminalFrameSequence(5, 13n, 10n).kind).toBe('overlap');
    expect(classifyTerminalFrameSequence(2, 10n, 10n).kind).toBe('duplicate');
    expect(classifyTerminalFrameSequence(2, 15n, 10n)).toEqual({
      kind: 'gap',
      sequenceStart: 13n,
    });
  });

  it('trims a byte-aligned overlap without replaying the seen prefix', () => {
    const data = new TextEncoder().encode('abcde');

    expect(new TextDecoder().decode(trimFrameToUnseenSuffix(data, 13n, 10n))).toBe('cde');
  });

  it('keeps a frame intact when trimming would split terminal parser state', () => {
    const data = new Uint8Array([0x1b, 0x5b, 0x33, 0x31, 0x6d, 0x78]);

    expect(trimFrameToUnseenSuffix(data, 16n, 12n)).toEqual(data);
  });
});
