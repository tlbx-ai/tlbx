import { afterEach, describe, expect, it, vi } from 'vitest';

const updateAppServerControlHistoryStreamWindow = vi.fn();

vi.mock('../../api/client', () => ({
  updateAppServerControlHistoryStreamWindow,
}));

describe('historyWindowState', () => {
  afterEach(() => {
    updateAppServerControlHistoryStreamWindow.mockReset();
  });

  it('ignores fetched history windows older than the current live sequence', async () => {
    const { applyFetchedAppServerControlHistoryWindow } = await import('./historyWindowState');

    const state = {
      snapshot: {
        latestSequence: 12,
        historyWindowStart: 0,
        historyWindowEnd: 2,
        history: [{ entryId: 'assistant:newer', order: 1, body: 'newer' }],
      },
      historyWindowStart: 0,
      historyWindowCount: 2,
      disconnectStream: vi.fn(),
    } as any;

    const applied = applyFetchedAppServerControlHistoryWindow('session-1', state, {
      latestSequence: 11,
      historyWindowStart: 0,
      historyWindowEnd: 1,
      history: [{ entryId: 'assistant:older', order: 1, body: 'older' }],
    } as any);

    expect(applied).toBe(false);
    expect(state.snapshot.latestSequence).toBe(12);
    expect(state.snapshot.history[0]?.entryId).toBe('assistant:newer');
    expect(updateAppServerControlHistoryStreamWindow).not.toHaveBeenCalled();
  });

  it('ignores fetched history windows that do not match the current browser revision', async () => {
    const { applyFetchedAppServerControlHistoryWindow } = await import('./historyWindowState');

    const state = {
      snapshot: {
        latestSequence: 12,
        historyWindowStart: 5,
        historyWindowEnd: 7,
        history: [{ entryId: 'assistant:kept', order: 1, body: 'kept' }],
      },
      historyWindowStart: 5,
      historyWindowCount: 2,
      historyWindowRevision: 'rev-current',
      disconnectStream: vi.fn(),
    } as any;

    const applied = applyFetchedAppServerControlHistoryWindow('session-1', state, {
      latestSequence: 12,
      windowRevision: 'rev-stale',
      historyWindowStart: 0,
      historyWindowEnd: 2,
      history: [{ entryId: 'assistant:stale', order: 1, body: 'stale' }],
    } as any);

    expect(applied).toBe(false);
    expect(state.snapshot.historyWindowStart).toBe(5);
    expect(state.snapshot.history[0]?.entryId).toBe('assistant:kept');
    expect(updateAppServerControlHistoryStreamWindow).not.toHaveBeenCalled();
  });

  it('keeps a nonzero stream window when a metadata-only snapshot reports retained history', async () => {
    const { applyFetchedAppServerControlHistoryWindow } = await import('./historyWindowState');

    const state = {
      snapshot: null,
      historyWindowStart: 0,
      historyWindowCount: 80,
      historyWindowTargetCount: 120,
      historyWindowRevision: null,
      historyWindowViewportWidth: 1200,
      disconnectStream: vi.fn(),
    } as any;

    const applied = applyFetchedAppServerControlHistoryWindow('session-1', state, {
      latestSequence: 40,
      historyCount: 240,
      historyWindowStart: 240,
      historyWindowEnd: 240,
      history: [],
    } as any);

    expect(applied).toBe(true);
    expect(state.historyWindowCount).toBe(120);
    expect(updateAppServerControlHistoryStreamWindow).toHaveBeenCalledWith(
      'session-1',
      240,
      120,
      undefined,
      1200,
    );
  });
});
