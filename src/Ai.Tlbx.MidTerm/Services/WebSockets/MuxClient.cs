using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.WebSockets;

/// <summary>
/// Reference-counted pooled buffer shared across mux clients to avoid per-client copies.
/// </summary>
internal sealed class SharedOutputBuffer
{
    private byte[] _buffer;
    private int _length;
    private int _refCount;

    private SharedOutputBuffer(byte[] buffer, int length)
    {
        _buffer = buffer;
        _length = length;
        _refCount = 1;
    }

    public int Length => _length;
    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, _length);
    public Memory<byte> Memory => _buffer.AsMemory(0, _length);
    public Span<byte> WriteSpan => _buffer.AsSpan(0, _length);

    public static SharedOutputBuffer Rent(int length)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        return new SharedOutputBuffer(buffer, length);
    }

    public void AddRef()
    {
        Interlocked.Increment(ref _refCount);
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            var buffer = _buffer;
            _buffer = Array.Empty<byte>();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>
/// WebSocket client with per-session output buffering.
/// Active session gets immediate delivery; background sessions batch for efficiency.
/// Uses ArrayPool for zero-allocation buffering.
/// </summary>
public sealed class MuxClient : IAsyncDisposable
{
    private const int FlushThresholdBytes = MuxProtocol.CompressionThreshold;
    private const int MaxBufferBytesPerSession = 256 * 1024; // 256KB per session
    private const int MaxQueuedItems = 1000;
    private const int MaxFrameChunkBytes = 32 * 1024;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(15);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Channel<OutputItem> _inputChannel;
    private readonly Dictionary<string, SessionBuffer> _sessionBuffers = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _sessionsToRemove = new();
    private readonly ConcurrentQueue<string> _droppedSessions = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processor;

    private CancellationTokenSource? _loopTimeoutCts;
    private CancellationTokenSource? _sendTimeoutCts;
    private CancellationTokenRegistration _loopCtReg;
    private static readonly Action<object?> s_cancelCallback = static state =>
        ((CancellationTokenSource?)state)?.Cancel();

    private volatile string? _activeSessionId;
    private volatile bool _flushSuspended;
    private readonly ConcurrentDictionary<string, int> _lastFlushDelayMs = new(StringComparer.Ordinal);
    private readonly string? _allowedSessionId;
    private readonly Func<TerminalResumeModeSetting> _getResumeMode;
    private readonly Action<string, string, ulong, long>? _outputFrameSent;
    private FrozenSet<string> _visibleSessionIds = FrozenSet.ToFrozenSet<string>([], StringComparer.Ordinal);

    public string Id { get; }
    public WebSocket WebSocket { get; }

    private readonly record struct OutputItem(
        string SessionId,
        ulong SequenceEndExclusive,
        int Cols,
        int Rows,
        SharedOutputBuffer Buffer);

    /// <summary>
    /// Pooled contiguous buffer for session output. Uses ArrayPool to avoid GC pressure.
    /// </summary>
    private sealed class SessionBuffer : IDisposable
    {
        private byte[] _buffer;
        private int _start;
        private int _end;
        private bool _disposed;

        public int TotalBytes => _end - _start;
        public int LastCols { get; set; }
        public int LastRows { get; set; }
        public ulong LastSequenceEndExclusive { get; set; }
        public long LastFlushTicks { get; set; } = Stopwatch.GetTimestamp();
        public long QueuedAtTicks { get; set; }
        public int DroppedBytes { get; set; }

        public SessionBuffer()
        {
            _buffer = ArrayPool<byte>.Shared.Rent(MaxBufferBytesPerSession);
        }

        public void Write(ReadOnlySpan<byte> data)
        {
            if (_disposed) return;

            if (data.Length > _buffer.Length)
            {
                DroppedBytes += data.Length - _buffer.Length;
                data = data.Slice(data.Length - _buffer.Length);
            }

            EnsureWritableCapacity(data.Length);

            if (_end + data.Length > _buffer.Length)
            {
                var overflow = _end + data.Length - _buffer.Length;
                ConsumePrefix(overflow);

                if (_end + data.Length > _buffer.Length)
                {
                    CompactToStart();
                }
            }

            data.CopyTo(_buffer.AsSpan(_end));
            _end += data.Length;
        }

        public ReadOnlyMemory<byte> GetData() => _buffer.AsMemory(_start, TotalBytes);

        public void Consume(int count)
        {
            if (count <= 0 || _disposed)
            {
                return;
            }

            if (count >= TotalBytes)
            {
                Reset();
                return;
            }

            _start += count;

            if (_start >= _buffer.Length / 2)
            {
                CompactToStart();
            }
        }

        public void Reset()
        {
            _start = 0;
            _end = 0;
        }

        private void EnsureWritableCapacity(int incomingBytes)
        {
            if (_end + incomingBytes <= _buffer.Length)
            {
                return;
            }

            if (_start > 0)
            {
                CompactToStart();
            }
        }

        private void ConsumePrefix(int bytesToDrop)
        {
            if (bytesToDrop <= 0)
            {
                return;
            }

            var dropped = Math.Min(bytesToDrop, TotalBytes);
            if (dropped > 0)
            {
                DroppedBytes += dropped;
                _start += dropped;
            }

            if (_start == _end)
            {
                Reset();
            }
        }

        private void CompactToStart()
        {
            var totalBytes = TotalBytes;
            if (totalBytes > 0 && _start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, totalBytes);
            }

            _start = 0;
            _end = totalBytes;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = null!;
            }
        }
    }

    public MuxClient(
        string id,
        WebSocket webSocket,
        Func<TerminalResumeModeSetting> getResumeMode,
        string? allowedSessionId = null,
        Action<string, string, ulong, long>? outputFrameSent = null)
    {
        Id = id;
        WebSocket = webSocket;
        _getResumeMode = getResumeMode;
        _allowedSessionId = allowedSessionId;
        _outputFrameSent = outputFrameSent;
        _inputChannel = Channel.CreateBounded<OutputItem>(new BoundedChannelOptions(MaxQueuedItems)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        _processor = ProcessLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Queue raw terminal output for buffered delivery.
    /// Copies data into a pooled buffer owned by this client.
    /// </summary>
    internal bool QueueOutput(string sessionId, ulong sequenceEndExclusive, int cols, int rows, SharedOutputBuffer buffer)
    {
        if (_cts.IsCancellationRequested)
        {
            buffer.Release();
            return false;
        }
        if (!CanAccessSession(sessionId))
        {
            buffer.Release();
            return false;
        }
        if (WebSocket.State != WebSocketState.Open)
        {
            buffer.Release();
            return false;
        }
        if (!ShouldDeliverSession(sessionId))
        {
            buffer.Release();
            return false;
        }

        if (!_inputChannel.Writer.TryWrite(new OutputItem(sessionId, sequenceEndExclusive, cols, rows, buffer)))
        {
            _droppedSessions.Enqueue(sessionId);
            Log.Verbose(() => $"[MuxClient] {Id}: Input queue full, dropped frame for {sessionId}");
            buffer.Release();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Set the active session for priority delivery.
    /// </summary>
    public void SetActiveSession(string? sessionId)
    {
        _activeSessionId = sessionId is not null && CanAccessSession(sessionId) ? sessionId : null;
    }

    public void SetVisibleSessions(HashSet<string> sessionIds)
    {
        if (sessionIds.Count == 0)
        {
            _visibleSessionIds = FrozenSet.ToFrozenSet<string>([], StringComparer.Ordinal);
            return;
        }

        var visibleSessions = sessionIds
            .Where(CanAccessSession)
            .ToArray();
        _visibleSessionIds = visibleSessions.Length == 0
            ? FrozenSet.ToFrozenSet<string>([], StringComparer.Ordinal)
            : FrozenSet.ToFrozenSet(visibleSessions, StringComparer.Ordinal);
    }

    public int GetFlushDelay(string sessionId)
    {
        return _lastFlushDelayMs.TryGetValue(sessionId, out var delay) ? delay : -1;
    }

    /// <summary>
    /// Suspend flushing — ProcessLoop continues draining into buffers but won't send.
    /// Used during buffer replay to prevent live output from interleaving with replay frames.
    /// </summary>
    public void SuspendFlush()
    {
        _flushSuspended = true;
    }

    /// <summary>
    /// Resume flushing — next ProcessLoop iteration will flush all accumulated data.
    /// </summary>
    public void ResumeFlush()
    {
        _flushSuspended = false;
    }

    /// <summary>
    /// Drain session IDs that had frames dropped. Returns null if none.
    /// </summary>
    public HashSet<string>? DrainDroppedSessions()
    {
        if (_droppedSessions.IsEmpty) return null;
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (_droppedSessions.TryDequeue(out var sessionId))
        {
            result.Add(sessionId);
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Queue session buffer removal (thread-safe, processed by loop).
    /// </summary>
    public void RemoveSession(string sessionId)
    {
        _lastFlushDelayMs.TryRemove(sessionId, out _);
        _sessionsToRemove.Enqueue(sessionId);
    }

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        var reader = _inputChannel.Reader;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 1. Process pending session removals (dispose buffers to return to pool)
                while (_sessionsToRemove.TryDequeue(out var sessionId))
                {
                    if (_sessionBuffers.Remove(sessionId, out var buffer))
                    {
                        buffer.Dispose();
                    }
                }

                // 2. Drain all immediately available items into buffers
                while (reader.TryRead(out var item))
                {
                    BufferOutput(item);
                }

                // 3. Flush what's due (active immediately, background if threshold/time)
                var now = Stopwatch.GetTimestamp();
                await FlushDueBuffersAsync(now).ConfigureAwait(false);

                // 4. Wait for more data OR the next due background flush.
                try
                {
                    var waitDelay = CalculateNextFlushDelay(now);
                    if (waitDelay is null)
                    {
                        await reader.WaitToReadAsync(ct).ConfigureAwait(false);
                    }
                    else
                    {
                        if (_loopTimeoutCts is null || !_loopTimeoutCts.TryReset())
                        {
                            _loopCtReg.Dispose();
                            _loopTimeoutCts?.Dispose();
                            _loopTimeoutCts = new CancellationTokenSource();
                            _loopCtReg = ct.UnsafeRegister(s_cancelCallback, _loopTimeoutCts);
                        }
                        _loopTimeoutCts.CancelAfter(waitDelay.Value);
                        await reader.WaitToReadAsync(_loopTimeoutCts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Due background flush - loop around and flush.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Log.Exception(ex, $"MuxClient.ProcessLoop({Id})");
        }
    }

    private void BufferOutput(OutputItem item)
    {
        try
        {
            if (!ShouldDeliverSession(item.SessionId))
            {
                return;
            }

            var buffer = GetOrCreateSessionBuffer(item.SessionId);

            if (buffer.TotalBytes == 0)
            {
                buffer.QueuedAtTicks = Stopwatch.GetTimestamp();
            }
            buffer.Write(item.Buffer.Span);
            buffer.LastCols = item.Cols;
            buffer.LastRows = item.Rows;
            buffer.LastSequenceEndExclusive = item.SequenceEndExclusive;
        }
        finally
        {
            item.Buffer.Release();
        }
    }

    private SessionBuffer GetOrCreateSessionBuffer(string sessionId)
    {
        if (_sessionBuffers.TryGetValue(sessionId, out var existing))
        {
            return existing;
        }

        SessionBuffer? created = new();
        if (_sessionBuffers.TryGetValue(sessionId, out existing))
        {
            created.Dispose();
            return existing;
        }

        _sessionBuffers[sessionId] = created;
        var owned = created;
        created = null;
        return owned!;
    }

    private async Task FlushDueBuffersAsync(long nowTicks)
    {
        if (WebSocket.State != WebSocketState.Open) return;
        if (_flushSuspended) return;

        // Active session first — ensures it gets WebSocket priority ahead of background flushes
        var activeId = _activeSessionId;
        if (activeId is not null
            && _sessionBuffers.TryGetValue(activeId, out var activeBuffer)
            && activeBuffer.TotalBytes > 0)
        {
            var delayMs = (int)Stopwatch.GetElapsedTime(activeBuffer.QueuedAtTicks, nowTicks).TotalMilliseconds;
            _lastFlushDelayMs[activeId] = delayMs;
            if (delayMs > 50)
            {
                Log.Warn(() => string.Create(CultureInfo.InvariantCulture, $"[MuxClient] {Id}: Active session flush delayed {delayMs}ms"));
            }
            await FlushBufferAsync(activeId, activeBuffer, compress: false, flushAllAvailable: true).ConfigureAwait(false);
            activeBuffer.LastFlushTicks = nowTicks;
        }

        // Background sessions: flush if size threshold OR time elapsed
        foreach (var (sessionId, buffer) in _sessionBuffers)
        {
            if (buffer.TotalBytes == 0 || sessionId == activeId) continue;

            var elapsed = Stopwatch.GetElapsedTime(buffer.LastFlushTicks, nowTicks);
            if (buffer.TotalBytes >= FlushThresholdBytes
                || elapsed >= FlushInterval)
            {
                _lastFlushDelayMs[sessionId] = (int)Stopwatch.GetElapsedTime(buffer.QueuedAtTicks, nowTicks).TotalMilliseconds;
                await FlushBufferAsync(sessionId, buffer, compress: true, flushAllAvailable: false).ConfigureAwait(false);
                buffer.LastFlushTicks = nowTicks;
            }
        }
    }

    internal TimeSpan? CalculateNextFlushDelay(long nowTicks)
    {
        if (_flushSuspended)
        {
            return null;
        }

        var activeId = _activeSessionId;
        TimeSpan? nextDelay = null;

        foreach (var (sessionId, buffer) in _sessionBuffers)
        {
            if (buffer.TotalBytes == 0 || sessionId == activeId)
            {
                continue;
            }

            var elapsed = Stopwatch.GetElapsedTime(buffer.LastFlushTicks, nowTicks);
            var remaining = FlushInterval - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            if (nextDelay is null || remaining < nextDelay.Value)
            {
                nextDelay = remaining;
            }
        }

        return nextDelay;
    }

    private async Task FlushBufferAsync(
        string sessionId,
        SessionBuffer buffer,
        bool compress,
        bool flushAllAvailable)
    {
        while (buffer.TotalBytes > 0)
        {
            // If data was dropped, notify client before sending (so client can request resync)
            if (buffer.DroppedBytes > 0)
            {
                var lossFrame = MuxProtocol.CreateDataLossFrame(sessionId, buffer.DroppedBytes, TerminalReplayReason.MuxOverflow);
                await SendFrameAsync(lossFrame).ConfigureAwait(false);
                Log.Warn(() => string.Create(CultureInfo.InvariantCulture, $"[MuxClient] {Id}: Session {sessionId} lost {buffer.DroppedBytes} bytes (buffer overflow)"));
                buffer.DroppedBytes = 0;
            }

            // Get data directly from pooled buffer (zero-copy until frame creation)
            var totalBytes = buffer.TotalBytes;
            var data = buffer.GetData();
            var sequenceStart = buffer.LastSequenceEndExclusive - (ulong)totalBytes;
            var length = Math.Min(MaxFrameChunkBytes, data.Length);
            var chunk = data.Slice(0, length);
            var sequenceEndExclusive = sequenceStart + (ulong)length;

            var useCompression = compress && length > MuxProtocol.CompressionThreshold;
            var maxFrameSize = useCompression
                ? MuxProtocol.CompressedOutputHeaderSize + length + 100
                : MuxProtocol.OutputHeaderSize + length;

            var frameBuffer = ArrayPool<byte>.Shared.Rent(maxFrameSize);
            try
            {
                var frameLength = useCompression
                    ? MuxProtocol.WriteCompressedOutputFrameInto(
                        sessionId,
                        sequenceEndExclusive,
                        buffer.LastCols,
                        buffer.LastRows,
                        chunk.Span,
                        frameBuffer)
                    : MuxProtocol.WriteOutputFrameInto(
                        sessionId,
                        sequenceEndExclusive,
                        buffer.LastCols,
                        buffer.LastRows,
                        chunk.Span,
                        frameBuffer);

                // Send first, reset after - prevents data loss on send failure.
                await SendFrameAsync(frameBuffer, frameLength).ConfigureAwait(false);
                _outputFrameSent?.Invoke(Id, sessionId, sequenceEndExclusive, Environment.TickCount64);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frameBuffer);
            }

            buffer.Consume(length);

            if (!flushAllAvailable)
            {
                break;
            }
        }
    }

    private async Task SendFrameAsync(byte[] data)
    {
        await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            if (WebSocket.State == WebSocketState.Open)
            {
                var token = GetSendTimeoutToken();
                await WebSocket.SendAsync(data, WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Warn(() => $"[MuxClient] {Id}: SendAsync timed out, aborting WebSocket");
            WebSocket.Abort();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendFrameAsync(byte[] data, int length)
    {
        await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            if (WebSocket.State == WebSocketState.Open)
            {
                var token = GetSendTimeoutToken();
                await WebSocket.SendAsync(data.AsMemory(0, length), WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Warn(() => $"[MuxClient] {Id}: SendAsync timed out, aborting WebSocket");
            WebSocket.Abort();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private CancellationToken GetSendTimeoutToken()
    {
        if (_sendTimeoutCts is null || !_sendTimeoutCts.TryReset())
        {
            _sendTimeoutCts?.Dispose();
            _sendTimeoutCts = new CancellationTokenSource();
        }
        _sendTimeoutCts.CancelAfter(SendTimeout);
        return _sendTimeoutCts.Token;
    }

    /// <summary>
    /// Queue a pre-built frame to be sent immediately (fire-and-forget).
    /// Used for process events and foreground changes.
    /// </summary>
    public void QueueFrame(byte[] frame, string? sessionId = null)
    {
        if (_cts.IsCancellationRequested) return;
        if (WebSocket.State != WebSocketState.Open) return;
        if (sessionId is not null && !CanAccessSession(sessionId)) return;

        _ = SendFrameAsync(frame);
    }

    private bool CanAccessSession(string sessionId)
    {
        return _allowedSessionId is null || string.Equals(_allowedSessionId, sessionId, StringComparison.Ordinal);
    }

    public bool ShouldDeliverSession(string sessionId)
    {
        return CanAccessSession(sessionId);
    }

    public bool ShouldUseQuickResume()
    {
        return _getResumeMode() == TerminalResumeModeSetting.QuickResume;
    }

    /// <summary>
    /// Send a frame directly (bypassing buffering) - used for init/sync frames.
    /// </summary>
    public async Task<bool> TrySendAsync(byte[] data)
    {
        if (WebSocket.State != WebSocketState.Open) return false;

        await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            if (WebSocket.State == WebSocketState.Open)
            {
                var token = GetSendTimeoutToken();
                await WebSocket.SendAsync(data, WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
                return true;
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            Log.Warn(() => $"[MuxClient] {Id}: TrySendAsync timed out, aborting WebSocket");
            WebSocket.Abort();
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(() => $"[MuxClient] {Id}: TrySend failed: {ex.Message}");
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Send a frame directly (bypassing buffering) - used for init/sync frames with pooled buffers.
    /// </summary>
    public async Task<bool> TrySendAsync(byte[] data, int length)
    {
        if (WebSocket.State != WebSocketState.Open) return false;

        await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            if (WebSocket.State == WebSocketState.Open)
            {
                var token = GetSendTimeoutToken();
                await WebSocket.SendAsync(data.AsMemory(0, length), WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
                return true;
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            Log.Warn(() => $"[MuxClient] {Id}: TrySendAsync timed out, aborting WebSocket");
            WebSocket.Abort();
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(() => $"[MuxClient] {Id}: TrySend failed: {ex.Message}");
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _inputChannel.Writer.Complete();

        try
        {
            await _processor.ConfigureAwait(false);
        }
        catch
        {
            // Ignore shutdown errors
        }

        // Return all pooled buffers
        foreach (var buffer in _sessionBuffers.Values)
        {
            buffer.Dispose();
        }
        _sessionBuffers.Clear();

        _loopCtReg.Dispose();
        _loopTimeoutCts?.Dispose();
        _sendTimeoutCts?.Dispose();
        _cts.Dispose();
        _sendLock.Dispose();
    }
}
