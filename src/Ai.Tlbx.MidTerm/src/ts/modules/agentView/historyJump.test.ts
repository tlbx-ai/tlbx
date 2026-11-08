import { describe, expect, it } from 'vitest';
import { alignHistoryJumpTarget, buildHistoryJumpVirtualWindow, clearHistoryJumpTarget, resolveHistoryJumpEntryIndex, setHistoryJumpTarget } from './historyJump';
import { shouldRefreshHistoryNavigatorFromScroll } from './historyNavigator';
import type { SessionAppServerControlViewState } from './types';

describe('persistent canonical seek alignment', () => {
  function fixture() {
    const layout = { top: 1000, height: 235.78125, viewportHeight: 680 };
    const viewport = {
      scrollTop: 0, scrollHeight: 5000,
      get clientHeight() { return layout.viewportHeight; },
      getBoundingClientRect: () => ({ top: 40 }),
    } as HTMLDivElement;
    const node = { getBoundingClientRect: () => ({
      top: 40 + layout.top - viewport.scrollTop,
      bottom: 40 + layout.top - viewport.scrollTop + layout.height,
      height: layout.height,
    }) };
    const history = Array.from({ length: 7 }, (_, index) => ({ entryId: `row-${123 + index}`, order: 123 + index }));
    const state = {
      snapshot: { history, historyWindowStart: 122, historyWindowEnd: 129 },
      historyEntries: [{ id: 'row-123', order: 123 }, { id: 'row-126', order: 126 }, { id: 'busy', order: 126.01 }],
      historyRenderedNodes: new Map([['row-126', { node }]]),
      historyPendingJumpTargetIndex: null, historyPendingJumpAlign: null, historyJumpTargetEntryId: null,
      historyNavigatorMode: 'drag-preview',
    } as unknown as SessionAppServerControlViewState;
    return { state, viewport, layout };
  }

  it('keeps canonical 125 through preview, hydration, preceding-row growth and target reflow', () => {
    const { state, viewport, layout } = fixture();
    setHistoryJumpTarget(state, 125, 'center');
    expect(alignHistoryJumpTarget(viewport, state)).toBe(true);
    expect(state.historyJumpTargetEntryId).toBe('row-126');
    const centered = () => layout.top - viewport.scrollTop + layout.height / 2;
    expect(centered()).toBeCloseTo(340);
    state.historyNavigatorMode = 'browse';
    state.snapshot!.historyWindowStart = 88;
    state.snapshot!.historyWindowEnd = 159;
    setHistoryJumpTarget(state, 125, 'center'); // Hydration of the same target preserves identity.
    layout.top += 180;
    expect(alignHistoryJumpTarget(viewport, state)).toBe(true);
    expect(centered()).toBeCloseTo(340);
    layout.viewportHeight = 840;
    expect(alignHistoryJumpTarget(viewport, state)).toBe(true);
    expect(centered()).toBeCloseTo(420);
    layout.height += 100;
    expect(alignHistoryJumpTarget(viewport, state)).toBe(true);
    expect(centered()).toBeCloseTo(420);
    expect(alignHistoryJumpTarget(viewport, state)).toBe(false); // Already aligned, still bound.
    expect(state.historyPendingJumpTargetIndex).toBe(125);
    expect(state.historyPendingJumpAlign).toBe('center');
    expect(state.historyNavigatorAnchorIndex).toBe(125);
  });

  it('releases the pin synchronously for user movement and replaces it for a new jump', () => {
    const { state, viewport, layout } = fixture();
    setHistoryJumpTarget(state, 125, 'center');
    alignHistoryJumpTarget(viewport, state);
    // Wheel/touch/navigation-key handlers call this before native pixel movement; no timer gate.
    clearHistoryJumpTarget(state);
    const previous = { scrollTop: viewport.scrollTop, scrollHeight: 5000, clientHeight: 680 };
    viewport.scrollTop += 40;
    layout.height += 100;
    expect(alignHistoryJumpTarget(viewport, state)).toBeNull();
    expect(shouldRefreshHistoryNavigatorFromScroll({
      now: 100, lastUserIntentAt: 100, intentWindowMs: 900, previous,
      current: { ...previous, scrollTop: viewport.scrollTop },
    })).toBe(true);
    setHistoryJumpTarget(state, 125, 'center');
    alignHistoryJumpTarget(viewport, state);
    setHistoryJumpTarget(state, 128, 'bottom');
    expect(state.historyJumpTargetEntryId).toBeNull();
    expect(state.historyPendingJumpTargetIndex).toBe(128);
    clearHistoryJumpTarget(state); // Follow-live uses the same release.
    expect(state.historyPendingJumpAlign).toBeNull();
  });

  it('never selects a synthetic row as fallback for a hidden canonical target', () => {
    const { state } = fixture();
    expect(resolveHistoryJumpEntryIndex(state.historyEntries, state.snapshot, 125)).toBe(1);
    expect(resolveHistoryJumpEntryIndex(state.historyEntries, state.snapshot, 126)).toBe(1);
    state.historyEntries = state.historyEntries.filter((entry) => entry.id === 'busy');
    expect(resolveHistoryJumpEntryIndex(state.historyEntries, state.snapshot, 125)).toBe(-1);
  });
});

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
