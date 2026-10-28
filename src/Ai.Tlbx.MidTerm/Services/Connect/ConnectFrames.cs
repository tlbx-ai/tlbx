using System.Buffers.Binary;

namespace Ai.Tlbx.MidTerm.Services.Connect;

/// <summary>Bounded framing for complete HTTP and WebSocket envelopes over a DataChannel.</summary>
internal sealed class ConnectFrames
{
    internal const int HeaderSize = 13;
    internal const int ChunkSize = 48 * 1024;
    internal const int MaximumMessageSize = 32 * 1024 * 1024;
    private const int MaximumBufferedSize = 64 * 1024 * 1024;
    private readonly Dictionary<uint, Assembly> _messages = [];
    private int _buffered;
    private uint _nextId;

    public byte[]? Receive(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < HeaderSize || frame[0] != 1 || frame.Length > HeaderSize + ChunkSize)
            throw new InvalidDataException("Invalid Connect frame.");
        var id = BinaryPrimitives.ReadUInt32LittleEndian(frame[1..]);
        var offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(frame[5..]));
        var total = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(frame[9..]));
        var data = frame[HeaderSize..];
        if (total <= 0 || total > MaximumMessageSize || data.IsEmpty || offset > total - data.Length)
            throw new InvalidDataException("Invalid Connect frame length.");
        var now = DateTimeOffset.UtcNow;
        if (_messages.Values.Any(message => now - message.Created > TimeSpan.FromSeconds(30)))
            throw new InvalidDataException("Connect message assembly timed out.");
        if (!_messages.TryGetValue(id, out var assembly))
        {
            if (offset != 0 || _messages.Count >= 8 || _buffered > MaximumBufferedSize - total)
                throw new InvalidDataException("Connect assembly limit exceeded.");
            assembly = new Assembly(new byte[total], now);
            _messages.Add(id, assembly);
            _buffered += total;
        }
        if (assembly.Bytes.Length != total || assembly.Offset != offset)
            throw new InvalidDataException("Out-of-order Connect fragment.");
        data.CopyTo(assembly.Bytes.AsSpan(offset));
        assembly.Offset += data.Length;
        if (assembly.Offset != total) return null;
        _messages.Remove(id);
        _buffered -= total;
        return assembly.Bytes;
    }

    public IEnumerable<byte[]> Encode(byte[] message)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(message.Length, MaximumMessageSize);
        ArgumentOutOfRangeException.ThrowIfZero(message.Length);
        var id = unchecked(++_nextId);
        for (var offset = 0; offset < message.Length; offset += ChunkSize)
        {
            var count = Math.Min(ChunkSize, message.Length - offset);
            var frame = new byte[HeaderSize + count];
            frame[0] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), id);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5), (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(9), (uint)message.Length);
            message.AsSpan(offset, count).CopyTo(frame.AsSpan(HeaderSize));
            yield return frame;
        }
    }

    private sealed class Assembly(byte[] bytes, DateTimeOffset created)
    {
        public byte[] Bytes { get; } = bytes;
        public DateTimeOffset Created { get; } = created;
        public int Offset { get; set; }
    }
}
