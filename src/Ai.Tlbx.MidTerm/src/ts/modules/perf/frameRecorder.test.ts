import { describe, expect, it } from 'vitest';

import { calculatePerfFrameStats } from './frameRecorder';

describe('perf frame recorder', () => {
  it('returns empty stats without frames', () => {
    expect(calculatePerfFrameStats([])).toEqual({
      count: 0,
      p50Ms: null,
      p95Ms: null,
      p99Ms: null,
      maxMs: null,
      over50Ms: 0,
      over100Ms: 0,
      over250Ms: 0,
      effectiveFpsP95: null,
    });
  });

  it('summarizes frame pacing and visible jank thresholds', () => {
    expect(calculatePerfFrameStats([16, 17, 33, 100, 300])).toEqual({
      count: 5,
      p50Ms: 33,
      p95Ms: 300,
      p99Ms: 300,
      maxMs: 300,
      over50Ms: 2,
      over100Ms: 1,
      over250Ms: 1,
      effectiveFpsP95: 3.3,
    });
  });
});
