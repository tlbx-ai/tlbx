using System.Buffers;

namespace Ai.Tlbx.MidTerm.TtyHost;

/// <summary>
/// Fixed-size circular buffer for terminal scrollback.
/// Single allocation at creation, O(1) trim, no GC pressure during writes.
/// </summary>
public sealed class CircularByteBuffer : IDisposable
{
    private readonly byte[] _buffer;
    private readonly int _capacity;
    private bool _disposed;
    private int _head;  // next write position
    private int _tail;  // oldest data position
    private int _count; // bytes currently stored
    private ulong _totalBytesWritten;

    public int Count => _count;
    public int Capacity => _capacity;
    public ulong TotalBytesWritten => _totalBytesWritten;
    public ulong TailPosition => _totalBytesWritten - (ulong)_count;

    public CircularByteBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive");
        }

        _buffer = ArrayPool<byte>.Shared.Rent(capacity);
        if (_buffer.Length < capacity)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            throw new InvalidOperationException("ArrayPool returned a smaller buffer than requested");
        }

        _capacity = capacity;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;

        var capacity = _capacity;

        // If data >= capacity, only keep last (capacity) bytes
        if (data.Length >= capacity)
        {
            data.Slice(data.Length - capacity).CopyTo(_buffer.AsSpan(0, capacity));
            _head = 0;
            _tail = 0;
            _count = capacity;
            _totalBytesWritten += (ulong)data.Length;
            return;
        }

        // Calculate overflow, advance tail to discard oldest
        var overflow = (_count + data.Length) - capacity;
        if (overflow > 0)
        {
            _tail = (_tail + overflow) % capacity;
            _count -= overflow;
        }

        // Write first chunk (from head to end of buffer or end of data)
        var firstChunk = Math.Min(data.Length, capacity - _head);
        data.Slice(0, firstChunk).CopyTo(_buffer.AsSpan(_head, firstChunk));

        // Write second chunk if wrapped
        var secondChunk = data.Length - firstChunk;
        if (secondChunk > 0)
        {
            data.Slice(firstChunk).CopyTo(_buffer.AsSpan(0, secondChunk));
        }

        _head = (_head + data.Length) % capacity;
        _count += data.Length;
        _totalBytesWritten += (ulong)data.Length;
    }

    public byte[] ToArray()
    {
        var result = new byte[_count];
        if (_count == 0) return result;

        if (_tail < _head)
        {
            // Contiguous: [....TAIL####HEAD....]
            Array.Copy(_buffer, _tail, result, 0, _count);
        }
        else
        {
            // Wrapped: [###HEAD.....TAIL####]
            var tailToEnd = _capacity - _tail;
            Array.Copy(_buffer, _tail, result, 0, tailToEnd);
            Array.Copy(_buffer, 0, result, tailToEnd, _head);
        }

        return result;
    }

    public void Clear()
    {
        _head = 0;
        _tail = 0;
        _count = 0;
        _totalBytesWritten = 0;
    }

    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < _count)
        {
            throw new ArgumentException("Destination span too small", nameof(destination));
        }

        if (_count == 0)
        {
            return;
        }

        if (_tail < _head)
        {
            _buffer.AsSpan(_tail, _count).CopyTo(destination);
        }
        else
        {
            var tailToEnd = _capacity - _tail;
            _buffer.AsSpan(_tail, tailToEnd).CopyTo(destination);
            _buffer.AsSpan(0, _head).CopyTo(destination.Slice(tailToEnd));
        }
    }

    public int CopyTailTo(Span<byte> destination, out ulong sequenceStart)
    {
        var bytesToCopy = Math.Min(destination.Length, _count);
        sequenceStart = _totalBytesWritten - (ulong)bytesToCopy;
        if (bytesToCopy == 0)
        {
            return 0;
        }

        var logicalOffset = _count - bytesToCopy;
        var physical = (_tail + logicalOffset) % _capacity;

        if (physical + bytesToCopy <= _capacity)
        {
            _buffer.AsSpan(physical, bytesToCopy).CopyTo(destination);
            return bytesToCopy;
        }

        var firstChunk = _capacity - physical;
        _buffer.AsSpan(physical, firstChunk).CopyTo(destination);
        _buffer.AsSpan(0, bytesToCopy - firstChunk).CopyTo(destination[firstChunk..]);
        return bytesToCopy;
    }

    public bool TryCopySince(ulong position, Span<byte> destination, out int bytesCopied)
    {
        var availableStart = TailPosition;
        if (position < availableStart || position > _totalBytesWritten)
        {
            bytesCopied = 0;
            return false;
        }

        var offset = checked((int)(position - availableStart));
        if (offset >= _count)
        {
            bytesCopied = 0;
            return true;
        }

        var physical = (_tail + offset) % _capacity;
        var contiguous = Math.Min(_count - offset, _capacity - physical);
        var toCopy = Math.Min(contiguous, destination.Length);

        _buffer.AsSpan(physical, toCopy).CopyTo(destination);
        bytesCopied = toCopy;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
    }
}
