namespace Ai.Tlbx.MidTerm.Models;

public sealed class MainBrowserStatusMessage
{
    public string Type { get; set; } = "main-browser-status";
    public bool IsMain { get; set; }
    public bool ShowButton { get; set; }
    public List<BrowserSessionStatus> Browsers { get; set; } = [];
}

public sealed class BrowserSessionStatus
{
    public string BrowserId { get; set; } = "";
    public bool IsMain { get; set; }
    public bool IsActive { get; set; }
    public int ConnectionCount { get; set; }
    public int ActiveConnectionCount { get; set; }
    public string? ActiveSessionId { get; set; }
    public string? ActiveSurface { get; set; }
}
