import { computeHistoryVirtualWindow, resolveHistoryRenderScrollTop } from './historyViewport';
import type { AppServerControlHistoryEntry } from './types';
import { describe, expect, it } from 'vitest';
import {
  MAX_HISTORY_FILL_ITEMS,
  resolveUnderfilledHistoryWindowRequest,
  resolveViewportDrivenHistoryWindowCount,
} from './historyWindowSizing';

describe('bounded filling of sparse canonical windows', () => {
  it('does not collapse the canonical budget after observing huge hidden tools', () => {
    expect(
      resolveViewportDrivenHistoryWindowCount(
        { clientHeight: 680 } as HTMLDivElement,
        30,
        240,
        [20000, 30000, 100000],
      ),
    ).toBe(240);
  });

  it('expands Roots short end kernel while keeping the actual end and visible prose', () => {
    expect(
      resolveUnderfilledHistoryWindowRequest({
        viewportHeight: 680,
        renderedHeight: 267.97,
        windowStart: 433,
        windowEnd: 438,
        historyCount: 438,
      }),
    ).toEqual({ startIndex: 358, count: 80 });
    expect(
      resolveUnderfilledHistoryWindowRequest({
        viewportHeight: 680,
        renderedHeight: 1200,
        windowStart: 358,
        windowEnd: 438,
        historyCount: 438,
      }),
    ).toBeNull();
  });

  it('grows measured sparse pages progressively and stops at the archive start or fixed budget', () => {
    let start = 433;
    let count = 5;
    for (let page = 0; page < 4; page += 1) {
      const request = resolveUnderfilledHistoryWindowRequest({
        viewportHeight: 680,
        renderedHeight: 268,
        windowStart: start,
        windowEnd: 438,
        historyCount: 438,
      });
      expect(request).not.toBeNull();
      start = request!.startIndex;
      count = request!.count;
      expect(count).toBeLessThanOrEqual(MAX_HISTORY_FILL_ITEMS);
      expect(start + count).toBe(438);
    }
    expect(start).toBe(0);
    expect(
      resolveUnderfilledHistoryWindowRequest({
        viewportHeight: 680,
        renderedHeight: 268,
        windowStart: start,
        windowEnd: count,
        historyCount: 438,
      }),
    ).toBeNull();
    expect(
      resolveUnderfilledHistoryWindowRequest({
        viewportHeight: 680,
        renderedHeight: 0,
        windowStart: 100000 - MAX_HISTORY_FILL_ITEMS,
        windowEnd: 100000,
        historyCount: 100000,
      }),
    ).toBeNull();
  });

  it('preserves a browse corridor and ignores an unmeasurable hidden viewport', () => {
    const request = resolveUnderfilledHistoryWindowRequest({
      viewportHeight: 680,
      renderedHeight: 0,
      windowStart: 1000,
      windowEnd: 1080,
      historyCount: 100000,
    })!;
    expect(request.startIndex).toBeLessThanOrEqual(1000);
    expect(request.startIndex + request.count).toBeGreaterThanOrEqual(1080);
    expect(
      resolveUnderfilledHistoryWindowRequest({
        viewportHeight: 0,
        renderedHeight: 0,
        windowStart: 433,
        windowEnd: 438,
        historyCount: 438,
      }),
    ).toBeNull();
  });
});

describe('pixel-bounded rich history rendering', () => {
  it('virtualizes a short canonical window when its rich rows span many screens', () => {
    const rows = Array.from({ length: 38 }, (_, index) => ({
      id: String(index),
      order: index + 1,
    })) as AppServerControlHistoryEntry[];
    const window = computeHistoryVirtualWindow(rows, 10000, 800, 1200, () => 1000);
    expect(window.start).toBeLessThanOrEqual(10);
    expect(window.end).toBeGreaterThan(10);
    expect(window.end - window.start).toBeLessThanOrEqual(6);
    expect(window.topSpacerPx + (window.end - window.start) * 1000 + window.bottomSpacerPx).toBe(
      38000,
    );
  });
  it('keeps small fitting histories intact and covers short rows on narrow screens', () => {
    const rows = Array.from({ length: 5 }, (_, index) => ({
      id: String(index),
    })) as AppServerControlHistoryEntry[];
    expect(computeHistoryVirtualWindow(rows, 0, 800, 390, () => 40)).toEqual({
      start: 0,
      end: 5,
      topSpacerPx: 0,
      bottomSpacerPx: 0,
    });
    const many = Array.from({ length: 200 }, (_, index) => ({
      id: String(index),
    })) as AppServerControlHistoryEntry[];
    const window = computeHistoryVirtualWindow(many, 400, 800, 390, () => 20);
    expect(window.start * 20).toBeLessThanOrEqual(400);
    expect(window.end * 20).toBeGreaterThanOrEqual(1200);
  });
});

describe('kernel pixel origins', () => {
  it('selects the new virtual window around the stable reader before mounting rows', () => {
    const rows = Array.from({ length: 160 }, (_, i) => ({
      id: String(i),
    })) as AppServerControlHistoryEntry[];
    const top = resolveHistoryRenderScrollTop(
      rows,
      1200,
      { entryId: '120', topOffsetPx: -80 },
      () => 500,
      18,
    );
    expect(top).toBe(60098);
    const window = computeHistoryVirtualWindow(rows, top, 800, 1200, () => 500);
    expect(window.start).toBeLessThanOrEqual(120);
    expect(window.end).toBeGreaterThan(120);
    expect(window.end - window.start).toBeLessThanOrEqual(10);
    expect(resolveHistoryRenderScrollTop(rows, 1200, null, () => 500)).toBe(1200);
    expect(
      resolveHistoryRenderScrollTop(rows, 1200, { entryId: 'absent', topOffsetPx: 0 }, () => 500),
    ).toBe(1200);
  });
});
