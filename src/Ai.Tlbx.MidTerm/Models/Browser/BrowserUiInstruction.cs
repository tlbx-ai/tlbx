namespace Ai.Tlbx.MidTerm.Models.Browser;

public sealed class BrowserUiInstruction
{
    public string Type { get; set; } = "browser-ui";
    public string Command { get; set; } = "";
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? Url { get; set; }
    public string? SessionId { get; set; }
    public string? PreviewName { get; set; }
    public bool? ActivateSession { get; set; }
    public string? DeviceAction { get; set; }
    public string? DeviceProfile { get; set; }
    public string? RequestId { get; set; }
    public long? TargetRevision { get; set; }
    public double? DeltaY { get; set; }
    public int? Steps { get; set; }
}

public sealed class BrowserUiCommandResult
{
    public string? SessionId { get; init; }
    public string? PreviewName { get; init; }
    public long? TargetRevision { get; init; }
    public BrowserMobileDeviceState? MobileDevice { get; init; }
    public string? ScreenshotDataUrl { get; init; }
    public long OwnershipGeneration { get; init; }
    public string RequestId { get; init; } = "";
    public string Command { get; init; } = "";
    public bool Success { get; init; }
    public string? Error { get; init; }
    public BrowserViewportMetrics? Viewport { get; init; }
}

public sealed class BrowserMobileDeviceState
{
    public bool Available { get; init; }
    public bool Connected { get; init; }
    public bool? Open { get; init; }
    public string? Error { get; init; }
    public string? ProfileId { get; init; }
    public string? ProfileLabel { get; init; }
    public string? Orientation { get; init; }
    public bool? Keyboard { get; init; }
    public bool? Background { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? DeviceScaleFactor { get; init; }
}

public sealed class BrowserViewportMetrics
{
    public int Width { get; init; }
    public int Height { get; init; }
    public double Scale { get; init; }
    // Rectangles are [x, y, width, height] in the owning tlbx document.
    public double[] Frame { get; init; } = [];
    public double[] Host { get; init; } = [];
    public bool Visible { get; init; }
    public bool Fits => Width > 0 && Height > 0
        && Frame is { Length: 4 } && Host is { Length: 4 }
        && Frame[2] > 0 && Frame[3] > 0
        && Frame[0] >= Host[0] - 0.5 && Frame[1] >= Host[1] - 0.5
        && Frame[0] + Frame[2] <= Host[0] + Host[2] + 0.5
        && Frame[1] + Frame[3] <= Host[1] + Host[3] + 0.5;
}

public sealed class MobileDeviceRequest
{
    public string Action { get; init; } = "status";
    public string? Profile { get; init; }
    public string? SessionId { get; init; }
    public string? PreviewName { get; init; }
}

public sealed class ViewportRequest
{
    public int Width { get; init; }
    public int Height { get; init; }
    public string? SessionId { get; init; }
    public string? PreviewName { get; init; }
}
