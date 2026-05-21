using System.Text.Json.Serialization;

namespace Ai.Tlbx.MidTerm.Models.History;

public static class LaunchEntryLaunchModes
{
    public const string Terminal = "terminal";
    public const string AppServerControl = "appServerControl";
}

public static class HistorySurfaceTypes
{
    public const string Terminal = "trm";
    public const string Codex = "cdx";
    public const string Claude = "cld";
    public const string Grok = "grk";
}

public sealed class LaunchEntry
{
    public string Id { get; set; } = "";
    public string ShellType { get; set; } = "";
    public string Executable { get; set; } = "";
    public string? CommandLine { get; set; }
    public string WorkingDirectory { get; set; } = "";
    public bool IsStarred { get; set; }
    public string? Label { get; set; }
    public string? Notes { get; set; }
    public DateTime LastUsed { get; set; }
    public int Order { get; set; }
    public string LaunchMode { get; set; } = LaunchEntryLaunchModes.Terminal;
    public string? Profile { get; set; }
    public string? LaunchOrigin { get; set; }
    public string SurfaceType { get; set; } = HistorySurfaceTypes.Terminal;
    public string? ForegroundProcessName { get; set; }
    public string? ForegroundProcessCommandLine { get; set; }
    public string? ForegroundProcessDisplayName { get; set; }
    public string? ForegroundProcessIdentity { get; set; }
}

public sealed class HistoryReorderRequest
{
    public required List<string> OrderedIds { get; init; }
}

public sealed class LaunchHistory
{
    public List<LaunchEntry> Entries { get; set; } = [];
}

[JsonSerializable(typeof(LaunchEntry))]
[JsonSerializable(typeof(LaunchHistory))]
[JsonSerializable(typeof(List<LaunchEntry>))]
[JsonSerializable(typeof(HistoryReorderRequest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class HistoryJsonContext : JsonSerializerContext
{
}
