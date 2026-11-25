import { describe, expect, it } from 'vitest';
import { resolveHistoryWheelMotion } from './historyWheel';

describe('history wheel carry across kernel replacement', () => {
  it('keeps only the gesture that the new kernel cannot consume', () => {
    const first = resolveHistoryWheelMotion({
      scrollTop: 400,
      scrollHeight: 1000,
      clientHeight: 500,
      deltaY: 700,
      hasMore: true,
    });
    expect(first).toEqual({ scrollTop: 500, remainingDeltaPx: 600 });
    expect(
      resolveHistoryWheelMotion({
        scrollTop: 1000,
        scrollHeight: 3000,
        clientHeight: 500,
        deltaY: first.remainingDeltaPx,
        hasMore: true,
      }),
    ).toEqual({ scrollTop: 1600, remainingDeltaPx: 0 });
  });
  it('consumes upward motion in the new prefix coordinates before rows mount', () => {
    expect(
      resolveHistoryWheelMotion({
        scrollTop: 2500,
        scrollHeight: 5000,
        clientHeight: 700,
        deltaY: -1800,
        hasMore: true,
      }),
    ).toEqual({ scrollTop: 700, remainingDeltaPx: 0 });
  });
  it('ends a gesture at the canonical boundary and handles a short kernel', () => {
    expect(
      resolveHistoryWheelMotion({
        scrollTop: 0,
        scrollHeight: 200,
        clientHeight: 700,
        deltaY: -1800,
        hasMore: false,
      }),
    ).toEqual({ scrollTop: 0, remainingDeltaPx: 0 });
  });
  it('does not count automatic resize clamping as wheel consumption', () => {
    expect(
      resolveHistoryWheelMotion({
        scrollTop: 4000,
        scrollHeight: 1000,
        clientHeight: 500,
        deltaY: -100,
        hasMore: true,
      }),
    ).toEqual({ scrollTop: 400, remainingDeltaPx: 0 });
  });
});
