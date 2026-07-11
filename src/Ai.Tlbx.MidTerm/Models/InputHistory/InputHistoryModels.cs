using System.Text.Json.Serialization;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.Models.InputHistory;

public static class InputHistoryKinds
{
    public const string Prompt = "prompt";
    public const string TextPaste = "textPaste";
    public const string ImagePaste = "imagePaste";
    public const string FileUpload = "fileUpload";
}

public static class InputHistorySources
{
    public const string CommandBay = "commandBay";
    public const string SessionPrompt = "sessionPrompt";
    public const string TerminalPaste = "terminalPaste";
    public const string Clipboard = "clipboard";
    public const string FileDrop = "fileDrop";
    public const string Upload = "upload";
    public const string UploadPath = "uploadPath";
    public const string ControlPlaneDispatch = "controlPlaneDispatch";
    public const string HistoryReplay = "historyReplay";
}

public static class InputHistorySurfaces
{
    public const string Terminal = "terminal";
    public const string AgentControl = "agentControl";
}

public sealed class InputHistoryEntry
{
    public string Id { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string? SessionName { get; set; }
    public string? WorkingDirectory { get; set; }
    public string Kind { get; set; } = InputHistoryKinds.TextPaste;
    public string Source { get; set; } = InputHistorySources.TerminalPaste;
    public string Surface { get; set; } = InputHistorySurfaces.Terminal;
    public DateTimeOffset CreatedAt { get; set; }
    public string? Text { get; set; }
    public string? Path { get; set; }
    public string? DisplayName { get; set; }
    public string? MimeType { get; set; }
    public long? SizeBytes { get; set; }
    public bool BracketedPaste { get; set; }
    public bool IsFilePath { get; set; }
    public bool Submit { get; set; }
    public AppServerControlTurnRequest? Turn { get; set; }
}

public sealed class InputHistoryDocument
{
    public List<InputHistoryEntry> Entries { get; set; } = [];
}

public sealed class InputHistoryListResponse
{
    public int TotalCount { get; set; }
    public List<InputHistoryEntry> Entries { get; set; } = [];
}

public sealed class InputHistoryReplayRequest
{
    public string? TargetSessionId { get; set; }
}

public sealed class InputHistoryReplayResponse
{
    public string EntryId { get; set; } = string.Empty;
    public string TargetSessionId { get; set; } = string.Empty;
    public bool Accepted { get; set; }
    public bool Queued { get; set; }
}

public sealed class InputHistoryClearResponse
{
    public string SessionId { get; set; } = string.Empty;
    public int RemovedCount { get; set; }
}

[JsonSerializable(typeof(InputHistoryDocument))]
[JsonSerializable(typeof(InputHistoryEntry))]
[JsonSerializable(typeof(List<InputHistoryEntry>))]
[JsonSerializable(typeof(AppServerControlTurnRequest))]
[JsonSerializable(typeof(AppServerControlAttachmentReference))]
[JsonSerializable(typeof(List<AppServerControlAttachmentReference>))]
[JsonSerializable(typeof(AppServerControlTerminalReplayStep))]
[JsonSerializable(typeof(List<AppServerControlTerminalReplayStep>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class InputHistoryJsonContext : JsonSerializerContext
{
}
