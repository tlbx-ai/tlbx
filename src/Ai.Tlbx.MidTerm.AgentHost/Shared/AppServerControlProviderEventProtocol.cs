using System.Text.Json.Serialization;

namespace Ai.Tlbx.MidTerm.Common.Protocol;

public sealed class AppServerControlProviderEvent
{
    public long Sequence { get; set; }
    public string EventId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string ThreadId { get; set; } = string.Empty;
    public string? TurnId { get; set; }
    public string? ItemId { get; set; }
    public string? RequestId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Type { get; set; } = string.Empty;
    public AppServerControlProviderEventRaw? Raw { get; set; }
    public AppServerControlProviderSessionStatePayload? SessionState { get; set; }
    public AppServerControlProviderAgentMetadataPayload? AgentMetadataUpdated { get; set; }
    public AppServerControlProviderThreadStatePayload? ThreadState { get; set; }
    public AppServerControlGoalState? GoalUpdated { get; set; }
    public AppServerControlProviderTurnStartedPayload? TurnStarted { get; set; }
    public AppServerControlProviderTurnCompletedPayload? TurnCompleted { get; set; }
    public AppServerControlProviderContentDeltaPayload? ContentDelta { get; set; }
    public AppServerControlProviderPlanDeltaPayload? PlanDelta { get; set; }
    public AppServerControlProviderPlanCompletedPayload? PlanCompleted { get; set; }
    public AppServerControlProviderDiffUpdatedPayload? DiffUpdated { get; set; }
    public AppServerControlProviderItemPayload? Item { get; set; }
    public AppServerControlProviderTaskPayload? Task { get; set; }
    public AppServerControlQuickSettingsPayload? QuickSettingsUpdated { get; set; }
    public AppServerControlProviderRequestOpenedPayload? RequestOpened { get; set; }
    public AppServerControlProviderRequestResolvedPayload? RequestResolved { get; set; }
    public AppServerControlProviderUserInputRequestedPayload? UserInputRequested { get; set; }
    public AppServerControlProviderUserInputResolvedPayload? UserInputResolved { get; set; }
    public AppServerControlProviderRuntimeMessagePayload? RuntimeMessage { get; set; }
    public AppServerControlProviderRuntimeMessagePayload? RuntimeNoticeOnly { get; set; }
}

public sealed class AppServerControlProviderEventRaw
{
    public string Source { get; set; } = string.Empty;
    public string? Method { get; set; }
    public bool PayloadOmitted { get; set; }
}

public sealed class AppServerControlProviderAgentMetadataPayload
{
    public string? AgentName { get; set; }
    public AppServerControlUsageSummary? Usage { get; set; }
    public List<AppServerControlCommand>? AvailableCommands { get; set; }
}

public sealed class AppServerControlProviderSessionStatePayload
{
    public string State { get; set; } = string.Empty;
    public string StateLabel { get; set; } = string.Empty;
    public string? Reason { get; set; }
}

public sealed class AppServerControlProviderThreadStatePayload
{
    public string State { get; set; } = string.Empty;
    public string StateLabel { get; set; } = string.Empty;
    public string? ProviderThreadId { get; set; }
}

public sealed class AppServerControlProviderTurnStartedPayload
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsReplay { get; set; }
    public string? Model { get; set; }
    public string? Effort { get; set; }
}

public sealed class AppServerControlProviderTurnCompletedPayload
{
    public string State { get; set; } = string.Empty;
    public string StateLabel { get; set; } = string.Empty;
    public string? StopReason { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class AppServerControlProviderContentDeltaPayload
{
    public string StreamKind { get; set; } = string.Empty;
    public string Delta { get; set; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Replace { get; set; }
}

public sealed class AppServerControlProviderPlanDeltaPayload
{
    public string Delta { get; set; } = string.Empty;
}

public sealed class AppServerControlProviderPlanCompletedPayload
{
    public string PlanMarkdown { get; set; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsProposal { get; set; }
}

public sealed class AppServerControlProviderDiffUpdatedPayload
{
    public string UnifiedDiff { get; set; } = string.Empty;
}

public sealed class AppServerControlProviderItemPayload
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Replace { get; set; }
    public string ItemType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Detail { get; set; }
    public AppServerControlToolPresentation? ToolPresentation { get; set; }
    public List<AppServerControlAttachmentReference> Attachments { get; set; } = [];
}

public sealed class AppServerControlProviderTaskPayload
{
    public string TaskId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? TaskType { get; set; }
    public string? Description { get; set; }
    public string? Summary { get; set; }
    public string? LastToolName { get; set; }
    public string? UsageJson { get; set; }
}

public sealed class AppServerControlProviderRequestOpenedPayload
{
    public string RequestType { get; set; } = string.Empty;
    public string RequestTypeLabel { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

public sealed class AppServerControlProviderRequestResolvedPayload
{
    public string RequestType { get; set; } = string.Empty;
    public string? Decision { get; set; }
}

public sealed class AppServerControlProviderUserInputRequestedPayload
{
    public List<AppServerControlQuestion> Questions { get; set; } = [];
}

public sealed class AppServerControlProviderUserInputResolvedPayload
{
    public List<AppServerControlAnsweredQuestion> Answers { get; set; } = [];
}

public sealed class AppServerControlProviderRuntimeMessagePayload
{
    public string Message { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

public sealed class AppServerControlProviderEventListResponse
{
    public string SessionId { get; set; } = string.Empty;
    public long LatestSequence { get; set; }
    public List<AppServerControlProviderEvent> Events { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppServerControlProviderEvent))]
[JsonSerializable(typeof(List<AppServerControlProviderEvent>))]
[JsonSerializable(typeof(AppServerControlProviderEventRaw))]
[JsonSerializable(typeof(AppServerControlProviderSessionStatePayload))]
[JsonSerializable(typeof(AppServerControlProviderThreadStatePayload))]
[JsonSerializable(typeof(AppServerControlProviderTurnStartedPayload))]
[JsonSerializable(typeof(AppServerControlProviderTurnCompletedPayload))]
[JsonSerializable(typeof(AppServerControlProviderContentDeltaPayload))]
[JsonSerializable(typeof(AppServerControlProviderPlanDeltaPayload))]
[JsonSerializable(typeof(AppServerControlProviderPlanCompletedPayload))]
[JsonSerializable(typeof(AppServerControlProviderDiffUpdatedPayload))]
[JsonSerializable(typeof(AppServerControlProviderItemPayload))]
[JsonSerializable(typeof(AppServerControlToolPresentation))]
[JsonSerializable(typeof(AppServerControlProviderTaskPayload))]
[JsonSerializable(typeof(AppServerControlProviderRequestOpenedPayload))]
[JsonSerializable(typeof(AppServerControlProviderRequestResolvedPayload))]
[JsonSerializable(typeof(AppServerControlProviderUserInputRequestedPayload))]
[JsonSerializable(typeof(AppServerControlProviderUserInputResolvedPayload))]
[JsonSerializable(typeof(AppServerControlProviderRuntimeMessagePayload))]
[JsonSerializable(typeof(AppServerControlProviderEventListResponse))]
public partial class AppServerControlProviderEventJsonContext : JsonSerializerContext
{
}
