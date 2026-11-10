import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  measureHistoryNavigatorRange,
  measureHistoryNavigatorAnchor,
  resolveCanonicalHistoryCount,
  resolveHistoryNavigatorGeometry,
  resolveHistoryNavigatorPointerIndex,
  shouldRefreshHistoryNavigatorFromScroll,
} from './historyNavigator';
import {
  createAgentHistoryRender,
  resolveHistoryNavigatorTarget,
  resolveHistoryRetainedWindowDescriptor,
  resolveMeasurementBrowseAnchorIndex,
  readHistoryNavigatorMapping,
  confirmHistoryReaderAnchor,
  prepareHistoryReaderResize,
  prepareHistoryViewportResize,
  recordHistoryReaderScroll,
  restorePendingHistoryAnchor,
  completeHistoryReaderMeasurements,
  syncHistoryMeasurementObserver,
} from './historyRender';
import type { SessionAppServerControlViewState } from './types';

vi.mock('../i18n', () => ({ t: (key: string) => key }));
vi.mock('./historyDom', () => ({
  isProposedPlanActionDisabled: vi.fn(),
  resolveToolCallOutputLineLimit: vi.fn(),
}));
vi.mock('./activationHelpers', () => ({ resolveHistoryBadgeLabel: vi.fn() }));

describe('confirmed reader anchors across post-reflow viewport notifications', () => {
  afterEach(() => vi.unstubAllGlobals());
  function readerFixture() {
    const viewport = {
      clientWidth: 1280,
      clientHeight: 671,
      scrollTop: 6904,
      scrollHeight: 8175,
      getBoundingClientRect: () => ({ top: 0, bottom: 671 }),
      scrollTo({ top }: { top: number }) {
        this.scrollTop = Math.min(top, this.scrollHeight - this.clientHeight);
      },
    };
    let rowTop = 6904 - 76.65625;
    let rowHeight = 900;
    const node = {
      isConnected: true,
      getBoundingClientRect: () => ({
        top: rowTop - viewport.scrollTop,
        bottom: rowTop - viewport.scrollTop + rowHeight,
      }),
    };
    const state = {
      historyViewport: viewport,
      historyViewportSize: { width: 1280, height: 671 },
      historyReaderAnchor: null,
      historyReaderResizeMeasurementPending: false,
      historyAutoScrollPinned: false,
      historyScrollMode: 'browse',
      historyPendingJumpTargetIndex: null,
      pendingHistoryPrependAnchor: null,
      pendingHistoryLayoutAnchor: null,
      historyEntries: [{ id: 'reader', order: 901 }],
      historyRenderedNodes: new Map([['reader', { node }]]),
      snapshot: { historyCount: 1345, historyWindowStart: 880, historyWindowEnd: 960 },
      historyNavigatorAnchorIndex: 901,
      historyLastUserScrollIntentAt: 0,
      historyLastUserScrollDirection: 0,
      panel: { querySelector: () => null },
    } as unknown as SessionAppServerControlViewState;
    const reflow = (width: number) => {
      viewport.clientWidth = width;
      viewport.scrollHeight = width === 1000 ? 8474 : 8175;
      rowTop = width === 1000 ? 6904 + 202.46875 : 6904 - 76.65625;
    };
    const restore = (
      key:
        'pendingHistoryPrependAnchor' | 'pendingHistoryLayoutAnchor' = 'pendingHistoryLayoutAnchor',
    ) => restorePendingHistoryAnchor(viewport as unknown as HTMLDivElement, state, key);
    confirmHistoryReaderAnchor(state);
    return {
      state,
      viewport,
      node,
      reflow,
      restore,
      offset: () => node.getBoundingClientRect().top,
      growRow: () => {
        rowHeight = 1800;
      },
      shiftRow: (delta: number) => {
        rowTop += delta;
      },
    };
  }

  it('uses the confirmed partial row when the actual viewport RO planner sees shifted DOM', () => {
    const { state, viewport, reflow, restore, offset } = readerFixture();
    expect(state.historyReaderAnchor?.absoluteIndex).toBe(900); // canonical order, not array position
    reflow(1000);
    expect(offset()).toBe(202.46875);
    expect(prepareHistoryViewportResize(state)).toBe(true);
    expect(state.pendingHistoryLayoutAnchor).toBeNull();
    expect(offset()).toBe(-76.65625); // Synchronous RO correction, before another frame.
    expect(
      resolveMeasurementBrowseAnchorIndex(state, viewport as unknown as HTMLDivElement),
    ).toBeNull();
    expect(restore()).toBe(false); // Already consumed; do not correct twice.
    expect(viewport.scrollTop).toBe(7183.125);
    expect(offset()).toBe(-76.65625);
    expect(state.historyReaderAnchor?.width).toBe(1000);
    expect(prepareHistoryViewportResize(state)).toBe(false);
  });

  it('refreshes the confirmed reader after restoration for repeated narrow/wide reflows', () => {
    const { state, viewport, node, reflow, restore, offset } = readerFixture();
    for (const width of [1000, 1280, 1000, 1280]) {
      reflow(width);
      expect(prepareHistoryViewportResize(state)).toBe(true);
      restore();
      expect(offset()).toBe(-76.65625);
      expect(state.historyReaderAnchor?.width).toBe(width);
      expect(state.historyRenderedNodes.get('reader')?.node).toBe(node);
    }
    expect(viewport.scrollTop).toBe(6904);
  });

  it('does not let an already queued scroll overwrite the pre-reflow reader', () => {
    const { state, reflow, restore, offset } = readerFixture();
    reflow(1000);
    expect(recordHistoryReaderScroll(state, false)).toBe(true);
    expect(state.historyReaderAnchor?.topOffsetPx).toBe(-76.65625);
    prepareHistoryViewportResize(state);
    restore();
    expect(offset()).toBe(-76.65625);
    recordHistoryReaderScroll(state, false);
    expect(state.pendingHistoryLayoutAnchor).toBeNull();
  });

  it.each(['before-ro', 'after-ro', 'new-wheel-after-ro'])(
    'preserves real scroll movement during resize/grace: %s',
    (phase) => {
      const { state, viewport, reflow, restore, offset } = readerFixture();
      state.historyLastUserScrollIntentAt = Date.now();
      state.historyLastUserScrollDirection = -1;
      reflow(1000);
      if (phase !== 'before-ro') prepareHistoryViewportResize(state);
      // A new wheel clears pending restores in the existing input handler;
      // momentum does not. Both must retain the actual -120px user movement.
      if (phase === 'new-wheel-after-ro') state.pendingHistoryLayoutAnchor = null;
      viewport.scrollTop -= 120;
      recordHistoryReaderScroll(state, true);
      prepareHistoryViewportResize(state);
      expect(state.historyReaderAnchor?.topOffsetPx).toBe(43.34375);
      restore();
      expect(viewport.scrollTop).toBe(7063.125);
      expect(offset()).toBe(43.34375);
      expect(state.historyLastUserScrollDirection).toBe(-1);
    },
  );

  it('gives prepend priority and never follows it with a stale resize restore', () => {
    const { state, reflow, restore, offset } = readerFixture();
    const prepend = { entryId: 'reader', absoluteIndex: 900, topOffsetPx: -40 };
    state.pendingHistoryPrependAnchor = prepend;
    reflow(1000);
    prepareHistoryViewportResize(state);
    expect(state.pendingHistoryPrependAnchor).toBe(prepend);
    expect(state.pendingHistoryLayoutAnchor).toBeNull();
    // A pre-existing layout restore also yields to the prepend restoration.
    state.pendingHistoryLayoutAnchor = { ...prepend, topOffsetPx: -76.65625 };
    restore('pendingHistoryPrependAnchor');
    expect(offset()).toBe(-40);
    expect(state.pendingHistoryLayoutAnchor).toBeNull();
    expect(state.historyReaderAnchor?.topOffsetPx).toBe(-40);
  });

  it('does not mistake browser clamping during reflow for recent user momentum', () => {
    const { state, viewport, reflow, restore, offset } = readerFixture();
    reflow(1000);
    prepareHistoryViewportResize(state);
    restore();
    viewport.scrollTop = 7700;
    recordHistoryReaderScroll(state, true);
    const expected = offset();
    reflow(1280);
    viewport.scrollTop = viewport.scrollHeight - viewport.clientHeight; // native clamp
    recordHistoryReaderScroll(state, true); // recent intent, but no actual new input
    prepareHistoryViewportResize(state);
    expect(state.historyReaderAnchor?.topOffsetPx).toBe(expected);
    restore();
    expect(offset()).toBe(expected);
  });

  it('keeps explicit jumps authoritative and follow-live pinned to the resized end', () => {
    const { state, viewport, reflow } = readerFixture();
    state.historyPendingJumpTargetIndex = 1200;
    reflow(1000);
    prepareHistoryViewportResize(state);
    expect(state.pendingHistoryLayoutAnchor).toBeNull();
    expect(state.historyPendingJumpTargetIndex).toBe(1200);
    const render = createAgentHistoryRender({ getState: () => state } as Parameters<
      typeof createAgentHistoryRender
    >[0]);
    render.scrollHistoryToBottom('session');
    expect(viewport.scrollHeight - viewport.clientHeight - viewport.scrollTop).toBe(0);
    expect(state.historyReaderAnchor).toBeNull();
    expect(state.historyAutoScrollPinned).toBe(true);
    reflow(1280);
    expect(prepareHistoryReaderResize(state)).toBe(false);
    render.scrollHistoryToBottom('session');
    expect(viewport.scrollHeight - viewport.clientHeight - viewport.scrollTop).toBe(0);
  });

  it('anchors a tall row by its top, and invents no reader when none was confirmed', () => {
    const { state, reflow, restore, growRow, offset } = readerFixture();
    reflow(1000);
    growRow();
    prepareHistoryViewportResize(state);
    restore();
    expect(offset()).toBe(-76.65625);
    state.historyReaderAnchor = null;
    reflow(1280);
    prepareHistoryViewportResize(state);
    expect(state.pendingHistoryLayoutAnchor).toBeNull();
  });

  it.each(['viewport-first', 'row-first'])(
    'restores before paint and never compensates old measurements twice: %s',
    (order) => {
      const { state, reflow, offset, shiftRow } = readerFixture();
      const before = {
        dataset: { appServerControlEntryId: 'before' },
        getBoundingClientRect: () => ({ top: -600, bottom: -400 }),
      };
      state.historyEntries.unshift({
        id: 'before',
        order: 900,
        estimatedHeightPx: 100,
      } as SessionAppServerControlViewState['historyEntries'][number]);
      let notify!: (records: ResizeObserverEntry[]) => void;
      class Observer {
        constructor(callback: ResizeObserverCallback) {
          notify = (records) => callback(records, this as unknown as ResizeObserver);
        }
        observe() {}
        disconnect() {}
        unobserve() {}
      }
      vi.stubGlobal('ResizeObserver', Observer);
      syncHistoryMeasurementObserver({
        sessionId: 'session',
        state,
        visibleEntries: [],
        getState: () => state,
        scheduleHistoryRender: vi.fn(),
      });
      const rowResize = (height: number) =>
        notify([
          {
            target: before,
            borderBoxSize: [{ blockSize: height, inlineSize: 1000 }],
          } as unknown as ResizeObserverEntry,
        ]);
      reflow(1000);
      if (order === 'row-first') rowResize(400);
      else prepareHistoryViewportResize(state);
      expect(offset()).toBe(-76.65625); // First observer has already fixed the pre-paint position.
      if (order === 'row-first') prepareHistoryViewportResize(state);
      else rowResize(400);
      expect(offset()).toBe(-76.65625);
      expect(state.historyReaderResizeMeasurementPending).toBe(true);
      expect(resolveMeasurementBrowseAnchorIndex(state, state.historyViewport!)).toBeNull();
      rowResize(400);
      expect(offset()).toBe(-76.65625);
      // New content can grow above the reader before the queued render too.
      // Keep the pixel anchor authoritative until the baseline is complete.
      shiftRow(50);
      rowResize(450);
      expect(offset()).toBe(-76.65625);
      // The real render finalizer releases the guard after all mounted rows were measured.
      completeHistoryReaderMeasurements(state);
      expect(state.historyReaderResizeMeasurementPending).toBe(false);
      shiftRow(30);
      rowResize(480); // A later real height change still compensates normally.
      expect(offset()).toBe(-76.65625);
    },
  );

  it('retains an unmounted anchor through window shift, then consumes it after remount', () => {
    const { state, reflow, restore, offset } = readerFixture();
    const rendered = state.historyRenderedNodes.get('reader')!;
    state.historyRenderedNodes.delete('reader');
    reflow(1000);
    prepareHistoryViewportResize(state);
    const pending = state.pendingHistoryLayoutAnchor;
    expect(pending?.topOffsetPx).toBe(-76.65625);
    expect(restore()).toBe(false);
    confirmHistoryReaderAnchor(state);
    expect(state.pendingHistoryLayoutAnchor).toBe(pending);
    expect(state.historyReaderAnchor?.width).toBe(1280);
    state.historyRenderedNodes.set('reader', rendered);
    expect(restore()).toBe(true);
    expect(state.pendingHistoryLayoutAnchor).toBeNull();
    expect(offset()).toBe(-76.65625);
  });

  it('consumes mounted anchors even when already aligned or physically clamped', () => {
    const { state, viewport, restore, offset } = readerFixture();
    state.pendingHistoryLayoutAnchor = {
      entryId: 'reader',
      absoluteIndex: 900,
      topOffsetPx: offset(),
    };
    expect(restore()).toBe(false);
    expect(state.pendingHistoryLayoutAnchor).toBeNull();
    viewport.scrollTop = viewport.scrollHeight - viewport.clientHeight;
    state.pendingHistoryLayoutAnchor = {
      entryId: 'reader',
      absoluteIndex: 900,
      topOffsetPx: -100000,
    };
    expect(restore()).toBe(false);
    expect(state.pendingHistoryLayoutAnchor).toBeNull();
  });
});

describe('canonical scrollbar geometry', () => {
  it('chooses a visible central row across sparse canonical gaps', () => {
    expect(
      measureHistoryNavigatorAnchor(40, 680, [
        { absoluteIndex: 122, top: 100, bottom: 200 },
        { absoluteIndex: 125, top: 261.984375, bottom: 497.765625 },
        { absoluteIndex: 128, top: 900, bottom: 1000 },
      ]),
    ).toBe(125);
    expect(
      measureHistoryNavigatorAnchor(0, 100, [
        { absoluteIndex: 122, top: 0, bottom: 30 },
        { absoluteIndex: 128, top: 60, bottom: 100 },
      ]),
    ).toBe(128); // The center lies in a gap; 125 is not a rendered row.
    expect(measureHistoryNavigatorAnchor(0, 100, [])).toBeNull();
  });
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

  it.each([0, 1, 499, 998, 999])(
    'roundtrips canonical index %i at any grab offset',
    (anchorIndex) => {
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
    },
  );

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
      getAttribute: (key: string) => attributes.get(key) ?? null,
      getBoundingClientRect: () => ({ top: 0, height: 412 }),
    };
    const thumb = {
      style: { top: '', height: '' },
      getBoundingClientRect: (): { height: number } => ({
        height: Number.parseFloat(thumb.style.height) || 0,
      }),
    };
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

  it('compensates resize against the stored reader, not the row that grew into view', () => {
    const { state } = fixture();
    state.pendingHistoryPrependAnchor = null;
    state.pendingHistoryLayoutAnchor = null;
    const viewport = state.historyViewport!;
    viewport.getBoundingClientRect = vi.fn(() => {
      throw new Error('Already-reflowed geometry is not the reader anchor');
    });
    expect(resolveMeasurementBrowseAnchorIndex(state, viewport)).toBe(500);
    state.historyNavigatorAnchorIndex = 5000;
    expect(resolveMeasurementBrowseAnchorIndex(state, viewport)).toBe(5000);
    state.pendingHistoryLayoutAnchor = { key: 'row', absoluteIndex: 100, offsetPx: 10 };
    expect(resolveMeasurementBrowseAnchorIndex(state, viewport)).toBeNull();
    state.pendingHistoryLayoutAnchor = null;
    state.historyAutoScrollPinned = true;
    expect(resolveMeasurementBrowseAnchorIndex(state, viewport)).toBeNull();
  });
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

  it('remeasures the visible fraction on viewport resize without moving the canonical anchor', () => {
    const { state, render, rowRect, attributes } = fixture();
    render.syncViewportOffset('session', { refreshVisibleRange: true });
    expect(state.historyNavigatorVisibleCount).toBe(0.5);
    expect(state.historyNavigatorAnchorIndex).toBe(500);
    expect(attributes.get('aria-valuenow')).toBe('501');
    state.historyViewport!.clientHeight = 200;
    rowRect.mockReturnValue({ top: 0, bottom: 200 });
    render.syncViewportOffset('session', { refreshVisibleRange: true });
    expect(state.historyNavigatorVisibleCount).toBe(1);
    expect(state.historyNavigatorAnchorIndex).toBe(500);
    state.historyViewport!.clientHeight = 50;
    render.syncViewportOffset('session', { refreshVisibleRange: true });
    expect(state.historyNavigatorVisibleCount).toBe(0.25);
    expect(state.historyNavigatorAnchorIndex).toBe(500);
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

  it('labels the actual centered canonical assistant and ignores a synthetic row at the center', () => {
    const { state, render, attributes } = fixture();
    const snapshot = state.snapshot!;
    snapshot.historyCount = 698;
    snapshot.historyWindowStart = 88;
    snapshot.historyWindowEnd = 159;
    const base = snapshot.history[0]!;
    snapshot.history = [
      { ...base, entryId: 'tool', order: 123 },
      { ...base, entryId: 'reasoning-124', order: 124 },
      { ...base, entryId: 'reasoning-125', order: 125 },
      { ...base, entryId: 'assistant', order: 126 },
      { ...base, entryId: 'tool-127', order: 127 },
      { ...base, entryId: 'reasoning-128', order: 128 },
      { ...base, entryId: 'tool-129', order: 129 },
    ];
    state.historyViewport!.clientHeight = 680;
    state.historyViewport!.scrollHeight = 3802;
    state.historyViewport!.scrollTop = 805;
    state.historyViewport!.getBoundingClientRect = () => ({ top: 40 }) as DOMRect;
    state.historyRenderedNodes = new Map([
      ['tool', { node: { getBoundingClientRect: () => ({ top: 100, bottom: 200 }) } }],
      [
        'assistant',
        { node: { getBoundingClientRect: () => ({ top: 261.984375, bottom: 497.765625 }) } },
      ],
      ['busy', { node: { getBoundingClientRect: () => ({ top: 300, bottom: 450 }) } }],
    ]) as unknown as SessionAppServerControlViewState['historyRenderedNodes'];
    render.syncViewportOffset('session', { refreshAnchorFromViewport: true });
    expect(state.historyNavigatorAnchorIndex).toBe(125);
    expect(attributes.get('aria-valuenow')).toBe('126');
  });

  it('maps Roots painted geometry consistently while hidden rendering lags a growing count', () => {
    const { state, render, attributes } = fixture();
    attributes.set('aria-valuemax', '698');
    state.snapshot!.historyCount = 704; // New model state; the rail still displays the earlier count.
    state.historyProgressNav!.clientHeight = 680;
    state.historyProgressNav!.getBoundingClientRect = () => ({ top: 40 }) as DOMRect;
    state.historyProgressThumb!.getBoundingClientRect = () => ({ height: 36.359375 }) as DOMRect;
    state.historyNavigatorDragMapping = readHistoryNavigatorMapping(state);
    const args = { state, clientY: 177.45813531205164, thumbDragOffsetPx: 36.359375 / 2 };
    expect(resolveHistoryNavigatorTarget(args)?.targetIndex).toBe(125);
    expect(resolveHistoryNavigatorTarget({ ...args, clientY: 175.6328125 })?.targetIndex).toBe(123);
    state.historyNavigatorMode = 'drag-preview';
    state.historyNavigatorDragTargetIndex = 125;
    state.snapshot!.historyCount = 1000;
    render.syncViewportOffset('session');
    expect(attributes.get('aria-valuemax')).toBe('698');
    expect(resolveHistoryNavigatorTarget(args)?.targetIndex).toBe(125);
    state.historyNavigatorDragMapping = null;
    state.historyNavigatorMode = 'browse';
    state.historyNavigatorDragTargetIndex = null;
    render.syncViewportOffset('session');
    expect(attributes.get('aria-valuemax')).toBe('1000');
  });

  it('requests older rows for the measured quiet end instead of counting hidden tools or the busy indicator', () => {
    const { state, render } = fixture();
    const snapshot = state.snapshot!;
    const base = snapshot.history[0]!;
    snapshot.historyCount = 438;
    snapshot.historyWindowStart = 433;
    snapshot.historyWindowEnd = 438;
    snapshot.history = [
      { ...base, entryId: 'user', order: 434 },
      { ...base, entryId: 'hidden-tool-435', order: 435 },
      { ...base, entryId: 'hidden-reasoning-436', order: 436 },
      { ...base, entryId: 'hidden-tool-437', order: 437 },
      { ...base, entryId: 'assistant', order: 438 },
    ];
    state.historyViewport!.clientHeight = 680;
    state.historyRenderedNodes = new Map([
      ['user', { node: { getBoundingClientRect: () => ({ top: 50, bottom: 230 }) } }],
      ['assistant', { node: { getBoundingClientRect: () => ({ top: 230, bottom: 317.97 }) } }],
      ['busy', { node: { getBoundingClientRect: () => ({ top: 0, bottom: 100000 }) } }],
    ]) as unknown as SessionAppServerControlViewState['historyRenderedNodes'];
    expect(render.getUnderfilledHistoryWindowRequest(state)).toEqual({
      startIndex: 358,
      count: 80,
    });
    state.historyNavigatorMode = 'drag-preview';
    expect(render.getUnderfilledHistoryWindowRequest(state)).toBeNull();
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

  it.each([0, 1, 2])(
    'keeps count %i authoritative with empty or entirely filtered visual rows',
    (count) => {
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
    },
  );

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
