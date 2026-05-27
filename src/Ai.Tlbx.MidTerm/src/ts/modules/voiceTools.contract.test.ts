import { beforeEach, describe, expect, it, vi } from 'vitest';
import { $activeSessionId, $focusedSessionId, $sessions } from '../stores';
import type { Session } from '../types';

const sendSessionPrompt = vi.fn();
const sendInput = vi.fn();
const getSessionAgentVibe = vi.fn();
const getWebPreviewTarget = vi.fn();
const getBrowserPreviewStatus = vi.fn();
const requestSelectSession = vi.fn();
const getLayoutSessionIds = vi.fn(() => ['s1']);

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
    $sessions.set({});
    $activeSessionId.set(null);
    $focusedSessionId.set(null);
    getLayoutSessionIds.mockReturnValue(['s1']);
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
