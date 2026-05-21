using System.Text.Json.Serialization;

namespace Ai.Tlbx.MidTerm.Models.Spaces;

public static class SpaceKinds
{
    public const string Plain = "plain";
    public const string Git = "git";
}

public static class SpaceWorkspaceKinds
{
    public const string Plain = "plain";
    public const string Worktree = "worktree";
}

public static class SpaceSurfaceKinds
{
    public const string Terminal = "terminal";
    public const string Codex = "codex";
    public const string Claude = "claude";
    public const string Grok = "grok";
}

public sealed class SpaceRecord
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Kind { get; set; } = SpaceKinds.Plain;
    public string RootPath { get; set; } = "";
    public string ImportedPath { get; set; } = "";
    public string? CommonRepoId { get; set; }
    public bool IsPinned { get; set; } = true;
    public List<SpaceWorktreeRecord> Worktrees { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class SpaceWorktreeRecord
{
    public string Path { get; set; } = "";
    public string? Label { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class SpaceStore
{
    public bool MigratedFromHistory { get; set; }
    public List<SpaceRecord> Spaces { get; set; } = [];
}

public sealed class SpaceSummaryDto
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Label { get; set; } = "";
    public string Kind { get; set; } = SpaceKinds.Plain;
    public string RootPath { get; set; } = "";
    public string ImportedPath { get; set; } = "";
    public string? CommonRepoId { get; set; }
    public bool IsPinned { get; set; }
    public bool CanInitGit { get; set; }
    public bool CanCreateWorktree { get; set; }
    public string? PrimaryWorkspaceKey { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public SpaceWorkspaceDto[] Workspaces { get; set; } = [];
}

public sealed class SpaceWorkspaceDto
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";
    public string Kind { get; set; } = SpaceWorkspaceKinds.Plain;
    public string? Branch { get; set; }
    public string? Head { get; set; }
    public bool IsMain { get; set; }
    public bool IsDetached { get; set; }
    public bool Locked { get; set; }
    public bool Prunable { get; set; }
    public int ChangeCount { get; set; }
    public bool HasChanges { get; set; }
    public bool HasActiveAiSession { get; set; }
    public SpaceWorkspaceSessionDto[] ActiveSessions { get; set; } = [];
}

public sealed class SpaceWorkspaceSessionDto
{
    public string SessionId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Surface { get; set; } = SpaceSurfaceKinds.Terminal;
    public bool AppServerControlOnly { get; set; }
    public string? ProfileHint { get; set; }
}

public sealed class SpaceImportRequest
{
    public required string Path { get; init; }
    public string? Label { get; init; }
}

public sealed class SpaceUpdateRequest
{
    public string? Label { get; init; }
    public bool? IsPinned { get; init; }
}

public sealed class SpaceCreateWorktreeRequest
{
    public required string Path { get; init; }
    public required string BranchName { get; init; }
    public string? Name { get; init; }
}

public sealed class SpaceUpdateWorkspaceRequest
{
    public string? Label { get; init; }
}

public sealed class SpaceDeleteWorktreeRequest
{
    public bool Force { get; init; }
}

public sealed class SpaceLaunchRequest
{
    public required string Surface { get; init; }
    public int Cols { get; init; } = 120;
    public int Rows { get; init; } = 30;
    public string? Shell { get; init; }
}

[JsonSerializable(typeof(SpaceRecord))]
[JsonSerializable(typeof(SpaceWorktreeRecord))]
[JsonSerializable(typeof(SpaceStore))]
[JsonSerializable(typeof(SpaceSummaryDto))]
[JsonSerializable(typeof(List<SpaceSummaryDto>))]
[JsonSerializable(typeof(SpaceWorkspaceDto))]
[JsonSerializable(typeof(List<SpaceWorkspaceDto>))]
[JsonSerializable(typeof(SpaceWorkspaceSessionDto))]
[JsonSerializable(typeof(List<SpaceWorkspaceSessionDto>))]
[JsonSerializable(typeof(SpaceImportRequest))]
[JsonSerializable(typeof(SpaceUpdateRequest))]
[JsonSerializable(typeof(SpaceCreateWorktreeRequest))]
[JsonSerializable(typeof(SpaceUpdateWorkspaceRequest))]
[JsonSerializable(typeof(SpaceDeleteWorktreeRequest))]
[JsonSerializable(typeof(SpaceLaunchRequest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class SpaceJsonContext : JsonSerializerContext
{
}
