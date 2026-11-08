import { describe, expect, it } from 'vitest';
import type {
  AppServerControlHistoryDelta,
  AppServerControlHistoryItem,
  AppServerControlHistorySnapshot,
} from '../../api/types';
import { applyCanonicalAppServerControlDelta } from './snapshotState';
import type { SessionAppServerControlViewState } from './types';

const earlier = '2026-11-08T00:00:00Z';
const later = '2026-11-08T00:01:00Z';

function item(
  itemId: string,
  updatedAt = earlier,
): AppServerControlHistorySnapshot['items'][number] {
  return { itemId, itemType: 'message', status: 'completed', attachments: [], updatedAt };
}

function request(
  requestId: string,
  updatedAt = earlier,
): AppServerControlHistorySnapshot['requests'][number] {
  return {
    requestId,
    kind: 'question',
    kindLabel: 'Question',
    state: 'pending',
    questions: [],
    answers: [],
    updatedAt,
  };
}

function notice(
  eventId: string,
  createdAt = earlier,
): AppServerControlHistorySnapshot['notices'][number] {
  return { eventId, type: 'info', message: eventId, createdAt };
}

function historyEntry(body: string): AppServerControlHistoryItem {
  return {
    entryId: 'assistant',
    order: 1,
    kind: 'assistant',
    status: 'streaming',
    body,
    attachments: [],
    streaming: true,
    createdAt: earlier,
    updatedAt: earlier,
  };
}

function createState(): SessionAppServerControlViewState {
  const snapshot: AppServerControlHistorySnapshot = {
    sessionId: 'session',
    provider: 'codex',
    generatedAt: earlier,
    latestSequence: 1,
    historyCount: 1,
    historyWindowStart: 0,
    historyWindowEnd: 1,
    hasOlderHistory: false,
    hasNewerHistory: false,
    session: { state: 'running', stateLabel: 'Running' },
    thread: { threadId: 'thread', state: 'ready', stateLabel: 'Ready' },
    currentTurn: { turnId: 'turn', state: 'running', stateLabel: 'Running' },
    quickSettings: { planMode: 'off', permissionMode: 'manual' },
    streams: {
      assistantText: '',
      reasoningText: '',
      reasoningSummaryText: '',
      planText: '',
      commandOutput: '',
      fileChangeOutput: '',
      unifiedDiff: '',
    },
    history: [historyEntry('Hello')],
    items: [],
    requests: [],
    notices: [],
  };
  // The reducer uses only snapshot/window state; no DOM or view runtime is needed.
  return {
    snapshot,
    historyAutoScrollPinned: true,
    historyWindowStart: 0,
    historyWindowCount: 1,
    historyWindowTargetCount: 10,
  } as SessionAppServerControlViewState;
}

function createDelta(snapshot: AppServerControlHistorySnapshot): AppServerControlHistoryDelta {
  return {
    sessionId: snapshot.sessionId,
    provider: snapshot.provider,
    generatedAt: later,
    latestSequence: 2,
    historyCount: snapshot.historyCount,
    session: snapshot.session,
    thread: snapshot.thread,
    currentTurn: snapshot.currentTurn,
    quickSettings: snapshot.quickSettings,
    streams: snapshot.streams,
    historyUpserts: [],
    historyRemovals: [],
    itemUpserts: [],
    itemRemovals: [],
    requestUpserts: [],
    requestRemovals: [],
    noticeUpserts: [],
  };
}

function setRetainedReferences(
  state: SessionAppServerControlViewState,
  itemIds: readonly string[],
  requestIds: readonly string[] = itemIds,
): void {
  const snapshot = state.snapshot!;
  const count = Math.max(itemIds.length, requestIds.length);
  snapshot.history = Array.from({ length: count }, (_, i) => ({
    ...historyEntry('Retained canonical row'),
    entryId: `entry-${i}`,
    order: i + 1,
    itemId: itemIds[i] ?? null,
    requestId: requestIds[i] ?? null,
  }));
  snapshot.historyCount = count;
  snapshot.historyWindowEnd = count;
  state.historyWindowCount = count;
  state.historyWindowTargetCount = Math.max(1, count);
}

describe('canonical history patch summary arrays', () => {
  it.each([0, 10_000])('retains array identity for empty summary deltas with %i entries', (count) => {
    const state = createState();
    const snapshot = state.snapshot!;
    const items = (snapshot.items = Array.from({ length: count }, (_, i) => item(`item-${i}`)));
    const requests = (snapshot.requests = Array.from({ length: count }, (_, i) => request(`req-${i}`)));
    const notices = (snapshot.notices = Array.from({ length: count }, (_, i) => notice(`notice-${i}`)));
    setRetainedReferences(state, items.map((value) => value.itemId), requests.map((value) => value.requestId));
    Object.freeze(items);
    Object.freeze(requests);
    Object.freeze(notices);

    applyCanonicalAppServerControlDelta(state, createDelta(snapshot));

    expect(snapshot.items).toBe(items);
    expect(snapshot.requests).toBe(requests);
    expect(snapshot.notices).toBe(notices);
    expect(snapshot.latestSequence).toBe(2);
  });

  it('still applies streamed text, history and turn changes when summary deltas are empty', () => {
    const state = createState();
    const snapshot = state.snapshot!;
    const previousEntry = snapshot.history[0]!;
    const delta = createDelta(snapshot);
    delta.streams = { ...snapshot.streams, assistantText: 'Hello world' };
    delta.currentTurn = { ...snapshot.currentTurn, state: 'completed', stateLabel: 'Completed' };
    delta.historyUpserts = [{ ...previousEntry, body: 'Hello world', streaming: false }];

    applyCanonicalAppServerControlDelta(state, delta);

    expect(snapshot.streams.assistantText).toBe('Hello world');
    expect(snapshot.currentTurn.state).toBe('completed');
    expect(snapshot.history[0]!.body).toBe('Hello world');
    expect(snapshot.history[0]!.streaming).toBe(false);
    expect(previousEntry.body).toBe('Hello');
    expect(delta.historyUpserts[0]!.body).toBe('Hello world');
  });

  it('applies removals without upserts and leaves the previous arrays untouched', () => {
    const state = createState();
    const snapshot = state.snapshot!;
    const items = (snapshot.items = [item('keep'), item('remove', later)]);
    const requests = (snapshot.requests = [request('keep'), request('remove', later)]);
    setRetainedReferences(state, ['keep', 'remove']);
    Object.freeze(items);
    Object.freeze(requests);
    const delta = createDelta(snapshot);
    delta.itemRemovals = ['remove', 'unknown'];
    delta.requestRemovals = ['remove', 'unknown'];

    applyCanonicalAppServerControlDelta(state, delta);

    expect(snapshot.items.map((value) => value.itemId)).toEqual(['keep']);
    expect(snapshot.requests.map((value) => value.requestId)).toEqual(['keep']);
    expect(items.map((value) => value.itemId)).toEqual(['keep', 'remove']);
    expect(requests.map((value) => value.requestId)).toEqual(['keep', 'remove']);
  });

  it('applies upserts without removals, replaces by identity and keeps descending stable order', () => {
    const state = createState();
    const snapshot = state.snapshot!;
    const items = (snapshot.items = [item('old'), item('replace')]);
    const requests = (snapshot.requests = [request('old'), request('replace')]);
    const notices = (snapshot.notices = [notice('old'), notice('replace')]);
    setRetainedReferences(state, ['old', 'replace', 'new']);
    const previous = structuredClone({ items, requests, notices });
    Object.freeze(items);
    Object.freeze(requests);
    Object.freeze(notices);
    const delta = createDelta(snapshot);
    delta.itemUpserts = [item('replace', later), item('new', later)];
    delta.requestUpserts = [request('replace', later), request('new', later)];
    delta.noticeUpserts = [notice('replace', later), notice('new', later)];

    applyCanonicalAppServerControlDelta(state, delta);

    expect(snapshot.items.map((value) => value.itemId)).toEqual(['replace', 'new', 'old']);
    expect(snapshot.requests.map((value) => value.requestId)).toEqual(['replace', 'new', 'old']);
    expect(snapshot.notices.map((value) => value.eventId)).toEqual(['replace', 'new', 'old']);
    expect({ items, requests, notices }).toEqual(previous);
    expect(snapshot.items[0]!.updatedAt).toBe(later);
    expect(snapshot.requests[0]!.updatedAt).toBe(later);
    expect(snapshot.notices[0]!.createdAt).toBe(later);
  });

  it('applies removals before upserts and preserves nested clone isolation on changed lists', () => {
    const state = createState();
    const snapshot = state.snapshot!;
    const retainedItem = item('retained');
    retainedItem.attachments = [{ kind: 'file', path: '/retained.txt' }];
    const upsertItem = item('replace', later);
    upsertItem.attachments = [{ kind: 'file', path: '/incoming.txt' }];
    const retainedRequest = request('retained');
    retainedRequest.answers = [{ questionId: 'q', answers: ['original'] }];
    const upsertRequest = request('replace', later);
    upsertRequest.questions = [
      {
        id: 'q',
        header: 'Choice',
        question: 'Continue?',
        multiSelect: false,
        options: [{ label: 'Yes', description: '' }],
      },
    ];
    snapshot.items = [retainedItem, item('replace'), item('remove')];
    snapshot.requests = [retainedRequest, request('replace'), request('remove')];
    setRetainedReferences(state, ['replace', 'retained', 'remove']);
    const delta = createDelta(snapshot);
    delta.itemRemovals = ['replace', 'remove'];
    delta.requestRemovals = ['replace', 'remove'];
    delta.itemUpserts = [upsertItem];
    delta.requestUpserts = [upsertRequest];

    applyCanonicalAppServerControlDelta(state, delta);

    expect(snapshot.items.map((value) => value.itemId)).toEqual(['replace', 'retained']);
    expect(snapshot.requests.map((value) => value.requestId)).toEqual(['replace', 'retained']);
    snapshot.items[0]!.attachments[0]!.path = '/changed-upsert.txt';
    snapshot.items[1]!.attachments[0]!.path = '/changed-retained.txt';
    snapshot.requests[0]!.questions[0]!.options[0]!.label = 'Changed';
    snapshot.requests[1]!.answers[0]!.answers.push('changed');
    expect(upsertItem.attachments[0]!.path).toBe('/incoming.txt');
    expect(retainedItem.attachments[0]!.path).toBe('/retained.txt');
    expect(upsertRequest.questions[0]!.options[0]!.label).toBe('Yes');
    expect(retainedRequest.answers[0]!.answers).toEqual(['original']);
  });

  it.each([10_000, 100_000])('bounds cached summaries with %i completed tools in one active turn', (count) => {
    const state = createState();
    const snapshot = state.snapshot!;
    setRetainedReferences(state, ['item-0', 'item-1']);
    state.historyAutoScrollPinned = false;
    snapshot.historyCount = count;
    snapshot.hasNewerHistory = true;
    snapshot.items = Array.from({ length: count }, (_, i) => ({
      ...item(`item-${i}`), turnId: 'turn',
    }));
    snapshot.items.push({ ...item('active'), turnId: 'turn', status: 'running' });
    snapshot.requests = [request('pending'), { ...request('resolved'), state: 'resolved' }];
    snapshot.requests[0]!.state = 'open';
    const delta = createDelta(snapshot);
    delta.streams = { ...snapshot.streams, assistantText: 'Still working outside the browse window' };
    delta.session = { ...snapshot.session, usage: { inputTokens: 123456 } };
    applyCanonicalAppServerControlDelta(state, delta);

    expect(snapshot.items.map((value) => value.itemId)).toEqual(['item-0', 'item-1', 'active']);
    expect(snapshot.requests.map((value) => value.requestId)).toEqual(['pending']);
    expect(snapshot.history).toHaveLength(2);
    expect(snapshot.historyCount).toBe(count);
    expect(snapshot.session.usage!.inputTokens).toBe(123456);
    expect(snapshot.streams.assistantText).toBe(delta.streams.assistantText);
    expect(JSON.stringify(snapshot).length).toBeLessThan(10_000);
    const settled = createDelta(snapshot);
    settled.currentTurn = { ...snapshot.currentTurn, state: 'completed' };
    settled.requestUpserts = [{ ...snapshot.requests[0]!, state: 'resolved' }];
    applyCanonicalAppServerControlDelta(state, settled);
    expect(snapshot.items.map((value) => value.itemId)).toEqual(['item-0', 'item-1']);
    expect(snapshot.requests).toEqual([]);
  });

  it('evicts completed summaries as full live patches roll the retained history window forward', () => {
    const state = createState();
    const snapshot = state.snapshot!;
    state.historyWindowTargetCount = 5;
    for (let i = 1; i <= 200; i++) {
      const delta = createDelta(snapshot);
      delta.historyCount = i + 1;
      delta.historyUpserts = [{
        ...historyEntry('Completed tool'), entryId: `tool-${i}`, order: i + 1, itemId: `item-${i}`,
      }];
      delta.itemUpserts = [{ ...item(`item-${i}`), turnId: 'turn' }];
      applyCanonicalAppServerControlDelta(state, delta);
      expect(snapshot.history.length).toBeLessThanOrEqual(5);
      expect(snapshot.items.length).toBeLessThanOrEqual(5);
    }
    expect(snapshot.items.some((value) => value.itemId === 'item-1')).toBe(false);
    expect(snapshot.items.some((value) => value.itemId === 'item-200')).toBe(true);
  });

  it('keeps the browser notice projection aligned with the host existing 64-notice bound', () => {
    const state = createState();
    const snapshot = state.snapshot!;
    snapshot.notices = Array.from({ length: 64 }, (_, i) => notice(`notice-${i}`));
    const delta = createDelta(snapshot);
    delta.noticeUpserts = [notice('latest-notice', later)];
    applyCanonicalAppServerControlDelta(state, delta);
    expect(snapshot.notices).toHaveLength(64);
    expect(snapshot.notices[0]!.eventId).toBe('latest-notice');
  });
});
