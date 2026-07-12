using System.Text.Json.Serialization;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.Models.ControlPlane;

public static class ControlPlaneWorkItemStates
{
    public const string Open = "open";
    public const string Active = "active";
    public const string Waiting = "waiting";
    public const string Blocked = "blocked";
    public const string Done = "done";
    public const string Dismissed = "dismissed";

    public static readonly HashSet<string> All = new(StringComparer.Ordinal)
    {
        Open, Active, Waiting, Blocked, Done, Dismissed
    };
}

public static class ControlPlanePriorities
{
    public const string Low = "low";
    public const string Normal = "normal";
    public const string High = "high";
    public const string Urgent = "urgent";

    public static readonly HashSet<string> All = new(StringComparer.Ordinal)
    {
        Low, Normal, High, Urgent
    };
}

public static class ControlPlaneSessionStates
{
    public const string Working = "working";
    public const string Waiting = "waiting";
    public const string NeedsInput = "needsInput";
    public const string Blocked = "blocked";
    public const string Done = "done";

    public static readonly HashSet<string> All = new(StringComparer.Ordinal)
    {
        Working, Waiting, NeedsInput, Blocked, Done
    };
}

public static class ControlPlaneEventTypes
{
    public const string WorkItemCreated = "workItemCreated";
    public const string WorkItemUpdated = "workItemUpdated";
    public const string WorkItemDeleted = "workItemDeleted";
    public const string SessionStatusPublished = "sessionStatusPublished";
    public const string SessionStatusCleared = "sessionStatusCleared";
    public const string CheckpointCreated = "checkpointCreated";
}

public sealed class ControlPlaneWorkItem
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = "todo";
    public string State { get; set; } = ControlPlaneWorkItemStates.Open;
    public string Priority { get; set; } = ControlPlanePriorities.Normal;
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? NextAction { get; set; }
    public string? Project { get; set; }
    public string? RepositoryPath { get; set; }
    public string? SessionId { get; set; }
    public string? Url { get; set; }
    public string Source { get; set; } = "agent";
    public string? DedupeKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Revision { get; set; }
}

public sealed class ControlPlaneSessionStatus
{
    public string SessionId { get; set; } = string.Empty;
    public string State { get; set; } = ControlPlaneSessionStates.Working;
    public string Summary { get; set; } = string.Empty;
    public string? CurrentTask { get; set; }
    public string? NextAction { get; set; }
    public string? Project { get; set; }
    public string? RepositoryPath { get; set; }
    public string Source { get; set; } = "agent";
    public DateTimeOffset UpdatedAt { get; set; }
    public int Revision { get; set; }
}

public sealed class ControlPlaneCheckpoint
{
    public string Id { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Kind { get; set; } = "progress";
    public string Summary { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string? Project { get; set; }
    public string? RepositoryPath { get; set; }
    public string Source { get; set; } = "agent";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ControlPlaneDocument
{
    public List<ControlPlaneWorkItem> WorkItems { get; set; } = [];
    public List<ControlPlaneSessionStatus> SessionStatuses { get; set; } = [];
    public List<ControlPlaneCheckpoint> Checkpoints { get; set; } = [];
    public long NextEventSequence { get; set; } = 1;
    public List<ControlPlaneEvent> Events { get; set; } = [];
}

public sealed class ControlPlaneEvent
{
    public long Sequence { get; set; }
    public string Type { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public string? State { get; set; }
    public string? Priority { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string Source { get; set; } = "agent";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CreateControlPlaneWorkItemRequest
{
    public string? Kind { get; set; }
    public string? State { get; set; }
    public string? Priority { get; set; }
    public string? Title { get; set; }
    public string? Summary { get; set; }
    public string? NextAction { get; set; }
    public string? Project { get; set; }
    public string? RepositoryPath { get; set; }
    public string? SessionId { get; set; }
    public string? Url { get; set; }
    public string? Source { get; set; }
    public string? DedupeKey { get; set; }
}

public sealed class UpdateControlPlaneWorkItemRequest
{
    public string? Kind { get; set; }
    public string? State { get; set; }
    public string? Priority { get; set; }
    public string? Title { get; set; }
    public string? Summary { get; set; }
    public string? NextAction { get; set; }
    public string? Project { get; set; }
    public string? RepositoryPath { get; set; }
    public string? SessionId { get; set; }
    public string? Url { get; set; }
    public string? Source { get; set; }
}

public sealed class PublishControlPlaneSessionStatusRequest
{
    public string? State { get; set; }
    public string? Summary { get; set; }
    public string? CurrentTask { get; set; }
    public string? NextAction { get; set; }
    public string? Project { get; set; }
    public string? RepositoryPath { get; set; }
    public string? Source { get; set; }
}

public sealed class CreateControlPlaneCheckpointRequest
{
    public string? SessionId { get; set; }
    public string? Kind { get; set; }
    public string? Summary { get; set; }
    public string? Details { get; set; }
    public string? Project { get; set; }
    public string? RepositoryPath { get; set; }
    public string? Source { get; set; }
}

public sealed class ControlPlaneWorkItemListResponse
{
    public int TotalCount { get; set; }
    public List<ControlPlaneWorkItem> Items { get; set; } = [];
}

public sealed class ControlPlaneSessionStatusListResponse
{
    public List<ControlPlaneSessionStatus> Statuses { get; set; } = [];
}

public sealed class ControlPlaneCheckpointListResponse
{
    public int TotalCount { get; set; }
    public List<ControlPlaneCheckpoint> Checkpoints { get; set; } = [];
}

public sealed class ControlPlaneSnapshotResponse
{
    public List<ControlPlaneWorkItem> WorkItems { get; set; } = [];
    public List<ControlPlaneSessionStatus> SessionStatuses { get; set; } = [];
    public List<ControlPlaneCheckpoint> Checkpoints { get; set; } = [];
}

public sealed class ControlPlaneEventListResponse
{
    public long LatestSequence { get; set; }
    public List<ControlPlaneEvent> Events { get; set; } = [];
}

public sealed class ControlPlaneDispatchRequest
{
    public List<string> SessionIds { get; set; } = [];
    public AppServerControlTurnRequest? Turn { get; set; }
}

public sealed class ControlPlaneDispatchResult
{
    public string SessionId { get; set; } = string.Empty;
    public bool Accepted { get; set; }
    public bool Queued { get; set; }
    public string? Error { get; set; }
}

public sealed class ControlPlaneDispatchResponse
{
    public List<ControlPlaneDispatchResult> Results { get; set; } = [];
}

public sealed class ControlPlaneSessionCapability
{
    public string SessionId { get; set; } = string.Empty;
    public bool IsRunning { get; set; }
    public bool AgentControlled { get; set; }
    public bool AppServerControlAttached { get; set; }
    public bool AppServerControlOnly { get; set; }
    public string? Surface { get; set; }
    public string? ProfileHint { get; set; }
    public List<string> PromptModes { get; set; } = [];
}

public sealed class ControlPlaneCapabilitiesResponse
{
    public int SchemaVersion { get; set; } = 1;
    public List<string> Features { get; set; } = [];
    public List<string> WorkItemStates { get; set; } = [];
    public List<string> SessionStates { get; set; } = [];
    public List<ControlPlaneSessionCapability> Sessions { get; set; } = [];
}

[JsonSerializable(typeof(ControlPlaneDocument))]
[JsonSerializable(typeof(ControlPlaneWorkItem))]
[JsonSerializable(typeof(List<ControlPlaneWorkItem>))]
[JsonSerializable(typeof(ControlPlaneSessionStatus))]
[JsonSerializable(typeof(List<ControlPlaneSessionStatus>))]
[JsonSerializable(typeof(ControlPlaneCheckpoint))]
[JsonSerializable(typeof(List<ControlPlaneCheckpoint>))]
[JsonSerializable(typeof(ControlPlaneEvent))]
[JsonSerializable(typeof(List<ControlPlaneEvent>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class ControlPlaneJsonContext : JsonSerializerContext
{
}
