using System.Text.Json.Serialization;

namespace Ai.Tlbx.MidTerm.Services.Boards;

public sealed record BoardDocument
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "Untitled board";
    public long Revision { get; set; }
    public double PanX { get; set; }
    public double PanY { get; set; }
    public double Zoom { get; set; } = 1;
    public string? WorkingDirectory { get; set; }
    public string? LaunchCommand { get; set; }
    public string? SessionProfile { get; set; }
    public bool AppServerControlOnly { get; set; }
    public List<BoardItem> Items { get; set; } = [];
}

public sealed record BoardItem
{
    public string Id { get; set; } = "";
    public long Revision { get; set; }
    public string Kind { get; set; } = "note";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string Color { get; set; } = "sand";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 300;
    public double Height { get; set; } = 240;
    public int Z { get; set; }
    public List<BoardEntry> Entries { get; set; } = [];
    public string? Url { get; set; }
    public string? LocalDirectory { get; set; }
    public string? BrowserSessionId { get; set; }
    public string? BrowserPreviewName { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? LaunchCommand { get; set; }
    public string? Prompt { get; set; }
    public string? SessionProfile { get; set; }
    public bool? AppServerControlOnly { get; set; }
    public List<BoardLaunch> Launches { get; set; } = [];
    public string? CalendarMonth { get; set; }
}

public sealed record BoardEntry
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public string? Date { get; set; }
    public int? Year { get; set; }
    public int? Week { get; set; }
    public int? Quarter { get; set; }
    public string? Sender { get; set; }
    public string? Url { get; set; }
}

public sealed record BoardPatch
{
    public long? ExpectedRevision { get; set; }
    public string? Title { get; set; }
    public double? PanX { get; set; }
    public double? PanY { get; set; }
    public double? Zoom { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? LaunchCommand { get; set; }
    public string? SessionProfile { get; set; }
    public bool? AppServerControlOnly { get; set; }
}

public sealed record BoardViewport(double PanX, double PanY, double Zoom);

public sealed record BoardItemWrite
{
    public long? ExpectedRevision { get; set; }
    public BoardItem Item { get; set; } = new();
}

public sealed record BoardSignal(string Type, string BoardId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BoardDocument))]
[JsonSerializable(typeof(List<BoardDocument>))]
[JsonSerializable(typeof(BoardItem))]
[JsonSerializable(typeof(BoardPatch))]
[JsonSerializable(typeof(BoardItemWrite))]
[JsonSerializable(typeof(BoardCalendarMonth))]
[JsonSerializable(typeof(BoardSignal))]
[JsonSerializable(typeof(BoardViewport))]
[JsonSerializable(typeof(BoardLaunch))]
[JsonSerializable(typeof(List<BoardLaunch>))]
[JsonSerializable(typeof(BoardLaunchRequest))]
internal partial class BoardJsonContext : JsonSerializerContext;
