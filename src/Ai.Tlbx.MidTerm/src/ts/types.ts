/**
 * Type Definitions
 *
 * Client-only interfaces and types used across all modules.
 * API types are imported from api/types.ts which re-exports from generated types.
 */

import type { Terminal } from '@xterm/xterm';
import type { FitAddon } from '@xterm/addon-fit';
import type { WebglAddon } from '@xterm/addon-webgl';

// Import types needed for local use
import type {
  Session as SessionType,
  MidTermSettingsPublic as MidTermSettingsPublicType,
  AuthStatusResponse as AuthStatusResponseType,
  UpdateInfo as UpdateInfoType,
} from './api/types';

// Re-export API types for convenience
export type {
  Session,
  MidTermSettingsPublic,
  AuthStatusResponse,
  UpdateInfo,
  LocalUpdateInfo,
  UpdateType,
  UpdateResult,
  SystemHealth,
  FirewallRuleStatusResponse,
  ShellInfoDto,
  CertificateInfoResponse,
  BootstrapResponse,
  BootstrapLoginResponse,
  NetworkInterfaceDto,
  UserInfo,
  FeatureFlags,
  ShareAccessMode,
  CreateShareLinkRequest,
  CreateShareLinkResponse,
  ClaimShareRequest,
  ClaimShareResponse,
  ShareBootstrapResponse,
  FilePathInfo,
  FileCheckResponse,
  DirectoryEntry,
  DirectoryListResponse,
  FileResolveResponse,
  ThemeSetting,
  CursorStyleSetting,
  CursorInactiveStyleSetting,
  BellStyleSetting,
  ClipboardShortcutsSetting,
  TabTitleModeSetting,
  // Backward compat aliases (non-deprecated only)
  ThemeName,
  CursorStyle,
  CursorInactiveStyle,
  BellStyle,
  ClipboardShortcuts,
  TabTitleMode,
} from './api/types';

// =============================================================================
// Process Monitoring Types
// =============================================================================

/** Foreground process change from server */
export interface ForegroundChangePayload {
  Pid: number;
  Name: string;
  CommandLine: string | null;
  Cwd: string | null;
  DisplayName?: string | null;
  ProcessIdentity?: string | null;
}

/** Process state for a session */
export interface ProcessState {
  foregroundPid: number | null;
  foregroundName: string | null;
  foregroundCommandLine: string | null;
  foregroundCwd: string | null;
  foregroundDisplayName: string | null;
  foregroundProcessIdentity: string | null;
}

export interface BrowserSessionStatus {
  browserId: string;
  isMain: boolean;
  isActive: boolean;
  connectionCount: number;
  activeConnectionCount: number;
  activeSessionId: string | null;
  activeSurface: string | null;
}

/** Terminal state for a session */
export interface TerminalState {
  terminal: Terminal;
  fitAddon: FitAddon;
  container: HTMLDivElement;
  inputProxy?: HTMLTextAreaElement | null;
  serverCols: number;
  serverRows: number;
  opened: boolean;
  reconnectFreezeOverlay?: HTMLDivElement | null;
  contextMenuHandler?: (e: MouseEvent) => void;
  pasteHandler?: (e: ClipboardEvent) => void;
  enterOverrideHandler?: (e: KeyboardEvent) => void;
  hasWebgl?: boolean;
  webglAddon?: WebglAddon | null;
  ligatureJoinerId?: number | null;
  richBackgroundTransparencyAlpha?: number | null;
  richBackgroundTransparencyDisposable?: { dispose: () => void } | undefined;
  disposables?: Array<{ dispose: () => void }>;
  mouseMoveHandler?: () => void;
  mouseLeaveHandler?: () => void;
  earlyDataDisposable?: { dispose: () => void };
  cursorHideTimer?: number | null;
  burstCursorRestoreTimer?: number | null;
  burstCursorRestoreDueAtMs?: number | null;
  lastBurstOutputAtMs?: number | null;
  lastLocalInputAtMs?: number | null;
  remoteCursorVisible?: boolean;
  burstCursorHidden?: boolean;
  syncOutputCursorHidden?: boolean;
  pendingVisualRefresh?: boolean;
}

// =============================================================================
// WebSocket Command Types
// =============================================================================

/** WebSocket command from client to server */
export type WsCommand =
  | {
      type: 'command';
      id: string;
      action: 'session.rename';
      payload: {
        sessionId: string;
        name: string | null;
        auto?: boolean;
      };
    }
  | {
      type: 'command';
      id: string;
      action: 'session.reorder';
      payload: {
        sessionIds: string[];
      };
    }
  | {
      type: 'command';
      id: string;
      action: 'browser.claimMain';
    }
  | {
      type: 'command';
      id: string;
      action: 'browser.releaseMain';
    }
  | {
      type: 'command';
      id: string;
      action: 'browser.setActivity';
      payload: {
        isActive: boolean;
        activeSessionId?: string | null;
        activeSurface?: string | null;
      };
    };

export type WsCommandAction = WsCommand['action'];
interface WsCommandPayloadMap {
  'session.rename': {
    sessionId: string;
    name: string | null;
    auto?: boolean;
  };
  'session.reorder': {
    sessionIds: string[];
  };
  'browser.claimMain': undefined;
  'browser.releaseMain': undefined;
  'browser.setActivity': {
    isActive: boolean;
    activeSessionId?: string | null;
    activeSurface?: string | null;
  };
}

export type WsCommandPayload<A extends WsCommandAction> = WsCommandPayloadMap[A];

/** WebSocket command response from server */
export interface WsCommandResponse {
  type: 'response';
  id: string;
  success: boolean;
  data?: unknown;
  error?: string;
}

/** Data returned for session.create command */
export interface WsSessionCreatedData {
  id: string;
  pid: number;
  shellType: string;
}

// =============================================================================
// Terminal Theme Types
// =============================================================================

/** xterm.js theme colors */
export interface TerminalTheme {
  background: string;
  foreground: string;
  cursor: string;
  cursorAccent: string;
  selectionBackground: string;
  scrollbarSliderBackground: string;
  scrollbarSliderHoverBackground: string;
  scrollbarSliderActiveBackground: string;
  black?: string;
  red?: string;
  green?: string;
  yellow?: string;
  blue?: string;
  magenta?: string;
  cyan?: string;
  white?: string;
  brightBlack?: string;
  brightRed?: string;
  brightGreen?: string;
  brightYellow?: string;
  brightBlue?: string;
  brightMagenta?: string;
  brightCyan?: string;
  brightWhite?: string;
}

// =============================================================================
// Application State
// =============================================================================

/** UI state */
export interface UIState {
  settingsOpen: boolean;
  sidebarOpen: boolean;
  sidebarCollapsed: boolean;
}

/** Full application state */
export interface AppState {
  sessions: SessionType[];
  activeSessionId: string | null;
  settings: MidTermSettingsPublicType | null;
  ui: UIState;
  update: UpdateInfoType | null;
  auth: AuthStatusResponseType | null;
}

// =============================================================================
// Voice/Chat Types
// =============================================================================

/** Chat message role */
export type ChatRole = 'user' | 'assistant' | 'tool';

/** Chat message from voice server */
export interface ChatMessage {
  role: ChatRole;
  content: string;
  toolName?: string;
  timestamp: string;
}

/** Voice provider info from health endpoint */
export interface VoiceProvider {
  id: string;
  name: string;
  available: boolean;
  voices: VoiceInfo[];
}

/** Voice info within a provider */
export interface VoiceInfo {
  id: string;
  name: string;
}

/** Voice server defaults */
export interface VoiceDefaults {
  provider: string;
  voice: string;
  speed: number;
}

/** Extended voice health response */
export interface VoiceHealthResponse {
  status: string;
  version: string;
  providers?: VoiceProvider[];
  defaults?: VoiceDefaults;
}

// =============================================================================
// Voice Tool Protocol Types
// =============================================================================

/** Tool names available to voice assistant */
export type VoiceToolName =
  | 'state_of_things'
  | 'session_overview'
  | 'conversation_continuity'
  | 'make_input'
  | 'read_scrollback'
  | 'interactive_read'
  | 'create_session'
  | 'select_session'
  | 'send_prompt'
  | 'session_activity'
  | 'session_turn_summary'
  | 'wait_for_turn_completion'
  | 'dev_browser_open'
  | 'dev_browser_status'
  | 'dev_browser_command'
  | 'dev_browser_screenshot'
  | 'repo_monitor'
  | 'layout_control'
  | 'close_session'
  | 'bookmarks'
  | 'wait_for_user';

/** Tool request from voice server to browser */
export interface VoiceToolRequest {
  type: 'tool_request';
  requestId: string;
  tool: VoiceToolName;
  args: Record<string, unknown>;
  requiresConfirmation?: boolean;
}

/** Tool response from browser to voice server */
export interface VoiceToolResponse {
  type: 'tool_response';
  requestId: string;
  result: unknown;
  error?: string;
  declined?: boolean;
}

/** Args for make_input tool */
export interface MakeInputArgs {
  sessionId: string;
  text: string;
  justification?: string;
  delayMs?: number;
}

/** Args for read_scrollback tool */
export interface ReadScrollbackArgs {
  sessionId: string;
  start?: string;
  lines?: number;
}

/** Args for interactive_read tool */
export interface InteractiveReadArgs {
  sessionId: string;
  operations: InteractiveOp[];
  justification?: string;
}

/** Single operation in interactive_read */
export interface InteractiveOp {
  type: 'input' | 'delay' | 'screenshot';
  data?: string;
  delayMs?: number;
}

/** Result of state_of_things tool */
export interface StateOfThingsResult {
  sessions: VoiceSessionState[];
  activeSessionId: string | null;
  version: string;
  updateAvailable: boolean;
  recentBells: BellNotification[];
}

/** Args for session_overview tool */
export interface SessionOverviewArgs {
  includeBrowserStatus?: boolean;
  includeRepoStatus?: boolean;
}

/** Compact result of session_overview tool */
export interface SessionOverviewResult {
  success: boolean;
  activeSessionId: string | null;
  focusedSessionId: string | null;
  layoutSessionIds: string[];
  version: string;
  updateAvailable: boolean;
  sessions: VoiceSessionOverview[];
}

/** Compact session state for orientation, switching, and multi-session control */
export interface VoiceSessionOverview {
  id: string;
  title: string;
  userTitle: string | null;
  terminalTitle: string | null;
  foregroundName: string | null;
  currentDirectory: string | null;
  shell: string;
  isActive: boolean;
  isFocused: boolean;
  isInLayout: boolean;
  hasRenderedTerminal: boolean;
  defaultPreview?: VoicePreviewOverview | null;
  repos?: unknown[];
}

/** Compact default Dev Browser preview state for a session */
export interface VoicePreviewOverview {
  previewName: string;
  url: string | null;
  state: string | null;
  ready: boolean;
}

/** Args for conversation_continuity tool */
export interface ConversationContinuityArgs {
  sessionId?: string | null;
  scope?: 'active' | 'all';
  activitySeconds?: number;
  includeTail?: boolean;
}

/** Read-only handoff packet for keeping the voice conversation flowing */
export interface ConversationContinuityResult {
  success: boolean;
  scope: 'active' | 'all';
  activeSessionId: string | null;
  generatedAt: string;
  responseText: string;
  nextAction: string;
  sessions: ConversationContinuitySession[];
  attentionSessionIds: string[];
  busySessionIds: string[];
  completeSessionIds: string[];
}

/** Per-session continuity summary */
export interface ConversationContinuitySession {
  sessionId: string;
  title: string;
  isActive: boolean;
  status: string;
  state: string;
  stateLabel: string;
  needsAttention: boolean;
  attentionReason: string | null;
  summary: string;
  nextAction: string;
  latestActivities: unknown[];
  tailText?: string;
}

/** Session state for voice assistant */
export interface VoiceSessionState {
  id: string;
  userTitle: string | null;
  terminalTitle: string | null;
  foregroundName: string | null;
  foregroundCommandLine: string | null;
  currentDirectory: string | null;
  shell: string;
  cols: number;
  rows: number;
  isRunning: boolean;
  isActive: boolean;
  screenContent: string;
}

/** Bell notification for voice assistant */
export interface BellNotification {
  sessionId: string;
  timestamp: string;
}

/** Result of make_input tool */
export interface MakeInputResult {
  success: boolean;
  screenContent: string;
  cols: number;
  rows: number;
}

/** Result of read_scrollback tool */
export interface ReadScrollbackResult {
  content: string;
  totalLines: number;
  returnedLines: number;
  startLine: number;
}

/** Result of interactive_read tool */
export interface InteractiveReadResult {
  results: InteractiveOpResult[];
}

/** Single operation result */
export interface InteractiveOpResult {
  index: number;
  success: boolean;
  screenshot?: string;
}

/** Args for create_session tool */
export interface CreateSessionArgs {
  shellType?: string;
  workingDirectory?: string;
}

/** Args for select_session tool */
export interface SelectSessionArgs {
  sessionId: string;
  focusTerminal?: boolean;
}

/** Args for send_prompt tool */
export interface SendPromptArgs {
  sessionId: string;
  text: string;
  interruptFirst?: boolean;
  profile?: string | null;
  justification: string;
}

/** Args for session_activity tool */
export interface SessionActivityArgs {
  sessionId?: string | null;
  tailLines?: number;
  activitySeconds?: number;
  bellLimit?: number;
}

/** Args for session_turn_summary tool */
export interface SessionTurnSummaryArgs {
  sessionId: string;
  tailLines?: number;
  activitySeconds?: number;
  bellLimit?: number;
}

/** Compact lifecycle status for a MidTerm agent turn */
export type SessionTurnStatus =
  | 'complete'
  | 'busy'
  | 'needs_user'
  | 'blocked'
  | 'shell'
  | 'unknown';

/** Args for wait_for_turn_completion tool */
export interface WaitForTurnCompletionArgs {
  sessionId: string;
  timeoutMs?: number;
  pollIntervalMs?: number;
  activitySeconds?: number;
  includeTail?: boolean;
}

/** Result of wait_for_turn_completion tool */
export interface WaitForTurnCompletionResult {
  success: boolean;
  sessionId: string;
  title: string;
  completed: boolean;
  timedOut: boolean;
  status: SessionTurnStatus;
  state: string;
  stateLabel: string;
  elapsedMs: number;
  pollCount: number;
  settledAt: string;
  responseText: string;
  summary: string;
  nextAction: string;
  needsAttention: boolean;
  attentionReason: string | null;
  latestActivities: unknown[];
  tailText?: string;
}

/** Args for dev_browser_open tool */
export interface DevBrowserOpenArgs {
  sessionId?: string | null;
  previewName?: string | null;
  url: string;
}

/** Args for dev_browser_status tool */
export interface DevBrowserStatusArgs {
  sessionId?: string | null;
  previewName?: string | null;
  previewId?: string | null;
}

/** Args for dev_browser_command tool */
export interface DevBrowserCommandArgs {
  sessionId?: string | null;
  previewName?: string | null;
  previewId?: string | null;
  command: string;
  selector?: string | null;
  value?: string | null;
  maxDepth?: number;
  textOnly?: boolean;
  timeout?: number;
  justification?: string;
}

/** Args for dev_browser_screenshot tool */
export interface DevBrowserScreenshotArgs {
  sessionId?: string | null;
  previewName?: string | null;
  previewId?: string | null;
}

/** Args for repo_monitor tool */
export interface RepoMonitorArgs {
  action: 'list' | 'add' | 'remove' | 'refresh';
  sessionId?: string | null;
  path?: string | null;
  repoRoot?: string | null;
  role?: string | null;
  label?: string | null;
  justification?: string;
}

/** Args for layout_control tool */
export interface LayoutControlArgs {
  action: 'status' | 'focus' | 'dock' | 'undock' | 'swap' | 'clear';
  sessionId?: string | null;
  targetSessionId?: string | null;
  otherSessionId?: string | null;
  position?: DockPosition | null;
  focusTerminal?: boolean;
  justification?: string;
}

/** Args for close_session tool */
export interface CloseSessionArgs {
  sessionId: string;
  justification: string;
}

/** Args for bookmarks tool */
export interface BookmarksArgs {
  action: string;
  bookmarkId?: string;
}

/** Pending tool confirmation request */
export interface PendingToolConfirmation {
  requestId: string;
  tool: VoiceToolName;
  args: Record<string, unknown>;
  justification?: string;
  displayText: string;
  resolve: (approved: boolean) => void;
}

// =============================================================================
// Layout Types
// =============================================================================

/** Direction of a split layout */
export type LayoutDirection = 'horizontal' | 'vertical';

/** Position for docking a session relative to another */
export type DockPosition = 'top' | 'bottom' | 'left' | 'right';

/** A pane can be either a terminal session or a nested split */
export type LayoutNode = LayoutLeaf | LayoutSplit;

/** Leaf node - contains a terminal session */
export interface LayoutLeaf {
  type: 'leaf';
  sessionId: string;
}

/** Split node - contains children arranged in a direction */
export interface LayoutSplit {
  type: 'split';
  direction: LayoutDirection;
  children: LayoutNode[];
}

/** Root layout for the display (null = single standalone session) */
export interface DisplayLayout {
  root: LayoutNode | null;
}

// =============================================================================
// Manager Bar Queue Types
// =============================================================================

export interface ManagerBarQueueScheduleEntry {
  timeOfDay: string;
  repeat: 'daily' | 'weekdays' | 'weekends';
}

export interface ManagerBarQueueAttachment {
  kind: string;
  path: string;
  mimeType?: string | null;
  displayName?: string | null;
}

export interface ManagerBarQueueTurn {
  text?: string | null;
  model?: string | null;
  effort?: string | null;
  planMode?: string | null;
  permissionMode?: string | null;
  attachments: ManagerBarQueueAttachment[];
}

export interface ManagerBarQueueTrigger {
  kind: 'fireAndForget' | 'onCooldown' | 'repeatCount' | 'repeatInterval' | 'schedule';
  repeatCount: number;
  repeatEveryValue: number;
  repeatEveryUnit: 'seconds' | 'minutes' | 'hours' | 'days';
  schedule: ManagerBarQueueScheduleEntry[];
}

export interface ManagerBarQueueAction {
  id: string;
  label: string;
  text: string;
  actionType: 'single' | 'chain';
  prompts: string[];
  trigger: ManagerBarQueueTrigger;
}

export interface ManagerBarQueueEntry {
  queueId: string;
  sessionId: string;
  kind: 'automation' | 'prompt';
  action?: ManagerBarQueueAction | null;
  turn?: ManagerBarQueueTurn | null;
  phase:
    | 'pendingImmediate'
    | 'pendingCooldown'
    | 'chainCooldown'
    | 'pendingInterval'
    | 'pendingSchedule';
  nextPromptIndex: number;
  completedCycles: number;
  nextRunAt: string | null;
  ignoreHeatUntil: string | null;
  awaitingHeatRise: boolean;
}

// =============================================================================
// DOM Element Cache
// =============================================================================

/** Cached DOM elements */
export interface DOMElements {
  sessionList: HTMLElement | null;
  sessionFilterBar: HTMLElement | null;
  sessionFilterInput: HTMLInputElement | null;
  sessionFilterClear: HTMLButtonElement | null;
  terminalsArea: HTMLElement | null;
  emptyState: HTMLElement | null;
  mobileTitle: HTMLElement | null;
  topbarActions: HTMLElement | null;
  app: HTMLElement | null;
  sidebarOverlay: HTMLElement | null;
  settingsView: HTMLElement | null;
  settingsBtn: HTMLElement | null;
  titleBarCustom: HTMLElement | null;
  titleBarTerminal: HTMLElement | null;
  titleBarSeparator: HTMLElement | null;
}
