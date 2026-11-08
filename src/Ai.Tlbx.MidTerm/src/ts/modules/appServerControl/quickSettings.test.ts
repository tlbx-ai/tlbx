import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../../api/client', () => ({
  AppServerControlHttpError: class extends Error {},
  configureAppServerControlRuntime: vi.fn(),
  updateSettings: vi.fn().mockResolvedValue({ response: { ok: true } }),
  updateAppServerControlHistoryStreamWindow: vi.fn(),
}));
vi.mock('../i18n', () => ({ t: (key: string) => key }));

import { configureAppServerControlRuntime, updateSettings } from '../../api/client';
import type { AppServerControlConfigureResponse, AppServerControlHistorySnapshot, MidTermSettingsPublic } from '../../api/types';
import { applyFetchedAppServerControlHistoryWindow, applyLiveAppServerControlHistoryDelta } from '../agentView/historyWindowState';
import type { SessionAppServerControlViewState } from '../agentView/types';
import { $currentSettings, $sessions } from '../../stores';
import {
  configureAppServerControlQuickSettings,
  getAppServerControlQuickSettingsEffective,
  getAppServerControlRuntimeSettingStatus,
  createAppServerControlTurnRequestWithQuickSettings,
  getAppServerControlQuickSettingsDraft,
  removeAppServerControlQuickSettingsSessionState,
  setAppServerControlQuickSettingsDraft,
  syncAppServerControlQuickSettingsFromSnapshot,
  syncAppServerControlQuickSettingsFromHistory,
} from './quickSettings';

function createMemoryStorage(): Storage {
  const values = new Map<string, string>();
  return {
    get length() {
      return values.size;
    },
    clear() {
      values.clear();
    },
    getItem(key) {
      return values.has(key) ? values.get(key)! : null;
    },
    key(index) {
      return Array.from(values.keys())[index] ?? null;
    },
    removeItem(key) {
      values.delete(key);
    },
    setItem(key, value) {
      values.set(key, value);
    },
  };
}

function createSettings(patch: Partial<MidTermSettingsPublic> = {}): MidTermSettingsPublic {
  return {
    codexYoloDefault: false,
    codexDefaultAppServerControlModel: '',
    codexEnvironmentVariables: '',
    ...patch,
  } as MidTermSettingsPublic;
}

describe('appServerControl quick settings', () => {
  beforeEach(() => {
    vi.stubGlobal('localStorage', createMemoryStorage());
    globalThis.localStorage.clear();
    $sessions.set({});
    $currentSettings.set(createSettings());
    vi.mocked(updateSettings).mockClear();
  });

  afterEach(() => {
    removeAppServerControlQuickSettingsSessionState('codex-default');
    removeAppServerControlQuickSettingsSessionState('codex-save');
    removeAppServerControlQuickSettingsSessionState('grok-stale');
    $sessions.set({});
    globalThis.localStorage.clear();
    vi.unstubAllGlobals();
  });

  it('retains native Claude settings when the session profile is Claude', () => {
    $sessions.set({ 'grok-stale': { id: 'grok-stale', profileHint: 'claude' } } as any);
    syncAppServerControlQuickSettingsFromSnapshot('grok-stale', 'claude', {
      model: 'haiku',
      effort: 'low',
    });
    expect(getAppServerControlQuickSettingsDraft('grok-stale').model).toBe('haiku');
    expect(getAppServerControlQuickSettingsDraft('grok-stale').effort).toBe('low');
  });

  it('clears unsupported effort when switching native models and deep-clones their catalogues', () => {
    $sessions.set({ 'grok-stale': { id: 'grok-stale', profileHint: 'claude' } } as any);
    syncAppServerControlQuickSettingsFromSnapshot('grok-stale', 'claude', {
      model: 'opus',
      effort: 'high',
      modelOptions: [
        { value: 'opus', label: 'Opus', effortOptions: [{ value: 'high', label: 'High' }] },
        { value: 'haiku', label: 'Haiku', effortOptions: [] },
      ],
    });
    const draft = getAppServerControlQuickSettingsDraft('grok-stale');
    draft.modelOptions![0]!.effortOptions![0]!.label = 'Changed copy';
    expect(
      getAppServerControlQuickSettingsDraft('grok-stale').modelOptions![0]!.effortOptions![0]!
        .label,
    ).toBe('High');
    setAppServerControlQuickSettingsDraft('grok-stale', { model: 'haiku' });
    expect(getAppServerControlQuickSettingsDraft('grok-stale').effort).toBeNull();
  });

  it('sends changed native settings with opaque empty and boolean values and isolates returned drafts', () => {
    const id = 'grok-stale';
    syncAppServerControlQuickSettingsFromSnapshot(id, 'grok', {
      configOptions: [
        {
          configId: 'agent',
          name: 'Agent',
          type: 'select',
          currentValue: 'reviewer',
          options: [
            {
              name: 'Agents',
              options: [
                { value: '', name: 'Default' },
                { value: 'reviewer', name: 'Reviewer' },
              ],
            },
          ],
        },
        { configId: 'concise', name: 'Concise', type: 'boolean', currentValue: false },
      ],
    });
    const draft = getAppServerControlQuickSettingsDraft(id);
    const options = draft.configOptions!;
    options[0]!.currentValue = '';
    options[1]!.currentValue = true;
    expect(getAppServerControlQuickSettingsDraft(id).configOptions![0]!.currentValue).toBe(
      'reviewer',
    );
    setAppServerControlQuickSettingsDraft(id, { configOptions: options });
    expect(createAppServerControlTurnRequestWithQuickSettings(id, 'Inspect').configValues).toEqual({
      agent: '',
      concise: true,
    });
  });

  it('refreshes native choices while preserving valid pending edits and discarding removed choices', () => {
    const id = 'grok-stale';
    const options = [
      {
        configId: 'agent',
        name: 'Agent',
        type: 'select',
        currentValue: '',
        options: [
          { value: '', name: 'Default' },
          { value: 'reviewer', name: 'Reviewer' },
        ],
      },
      { configId: 'concise', name: 'Concise', type: 'boolean', currentValue: false },
    ];
    syncAppServerControlQuickSettingsFromSnapshot(id, 'grok', { configOptions: options });
    setAppServerControlQuickSettingsDraft(id, {
      configOptions: options.map((option) => ({
        ...option,
        currentValue: option.configId === 'agent' ? 'reviewer' : true,
      })),
    });
    syncAppServerControlQuickSettingsFromSnapshot(id, 'grok', { configOptions: options });
    expect(createAppServerControlTurnRequestWithQuickSettings(id, 'Inspect').configValues).toEqual({
      agent: 'reviewer',
      concise: true,
    });
    syncAppServerControlQuickSettingsFromSnapshot(id, 'grok', {
      configOptions: [
        { ...options[0]!, options: [{ value: '', name: 'Default' }] },
        { ...options[1]!, currentValue: true },
      ],
    });
    expect(
      createAppServerControlTurnRequestWithQuickSettings(id, 'Inspect').configValues,
    ).toBeUndefined();
  });

  it('persists Codex fast mode and includes it in the next turn request', () => {
    $sessions.set({
      'codex-default': {
        id: 'codex-default',
        profileHint: 'codex',
      } as never,
    });

    setAppServerControlQuickSettingsDraft('codex-default', { fastMode: 'on' });

    expect(getAppServerControlQuickSettingsDraft('codex-default').fastMode).toBe('on');
    expect(
      createAppServerControlTurnRequestWithQuickSettings(
        'codex-default',
        'Use the fast service tier.',
      ).fastMode,
    ).toBe('on');
  });

  it('preserves ACP agent model ids without provider-specific alias rewriting', () => {
    globalThis.localStorage.setItem(
      'midterm:appServerControl-quick-settings:provider:grok',
      JSON.stringify({ model: 'grok-build' }),
    );
    $sessions.set({
      'grok-stale': {
        id: 'grok-stale',
        profileHint: 'grok',
      } as never,
    });

    expect(getAppServerControlQuickSettingsDraft('grok-stale').model).toBe('grok-build');
  });
});

describe('runtime quick-settings acknowledgements', () => {
  const id = 'runtime-settings';
  const api = vi.mocked(configureAppServerControlRuntime);
  const original = {
    model: 'model-A',
    effort: 'medium',
    planMode: 'off',
    permissionMode: 'manual',
    fastMode: 'off',
  };
  function seed(provider = 'codex', running = true, threadId = 'thread-A') {
    $sessions.set({ [id]: { id, profileHint: provider } } as any);
    syncAppServerControlQuickSettingsFromSnapshot(id, provider, original, {
      thread: { threadId, state: 'active', stateLabel: 'Active' },
      currentTurn: { turnId: 'turn-A', state: running ? 'running' : 'completed', stateLabel: '' },
      connected: true,
      sessionState: 'ready',
    });
  }
  function receipt(
    patch: Partial<AppServerControlConfigureResponse> = {},
  ): AppServerControlConfigureResponse {
    return {
      sessionId: id,
      threadId: 'thread-A',
      turnId: 'turn-A',
      status: 'applied',
      threadStatus: 'applied',
      threadScope: 'subsequentTurns',
      turnStatus: 'applied',
      turnScope: 'subsequentCaptures',
      effectiveSettings: {
        model: 'provider-B',
        effort: 'high',
        collaborationMode: { mode: 'default' },
        approvalPolicy: 'on-request',
        sandboxPolicy: { type: 'workspaceWrite' },
        serviceTier: null,
      },
      confirmedSettings: {},
      deferredFields: [],
      unconfirmedFields: [],
      fieldErrors: {},
      ...patch,
    };
  }
  beforeEach(() => {
    vi.stubGlobal('localStorage', createMemoryStorage());
    $currentSettings.set(createSettings());
    api.mockReset();
    vi.mocked(updateSettings).mockClear();
    seed();
  });
  afterEach(() => {
    removeAppServerControlQuickSettingsSessionState(id);
    $sessions.set({});
    vi.unstubAllGlobals();
  });

  function startupSnapshot(threadId = id, sessionState = 'starting'): AppServerControlHistorySnapshot {
    return {
      sessionId: id, provider: 'codex', latestSequence: 1, generatedAt: '',
      historyCount: 0, historyWindowStart: 0, historyWindowEnd: 0,
      hasOlderHistory: false, hasNewerHistory: false,
      session: { state: sessionState, stateLabel: '' },
      thread: { threadId, state: 'active', stateLabel: '' },
      currentTurn: { turnId: null, state: '', stateLabel: '' },
      quickSettings: original,
      streams: { assistantText: '', reasoningText: '', reasoningSummaryText: '', planText: '', commandOutput: '', fileChangeOutput: '', unifiedDiff: '' },
      history: [], items: [], requests: [], notices: [],
    };
  }

  it('keeps all four pre-prompt selections pending until canonical attach-ready metadata arrives without paint', async () => {
    removeAppServerControlQuickSettingsSessionState(id);
    const state = { snapshot: null, streamConnected: true, historyWindowTargetCount: 80 } as unknown as SessionAppServerControlViewState;
    const raf = vi.fn();
    vi.stubGlobal('requestAnimationFrame', raf);
    applyFetchedAppServerControlHistoryWindow(id, state, startupSnapshot());
    api.mockResolvedValue(receipt({
      threadId: 'provider-real', turnId: null,
      effectiveSettings: { ...receipt().effectiveSettings, collaborationMode: { mode: 'plan' }, approvalPolicy: 'never', sandboxPolicy: { type: 'dangerFullAccess' } },
    }));
    const operations = [
      configureAppServerControlQuickSettings(id, { model: 'provider-B' }),
      configureAppServerControlQuickSettings(id, { effort: 'high' }),
      configureAppServerControlQuickSettings(id, { planMode: 'on' }),
      configureAppServerControlQuickSettings(id, { permissionMode: 'auto' }),
    ];
    await Promise.resolve();
    expect(api).not.toHaveBeenCalled();
    expect(getAppServerControlQuickSettingsDraft(id).model).toBe('provider-B');
    for (const field of ['model', 'effort', 'planMode', 'permissionMode'] as const)
      expect(getAppServerControlRuntimeSettingStatus(id, field)?.state).toBe('pending');
    // A ready label cannot make the session fallback ID an authoritative provider target.
    applyFetchedAppServerControlHistoryWindow(id, state, startupSnapshot(id, 'ready'));
    applyFetchedAppServerControlHistoryWindow(id, state, startupSnapshot('provider-real', 'starting'));
    await Promise.resolve();
    expect(api).not.toHaveBeenCalled();
    const readyWindow = startupSnapshot('provider-real', 'ready');
    readyWindow.thread.state = 'idle';
    applyFetchedAppServerControlHistoryWindow(id, state, readyWindow);
    await Promise.all(operations);
    expect(api).toHaveBeenCalledTimes(4);
    for (const [, request] of api.mock.calls) {
      expect(request.expectedThreadId).toBe('provider-real');
      expect(request.expectedTurnId).toBeNull();
    }
    expect(api.mock.calls.map(([, request]) => request.settings)).toEqual([
      { model: 'provider-B' }, { effort: 'high' }, { planMode: 'on' }, { permissionMode: 'auto' },
    ]);
    for (let i = 0; i < 3; i++) syncAppServerControlQuickSettingsFromHistory(state.snapshot!, true);
    await Promise.resolve();
    expect(api).toHaveBeenCalledTimes(4);
    expect(raf).not.toHaveBeenCalled();
  });

  it('drops superseded startup choices and sends only the latest after the first binding', async () => {
    removeAppServerControlQuickSettingsSessionState(id);
    syncAppServerControlQuickSettingsFromHistory(startupSnapshot(), true);
    const first = configureAppServerControlQuickSettings(id, { model: 'B' });
    const second = configureAppServerControlQuickSettings(id, { model: 'C' });
    api.mockResolvedValue(receipt({ threadId: 'provider-real', turnId: null }));
    syncAppServerControlQuickSettingsFromHistory(startupSnapshot('provider-real', 'ready'), true);
    await Promise.all([first, second]);
    expect(api).toHaveBeenCalledTimes(1);
    expect(api.mock.calls[0]?.[1].settings.model).toBe('C');
  });

  it('waits for connection readiness without changing a known target guard', async () => {
    removeAppServerControlQuickSettingsSessionState(id);
    const snapshot = startupSnapshot('thread-A', 'ready');
    syncAppServerControlQuickSettingsFromHistory(snapshot, false);
    const pending = configureAppServerControlQuickSettings(id, { model: 'B' });
    await Promise.resolve();
    expect(api).not.toHaveBeenCalled();
    api.mockResolvedValue(receipt({ turnId: null }));
    syncAppServerControlQuickSettingsFromHistory(snapshot, true);
    await pending;
    expect(api).toHaveBeenCalledTimes(1);
    expect(api.mock.calls[0]?.[1]).toMatchObject({ expectedThreadId: 'thread-A', expectedTurnId: null });
  });

  it('binds all startup edits to the same first-ready scope rather than a later busy turn', async () => {
    removeAppServerControlQuickSettingsSessionState(id);
    syncAppServerControlQuickSettingsFromHistory(startupSnapshot(), true);
    let resolve!: (value: AppServerControlConfigureResponse) => void;
    api.mockReturnValueOnce(new Promise((done) => { resolve = done; }));
    const first = configureAppServerControlQuickSettings(id, { model: 'B' });
    const second = configureAppServerControlQuickSettings(id, { effort: 'high' });
    syncAppServerControlQuickSettingsFromHistory(startupSnapshot('thread-A', 'ready'), true);
    await vi.waitFor(() => expect(api).toHaveBeenCalledTimes(1));
    const busy = startupSnapshot('thread-A', 'running');
    busy.currentTurn = { turnId: 'new-turn', state: 'running', stateLabel: '' };
    syncAppServerControlQuickSettingsFromHistory(busy, true);
    resolve(receipt({ turnId: null }));
    await Promise.all([first, second]);
    expect(api).toHaveBeenCalledTimes(1);
    expect(api.mock.calls[0]?.[1].expectedTurnId).toBeNull();
    expect(getAppServerControlRuntimeSettingStatus(id, 'effort')?.state).toBe('targetUnavailable');
  });

  it('does not replace runtime identity when a stale history window is rejected', async () => {
    const state = { snapshot: null, streamConnected: true, historyWindowTargetCount: 80 } as unknown as SessionAppServerControlViewState;
    const current = { ...startupSnapshot('thread-A', 'ready'), latestSequence: 10 };
    applyFetchedAppServerControlHistoryWindow(id, state, current);
    const stale = { ...startupSnapshot('stale-thread', 'ready'), latestSequence: 9 };
    expect(applyFetchedAppServerControlHistoryWindow(id, state, stale)).toBe(false);
    api.mockResolvedValue(receipt({ turnId: null }));
    await configureAppServerControlQuickSettings(id, { effort: 'high' });
    expect(api.mock.calls[0]?.[1]).toMatchObject({ expectedThreadId: 'thread-A', expectedTurnId: null });
  });

  it('releases an unsent startup waiter on close and never transfers it to a replacement', async () => {
    removeAppServerControlQuickSettingsSessionState(id);
    syncAppServerControlQuickSettingsFromHistory(startupSnapshot(), true);
    const pending = configureAppServerControlQuickSettings(id, { model: 'B' });
    await Promise.resolve();
    removeAppServerControlQuickSettingsSessionState(id);
    seed('codex', false, 'replacement');
    await pending;
    expect(api).not.toHaveBeenCalled();
    expect(getAppServerControlRuntimeSettingStatus(id, 'model')).toBeUndefined();
  });

  it('cancels an unsent known-thread change on replacement and reports failed attach without replay', async () => {
    removeAppServerControlQuickSettingsSessionState(id);
    syncAppServerControlQuickSettingsFromHistory(startupSnapshot('provider-A'), true);
    const pending = configureAppServerControlQuickSettings(id, { model: 'B' });
    await Promise.resolve();
    syncAppServerControlQuickSettingsFromHistory(startupSnapshot('provider-B', 'ready'), true);
    await pending;
    expect(api).not.toHaveBeenCalled();
    removeAppServerControlQuickSettingsSessionState(id);
    syncAppServerControlQuickSettingsFromHistory(startupSnapshot(), true);
    const failed = configureAppServerControlQuickSettings(id, { model: 'C' });
    await Promise.resolve();
    syncAppServerControlQuickSettingsFromHistory(startupSnapshot(id, 'error'), true);
    await failed;
    expect(api).not.toHaveBeenCalled();
    expect(getAppServerControlRuntimeSettingStatus(id, 'model')?.state).toBe('targetUnavailable');
  });

  it('uses the latest ingested busy-turn guard without waiting for a render', async () => {
    seed('codex', false);
    const snapshot = startupSnapshot('thread-A', 'running');
    const state = { snapshot, streamConnected: true, historyWindowTargetCount: 80 } as unknown as SessionAppServerControlViewState;
    applyLiveAppServerControlHistoryDelta(state, {
      ...snapshot, latestSequence: 2,
      currentTurn: { turnId: 'busy-now', state: 'running', stateLabel: '' },
      historyUpserts: [], historyRemovals: [], itemUpserts: [], itemRemovals: [],
      requestUpserts: [], requestRemovals: [], noticeUpserts: [],
    });
    api.mockResolvedValue(receipt({ turnId: 'busy-now' }));
    await configureAppServerControlQuickSettings(id, { effort: 'high' });
    expect(api.mock.calls[0]?.[1].expectedTurnId).toBe('busy-now');
  });

  it('does not replay a mutation with unknown outcome when readiness is observed again', async () => {
    api.mockRejectedValueOnce(new Error('lost after send'));
    await configureAppServerControlQuickSettings(id, { model: 'B' });
    for (let i = 0; i < 3; i++) seed();
    await Promise.resolve();
    expect(api).toHaveBeenCalledTimes(1);
    expect(getAppServerControlRuntimeSettingStatus(id, 'model')?.state).toBe('unconfirmed');
  });

  it('sends exact canonical busy guards independently of a prompt and displays normalized readback', async () => {
    let resolve!: (value: AppServerControlConfigureResponse) => void;
    api.mockReturnValueOnce(
      new Promise((done) => {
        resolve = done;
      }),
    );
    const pending = configureAppServerControlQuickSettings(id, { model: 'requested-B' });
    expect(getAppServerControlRuntimeSettingStatus(id, 'model')?.state).toBe('pending');
    expect(getAppServerControlQuickSettingsEffective(id).model).toBe('model-A');
    await vi.waitFor(() => expect(api).toHaveBeenCalledTimes(1));
    expect(api).toHaveBeenCalledWith(id, {
      expectedThreadId: 'thread-A',
      expectedTurnId: 'turn-A',
      settings: { model: 'requested-B' },
    });
    resolve(receipt());
    await pending;
    expect(getAppServerControlQuickSettingsDraft(id).model).toBe('provider-B');
    expect(getAppServerControlQuickSettingsDraft(id).effort).toBe('high');
    expect(getAppServerControlRuntimeSettingStatus(id, 'effort')?.scope).toBe('subsequentTurns');
    expect(getAppServerControlRuntimeSettingStatus(id, 'model')).toMatchObject({
      state: 'applied',
      scope: 'subsequentCaptures',
    });
    const nextPrompt = createAppServerControlTurnRequestWithQuickSettings(id, 'Continue');
    expect(nextPrompt).not.toHaveProperty('model');
    expect(nextPrompt).not.toHaveProperty('planMode');
    expect(nextPrompt).not.toHaveProperty('permissionMode');
  });

  it('protects readback from stale snapshots then follows later canonical changes after observing it', async () => {
    api.mockResolvedValueOnce(receipt());
    await configureAppServerControlQuickSettings(id, { model: 'requested-B' });
    seed();
    expect(getAppServerControlQuickSettingsDraft(id).model).toBe('provider-B');
    syncAppServerControlQuickSettingsFromSnapshot(id, 'codex', {
      ...original,
      model: 'provider-B',
      effort: 'high',
    });
    syncAppServerControlQuickSettingsFromSnapshot(id, 'codex', {
      ...original,
      model: 'external-C',
      effort: 'low',
    });
    expect(getAppServerControlQuickSettingsDraft(id).model).toBe('external-C');
    expect(api).toHaveBeenCalledTimes(1);
  });

  it('retains confirmed future defaults when the active target ends, without retrying', async () => {
    let resolve!: (value: AppServerControlConfigureResponse) => void;
    api.mockReturnValueOnce(
      new Promise((done) => {
        resolve = done;
      }),
    );
    const pending = configureAppServerControlQuickSettings(id, { model: 'requested-B' });
    await vi.waitFor(() => expect(api).toHaveBeenCalledTimes(1));
    seed('codex', false);
    resolve(receipt({ status: 'partial', turnStatus: 'targetUnavailable' }));
    await pending;
    expect(getAppServerControlQuickSettingsDraft(id).model).toBe('provider-B');
    expect(getAppServerControlRuntimeSettingStatus(id, 'model')).toMatchObject({
      state: 'partial',
      scope: 'subsequentTurns',
    });
    expect(api).toHaveBeenCalledTimes(1);
  });

  it('retains an acknowledged active capture when future defaults fail and expires it with the turn', async () => {
    api.mockResolvedValueOnce(
      receipt({
        status: 'partial',
        threadStatus: 'unconfirmed',
        turnStatus: 'applied',
        effectiveSettings: {},
        confirmedSettings: { model: 'live-B' },
        unconfirmedFields: ['model'],
      }),
    );
    await configureAppServerControlQuickSettings(id, { model: 'live-B' });
    expect(getAppServerControlQuickSettingsDraft(id).model).toBe('live-B');
    expect(getAppServerControlQuickSettingsEffective(id).model).toBe('model-A');
    expect(getAppServerControlRuntimeSettingStatus(id, 'model')).toMatchObject({
      state: 'partial',
      scope: 'subsequentCaptures',
    });
    seed('codex', false);
    expect(getAppServerControlQuickSettingsDraft(id).model).toBe('model-A');
    expect(api).toHaveBeenCalledTimes(1);
  });

  it('rolls back unconfirmed errors and never persists or resubmits the failed draft', async () => {
    api.mockResolvedValueOnce(
      receipt({
        status: 'unconfirmed',
        threadStatus: 'unconfirmed',
        turnStatus: 'unconfirmed',
        effectiveSettings: {},
      }),
    );
    await configureAppServerControlQuickSettings(id, { model: 'failed-model' });
    expect(getAppServerControlQuickSettingsDraft(id).model).toBe('model-A');
    expect(getAppServerControlRuntimeSettingStatus(id, 'model')?.state).toBe('unconfirmed');
    expect(updateSettings).not.toHaveBeenCalled();
    expect(createAppServerControlTurnRequestWithQuickSettings(id, 'Continue')).not.toHaveProperty(
      'model',
    );
    expect(api).toHaveBeenCalledTimes(1);
  });

  it('keeps a newer selection pending over an older ack and rolls failure back to that ack', async () => {
    let resolve!: (value: AppServerControlConfigureResponse) => void;
    api.mockReturnValueOnce(
      new Promise((done) => {
        resolve = done;
      }),
    );
    const first = configureAppServerControlQuickSettings(id, { model: 'B' });
    await vi.waitFor(() => expect(api).toHaveBeenCalledTimes(1));
    const second = configureAppServerControlQuickSettings(id, { model: 'C' });
    let rejectSecond!: (error: Error) => void;
    api.mockReturnValueOnce(
      new Promise((_done, reject) => {
        rejectSecond = reject;
      }),
    );
    resolve(receipt());
    await first;
    expect(getAppServerControlQuickSettingsDraft(id).model).toBe('C');
    await vi.waitFor(() => expect(api).toHaveBeenCalledTimes(2));
    rejectSecond(new Error('transport lost after send'));
    await second;
    expect(getAppServerControlQuickSettingsDraft(id).model).toBe('provider-B');
    expect(getAppServerControlRuntimeSettingStatus(id, 'model')?.state).toBe('unconfirmed');
    expect(api).toHaveBeenCalledTimes(2);
  });

  it('does not retarget a queued explicit change to a new turn after its click-time guard expires', async () => {
    let resolve!: (value: AppServerControlConfigureResponse) => void;
    api.mockReturnValueOnce(
      new Promise((done) => {
        resolve = done;
      }),
    );
    const first = configureAppServerControlQuickSettings(id, { model: 'B' });
    await vi.waitFor(() => expect(api).toHaveBeenCalledTimes(1));
    const second = configureAppServerControlQuickSettings(id, { model: 'C' });
    seed('codex', false);
    resolve(receipt({ status: 'partial', turnStatus: 'targetUnavailable' }));
    await Promise.all([first, second]);
    expect(api).toHaveBeenCalledTimes(1);
    expect(getAppServerControlQuickSettingsDraft(id).model).toBe('provider-B');
    expect(getAppServerControlRuntimeSettingStatus(id, 'model')?.state).toBe('targetUnavailable');
  });

  it('drops superseded unsent selections and sends only the latest explicit choice', async () => {
    api.mockResolvedValueOnce(receipt());
    const first = configureAppServerControlQuickSettings(id, { model: 'B' });
    const second = configureAppServerControlQuickSettings(id, { model: 'C' });
    await Promise.all([first, second]);
    expect(api).toHaveBeenCalledTimes(1);
    expect(api.mock.calls[0]?.[1].settings.model).toBe('C');
  });

  it('ignores an old-thread ack after a canonical thread replacement', async () => {
    let resolve!: (value: AppServerControlConfigureResponse) => void;
    api.mockReturnValueOnce(
      new Promise((done) => {
        resolve = done;
      }),
    );
    const pending = configureAppServerControlQuickSettings(id, { model: 'B' });
    await vi.waitFor(() => expect(api).toHaveBeenCalledTimes(1));
    seed('codex', false, 'thread-new');
    resolve(receipt());
    await pending;
    expect(getAppServerControlQuickSettingsDraft(id).model).toBe('model-A');
    expect(getAppServerControlRuntimeSettingStatus(id, 'model')).toBeUndefined();
    expect(api).toHaveBeenCalledTimes(1);
  });

  it('labels Claude void-control acceptance for subsequent responses without inventing readback', async () => {
    seed('claude');
    api.mockResolvedValueOnce(
      receipt({
        status: 'acknowledged',
        threadStatus: 'acknowledged',
        turnStatus: 'acknowledged',
        threadScope: 'subsequentResponses',
        turnScope: 'subsequentResponses',
        effectiveSettings: {},
        confirmedSettings: { model: 'claude-B', planMode: 'on', permissionMode: 'manual' },
      }),
    );
    await configureAppServerControlQuickSettings(id, { model: 'claude-B', planMode: 'on' });
    expect(getAppServerControlQuickSettingsDraft(id).model).toBe('claude-B');
    expect(getAppServerControlQuickSettingsEffective(id).model).toBe('model-A');
    expect(getAppServerControlRuntimeSettingStatus(id, 'model')).toMatchObject({
      state: 'acknowledged',
      scope: 'subsequentResponses',
    });
    seed('claude'); // A stale snapshot must not erase accepted defaults.
    expect(getAppServerControlQuickSettingsDraft(id).model).toBe('claude-B');
  });

  it('preserves explicit Codex effort null instead of replacing it with the medium UI seed', async () => {
    api.mockResolvedValueOnce(
      receipt({ effectiveSettings: { effort: null }, confirmedSettings: { effort: null } }),
    );
    await configureAppServerControlQuickSettings(id, { effort: null });
    expect(api.mock.calls[0]?.[1].settings).toEqual({ effort: null });
    expect(getAppServerControlQuickSettingsDraft(id).effort).toBeNull();
  });

  it('uses a null idle guard and preserves an explicit reset', async () => {
    seed('claude', false);
    api.mockResolvedValueOnce(
      receipt({
        turnId: null,
        status: 'acknowledged',
        threadStatus: 'acknowledged',
        turnStatus: 'notRequested',
        threadScope: 'subsequentResponses',
        turnScope: 'subsequentResponses',
        effectiveSettings: {},
        confirmedSettings: { model: null },
      }),
    );
    await configureAppServerControlQuickSettings(id, { model: null });
    expect(api.mock.calls[0]?.[1]).toEqual({
      expectedThreadId: 'thread-A',
      expectedTurnId: null,
      settings: { model: null },
    });
    expect(getAppServerControlQuickSettingsDraft(id).model).toBeNull();
  });
  it('restores Claude acknowledged defaults after reload without inventing effective inference settings', () => {
    seed('claude');
    const before = getAppServerControlQuickSettingsEffective(id);
    syncAppServerControlQuickSettingsFromSnapshot(id, 'claude', {
      ...original, model: 'accepted-model', effort: 'high', permissionMode: 'auto',
      acknowledgedFields: ['model', 'effort', 'permissionMode', 'planMode'],
    });
    expect(getAppServerControlQuickSettingsDraft(id).permissionMode).toBe('auto');
    expect(getAppServerControlQuickSettingsDraft(id).model).toBe('accepted-model');
    expect(getAppServerControlQuickSettingsEffective(id).model).toBe(before.model);
    expect(getAppServerControlQuickSettingsEffective(id).permissionMode).toBe(before.permissionMode);
    expect(getAppServerControlRuntimeSettingStatus(id, 'permissionMode')).toEqual({
      state: 'acknowledged', scope: 'subsequentResponses',
    });
    expect(api).not.toHaveBeenCalled();
    const next = createAppServerControlTurnRequestWithQuickSettings(id, 'next prompt');
    expect(next.permissionMode).toBeUndefined();
  });

  it('does not let an older canonical acknowledgement undo a newer Claude receipt', async () => {
    seed('claude');
    api.mockResolvedValueOnce(receipt({ status: 'acknowledged', threadStatus: 'acknowledged',
      turnStatus: 'acknowledged', threadScope: 'subsequentResponses', turnScope: 'subsequentResponses',
      effectiveSettings: {}, confirmedSettings: { permissionMode: 'auto', planMode: 'off' } }));
    await configureAppServerControlQuickSettings(id, { permissionMode: 'auto' });
    syncAppServerControlQuickSettingsFromSnapshot(id, 'claude', {
      ...original, acknowledgedFields: ['permissionMode', 'planMode'],
    });
    expect(getAppServerControlQuickSettingsDraft(id).permissionMode).toBe('auto');
    expect(getAppServerControlQuickSettingsEffective(id).permissionMode).toBe('manual');
  });

  it('keeps a rejected mode error visible when the canonical stream repeats old acknowledged defaults', async () => {
    seed('claude');
    syncAppServerControlQuickSettingsFromSnapshot(id, 'claude', {
      ...original, acknowledgedFields: ['permissionMode', 'planMode'],
    });
    api.mockRejectedValueOnce(new Error('mode rejected'));
    await configureAppServerControlQuickSettings(id, { permissionMode: 'auto' });
    syncAppServerControlQuickSettingsFromSnapshot(id, 'claude', {
      ...original, acknowledgedFields: ['permissionMode', 'planMode'],
    });
    expect(getAppServerControlQuickSettingsDraft(id).permissionMode).toBe('manual');
    expect(getAppServerControlRuntimeSettingStatus(id, 'permissionMode')?.state).toBe('unconfirmed');
  });

});
