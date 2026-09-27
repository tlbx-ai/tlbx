import { describe, expect, it } from 'vitest';

import {
  activateVirtualizerMeasurementBucket,
  captureViewportAnchor,
  computeVirtualWindow,
  resolveKernelWindowRequest,
  resolveResizeObserverBorderBoxSize,
  resolveScrollCompensationDelta,
  restoreViewportAnchor,
} from './virtualizer';

describe('virtualizer', () => {
  it('bounds retained measurement history to the three most recent width buckets', () => {
    const state = {
      measuredSizes: new Map(),
      observedSizes: new Map(),
      measuredSizesByBucket: new Map(),
      observedSizesByBucket: new Map(),
      observedSizeSamplesByBucket: new Map(),
      measuredWidthBucket: 0,
      lastWindowKey: null,
    };

    for (const width of [400, 800, 1200, 1600]) {
      activateVirtualizerMeasurementBucket(state, width);
    }

    expect([...state.measuredSizesByBucket.keys()]).toEqual([800, 1200, 1600]);
    expect([...state.observedSizesByBucket.keys()]).toEqual([800, 1200, 1600]);
    expect([...state.observedSizeSamplesByBucket.keys()]).toEqual([800, 1200, 1600]);
  });

  it('uses the ResizeObserver border box instead of the smaller content box', () => {
    const record = {
      borderBoxSize: [{ blockSize: 132, inlineSize: 800 }],
      contentRect: { height: 116 },
      target: {
        getBoundingClientRect: () => ({ height: 132 }),
      },
    } as unknown as ResizeObserverEntry;

    expect(resolveResizeObserverBorderBoxSize(record)).toBe(132);
  });

  it('restores a concrete visible row after spacer geometry changes', () => {
    const viewport = {
      scrollTop: 1000,
      scrollHeight: 5000,
      clientHeight: 600,
      getBoundingClientRect: () => ({ top: 100, bottom: 700 }),
    } as HTMLDivElement;
    let rowTop = 124;
    const row = {
      getBoundingClientRect: () => ({ top: rowTop, bottom: rowTop + 200 }),
    } as HTMLElement;
    const anchor = captureViewportAnchor({
      viewport,
      renderedNodes: [{ key: 'row-42', node: row, absoluteIndex: 42 }],
    });

    rowTop = 284;
    expect(anchor).not.toBeNull();
    expect(restoreViewportAnchor({ viewport, anchor: anchor!, resolveNode: () => row })).toBe(true);
    expect(viewport.scrollTop).toBe(1160);
  });

  it('keeps a 10k item history render window bounded and spacer-backed', () => {
    const items = Array.from({ length: 10000 }, (_, index) => index);

    const window = computeVirtualWindow({
      items,
      scrollTop: 420000,
      clientHeight: 900,
      overscanItems: 12,
      resolveItemSize: (_item, index) => 72 + (index % 7) * 18,
    });

    expect(window.start).toBeGreaterThan(0);
    expect(window.end).toBeLessThan(items.length);
    expect(window.end - window.start).toBeLessThanOrEqual(40);
    expect(window.topSpacerPx).toBeGreaterThan(350000);
    expect(window.bottomSpacerPx).toBeGreaterThan(400000);
  });

  it('computes scroll compensation from size changes above the current browse anchor', () => {
    const delta = resolveScrollCompensationDelta({
      anchorAbsoluteIndex: 25,
      changes: [
        { absoluteIndex: 10, previousSize: 100, nextSize: 132 },
        { absoluteIndex: 18, previousSize: 80, nextSize: 70 },
        { absoluteIndex: 30, previousSize: 90, nextSize: 140 },
      ],
    });

    expect(delta).toBe(22);
  });

  it('shifts an overlapping kernel backward without inventing off-window pixel space', () => {
    const request = resolveKernelWindowRequest({
      items: Array.from({ length: 60 }, (_, index) => index),
      viewportMetrics: { scrollTop: 0, clientHeight: 500, clientWidth: 900 },
      retainedWindow: { windowStart: 200, windowEnd: 260, totalCount: 1000 },
      fetchAheadItems: 20,
      resolveItemSize: () => 100,
      anchorAbsoluteIndex: 200,
    });

    expect(request).toEqual({ startIndex: 174, count: 60 });
    expect(request!.startIndex).toBeLessThanOrEqual(200);
    expect(request!.startIndex + request!.count).toBeGreaterThan(200);
  });

  it('shifts the same bounded kernel forward and eventually reaches the live tail', () => {
    const request = resolveKernelWindowRequest({
      items: Array.from({ length: 60 }, (_, index) => index),
      viewportMetrics: { scrollTop: 5500, clientHeight: 500, clientWidth: 900 },
      retainedWindow: { windowStart: 200, windowEnd: 260, totalCount: 300 },
      fetchAheadItems: 20,
      resolveItemSize: () => 100,
      anchorAbsoluteIndex: 255,
    });

    expect(request).toEqual({ startIndex: 228, count: 60 });

    const tailRequest = resolveKernelWindowRequest({
      items: Array.from({ length: 60 }, (_, index) => index),
      viewportMetrics: { scrollTop: 5500, clientHeight: 500, clientWidth: 900 },
      retainedWindow: { windowStart: 228, windowEnd: 288, totalCount: 300 },
      fetchAheadItems: 20,
      resolveItemSize: () => 100,
      anchorAbsoluteIndex: 283,
    });
    expect(tailRequest).toEqual({ startIndex: 240, count: 60 });
    expect(tailRequest!.startIndex + tailRequest!.count).toBe(300);
  });

  it('uses canonical indexes for sparse visual items when choosing a forward window', () => {
    const items = Array.from({ length: 20 }, (_, index) => ({
      visualIndex: index,
      canonicalIndex: 100 + index * 3,
    }));
    const request = resolveKernelWindowRequest({
      items,
      viewportMetrics: { scrollTop: 1500, clientHeight: 500, clientWidth: 900 },
      retainedWindow: { windowStart: 100, windowEnd: 160, totalCount: 220 },
      fetchAheadItems: 20,
      resolveItemSize: () => 100,
      resolveAbsoluteIndex: (item) => item.canonicalIndex,
      anchorAbsoluteIndex: 157,
    });

    expect(request).not.toBeNull();
    expect(request!.startIndex).toBeGreaterThan(100);
    expect(request!.startIndex + request!.count).toBeGreaterThan(157);
  });

  it('moves forward from a physical kernel edge even when later canonical items are filtered out', () => {
    const request = resolveKernelWindowRequest({
      items: Array.from({ length: 20 }, (_, index) => ({ canonicalIndex: 100 + index })),
      viewportMetrics: { scrollTop: 1500, clientHeight: 500, clientWidth: 900 },
      retainedWindow: { windowStart: 80, windowEnd: 160, totalCount: 240 },
      fetchAheadItems: 20,
      resolveItemSize: () => 100,
      resolveAbsoluteIndex: (item) => item.canonicalIndex,
      edgeDirection: 'later',
      anchorAbsoluteIndex: 119,
    });

    expect(request).not.toBeNull();
    expect(request!.startIndex).toBeGreaterThan(80);
    expect(request!.startIndex).toBeLessThanOrEqual(119);
    expect(request!.startIndex + request!.count).toBeGreaterThan(119);
  });

  it('does not reverse a backward browse into the newer kernel margin after anchor restore', () => {
    const request = resolveKernelWindowRequest({
      items: Array.from({ length: 60 }, (_, index) => index),
      viewportMetrics: { scrollTop: 5500, clientHeight: 500, clientWidth: 900 },
      retainedWindow: { windowStart: 160, windowEnd: 220, totalCount: 300 },
      fetchAheadItems: 20,
      resolveItemSize: () => 100,
      navigationDirection: 'earlier',
      anchorAbsoluteIndex: 215,
    });

    expect(request).toBeNull();
  });

  it('shifts backward when sparse retained rows look near both local edges', () => {
    const request = resolveKernelWindowRequest({
      items: Array.from({ length: 12 }, (_, index) => ({ canonicalIndex: 900 + index })),
      viewportMetrics: { scrollTop: 700, clientHeight: 500, clientWidth: 900 },
      retainedWindow: { windowStart: 900, windowEnd: 996, totalCount: 1512 },
      fetchAheadItems: 20,
      resolveItemSize: () => 100,
      resolveAbsoluteIndex: (item) => item.canonicalIndex,
      edgeDirection: 'later',
      navigationDirection: 'earlier',
      anchorAbsoluteIndex: 909,
    });

    expect(request).not.toBeNull();
    expect(request!.startIndex).toBeLessThan(900);
    expect(request!.startIndex + request!.count).toBeGreaterThan(909);
  });

  it('does not refetch at a canonical bound when the loaded kernel already covers it', () => {
    expect(
      resolveKernelWindowRequest({
        items: Array.from({ length: 60 }, (_, index) => index),
        viewportMetrics: { scrollTop: 0, clientHeight: 500, clientWidth: 900 },
        retainedWindow: { windowStart: 0, windowEnd: 60, totalCount: 300 },
        fetchAheadItems: 20,
        resolveItemSize: () => 100,
      }),
    ).toBeNull();
  });
});
