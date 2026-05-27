import { beforeEach, describe, expect, it, vi } from 'vitest';
import { $activeSessionId, $focusedSessionId, $settingsOpen, $sessions } from '../stores';
import type { Session } from '../types';

const sendSessionPrompt = vi.fn();
const sendInput = vi.fn();
const getSessionAgentVibe = vi.fn();
const getWebPreviewTarget = vi.fn();
const getBrowserPreviewStatus = vi.fn();
const requestSelectSession = vi.fn();
const getLayoutSessionIds = vi.fn(() => ['s1']);
const openSettings = vi.fn();
const closeSettings = vi.fn();
const switchSettingsTab = vi.fn((tab: string) => {
  activeSettingsTab = tab;
});
let activeSettingsTab = 'updates';

vi.mock('./logging', () => ({
  createLogger: () => ({
    debug: vi.fn(),
    error: vi.fn(),
    info: vi.fn(),
    warn: vi.fn(),
  }),
}));

vi.mock('./comms', () => ({
  sendInput,
}));

vi.mock('./comms/stateChannel', () => ({
  requestSelectSession,
}));

vi.mock('../api/client', () => ({
  ApiProblemError: class ApiProblemError extends Error {},
  createSession: vi.fn(),
  deleteSession: vi.fn(),
  getHistory: vi.fn(),
  getSessionAgentFeed: vi.fn(),
  getSessionAgentVibe,
  sendSessionPrompt,
}));

vi.mock('./web/webApi', () => ({
  captureBrowserScreenshotRaw: vi.fn(),
  getBrowserPreviewStatus,
  getWebPreviewTarget,
  runBrowserCommand: vi.fn(),
  setWebPreviewTarget: vi.fn(),
}));

vi.mock('./web', () => ({
  selectActivePreview: vi.fn(),
}));

vi.mock('./terminal/fileDrop', () => ({
  uploadFile: vi.fn(),
}));

vi.mock('./git/gitApi', () => ({
  addGitRepo: vi.fn(),
  fetchGitRepos: vi.fn(),
  refreshGitRepo: vi.fn(),
  removeGitRepo: vi.fn(),
}));

vi.mock('./git', () => ({
  applyGitReposForSession: vi.fn(),
  getCachedGitReposForSession: vi.fn(() => []),
}));

vi.mock('./layout/layoutStore', () => ({
  dockSession: vi.fn(),
  focusLayoutSession: vi.fn(),
  getLayoutSessionIds,
  isSessionInLayout: vi.fn((sessionId: string) => sessionId === 's1'),
  swapLayoutSessions: vi.fn(),
  undockSession: vi.fn(),
}));

vi.mock('./settings/panel', () => ({
  closeSettings,
  openSettings,
}));

vi.mock('./settings/tabs', () => ({
  getActiveSettingsTab: vi.fn(() => activeSettingsTab),
  normalizeStoredSettingsTab: vi.fn((tab: string | null) => {
    switch (tab) {
      case 'updates':
      case 'sessions':
      case 'appearance':
      case 'workflow':
      case 'terminal':
      case 'ai-agents':
      case 'security':
      case 'connected-hosts':
      case 'advanced':
        return tab;
      case 'general':
        return 'updates';
      case 'hub':
        return 'connected-hosts';
      default:
        return null;
    }
  }),
  switchSettingsTab,
}));

const { processToolRequest } = await import('./voiceTools');

function createSession(id: string, title: string): Session {
  return {
    id,
    name: title,
    terminalTitle: title,
    foregroundName: 'codex',
    currentDirectory: 'Q:\\repos\\MidTerm',
    shellType: 'pwsh',
    order: 1,
    _order: 1,
  } as Session;
}

function getResult(
  response: Awaited<ReturnType<typeof processToolRequest>>,
): Record<string, unknown> {
  expect(response.type).toBe('tool_response');
  expect(response.error).toBeUndefined();
  expect(response.result).toBeTruthy();
  return response.result as Record<string, unknown>;
}

function createAgentVibe(
  sessionId: string,
  state: string,
  latestSummary: string,
): Record<string, unknown> {
  return {
    sessionId,
    header: {
      state,
      stateLabel: state,
      title: 'Worker lane',
      providerLabel: 'Codex',
      needsAttention: false,
      attentionReason: null,
      chips: [],
    },
    overview: {
      stateMeta: state,
      activityMeta: latestSummary,
    },
    activities: [
      {
        tone: 'neutral',
        kind: 'agent',
        summary: latestSummary,
        detail: latestSummary,
        createdAt: '2026-05-27T00:00:00.000Z',
      },
    ],
    terminal: {
      tailText: latestSummary,
    },
  };
}

describe('voice tool response contract', () => {
  beforeEach(async () => {
    vi.clearAllMocks();
    activeSettingsTab = 'updates';
    $sessions.set({});
    $activeSessionId.set(null);
    $focusedSessionId.set(null);
    $settingsOpen.set(false);
    getLayoutSessionIds.mockReturnValue(['s1']);
    openSettings.mockImplementation(() => {
      $settingsOpen.set(true);
    });
    closeSettings.mockImplementation(() => {
      $settingsOpen.set(false);
    });
    await processToolRequest({
      type: 'tool_request',
      requestId: 'focus-clear',
      tool: 'focus_context',
      args: { action: 'clear' },
    });
  });

  it('returns target context and flow guidance when selecting a session', async () => {
    $sessions.set({ s1: createSession('s1', 'MidTerm source') });
    $activeSessionId.set('s1');
    $focusedSessionId.set('s1');

    const result = getResult(
      await processToolRequest({
        type: 'tool_request',
        requestId: 'select-1',
        tool: 'select_session',
        args: { sessionId: 's1' },
      }),
    );

    expect(requestSelectSession).toHaveBeenCalledWith('s1', {
      closeSettingsPanel: false,
      focusTerminal: true,
    });
    expect(result).toMatchObject({
      success: true,
      activeSessionId: 's1',
      responseText: 'Selected MidTerm source.',
    });
    expect(result.nextAction).toEqual(expect.stringContaining('summarize its turn state'));
    expect(result.targetContext).toMatchObject({
      activeSessionId: 's1',
      focusedSessionId: 's1',
      layoutSessionIds: ['s1'],
      targetSessionId: 's1',
      targetSessionTitle: 'MidTerm source',
      targetSessionExists: true,
      action: 'select_session',
      isTargetActive: true,
      isTargetFocused: true,
      isTargetInLayout: true,
    });
  });

  it('persists voice focus context across session and browser targeting', async () => {
    $sessions.set({ s1: createSession('s1', 'Browser lane') });
    $activeSessionId.set('s1');
    $focusedSessionId.set('s1');
    getWebPreviewTarget.mockResolvedValue({
      sessionId: 's1',
      previewName: 'app',
      url: 'https://localhost:2100',
      active: true,
      targetRevision: 7,
    });
    getBrowserPreviewStatus.mockResolvedValue({
      connected: true,
      controllable: true,
      hasTarget: true,
      hasUiClient: true,
      isScoped: true,
      state: 'ready',
      connectedClientCount: 1,
      totalConnectedClientCount: 1,
      connectedUiClientCount: 1,
      ownerConnected: true,
      clients: [],
    });

    await processToolRequest({
      type: 'tool_request',
      requestId: 'browser-status-focus',
      tool: 'dev_browser_status',
      args: { sessionId: 's1', previewName: 'app', previewId: 'preview-1' },
    });

    const result = getResult(
      await processToolRequest({
        type: 'tool_request',
        requestId: 'focus-status',
        tool: 'focus_context',
        args: { action: 'status' },
      }),
    );

    expect(result).toMatchObject({
      success: true,
      action: 'status',
      responseText: 'Voice focus is Browser lane.',
    });
    expect(typeof result.persisted).toBe('boolean');
    expect(result.focus).toMatchObject({
      active: true,
      sessionId: 's1',
      sessionTitle: 'Browser lane',
      sessionExists: true,
      previewName: 'app',
      previewId: 'preview-1',
      reason: 'dev_browser_status',
    });
    expect(result.nextAction).toEqual(expect.stringContaining('Carry targetContext'));
    expect(result.targetContext).toMatchObject({
      targetSessionId: 's1',
      targetPreviewName: 'app',
      targetPreviewId: 'preview-1',
      action: 'focus_context:status',
      isTargetActive: true,
      isTargetFocused: true,
      isTargetInLayout: true,
    });
  });

  it('tracks manual MidTerm session focus changes as voice focus context', async () => {
    $sessions.set({
      s1: createSession('s1', 'Worker one'),
      s2: createSession('s2', 'Worker two'),
    });
    getLayoutSessionIds.mockReturnValue(['s1', 's2']);

    $activeSessionId.set('s1');
    $focusedSessionId.set('s1');
    $activeSessionId.set('s2');
    $focusedSessionId.set('s2');

    const result = getResult(
      await processToolRequest({
        type: 'tool_request',
        requestId: 'manual-focus-status',
        tool: 'focus_context',
        args: { action: 'status' },
      }),
    );

    expect(result).toMatchObject({
      success: true,
      action: 'status',
      responseText: 'Voice focus is Worker two.',
    });
    expect(result.focus).toMatchObject({
      active: true,
      sessionId: 's2',
      sessionTitle: 'Worker two',
      sessionExists: true,
      previewName: null,
      previewId: null,
      repoRoot: null,
      reason: 'ui_focused_session',
    });
    expect(result.targetContext).toMatchObject({
      activeSessionId: 's2',
      focusedSessionId: 's2',
      targetSessionId: 's2',
      targetSessionTitle: 'Worker two',
      targetSessionExists: true,
      isTargetActive: true,
      isTargetFocused: true,
      isTargetInLayout: true,
    });
  });

  it('opens the global update settings surface through app_shell', async () => {
    $sessions.set({ s1: createSession('s1', 'Worker lane') });
    $activeSessionId.set('s1');
    $focusedSessionId.set('s1');

    const result = getResult(
      await processToolRequest({
        type: 'tool_request',
        requestId: 'app-shell-open-updates',
        tool: 'app_shell',
        args: { action: 'open_settings', settingsTab: 'updates', reason: 'user asked for updates' },
      }),
    );

    expect(openSettings).toHaveBeenCalledOnce();
    expect(switchSettingsTab).toHaveBeenCalledWith('updates');
    expect(result).toMatchObject({
      success: true,
      action: 'open_settings',
      settingsOpen: true,
      activeSettingsTab: 'updates',
      requestedSettingsTab: 'updates',
      activeSessionId: 's1',
      focusedSessionId: 's1',
      responseText: 'Opened Settings on updates.',
    });
    expect(result.nextAction).toEqual(expect.stringContaining('Updates & About'));
    expect(result.targetContext).toMatchObject({
      action: 'app_shell:open_settings',
      activeSessionId: 's1',
      focusedSessionId: 's1',
    });
  });

  it('anchors send_prompt responses to the operated session', async () => {
    $sessions.set({ s1: createSession('s1', 'Worker lane') });
    $activeSessionId.set('s1');
    $focusedSessionId.set('s1');
    sendSessionPrompt.mockResolvedValue(undefined);

    const result = getResult(
      await processToolRequest({
        type: 'tool_request',
        requestId: 'prompt-1',
        tool: 'send_prompt',
        args: { sessionId: 's1', text: 'Continue the test campaign.' },
      }),
    );

    expect(sendSessionPrompt).toHaveBeenCalledWith(
      's1',
      expect.objectContaining({
        text: 'Continue the test campaign.',
        mode: 'auto',
        submitKeys: ['Enter'],
      }),
    );
    expect(result).toMatchObject({
      success: true,
      sessionId: 's1',
      responseText: 'Prompt sent to Worker lane.',
    });
    expect(result.nextAction).toEqual(expect.stringContaining('wait_for_turn_completion'));
    expect(result.targetContext).toMatchObject({
      targetSessionId: 's1',
      targetSessionTitle: 'Worker lane',
      action: 'send_prompt',
      isTargetActive: true,
      isTargetFocused: true,
      isTargetInLayout: true,
    });
  });

  it('runs an agent turn with a spoken-ready completion summary', async () => {
    $sessions.set({ s1: createSession('s1', 'Worker lane') });
    $activeSessionId.set('s1');
    $focusedSessionId.set('s1');
    sendSessionPrompt.mockResolvedValue(undefined);
    getSessionAgentVibe.mockResolvedValue(
      createAgentVibe('s1', 'idle-prompt', 'Patch release finished.'),
    );

    const result = getResult(
      await processToolRequest({
        type: 'tool_request',
        requestId: 'agent-turn-1',
        tool: 'agent_turn',
        args: {
          sessionId: 's1',
          text: 'Ship the next patch release.',
          timeoutMs: 1000,
          pollIntervalMs: 500,
          justification: 'complete the feature run',
        },
      }),
    );

    expect(sendSessionPrompt).toHaveBeenCalledWith(
      's1',
      expect.objectContaining({
        text: 'Ship the next patch release.',
        mode: 'auto',
        submitKeys: ['Enter'],
      }),
    );
    expect(getSessionAgentVibe).toHaveBeenCalledWith('s1', 20, 120, 8);
    expect(result).toMatchObject({
      success: true,
      sessionId: 's1',
      promptSent: true,
      responseText: 'Worker lane appears done or idle. Latest signal: Patch release finished.',
      nextAction: 'Summarize the observed outcome in one or two short sentences.',
    });
    expect(result.turn).toMatchObject({
      success: true,
      status: 'complete',
      completed: true,
      timedOut: false,
    });
    expect(result.targetContext).toMatchObject({
      targetSessionId: 's1',
      targetSessionTitle: 'Worker lane',
      action: 'agent_turn',
      isTargetActive: true,
      isTargetFocused: true,
      isTargetInLayout: true,
    });
  });

  it('runs an agent turn against the focused session when no session id is supplied', async () => {
    $sessions.set({ s1: createSession('s1', 'Worker lane') });
    $activeSessionId.set('s1');
    $focusedSessionId.set('s1');
    sendSessionPrompt.mockResolvedValue(undefined);
    getSessionAgentVibe.mockResolvedValue(
      createAgentVibe('s1', 'idle-prompt', 'Focused turn finished.'),
    );

    const result = getResult(
      await processToolRequest({
        type: 'tool_request',
        requestId: 'agent-turn-focused',
        tool: 'agent_turn',
        args: {
          text: 'Continue the focused work.',
          timeoutMs: 1000,
          pollIntervalMs: 500,
          justification: 'continue focused session',
        },
      }),
    );

    expect(sendSessionPrompt).toHaveBeenCalledWith(
      's1',
      expect.objectContaining({
        text: 'Continue the focused work.',
        mode: 'auto',
        submitKeys: ['Enter'],
      }),
    );
    expect(result).toMatchObject({
      success: true,
      sessionId: 's1',
      requestedSessionId: null,
      resolvedTargetSource: 'focus_context',
      promptSent: true,
      responseText: 'Worker lane appears done or idle. Latest signal: Focused turn finished.',
    });
    expect(result.targetContext).toMatchObject({
      targetSessionId: 's1',
      targetSessionTitle: 'Worker lane',
      action: 'agent_turn',
      isTargetActive: true,
      isTargetFocused: true,
      isTargetInLayout: true,
    });
  });

  it('dispatches one campaign prompt to multiple exact sessions', async () => {
    $sessions.set({
      s1: createSession('s1', 'Worker one'),
      s2: createSession('s2', 'Worker two'),
    });
    $activeSessionId.set('s1');
    $focusedSessionId.set('s1');
    getLayoutSessionIds.mockReturnValue(['s1', 's2']);
    sendSessionPrompt.mockResolvedValue(undefined);

    const result = getResult(
      await processToolRequest({
        type: 'tool_request',
        requestId: 'campaign-dispatch-1',
        tool: 'campaign_dispatch',
        args: {
          sessionIds: ['s1', 's2', 'missing'],
          text: 'Run the next verification lane.',
          justification: 'fan out the test campaign',
        },
      }),
    );

    expect(sendSessionPrompt).toHaveBeenCalledTimes(2);
    expect(sendSessionPrompt).toHaveBeenNthCalledWith(
      1,
      's1',
      expect.objectContaining({
        text: 'Run the next verification lane.',
        mode: 'auto',
        submitKeys: ['Enter'],
      }),
    );
    expect(sendSessionPrompt).toHaveBeenNthCalledWith(
      2,
      's2',
      expect.objectContaining({
        text: 'Run the next verification lane.',
        mode: 'auto',
        submitKeys: ['Enter'],
      }),
    );
    expect(result).toMatchObject({
      success: true,
      requestedSessionIds: ['s1', 's2', 'missing'],
      dispatchedSessionIds: ['s1', 's2'],
      missingSessionIds: ['missing'],
      dispatchCount: 2,
      responseText: 'Dispatched campaign prompt to 2 sessions; 1 requested sessions were missing.',
    });
    expect(result.nextAction).toEqual(expect.stringContaining('campaign_report'));
    expect(result.targetContext).toMatchObject({
      targetSessionId: 's1',
      targetSessionTitle: 'Worker one',
      action: 'campaign_dispatch',
    });
  });

  it('gives terminal input tools spoken flow guidance', async () => {
    $sessions.set({ s1: createSession('s1', 'Terminal lane') });
    $activeSessionId.set('s1');
    $focusedSessionId.set('s1');

    const result = getResult(
      await processToolRequest({
        type: 'tool_request',
        requestId: 'input-1',
        tool: 'make_input',
        args: { sessionId: 's1', text: '{Enter}', delayMs: 0 },
      }),
    );

    expect(sendInput).toHaveBeenCalledWith('s1', '\r');
    expect(result).toMatchObject({
      success: true,
      responseText: 'Input sent to Terminal lane.',
      screenContent: '[terminal not in view - ask user to switch to this session to see content]',
    });
    expect(result.nextAction).toEqual(expect.stringContaining('wait_for_turn_completion'));
    expect(result.targetContext).toMatchObject({
      targetSessionId: 's1',
      targetSessionTitle: 'Terminal lane',
      isTargetActive: true,
      isTargetFocused: true,
      isTargetInLayout: true,
    });
  });

  it('preserves the session and preview target for Dev Browser status', async () => {
    $sessions.set({ s1: createSession('s1', 'Browser lane') });
    $activeSessionId.set('s1');
    $focusedSessionId.set('s1');
    getWebPreviewTarget.mockResolvedValue({
      sessionId: 's1',
      previewName: 'app',
      url: 'https://localhost:2100',
      active: true,
      targetRevision: 7,
    });
    getBrowserPreviewStatus.mockResolvedValue({
      connected: true,
      controllable: true,
      hasTarget: true,
      hasUiClient: true,
      isScoped: true,
      state: 'ready',
      connectedClientCount: 1,
      totalConnectedClientCount: 1,
      connectedUiClientCount: 1,
      ownerConnected: true,
      clients: [],
    });

    const result = getResult(
      await processToolRequest({
        type: 'tool_request',
        requestId: 'browser-status-1',
        tool: 'dev_browser_status',
        args: { sessionId: 's1', previewName: 'app', previewId: 'preview-1' },
      }),
    );

    expect(getWebPreviewTarget).toHaveBeenCalledWith('s1', 'app');
    expect(getBrowserPreviewStatus).toHaveBeenCalledWith('s1', 'app', 'preview-1');
    expect(result).toMatchObject({
      success: true,
      sessionId: 's1',
      previewName: 'app',
      responseText: 'Dev Browser app is ready for session s1.',
    });
    expect(result.nextAction).toEqual(expect.stringContaining('dev_browser_command'));
    expect(result.targetContext).toMatchObject({
      targetSessionId: 's1',
      targetSessionTitle: 'Browser lane',
      targetPreviewName: 'app',
      targetPreviewId: 'preview-1',
      action: 'dev_browser_status',
      isTargetActive: true,
      isTargetFocused: true,
      isTargetInLayout: true,
    });
  });

  it('exposes session turn summaries as spoken-ready response text', async () => {
    $sessions.set({ s1: createSession('s1', 'Worker lane') });
    $activeSessionId.set('s1');
    $focusedSessionId.set('s1');
    getSessionAgentVibe.mockResolvedValue(
      createAgentVibe('s1', 'idle-prompt', 'Tests passed and release is ready.'),
    );

    const result = getResult(
      await processToolRequest({
        type: 'tool_request',
        requestId: 'turn-summary-1',
        tool: 'session_turn_summary',
        args: { sessionId: 's1' },
      }),
    );

    expect(getSessionAgentVibe).toHaveBeenCalledWith('s1', 80, 90, 8);
    expect(result).toMatchObject({
      success: true,
      sessionId: 's1',
      status: 'complete',
      responseText:
        'Worker lane appears done or idle. Latest signal: Tests passed and release is ready.',
      summary:
        'Worker lane appears done or idle. Latest signal: Tests passed and release is ready.',
      nextAction: 'Summarize the observed outcome in one or two short sentences.',
    });
    expect(result.targetContext).toMatchObject({
      targetSessionId: 's1',
      targetSessionTitle: 'Worker lane',
      isTargetActive: true,
      isTargetFocused: true,
      isTargetInLayout: true,
    });
  });
});
