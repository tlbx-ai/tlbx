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
