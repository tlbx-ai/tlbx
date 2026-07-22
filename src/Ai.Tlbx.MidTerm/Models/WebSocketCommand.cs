namespace Ai.Tlbx.MidTerm.Models;

/// <summary>
/// WebSocket command message from client.
/// </summary>
public sealed class WsCommand
{
    public string Type { get; init; } = "";
    public string Id { get; init; } = "";
    public string Action { get; init; } = "";
    public WsCommandPayload? Payload { get; init; }
}

/// <summary>
/// Payload for WebSocket commands. Contains optional fields for all command types.
/// </summary>
public sealed class WsCommandPayload
{
    // session.create
    public int? Cols { get; init; }
    public int? Rows { get; init; }
    public string? Shell { get; init; }
    public string? WorkingDirectory { get; init; }

    // session.close, session.rename
    public string? SessionId { get; init; }

    // session.rename
    public string? Name { get; init; }
    public bool? Auto { get; init; }

    // session.reorder - array of session IDs in desired order
    public List<string>? SessionIds { get; init; }

    // browser.setActivity
    public bool? IsActive { get; init; }
    public string? ActiveSessionId { get; init; }
    public string? ActiveSurface { get; init; }

    // terminal.requestSizeControl, terminal.resize
    public bool? Force { get; init; }
    public long? ExpectedEpoch { get; init; }

    // settings.save - full settings object
    public Settings.MidTermSettingsPublic? Settings { get; init; }
}

/// <summary>
/// WebSocket command response from server.
/// </summary>
public sealed class WsCommandResponse
{
    public string Type { get; init; } = "response";
    public string Id { get; init; } = "";
    public bool Success { get; init; }
    public object? Data { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Data returned for session.create command.
/// </summary>
public sealed class WsSessionCreatedData
{
    public string Id { get; init; } = "";
    public int Pid { get; init; }
    public string ShellType { get; init; } = "";
}
