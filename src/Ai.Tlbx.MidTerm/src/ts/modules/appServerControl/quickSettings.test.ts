import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../../api/client', () => ({
  configureAppServerControlRuntime: vi.fn(),
  updateSettings: vi.fn().mockResolvedValue({ response: { ok: true } }),
}));

import { configureAppServerControlRuntime, updateSettings } from '../../api/client';
import type { AppServerControlConfigureResponse, MidTermSettingsPublic } from '../../api/types';
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
