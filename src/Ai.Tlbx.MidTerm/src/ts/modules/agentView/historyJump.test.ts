import { describe, expect, it } from 'vitest';
import { buildHistoryJumpVirtualWindow, resolveHistoryJumpEntryIndex } from './historyJump';

describe('canonical history jumps across filtered visual rows', () => {
  const snapshot = { historyWindowStart: 100, historyWindowEnd: 110 };
  const visible = [{ order: 101 }, { order: 104 }, { order: 109 }];

  it('selects the exact canonical item after omitted rows', () => {
    expect(resolveHistoryJumpEntryIndex(visible, snapshot, 108)).toBe(2);
    expect(resolveHistoryJumpEntryIndex(visible, snapshot, 103)).toBe(1);
  });

  it('uses the next visible row for a hidden target and preceding row at the end', () => {
    expect(resolveHistoryJumpEntryIndex(visible, snapshot, 102)).toBe(1);
    expect(resolveHistoryJumpEntryIndex(visible, snapshot, 109)).toBe(2);
  });

  it('waits for the correct retained window rather than jumping into the wrong one', () => {
    expect(resolveHistoryJumpEntryIndex(visible, snapshot, 99)).toBe(-1);
    expect(resolveHistoryJumpEntryIndex(visible, snapshot, 110)).toBe(-1);
    expect(resolveHistoryJumpEntryIndex([], snapshot, 105)).toBe(-1);
    expect(resolveHistoryJumpEntryIndex(visible, null, 105)).toBe(-1);
    expect(resolveHistoryJumpEntryIndex(visible, snapshot, Number.NaN)).toBe(-1);
  });

  it('mounts a bounded target corridor without retaining all intervening rows', () => {
    const rows = Array.from({ length: 1000 }, (_, index) => ({ height: (index % 3) + 20 }));
    const original = { start: 0, end: 30, topSpacerPx: 0, bottomSpacerPx: 20000 };
    const result = buildHistoryJumpVirtualWindow(rows, original, 800, 12, (row) => row.height);
    expect(result.start).toBeLessThanOrEqual(800);
    expect(result.end).toBeGreaterThan(800);
    expect(result.end - result.start).toBe(30);
    expect(result.topSpacerPx).toBe(
      rows.slice(0, result.start).reduce((sum, r) => sum + r.height, 0),
    );
    expect(result.bottomSpacerPx).toBe(
      rows.slice(result.end).reduce((sum, r) => sum + r.height, 0),
    );
  });

  it('keeps both retained-window edges covered when seeking to their bounds', () => {
    const rows = Array.from({ length: 100 }, () => 40);
    const original = { start: 35, end: 60, topSpacerPx: 1400, bottomSpacerPx: 1600 };
    const first = buildHistoryJumpVirtualWindow(rows, original, 0, 12, (height) => height);
    const last = buildHistoryJumpVirtualWindow(rows, original, 99, 12, (height) => height);
    expect(first).toEqual({ start: 0, end: 25, topSpacerPx: 0, bottomSpacerPx: 3000 });
    expect(last).toEqual({ start: 75, end: 100, topSpacerPx: 3000, bottomSpacerPx: 0 });
  });
});
