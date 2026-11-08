import { describe, expect, it, vi } from 'vitest';
import {
  measureHistoryNavigatorRange,
  resolveCanonicalHistoryCount,
  resolveHistoryNavigatorGeometry,
  resolveHistoryNavigatorPointerIndex,
  shouldRefreshHistoryNavigatorFromScroll,
} from './historyNavigator';
import {
  createAgentHistoryRender,
  resolveHistoryNavigatorTarget,
  resolveHistoryRetainedWindowDescriptor,
} from './historyRender';
import type { SessionAppServerControlViewState } from './types';

vi.mock('../i18n', () => ({ t: (key: string) => key }));
vi.mock('./historyDom', () => ({
  isProposedPlanActionDisabled: vi.fn(),
  resolveToolCallOutputLineLimit: vi.fn(),
}));
vi.mock('./activationHelpers', () => ({ resolveHistoryBadgeLabel: vi.fn() }));

describe('canonical scrollbar geometry', () => {
  it('sizes the thumb proportionally, with a small minimum bounded by the actual track', () => {
    const geometry = (visibleCount: number, trackHeightPx = 400) =>
      resolveHistoryNavigatorGeometry({
        trackHeightPx,
        historyCount: 100,
        visibleCount,
        anchorIndex: 0,
      });
    expect(geometry(25).thumbHeightPx).toBe(100);
    expect(geometry(50).thumbHeightPx).toBe(200);
    expect(geometry(0.1).thumbHeightPx).toBe(18);
    expect(geometry(100).thumbHeightPx).toBe(400);
    expect(geometry(0.1, 10).thumbHeightPx).toBe(10);
    expect(geometry(0.1, 0).travelPx).toBe(0);
  });

  it.each([0, 1, 499, 998, 999])('roundtrips canonical index %i at any grab offset', (anchorIndex) => {
    const geometry = resolveHistoryNavigatorGeometry({
      trackHeightPx: 400,
      historyCount: 1000,
      visibleCount: 100,
      anchorIndex,
    });
    for (const grabOffset of [0, geometry.thumbHeightPx / 2, geometry.thumbHeightPx]) {
      expect(
        resolveHistoryNavigatorPointerIndex(geometry, geometry.topPx + grabOffset, grabOffset),
      ).toBe(anchorIndex);
    }
    expect(resolveHistoryNavigatorPointerIndex(geometry, -100)).toBe(0);
    expect(resolveHistoryNavigatorPointerIndex(geometry, 500)).toBe(999);
  });

  it('does not seek when all history fits on the track', () => {
    const geometry = resolveHistoryNavigatorGeometry({
      trackHeightPx: 400,
      historyCount: 4,
      visibleCount: 4,
      anchorIndex: 2,
    });
    expect(resolveHistoryNavigatorPointerIndex(geometry, 400)).toBe(2);
  });

  it('measures only intersecting canonical rows, including fractions of tall rows', () => {
    expect(
      measureHistoryNavigatorRange(0, 100, [
        { absoluteIndex: 90, top: -300, bottom: -200 },
        { absoluteIndex: 100, top: -50, bottom: 150 },
        { absoluteIndex: 101, top: 150, bottom: 250 },
      ]),
    ).toEqual({ start: 100.25, end: 100.75 });
    expect(
      measureHistoryNavigatorRange(0, 100, [
        { absoluteIndex: 100, top: -50, bottom: 50 },
        { absoluteIndex: 104, top: 50, bottom: 150 },
      ]),
    ).toEqual({ start: 100.5, end: 104.5 });
    expect(measureHistoryNavigatorRange(0, 100, [])).toBeNull();
  });
});

describe('user movement versus programmatic scroll corrections', () => {
  const corrected = { scrollTop: 300, scrollHeight: 1000, clientHeight: 100 };
  const args = {
    now: 1100,
    lastUserIntentAt: 1000,
    intentWindowMs: 900,
    previous: corrected,
    current: corrected,
  };

  it('does not treat the correction scroll event as input even inside a recent intent window', () => {
    expect(shouldRefreshHistoryNavigatorFromScroll(args)).toBe(false);
    expect(
      shouldRefreshHistoryNavigatorFromScroll({
        ...args,
        current: { ...corrected, scrollHeight: 1200 },
      }),
    ).toBe(false);
  });

  it('accepts subsequent real movement immediately, including touch momentum after correction', () => {
    expect(
      shouldRefreshHistoryNavigatorFromScroll({
        ...args,
        current: { ...corrected, scrollTop: 301 },
      }),
    ).toBe(true);
    expect(
      shouldRefreshHistoryNavigatorFromScroll({
        ...args,
        now: 2000,
        current: { ...corrected, scrollTop: 301 },
      }),
    ).toBe(false);
  });
});

describe('navigator rendering across passive layout and hydration', () => {
  function fixture() {
    const attributes = new Map<string, string>();
    const rowRect = vi.fn(() => ({ top: -50, bottom: 150 }));
    const host = {
      clientHeight: 412,
      dataset: {},
      removeAttribute: vi.fn(),
      setAttribute: (key: string, value: string) => attributes.set(key, value),
      getBoundingClientRect: () => ({ top: 0, height: 412 }),
    };
    const thumb = { style: { top: '', height: '' } };
    const state = {
      snapshot: {
        historyCount: 1000,
        historyWindowStart: 100,
        historyWindowEnd: 110,
        history: [{ entryId: 'row', order: 101 }],
      },
      historyEntries: [{ id: 'row', order: 101 }],
      historyProgressNav: host,
      historyProgressThumb: thumb,
      historyViewport: {
        clientHeight: 100,
        scrollHeight: 1000,
        scrollTop: 50,
        getBoundingClientRect: () => ({ top: 0 }),
      },
      historyRenderedNodes: new Map([['row', { node: { getBoundingClientRect: rowRect } }]]),
      historyAutoScrollPinned: false,
      historyNavigatorMode: 'browse',
      historyNavigatorDragTargetIndex: null,
      historyNavigatorAnchorIndex: 500,
      historyNavigatorVisibleCount: 100,
    } as unknown as SessionAppServerControlViewState;
    const render = createAgentHistoryRender({ getState: () => state } as Parameters<
      typeof createAgentHistoryRender
    >[0]);
    return { state, render, rowRect, thumb, attributes };
  }

  it('redraws the stored anchor and span without remeasuring passive layout changes', () => {
    const { state, render, rowRect, thumb, attributes } = fixture();
    render.syncViewportOffset('session');
    const before = { ...thumb.style };
    rowRect.mockReturnValue({ top: -2000, bottom: 2000 });
    state.snapshot!.historyWindowStart = 90;
    state.snapshot!.historyWindowEnd = 150;
    render.syncViewportOffset('session');

    expect(state.historyNavigatorAnchorIndex).toBe(500);
    expect(attributes.get('aria-valuenow')).toBe('501');
    expect(thumb.style).toEqual(before);
    expect(rowRect).not.toHaveBeenCalled();
  });

  it('updates the measured span and anchor only on explicit viewport movement', () => {
    const { state, render, thumb } = fixture();
    render.syncViewportOffset('session', { refreshAnchorFromViewport: true });
    expect(state.historyNavigatorAnchorIndex).toBe(100);
    expect(state.historyNavigatorVisibleCount).toBe(0.5);
    expect(thumb.style.height).toBe('18px');
    const top = Number.parseFloat(thumb.style.top);
    expect(
      resolveHistoryNavigatorTarget({ state, clientY: top + 7, thumbDragOffsetPx: 7 })?.targetIndex,
    ).toBe(100);
  });

  it('keeps the drag target and geometry through preview hydration and clamps explicit edges', () => {
    const { state, render, thumb } = fixture();
    state.historyNavigatorMode = 'drag-preview';
    state.historyNavigatorDragTargetIndex = 750;
    render.syncViewportOffset('session', { refreshAnchorFromViewport: true });
    expect(state.historyNavigatorAnchorIndex).toBe(750);
    expect(state.historyNavigatorVisibleCount).toBe(100);
    expect(thumb.style.height).toBe('40px');
    state.historyNavigatorMode = 'browse';
    state.historyNavigatorDragTargetIndex = null;
    render.syncViewportOffset('session');
    expect(state.historyNavigatorAnchorIndex).toBe(750);
    state.snapshot!.historyWindowStart = 0;
    state.historyViewport!.scrollTop = 0;
    render.syncViewportOffset('session', { refreshAnchorFromViewport: true });
    expect(thumb.style.top).toBe('6px');
    state.historyAutoScrollPinned = true;
    render.syncViewportOffset('session');
    expect(state.historyNavigatorAnchorIndex).toBe(999);
  });

  it('keeps Root SourceClaude95f6235a two-entry history canonical despite a duration note', () => {
    // Root observed this after a real 4s turn: 2 canonical rows, 3 visual rows, aria max 3.
    const { state, render, thumb, attributes } = fixture();
    const snapshot = state.snapshot!;
    snapshot.historyCount = 2;
    snapshot.historyWindowStart = 0;
    snapshot.historyWindowEnd = 2;
    snapshot.history = [
      { ...snapshot.history[0]!, entryId: 'user', order: 1 },
      { ...snapshot.history[0]!, entryId: 'assistant', order: 2 },
    ];
    const base = state.historyEntries[0]!;
    state.historyEntries = [
      { ...base, id: 'user', order: 1 },
      { ...base, id: 'assistant', order: 2 },
      { ...base, id: 'turn-duration:turn', order: 2.01, turnDurationNote: true },
    ];
    const syntheticRect = vi.fn(() => ({ top: 70, bottom: 100 }));
    state.historyRenderedNodes = new Map([
      ['user', { node: { getBoundingClientRect: () => ({ top: 0, bottom: 30 }) } }],
      ['assistant', { node: { getBoundingClientRect: () => ({ top: 30, bottom: 70 }) } }],
      ['turn-duration:turn', { node: { getBoundingClientRect: syntheticRect } }],
    ]) as unknown as SessionAppServerControlViewState['historyRenderedNodes'];
    state.historyViewport!.scrollTop = 0;
    state.historyViewport!.scrollHeight = 100;
    state.historyNavigatorVisibleCount = null;
    render.syncViewportOffset('session', { refreshAnchorFromViewport: true });

    expect(attributes.get('aria-valuemax')).toBe('2');
    expect(attributes.get('aria-valuenow')).toBe('2');
    expect(state.historyNavigatorAnchorIndex).toBe(1);
    expect(state.historyNavigatorVisibleCount).toBe(2);
    expect(thumb.style.height).toBe('400px');
    expect(syntheticRect).not.toHaveBeenCalled();
    expect(resolveHistoryNavigatorTarget({ state, clientY: 412 })).toEqual({
      targetIndex: 1,
      atLiveEdge: true,
    });
    expect(resolveHistoryRetainedWindowDescriptor(state.historyEntries, state)).toEqual({
      windowStart: 0,
      windowEnd: 2,
      totalCount: 2,
    });
  });

  it.each([0, 1, 2])('keeps count %i authoritative with empty or entirely filtered visual rows', (count) => {
    const { state, render, attributes } = fixture();
    state.snapshot!.historyCount = count;
    state.snapshot!.historyWindowStart = 0;
    state.snapshot!.historyWindowEnd = count;
    state.historyEntries = [];
    render.syncViewportOffset('session');
    expect(attributes.get('aria-valuemax')).toBe(String(count));
    expect(resolveCanonicalHistoryCount(state.snapshot)).toBe(count);
    expect(resolveHistoryRetainedWindowDescriptor([], state)).toEqual({
      windowStart: 0,
      windowEnd: count,
      totalCount: count,
    });
    const target = resolveHistoryNavigatorTarget({ state, clientY: 412 });
    if (count === 0) {
      expect(target).toBeNull();
      expect(state.historyNavigatorAnchorIndex).toBeNull();
    } else {
      expect(target!.targetIndex).toBeLessThan(count);
    }
  });

  it('never expands canonical metadata from synthetic or filtered visual orders', () => {
    const { state, render, attributes } = fixture();
    state.snapshot!.historyCount = 2;
    state.snapshot!.historyWindowStart = 0;
    state.snapshot!.historyWindowEnd = 2;
    const entries = [{ ...state.historyEntries[0]!, id: 'synthetic', order: 999 }];
    expect(resolveHistoryRetainedWindowDescriptor(entries, state)).toEqual({
      windowStart: 0,
      windowEnd: 2,
      totalCount: 2,
    });
    expect(resolveHistoryRetainedWindowDescriptor(entries, undefined).totalCount).toBe(0);
    state.historyEntries = entries;
    state.snapshot!.historyCount = 0;
    state.snapshot!.historyWindowEnd = 0;
    render.syncViewportOffset('session');
    expect(attributes.get('aria-valuemax')).toBe('0');
    expect(resolveHistoryNavigatorTarget({ state, clientY: 412 })).toBeNull();
  });
});
