using System.Buffers;

namespace Ai.Tlbx.MidTerm.TtyHost;

/// <summary>
/// Fixed-size circular buffer for terminal scrollback. Retained and capped-tail
/// replay starts are aligned to ANSI/UTF-8 parser boundaries without per-write allocations.
/// </summary>
public sealed class CircularByteBuffer : IDisposable
{
    private enum TerminalParseState : byte
    {
        Ground,
        Escape,
        EscapeIntermediate,
        Csi,
        Osc,
        ControlString,
        OscEscape,
        ControlStringEscape
    }

    private readonly byte[] _buffer;
    private readonly int _capacity;
    private bool _disposed;
    private int _head;  // next write position
    private int _tail;  // oldest data position
    private int _count; // bytes currently stored
    private ulong _totalBytesWritten;
    private TerminalBoundaryScanner _discardScanner;
    private bool _discardUntilSafeBoundary;

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

        var originalLength = data.Length;
        if (_discardUntilSafeBoundary)
        {
            var skipped = ScanUntilSafeBoundary(data, ref _discardScanner);
            data = data[skipped..];
            if (!_discardScanner.IsSafeBoundary)
            {
                _totalBytesWritten += (ulong)originalLength;
                return;
            }

            _discardUntilSafeBoundary = false;
            _discardScanner = default;
        }

        var overflow = (_count + data.Length) - _capacity;
        if (overflow > 0)
        {
            var scanner = new TerminalBoundaryScanner();
            var discard = FindSafeDiscardLength(data, overflow, ref scanner);
            if (!scanner.IsSafeBoundary)
            {
                _head = 0;
                _tail = 0;
                _count = 0;
                _discardScanner = scanner;
                _discardUntilSafeBoundary = true;
                _totalBytesWritten += (ulong)originalLength;
                return;
            }

            var bufferedDiscard = Math.Min(discard, _count);
            _tail = (_tail + bufferedDiscard) % _capacity;
            _count -= bufferedDiscard;
            data = data[(discard - bufferedDiscard)..];
        }

        var capacity = _capacity;
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
        _totalBytesWritten += (ulong)originalLength;
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
        _discardScanner = default;
        _discardUntilSafeBoundary = false;
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
        if (bytesToCopy == 0)
        {
            sequenceStart = _totalBytesWritten;
            return 0;
        }

        var requestedOffset = _count - bytesToCopy;
        var logicalOffset = FindSafeLogicalOffset(requestedOffset);
        bytesToCopy = _count - logicalOffset;
        sequenceStart = TailPosition + (ulong)logicalOffset;
        if (bytesToCopy == 0)
        {
            return 0;
        }

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

    private int FindSafeDiscardLength(
        ReadOnlySpan<byte> incoming,
        int minimumDiscard,
        ref TerminalBoundaryScanner scanner)
    {
        var combinedLength = _count + incoming.Length;
        var offset = 0;
        while (offset < combinedLength)
        {
            ReadOnlySpan<byte> segment;
            if (offset < _count)
            {
                var physical = (_tail + offset) % _capacity;
                segment = _buffer.AsSpan(physical, Math.Min(_count - offset, _capacity - physical));
            }
            else
            {
                segment = incoming[(offset - _count)..];
            }
            offset += scanner.Scan(segment, Math.Max(0, minimumDiscard - offset));
            if (offset >= minimumDiscard && scanner.IsSafeBoundary)
            {
                return offset;
            }
        }
        return combinedLength;
    }

    private int FindSafeLogicalOffset(int requestedOffset)
    {
        if (requestedOffset <= 0) return 0;
        var scanner = new TerminalBoundaryScanner();
        return FindSafeDiscardLength([], requestedOffset, ref scanner);
    }

    private static int ScanUntilSafeBoundary(ReadOnlySpan<byte> data, ref TerminalBoundaryScanner scanner) =>
        scanner.Scan(data, 1);

    private struct TerminalBoundaryScanner
    {
        private TerminalParseState _state;
        private byte _utf8ContinuationBytes;

        public readonly bool IsSafeBoundary =>
            _state == TerminalParseState.Ground && _utf8ContinuationBytes == 0;

        public int Scan(ReadOnlySpan<byte> data, int minimum)
        {
            var offset = 0;
            if (minimum == 0 && IsSafeBoundary) return 0;
            while (offset < data.Length)
            {
                // Printable ASCII cannot change either parser. Scan contiguous
                // runs with the runtime's SIMD search, retaining scalar handling
                // for ANSI controls, UTF-8 and fragments crossing ring segments.
                if (IsSafeBoundary && offset < minimum)
                {
                    var length = Math.Min(minimum - offset, data.Length - offset);
                    var special = data.Slice(offset, length).IndexOfAnyExceptInRange((byte)0x20, (byte)0x7e);
                    offset += special < 0 ? length : special;
                    if (offset >= minimum || offset == data.Length) return offset;
                }
                Consume(data[offset++]);
                if (offset >= minimum && IsSafeBoundary) return offset;
            }
            return offset;
        }

        public void Consume(byte value)
        {
            if (_state == TerminalParseState.Ground && ConsumeUtf8(value))
            {
                return;
            }

            if (value is 0x18 or 0x1a)
            {
                _state = TerminalParseState.Ground;
                _utf8ContinuationBytes = 0;
                return;
            }

            switch (_state)
            {
                case TerminalParseState.Ground:
                    _state = value switch
                    {
                        0x1b => TerminalParseState.Escape,
                        0x9b => TerminalParseState.Csi,
                        0x9d => TerminalParseState.Osc,
                        0x90 or 0x98 or 0x9e or 0x9f => TerminalParseState.ControlString,
                        _ => TerminalParseState.Ground
                    };
                    break;

                case TerminalParseState.Escape:
                    _state = value switch
                    {
                        0x1b => TerminalParseState.Escape,
                        >= 0x20 and <= 0x2f => TerminalParseState.EscapeIntermediate,
                        (byte)'[' => TerminalParseState.Csi,
                        (byte)']' => TerminalParseState.Osc,
                        (byte)'P' or (byte)'X' or (byte)'^' or (byte)'_' => TerminalParseState.ControlString,
                        _ => TerminalParseState.Ground
                    };
                    break;

                case TerminalParseState.EscapeIntermediate:
                    if (value == 0x1b)
                    {
                        _state = TerminalParseState.Escape;
                    }
                    else if (value is >= 0x30 and <= 0x7e)
                    {
                        _state = TerminalParseState.Ground;
                    }
                    break;

                case TerminalParseState.Csi:
                    if (value == 0x1b)
                    {
                        _state = TerminalParseState.Escape;
                    }
                    else if (value is >= 0x40 and <= 0x7e)
                    {
                        _state = TerminalParseState.Ground;
                    }
                    break;

                case TerminalParseState.Osc:
                    if (value == 0x07 || value == 0x9c)
                    {
                        _state = TerminalParseState.Ground;
                    }
                    else if (value == 0x1b)
                    {
                        _state = TerminalParseState.OscEscape;
                    }
                    break;

                case TerminalParseState.ControlString:
                    if (value == 0x9c)
                    {
                        _state = TerminalParseState.Ground;
                    }
                    else if (value == 0x1b)
                    {
                        _state = TerminalParseState.ControlStringEscape;
                    }
                    break;

                case TerminalParseState.OscEscape:
                    _state = value == (byte)'\\'
                        ? TerminalParseState.Ground
                        : value == 0x1b
                            ? TerminalParseState.OscEscape
                            : TerminalParseState.Osc;
                    break;

                case TerminalParseState.ControlStringEscape:
                    _state = value == (byte)'\\'
                        ? TerminalParseState.Ground
                        : value == 0x1b
                            ? TerminalParseState.ControlStringEscape
                            : TerminalParseState.ControlString;
                    break;
            }
        }

        private bool ConsumeUtf8(byte value)
        {
            if (_utf8ContinuationBytes > 0)
            {
                if ((value & 0xc0) == 0x80)
                {
                    _utf8ContinuationBytes--;
                    return true;
                }

                _utf8ContinuationBytes = 0;
            }

            _utf8ContinuationBytes = value switch
            {
                >= 0xc2 and <= 0xdf => 1,
                >= 0xe0 and <= 0xef => 2,
                >= 0xf0 and <= 0xf4 => 3,
                _ => 0
            };
            return _utf8ContinuationBytes > 0;
        }
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
