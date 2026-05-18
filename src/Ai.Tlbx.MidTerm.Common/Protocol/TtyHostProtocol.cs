using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ai.Tlbx.MidTerm.Common.Logging;

namespace Ai.Tlbx.MidTerm.Common.Protocol;

/// <summary>
/// Binary IPC protocol between mm and mmttyhost.
/// Format: [1 byte type][4 bytes length][payload]
/// </summary>
public static class TtyHostProtocol
{
    public const int HeaderSize = 5;
    public const int MaxPayloadSize = 1024 * 1024;
    public const int InputTraceMarkerPayloadSize = 4;
    public const int MinimumInputTraceReportPayloadSize = 28;
    public const int InputTraceReportPayloadSize = 68;

    public static byte[] CreateInfoRequest()
    {
        return CreateFrame(TtyHostMessageType.GetInfo, []);
    }

    public static byte[] CreateAttachRequest(TtyHostAttachRequest request)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(request, TtyHostJsonContext.Default.TtyHostAttachRequest);
        return CreateFrame(TtyHostMessageType.Attach, json);
    }

    public static TtyHostAttachRequest? ParseAttachRequest(ReadOnlySpan<byte> payload)
    {
        return JsonSerializer.Deserialize(payload, TtyHostJsonContext.Default.TtyHostAttachRequest);
    }

    public static byte[] CreateAttachAck(bool accepted, string? message = null)
    {
        var response = new TtyHostAttachResponse
        {
            Accepted = accepted,
            Message = message
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(response, TtyHostJsonContext.Default.TtyHostAttachResponse);
        return CreateFrame(TtyHostMessageType.AttachAck, json);
    }

    public static TtyHostAttachResponse? ParseAttachAck(ReadOnlySpan<byte> payload)
    {
        return JsonSerializer.Deserialize(payload, TtyHostJsonContext.Default.TtyHostAttachResponse);
    }

    public static byte[] CreateInfoResponse(SessionInfo info)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(info, TtyHostJsonContext.Default.SessionInfo);
        return CreateFrame(TtyHostMessageType.Info, json);
    }

    /// <summary>
    /// Writes an input message into a pre-allocated buffer. Zero allocations.
    /// Destination must be at least HeaderSize + data.Length bytes.
    /// </summary>
    public static void WriteInputFrameInto(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        destination[0] = (byte)TtyHostMessageType.Input;
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(1, 4), data.Length);
        data.CopyTo(destination.Slice(HeaderSize));
    }

    public static void WriteInputTraceMarkerFrameInto(uint traceId, Span<byte> destination)
    {
        destination[0] = (byte)TtyHostMessageType.InputTraceMarker;
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(1, 4), InputTraceMarkerPayloadSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(HeaderSize, 4), traceId);
    }

    public static bool TryParseInputTraceMarker(ReadOnlySpan<byte> payload, out uint traceId)
    {
        traceId = 0;
        if (payload.Length < InputTraceMarkerPayloadSize)
        {
            return false;
        }

        traceId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        return traceId != 0;
    }

    /// <summary>
    /// Creates an input message using a pooled buffer. Zero allocations.
    /// Callback receives the frame; buffer is returned to pool after callback.
    /// </summary>
    public static void WriteInputMessage(ReadOnlySpan<byte> data, Action<ReadOnlySpan<byte>> callback)
    {
        var frameSize = HeaderSize + data.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(frameSize);
        try
        {
            buffer[0] = (byte)TtyHostMessageType.Input;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(1, 4), data.Length);
            data.CopyTo(buffer.AsSpan(HeaderSize));
            callback(buffer.AsSpan(0, frameSize));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Creates an output message using a pooled buffer. Zero allocations.
    /// Callback receives the frame; buffer is returned to pool after callback.
    /// </summary>
    public static void WriteOutputMessage(ulong sequenceStart, int cols, int rows, ReadOnlySpan<byte> data, Action<ReadOnlySpan<byte>> callback)
    {
        var frameSize = HeaderSize + 12 + data.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(frameSize);
        try
        {
            buffer[0] = (byte)TtyHostMessageType.Output;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(1, 4), 12 + data.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(HeaderSize, 8), sequenceStart);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(HeaderSize + 8, 2), (ushort)cols);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(HeaderSize + 10, 2), (ushort)rows);
            data.CopyTo(buffer.AsSpan(HeaderSize + 12));
            callback(buffer.AsSpan(0, frameSize));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static ulong ParseOutputSequenceStart(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
        {
            return 0;
        }

        return BinaryPrimitives.ReadUInt64LittleEndian(payload[..8]);
    }

    public static (int cols, int rows) ParseOutputDimensions(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12)
        {
            return (0, 0);
        }
        var cols = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2));
        var rows = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(10, 2));
        return (cols, rows);
    }

    public static ReadOnlySpan<byte> GetOutputData(ReadOnlySpan<byte> payload)
    {
        return payload.Length >= 12 ? payload.Slice(12) : payload;
    }

    public static byte[] CreateResizeMessage(int cols, int rows)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), cols);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), rows);
        return CreateFrame(TtyHostMessageType.Resize, payload);
    }

    public static byte[] CreateResizeAck()
    {
        return CreateFrame(TtyHostMessageType.ResizeAck, []);
    }

    public static byte[] CreateStateChange(bool isRunning, int? exitCode)
    {
        var payload = new StateChangePayload { IsRunning = isRunning, ExitCode = exitCode };
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, TtyHostJsonContext.Default.StateChangePayload);
        return CreateFrame(TtyHostMessageType.StateChange, json);
    }

    public static byte[] CreateGetBuffer(int? maxBytes = null, TerminalReplayReason reason = TerminalReplayReason.Manual)
    {
        var payload = new TtyHostGetBufferRequest
        {
            MaxBytes = maxBytes,
            Reason = reason
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, TtyHostJsonContext.Default.TtyHostGetBufferRequest);
        return CreateFrame(TtyHostMessageType.GetBuffer, json);
    }

    public static TtyHostGetBufferRequest? ParseGetBuffer(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            return new TtyHostGetBufferRequest();
        }

        return JsonSerializer.Deserialize(payload, TtyHostJsonContext.Default.TtyHostGetBufferRequest);
    }

    /// <summary>
    /// Creates a buffer response using a pooled buffer. Zero allocations.
    /// Callback receives the frame; buffer is returned to pool after callback.
    /// </summary>
    public static void WriteBufferResponse(ulong sequenceStart, ReadOnlySpan<byte> data, Action<ReadOnlySpan<byte>> callback)
    {
        var frameSize = HeaderSize + 8 + data.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(frameSize);
        try
        {
            buffer[0] = (byte)TtyHostMessageType.Buffer;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(1, 4), 8 + data.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(HeaderSize, 8), sequenceStart);
            data.CopyTo(buffer.AsSpan(HeaderSize + 8));
            callback(buffer.AsSpan(0, frameSize));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static TtyHostBufferSnapshot ParseBuffer(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
        {
            return new TtyHostBufferSnapshot
            {
                SequenceStart = 0,
                Data = payload.ToArray()
            };
        }

        return new TtyHostBufferSnapshot
        {
            SequenceStart = BinaryPrimitives.ReadUInt64LittleEndian(payload[..8]),
            Data = payload[8..].ToArray()
        };
    }

    public static byte[] CreateClose()
    {
        return CreateFrame(TtyHostMessageType.Close, []);
    }

    public static byte[] CreateCloseAck()
    {
        return CreateFrame(TtyHostMessageType.CloseAck, []);
    }

    public static byte[] CreateSetName(string? name)
    {
        var payload = Encoding.UTF8.GetBytes(name ?? string.Empty);
        return CreateFrame(TtyHostMessageType.SetName, payload);
    }

    public static byte[] CreateSetNameAck()
    {
        return CreateFrame(TtyHostMessageType.SetNameAck, []);
    }

    public static byte[] CreateSetClipboardImage(string filePath, string? mimeType)
    {
        var payload = new ClipboardImageRequest
        {
            FilePath = filePath,
            MimeType = mimeType
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, TtyHostJsonContext.Default.ClipboardImageRequest);
        return CreateFrame(TtyHostMessageType.SetClipboardImage, json);
    }

    public static ClipboardImageRequest? ParseSetClipboardImage(ReadOnlySpan<byte> payload)
    {
        return JsonSerializer.Deserialize(payload, TtyHostJsonContext.Default.ClipboardImageRequest);
    }

    public static byte[] CreateSetClipboardImageAck(bool success, string? error = null)
    {
        var payload = new ClipboardImageResponse
        {
            Success = success,
            Error = error
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, TtyHostJsonContext.Default.ClipboardImageResponse);
        return CreateFrame(TtyHostMessageType.SetClipboardImageAck, json);
    }

    public static ClipboardImageResponse? ParseSetClipboardImageAck(ReadOnlySpan<byte> payload)
    {
        return JsonSerializer.Deserialize(payload, TtyHostJsonContext.Default.ClipboardImageResponse);
    }

    private static byte[] CreateFrame(TtyHostMessageType type, byte[] payload)
    {
        var frame = new byte[HeaderSize + payload.Length];
        frame[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1, 4), payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> buffer, out TtyHostMessageType type, out int payloadLength)
    {
        type = default;
        payloadLength = 0;

        if (buffer.Length < HeaderSize)
        {
            return false;
        }

        type = (TtyHostMessageType)buffer[0];
        payloadLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(1, 4));
        return true;
    }

    public static SessionInfo? ParseInfo(ReadOnlySpan<byte> payload)
    {
        return JsonSerializer.Deserialize(payload, TtyHostJsonContext.Default.SessionInfo);
    }

    public static StateChangePayload? ParseStateChange(ReadOnlySpan<byte> payload)
    {
        return JsonSerializer.Deserialize(payload, TtyHostJsonContext.Default.StateChangePayload);
    }

    public static (int cols, int rows) ParseResize(ReadOnlySpan<byte> payload)
    {
        var cols = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(0, 4));
        var rows = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4));
        return (cols, rows);
    }

    public static string ParseSetName(ReadOnlySpan<byte> payload)
    {
        return Encoding.UTF8.GetString(payload);
    }

    public static byte[] CreateForegroundChange(ForegroundChangePayload payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, TtyHostJsonContext.Default.ForegroundChangePayload);
        return CreateFrame(TtyHostMessageType.ForegroundChange, json);
    }

    public static ForegroundChangePayload? ParseForegroundChange(ReadOnlySpan<byte> payload)
    {
        return JsonSerializer.Deserialize(payload, TtyHostJsonContext.Default.ForegroundChangePayload);
    }

    public static byte[] CreatePing(ReadOnlySpan<byte> payload)
    {
        return CreateFrame(TtyHostMessageType.Ping, payload.ToArray());
    }

    public static byte[] CreatePong(ReadOnlySpan<byte> payload)
    {
        return CreateFrame(TtyHostMessageType.Pong, payload.ToArray());
    }

    public static byte[] CreateInputTraceReport(TtyHostInputTraceReport report)
    {
        var payload = new byte[InputTraceReportPayloadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), report.TraceId);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(4, 8), report.MarkerReceivedAtMs);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(12, 8), report.InputReceivedAtMs);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(20, 8), report.PtyWriteDoneAtMs);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(28, 8), report.FirstOutputSequenceEndExclusive);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(36, 8), report.PtyOutputReadAtMs);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(44, 8), report.IpcOutputEnqueuedAtMs);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(52, 8), report.IpcOutputWriteDoneAtMs);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(60, 8), report.IpcOutputFlushDoneAtMs);
        return CreateFrame(TtyHostMessageType.InputTrace, payload);
    }

    public static TtyHostInputTraceReport? ParseInputTraceReport(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < MinimumInputTraceReportPayloadSize)
        {
            return null;
        }

        var traceId = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
        if (traceId == 0)
        {
            return null;
        }

        return new TtyHostInputTraceReport(
            traceId,
            BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(4, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(12, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(20, 8)),
            payload.Length >= 36 ? BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(28, 8)) : 0,
            payload.Length >= 44 ? BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(36, 8)) : 0,
            payload.Length >= 52 ? BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(44, 8)) : 0,
            payload.Length >= 60 ? BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(52, 8)) : 0,
            payload.Length >= 68 ? BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(60, 8)) : 0);
    }

    public static byte[] CreateDataLoss(TtyHostDataLossPayload payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, TtyHostJsonContext.Default.TtyHostDataLossPayload);
        return CreateFrame(TtyHostMessageType.DataLoss, json);
    }

    public static TtyHostDataLossPayload? ParseDataLoss(ReadOnlySpan<byte> payload)
    {
        return JsonSerializer.Deserialize(payload, TtyHostJsonContext.Default.TtyHostDataLossPayload);
    }

    public static byte[] CreateSetOrder(byte order)
    {
        return CreateFrame(TtyHostMessageType.SetOrder, [order]);
    }

    public static byte[] CreateSetOrderAck()
    {
        return CreateFrame(TtyHostMessageType.SetOrderAck, []);
    }

    public static byte ParseSetOrder(ReadOnlySpan<byte> payload)
    {
        return payload.Length > 0 ? payload[0] : (byte)0;
    }

    public static byte[] CreateSetMetadata(TtyHostSessionMetadata metadata)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(metadata, TtyHostJsonContext.Default.TtyHostSessionMetadata);
        return CreateFrame(TtyHostMessageType.SetMetadata, json);
    }

    public static TtyHostSessionMetadata? ParseSetMetadata(ReadOnlySpan<byte> payload)
    {
        return JsonSerializer.Deserialize(payload, TtyHostJsonContext.Default.TtyHostSessionMetadata);
    }

    public static byte[] CreateSetMetadataAck()
    {
        return CreateFrame(TtyHostMessageType.SetMetadataAck, []);
    }
}

/// <summary>
/// Message types for the TtyHost IPC protocol.
/// </summary>
public enum TtyHostMessageType : byte
{
    GetInfo = 0x01,
    Info = 0x02,
    GetBuffer = 0x03,
    Buffer = 0x04,
    Attach = 0x05,
    AttachAck = 0x06,

    Input = 0x10,
    Output = 0x11,

    Resize = 0x20,
    ResizeAck = 0x21,
    SetName = 0x22,
    SetNameAck = 0x23,
    SetClipboardImage = 0x26,
    SetClipboardImageAck = 0x27,
    Close = 0x30,
    CloseAck = 0x31,

    StateChange = 0x40,

    // Process monitoring
    ProcessEvent = 0x50,
    ForegroundChange = 0x51,
    ProcessSnapshot = 0x52,

    // Display order
    SetOrder = 0x24,
    SetOrderAck = 0x25,
    SetMetadata = 0x28,
    SetMetadataAck = 0x29,

    // Latency measurement
    Ping = 0x60,
    Pong = 0x61,

    // Transport recovery
    DataLoss = 0x62,

    // Input latency diagnostics
    InputTraceMarker = 0x63,
    InputTrace = 0x64
}

public readonly record struct TtyHostInputTraceReport(
    uint TraceId,
    long MarkerReceivedAtMs,
    long InputReceivedAtMs,
    long PtyWriteDoneAtMs,
    ulong FirstOutputSequenceEndExclusive = 0,
    long PtyOutputReadAtMs = 0,
    long IpcOutputEnqueuedAtMs = 0,
    long IpcOutputWriteDoneAtMs = 0,
    long IpcOutputFlushDoneAtMs = 0);

/// <summary>
/// Session metadata exchanged between mt and mthost.
/// </summary>
public sealed class SessionInfo
{
    [JsonIgnore]
    private readonly Lock _lock = new();

    private int _cols;
    private int _rows;
    private bool _isRunning;
    private int? _exitCode;
    private string? _name;
    private string? _terminalTitle;
    private bool _manuallyNamed;
    private string? _currentDirectory;
    private int? _foregroundPid;
    private string? _foregroundName;
    private string? _foregroundCommandLine;
    private string? _foregroundDisplayName;
    private string? _foregroundProcessIdentity;
    private SessionAgentAttachPoint? _agentAttachPoint;
    private byte _order;
    private TtyHostTransportInfo? _transport;
    private string? _topic;
    private TtyHostGitRepoMetadata[] _extraGitRepos = [];

    public string Id { get; set; } = string.Empty;
    public int Pid { get; set; }
    public int HostPid { get; set; }
    public string ShellType { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? TtyHostVersion { get; set; }
    public string? OwnerInstanceId { get; set; }

    public int Cols { get => Lock(() => _cols); set => Lock(() => _cols = value); }
    public int Rows { get => Lock(() => _rows); set => Lock(() => _rows = value); }
    public bool IsRunning { get => Lock(() => _isRunning); set => Lock(() => _isRunning = value); }
    public int? ExitCode { get => Lock(() => _exitCode); set => Lock(() => _exitCode = value); }
    public string? Name { get => Lock(() => _name); set => Lock(() => _name = value); }
    public string? TerminalTitle { get => Lock(() => _terminalTitle); set => Lock(() => _terminalTitle = value); }
    public bool ManuallyNamed { get => Lock(() => _manuallyNamed); set => Lock(() => _manuallyNamed = value); }
    public string? CurrentDirectory { get => Lock(() => _currentDirectory); set => Lock(() => _currentDirectory = value); }
    public int? ForegroundPid { get => Lock(() => _foregroundPid); set => Lock(() => _foregroundPid = value); }
    public string? ForegroundName { get => Lock(() => _foregroundName); set => Lock(() => _foregroundName = value); }
    public string? ForegroundCommandLine { get => Lock(() => _foregroundCommandLine); set => Lock(() => _foregroundCommandLine = value); }
    public string? ForegroundDisplayName { get => Lock(() => _foregroundDisplayName); set => Lock(() => _foregroundDisplayName = value); }
    public string? ForegroundProcessIdentity { get => Lock(() => _foregroundProcessIdentity); set => Lock(() => _foregroundProcessIdentity = value); }
    public SessionAgentAttachPoint? AgentAttachPoint { get => Lock(() => _agentAttachPoint); set => Lock(() => _agentAttachPoint = value); }
    public byte Order { get => Lock(() => _order); set => Lock(() => _order = value); }
    public TtyHostTransportInfo? Transport { get => Lock(() => _transport); set => Lock(() => _transport = value); }
    public string? Topic { get => Lock(() => _topic); set => Lock(() => _topic = value); }
    public TtyHostGitRepoMetadata[] ExtraGitRepos { get => Lock(() => _extraGitRepos); set => Lock(() => _extraGitRepos = value ?? []); }

    private T Lock<T>(Func<T> func)
    {
        lock (_lock) return func();
    }

    private void Lock(Action action)
    {
        lock (_lock) action();
    }
}

/// <summary>
/// Payload for session state change notifications.
/// </summary>
public sealed class StateChangePayload
{
    public bool IsRunning { get; set; }
    public int? ExitCode { get; set; }
}

/// <summary>
/// Payload for foreground process change notifications.
/// </summary>
public sealed class ForegroundChangePayload
{
    public int Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? CommandLine { get; set; }
    public string? Cwd { get; set; }
    public string? DisplayName { get; set; }
    public string? ProcessIdentity { get; set; }
    public SessionAgentAttachPoint? AgentAttachPoint { get; set; }
}

public sealed class ClipboardImageRequest
{
    public string FilePath { get; set; } = string.Empty;
    public string? MimeType { get; set; }
}

public sealed class ClipboardImageResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public sealed class TtyHostSessionMetadata
{
    public string? Topic { get; set; }
    public TtyHostGitRepoMetadata[] ExtraGitRepos { get; set; } = [];
}

public sealed class TtyHostGitRepoMetadata
{
    public string RepoRoot { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? Role { get; set; }
    public string? Source { get; set; }
}

public sealed class TtyHostAttachRequest
{
    public string InstanceId { get; set; } = string.Empty;
    public string OwnerToken { get; set; } = string.Empty;
}

public sealed class TtyHostAttachResponse
{
    public bool Accepted { get; set; }
    public string? Message { get; set; }
}

public sealed class TtyHostGetBufferRequest
{
    public int? MaxBytes { get; set; }
    public TerminalReplayReason Reason { get; set; } = TerminalReplayReason.Manual;
}

public sealed class TtyHostBufferSnapshot
{
    public ulong SequenceStart { get; set; }
    public byte[] Data { get; set; } = [];
}

public sealed class TtyHostDataLossPayload
{
    public TerminalReplayReason Reason { get; set; } = TerminalReplayReason.Manual;
    public int DroppedBytes { get; set; }
}

public sealed class TtyHostTransportInfo
{
    public ulong SourceSeq { get; set; }
    public ulong IpcQueuedSeq { get; set; }
    public ulong IpcFlushedSeq { get; set; }
    public int IpcBacklogFrames { get; set; }
    public long IpcBacklogBytes { get; set; }
    public long OldestBacklogAgeMs { get; set; }
    public int ScrollbackBytes { get; set; }
    public int LastReplayBytes { get; set; }
    public TerminalReplayReason? LastReplayReason { get; set; }
    public int DataLossCount { get; set; }
    public TerminalReplayReason? LastDataLossReason { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<TerminalReplayReason>))]
public enum TerminalReplayReason
{
    Manual = 0,
    MthostIpcOverflow = 1,
    MuxOverflow = 2,
    BrowserPendingOverflow = 3,
    IpcTimeoutReconnect = 4,
    BufferRefreshTailReplay = 5,
    ReconnectTailReplay = 6,
    QuickResumeTailReplay = 7
}

[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(SessionAgentAttachPoint))]
[JsonSerializable(typeof(TtyHostSessionMetadata))]
[JsonSerializable(typeof(TtyHostGitRepoMetadata))]
[JsonSerializable(typeof(StateChangePayload))]
[JsonSerializable(typeof(ForegroundChangePayload))]
[JsonSerializable(typeof(ClipboardImageRequest))]
[JsonSerializable(typeof(ClipboardImageResponse))]
[JsonSerializable(typeof(TtyHostAttachRequest))]
[JsonSerializable(typeof(TtyHostAttachResponse))]
[JsonSerializable(typeof(TtyHostGetBufferRequest))]
[JsonSerializable(typeof(TtyHostDataLossPayload))]
[JsonSerializable(typeof(TtyHostTransportInfo))]
public partial class TtyHostJsonContext : JsonSerializerContext
{
}
