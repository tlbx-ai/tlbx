namespace Ai.Tlbx.MidTerm.Models.Git;

public sealed class GitWsMessage
{
    public string Type { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string[]? SessionIds { get; set; }
    public GitStatusResponse? Status { get; set; }
    public GitRepoBinding[]? Repos { get; set; }
    public string? Diff { get; set; }
    public string? Error { get; set; }
}
