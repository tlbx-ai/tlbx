namespace Ai.Tlbx.MidTerm.Models.Browser;

public sealed class BrowserStatusResponse
{
    public bool Connected { get; init; }
    public bool Controllable { get; init; }
    public bool HasTarget { get; init; }
    public bool HasUiClient { get; init; }
    public bool IsScoped { get; init; }
    public string State { get; init; } = "disconnected";
    public string BridgePhase { get; init; } = "disconnected";
    public string? ScopeDescription { get; init; }
    public string? StatusMessage { get; init; }
    public string? RecoveryHint { get; init; }
    public int ConnectedClientCount { get; init; }
    public int TotalConnectedClientCount { get; init; }
    public int ConnectedUiClientCount { get; init; }
    public string? TargetUrl { get; init; }
    public string? OwnerBrowserId { get; init; }
    public bool OwnerConnected { get; init; }
    public long OwnershipGeneration { get; init; }
    public BrowserClientInfo? DefaultClient { get; init; }
    public BrowserClientInfo[] Clients { get; init; } = [];
}

public sealed class BrowserClientInfo
{
    public string? SessionId { get; init; }
    public string? PreviewName { get; init; }
    public string? PreviewId { get; init; }
    public string? BrowserId { get; init; }
    public long? TargetRevision { get; init; }
    public DateTimeOffset ConnectedAtUtc { get; init; }
    public bool IsMainBrowser { get; init; }
    public bool IsVisible { get; init; }
    public bool HasFocus { get; init; }
    public bool IsTopLevel { get; init; }
}
