import { describe, expect, it } from 'vitest';

import { resolveHistoryNavigatorTarget } from './historyRender';

function createState() {
  const host = {
    clientHeight: 512,
    getBoundingClientRect: () => ({
      top: 100,
      bottom: 612,
      height: 512,
      left: 0,
      right: 12,
      width: 12,
      x: 0,
      y: 100,
      toJSON: () => ({}),
    }),
  };

  return {
    historyProgressNav: host,
    snapshot: {
      historyCount: 100,
    },
    historyEntries: [],
  } as any;
}

describe('resolveHistoryNavigatorTarget', () => {
  it('maps a thumb drag at the top of the track to the first history item', () => {
    const state = createState();

    const target = resolveHistoryNavigatorTarget({
      state,
      clientY: 126,
      thumbDragOffsetPx: 20,
    });

    expect(target?.targetIndex).toBe(0);
    expect(target?.atLiveEdge).toBe(false);
  });

  it('maps a thumb drag at the bottom of the track to the live edge', () => {
    const state = createState();

    const target = resolveHistoryNavigatorTarget({
      state,
      clientY: 586,
      thumbDragOffsetPx: 20,
    });

    expect(target?.targetIndex).toBe(99);
    expect(target?.atLiveEdge).toBe(true);
  });

  it('keeps track clicks mapped to the clicked track position', () => {
    const state = createState();

    expect(
      resolveHistoryNavigatorTarget({
        state,
        clientY: 106,
      })?.targetIndex,
    ).toBe(0);
  });
});
