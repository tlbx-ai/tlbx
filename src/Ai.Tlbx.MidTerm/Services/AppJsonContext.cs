using System.Text.Json.Serialization;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models;
using Ai.Tlbx.MidTerm.Models.Update;
using Ai.Tlbx.MidTerm.Settings;
using Microsoft.AspNetCore.Mvc;

using Ai.Tlbx.MidTerm.Models.Auth;
using Ai.Tlbx.MidTerm.Models.Certificates;
using Ai.Tlbx.MidTerm.Models.Files;
using Ai.Tlbx.MidTerm.Models.History;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Models.System;
using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Models.Share;
using Ai.Tlbx.MidTerm.Models.Security;
using Ai.Tlbx.MidTerm.Models.WebPreview;
using Ai.Tlbx.MidTerm.Models.Hub;
using Ai.Tlbx.MidTerm.Models.Spaces;
using Ai.Tlbx.MidTerm.Models.Git;
using Ai.Tlbx.MidTerm.Services.Git;
using Ai.Tlbx.MidTerm.Services.Security;
namespace Ai.Tlbx.MidTerm.Services;

// --- Bootstrap & System ---
[JsonSerializable(typeof(BootstrapResponse))]
[JsonSerializable(typeof(BootstrapLoginResponse))]
[JsonSerializable(typeof(FeatureFlags))]
[JsonSerializable(typeof(SystemResponse))]
[JsonSerializable(typeof(SystemHealth))]
[JsonSerializable(typeof(TtyHostInfo))]
[JsonSerializable(typeof(PathsResponse))]
[JsonSerializable(typeof(BackgroundImageInfoResponse))]
[JsonSerializable(typeof(List<NetworkInterfaceDto>))]
[JsonSerializable(typeof(ShellInfoDto))]
[JsonSerializable(typeof(List<ShellInfoDto>))]
[JsonSerializable(typeof(UserInfo))]
[JsonSerializable(typeof(List<UserInfo>))]
[JsonSerializable(typeof(ProblemDetails))]

// --- Auth ---
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(ChangePasswordRequest))]
[JsonSerializable(typeof(AuthResponse))]
[JsonSerializable(typeof(AuthStatusResponse))]
[JsonSerializable(typeof(SecurityStatus))]
[JsonSerializable(typeof(ApiKeyInfoResponse))]
[JsonSerializable(typeof(List<ApiKeyInfoResponse>))]
[JsonSerializable(typeof(ApiKeyListResponse))]
[JsonSerializable(typeof(CreateApiKeyRequest))]
[JsonSerializable(typeof(CreateApiKeyResponse))]
[JsonSerializable(typeof(FirewallRuleStatusResponse))]
[JsonSerializable(typeof(FirewallRuleSnapshot))]
[JsonSerializable(typeof(List<StoredApiKeyRecord>))]

// --- Sessions ---
[JsonSerializable(typeof(SessionListDto))]
[JsonSerializable(typeof(SessionInfoDto))]
[JsonSerializable(typeof(SessionAgentAttachPoint))]
[JsonSerializable(typeof(CreateSessionRequest))]
[JsonSerializable(typeof(SessionReorderRequest))]
[JsonSerializable(typeof(SessionInputRequest))]
[JsonSerializable(typeof(SessionKeyInputRequest))]
[JsonSerializable(typeof(SessionPromptRequest))]
[JsonSerializable(typeof(SessionSupervisorInfoDto))]
[JsonSerializable(typeof(SessionAttentionResponse))]
[JsonSerializable(typeof(SessionAttentionItem))]
[JsonSerializable(typeof(List<SessionAttentionItem>))]
[JsonSerializable(typeof(WorkerBootstrapRequest))]
[JsonSerializable(typeof(WorkerBootstrapResponse))]
[JsonSerializable(typeof(ProviderResumeCatalogEntryDto))]
[JsonSerializable(typeof(List<ProviderResumeCatalogEntryDto>))]
[JsonSerializable(typeof(AppServerControlTurnRequest))]
[JsonSerializable(typeof(AppServerControlGoalSetRequest))]
[JsonSerializable(typeof(AppServerControlAttachmentReference))]
[JsonSerializable(typeof(List<AppServerControlAttachmentReference>))]
[JsonSerializable(typeof(AppServerControlTerminalReplayStep))]
[JsonSerializable(typeof(List<AppServerControlTerminalReplayStep>))]
[JsonSerializable(typeof(AppServerControlTurnStartResponse))]
[JsonSerializable(typeof(AppServerControlInterruptRequest))]
[JsonSerializable(typeof(AppServerControlRequestDecisionRequest))]
[JsonSerializable(typeof(AppServerControlUserInputAnswerRequest))]
[JsonSerializable(typeof(AppServerControlCommandAcceptedResponse))]
[JsonSerializable(typeof(AppServerControlHistoryWindowRequest))]
[JsonSerializable(typeof(AppServerControlWsRequestMessage))]
[JsonSerializable(typeof(AppServerControlWsSubscriptionMessage))]
[JsonSerializable(typeof(AppServerControlWsAckMessage))]
[JsonSerializable(typeof(AppServerControlWsErrorMessage))]
[JsonSerializable(typeof(AppServerControlWsHistoryWindowMessage))]
[JsonSerializable(typeof(AppServerControlWsHistoryPatchMessage))]
[JsonSerializable(typeof(AppServerControlWsTurnStartedMessage))]
[JsonSerializable(typeof(AppServerControlWsCommandAcceptedMessage))]
[JsonSerializable(typeof(RenameSessionRequest))]
[JsonSerializable(typeof(SetSessionNotesRequest))]
[JsonSerializable(typeof(SetSessionTopicRequest))]
[JsonSerializable(typeof(SetSessionControlRequest))]
[JsonSerializable(typeof(ResizeRequest))]
[JsonSerializable(typeof(ResizeResponse))]
[JsonSerializable(typeof(FileUploadResponse))]
[JsonSerializable(typeof(InjectGuidanceResponse))]
[JsonSerializable(typeof(SessionActivityResponse))]
[JsonSerializable(typeof(SessionActivityHeatSample))]
[JsonSerializable(typeof(List<SessionActivityHeatSample>))]
[JsonSerializable(typeof(SessionBellEvent))]
[JsonSerializable(typeof(List<SessionBellEvent>))]
[JsonSerializable(typeof(AgentSessionVibeResponse))]
[JsonSerializable(typeof(AgentSessionVibeHeader))]
[JsonSerializable(typeof(AgentSessionVibeChip))]
[JsonSerializable(typeof(List<AgentSessionVibeChip>))]
[JsonSerializable(typeof(AgentSessionVibeLane))]
[JsonSerializable(typeof(AgentSessionVibeCapability))]
[JsonSerializable(typeof(List<AgentSessionVibeCapability>))]
[JsonSerializable(typeof(AgentSessionVibeOverview))]
[JsonSerializable(typeof(AgentSessionVibeActivity))]
[JsonSerializable(typeof(List<AgentSessionVibeActivity>))]
[JsonSerializable(typeof(AgentSessionVibeTerminal))]
[JsonSerializable(typeof(AgentSessionFeedResponse))]
[JsonSerializable(typeof(AppServerControlQuickSettingsPayload))]
[JsonSerializable(typeof(AppServerControlQuickSettingsOption))]
[JsonSerializable(typeof(AppServerControlQuestion))]
[JsonSerializable(typeof(List<AppServerControlQuestion>))]
[JsonSerializable(typeof(AppServerControlQuestionOption))]
[JsonSerializable(typeof(List<AppServerControlQuestionOption>))]
[JsonSerializable(typeof(AppServerControlAnsweredQuestion))]
[JsonSerializable(typeof(List<AppServerControlAnsweredQuestion>))]
[JsonSerializable(typeof(AppServerControlHistoryWindowResponse))]
[JsonSerializable(typeof(AppServerControlHistoryPatch))]
[JsonSerializable(typeof(AppServerControlSessionSummary))]
[JsonSerializable(typeof(AppServerControlThreadSummary))]
[JsonSerializable(typeof(AppServerControlTurnSummary))]
[JsonSerializable(typeof(AppServerControlQuickSettingsSummary))]
[JsonSerializable(typeof(AppServerControlStreamsSummary))]
[JsonSerializable(typeof(AppServerControlItemSummary))]
[JsonSerializable(typeof(List<AppServerControlItemSummary>))]
[JsonSerializable(typeof(AppServerControlRequestSummary))]
[JsonSerializable(typeof(List<AppServerControlRequestSummary>))]
[JsonSerializable(typeof(AppServerControlRuntimeNotice))]
[JsonSerializable(typeof(List<AppServerControlRuntimeNotice>))]
[JsonSerializable(typeof(SessionBufferTextResponse))]
[JsonSerializable(typeof(TerminalTransportDiagnosticsDto))]
[JsonSerializable(typeof(SessionStateResponse))]
[JsonSerializable(typeof(SessionLayoutState))]
[JsonSerializable(typeof(ManagerBarQueueEntryDto))]
[JsonSerializable(typeof(List<ManagerBarQueueEntryDto>))]
[JsonSerializable(typeof(ManagerBarQueueEnqueueRequest))]

// --- Files ---
[JsonSerializable(typeof(FileCheckRequest))]
[JsonSerializable(typeof(FileCheckResponse))]
[JsonSerializable(typeof(FileRegisterRequest))]
[JsonSerializable(typeof(FileResolveResponse))]
[JsonSerializable(typeof(FilePathInfo))]
[JsonSerializable(typeof(DirectoryListResponse))]
[JsonSerializable(typeof(DirectoryEntry))]
[JsonSerializable(typeof(Dictionary<string, FilePathInfo>))]
[JsonSerializable(typeof(FileTreeResponse))]
[JsonSerializable(typeof(FileTreeEntry))]
[JsonSerializable(typeof(FileTreeEntry[]))]
[JsonSerializable(typeof(FileSaveRequest))]
[JsonSerializable(typeof(FileSaveResponse))]
[JsonSerializable(typeof(LauncherPathResponse))]
[JsonSerializable(typeof(LauncherDirectoryEntry))]
[JsonSerializable(typeof(LauncherDirectoryEntry[]))]
[JsonSerializable(typeof(LauncherDirectoryListResponse))]
[JsonSerializable(typeof(LauncherDirectoryAccessResponse))]
[JsonSerializable(typeof(LauncherCreateDirectoryRequest))]
[JsonSerializable(typeof(LauncherCloneRepositoryRequest))]
[JsonSerializable(typeof(LauncherDirectoryMutationResponse))]

// --- History ---
[JsonSerializable(typeof(HistoryPatchRequest))]
[JsonSerializable(typeof(HistoryReorderRequest))]
[JsonSerializable(typeof(LaunchEntry))]
[JsonSerializable(typeof(List<LaunchEntry>))]
[JsonSerializable(typeof(CreateHistoryRequest))]
[JsonSerializable(typeof(CreateHistoryResponse))]
[JsonSerializable(typeof(SetBookmarkRequest))]
[JsonSerializable(typeof(SpaceSummaryDto))]
[JsonSerializable(typeof(List<SpaceSummaryDto>))]
[JsonSerializable(typeof(SpaceWorkspaceDto))]
[JsonSerializable(typeof(List<SpaceWorkspaceDto>))]
[JsonSerializable(typeof(SpaceWorkspaceSessionDto))]
[JsonSerializable(typeof(List<SpaceWorkspaceSessionDto>))]
[JsonSerializable(typeof(SpaceWorktreeRecord))]
[JsonSerializable(typeof(List<SpaceWorktreeRecord>))]
[JsonSerializable(typeof(SpaceImportRequest))]
[JsonSerializable(typeof(SpaceUpdateRequest))]
[JsonSerializable(typeof(SpaceCreateWorktreeRequest))]
[JsonSerializable(typeof(SpaceUpdateWorkspaceRequest))]
[JsonSerializable(typeof(SpaceDeleteWorktreeRequest))]
[JsonSerializable(typeof(SpaceLaunchRequest))]

// --- Commands ---
[JsonSerializable(typeof(ScriptDefinition))]
[JsonSerializable(typeof(ScriptListResponse))]
[JsonSerializable(typeof(CreateScriptRequest))]
[JsonSerializable(typeof(UpdateScriptRequest))]
[JsonSerializable(typeof(RunScriptRequest))]
[JsonSerializable(typeof(RunScriptResponse))]
[JsonSerializable(typeof(StopScriptRequest))]

// --- Certificates ---
[JsonSerializable(typeof(CertificateInfoResponse))]
[JsonSerializable(typeof(CertificateDownloadInfo))]
[JsonSerializable(typeof(SharePacketInfo))]
[JsonSerializable(typeof(NetworkEndpointInfo))]
[JsonSerializable(typeof(NetworkEndpointInfo[]))]

// --- Share ---
[JsonSerializable(typeof(CreateShareLinkRequest))]
[JsonSerializable(typeof(CreateShareLinkResponse))]
[JsonSerializable(typeof(ActiveShareGrantInfo))]
[JsonSerializable(typeof(List<ActiveShareGrantInfo>))]
[JsonSerializable(typeof(ActiveShareGrantListResponse))]
[JsonSerializable(typeof(ClaimShareRequest))]
[JsonSerializable(typeof(ClaimShareResponse))]
[JsonSerializable(typeof(ShareBootstrapResponse))]
[JsonSerializable(typeof(ShareAccessMode))]

// --- Settings ---
[JsonSerializable(typeof(MidTermSettings))]
[JsonSerializable(typeof(MidTermSettingsPublic))]
[JsonSerializable(typeof(TerminalColorSchemeDefinition))]
[JsonSerializable(typeof(List<TerminalColorSchemeDefinition>))]
[JsonSerializable(typeof(CursorStyleSetting))]
[JsonSerializable(typeof(ThemeSetting))]
[JsonSerializable(typeof(BellStyleSetting))]
[JsonSerializable(typeof(ClipboardShortcutsSetting))]
[JsonSerializable(typeof(ScrollbarStyleSetting))]
[JsonSerializable(typeof(LanguageSetting))]
[JsonSerializable(typeof(ManagerBarButton))]
[JsonSerializable(typeof(List<ManagerBarButton>))]
[JsonSerializable(typeof(ManagerBarTrigger))]
[JsonSerializable(typeof(ManagerBarScheduleEntry))]
[JsonSerializable(typeof(List<ManagerBarScheduleEntry>))]
[JsonSerializable(typeof(HubMachineSettings))]
[JsonSerializable(typeof(List<HubMachineSettings>))]
[JsonSerializable(typeof(HubMachineInfo))]
[JsonSerializable(typeof(List<HubMachineInfo>))]
[JsonSerializable(typeof(HubMachineState))]
[JsonSerializable(typeof(List<HubMachineState>))]
[JsonSerializable(typeof(HubStateResponse))]
[JsonSerializable(typeof(HubMachineUpsertRequest))]
[JsonSerializable(typeof(HubMachinePinRequest))]
[JsonSerializable(typeof(HubUpdateRolloutRequest))]
[JsonSerializable(typeof(HubUpdateRolloutItem))]
[JsonSerializable(typeof(List<HubUpdateRolloutItem>))]
[JsonSerializable(typeof(HubUpdateRolloutResponse))]

// --- Git ---
[JsonSerializable(typeof(GitStatusResponse))]
[JsonSerializable(typeof(GitRepoBinding))]
[JsonSerializable(typeof(GitRepoBinding[]))]
[JsonSerializable(typeof(GitRepoListResponse))]
[JsonSerializable(typeof(GitRepoBindRequest))]
[JsonSerializable(typeof(GitRepoRefreshRequest))]
[JsonSerializable(typeof(GitFileEntry))]
[JsonSerializable(typeof(GitFileEntry[]))]
[JsonSerializable(typeof(GitLogEntry))]
[JsonSerializable(typeof(GitLogEntry[]))]
[JsonSerializable(typeof(GitDiffViewResponse))]
[JsonSerializable(typeof(GitDiffFileView))]
[JsonSerializable(typeof(GitDiffFileView[]))]
[JsonSerializable(typeof(GitDiffHunk))]
[JsonSerializable(typeof(GitDiffHunk[]))]
[JsonSerializable(typeof(GitDiffLine))]
[JsonSerializable(typeof(GitDiffLine[]))]
[JsonSerializable(typeof(GitCommitDetailsResponse))]
[JsonSerializable(typeof(GitWsMessage))]
[JsonSerializable(typeof(GitDebugResponse))]
[JsonSerializable(typeof(GitDebugSessionInfo))]
[JsonSerializable(typeof(GitDebugSessionInfo[]))]
[JsonSerializable(typeof(GitCommandLog))]

// --- WebPreview ---
[JsonSerializable(typeof(WebPreviewTargetRequest))]
[JsonSerializable(typeof(WebPreviewTargetResponse))]
[JsonSerializable(typeof(WebPreviewSessionRequest))]
[JsonSerializable(typeof(WebPreviewSessionInfo))]
[JsonSerializable(typeof(WebPreviewSessionListResponse))]
[JsonSerializable(typeof(WebPreviewCookieSetRequest))]
[JsonSerializable(typeof(WebPreviewCookiesResponse))]
[JsonSerializable(typeof(WebPreviewCookieInfo))]
[JsonSerializable(typeof(WebPreviewReloadRequest))]
[JsonSerializable(typeof(WebPreviewSnapshotRequest))]
[JsonSerializable(typeof(WebPreviewSnapshotResponse))]
[JsonSerializable(typeof(WebPreviewProxyLogEntry))]
[JsonSerializable(typeof(List<WebPreviewProxyLogEntry>))]

// --- Updates ---
[JsonSerializable(typeof(UpdateInfo))]
[JsonSerializable(typeof(LocalUpdateInfo))]
[JsonSerializable(typeof(UpdateType))]
[JsonSerializable(typeof(UpdateResult))]
[JsonSerializable(typeof(VersionManifest))]

// --- Browser ---
[JsonSerializable(typeof(BrowserCommandRequest))]
[JsonSerializable(typeof(BrowserCommandResponse))]
[JsonSerializable(typeof(BrowserCapabilitiesResponse))]
[JsonSerializable(typeof(BrowserStatusResponse))]
[JsonSerializable(typeof(BrowserClientInfo))]
[JsonSerializable(typeof(BrowserClientInfo[]))]
[JsonSerializable(typeof(BrowserPreviewClientRequest))]
[JsonSerializable(typeof(BrowserPreviewClientResponse))]
[JsonSerializable(typeof(BrowserWsMessage))]
[JsonSerializable(typeof(BrowserWsResult))]
[JsonSerializable(typeof(BrowserUiInstruction))]
[JsonSerializable(typeof(ViewportRequest))]

// --- WebSocket Protocol ---
[JsonSerializable(typeof(WsCommand))]
[JsonSerializable(typeof(WsCommandPayload))]
[JsonSerializable(typeof(WsCommandResponse))]
[JsonSerializable(typeof(WsSessionCreatedData))]
[JsonSerializable(typeof(StateUpdate))]
[JsonSerializable(typeof(SettingsWsMessage))]
[JsonSerializable(typeof(MainBrowserStatusMessage))]
[JsonSerializable(typeof(LayoutNode))]
[JsonSerializable(typeof(List<LayoutNode>))]

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
public partial class AppJsonContext : JsonSerializerContext
{
}



















