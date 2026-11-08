import { describe, expect, it } from 'vitest';
import { MAX_HISTORY_FILL_ITEMS, resolveUnderfilledHistoryWindowRequest, resolveViewportDrivenHistoryWindowCount } from './historyWindowSizing';

describe('bounded filling of sparse canonical windows', () => {
  it('does not collapse the canonical budget after observing huge hidden tools', () => {
    expect(resolveViewportDrivenHistoryWindowCount(
      { clientHeight: 680 } as HTMLDivElement, 30, 240, [20000, 30000, 100000],
    )).toBe(240);
  });

  it('expands Roots short end kernel while keeping the actual end and visible prose', () => {
    expect(resolveUnderfilledHistoryWindowRequest({
      viewportHeight: 680, renderedHeight: 267.97,
      windowStart: 433, windowEnd: 438, historyCount: 438,
    })).toEqual({ startIndex: 358, count: 80 });
    expect(resolveUnderfilledHistoryWindowRequest({
      viewportHeight: 680, renderedHeight: 1200,
      windowStart: 358, windowEnd: 438, historyCount: 438,
    })).toBeNull();
  });

  it('grows measured sparse pages progressively and stops at the archive start or fixed budget', () => {
    let start = 433;
    let count = 5;
    for (let page = 0; page < 4; page += 1) {
      const request = resolveUnderfilledHistoryWindowRequest({
        viewportHeight: 680, renderedHeight: 268,
        windowStart: start, windowEnd: 438, historyCount: 438,
      });
      expect(request).not.toBeNull();
      start = request!.startIndex;
      count = request!.count;
      expect(count).toBeLessThanOrEqual(MAX_HISTORY_FILL_ITEMS);
      expect(start + count).toBe(438);
    }
    expect(start).toBe(0);
    expect(resolveUnderfilledHistoryWindowRequest({
      viewportHeight: 680, renderedHeight: 268, windowStart: start, windowEnd: count, historyCount: 438,
    })).toBeNull();
    expect(resolveUnderfilledHistoryWindowRequest({
      viewportHeight: 680, renderedHeight: 0,
      windowStart: 100000 - MAX_HISTORY_FILL_ITEMS, windowEnd: 100000, historyCount: 100000,
    })).toBeNull();
  });

  it('preserves a browse corridor and ignores an unmeasurable hidden viewport', () => {
    const request = resolveUnderfilledHistoryWindowRequest({
      viewportHeight: 680, renderedHeight: 0, windowStart: 1000, windowEnd: 1080, historyCount: 100000,
    })!;
    expect(request.startIndex).toBeLessThanOrEqual(1000);
    expect(request.startIndex + request.count).toBeGreaterThanOrEqual(1080);
    expect(resolveUnderfilledHistoryWindowRequest({
      viewportHeight: 0, renderedHeight: 0, windowStart: 433, windowEnd: 438, historyCount: 438,
    })).toBeNull();
  });
});
