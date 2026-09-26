namespace Ai.Tlbx.MidTerm.Models.Browser;

public sealed class BrowserWsResult
{
    public string? Type { get; init; }
    public bool Visible { get; init; }
    public bool Focus { get; init; }
    public bool TopLevel { get; init; }
    public string Id { get; init; } = "";
    public bool Success { get; init; }
    public string? Result { get; init; }
    public string? Error { get; init; }
    public int? MatchCount { get; init; }
    public string? SessionId { get; init; }
    public string? PreviewName { get; init; }
    public string? PreviewId { get; init; }
}
