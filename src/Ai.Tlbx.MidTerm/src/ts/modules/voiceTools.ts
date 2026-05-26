/**
 * Voice Tools Module
 *
 * Handles tool requests from the voice assistant server.
 * Tools execute locally in the browser using xterm.js buffers and stores.
 */

import { createLogger } from './logging';
import { sendInput } from './comms';
import { requestSelectSession } from './comms/stateChannel';
import { sessionTerminals } from '../state';
import { $sessionList, $activeSessionId, $updateInfo, getSession } from '../stores';
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
  getBrowserPreviewStatus,
  getWebPreviewTarget,
  runBrowserCommand,
  setWebPreviewTarget,
} from './web/webApi';
import { selectActivePreview } from './web';
import { addGitRepo, fetchGitRepos, refreshGitRepo, removeGitRepo } from './git/gitApi';
import { applyGitReposForSession, getCachedGitReposForSession } from './git';
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
  SessionActivityArgs,
  SessionTurnSummaryArgs,
  DevBrowserOpenArgs,
  DevBrowserStatusArgs,
  DevBrowserCommandArgs,
  RepoMonitorArgs,
  CloseSessionArgs,
  BookmarksArgs,
  StateOfThingsResult,
  VoiceSessionState,
  MakeInputResult,
  ReadScrollbackResult,
  InteractiveReadResult,
  InteractiveOpResult,
  BellNotification,
} from '../types';
import type { AgentSessionVibeResponse } from '../api/types';
import type { GitRepoBinding } from './git/types';
import { JS_BUILD_VERSION } from '../constants';

const log = createLogger('voiceTools');

const recentBells: BellNotification[] = [];
const DEFAULT_PREVIEW_NAME = 'default';
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
    version: JS_BUILD_VERSION,
    updateAvailable: updateInfo?.available ?? false,
    recentBells: [...recentBells],
  };
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

  return { results };
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
    if (!data) return { success: false, error: 'Failed to create session' };

    return { success: true, sessionId: data.id, shell: data.shellType };
  } catch (error) {
    return { success: false, error: getSessionLaunchErrorMessage(error) };
  }
}

/**
 * Handle select_session tool - bring a session into the active MidTerm surface.
 */
function handleSelectSession(args: SelectSessionArgs): unknown {
  const session = getSession(args.sessionId);
  if (!session) {
    return { success: false, error: `Session ${args.sessionId} not found` };
  }

  requestSelectSession(args.sessionId, {
    closeSettingsPanel: false,
    focusTerminal: args.focusTerminal ?? true,
  });

  return {
    success: true,
    activeSessionId: args.sessionId,
    title: session.name || session.terminalTitle || null,
  };
}

/**
 * Handle send_prompt tool - send a structured prompt to an agent session.
 */
async function handleSendPrompt(args: SendPromptArgs): Promise<unknown> {
  const session = getSession(args.sessionId);
  if (!session) {
    return { success: false, error: `Session ${args.sessionId} not found` };
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
    return { success: false, error: `Session ${args.sessionId} not found` };
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

  return { success: true, feeds };
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
    return 'Wait briefly, then call session_turn_summary again before speaking as if the turn finished.';
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

/**
 * Handle session_turn_summary tool - compact turn lifecycle summary for voice flow.
 */
async function handleSessionTurnSummary(args: SessionTurnSummaryArgs): Promise<unknown> {
  const session = getSession(args.sessionId);
  if (!session) {
    return { success: false, error: `Session ${args.sessionId} not found` };
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

/**
 * Handle dev_browser_open tool - open a URL in a session-scoped named Dev Browser preview.
 */
async function handleDevBrowserOpen(args: DevBrowserOpenArgs): Promise<unknown> {
  const sessionId = resolveSessionId(args.sessionId);
  if (!sessionId) {
    return { success: false, error: 'No active session and no sessionId provided' };
  }

  if (!getSession(sessionId)) {
    return { success: false, error: `Session ${sessionId} not found` };
  }

  const previewName = resolvePreviewName(args.previewName);
  const url = args.url.trim();
  if (!url) {
    return { success: false, error: 'url is required' };
  }

  const target = await setWebPreviewTarget(sessionId, previewName, url);
  if (!target?.active) {
    return {
      success: false,
      error: `Failed to set Dev Browser target for ${sessionId}/${previewName}`,
    };
  }

  await selectSessionForBrowser(sessionId, previewName);
  await sleep(250);
  const status = await getBrowserPreviewStatus(sessionId, previewName);

  return {
    success: true,
    sessionId,
    previewName,
    url: target.url,
    targetRevision: target.targetRevision,
    status,
  };
}

/**
 * Handle dev_browser_status tool - report target and browser bridge readiness.
 */
async function handleDevBrowserStatus(args: DevBrowserStatusArgs): Promise<unknown> {
  const sessionId = resolveSessionId(args.sessionId);
  if (!sessionId) {
    return { success: false, error: 'No active session and no sessionId provided' };
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
    target,
    status,
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
    return { success: false, error: 'command is required' };
  }

  if (!BROWSER_COMMANDS.has(command)) {
    return {
      success: false,
      error: `Unsupported Dev Browser command: ${command}`,
      supportedCommands: [...BROWSER_COMMANDS],
    };
  }

  return null;
}

async function handleDevBrowserCommand(args: DevBrowserCommandArgs): Promise<unknown> {
  const sessionId = resolveSessionId(args.sessionId);
  if (!sessionId) {
    return { success: false, error: 'No active session and no sessionId provided' };
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

  return {
    success: result?.success ?? false,
    sessionId,
    previewName,
    command,
    result: result?.result ?? null,
    error: result?.error ?? null,
    matchCount: result?.matchCount ?? null,
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
    return { success: false, error: 'No active session and no sessionId provided' };
  }

  if (!getSession(sessionId)) {
    return { success: false, error: `Session ${sessionId} not found` };
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
    repos: compactGitRepos(repos),
    repoCount: repos.length,
  };
}

/**
 * Handle close_session tool - close a terminal session.
 */
async function handleCloseSession(args: CloseSessionArgs): Promise<unknown> {
  const session = getSession(args.sessionId);
  if (!session) {
    return { success: false, error: `Session ${args.sessionId} not found` };
  }

  await apiDeleteSession(args.sessionId);
  return { success: true };
}

async function listBookmarks(): Promise<unknown> {
  const { data } = await getHistory();
  if (!data) {
    return { success: false, error: 'Failed to fetch history' };
  }

  const starred = data.filter((entry) => entry.isStarred);
  return {
    success: true,
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
    return { success: false, error: 'Failed to fetch history' };
  }

  const bookmark = historyData.find((entry) => entry.id === bookmarkId && entry.isStarred);
  if (!bookmark) {
    return { success: false, error: `Bookmark ${bookmarkId} not found` };
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
    return { success: false, error: getSessionLaunchErrorMessage(error) };
  }

  if (!sessionData) {
    return { success: false, error: 'Failed to create session' };
  }

  if (bookmark.commandLine) {
    await sleep(300);
    sendInput(sessionData.id, bookmark.commandLine + '\r');
  }

  return {
    success: true,
    sessionId: sessionData.id,
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
      return { success: false, error: 'bookmarkId is required for launch action' };
    }
    return launchBookmark(args.bookmarkId);
  }

  return { success: false, error: `Unknown action: ${args.action}. Use 'list' or 'launch'.` };
}

const voiceToolHandlers: Record<
  VoiceToolName,
  (args: Record<string, unknown>) => Promise<unknown>
> = {
  state_of_things: () => Promise.resolve(handleStateOfThings()),
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
  dev_browser_open: (args) => handleDevBrowserOpen(args as unknown as DevBrowserOpenArgs),
  dev_browser_status: (args) => handleDevBrowserStatus(args as unknown as DevBrowserStatusArgs),
  dev_browser_command: (args) => handleDevBrowserCommand(args as unknown as DevBrowserCommandArgs),
  repo_monitor: (args) => handleRepoMonitor(args as unknown as RepoMonitorArgs),
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
