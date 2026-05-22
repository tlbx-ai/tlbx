using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.Services.WebSockets;

/// <summary>
/// Binary protocol for multiplexed WebSocket communication.
/// Base frame format: [1 byte type][8 byte sessionId][payload]
/// Output frame format: [1 byte type][8 byte sessionId][2 byte cols][2 byte rows][payload]
/// SessionId is the first 8 chars of the session GUID (already 8 chars).
/// </summary>
public static class MuxProtocol
{
    private static readonly ConcurrentDictionary<ulong, string> _sessionIdCache = new();

    public const int HeaderSize = 9; // 1 byte type + 8 bytes sessionId
    public const int OutputHeaderSize = 21; // HeaderSize + seq(8) + dims(4)
    public const int MaxFrameSize = 64 * 1024;

    // Protocol versioning
    public const ushort ProtocolVersion = 1;
    public const ushort MinCompatibleProtocolVersion = 1;

    // Custom WebSocket close codes (4000-4999 range)
    public const int CloseAuthFailed = 4401;
    public const int CloseServerShutdown = 4503;
    public const int CloseProtocolError = 4400;

    public const byte TypeTerminalOutput = 0x01;
    public const byte TypeTerminalInput = 0x02;
    public const byte TypeResize = 0x03;
    public const byte TypeSessionState = 0x04;
    public const byte TypeResync = 0x05; // Server -> Client: clear all terminals, buffer refresh follows
    public const byte TypeBufferRequest = 0x06; // Client -> Server: request buffer refresh for session
    public const byte TypeCompressedOutput = 0x07; // Server -> Client: GZip compressed terminal output
    public const byte TypeActiveSessionHint = 0x08; // Client -> Server: hint which session is active (for priority)
    public const byte TypePing = 0x09; // Client -> Server: latency measurement ping
    public const byte TypeForegroundChange = 0x0A; // Server -> Client: foreground process changed
    public const byte TypeDataLoss = 0x0B; // Server -> Client: background session dropped data, resync recommended
    public const byte TypePong = 0x0C; // Server -> Client: latency measurement pong
    public const byte TypeSyncComplete = 0x0D; // Server -> Client: initial buffer replay finished
    public const byte TypeVisibleSessionsHint = 0x0E; // Client -> Server: visible terminal sessions for quick resume
    public const byte TypeInputTraceMarker = 0x0F; // Client -> Server: sample the next input for latency tracing
    public const byte TypeInputTraceResult = 0x10; // Server -> Client: sampled input latency trace result

    // Compression settings
    public const int CompressionChunkSize = 256 * 1024; // Chunk large data before compressing
    public const int CompressionThreshold = 8192; // Only compress payloads > 8KB (buffer replays)
    public const int CompressedOutputHeaderSize = 25; // HeaderSize + seq(8) + dims(4) + uncompressedLen(4)
    public const int InputTraceMarkerPayloadSize = 4;
    public const int InputTraceResultPayloadSize = 68;

    public const byte BufferRequestModeFullReplay = 0x00;
    public const byte BufferRequestModeQuickResume = 0x01;

    public readonly record struct BufferRequestOptions(bool QuickResume, int? ReplayRows);

    public static byte[] CreateOutputFrame(string sessionId, ulong sequenceEndExclusive, int cols, int rows, ReadOnlySpan<byte> data)
    {
        var frame = new byte[OutputHeaderSize + data.Length];
        frame[0] = TypeTerminalOutput;
        WriteSessionId(frame.AsSpan(1, 8), sessionId);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(9, 8), sequenceEndExclusive);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(17, 2), (ushort)cols);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(19, 2), (ushort)rows);
        data.CopyTo(frame.AsSpan(OutputHeaderSize));
        return frame;
    }

    /// <summary>
    /// Creates an uncompressed output frame using pooled buffers. Zero allocations.
    /// Callback receives the frame data; buffer is returned to pool after callback.
    /// </summary>
    public static void WriteOutputFrame(
        string sessionId, ulong sequenceEndExclusive, int cols, int rows, ReadOnlySpan<byte> data,
        Action<ReadOnlyMemory<byte>> callback)
    {
        var frameSize = OutputHeaderSize + data.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(frameSize);
        try
        {
            buffer[0] = TypeTerminalOutput;
            WriteSessionId(buffer.AsSpan(1, 8), sessionId);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(9, 8), sequenceEndExclusive);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(17, 2), (ushort)cols);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(19, 2), (ushort)rows);
            data.CopyTo(buffer.AsSpan(OutputHeaderSize));

            callback(new ReadOnlyMemory<byte>(buffer, 0, frameSize));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Writes a compressed output frame into a caller-provided buffer.
    /// Returns the total bytes written. Caller is responsible for buffer lifecycle.
    /// Buffer must be at least CompressedOutputHeaderSize + data.Length + 100 bytes.
    /// </summary>
    public static int WriteCompressedOutputFrameInto(
        string sessionId, ulong sequenceEndExclusive, int cols, int rows, ReadOnlySpan<byte> data,
        byte[] destination)
    {
        destination[0] = TypeCompressedOutput;
        WriteSessionId(destination.AsSpan(1, 8), sessionId);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.AsSpan(9, 8), sequenceEndExclusive);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(17, 2), (ushort)cols);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(19, 2), (ushort)rows);
        BinaryPrimitives.WriteInt32LittleEndian(destination.AsSpan(21, 4), data.Length);

        using var ms = new MemoryStream(destination, CompressedOutputHeaderSize,
            destination.Length - CompressedOutputHeaderSize);
        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return CompressedOutputHeaderSize + (int)ms.Position;
    }

    /// <summary>
    /// Writes an uncompressed output frame into a caller-provided buffer.
    /// Returns the total bytes written. Caller is responsible for buffer lifecycle.
    /// Buffer must be at least OutputHeaderSize + data.Length bytes.
    /// </summary>
    public static int WriteOutputFrameInto(
        string sessionId, ulong sequenceEndExclusive, int cols, int rows, ReadOnlySpan<byte> data,
        byte[] destination)
    {
        destination[0] = TypeTerminalOutput;
        WriteSessionId(destination.AsSpan(1, 8), sessionId);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.AsSpan(9, 8), sequenceEndExclusive);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(17, 2), (ushort)cols);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(19, 2), (ushort)rows);
        data.CopyTo(destination.AsSpan(OutputHeaderSize));
        return OutputHeaderSize + data.Length;
    }

    public static byte[] CreateStateFrame(string sessionId, bool created)
    {
        var frame = new byte[HeaderSize + 1];
        frame[0] = TypeSessionState;
        WriteSessionId(frame.AsSpan(1, 8), sessionId);
        frame[HeaderSize] = created ? (byte)1 : (byte)0;
        return frame;
    }

    /// <summary>
    /// Creates a resync frame that tells client to clear all terminals.
    /// Buffer refresh will follow immediately after.
    /// </summary>
    public static byte[] CreateClearScreenFrame()
    {
        var frame = new byte[HeaderSize];
        frame[0] = TypeResync;
        // Session ID is all zeros (applies to all sessions)
        return frame;
    }

    public static bool TryParseFrame(
        ReadOnlySpan<byte> data,
        out byte type,
        out string sessionId,
        out ReadOnlySpan<byte> payload)
    {
        type = 0;
        sessionId = string.Empty;
        payload = default;

        if (data.Length < HeaderSize)
        {
            return false;
        }

        type = data[0];

        var sessionIdSpan = data.Slice(1, 8);
        var key = BinaryPrimitives.ReadUInt64LittleEndian(sessionIdSpan);

        if (!_sessionIdCache.TryGetValue(key, out sessionId!))
        {
            sessionId = Encoding.ASCII.GetString(sessionIdSpan);
            _sessionIdCache.TryAdd(key, sessionId);
        }

        payload = data.Slice(HeaderSize);
        return true;
    }

    /// <summary>
    /// Parses dimensions from an output frame payload.
    /// Output frame payload starts with [cols:2][rows:2][data].
    /// </summary>
    public static ulong ParseOutputSequenceEnd(ReadOnlySpan<byte> payload)
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

    /// <summary>
    /// Gets the data portion of an output frame payload (skipping the 4-byte dimension header).
    /// </summary>
    public static ReadOnlySpan<byte> GetOutputData(ReadOnlySpan<byte> payload)
    {
        return payload.Length >= 12 ? payload.Slice(12) : payload;
    }

    public static (int cols, int rows) ParseResizePayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
        {
            return (80, 24);
        }
        var cols = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(0, 2));
        var rows = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2));
        return (cols, rows);
    }

    public static byte[] CreateResizePayload(int cols, int rows)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), (ushort)cols);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), (ushort)rows);
        return payload;
    }

    /// <summary>
    /// Creates a foreground change frame.
    /// Format: [type:1][sessionId:8][json-payload]
    /// </summary>
    public static byte[] CreateForegroundChangeFrame(string sessionId, byte[] jsonPayload)
    {
        var frame = new byte[HeaderSize + jsonPayload.Length];
        frame[0] = TypeForegroundChange;
        WriteSessionId(frame.AsSpan(1, 8), sessionId);
        jsonPayload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>
    /// Creates a data loss notification frame.
    /// Format: [type:1][sessionId:8][droppedBytes:4]
    /// Client should request buffer refresh when receiving this.
    /// </summary>
    public static byte[] CreateDataLossFrame(string sessionId, int droppedBytes, TerminalReplayReason reason)
    {
        var frame = new byte[HeaderSize + 5];
        frame[0] = TypeDataLoss;
        WriteSessionId(frame.AsSpan(1, 8), sessionId);
        frame[HeaderSize] = (byte)reason;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(HeaderSize + 1, 4), droppedBytes);
        return frame;
    }

    public static (TerminalReplayReason reason, int droppedBytes) ParseDataLossPayload(ReadOnlySpan<byte> payload)
    {
        var reason = payload.Length > 0
            ? (TerminalReplayReason)payload[0]
            : TerminalReplayReason.Manual;
        var droppedBytes = payload.Length >= 5
            ? BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(1, 4))
            : 0;
        return (reason, droppedBytes);
    }

    public static byte[] CreateSyncCompleteFrame()
    {
        var frame = new byte[HeaderSize];
        frame[0] = TypeSyncComplete;
        return frame;
    }

    public static byte[] CreateBufferRequestFrame(string sessionId, bool quickResume, int? replayRows = null)
    {
        var includeReplayRows = replayRows is > 0;
        var frame = new byte[HeaderSize + (includeReplayRows ? 3 : 1)];
        frame[0] = TypeBufferRequest;
        WriteSessionId(frame.AsSpan(1, 8), sessionId);
        frame[HeaderSize] = quickResume ? BufferRequestModeQuickResume : BufferRequestModeFullReplay;
        if (includeReplayRows)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                frame.AsSpan(HeaderSize + 1, 2),
                (ushort)Math.Clamp(replayRows!.Value, 1, ushort.MaxValue));
        }
        return frame;
    }

    public static bool ParseBufferRequestQuickResume(ReadOnlySpan<byte> payload)
    {
        return payload.Length > 0 && payload[0] == BufferRequestModeQuickResume;
    }

    public static BufferRequestOptions ParseBufferRequestOptions(ReadOnlySpan<byte> payload)
    {
        var quickResume = ParseBufferRequestQuickResume(payload);
        int? replayRows = null;

        if (payload.Length >= 3)
        {
            var rows = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(1, 2));
            if (rows > 0)
            {
                replayRows = rows;
            }
        }

        return new BufferRequestOptions(quickResume, replayRows);
    }

    public static byte[] CreateVisibleSessionsHintFrame(IReadOnlyCollection<string> sessionIds)
    {
        var frame = new byte[HeaderSize + (sessionIds.Count * 8)];
        frame[0] = TypeVisibleSessionsHint;
        var offset = HeaderSize;
        foreach (var sessionId in sessionIds)
        {
            WriteSessionId(frame.AsSpan(offset, 8), sessionId);
            offset += 8;
        }

        return frame;
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

    public static byte[] CreateInputTraceResultFrame(string sessionId, MuxInputTraceResult result)
    {
        var frame = new byte[HeaderSize + InputTraceResultPayloadSize];
        frame[0] = TypeInputTraceResult;
        WriteSessionId(frame.AsSpan(1, 8), sessionId);

        var payload = frame.AsSpan(HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[..4], result.TraceId);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.Slice(4, 8), result.FirstOutputSequenceEndExclusive);
        WriteInt32(payload, 12, result.ServerReceiveToIpcStartMs);
        WriteInt32(payload, 16, result.IpcWriteMs);
        WriteInt32(payload, 20, result.ServerReceiveToMthostReceiveMs);
        WriteInt32(payload, 24, result.ServerReceiveToPtyWriteDoneMs);
        WriteInt32(payload, 28, result.PtyWriteDoneToPtyOutputReadMs);
        WriteInt32(payload, 32, result.PtyOutputReadToMthostIpcEnqueuedMs);
        WriteInt32(payload, 36, result.MthostIpcEnqueuedToWriteDoneMs);
        WriteInt32(payload, 40, result.MthostIpcWriteDoneToFlushDoneMs);
        WriteInt32(payload, 44, result.MthostIpcEnqueuedToServerOutputObservedMs);
        WriteInt32(payload, 48, result.ServerReceiveToOutputObservedMs);
        WriteInt32(payload, 52, result.OutputObservedToMuxQueuedMs);
        WriteInt32(payload, 56, result.MuxQueuedToClientQueuedMs);
        WriteInt32(payload, 60, result.ClientQueuedToWsFlushMs);
        WriteInt32(payload, 64, result.ServerReceiveToWsFlushMs);
        return frame;
    }

    private static void WriteInt32(Span<byte> payload, int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(payload.Slice(offset, 4), value);
    }

    public static HashSet<string> ParseVisibleSessionsHintPayload(ReadOnlySpan<byte> payload)
    {
        var sessionIds = new HashSet<string>(StringComparer.Ordinal);
        for (var offset = 0; offset + 8 <= payload.Length; offset += 8)
        {
            var sessionId = ReadSessionId(payload.Slice(offset, 8));
            if (!string.IsNullOrEmpty(sessionId))
            {
                sessionIds.Add(sessionId);
            }
        }

        return sessionIds;
    }

    public static void ClearSessionCache(string sessionId)
    {
        Span<byte> bytes = stackalloc byte[8];
        WriteSessionId(bytes, sessionId);
        var key = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        _sessionIdCache.TryRemove(key, out _);
    }

    internal static void WriteSessionId(Span<byte> dest, string sessionId)
    {
        for (var i = 0; i < 8 && i < sessionId.Length; i++)
        {
            dest[i] = (byte)sessionId[i];
        }
    }

    private static string ReadSessionId(ReadOnlySpan<byte> src)
    {
        Span<byte> sessionIdBytes = stackalloc byte[8];
        src[..Math.Min(8, src.Length)].CopyTo(sessionIdBytes);
        var length = sessionIdBytes.IndexOf((byte)0);
        if (length < 0)
        {
            length = sessionIdBytes.Length;
        }

        return Encoding.ASCII.GetString(sessionIdBytes[..length]);
    }
}

public readonly record struct MuxInputTraceResult(
    uint TraceId,
    ulong FirstOutputSequenceEndExclusive,
    int ServerReceiveToIpcStartMs,
    int IpcWriteMs,
    int ServerReceiveToMthostReceiveMs,
    int ServerReceiveToPtyWriteDoneMs,
    int PtyWriteDoneToPtyOutputReadMs,
    int PtyOutputReadToMthostIpcEnqueuedMs,
    int MthostIpcEnqueuedToWriteDoneMs,
    int MthostIpcWriteDoneToFlushDoneMs,
    int MthostIpcEnqueuedToServerOutputObservedMs,
    int ServerReceiveToOutputObservedMs,
    int OutputObservedToMuxQueuedMs,
    int MuxQueuedToClientQueuedMs,
    int ClientQueuedToWsFlushMs,
    int ServerReceiveToWsFlushMs);
