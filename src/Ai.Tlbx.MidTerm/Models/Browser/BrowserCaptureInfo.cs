namespace Ai.Tlbx.MidTerm.Models.Browser;

/// <summary>Renderer diagnostics, not a guarantee of compositor pixel fidelity.</summary>
public sealed class BrowserCaptureInfo
{
    public string Renderer { get; init; } = "dom-svg";
    public string PixelCoverage { get; init; } = "unverified";
    public string? Inventory { get; init; }
    public string MediaPixels { get; init; } = "unverified";
    public string NestedMedia { get; init; } = "unknown";
    public BrowserCaptureMedia? Media { get; init; }
    public Dictionary<string, int>? WarningCodes { get; init; }
    public bool WarningsMayBeTruncated { get; init; }
    public BrowserCaptureOutput? Output { get; init; }
}

public sealed class BrowserCaptureMedia
{
    public int Frames { get; init; }
    public int UnreadableFrames { get; init; }
    public int Canvases { get; init; }
    public int Videos { get; init; }
}

public sealed class BrowserCaptureOutput
{
    public int Width { get; init; }
    public int Height { get; init; }
}
