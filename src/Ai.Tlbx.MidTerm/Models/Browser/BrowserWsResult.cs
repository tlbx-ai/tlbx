namespace Ai.Tlbx.MidTerm.Models.Browser;

public sealed class BrowserWsResult
{
    public string? StorageToken { get; init; }
    public string? Type { get; init; }
    public bool Visible { get; init; }
    public bool? PanelVisible { get; init; }
    public bool? DocumentVisible { get; init; }
    public string? DocumentVisibilityState { get; init; }
    public bool Focus { get; init; }
    public bool TopLevel { get; init; }
    public string Id { get; init; } = "";
    public bool Success { get; init; }
    public string? Result { get; init; }
    public string? Error { get; init; }
    public int? MatchCount { get; init; }
    /// <summary>DOM screenshot coverage diagnostics; never proof of compositor pixel fidelity.</summary>
    public BrowserCaptureInfo? Capture { get; init; }
    public string? SessionId { get; init; }
    public string? PreviewName { get; init; }
    public string? PreviewId { get; init; }
    public string? DocumentId { get; init; }
    public long? NavigationVersion { get; init; }
    public long? NavigationVersionBefore { get; init; }
    public string? NavigationKind { get; init; }
}
