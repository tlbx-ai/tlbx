/**
 * API Types Module
 *
 * Central import point for all API types. Re-exports generated types from
 * api.generated.ts and defines client-extended types.
 *
 * IMPORTANT: This is the ONLY place API types should be imported from.
 * Never import directly from api.generated.ts in application code.
 */

import type { components } from '../api.generated';

// =============================================================================
// Schema Types Access
// =============================================================================

export type Schemas = components['schemas'];

// =============================================================================
// API Response Types (re-exported from generated)
// =============================================================================

// Auth
export type AuthResponse = Schemas['AuthResponse'];
export type AuthStatusResponse = Schemas['AuthStatusResponse'];
export type LoginRequest = Schemas['LoginRequest'];
export type ChangePasswordRequest = Schemas['ChangePasswordRequest'];

// Bootstrap
export type BootstrapResponse = Schemas['BootstrapResponse'];
export type BootstrapLoginResponse = Schemas['BootstrapLoginResponse'];

// Sessions
export type SessionInfoDto = Omit<Schemas['SessionInfoDto'], 'notes' | 'topic'> & {
  appServerControlOnly?: boolean;
  profileHint?: string | null;
  appServerControlResumeThreadId?: string | null;
  topic?: string | null;
  notes?: string | null;
  spaceId?: string | null;
  workspacePath?: string | null;
  surface?: string | null;
  isAdHoc?: boolean;
};
export type SessionListDto = Schemas['SessionListDto'];
export type CreateSessionRequest = Schemas['CreateSessionRequest'];
export type WorkerBootstrapRequest = Schemas['WorkerBootstrapRequest'] & {
  appServerControlOnly?: boolean;
  resumeThreadId?: string | null;
};
export type WorkerBootstrapResponse = Schemas['WorkerBootstrapResponse'];
export interface ProviderResumeCatalogEntryDto {
  provider: string;
  sessionId: string;
  workingDirectory: string;
  title: string;
  previewText?: string | null;
  updatedAtUtc: string;
}
export type RenameSessionRequest = Schemas['RenameSessionRequest'];
export type ResizeRequest = Schemas['ResizeRequest'];
export type ResizeResponse = Schemas['ResizeResponse'];
export type SessionPromptRequest = Schemas['SessionPromptRequest'];
export type SessionStateResponse = Schemas['SessionStateResponse'];
export type SessionBufferTextResponse = Schemas['SessionBufferTextResponse'];
export type AgentSessionFeedResponse = Schemas['AgentSessionFeedResponse'];
export type AgentSessionVibeChip = Schemas['AgentSessionVibeChip'];
export type AgentSessionVibeHeader = Schemas['AgentSessionVibeHeader'];
export type AgentSessionVibeLane = Schemas['AgentSessionVibeLane'];
export type AgentSessionVibeCapability = Schemas['AgentSessionVibeCapability'];
export type AgentSessionVibeOverview = Schemas['AgentSessionVibeOverview'];
export type AgentSessionVibeActivity = Schemas['AgentSessionVibeActivity'];
export type AgentSessionVibeHeatSample = Schemas['SessionActivityHeatSample'];
export type AgentSessionVibeTerminal = Schemas['AgentSessionVibeTerminal'];
export type AgentSessionVibeResponse = Schemas['AgentSessionVibeResponse'];
export interface AppServerControlAttachmentReference {
  kind: string;
  path: string;
  mimeType?: string | null;
  displayName?: string | null;
}

export interface AppServerControlInlineFileReference {
  field: 'title' | 'body' | 'commandText';
  displayText: string;
  path: string;
  pathKind: 'absolute' | 'relative';
  resolvedPath?: string | null;
  exists: boolean;
  isDirectory: boolean;
  mimeType?: string | null;
  line?: number | null;
  column?: number | null;
}

export interface AppServerControlInlineImagePreview {
  displayPath: string;
  resolvedPath: string;
  mimeType?: string | null;
}

export interface AppServerControlCommandAcceptedResponse {
  sessionId: string;
  status: string;
  requestId?: string | null;
  turnId?: string | null;
}

export interface AppServerControlInterruptRequest {
  turnId?: string | null;
}

export interface AppServerControlGoalSetRequest {
  objective: string;
}

export interface AppServerControlAnsweredQuestion {
  questionId: string;
  answers: string[];
}

export interface AppServerControlQuickSettingsOption {
  value: string;
  label: string;
  description?: string | null;
  hidden?: boolean;
  isDefault?: boolean;
}

export interface AppServerControlQuickSettingsPayload {
  model?: string | null;
  effort?: string | null;
  planMode: string;
  permissionMode: string;
  modelOptions?: AppServerControlQuickSettingsOption[];
  effortOptions?: AppServerControlQuickSettingsOption[];
}

export interface AppServerControlQuickSettingsSummary {
  model?: string | null;
  effort?: string | null;
  planMode: string;
  permissionMode: string;
  modelOptions?: AppServerControlQuickSettingsOption[];
  effortOptions?: AppServerControlQuickSettingsOption[];
}

export interface AppServerControlQuestionOption {
  label: string;
  description: string;
}

export interface AppServerControlQuestion {
  id: string;
  header: string;
  question: string;
  multiSelect: boolean;
  options: AppServerControlQuestionOption[];
}

export interface AppServerControlHistoryPatch {
  sessionId: string;
  provider: string;
  generatedAt: string;
  latestSequence: number;
  historyCount: number;
  session: AppServerControlSessionSummary;
  thread: AppServerControlThreadSummary;
  currentTurn: AppServerControlTurnSummary;
  quickSettings: AppServerControlQuickSettingsSummary;
  streams: AppServerControlStreamsSummary;
  historyUpserts: AppServerControlHistoryItem[];
  historyRemovals: string[];
  itemUpserts: AppServerControlItemSummary[];
  itemRemovals: string[];
  requestUpserts: AppServerControlRequestSummary[];
  requestRemovals: string[];
  noticeUpserts: AppServerControlRuntimeNotice[];
}

export type AppServerControlHistoryDelta = AppServerControlHistoryPatch;

export interface AppServerControlSessionSummary {
  state: string;
  stateLabel: string;
  reason?: string | null;
  lastError?: string | null;
  lastEventAt?: string | null;
}

export interface AppServerControlThreadSummary {
  threadId: string;
  state: string;
  stateLabel: string;
}

export interface AppServerControlTurnSummary {
  turnId?: string | null;
  state: string;
  stateLabel: string;
  model?: string | null;
  effort?: string | null;
  startedAt?: string | null;
  completedAt?: string | null;
}

export interface AppServerControlStreamsSummary {
  assistantText: string;
  reasoningText: string;
  reasoningSummaryText: string;
  planText: string;
  commandOutput: string;
  fileChangeOutput: string;
  unifiedDiff: string;
}

export interface AppServerControlHistoryItem {
  entryId: string;
  order: number;
  estimatedHeightPx?: number;
  kind: string;
  turnId?: string | null;
  itemId?: string | null;
  requestId?: string | null;
  status: string;
  itemType?: string | null;
  title?: string | null;
  commandText?: string | null;
  body: string;
  attachments: AppServerControlAttachmentReference[];
  fileMentions?: AppServerControlInlineFileReference[];
  imagePreviews?: AppServerControlInlineImagePreview[];
  streaming: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface AppServerControlItemSummary {
  itemId: string;
  turnId?: string | null;
  itemType: string;
  status: string;
  title?: string | null;
  detail?: string | null;
  attachments: AppServerControlAttachmentReference[];
  updatedAt: string;
}

export interface AppServerControlRequestSummary {
  requestId: string;
  turnId?: string | null;
  kind: string;
  kindLabel: string;
  state: string;
  detail?: string | null;
  decision?: string | null;
  questions: AppServerControlQuestion[];
  answers: AppServerControlAnsweredQuestion[];
  updatedAt: string;
}

export interface AppServerControlRuntimeNotice {
  eventId: string;
  type: string;
  message: string;
  detail?: string | null;
  createdAt: string;
}

export interface AppServerControlHistoryWindowResponse {
  sessionId: string;
  provider: string;
  generatedAt: string;
  windowRevision?: string | null;
  latestSequence: number;
  historyCount: number;
  historyWindowStart: number;
  historyWindowEnd: number;
  hasOlderHistory: boolean;
  hasNewerHistory: boolean;
  session: AppServerControlSessionSummary;
  thread: AppServerControlThreadSummary;
  currentTurn: AppServerControlTurnSummary;
  quickSettings: AppServerControlQuickSettingsSummary;
  streams: AppServerControlStreamsSummary;
  history: AppServerControlHistoryItem[];
  items: AppServerControlItemSummary[];
  requests: AppServerControlRequestSummary[];
  notices: AppServerControlRuntimeNotice[];
}

export type AppServerControlHistoryWindow = AppServerControlHistoryWindowResponse;
export type AppServerControlHistorySnapshot = AppServerControlHistoryWindowResponse;
export type AppServerControlHistoryRequestSummary = AppServerControlRequestSummary;
export type AppServerControlHistorySessionSummary = AppServerControlSessionSummary;
export type AppServerControlHistoryThreadSummary = AppServerControlThreadSummary;
export type AppServerControlHistoryTurnSummary = AppServerControlTurnSummary;
export type AppServerControlHistoryStreamsSummary = AppServerControlStreamsSummary;
export type AppServerControlHistoryRuntimeNotice = AppServerControlRuntimeNotice;

export interface AppServerControlRequestDecisionRequest {
  decision: string;
}

export interface AppServerControlTerminalReplayStep {
  kind: 'text' | 'image' | 'filePath' | 'textFile';
  text?: string | null;
  path?: string | null;
  mimeType?: string | null;
  useBracketedPaste?: boolean;
}

export interface AppServerControlTurnRequest {
  text?: string | null;
  model?: string | null;
  effort?: string | null;
  planMode?: string | null;
  permissionMode?: string | null;
  attachments: AppServerControlAttachmentReference[];
  terminalReplay?: AppServerControlTerminalReplayStep[];
}

export interface AppServerControlTurnStartResponse {
  sessionId: string;
  provider: string;
  threadId: string;
  turnId?: string | null;
  status: string;
  quickSettings: AppServerControlQuickSettingsSummary;
}

export interface AppServerControlUserInputAnswerRequest {
  answers: AppServerControlAnsweredQuestion[];
}

// Settings
export type MidTermSettingsPublic = Schemas['MidTermSettingsPublic'];
export type TerminalColorSchemeDefinition = Schemas['TerminalColorSchemeDefinition'];
export type MidTermSettingsUpdate = Omit<
  MidTermSettingsPublic,
  | 'authenticationEnabled'
  | 'backgroundImageFileName'
  | 'backgroundImageRevision'
  | 'runAsUserSid'
  | 'certificatePath'
>;

// System
export type SystemHealth = Schemas['SystemHealth'];
export type SystemResponse = Schemas['SystemResponse'];
export type SecurityStatus = Schemas['SecurityStatus'];
export type ApiKeyInfoResponse = Schemas['ApiKeyInfoResponse'];
export type ApiKeyListResponse = Schemas['ApiKeyListResponse'];
export type CreateApiKeyRequest = Schemas['CreateApiKeyRequest'];
export type CreateApiKeyResponse = Schemas['CreateApiKeyResponse'];
export type FirewallRuleStatusResponse = Schemas['FirewallRuleStatusResponse'];
export type TtyHostInfo = Schemas['TtyHostInfo'];
export type VersionManifest = Schemas['VersionManifest'];
export type PathsResponse = Schemas['PathsResponse'];

// Updates
export type UpdateInfo = Schemas['UpdateInfo'];
export type LocalUpdateInfo = Schemas['LocalUpdateInfo'];
export type UpdateResult = Schemas['UpdateResult'];
export type UpdateType = Schemas['UpdateType'];

// Certificate
export type CertificateInfoResponse = Schemas['CertificateInfoResponse'];
export type CertificateDownloadInfo = Schemas['CertificateDownloadInfo'];

// Share
export type SharePacketInfo = Schemas['SharePacketInfo'];
export type NetworkEndpointInfo = Schemas['NetworkEndpointInfo'];

// Shared sessions
export type ShareAccessMode = Schemas['ShareAccessMode'];
export type CreateShareLinkRequest = Schemas['CreateShareLinkRequest'];
export type CreateShareLinkResponse = Schemas['CreateShareLinkResponse'];
export type ActiveShareGrantInfo = Schemas['ActiveShareGrantInfo'];
export type ActiveShareGrantListResponse = Schemas['ActiveShareGrantListResponse'];
export type ClaimShareRequest = Schemas['ClaimShareRequest'];
export type ClaimShareResponse = Schemas['ClaimShareResponse'];
export type ShareBootstrapResponse = Schemas['ShareBootstrapResponse'];

// Files
export type FilePathInfo = Schemas['FilePathInfo'];
export type FileCheckRequest = Schemas['FileCheckRequest'];
export type FileCheckResponse = Schemas['FileCheckResponse'];
export type FileResolveResponse = Schemas['FileResolveResponse'];
export type FileRegisterRequest = Schemas['FileRegisterRequest'];
export type FileUploadResponse = Schemas['FileUploadResponse'];
export type DirectoryEntry = Schemas['DirectoryEntry'];
export type DirectoryListResponse = Schemas['DirectoryListResponse'];

// History
export type LaunchEntry = Schemas['LaunchEntry'] & {
  notes?: string | null;
  launchOrigin?: string | null;
  surfaceType?: string | null;
  foregroundProcessName?: string | null;
  foregroundProcessCommandLine?: string | null;
  foregroundProcessDisplayName?: string | null;
  foregroundProcessIdentity?: string | null;
};
export type CreateHistoryRequest = Schemas['CreateHistoryRequest'] & {
  notes?: string | null;
  dedupeKey?: string | null;
  launchOrigin?: string | null;
  surfaceType?: 'trm' | 'cdx' | 'cld';
  foregroundProcessName?: string | null;
  foregroundProcessCommandLine?: string | null;
  foregroundProcessDisplayName?: string | null;
  foregroundProcessIdentity?: string | null;
};
export type HistoryPatchRequest = Schemas['HistoryPatchRequest'];

// Spaces
export interface SpaceWorkspaceSessionDto {
  sessionId: string;
  title: string;
  surface: 'terminal' | 'codex' | 'claude';
  appServerControlOnly: boolean;
  profileHint?: string | null;
}

export interface SpaceWorkspaceDto {
  key: string;
  displayName: string;
  path: string;
  kind: 'plain' | 'worktree';
  branch?: string | null;
  head?: string | null;
  isMain: boolean;
  isDetached: boolean;
  locked: boolean;
  prunable: boolean;
  changeCount: number;
  hasChanges: boolean;
  hasActiveAiSession: boolean;
  activeSessions: SpaceWorkspaceSessionDto[];
}

export interface SpaceSummaryDto {
  id: string;
  displayName: string;
  label: string;
  kind: 'plain' | 'git';
  rootPath: string;
  importedPath: string;
  commonRepoId?: string | null;
  isPinned: boolean;
  canInitGit: boolean;
  canCreateWorktree: boolean;
  primaryWorkspaceKey?: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
  workspaces: SpaceWorkspaceDto[];
}

export interface SpaceImportRequest {
  path: string;
  label?: string | null;
}

export interface SpaceUpdateRequest {
  label?: string | null;
  isPinned?: boolean | null;
}

export interface SpaceCreateWorktreeRequest {
  path: string;
  branchName: string;
  name?: string | null;
}

export interface SpaceUpdateWorkspaceRequest {
  label?: string | null;
}

export interface SpaceDeleteWorktreeRequest {
  force?: boolean;
}

export interface SpaceLaunchRequest {
  surface: 'terminal' | 'codex' | 'claude';
  cols?: number;
  rows?: number;
  shell?: string | null;
}

// Shells & Users
export type ShellInfoDto = Schemas['ShellInfoDto'];
export type UserInfo = Schemas['UserInfo'];
export type NetworkInterfaceDto = Schemas['NetworkInterfaceDto'];

// Features
export type FeatureFlags = Schemas['FeatureFlags'];

// =============================================================================
// Enum Types (re-exported from generated)
// =============================================================================

export type ShellType = Schemas['ShellType'];
export type ThemeSetting = Schemas['ThemeSetting'];
export type CursorStyleSetting = Schemas['CursorStyleSetting'];
export type CursorInactiveStyleSetting = Schemas['CursorInactiveStyleSetting'];
export type BellStyleSetting = Schemas['BellStyleSetting'];
export type ClipboardShortcutsSetting = Schemas['ClipboardShortcutsSetting'];
export type TabTitleModeSetting = Schemas['TabTitleModeSetting'];
export type ScrollbarStyleSetting = Schemas['ScrollbarStyleSetting'];
export type TerminalColorSchemeSetting = MidTermSettingsPublic['terminalColorScheme'];
export type LanguageSetting = Schemas['LanguageSetting'];

// =============================================================================
// Client-Extended Types
// =============================================================================

/**
 * Session with client-side properties.
 * Extends the API SessionInfoDto - any API changes will propagate here.
 */
export interface Session extends SessionInfoDto {
  /** Client-side ordering index (used for local sorting before server sync) */
  _order?: number;
}

// =============================================================================
// Type Aliases for Backward Compatibility
// =============================================================================

/** @deprecated Use MidTermSettingsPublic directly */
export type Settings = MidTermSettingsPublic;

/** @deprecated Use AuthStatusResponse directly */
export type AuthStatus = AuthStatusResponse;

/** @deprecated Use SystemHealth directly */
export type HealthResponse = SystemHealth;

/** @deprecated Use ShellInfoDto directly */
export type ShellInfo = ShellInfoDto;

/** @deprecated Use CertificateInfoResponse directly */
export type CertificateInfo = CertificateInfoResponse;

/** @deprecated Use NetworkInterfaceDto directly */
export type NetworkInterface = NetworkInterfaceDto;

// Type aliases for backward compatibility with old naming
export type ThemeName = ThemeSetting;
export type CursorStyle = CursorStyleSetting;
export type CursorInactiveStyle = CursorInactiveStyleSetting;
export type BellStyle = BellStyleSetting;
export type ClipboardShortcuts = ClipboardShortcutsSetting;
export type TabTitleMode = TabTitleModeSetting;
