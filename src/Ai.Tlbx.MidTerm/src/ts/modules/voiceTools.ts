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
  getHistory,
  sendSessionPrompt,
} from '../api/client';
import type {
  VoiceToolRequest,
  VoiceToolResponse,
  MakeInputArgs,
  ReadScrollbackArgs,
  InteractiveReadArgs,
  CreateSessionArgs,
  SelectSessionArgs,
  SendPromptArgs,
  SessionActivityArgs,
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
import { JS_BUILD_VERSION } from '../constants';

const log = createLogger('voiceTools');

const recentBells: BellNotification[] = [];

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

/**
 * Process a tool request from the voice server.
 * Returns a tool response to send back.
 */
export async function processToolRequest(request: VoiceToolRequest): Promise<VoiceToolResponse> {
  log.info(() => `Processing tool request: ${request.tool} (${request.requestId})`);

  try {
    let result: unknown;

    switch (request.tool) {
      case 'state_of_things':
        result = handleStateOfThings();
        break;

      case 'make_input':
        result = await handleMakeInput(request.args as unknown as MakeInputArgs);
        break;

      case 'read_scrollback':
        result = handleReadScrollback(request.args as unknown as ReadScrollbackArgs);
        break;

      case 'interactive_read':
        result = await handleInteractiveRead(request.args as unknown as InteractiveReadArgs);
        break;

      case 'create_session':
        result = await handleCreateSession(request.args as unknown as CreateSessionArgs);
        break;

      case 'select_session':
        result = handleSelectSession(request.args as unknown as SelectSessionArgs);
        break;

      case 'send_prompt':
        result = await handleSendPrompt(request.args as unknown as SendPromptArgs);
        break;

      case 'session_activity':
        result = await handleSessionActivity(request.args as unknown as SessionActivityArgs);
        break;

      case 'close_session':
        result = await handleCloseSession(request.args as unknown as CloseSessionArgs);
        break;

      case 'bookmarks':
        result = await handleBookmarks(request.args as unknown as BookmarksArgs);
        break;

      case 'wait_for_user':
        result = { success: true, waiting: true };
        break;

      default:
        return {
          type: 'tool_response',
          requestId: request.requestId,
          result: null,
          error: `Unknown tool: ${String(request.tool)}`,
        };
    }

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
