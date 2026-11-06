namespace Ai.Tlbx.MidTerm.Services.Boards;

public sealed record BoardLaunchRequest
{
    public string? RequestId { get; set; }
    public long? ExpectedRevision { get; set; }
    public string? EntryId { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? LaunchCommand { get; set; }
    public string? Profile { get; set; }
    public bool? AppServerControlOnly { get; set; }
}

public sealed record BoardLaunch
{
    public string Id { get; set; } = "";
    public string BoardId { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string? EntryId { get; set; }
    public string? SessionId { get; set; }
    public string State { get; set; } = "starting";
    public string Name { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string? LaunchCommand { get; set; }
    public string? Profile { get; set; }
    public bool AppServerControlOnly { get; set; }
    public string Prompt { get; set; } = "";
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed record BoardLaunchStart(BoardLaunch Launch, bool Created);
