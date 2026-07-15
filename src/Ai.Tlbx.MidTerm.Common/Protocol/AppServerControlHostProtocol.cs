using System.Text.Json.Serialization;

namespace Ai.Tlbx.MidTerm.Common.Protocol;

public static class AppServerControlHostProtocol
{
    public const string CurrentVersion = "app-server-control-host-v2";
}

public sealed class AppServerControlQuickSettingsPayload
{
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public string PlanMode { get; set; } = AppServerControlQuickSettings.PlanModeOff;
    public string PermissionMode { get; set; } = AppServerControlQuickSettings.PermissionModeManual;
    public List<AppServerControlQuickSettingsOption> ModelOptions { get; set; } = [];
    public List<AppServerControlQuickSettingsOption> EffortOptions { get; set; } = [];
}

public sealed class AppServerControlQuickSettingsOption
{
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Hidden { get; set; }
    public bool IsDefault { get; set; }
}

public sealed class AppServerControlQuestion
{
    public string Id { get; set; } = string.Empty;
    public string Header { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public bool MultiSelect { get; set; }
    public List<AppServerControlQuestionOption> Options { get; set; } = [];
}

public sealed class AppServerControlQuestionOption
{
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class AppServerControlAnsweredQuestion
{
    public string QuestionId { get; set; } = string.Empty;
    public List<string> Answers { get; set; } = [];
}

public sealed class AppServerControlHistoryWindowResponse
{
    public string SessionId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; set; }
    public long LatestSequence { get; set; }
    public int HistoryCount { get; set; }
    public int HistoryWindowStart { get; set; }
    public int HistoryWindowEnd { get; set; }
    public bool HasOlderHistory { get; set; }
    public bool HasNewerHistory { get; set; }
    public AppServerControlSessionSummary Session { get; set; } = new();
    public AppServerControlThreadSummary Thread { get; set; } = new();
    public AppServerControlTurnSummary CurrentTurn { get; set; } = new();
    public AppServerControlQuickSettingsSummary QuickSettings { get; set; } = new();
    public AppServerControlStreamsSummary Streams { get; set; } = new();
    public List<AppServerControlHistoryItem> History { get; set; } = [];
    public List<AppServerControlItemSummary> Items { get; set; } = [];
    public List<AppServerControlRequestSummary> Requests { get; set; } = [];
    public List<AppServerControlRuntimeNotice> Notices { get; set; } = [];
}

public sealed class AppServerControlHistoryPatch
{
    public string SessionId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; set; }
    public long LatestSequence { get; set; }
    public int HistoryCount { get; set; }
    public AppServerControlSessionSummary Session { get; set; } = new();
    public AppServerControlThreadSummary Thread { get; set; } = new();
    public AppServerControlTurnSummary CurrentTurn { get; set; } = new();
    public AppServerControlQuickSettingsSummary QuickSettings { get; set; } = new();
    public AppServerControlStreamsSummary Streams { get; set; } = new();
    public List<AppServerControlHistoryItem> HistoryUpserts { get; set; } = [];
    public List<string> HistoryRemovals { get; set; } = [];
    public List<AppServerControlItemSummary> ItemUpserts { get; set; } = [];
    public List<string> ItemRemovals { get; set; } = [];
    public List<AppServerControlRequestSummary> RequestUpserts { get; set; } = [];
    public List<string> RequestRemovals { get; set; } = [];
    public List<AppServerControlRuntimeNotice> NoticeUpserts { get; set; } = [];
}

public sealed class AppServerControlSessionSummary
{
    public string State { get; set; } = string.Empty;
    public string StateLabel { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastEventAt { get; set; }
}

public sealed class AppServerControlThreadSummary
{
    public string ThreadId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string StateLabel { get; set; } = string.Empty;
}

public sealed class AppServerControlTurnSummary
{
    public string? TurnId { get; set; }
    public string State { get; set; } = string.Empty;
    public string StateLabel { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public static class AppServerControlQuickSettings
{
    public const string PlanModeOff = "off";
    public const string PlanModeOn = "on";
    public const string PermissionModeManual = "manual";
    public const string PermissionModeAuto = "auto";

    public static string NormalizePlanMode(string? value)
    {
        return string.Equals(value?.Trim(), PlanModeOn, StringComparison.OrdinalIgnoreCase)
            ? PlanModeOn
            : PlanModeOff;
    }

    public static string NormalizePermissionMode(string? value)
    {
        return string.Equals(value?.Trim(), PermissionModeAuto, StringComparison.OrdinalIgnoreCase)
            ? PermissionModeAuto
            : PermissionModeManual;
    }

    public static string? NormalizeOptionalValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static AppServerControlQuickSettingsSummary CreateSummary(
        string? model,
        string? effort,
        string? planMode,
        string? permissionMode,
        string? defaultPermissionMode = null)
    {
        return new AppServerControlQuickSettingsSummary
        {
            Model = NormalizeOptionalValue(model),
            Effort = NormalizeOptionalValue(effort),
            PlanMode = NormalizePlanMode(planMode),
            PermissionMode = NormalizePermissionMode(
                string.IsNullOrWhiteSpace(permissionMode) ? defaultPermissionMode : permissionMode)
        };
    }

    public static List<AppServerControlQuickSettingsOption> CloneOptions(IEnumerable<AppServerControlQuickSettingsOption>? options)
    {
        if (options is null)
        {
            return [];
        }

        return options
            .Where(static option => !string.IsNullOrWhiteSpace(option.Value))
            .Select(static option => new AppServerControlQuickSettingsOption
            {
                Value = option.Value.Trim(),
                Label = string.IsNullOrWhiteSpace(option.Label) ? option.Value.Trim() : option.Label.Trim(),
                Description = NormalizeOptionalValue(option.Description),
                Hidden = option.Hidden,
                IsDefault = option.IsDefault
            })
            .ToList();
    }

    public static AppServerControlQuickSettingsPayload ToPayload(AppServerControlQuickSettingsSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new AppServerControlQuickSettingsPayload
        {
            Model = NormalizeOptionalValue(summary.Model),
            Effort = NormalizeOptionalValue(summary.Effort),
            PlanMode = NormalizePlanMode(summary.PlanMode),
            PermissionMode = NormalizePermissionMode(summary.PermissionMode),
            ModelOptions = CloneOptions(summary.ModelOptions),
            EffortOptions = CloneOptions(summary.EffortOptions)
        };
    }

    public static string ApplyPlanModePrompt(string? text, string? planMode)
    {
        var prompt = NormalizeOptionalValue(text);
        if (!string.Equals(NormalizePlanMode(planMode), PlanModeOn, StringComparison.Ordinal))
        {
            return prompt ?? string.Empty;
        }

        const string planInstruction =
            "tlbx plan mode is enabled for this turn. Start with a concise step-by-step plan, keep it updated while you work, and use native planning capabilities when available.";

        return string.IsNullOrWhiteSpace(prompt)
            ? planInstruction
            : planInstruction + Environment.NewLine + Environment.NewLine + prompt;
    }
}

public sealed class AppServerControlQuickSettingsSummary
{
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public string PlanMode { get; set; } = AppServerControlQuickSettings.PlanModeOff;
    public string PermissionMode { get; set; } = AppServerControlQuickSettings.PermissionModeManual;
    public List<AppServerControlQuickSettingsOption> ModelOptions { get; set; } = [];
    public List<AppServerControlQuickSettingsOption> EffortOptions { get; set; } = [];
}

public sealed class AppServerControlStreamsSummary
{
    public string AssistantText { get; set; } = string.Empty;
    public string ReasoningText { get; set; } = string.Empty;
    public string ReasoningSummaryText { get; set; } = string.Empty;
    public string PlanText { get; set; } = string.Empty;
    public string CommandOutput { get; set; } = string.Empty;
    public string FileChangeOutput { get; set; } = string.Empty;
    public string UnifiedDiff { get; set; } = string.Empty;
}

public sealed class AppServerControlHistoryItem
{
    public string EntryId { get; set; } = string.Empty;
    public long Order { get; set; }
    public int EstimatedHeightPx { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string? TurnId { get; set; }
    public string? ItemId { get; set; }
    public string? RequestId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ItemType { get; set; }
    public string? Title { get; set; }
    public string? CommandText { get; set; }
    public string Body { get; set; } = string.Empty;
    public List<AppServerControlAttachmentReference> Attachments { get; set; } = [];
    public List<AppServerControlInlineFileReference> FileMentions { get; set; } = [];
    public List<AppServerControlInlineImagePreview> ImagePreviews { get; set; } = [];
    public bool Streaming { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonIgnore]
    public string? EnrichmentSourceSignature { get; set; }
}

public sealed class AppServerControlInlineFileReference
{
    public string Field { get; set; } = "body";
    public string DisplayText { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string PathKind { get; set; } = "relative";
    public string? ResolvedPath { get; set; }
    public bool Exists { get; set; }
    public bool IsDirectory { get; set; }
    public string? MimeType { get; set; }
    public int? Line { get; set; }
    public int? Column { get; set; }
}

public sealed class AppServerControlInlineImagePreview
{
    public string DisplayPath { get; set; } = string.Empty;
    public string ResolvedPath { get; set; } = string.Empty;
    public string? MimeType { get; set; }
}

public sealed class AppServerControlItemSummary
{
    public string ItemId { get; set; } = string.Empty;
    public string? TurnId { get; set; }
    public string ItemType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Detail { get; set; }
    public List<AppServerControlAttachmentReference> Attachments { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AppServerControlRequestSummary
{
    public string RequestId { get; set; } = string.Empty;
    public string? TurnId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string KindLabel { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public string? Decision { get; set; }
    public List<AppServerControlQuestion> Questions { get; set; } = [];
    public List<AppServerControlAnsweredQuestion> Answers { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AppServerControlRuntimeNotice
{
    public string EventId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AppServerControlTurnRequest
{
    public string? Text { get; set; }
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public string? PlanMode { get; set; }
    public string? PermissionMode { get; set; }
    public List<AppServerControlAttachmentReference> Attachments { get; set; } = [];
    public List<AppServerControlTerminalReplayStep> TerminalReplay { get; set; } = [];
}

public sealed class AppServerControlGoalSetRequest
{
    public string Objective { get; set; } = string.Empty;
}

public sealed class AppServerControlAttachmentReference
{
    public string Kind { get; set; } = "file";
    public string Path { get; set; } = string.Empty;
    public string? MimeType { get; set; }
    public string? DisplayName { get; set; }
}

public sealed class AppServerControlTerminalReplayStep
{
    public string Kind { get; set; } = "text";
    public string? Text { get; set; }
    public string? Path { get; set; }
    public string? MimeType { get; set; }
    public bool UseBracketedPaste { get; set; }
}

public sealed class AppServerControlTurnStartResponse
{
    public string SessionId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string ThreadId { get; set; } = string.Empty;
    public string? TurnId { get; set; }
    public string Status { get; set; } = "accepted";
    public AppServerControlQuickSettingsSummary QuickSettings { get; set; } = new();
}

public sealed class AppServerControlInterruptRequest
{
    public string? TurnId { get; set; }
}

public sealed class AppServerControlRequestDecisionRequest
{
    public string Decision { get; set; } = "accept";
}

public sealed class AppServerControlUserInputAnswerRequest
{
    public List<AppServerControlAnsweredQuestion> Answers { get; set; } = [];
}

public sealed class AppServerControlCommandAcceptedResponse
{
    public string SessionId { get; set; } = string.Empty;
    public string Status { get; set; } = "accepted";
    public string? RequestId { get; set; }
    public string? TurnId { get; set; }
}

public sealed class AppServerControlHistoryWindowRequest
{
    public int? StartIndex { get; set; }
    public int? Count { get; set; }
    public int? ViewportWidth { get; set; }
    public string? WindowRevision { get; set; }
}

public sealed class AppServerControlWsRequestMessage
{
    public string Type { get; set; } = "request";
    public string Id { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public long? AfterSequence { get; set; }
    public string? RequestId { get; set; }
    public AppServerControlHistoryWindowRequest? HistoryWindow { get; set; }
    public AppServerControlTurnRequest? Turn { get; set; }
    public AppServerControlInterruptRequest? Interrupt { get; set; }
    public AppServerControlRequestDecisionRequest? RequestDecision { get; set; }
    public AppServerControlUserInputAnswerRequest? UserInputAnswer { get; set; }
    public AppServerControlGoalSetRequest? GoalSet { get; set; }
}

public sealed class AppServerControlWsSubscriptionMessage
{
    public string Type { get; set; } = "subscribe";
    public string SessionId { get; set; } = string.Empty;
    public long AfterSequence { get; set; }
    public AppServerControlHistoryWindowRequest? HistoryWindow { get; set; }
}

public sealed class AppServerControlWsAckMessage
{
    public string Type { get; set; } = "ack";
    public string Id { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
}

public sealed class AppServerControlWsErrorMessage
{
    public string Type { get; set; } = "error";
    public string? Id { get; set; }
    public string? Action { get; set; }
    public string? SessionId { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class AppServerControlWsHistoryWindowMessage
{
    public string Type { get; set; } = "history.window";
    public string? Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string? WindowRevision { get; set; }
    public AppServerControlHistoryWindowResponse HistoryWindow { get; set; } = new();
}

public sealed class AppServerControlWsHistoryPatchMessage
{
    public string Type { get; set; } = "history.patch";
    public string SessionId { get; set; } = string.Empty;
    public AppServerControlHistoryPatch Patch { get; set; } = new();
}

public sealed class AppServerControlWsTurnStartedMessage
{
    public string Type { get; set; } = "turnStarted";
    public string Id { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public AppServerControlTurnStartResponse Response { get; set; } = new();
}

public sealed class AppServerControlWsCommandAcceptedMessage
{
    public string Type { get; set; } = "commandAccepted";
    public string Id { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public AppServerControlCommandAcceptedResponse Response { get; set; } = new();
}

public sealed class AppServerControlHostHello
{
    public string ProtocolVersion { get; set; } = AppServerControlHostProtocol.CurrentVersion;
    public string HostKind { get; set; } = "mtagenthost";
    public string HostVersion { get; set; } = "dev";
    public List<string> Providers { get; set; } = [];
    public List<string> Capabilities { get; set; } = [];
}

public sealed class AppServerControlAttachRuntimeRequest
{
    public string SessionId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string? InstanceId { get; set; }
    public string? OwnerToken { get; set; }
    public SessionAgentAttachPoint? AttachPoint { get; set; }
    public string? ExecutablePath { get; set; }
    public string? UserProfileDirectory { get; set; }
    public string? ResumeThreadId { get; set; }
}

public sealed class AppServerControlHostHistoryWindowRequest
{
    public int? StartIndex { get; set; }
    public int? Count { get; set; }
    public int? ViewportWidth { get; set; }
}

public sealed class AppServerControlHostCommandEnvelope
{
    public string ProtocolVersion { get; set; } = AppServerControlHostProtocol.CurrentVersion;
    public string CommandId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public AppServerControlAttachRuntimeRequest? AttachRuntime { get; set; }
    public AppServerControlTurnRequest? StartTurn { get; set; }
    public AppServerControlInterruptRequest? InterruptTurn { get; set; }
    public AppServerControlRequestResolutionCommand? ResolveRequest { get; set; }
    public AppServerControlUserInputResolutionCommand? ResolveUserInput { get; set; }
    public AppServerControlGoalSetRequest? SetGoal { get; set; }
    public AppServerControlHostHistoryWindowRequest? HistoryWindow { get; set; }
}

public sealed class AppServerControlRequestResolutionCommand
{
    public string RequestId { get; set; } = string.Empty;
    public string Decision { get; set; } = "accept";
}

public sealed class AppServerControlUserInputResolutionCommand
{
    public string RequestId { get; set; } = string.Empty;
    public List<AppServerControlAnsweredQuestion> Answers { get; set; } = [];
}

public sealed class AppServerControlHostCommandResultEnvelope
{
    public string ProtocolVersion { get; set; } = AppServerControlHostProtocol.CurrentVersion;
    public string CommandId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Status { get; set; } = "accepted";
    public string? Message { get; set; }
    public AppServerControlTurnStartResponse? TurnStarted { get; set; }
    public AppServerControlCommandAcceptedResponse? Accepted { get; set; }
    public AppServerControlHistoryWindowResponse? HistoryWindow { get; set; }
}

public sealed class AppServerControlHostHistoryPatchEnvelope
{
    public string ProtocolVersion { get; set; } = AppServerControlHostProtocol.CurrentVersion;
    public string SessionId { get; set; } = string.Empty;
    public AppServerControlHistoryPatch Patch { get; set; } = new();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppServerControlHostHello))]
[JsonSerializable(typeof(AppServerControlHostCommandEnvelope))]
[JsonSerializable(typeof(AppServerControlHostCommandResultEnvelope))]
[JsonSerializable(typeof(AppServerControlAttachRuntimeRequest))]
[JsonSerializable(typeof(AppServerControlHostHistoryWindowRequest))]
[JsonSerializable(typeof(SessionAgentAttachPoint))]
[JsonSerializable(typeof(AppServerControlRequestResolutionCommand))]
[JsonSerializable(typeof(AppServerControlUserInputResolutionCommand))]
[JsonSerializable(typeof(AppServerControlTurnRequest))]
[JsonSerializable(typeof(AppServerControlGoalSetRequest))]
[JsonSerializable(typeof(AppServerControlAttachmentReference))]
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
[JsonSerializable(typeof(AppServerControlQuickSettingsPayload))]
[JsonSerializable(typeof(AppServerControlQuickSettingsSummary))]
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
[JsonSerializable(typeof(AppServerControlStreamsSummary))]
[JsonSerializable(typeof(AppServerControlHistoryItem))]
[JsonSerializable(typeof(List<AppServerControlHistoryItem>))]
[JsonSerializable(typeof(AppServerControlInlineFileReference))]
[JsonSerializable(typeof(List<AppServerControlInlineFileReference>))]
[JsonSerializable(typeof(AppServerControlInlineImagePreview))]
[JsonSerializable(typeof(List<AppServerControlInlineImagePreview>))]
[JsonSerializable(typeof(AppServerControlItemSummary))]
[JsonSerializable(typeof(List<AppServerControlItemSummary>))]
[JsonSerializable(typeof(AppServerControlRequestSummary))]
[JsonSerializable(typeof(List<AppServerControlRequestSummary>))]
[JsonSerializable(typeof(AppServerControlRuntimeNotice))]
[JsonSerializable(typeof(List<AppServerControlRuntimeNotice>))]
[JsonSerializable(typeof(AppServerControlHostHistoryPatchEnvelope))]
public partial class AppServerControlHostJsonContext : JsonSerializerContext
{
}























