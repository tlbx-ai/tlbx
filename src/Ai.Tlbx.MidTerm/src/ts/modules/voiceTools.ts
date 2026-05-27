/**
 * Voice Tools Module
 *
 * Handles tool requests from the voice assistant server.
 * Tools execute locally in the browser using xterm.js buffers and stores.
 */
/* eslint-disable max-lines -- Voice bridge groups tool handlers until the module is split. */

import { createLogger } from './logging';
import { sendInput } from './comms';
import { requestSelectSession } from './comms/stateChannel';
import { sessionTerminals } from '../state';
import {
  $activeSessionId,
  $focusedSessionId,
  $layout,
  $sessionList,
  $updateInfo,
  getSession,
} from '../stores';
import {
  ApiProblemError,
  createSession as apiCreateSession,
  deleteSession as apiDeleteSession,
  getSessionAgentFeed,
  getSessionAgentVibe,
  getHistory,
  sendSessionPrompt,
} from '../api/client';
import {
  captureBrowserScreenshotRaw,
  getBrowserPreviewStatus,
  getWebPreviewTarget,
  runBrowserCommand,
  setWebPreviewTarget,
} from './web/webApi';
import { selectActivePreview } from './web';
import { uploadFile } from './terminal/fileDrop';
import { addGitRepo, fetchGitRepos, refreshGitRepo, removeGitRepo } from './git/gitApi';
import { applyGitReposForSession, getCachedGitReposForSession } from './git';
import {
  dockSession,
  focusLayoutSession,
  getLayoutSessionIds,
  isSessionInLayout,
  swapLayoutSessions,
  undockSession,
} from './layout/layoutStore';
import type {
  VoiceToolRequest,
  VoiceToolResponse,
  VoiceToolName,
  MakeInputArgs,
  ReadScrollbackArgs,
  InteractiveReadArgs,
  CreateSessionArgs,
  SelectSessionArgs,
  SendPromptArgs,
  SessionOverviewArgs,
  ConversationContinuityArgs,
  CampaignStatusArgs,
  SessionActivityArgs,
  SessionTurnSummaryArgs,
  WaitForTurnCompletionArgs,
  DevBrowserOpenArgs,
  DevBrowserStatusArgs,
  DevBrowserCommandArgs,
  DevBrowserScreenshotArgs,
  RepoMonitorArgs,
  LayoutControlArgs,
  CloseSessionArgs,
  BookmarksArgs,
  StateOfThingsResult,
  SessionOverviewResult,
  ConversationContinuityResult,
  CampaignStatusResult,
  WaitForTurnCompletionResult,
  VoicePreviewOverview,
  VoiceSessionState,
  MakeInputResult,
  ReadScrollbackResult,
  InteractiveReadResult,
  InteractiveOpResult,
  BellNotification,
  DockPosition,
  Session,
  VoiceTargetContext,
} from '../types';
import type { AgentSessionVibeResponse } from '../api/types';
import type { GitRepoBinding } from './git/types';
import { JS_BUILD_VERSION } from '../constants';

const log = createLogger('voiceTools');

const recentBells: BellNotification[] = [];
const DEFAULT_PREVIEW_NAME = 'default';
const LAYOUT_DOCK_POSITIONS = new Set<DockPosition>(['top', 'bottom', 'left', 'right']);
const BROWSER_COMMANDS = new Set([
  'query',
  'click',
  'scroll',
  'fill',
  'exec',
  'wait',
  'navigate',
  'reload',
  'outline',
  'attrs',
  'css',
  'log',
  'links',
  'submit',
  'forms',
  'url',
  'status',
]);
type SessionTurnStatus = 'complete' | 'busy' | 'needs_user' | 'blocked' | 'shell' | 'unknown';

function getSessionLaunchErrorMessage(error: unknown): string {
  if (error instanceof ApiProblemError) {
    if (error.errorDetails) {
      return `${error.detail}\n\n${error.errorDetails}`;
    }

    return error.detail || error.title || error.message;
  }

  return error instanceof Error ? error.message : String(error);
}

function getReadableSessionTitle(session: Session | null | undefined, fallback: string): string {
  return session?.name || session?.terminalTitle || session?.foregroundName || fallback;
}

interface VoiceTargetContextArgs {
  sessionId?: string | null | undefined;
  previewName?: string | null | undefined;
  previewId?: string | null | undefined;
  repoRoot?: string | null | undefined;
  action?: string | null | undefined;
}

function normalizeTargetValue(value: string | null | undefined): string | null {
  return value?.trim() || null;
}

function buildVoiceTargetContext(args: VoiceTargetContextArgs = {}): VoiceTargetContext {
  const targetSessionId = normalizeTargetValue(args.sessionId);
  const targetPreviewName = normalizeTargetValue(args.previewName);
  const targetPreviewId = normalizeTargetValue(args.previewId);
  const targetRepoRoot = normalizeTargetValue(args.repoRoot);
  const action = normalizeTargetValue(args.action);
  const targetSession = targetSessionId ? getSession(targetSessionId) : null;
  const activeSessionId = $activeSessionId.get();
  const focusedSessionId = $focusedSessionId.get();
  const layoutSessionIds = getLayoutSessionIds();
  return {
    activeSessionId,
    focusedSessionId,
    layoutSessionIds,
    targetSessionId,
    targetSessionTitle: targetSession
      ? getReadableSessionTitle(targetSession, targetSessionId ?? targetSession.id)
      : null,
    targetSessionExists: targetSessionId ? Boolean(targetSession) : null,
    targetPreviewName,
    targetPreviewId,
    targetRepoRoot,
    action,
    isTargetActive: targetSessionId ? targetSessionId === activeSessionId : null,
    isTargetFocused: targetSessionId ? targetSessionId === focusedSessionId : null,
    isTargetInLayout: targetSessionId ? layoutSessionIds.includes(targetSessionId) : null,
  };
}

/**
 * Get only the visible viewport content (cols x rows).
 * Returns descriptive message if terminal isn't rendered yet.
 */
function getTerminalViewport(sessionId: string): string {
  const termState = sessionTerminals.get(sessionId);
  if (!termState?.terminal) {
    return '[terminal not in view - ask user to switch to this session to see content]';
  }

  const terminal = termState.terminal;
  const buffer = terminal.buffer.active;
  const viewportStart = buffer.baseY;
  const rows = terminal.rows;
  const lines: string[] = [];

  for (let i = 0; i < rows; i++) {
    const lineIndex = viewportStart + i;
    const line = buffer.getLine(lineIndex);
    if (line) {
      lines.push(line.translateToString(true));
    }
  }

  return lines.join('\n');
}

/**
 * Named key map for {Key} syntax expansion.
 * Maps case-insensitive key names to terminal escape sequences.
 */
const NAMED_KEYS: Record<string, string> = {
  enter: '\r',
  tab: '\t',
  escape: '\x1b',
  esc: '\x1b',
  backspace: '\x7f',
  delete: '\x1b[3~',
  up: '\x1b[A',
  down: '\x1b[B',
  right: '\x1b[C',
  left: '\x1b[D',
  home: '\x1b[H',
  end: '\x1b[F',
  pageup: '\x1b[5~',
  pagedown: '\x1b[6~',
  space: ' ',
  'shift+tab': '\x1b[Z',
};

/**
 * Expand {Key} named syntax into terminal escape sequences.
 * Supports: {Enter}, {Ctrl+C}, {Alt+F}, {Shift+Tab}, etc.
 * Unrecognized keys are left as-is (literal text).
 */
function expandNamedKeys(text: string): string {
  return text.replace(/\{([^}]+)\}/g, (match, keyName: string) => {
    const lower = keyName.toLowerCase().trim();

    if (NAMED_KEYS[lower] !== undefined) {
      return NAMED_KEYS[lower];
    }

    const ctrlMatch = lower.match(/^ctrl\+([a-z])$/);
    if (ctrlMatch && ctrlMatch[1]) {
      const code = ctrlMatch[1].charCodeAt(0) - 96;
      return String.fromCharCode(code);
    }

    const altMatch = lower.match(/^alt\+(.+)$/);
    if (altMatch && altMatch[1]) {
      const altKey = altMatch[1];
      if (altKey === 'enter') return '\x1b\r';
      if (altKey.length === 1) return '\x1b' + altKey;
    }

    return match;
  });
}

/**
 * Parse escape sequences in text.
 * Handles: \r \n \t \x## (hex byte)
 */
function parseEscapeSequences(text: string): string {
  return text
    .replace(/\\r/g, '\r')
    .replace(/\\n/g, '\n')
    .replace(/\\t/g, '\t')
    .replace(/\\x([0-9a-fA-F]{2})/g, (_, hex: string) => String.fromCharCode(parseInt(hex, 16)));
}

/**
 * Sleep helper for delays between operations.
 */
function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function resolveSessionId(sessionId?: string | null): string | null {
  return sessionId?.trim() || $activeSessionId.get();
}

function resolvePreviewName(previewName?: string | null): string {
  return previewName?.trim() || DEFAULT_PREVIEW_NAME;
}

function compactGitRepo(repo: GitRepoBinding): Record<string, unknown> {
  const base = {
    repoRoot: repo.repoRoot,
    label: repo.label,
    role: repo.role,
    source: repo.source,
    isPrimary: repo.isPrimary,
  };
  const status = repo.status;
  if (!status) {
    return {
      ...base,
      branch: null,
      ahead: 0,
      behind: 0,
      staged: 0,
      modified: 0,
      untracked: 0,
      conflicted: 0,
      stashCount: 0,
      totalAdditions: 0,
      totalDeletions: 0,
    };
  }

  return {
    ...base,
    branch: status.branch,
    ahead: status.ahead,
    behind: status.behind,
    staged: status.staged.length,
    modified: status.modified.length,
    untracked: status.untracked.length,
    conflicted: status.conflicted.length,
    stashCount: status.stashCount,
    totalAdditions: status.totalAdditions,
    totalDeletions: status.totalDeletions,
  };
}

function compactGitRepos(repos: GitRepoBinding[]): unknown[] {
  return repos.map(compactGitRepo);
}

async function selectSessionForBrowser(sessionId: string, previewName: string): Promise<void> {
  if ($activeSessionId.get() !== sessionId) {
    requestSelectSession(sessionId, {
      closeSettingsPanel: false,
      focusTerminal: false,
    });
    await sleep(50);
  }

  await selectActivePreview(previewName);
}

/**
 * Handle state_of_things tool - get comprehensive state of all terminals.
 */
function handleStateOfThings(): StateOfThingsResult {
  const sessions = $sessionList.get();
  const activeId = $activeSessionId.get();
  const updateInfo = $updateInfo.get();

  const sessionStates: VoiceSessionState[] = sessions.map((s) => ({
    id: s.id,
    userTitle: s.name || null,
    terminalTitle: s.terminalTitle || null,
    foregroundName: s.foregroundName ?? null,
    foregroundCommandLine: s.foregroundCommandLine ?? null,
    currentDirectory: s.currentDirectory || null,
    shell: s.shellType,
    cols: s.cols,
    rows: s.rows,
    isRunning: true,
    isActive: s.id === activeId,
    screenContent: getTerminalViewport(s.id),
  }));

  return {
    sessions: sessionStates,
    activeSessionId: activeId,
    targetContext: buildVoiceTargetContext(),
    version: JS_BUILD_VERSION,
    updateAvailable: updateInfo?.available ?? false,
    recentBells: [...recentBells],
  };
}

/**
 * Handle session_overview tool - compact orientation map for switching and multi-session control.
 */
async function handleSessionOverview(args: SessionOverviewArgs): Promise<SessionOverviewResult> {
  const sessions = $sessionList.get();
  const activeSessionId = $activeSessionId.get();
  const focusedSessionId = $focusedSessionId.get();
  const layoutSessionIds = getLayoutSessionIds();
  const layoutSessionSet = new Set(layoutSessionIds);
  const includeBrowserStatus = args.includeBrowserStatus ?? true;
  const includeRepoStatus = args.includeRepoStatus ?? true;
  const updateInfo = $updateInfo.get();

  const sessionSummaries = await Promise.all(
    sessions.map((session) =>
      buildSessionOverview(session, {
        activeSessionId,
        focusedSessionId,
        layoutSessionSet,
        includeBrowserStatus,
        includeRepoStatus,
      }),
    ),
  );

  return {
    success: true,
    activeSessionId,
    focusedSessionId,
    layoutSessionIds,
    targetContext: buildVoiceTargetContext({ sessionId: activeSessionId }),
    version: JS_BUILD_VERSION,
    updateAvailable: updateInfo?.available ?? false,
    sessions: sessionSummaries,
  };
}

async function buildDefaultPreviewOverview(
  sessionId: string,
): Promise<VoicePreviewOverview | null> {
  const [target, status] = await Promise.all([
    getWebPreviewTarget(sessionId, DEFAULT_PREVIEW_NAME).catch(() => null),
    getBrowserPreviewStatus(sessionId, DEFAULT_PREVIEW_NAME).catch(() => null),
  ]);
  return {
    previewName: DEFAULT_PREVIEW_NAME,
    url: target?.url ?? null,
    state: status?.state ?? null,
    ready: status?.state === 'ready',
  };
}

async function buildSessionOverview(
  session: Session,
  options: {
    activeSessionId: string | null;
    focusedSessionId: string | null;
    layoutSessionSet: Set<string>;
    includeBrowserStatus: boolean;
    includeRepoStatus: boolean;
  },
): Promise<SessionOverviewResult['sessions'][number]> {
  const title = session.name || session.terminalTitle || session.shellType || session.id;
  const summary: SessionOverviewResult['sessions'][number] = {
    id: session.id,
    title,
    userTitle: session.name || null,
    terminalTitle: session.terminalTitle || null,
    foregroundName: session.foregroundName ?? null,
    currentDirectory: session.currentDirectory || null,
    shell: session.shellType,
    isActive: session.id === options.activeSessionId,
    isFocused: session.id === options.focusedSessionId,
    isInLayout: options.layoutSessionSet.has(session.id),
    hasRenderedTerminal: Boolean(sessionTerminals.get(session.id)?.terminal),
    defaultPreview: options.includeBrowserStatus
      ? await buildDefaultPreviewOverview(session.id)
      : null,
  };

  if (options.includeRepoStatus) {
    summary.repos = compactGitRepos(getCachedGitReposForSession(session.id));
  }

  return summary;
}

/**
 * Handle make_input tool - send input to a terminal and capture result.
 */
async function handleMakeInput(args: MakeInputArgs): Promise<MakeInputResult> {
  const { sessionId, text, delayMs = 100 } = args;

  const session = getSession(sessionId);
  if (!session) {
    return {
      success: false,
      screenContent: `Session ${sessionId} not found`,
      cols: 0,
      rows: 0,
      targetContext: buildVoiceTargetContext({ sessionId }),
    };
  }

  const parsedText = parseEscapeSequences(expandNamedKeys(text));
  sendInput(sessionId, parsedText);

  await sleep(delayMs);

  return {
    success: true,
    screenContent: getTerminalViewport(sessionId),
    cols: session.cols,
    rows: session.rows,
    targetContext: buildVoiceTargetContext({ sessionId }),
  };
}

/**
 * Handle read_scrollback tool - read lines from terminal scrollback.
 */
function handleReadScrollback(args: ReadScrollbackArgs): ReadScrollbackResult {
  const { sessionId, start = 'bottom', lines = 40 } = args;

  const termState = sessionTerminals.get(sessionId);
  if (!termState?.terminal) {
    return {
      content: `Session ${sessionId} not found`,
      totalLines: 0,
      returnedLines: 0,
      startLine: 0,
      targetContext: buildVoiceTargetContext({ sessionId }),
    };
  }

  const buffer = termState.terminal.buffer.active;
  const totalLines = buffer.length;
  const requestedLines = Math.min(lines, 500);

  let startLine: number;
  if (start === 'bottom') {
    startLine = Math.max(0, totalLines - requestedLines);
  } else {
    startLine = Math.max(0, Math.min(parseInt(start, 10) || 0, totalLines - 1));
  }

  const endLine = Math.min(startLine + requestedLines, totalLines);
  const extractedLines: string[] = [];

  for (let i = startLine; i < endLine; i++) {
    const line = buffer.getLine(i);
    if (line) {
      extractedLines.push(line.translateToString(true));
    }
  }

  return {
    content: extractedLines.join('\n'),
    totalLines,
    returnedLines: extractedLines.length,
    startLine,
    targetContext: buildVoiceTargetContext({ sessionId }),
  };
}

/**
 * Handle interactive_read tool - execute operation sequences for TUI navigation.
 */
async function handleInteractiveRead(args: InteractiveReadArgs): Promise<InteractiveReadResult> {
  const { sessionId, operations } = args;

  const session = getSession(sessionId);
  if (!session) {
    return {
      results: [{ index: 0, success: false, screenshot: `Session ${sessionId} not found` }],
      targetContext: buildVoiceTargetContext({ sessionId }),
    };
  }

  const results: InteractiveOpResult[] = [];

  for (let i = 0; i < operations.length; i++) {
    const op = operations[i];
    if (!op) continue;

    try {
      switch (op.type) {
        case 'input':
          if (op.data) {
            const parsed = parseEscapeSequences(expandNamedKeys(op.data));
            sendInput(sessionId, parsed);
          }
          results.push({ index: i, success: true });
          break;

        case 'delay':
          await sleep(op.delayMs ?? 100);
          results.push({ index: i, success: true });
          break;

        case 'screenshot':
          results.push({
            index: i,
            success: true,
            screenshot: getTerminalViewport(sessionId),
          });
          break;

        default:
          results.push({ index: i, success: false });
      }
    } catch (err) {
      log.error(() => `Operation ${i} failed: ${String(err)}`);
      results.push({ index: i, success: false });
    }
  }

  return { results, targetContext: buildVoiceTargetContext({ sessionId }) };
}

/**
 * Handle create_session tool - create a new terminal session.
 */
async function handleCreateSession(args: CreateSessionArgs): Promise<unknown> {
  try {
    const sessions = $sessionList.get();
    const refSession = sessions[0];
    const cols = refSession?.cols ?? 120;
    const rows = refSession?.rows ?? 30;

    const { data } = await apiCreateSession({
      cols,
      rows,
      shell: args.shellType ?? null,
      workingDirectory: args.workingDirectory ?? null,
    });
    if (!data) {
      return {
        success: false,
        error: 'Failed to create session',
        targetContext: buildVoiceTargetContext(),
        responseText: 'Session creation failed.',
        nextAction:
          'Inspect the MidTerm session list or retry with a specific shell and working directory.',
      };
    }

    const title = getReadableSessionTitle(getSession(data.id), data.id);
    return {
      success: true,
      sessionId: data.id,
      shell: data.shellType,
      targetContext: buildVoiceTargetContext({ sessionId: data.id, action: 'create_session' }),
      responseText: `Created session ${title}.`,
      nextAction: `Use select_session with sessionId ${data.id} if the user should look at or operate it next.`,
    };
  } catch (error) {
    const message = getSessionLaunchErrorMessage(error);
    return {
      success: false,
      error: message,
      targetContext: buildVoiceTargetContext(),
      responseText: `Session creation failed: ${message}.`,
      nextAction:
        'Ask for a different shell or working directory, or inspect the MidTerm session list.',
    };
  }
}

/**
 * Handle select_session tool - bring a session into the active MidTerm surface.
 */
function handleSelectSession(args: SelectSessionArgs): unknown {
  const session = getSession(args.sessionId);
  if (!session) {
    return {
      success: false,
      error: `Session ${args.sessionId} not found`,
      targetContext: buildVoiceTargetContext({ sessionId: args.sessionId }),
      responseText: `Session ${args.sessionId} was not found.`,
      nextAction: 'Call session_overview to discover valid session IDs before switching again.',
    };
  }

  requestSelectSession(args.sessionId, {
    closeSettingsPanel: false,
    focusTerminal: args.focusTerminal ?? true,
  });

  return {
    success: true,
    activeSessionId: args.sessionId,
    title: getReadableSessionTitle(session, args.sessionId),
    targetContext: buildVoiceTargetContext({
      sessionId: args.sessionId,
      action: 'select_session',
    }),
    responseText: `Selected ${getReadableSessionTitle(session, args.sessionId)}.`,
    nextAction:
      'Continue operating this session, or summarize its turn state before reporting completion.',
  };
}

/**
 * Handle send_prompt tool - send a structured prompt to an agent session.
 */
async function handleSendPrompt(args: SendPromptArgs): Promise<unknown> {
  const session = getSession(args.sessionId);
  if (!session) {
    return {
      success: false,
      error: `Session ${args.sessionId} not found`,
      targetContext: buildVoiceTargetContext({ sessionId: args.sessionId }),
      responseText: `Session ${args.sessionId} was not found.`,
      nextAction:
        'Call session_overview to get the current session IDs, then resend the prompt to the correct session.',
    };
  }

  await sendSessionPrompt(args.sessionId, {
    text: args.text,
    base64: null,
    mode: 'auto',
    interruptFirst: args.interruptFirst ?? false,
    interruptKeys: ['C-c'],
    literalInterruptKeys: false,
    interruptDelayMs: 150,
    submitKeys: ['Enter'],
    literalSubmitKeys: false,
    submitDelayMs: 300,
    followupSubmitCount: 0,
    followupSubmitDelayMs: 250,
    profile: args.profile ?? null,
  });

  return {
    success: true,
    sessionId: args.sessionId,
    interruptFirst: args.interruptFirst ?? false,
    targetContext: buildVoiceTargetContext({ sessionId: args.sessionId, action: 'send_prompt' }),
    responseText: `Prompt sent to ${getReadableSessionTitle(session, args.sessionId)}.`,
    nextAction:
      'Call session_turn_summary; if it is busy and the user is waiting, call wait_for_turn_completion once.',
  };
}

/**
 * Handle session_activity tool - fetch structured agent/feed signals for turn flow.
 */
async function handleSessionActivity(args: SessionActivityArgs): Promise<unknown> {
  const sessions = args.sessionId
    ? $sessionList.get().filter((session) => session.id === args.sessionId)
    : $sessionList.get();

  if (args.sessionId && sessions.length === 0) {
    return {
      success: false,
      error: `Session ${args.sessionId} not found`,
      targetContext: buildVoiceTargetContext({ sessionId: args.sessionId }),
    };
  }

  const tailLines = Math.max(20, Math.min(args.tailLines ?? 80, 200));
  const activitySeconds = Math.max(15, Math.min(args.activitySeconds ?? 90, 600));
  const bellLimit = Math.max(0, Math.min(args.bellLimit ?? 8, 25));
  const feeds = await Promise.all(
    sessions.map(async (session) => {
      const feed = await getSessionAgentFeed(session.id, tailLines, activitySeconds, bellLimit);
      return {
        sessionId: session.id,
        title: session.name || session.terminalTitle || null,
        isActive: session.id === $activeSessionId.get(),
        foregroundName: session.foregroundName ?? null,
        currentDirectory: session.currentDirectory || null,
        feed,
      };
    }),
  );

  return {
    success: true,
    targetContext: buildVoiceTargetContext({ sessionId: args.sessionId }),
    feeds,
  };
}

function classifyTurnStatus(vibe: AgentSessionVibeResponse): SessionTurnStatus {
  const state = vibe.header.state.toLowerCase();
  const attentionReason = vibe.header.attentionReason?.toLowerCase() ?? '';
  const stateMeta = vibe.overview.stateMeta.toLowerCase();
  const chips = vibe.header.chips.map((chip) => chip.text.toLowerCase()).join(' ');

  if (state.includes('error') || state === 'blocked') {
    return 'blocked';
  }

  if (
    vibe.header.needsAttention ||
    attentionReason.includes('prompt') ||
    stateMeta.includes('waiting for input') ||
    chips.includes('waiting for input')
  ) {
    return 'needs_user';
  }

  if (state === 'busy-turn' || state === 'running') {
    return 'busy';
  }

  if (state === 'shell') {
    return 'shell';
  }

  if (state === 'idle-prompt' || state === 'idle' || state === 'completed') {
    return 'complete';
  }

  return 'unknown';
}

function buildTurnSummaryText(vibe: AgentSessionVibeResponse, status: SessionTurnStatus): string {
  const latest = vibe.activities[0];
  const title = vibe.header.title || vibe.header.providerLabel || vibe.sessionId;
  const state = vibe.header.stateLabel || vibe.header.state;
  const reason = vibe.header.attentionReason || latest?.summary || state;
  const latestSignal = latest?.summary || state;

  const templates: Record<SessionTurnStatus, string> = {
    needs_user: `${title} needs user attention: ${reason}.`,
    blocked: `${title} is blocked: ${reason}.`,
    busy: `${title} is still working. Latest signal: ${latest?.summary || vibe.overview.activityMeta}.`,
    shell: `${title} is at a shell, not inside an active agent turn.`,
    complete: `${title} appears done or idle. Latest signal: ${latestSignal}.`,
    unknown: `${title} state is unclear. Latest signal: ${latestSignal}.`,
  };

  return templates[status];
}

function buildNextAction(status: SessionTurnStatus): string {
  if (status === 'busy') {
    return 'Call wait_for_turn_completion before speaking as if the turn finished.';
  }

  if (status === 'needs_user') {
    return 'Tell the user what input or approval the session needs, then stop.';
  }

  if (status === 'blocked') {
    return 'Report the blocker and propose one concrete recovery step.';
  }

  if (status === 'shell') {
    return 'Ask whether to start or resume an agent workflow before sending agent prompts.';
  }

  if (status === 'complete') {
    return 'Summarize the observed outcome in one or two short sentences.';
  }

  return 'Use session_activity for deeper evidence or ask one clarification question.';
}

function isSettledTurnStatus(status: SessionTurnStatus): boolean {
  return (
    status === 'complete' || status === 'needs_user' || status === 'blocked' || status === 'shell'
  );
}

/**
 * Handle session_turn_summary tool - compact turn lifecycle summary for voice flow.
 */
async function handleSessionTurnSummary(args: SessionTurnSummaryArgs): Promise<unknown> {
  const session = getSession(args.sessionId);
  if (!session) {
    return {
      success: false,
      error: `Session ${args.sessionId} not found`,
      targetContext: buildVoiceTargetContext({ sessionId: args.sessionId }),
    };
  }

  const tailLines = Math.max(20, Math.min(args.tailLines ?? 80, 200));
  const activitySeconds = Math.max(15, Math.min(args.activitySeconds ?? 90, 600));
  const bellLimit = Math.max(0, Math.min(args.bellLimit ?? 8, 25));
  const vibe = await getSessionAgentVibe(args.sessionId, tailLines, activitySeconds, bellLimit);
  const status = classifyTurnStatus(vibe);
  const latestActivities = vibe.activities.slice(0, 5).map((activity) => ({
    tone: activity.tone,
    kind: activity.kind,
    summary: activity.summary,
    detail: activity.detail,
    createdAt: activity.createdAt,
  }));

  return {
    success: true,
    sessionId: args.sessionId,
    title: session.name || session.terminalTitle || vibe.header.title,
    targetContext: buildVoiceTargetContext({ sessionId: args.sessionId }),
    status,
    state: vibe.header.state,
    stateLabel: vibe.header.stateLabel,
    needsAttention: vibe.header.needsAttention,
    attentionReason: vibe.header.attentionReason,
    summary: buildTurnSummaryText(vibe, status),
    nextAction: buildNextAction(status),
    overview: vibe.overview,
    latestActivities,
    tailText: vibe.terminal.tailText,
  };
}

function buildWaitResponseText(
  title: string,
  status: SessionTurnStatus,
  summary: string,
  elapsedMs: number,
  timedOut: boolean,
): string {
  if (!timedOut) {
    return summary;
  }

  const seconds = Math.round(elapsedMs / 1000);
  if (status === 'busy') {
    return `${title} is still working after ${seconds} seconds.`;
  }

  return `${title} did not settle clearly within ${seconds} seconds. Latest signal: ${summary}`;
}

async function getWaitVibe(
  sessionId: string,
  includeTail: boolean,
  activitySeconds: number,
): Promise<{ vibe: AgentSessionVibeResponse; status: SessionTurnStatus }> {
  const vibe = await getSessionAgentVibe(sessionId, includeTail ? 80 : 20, activitySeconds, 8);
  return { vibe, status: classifyTurnStatus(vibe) };
}

function buildWaitResult(
  args: WaitForTurnCompletionArgs,
  session: Session,
  vibe: AgentSessionVibeResponse,
  status: SessionTurnStatus,
  elapsedMs: number,
  pollCount: number,
  includeTail: boolean,
): WaitForTurnCompletionResult {
  const timedOut = !isSettledTurnStatus(status);
  const title = session.name || session.terminalTitle || vibe.header.title || args.sessionId;
  const summary = buildTurnSummaryText(vibe, status);
  const latestActivities = vibe.activities.slice(0, 5).map((activity) => ({
    tone: activity.tone,
    kind: activity.kind,
    summary: activity.summary,
    detail: activity.detail,
    createdAt: activity.createdAt,
  }));

  const result: WaitForTurnCompletionResult = {
    success: true,
    sessionId: args.sessionId,
    title,
    targetContext: buildVoiceTargetContext({ sessionId: args.sessionId }),
    completed: status === 'complete',
    timedOut,
    status,
    state: vibe.header.state,
    stateLabel: vibe.header.stateLabel,
    elapsedMs,
    pollCount,
    settledAt: new Date().toISOString(),
    responseText: buildWaitResponseText(title, status, summary, elapsedMs, timedOut),
    summary,
    nextAction: timedOut
      ? 'Tell the user the session is still working and offer to keep watching.'
      : buildNextAction(status),
    needsAttention: vibe.header.needsAttention,
    attentionReason: vibe.header.attentionReason,
    latestActivities,
  };

  if (includeTail) {
    result.tailText = vibe.terminal.tailText;
  }

  return result;
}

/**
 * Handle wait_for_turn_completion tool - poll a session until the current turn settles.
 */
async function handleWaitForTurnCompletion(
  args: WaitForTurnCompletionArgs,
): Promise<
  WaitForTurnCompletionResult | { success: false; error: string; targetContext: VoiceTargetContext }
> {
  const session = getSession(args.sessionId);
  if (!session) {
    return {
      success: false,
      error: `Session ${args.sessionId} not found`,
      targetContext: buildVoiceTargetContext({ sessionId: args.sessionId }),
    };
  }

  const timeoutMs = Math.max(1000, Math.min(args.timeoutMs ?? 45000, 90000));
  const pollIntervalMs = Math.max(500, Math.min(args.pollIntervalMs ?? 2000, 10000));
  const activitySeconds = Math.max(15, Math.min(args.activitySeconds ?? 120, 600));
  const includeTail = args.includeTail ?? false;
  const startedAt = Date.now();
  let pollCount = 0;
  let latestVibe: AgentSessionVibeResponse | null = null;
  let latestStatus: SessionTurnStatus = 'unknown';

  while (Date.now() - startedAt <= timeoutMs) {
    pollCount += 1;
    const current = await getWaitVibe(args.sessionId, includeTail, activitySeconds);
    latestVibe = current.vibe;
    latestStatus = current.status;

    if (isSettledTurnStatus(latestStatus)) {
      break;
    }

    const remainingMs = timeoutMs - (Date.now() - startedAt);
    if (remainingMs <= 0) {
      break;
    }

    await sleep(Math.min(pollIntervalMs, remainingMs));
  }

  if (!latestVibe) {
    const current = await getWaitVibe(args.sessionId, includeTail, activitySeconds);
    latestVibe = current.vibe;
    latestStatus = current.status;
    pollCount += 1;
  }

  const elapsedMs = Date.now() - startedAt;
  return buildWaitResult(
    args,
    session,
    latestVibe,
    latestStatus,
    elapsedMs,
    pollCount,
    includeTail,
  );
}

function resolveContinuitySessions(args: ConversationContinuityArgs): {
  scope: 'active' | 'all';
  sessions: Session[];
  error?: string;
} {
  const requestedScope = args.scope === 'all' ? 'all' : 'active';
  if (args.sessionId?.trim()) {
    const session = getSession(args.sessionId.trim());
    return session
      ? { scope: 'active', sessions: [session] }
      : { scope: 'active', sessions: [], error: `Session ${args.sessionId} not found` };
  }

  if (requestedScope === 'all') {
    return { scope: 'all', sessions: $sessionList.get().slice(0, 10) };
  }

  const activeSessionId = $activeSessionId.get();
  const activeSession = activeSessionId ? getSession(activeSessionId) : null;
  return activeSession
    ? { scope: 'active', sessions: [activeSession] }
    : { scope: 'active', sessions: [], error: 'No active session and no sessionId provided' };
}

async function buildContinuitySession(
  session: Session,
  activitySeconds: number,
  includeTail: boolean,
): Promise<ConversationContinuityResult['sessions'][number]> {
  const vibe = await getSessionAgentVibe(session.id, includeTail ? 80 : 20, activitySeconds, 8);
  const status = classifyTurnStatus(vibe);
  const latestActivities = vibe.activities.slice(0, 4).map((activity) => ({
    tone: activity.tone,
    kind: activity.kind,
    summary: activity.summary,
    detail: activity.detail,
    createdAt: activity.createdAt,
  }));
  const result: ConversationContinuityResult['sessions'][number] = {
    sessionId: session.id,
    title: session.name || session.terminalTitle || vibe.header.title || session.id,
    isActive: session.id === $activeSessionId.get(),
    status,
    state: vibe.header.state,
    stateLabel: vibe.header.stateLabel,
    needsAttention: vibe.header.needsAttention,
    attentionReason: vibe.header.attentionReason,
    summary: buildTurnSummaryText(vibe, status),
    nextAction: buildNextAction(status),
    latestActivities,
  };

  if (includeTail) {
    result.tailText = vibe.terminal.tailText;
  }

  return result;
}

function buildContinuityResponseText(sessions: ConversationContinuityResult['sessions']): string {
  const attention = sessions.find((session) => session.needsAttention);
  if (attention) {
    return `${attention.title} needs input: ${attention.attentionReason || attention.summary}`;
  }

  const blocked = sessions.find((session) => session.status === 'blocked');
  if (blocked) {
    return `${blocked.title} is blocked: ${blocked.attentionReason || blocked.summary}`;
  }

  const busy = sessions.find((session) => session.status === 'busy');
  if (busy) {
    return `${busy.title} is still working. ${busy.summary}`;
  }

  const complete = sessions.find((session) => session.status === 'complete');
  if (complete) {
    return complete.summary;
  }

  return sessions[0]?.summary ?? 'No matching MidTerm session is available.';
}

/**
 * Handle conversation_continuity tool - compact handoff packet for voice flow.
 */
async function handleConversationContinuity(
  args: ConversationContinuityArgs,
): Promise<ConversationContinuityResult> {
  const resolved = resolveContinuitySessions(args);
  if (resolved.error) {
    return {
      success: false,
      scope: resolved.scope,
      activeSessionId: $activeSessionId.get(),
      targetContext: buildVoiceTargetContext({ sessionId: args.sessionId }),
      generatedAt: new Date().toISOString(),
      responseText: resolved.error,
      nextAction:
        'Ask the user which session to inspect, or call session_overview to list sessions.',
      sessions: [],
      attentionSessionIds: [],
      busySessionIds: [],
      completeSessionIds: [],
    };
  }

  const activitySeconds = Math.max(15, Math.min(args.activitySeconds ?? 120, 600));
  const includeTail = args.includeTail ?? false;
  const sessions = await Promise.all(
    resolved.sessions.map((session) =>
      buildContinuitySession(session, activitySeconds, includeTail),
    ),
  );

  const attentionSessionIds = sessions
    .filter((session) => session.needsAttention || session.status === 'blocked')
    .map((session) => session.sessionId);
  const busySessionIds = sessions
    .filter((session) => session.status === 'busy')
    .map((session) => session.sessionId);
  const completeSessionIds = sessions
    .filter((session) => session.status === 'complete')
    .map((session) => session.sessionId);
  const responseText = buildContinuityResponseText(sessions);

  return {
    success: true,
    scope: resolved.scope,
    activeSessionId: $activeSessionId.get(),
    targetContext: buildVoiceTargetContext({
      sessionId: resolved.sessions[0]?.id ?? $activeSessionId.get(),
    }),
    generatedAt: new Date().toISOString(),
    responseText,
    nextAction:
      sessions.find((session) => session.needsAttention || session.status === 'blocked')
        ?.nextAction ??
      sessions.find((session) => session.status === 'busy')?.nextAction ??
      'Speak responseText briefly, then stop.',
    sessions,
    attentionSessionIds,
    busySessionIds,
    completeSessionIds,
  };
}

function resolveCampaignSessions(args: CampaignStatusArgs): {
  scope: CampaignStatusResult['scope'];
  sessions: Session[];
  errors: string[];
} {
  if (args.sessionIds?.length) {
    const errors: string[] = [];
    const sessions = args.sessionIds
      .map((sessionId) => {
        const session = getSession(sessionId);
        if (!session) {
          errors.push(`Session ${sessionId} not found`);
        }
        return session;
      })
      .filter((session): session is Session => Boolean(session));
    return { scope: 'explicit', sessions: sessions.slice(0, 12), errors };
  }

  const requestedScope = args.scope ?? 'all';
  if (requestedScope === 'active') {
    const activeSessionId = $activeSessionId.get();
    const activeSession = activeSessionId ? getSession(activeSessionId) : null;
    return {
      scope: 'active',
      sessions: activeSession ? [activeSession] : [],
      errors: activeSession ? [] : ['No active session is available'],
    };
  }

  if (requestedScope === 'layout') {
    const layoutSessionIds = getLayoutSessionIds();
    const sessions = layoutSessionIds
      .map((sessionId) => getSession(sessionId))
      .filter((session): session is Session => Boolean(session));
    return { scope: 'layout', sessions: sessions.slice(0, 12), errors: [] };
  }

  return { scope: 'all', sessions: $sessionList.get().slice(0, 12), errors: [] };
}

function buildCampaignState(
  sessions: CampaignStatusResult['sessions'],
): CampaignStatusResult['campaignState'] {
  if (sessions.length === 0) return 'empty';
  if (sessions.some((session) => session.needsAttention)) return 'needs_user';
  if (sessions.some((session) => session.status === 'blocked')) return 'blocked';
  if (sessions.some((session) => session.status === 'busy')) return 'busy';
  if (sessions.some((session) => session.status === 'complete')) return 'ready';
  return 'mixed';
}

function chooseCampaignFocus(sessions: CampaignStatusResult['sessions']): string | null {
  return (
    sessions.find((session) => session.needsAttention)?.sessionId ??
    sessions.find((session) => session.status === 'blocked')?.sessionId ??
    sessions.find((session) => session.status === 'busy')?.sessionId ??
    sessions.find((session) => session.status === 'complete' && session.isActive)?.sessionId ??
    sessions.find((session) => session.status === 'complete')?.sessionId ??
    sessions[0]?.sessionId ??
    null
  );
}

function buildCampaignResponseText(
  campaignState: CampaignStatusResult['campaignState'],
  sessions: CampaignStatusResult['sessions'],
): string {
  const target = sessions.find((session) => session.sessionId === chooseCampaignFocus(sessions));
  if (!target) return 'No MidTerm sessions are available.';

  if (campaignState === 'needs_user') {
    return `${target.title} needs input: ${target.attentionReason || target.summary}`;
  }

  if (campaignState === 'blocked') {
    return `${target.title} is blocked: ${target.attentionReason || target.summary}`;
  }

  if (campaignState === 'busy') {
    const busyCount = sessions.filter((session) => session.status === 'busy').length;
    return `${busyCount} session${busyCount === 1 ? ' is' : 's are'} still working. ${target.summary}`;
  }

  if (campaignState === 'ready') {
    const completeCount = sessions.filter((session) => session.status === 'complete').length;
    return `${completeCount} session${completeCount === 1 ? ' is' : 's are'} ready. ${target.summary}`;
  }

  return target.summary;
}

function buildCampaignNextAction(
  campaignState: CampaignStatusResult['campaignState'],
  recommendedFocusSessionId: string | null,
): string {
  if (!recommendedFocusSessionId) return 'Create or select a MidTerm session before acting.';
  if (campaignState === 'needs_user') {
    return `Select ${recommendedFocusSessionId}, tell the user what input is needed, then stop.`;
  }
  if (campaignState === 'blocked') {
    return `Select ${recommendedFocusSessionId}, report the blocker, and propose one concrete recovery step.`;
  }
  if (campaignState === 'busy') {
    return 'Tell the user work is still running; offer to keep watching if they want a later update.';
  }
  if (campaignState === 'ready') {
    return `Select ${recommendedFocusSessionId} if the user should inspect it, then summarize the observed outcome.`;
  }
  return 'Ask one concise clarification question before taking action.';
}

async function buildCampaignSession(
  session: Session,
  args: CampaignStatusArgs,
  layoutSessionSet: Set<string>,
): Promise<CampaignStatusResult['sessions'][number]> {
  const activitySeconds = Math.max(15, Math.min(args.activitySeconds ?? 120, 600));
  const includeBrowserStatus = args.includeBrowserStatus ?? true;
  const includeRepoStatus = args.includeRepoStatus ?? true;
  const initial = await buildContinuitySession(session, activitySeconds, false);
  let current = initial;
  let waited = false;
  let timedOut: boolean | undefined;
  let elapsedMs: number | undefined;

  if (args.waitForBusy && initial.status === 'busy') {
    const waitResult = await handleWaitForTurnCompletion({
      sessionId: session.id,
      timeoutMs: Math.max(1000, Math.min(args.timeoutMs ?? 30000, 90000)),
      pollIntervalMs: Math.max(500, Math.min(args.pollIntervalMs ?? 2000, 10000)),
      activitySeconds,
      includeTail: false,
    });
    if (waitResult.success) {
      waited = true;
      timedOut = waitResult.timedOut;
      elapsedMs = waitResult.elapsedMs;
      current = {
        sessionId: session.id,
        title: waitResult.title,
        isActive: session.id === $activeSessionId.get(),
        status: waitResult.status,
        state: waitResult.state,
        stateLabel: waitResult.stateLabel,
        needsAttention: waitResult.needsAttention,
        attentionReason: waitResult.attentionReason,
        summary: waitResult.summary,
        nextAction: waitResult.nextAction,
        latestActivities: waitResult.latestActivities,
      };
    }
  }

  const result: CampaignStatusResult['sessions'][number] = {
    ...current,
    isFocused: session.id === $focusedSessionId.get(),
    isInLayout: layoutSessionSet.has(session.id),
    waited,
    defaultPreview: includeBrowserStatus ? await buildDefaultPreviewOverview(session.id) : null,
  };

  if (includeRepoStatus) {
    result.repos = compactGitRepos(getCachedGitReposForSession(session.id));
  }

  if (timedOut !== undefined) {
    result.timedOut = timedOut;
  }

  if (elapsedMs !== undefined) {
    result.elapsedMs = elapsedMs;
  }

  return result;
}

/**
 * Handle campaign_status tool - read-only multi-session orchestration state.
 */
async function handleCampaignStatus(args: CampaignStatusArgs): Promise<CampaignStatusResult> {
  const startedAt = Date.now();
  const resolved = resolveCampaignSessions(args);
  const layoutSessionSet = new Set(getLayoutSessionIds());
  const sessions = await Promise.all(
    resolved.sessions.map((session) => buildCampaignSession(session, args, layoutSessionSet)),
  );
  const campaignState = buildCampaignState(sessions);
  const recommendedFocusSessionId = chooseCampaignFocus(sessions);

  return {
    success: resolved.errors.length === 0,
    scope: resolved.scope,
    campaignState,
    activeSessionId: $activeSessionId.get(),
    focusedSessionId: $focusedSessionId.get(),
    targetContext: buildVoiceTargetContext({ sessionId: recommendedFocusSessionId }),
    generatedAt: new Date().toISOString(),
    waited: sessions.some((session) => session.waited),
    elapsedMs: Date.now() - startedAt,
    responseText: resolved.errors[0] ?? buildCampaignResponseText(campaignState, sessions),
    nextAction: resolved.errors[0]
      ? 'Call session_overview to discover valid session IDs, then retry if needed.'
      : buildCampaignNextAction(campaignState, recommendedFocusSessionId),
    recommendedFocusSessionId,
    sessions,
    attentionSessionIds: sessions
      .filter((session) => session.needsAttention)
      .map((session) => session.sessionId),
    blockedSessionIds: sessions
      .filter((session) => session.status === 'blocked')
      .map((session) => session.sessionId),
    busySessionIds: sessions
      .filter((session) => session.status === 'busy')
      .map((session) => session.sessionId),
    completeSessionIds: sessions
      .filter((session) => session.status === 'complete')
      .map((session) => session.sessionId),
    shellSessionIds: sessions
      .filter((session) => session.status === 'shell')
      .map((session) => session.sessionId),
  };
}

/**
 * Handle dev_browser_open tool - open a URL in a session-scoped named Dev Browser preview.
 */
async function handleDevBrowserOpen(args: DevBrowserOpenArgs): Promise<unknown> {
  const sessionId = resolveSessionId(args.sessionId);
  if (!sessionId) {
    return {
      success: false,
      error: 'No active session and no sessionId provided',
      targetContext: buildVoiceTargetContext({ previewName: args.previewName }),
      responseText: 'No active session is available for Dev Browser open.',
      nextAction: 'Call session_overview, select a target session, then retry dev_browser_open.',
    };
  }

  if (!getSession(sessionId)) {
    return {
      success: false,
      error: `Session ${sessionId} not found`,
      targetContext: buildVoiceTargetContext({ sessionId, previewName: args.previewName }),
      responseText: `Session ${sessionId} was not found for Dev Browser open.`,
      nextAction: 'Call session_overview to discover valid session IDs before opening the preview.',
    };
  }

  const previewName = resolvePreviewName(args.previewName);
  const url = args.url.trim();
  if (!url) {
    return {
      success: false,
      error: 'url is required',
      targetContext: buildVoiceTargetContext({ sessionId, previewName }),
      responseText: 'Dev Browser open needs a URL.',
      nextAction: 'Ask the user for the exact URL or derive it from the running app output.',
    };
  }

  const target = await setWebPreviewTarget(sessionId, previewName, url);
  if (!target?.active) {
    return {
      success: false,
      error: `Failed to set Dev Browser target for ${sessionId}/${previewName}`,
      targetContext: buildVoiceTargetContext({ sessionId, previewName }),
      responseText: `Failed to open Dev Browser for ${sessionId}/${previewName}.`,
      nextAction: 'Check the session and preview target, then retry dev_browser_open.',
    };
  }

  await selectSessionForBrowser(sessionId, previewName);
  await sleep(250);
  const status = await getBrowserPreviewStatus(sessionId, previewName);

  return {
    success: true,
    sessionId,
    previewName,
    targetContext: buildVoiceTargetContext({ sessionId, previewName, action: 'dev_browser_open' }),
    url: target.url,
    targetRevision: target.targetRevision,
    status,
    responseText: `Opened Dev Browser ${previewName} for session ${sessionId}.`,
    nextAction:
      'Use dev_browser_status or dev_browser_command outline to inspect the loaded page before making UI claims.',
  };
}

/**
 * Handle dev_browser_status tool - report target and browser bridge readiness.
 */
async function handleDevBrowserStatus(args: DevBrowserStatusArgs): Promise<unknown> {
  const sessionId = resolveSessionId(args.sessionId);
  if (!sessionId) {
    return {
      success: false,
      error: 'No active session and no sessionId provided',
      targetContext: buildVoiceTargetContext({ previewName: args.previewName }),
      responseText: 'No active session is available for Dev Browser status.',
      nextAction: 'Call session_overview, select a target session, then retry dev_browser_status.',
    };
  }

  const previewName = resolvePreviewName(args.previewName);
  const [target, status] = await Promise.all([
    getWebPreviewTarget(sessionId, previewName),
    getBrowserPreviewStatus(sessionId, previewName, args.previewId ?? undefined),
  ]);

  return {
    success: true,
    sessionId,
    previewName,
    targetContext: buildVoiceTargetContext({
      sessionId,
      previewName,
      previewId: args.previewId,
      action: 'dev_browser_status',
    }),
    target,
    status,
    responseText: status?.controllable
      ? `Dev Browser ${previewName} is ready for session ${sessionId}.`
      : `Dev Browser ${previewName} is not ready for session ${sessionId}.`,
    nextAction: status?.controllable
      ? 'Use dev_browser_command outline or query for page inspection.'
      : 'Open a URL with dev_browser_open or wait for the preview bridge to become ready.',
  };
}

/**
 * Handle dev_browser_command tool - run a scoped Dev Browser command.
 */
function buildBrowserCommandOptions(args: DevBrowserCommandArgs): {
  selector?: string | null;
  value?: string | null;
  maxDepth?: number;
  textOnly?: boolean;
  timeout?: number;
} {
  const commandOptions: {
    selector?: string | null;
    value?: string | null;
    maxDepth?: number;
    textOnly?: boolean;
    timeout?: number;
  } = {};
  if (args.selector !== undefined) commandOptions.selector = args.selector;
  if (args.value !== undefined) commandOptions.value = args.value;
  if (args.maxDepth !== undefined) commandOptions.maxDepth = args.maxDepth;
  if (args.textOnly !== undefined) commandOptions.textOnly = args.textOnly;
  if (args.timeout !== undefined) commandOptions.timeout = args.timeout;
  return commandOptions;
}

function validateBrowserCommand(command: string): Record<string, unknown> | null {
  if (!command) {
    return {
      success: false,
      error: 'command is required',
      responseText: 'Dev Browser command is missing.',
      nextAction:
        'Use one supported command such as outline, query, wait, click, fill, or navigate.',
    };
  }

  if (!BROWSER_COMMANDS.has(command)) {
    return {
      success: false,
      error: `Unsupported Dev Browser command: ${command}`,
      supportedCommands: [...BROWSER_COMMANDS],
      responseText: `Unsupported Dev Browser command: ${command}.`,
      nextAction: 'Choose one of the supportedCommands values and retry.',
    };
  }

  return null;
}

function buildDevBrowserCommandResponse(
  sessionId: string,
  previewName: string,
  command: string,
  result: Awaited<ReturnType<typeof runBrowserCommand>>,
): Record<string, unknown> {
  const success = result?.success ?? false;
  return {
    success,
    sessionId,
    previewName,
    command,
    targetContext: buildVoiceTargetContext({
      sessionId,
      previewName,
      action: `dev_browser_command:${command}`,
    }),
    result: result?.result ?? null,
    error: result?.error ?? null,
    matchCount: result?.matchCount ?? null,
    responseText: success
      ? `Dev Browser ${command} completed for ${sessionId}/${previewName}.`
      : `Dev Browser ${command} failed for ${sessionId}/${previewName}: ${result?.error ?? 'unknown error'}.`,
    nextAction: success
      ? 'Use the command result as evidence; after mutating commands, wait or inspect before reporting final state.'
      : 'Check supported commands, selector, preview readiness, and session ID before retrying.',
  };
}

async function handleDevBrowserCommand(args: DevBrowserCommandArgs): Promise<unknown> {
  const sessionId = resolveSessionId(args.sessionId);
  if (!sessionId) {
    return {
      success: false,
      error: 'No active session and no sessionId provided',
      targetContext: buildVoiceTargetContext({ previewName: args.previewName }),
      responseText: 'No active session is available for the Dev Browser command.',
      nextAction:
        'Call session_overview, select a target session, then retry the Dev Browser command.',
    };
  }

  const command = args.command.trim();
  const validationError = validateBrowserCommand(command);
  if (validationError) return validationError;

  const previewName = resolvePreviewName(args.previewName);

  const result = await runBrowserCommand(
    command,
    sessionId,
    previewName,
    args.previewId ?? undefined,
    buildBrowserCommandOptions(args),
  );

  return buildDevBrowserCommandResponse(sessionId, previewName, command, result);
}

function decodeDataUrlToFile(dataUrl: string, filename: string): File | null {
  const match = /^data:([^;,]+);base64,(.+)$/.exec(dataUrl);
  if (!match) {
    return null;
  }

  const mimeType = match[1];
  const base64 = match[2];
  if (!mimeType || !base64) {
    return null;
  }

  try {
    const bytes = Uint8Array.from(atob(base64), (char) => char.charCodeAt(0));
    return new File([bytes], filename, { type: mimeType });
  } catch {
    return null;
  }
}

/**
 * Handle dev_browser_screenshot tool - capture a preview screenshot and upload it to the session.
 */
async function handleDevBrowserScreenshot(args: DevBrowserScreenshotArgs): Promise<unknown> {
  const sessionId = resolveSessionId(args.sessionId);
  if (!sessionId) {
    return {
      success: false,
      error: 'No active session and no sessionId provided',
      targetContext: buildVoiceTargetContext({ previewName: args.previewName }),
    };
  }

  const previewName = resolvePreviewName(args.previewName);
  const [target, status] = await Promise.all([
    getWebPreviewTarget(sessionId, previewName),
    getBrowserPreviewStatus(sessionId, previewName, args.previewId ?? undefined),
  ]);

  if (!target?.url) {
    return {
      success: false,
      sessionId,
      previewName,
      targetContext: buildVoiceTargetContext({
        sessionId,
        previewName,
        previewId: args.previewId,
      }),
      target,
      status,
      error: 'No URL is open in this Dev Browser preview',
    };
  }

  const dataUrl = await captureBrowserScreenshotRaw(
    sessionId,
    args.previewId ?? undefined,
    previewName,
  );
  if (!dataUrl) {
    return {
      success: false,
      sessionId,
      previewName,
      targetContext: buildVoiceTargetContext({
        sessionId,
        previewName,
        previewId: args.previewId,
      }),
      target,
      status,
      error: 'MidTerm did not receive screenshot image data from the Dev Browser',
    };
  }

  const timestamp = new Date().toISOString().replace(/[:.]/g, '-');
  const filename = `dev_browser_${previewName}_${timestamp}.png`;
  const file = decodeDataUrlToFile(dataUrl, filename);
  if (!file) {
    return {
      success: false,
      sessionId,
      previewName,
      targetContext: buildVoiceTargetContext({
        sessionId,
        previewName,
        previewId: args.previewId,
      }),
      target,
      status,
      error: 'Screenshot image data could not be decoded',
    };
  }

  const path = await uploadFile(sessionId, file);
  if (!path) {
    return {
      success: false,
      sessionId,
      previewName,
      targetContext: buildVoiceTargetContext({
        sessionId,
        previewName,
        previewId: args.previewId,
      }),
      target,
      status,
      error: 'Screenshot upload did not return a usable file path',
    };
  }

  return {
    success: true,
    sessionId,
    previewName,
    previewId: args.previewId ?? null,
    targetContext: buildVoiceTargetContext({
      sessionId,
      previewName,
      previewId: args.previewId,
      action: 'dev_browser_screenshot',
    }),
    target,
    status,
    path,
    fileName: filename,
    mimeType: file.type,
    sizeBytes: file.size,
    viewUrl: `/api/files/view?path=${encodeURIComponent(path)}&sessionId=${encodeURIComponent(sessionId)}`,
  };
}

/**
 * Handle repo_monitor tool - inspect or update session-scoped Git repo bindings.
 */
async function runRepoMonitorAction(
  sessionId: string,
  args: RepoMonitorArgs,
): Promise<Awaited<ReturnType<typeof fetchGitRepos>>> {
  const action = args.action.toLowerCase();
  switch (action) {
    case 'list':
      return fetchGitRepos(sessionId);
    case 'add': {
      const path = args.path?.trim();
      if (!path) {
        throw new Error('path is required for repo_monitor add');
      }
      return addGitRepo(
        sessionId,
        path,
        args.role?.trim() || 'target',
        args.label?.trim() || undefined,
      );
    }
    case 'remove': {
      const repoRoot = args.repoRoot?.trim();
      if (!repoRoot) {
        throw new Error('repoRoot is required for repo_monitor remove');
      }
      return removeGitRepo(sessionId, repoRoot);
    }
    case 'refresh':
      return refreshGitRepo(sessionId, args.repoRoot?.trim() || undefined);
    default:
      throw new Error(`Unknown action: ${args.action}. Use list, add, remove, or refresh.`);
  }
}

async function handleRepoMonitor(args: RepoMonitorArgs): Promise<unknown> {
  const sessionId = resolveSessionId(args.sessionId);
  if (!sessionId) {
    return {
      success: false,
      error: 'No active session and no sessionId provided',
      targetContext: buildVoiceTargetContext({ repoRoot: args.repoRoot ?? args.path }),
    };
  }

  if (!getSession(sessionId)) {
    return {
      success: false,
      error: `Session ${sessionId} not found`,
      targetContext: buildVoiceTargetContext({
        sessionId,
        repoRoot: args.repoRoot ?? args.path,
      }),
    };
  }

  const action = args.action.toLowerCase();
  const response = await runRepoMonitorAction(sessionId, args);

  if (response) {
    applyGitReposForSession(sessionId, response.repos);
  }

  const repos = response?.repos ?? getCachedGitReposForSession(sessionId);
  return {
    success: response !== null || repos.length > 0,
    sessionId,
    action,
    targetContext: buildVoiceTargetContext({
      sessionId,
      repoRoot: args.repoRoot ?? args.path,
      action: `repo_monitor:${action}`,
    }),
    repos: compactGitRepos(repos),
    repoCount: repos.length,
  };
}

function buildLayoutStatus(action: string): unknown {
  return {
    success: true,
    action,
    activeSessionId: $activeSessionId.get(),
    focusedSessionId: $focusedSessionId.get(),
    targetContext: buildVoiceTargetContext({ action: `layout_control:${action}` }),
    layoutSessionIds: getLayoutSessionIds(),
    root: $layout.get().root,
  };
}

function requireLayoutSession(sessionId: string | null | undefined, fieldName: string): string {
  const resolved = sessionId?.trim();
  if (!resolved) {
    throw new Error(`${fieldName} is required`);
  }
  if (!getSession(resolved)) {
    throw new Error(`Session ${resolved} not found`);
  }
  return resolved;
}

function requireDockPosition(position: DockPosition | null | undefined): DockPosition {
  if (!position || !LAYOUT_DOCK_POSITIONS.has(position)) {
    throw new Error('position must be top, bottom, left, or right');
  }
  return position;
}

/**
 * Handle layout_control tool - inspect and arrange MidTerm's multi-session layout.
 */
function handleLayoutControl(args: LayoutControlArgs): unknown {
  const action = args.action.toLowerCase();

  if (action === 'status') {
    return buildLayoutStatus(action);
  }

  if (action === 'focus') {
    const sessionId = requireLayoutSession(args.sessionId, 'sessionId');
    if (isSessionInLayout(sessionId)) {
      focusLayoutSession(sessionId);
    } else {
      handleSelectSession({ sessionId, focusTerminal: args.focusTerminal ?? true });
    }
    return buildLayoutStatus(action);
  }

  if (action === 'dock') {
    const sessionId = requireLayoutSession(args.sessionId, 'sessionId');
    const targetSessionId = requireLayoutSession(args.targetSessionId, 'targetSessionId');
    dockSession(targetSessionId, sessionId, requireDockPosition(args.position));
    return buildLayoutStatus(action);
  }

  if (action === 'undock') {
    undockSession(requireLayoutSession(args.sessionId, 'sessionId'));
    return buildLayoutStatus(action);
  }

  if (action === 'swap') {
    swapLayoutSessions(
      requireLayoutSession(args.sessionId, 'sessionId'),
      requireLayoutSession(args.otherSessionId, 'otherSessionId'),
    );
    return buildLayoutStatus(action);
  }

  if (action === 'clear') {
    $layout.set({ root: null });
    $focusedSessionId.set(null);
    return buildLayoutStatus(action);
  }

  return {
    success: false,
    error: `Unknown action: ${args.action}. Use status, focus, dock, undock, swap, or clear.`,
  };
}

/**
 * Handle close_session tool - close a terminal session.
 */
async function handleCloseSession(args: CloseSessionArgs): Promise<unknown> {
  const session = getSession(args.sessionId);
  if (!session) {
    return {
      success: false,
      error: `Session ${args.sessionId} not found`,
      targetContext: buildVoiceTargetContext({ sessionId: args.sessionId }),
    };
  }

  await apiDeleteSession(args.sessionId);
  return {
    success: true,
    sessionId: args.sessionId,
    targetContext: buildVoiceTargetContext({
      sessionId: args.sessionId,
      action: 'close_session',
    }),
  };
}

async function listBookmarks(): Promise<unknown> {
  const { data } = await getHistory();
  if (!data) {
    return {
      success: false,
      error: 'Failed to fetch history',
      targetContext: buildVoiceTargetContext({ action: 'bookmarks:list' }),
    };
  }

  const starred = data.filter((entry) => entry.isStarred);
  return {
    success: true,
    targetContext: buildVoiceTargetContext({ action: 'bookmarks:list' }),
    bookmarks: starred.map((entry) => ({
      id: entry.id,
      shellType: entry.shellType,
      executable: entry.executable,
      commandLine: entry.commandLine,
      workingDirectory: entry.workingDirectory,
    })),
  };
}

async function launchBookmark(bookmarkId: string): Promise<unknown> {
  const { data: historyData } = await getHistory();
  if (!historyData) {
    return {
      success: false,
      error: 'Failed to fetch history',
      targetContext: buildVoiceTargetContext({ action: 'bookmarks:launch' }),
    };
  }

  const bookmark = historyData.find((entry) => entry.id === bookmarkId && entry.isStarred);
  if (!bookmark) {
    return {
      success: false,
      error: `Bookmark ${bookmarkId} not found`,
      targetContext: buildVoiceTargetContext({ action: 'bookmarks:launch' }),
    };
  }

  const refSession = $sessionList.get()[0];
  const cols = refSession?.cols ?? 120;
  const rows = refSession?.rows ?? 30;

  let sessionData: Awaited<ReturnType<typeof apiCreateSession>>['data'];
  try {
    ({ data: sessionData } = await apiCreateSession({
      cols,
      rows,
      shell: bookmark.shellType || null,
      workingDirectory: bookmark.workingDirectory || null,
    }));
  } catch (error) {
    return {
      success: false,
      error: getSessionLaunchErrorMessage(error),
      targetContext: buildVoiceTargetContext({ action: 'bookmarks:launch' }),
    };
  }

  if (!sessionData) {
    return {
      success: false,
      error: 'Failed to create session',
      targetContext: buildVoiceTargetContext({ action: 'bookmarks:launch' }),
    };
  }

  if (bookmark.commandLine) {
    await sleep(300);
    sendInput(sessionData.id, bookmark.commandLine + '\r');
  }

  return {
    success: true,
    sessionId: sessionData.id,
    targetContext: buildVoiceTargetContext({
      sessionId: sessionData.id,
      action: 'bookmarks:launch',
    }),
    launched: {
      executable: bookmark.executable,
      commandLine: bookmark.commandLine,
      workingDirectory: bookmark.workingDirectory,
    },
  };
}

/**
 * Handle bookmarks tool - list or launch bookmarks (starred history entries).
 */
async function handleBookmarks(args: BookmarksArgs): Promise<unknown> {
  if (args.action === 'list') {
    return listBookmarks();
  }

  if (args.action === 'launch') {
    if (!args.bookmarkId) {
      return {
        success: false,
        error: 'bookmarkId is required for launch action',
        targetContext: buildVoiceTargetContext({ action: 'bookmarks:launch' }),
      };
    }
    return launchBookmark(args.bookmarkId);
  }

  return {
    success: false,
    error: `Unknown action: ${args.action}. Use 'list' or 'launch'.`,
    targetContext: buildVoiceTargetContext({ action: `bookmarks:${args.action}` }),
  };
}

const voiceToolHandlers: Record<
  VoiceToolName,
  (args: Record<string, unknown>) => Promise<unknown>
> = {
  state_of_things: () => Promise.resolve(handleStateOfThings()),
  session_overview: (args) => handleSessionOverview(args as unknown as SessionOverviewArgs),
  conversation_continuity: (args) =>
    handleConversationContinuity(args as unknown as ConversationContinuityArgs),
  campaign_status: (args) => handleCampaignStatus(args as unknown as CampaignStatusArgs),
  make_input: (args) => handleMakeInput(args as unknown as MakeInputArgs),
  read_scrollback: (args) =>
    Promise.resolve(handleReadScrollback(args as unknown as ReadScrollbackArgs)),
  interactive_read: (args) => handleInteractiveRead(args as unknown as InteractiveReadArgs),
  create_session: (args) => handleCreateSession(args as unknown as CreateSessionArgs),
  select_session: (args) =>
    Promise.resolve(handleSelectSession(args as unknown as SelectSessionArgs)),
  send_prompt: (args) => handleSendPrompt(args as unknown as SendPromptArgs),
  session_activity: (args) => handleSessionActivity(args as unknown as SessionActivityArgs),
  session_turn_summary: (args) =>
    handleSessionTurnSummary(args as unknown as SessionTurnSummaryArgs),
  wait_for_turn_completion: (args) =>
    handleWaitForTurnCompletion(args as unknown as WaitForTurnCompletionArgs),
  dev_browser_open: (args) => handleDevBrowserOpen(args as unknown as DevBrowserOpenArgs),
  dev_browser_status: (args) => handleDevBrowserStatus(args as unknown as DevBrowserStatusArgs),
  dev_browser_command: (args) => handleDevBrowserCommand(args as unknown as DevBrowserCommandArgs),
  dev_browser_screenshot: (args) =>
    handleDevBrowserScreenshot(args as unknown as DevBrowserScreenshotArgs),
  repo_monitor: (args) => handleRepoMonitor(args as unknown as RepoMonitorArgs),
  layout_control: (args) =>
    Promise.resolve(handleLayoutControl(args as unknown as LayoutControlArgs)),
  close_session: (args) => handleCloseSession(args as unknown as CloseSessionArgs),
  bookmarks: (args) => handleBookmarks(args as unknown as BookmarksArgs),
  wait_for_user: () => Promise.resolve({ success: true, waiting: true }),
};

/**
 * Process a tool request from the voice server.
 * Returns a tool response to send back.
 */
export async function processToolRequest(request: VoiceToolRequest): Promise<VoiceToolResponse> {
  log.info(() => `Processing tool request: ${request.tool} (${request.requestId})`);

  try {
    const handler = voiceToolHandlers[request.tool];
    const result = await handler(request.args);

    log.info(() => `Tool ${request.tool} completed successfully`);
    return {
      type: 'tool_response',
      requestId: request.requestId,
      result,
    };
  } catch (err) {
    const errorMsg = err instanceof Error ? err.message : String(err);
    log.error(() => `Tool ${request.tool} failed: ${errorMsg}`);
    return {
      type: 'tool_response',
      requestId: request.requestId,
      result: null,
      error: errorMsg,
    };
  }
}

/* eslint-enable max-lines */
