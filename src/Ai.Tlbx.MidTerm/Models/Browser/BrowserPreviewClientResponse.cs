namespace Ai.Tlbx.MidTerm.Models.Browser;

public sealed class BrowserPreviewClientResponse
{
    public string? SessionId { get; init; }
    public string PreviewName { get; init; } = "default";
    public string RouteKey { get; init; } = "";
    public long OwnershipGeneration { get; init; }
    public string PreviewId { get; init; } = "";
    public string PreviewToken { get; init; } = "";
    public string? Origin { get; init; }
}
