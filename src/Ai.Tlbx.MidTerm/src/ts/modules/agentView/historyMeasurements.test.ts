import { describe, expect, it } from 'vitest';
import { pruneHistoryMeasurementCache } from './historyMeasurements';
import type { AppServerControlHistoryEntry, SessionAppServerControlViewState } from './types';

function createState(): SessionAppServerControlViewState {
  return {
    historyMeasuredHeights: new Map([
      ['keep-1', 100],
      ['stale-1', 200],
    ]),
    historyObservedHeights: new Map([
      ['keep-2', 110],
      ['stale-2', 210],
    ]),
    historyMeasuredHeightsByBucket: new Map([
      [
        800,
        new Map([
          ['keep-1', 100],
          ['stale-1', 200],
        ]),
      ],
      [1200, new Map([['stale-bucket', 220]])],
    ]),
    historyObservedHeightsByBucket: new Map([
      [
        800,
        new Map([
          ['keep-2', 110],
          ['stale-2', 210],
        ]),
      ],
    ]),
    historyObservedHeightSamplesByBucket: new Map([
      [
        800,
        new Map([
          ['keep-1', [96, 100]],
          ['stale-1', [196, 200]],
        ]),
      ],
    ]),
  } as unknown as SessionAppServerControlViewState;
}

describe('pruneHistoryMeasurementCache', () => {
  it('keeps measurement state only for the active history window entries', () => {
    const state = createState();
    const entries = [{ id: 'keep-1' }, { id: 'keep-2' }] as AppServerControlHistoryEntry[];

    pruneHistoryMeasurementCache(state, entries);

    expect([...state.historyMeasuredHeights.keys()]).toEqual(['keep-1']);
    expect([...state.historyObservedHeights.keys()]).toEqual(['keep-2']);
    expect([...state.historyMeasuredHeightsByBucket.keys()]).toEqual([800]);
    expect([...(state.historyMeasuredHeightsByBucket.get(800)?.keys() ?? [])]).toEqual(['keep-1']);
    expect([...(state.historyObservedHeightsByBucket.get(800)?.keys() ?? [])]).toEqual(['keep-2']);
    expect([...(state.historyObservedHeightSamplesByBucket.get(800)?.keys() ?? [])]).toEqual([
      'keep-1',
    ]);
  });
});
