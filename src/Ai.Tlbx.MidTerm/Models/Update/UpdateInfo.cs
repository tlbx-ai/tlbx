namespace Ai.Tlbx.MidTerm.Models.Update;

/// <summary>
/// Information about an available update from GitHub releases.
/// </summary>
public sealed class UpdateInfo
{
    public bool Available { get; init; }
    /// <summary>Discovery failed; availability is unknown rather than up to date.</summary>
    public string? CheckError { get; init; }
    public string CurrentVersion { get; init; } = "";
    public string LatestVersion { get; init; } = "";
    public string ReleaseUrl { get; init; } = "";
    public string? DownloadUrl { get; init; }
    public string? AssetName { get; init; }
    public string? ReleaseNotes { get; init; }
    public UpdateType Type { get; init; } = UpdateType.Full;
    public bool SessionsPreserved => Type == UpdateType.WebOnly;
    /// <summary>The last completed update attempt, retained until explicitly dismissed.</summary>
    public UpdateResult? LastResult { get; set; }

    /// <summary>Local development environment name (MIDTERM_ENVIRONMENT).</summary>
    public string? Environment { get; init; }
    /// <summary>Local update info when running in dev environment.</summary>
    public LocalUpdateInfo? LocalUpdate { get; init; }
    /// <summary>True when switching from dev to stable channel offers a downgrade.</summary>
    public bool IsDowngrade { get; init; }
}
