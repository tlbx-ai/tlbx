namespace Ai.Tlbx.MidTerm.Models.WebPreview;

public sealed class WebPreviewSessionRequest
{
    public string SessionId { get; init; } = "";
    public string? BrowserId { get; init; }
    public string? PreviewName { get; init; }
}
