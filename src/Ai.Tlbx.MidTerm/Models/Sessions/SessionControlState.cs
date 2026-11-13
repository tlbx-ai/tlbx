using System.Text.Json.Serialization;

namespace Ai.Tlbx.MidTerm.Models.Sessions;

public sealed class SessionControlState
{
    public List<string> AgentControlledSessionIds { get; set; } = [];
    public List<string> AppServerControlOnlySessionIds { get; set; } = [];
    public Dictionary<string, string> LaunchOrigins { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ProfileHints { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> AppServerControlResumeThreadIds { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> SpaceIds { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> WorkspacePaths { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Surfaces { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, SessionPreviewState>> Previews { get; set; } = new(StringComparer.Ordinal);
}

// Only authoritative configuration survives a web restart. Capabilities, route
// keys, connected clients and cookies are deliberately not part of this record.
public sealed record SessionPreviewState
{
    public string? Url { get; init; }
    public string? LocalRoot { get; init; }
    public long TargetRevision { get; init; }
    public string? OwnerBrowserId { get; init; }
}

[JsonSerializable(typeof(SessionControlState))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class SessionControlStateJsonContext : JsonSerializerContext
{
}
