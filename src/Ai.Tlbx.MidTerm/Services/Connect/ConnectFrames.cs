using System.Buffers.Binary;

namespace Ai.Tlbx.MidTerm.Services.Connect;

/// <summary>Bounded framing for complete HTTP and WebSocket envelopes over a DataChannel.</summary>
internal sealed class ConnectFrames(TimeProvider? timeProvider = null)
{
    internal const int HeaderSize = 13;
    internal const int ChunkSize = 48 * 1024;
    internal const int MaximumMessageSize = 32 * 1024 * 1024;
    internal const int MaximumBinaryMetadataSize = 64 * 1024;
    internal const byte JsonFrameVersion = 1;
    internal const byte BinaryEnvelopeVersion = 2;
    private const int MaximumBufferedSize = 64 * 1024 * 1024;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<uint, Assembly> _messages = [];
    private int _buffered;
    private int _nextId;

    public byte[]? Receive(ReadOnlySpan<byte> frame) => ReceiveFrame(frame)?.Payload;

    public (byte Version, byte[] Payload)? ReceiveFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < HeaderSize
            || (frame[0] != JsonFrameVersion && frame[0] != BinaryEnvelopeVersion)
            || frame.Length > HeaderSize + ChunkSize)
            throw new InvalidDataException("Invalid Connect frame.");
        var version = frame[0];
        var id = BinaryPrimitives.ReadUInt32LittleEndian(frame[1..]);
        var offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(frame[5..]));
        var total = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(frame[9..]));
        var data = frame[HeaderSize..];
        if (total <= 0 || total > MaximumMessageSize || data.IsEmpty || offset > total - data.Length)
            throw new InvalidDataException("Invalid Connect frame length.");
        var now = _timeProvider.GetUtcNow();
        if (_messages.Values.Any(message => now - message.LastProgressAt > TimeSpan.FromSeconds(30)))
            throw new InvalidDataException("Connect message assembly timed out.");
        if (!_messages.TryGetValue(id, out var assembly))
        {
            if (offset != 0 || _messages.Count >= 8 || _buffered > MaximumBufferedSize - total)
                throw new InvalidDataException("Connect assembly limit exceeded.");
            assembly = new Assembly(version, new byte[total], now);
            _messages.Add(id, assembly);
            _buffered += total;
        }
        if (assembly.Version != version || assembly.Bytes.Length != total || assembly.Offset != offset)
            throw new InvalidDataException("Out-of-order Connect fragment.");
        data.CopyTo(assembly.Bytes.AsSpan(offset));
        assembly.Offset += data.Length;
        assembly.LastProgressAt = now;
        if (assembly.Offset != total) return null;
        _messages.Remove(id);
        _buffered -= total;
        return (assembly.Version, assembly.Bytes);
    }

    public IEnumerable<byte[]> Encode(byte[] message, int chunkSize = ChunkSize, byte version = JsonFrameVersion)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(message.Length, MaximumMessageSize);
        ArgumentOutOfRangeException.ThrowIfZero(message.Length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkSize, ChunkSize);
        if (version != JsonFrameVersion && version != BinaryEnvelopeVersion)
            throw new ArgumentOutOfRangeException(nameof(version), version, "Unsupported Connect frame version.");
        var id = unchecked((uint)Interlocked.Increment(ref _nextId));
        for (var offset = 0; offset < message.Length; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, message.Length - offset);
            var frame = new byte[HeaderSize + count];
            frame[0] = version;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), id);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5), (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(9), (uint)message.Length);
            message.AsSpan(offset, count).CopyTo(frame.AsSpan(HeaderSize));
            yield return frame;
        }
    }

    internal static byte[] PackBinaryEnvelope(byte[] metadataUtf8, ReadOnlySpan<byte> payload)
    {
        if (metadataUtf8.Length is <= 0 or > MaximumBinaryMetadataSize)
            throw new InvalidDataException("Invalid Connect binary envelope metadata length.");
        var total = checked(4 + metadataUtf8.Length + payload.Length);
        if (total > MaximumMessageSize)
            throw new InvalidDataException("Connect binary envelope exceeds the transport limit.");
        var packed = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(packed.AsSpan(0, 4), (uint)metadataUtf8.Length);
        metadataUtf8.CopyTo(packed.AsSpan(4));
        payload.CopyTo(packed.AsSpan(4 + metadataUtf8.Length));
        return packed;
    }

    internal static (ReadOnlyMemory<byte> MetadataUtf8, ReadOnlyMemory<byte> Payload) UnpackBinaryEnvelope(byte[] assembled)
    {
        if (assembled.Length < 4)
            throw new InvalidDataException("Invalid Connect binary envelope.");
        var metaLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(assembled.AsSpan(0, 4)));
        if (metaLength <= 0 || metaLength > MaximumBinaryMetadataSize || metaLength > assembled.Length - 4)
            throw new InvalidDataException("Invalid Connect binary envelope metadata length.");
        return (assembled.AsMemory(4, metaLength), assembled.AsMemory(4 + metaLength));
    }

    private sealed class Assembly(byte version, byte[] bytes, DateTimeOffset created)
    {
        public byte Version { get; } = version;
        public byte[] Bytes { get; } = bytes;
        public DateTimeOffset LastProgressAt { get; set; } = created;
        public int Offset { get; set; }
    }
}
