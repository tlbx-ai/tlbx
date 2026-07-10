using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
    internal bool IsReleased => _buffer.Length == 0;

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
    private const int ActiveFlushMaxChunksPerPass = 8;
    private static readonly TimeSpan ActiveFlushInterval = TimeSpan.FromMilliseconds(12);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(15);
    private static readonly TimeSpan SlowSendDegradedThreshold = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan TransportDegradedDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DegradedLogInterval = TimeSpan.FromSeconds(5);

    private readonly PrioritizedWebSocketWriter _writer;
    private readonly Channel<OutputItem> _inputChannel;
    private readonly Dictionary<string, SessionBuffer> _sessionBuffers = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _sessionsToRemove = new();
    private readonly ConcurrentDictionary<string, PausedSessionOutput> _pausedSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DeferredDataLoss> _deferredDataLoss = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ulong> _lastDeliveredSequences = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _activeRecoveries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RecoveryCounters> _recoveryCounters = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _recoveryGate = new();
    private readonly Task _processor;

    private CancellationTokenSource? _loopTimeoutCts;
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
    private long _transportDegradedUntilMs;
    private long _lastDegradedLogAtMs;
    private int _nextRecoveryGeneration;

    public string Id { get; }
    public WebSocket WebSocket { get; }

    private readonly record struct OutputItem(
        string SessionId,
        ulong SequenceEndExclusive,
        int Cols,
        int Rows,
        SharedOutputBuffer? Buffer)
    {
        public static OutputItem WakeProcessor => new(string.Empty, 0, 0, 0, null);
    }

    internal readonly record struct PausedSessionOutput(
        ulong ResumeSequence,
        ulong SourceSequenceEndExclusive);

    internal readonly record struct RecoveryResult(
        bool Succeeded,
        ulong SourceSequenceEndExclusive,
        int ReplayBytes,
        bool ResetTerminal);

    internal readonly record struct RecoveryTelemetrySnapshot(
        long Requested,
        long Coalesced,
        long Completed,
        long Resets,
        long ReplayBytes,
        long Failed);

    private readonly record struct DeferredDataLoss(
        TerminalReplayReason Reason,
        int DroppedBytes,
        ulong? MissingSequenceStart,
        ulong? MissingSequenceEndExclusive);

    private sealed class RecoveryCounters
    {
        public long Requested;
        public long Coalesced;
        public long Completed;
        public long Resets;
        public long ReplayBytes;
        public long Failed;
    }

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
        _writer = new PrioritizedWebSocketWriter(webSocket, ObserveSendDuration);
        _inputChannel = Channel.CreateBounded<OutputItem>(new BoundedChannelOptions(MaxQueuedItems)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
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
            PauseSessionOutput(sessionId, sequenceEndExclusive, buffer.Length);
            buffer.Release();
            return false;
        }

        if (_pausedSessions.ContainsKey(sessionId) && !_activeRecoveries.ContainsKey(sessionId))
        {
            // Once a session is paused, do not leak newer live frames across the
            // missing range. Visibility/activation starts one cursor recovery.
            PauseSessionOutput(sessionId, sequenceEndExclusive, buffer.Length);
            buffer.Release();
            return false;
        }

        if (!_inputChannel.Writer.TryWrite(new OutputItem(sessionId, sequenceEndExclusive, cols, rows, buffer)))
        {
            var bufferLength = (ulong)buffer.Length;
            var sequenceStart = sequenceEndExclusive >= bufferLength
                ? sequenceEndExclusive - bufferLength
                : 0;
            NotifyDataLoss(
                sessionId,
                TerminalReplayReason.MuxOverflow,
                buffer.Length,
                sequenceStart,
                sequenceEndExclusive);
            MarkTransportDegraded("client output queue full");
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

    internal bool TryGetPausedSession(string sessionId, out PausedSessionOutput paused)
    {
        return _pausedSessions.TryGetValue(sessionId, out paused);
    }

    internal IEnumerable<KeyValuePair<string, PausedSessionOutput>> GetVisiblePausedSessions()
    {
        foreach (var entry in _pausedSessions)
        {
            if (IsActiveSession(entry.Key) || _visibleSessionIds.Contains(entry.Key))
            {
                yield return entry;
            }
        }
    }

    internal void RecordDeliveredSequence(string sessionId, ulong sequenceEndExclusive)
    {
        _lastDeliveredSequences.AddOrUpdate(
            sessionId,
            static (_, candidate) => candidate,
            static (_, current, candidate) => Math.Max(current, candidate),
            sequenceEndExclusive);
    }

    internal void NotifyDataLoss(
        string sessionId,
        TerminalReplayReason reason,
        int droppedBytes,
        ulong? missingSequenceStart,
        ulong? missingSequenceEndExclusive)
    {
        if (!CanAccessSession(sessionId))
        {
            return;
        }

        var resumeSequence = _lastDeliveredSequences.TryGetValue(sessionId, out var delivered)
            ? delivered
            : missingSequenceStart ?? 0;
        var sourceSequenceEndExclusive = missingSequenceEndExclusive ?? resumeSequence;
        bool recoveryOwnsDelivery;
        lock (_recoveryGate)
        {
            _pausedSessions.AddOrUpdate(
                sessionId,
                static (_, initial) => initial,
                static (_, current, latest) => current with
                {
                    SourceSequenceEndExclusive = Math.Max(
                        current.SourceSequenceEndExclusive,
                        latest.SourceSequenceEndExclusive)
                },
                new PausedSessionOutput(resumeSequence, sourceSequenceEndExclusive));
            _deferredDataLoss[sessionId] = new DeferredDataLoss(
                reason,
                droppedBytes,
                missingSequenceStart,
                missingSequenceEndExclusive);
            recoveryOwnsDelivery = _activeRecoveries.ContainsKey(sessionId);
        }

        if (!recoveryOwnsDelivery)
        {
            SendDeferredDataLoss(sessionId);
        }
    }

    private void PauseSessionOutput(string sessionId, ulong sequenceEndExclusive, int byteCount)
    {
        var byteCountAsSequence = (ulong)Math.Max(0, byteCount);
        var sequenceStart = sequenceEndExclusive >= byteCountAsSequence
            ? sequenceEndExclusive - byteCountAsSequence
            : 0;
        var resumeSequence = _lastDeliveredSequences.TryGetValue(sessionId, out var delivered)
            ? delivered
            : sequenceStart;
        lock (_recoveryGate)
        {
            _pausedSessions.AddOrUpdate(
                sessionId,
                static (_, initial) => initial,
                static (_, current, latest) => current with
                {
                    SourceSequenceEndExclusive = Math.Max(
                        current.SourceSequenceEndExclusive,
                        latest.SourceSequenceEndExclusive)
                },
                new PausedSessionOutput(resumeSequence, sequenceEndExclusive));
        }
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
        WakeProcessor();
    }

    /// <summary>
    /// Runs one ordered recovery for a session. Duplicate requests are folded into
    /// the active transaction because live output remains held until its snapshot
    /// boundary has been committed to the socket.
    /// </summary>
    internal async Task<bool> ExecuteRecoveryAsync(
        string sessionId,
        Func<uint, CancellationToken, Task<RecoveryResult>> recoverAsync,
        CancellationToken ct)
    {
        var counters = _recoveryCounters.GetOrAdd(sessionId, static _ => new RecoveryCounters());
        Interlocked.Increment(ref counters.Requested);

        bool ownsRecovery;
        lock (_recoveryGate)
        {
            ownsRecovery = _activeRecoveries.TryAdd(sessionId, 0);
        }
        if (!ownsRecovery)
        {
            Interlocked.Increment(ref counters.Coalesced);
            return false;
        }

        try
        {
            var generation = unchecked((uint)Interlocked.Increment(ref _nextRecoveryGeneration));
            var result = await recoverAsync(generation, ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                Interlocked.Increment(ref counters.Failed);
                return false;
            }

            RecordDeliveredSequence(sessionId, result.SourceSequenceEndExclusive);
            lock (_recoveryGate)
            {
                if (_pausedSessions.TryGetValue(sessionId, out var paused)
                    && paused.SourceSequenceEndExclusive <= result.SourceSequenceEndExclusive)
                {
                    _pausedSessions.TryRemove(sessionId, out _);
                    _deferredDataLoss.TryRemove(sessionId, out _);
                }
            }
            Interlocked.Increment(ref counters.Completed);
            Interlocked.Add(ref counters.ReplayBytes, result.ReplayBytes);
            if (result.ResetTerminal)
            {
                Interlocked.Increment(ref counters.Resets);
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref counters.Failed);
            throw;
        }
        catch
        {
            Interlocked.Increment(ref counters.Failed);
            throw;
        }
        finally
        {
            lock (_recoveryGate)
            {
                _activeRecoveries.TryRemove(sessionId, out _);
            }
            SendDeferredDataLoss(sessionId);
            WakeProcessor();
        }
    }

    internal RecoveryTelemetrySnapshot GetRecoveryTelemetry(string sessionId)
    {
        if (!_recoveryCounters.TryGetValue(sessionId, out var counters))
        {
            return default;
        }

        return new RecoveryTelemetrySnapshot(
            Interlocked.Read(ref counters.Requested),
            Interlocked.Read(ref counters.Coalesced),
            Interlocked.Read(ref counters.Completed),
            Interlocked.Read(ref counters.Resets),
            Interlocked.Read(ref counters.ReplayBytes),
            Interlocked.Read(ref counters.Failed));
    }

    internal IReadOnlyDictionary<string, RecoveryTelemetrySnapshot> GetAllRecoveryTelemetry()
    {
        return _recoveryCounters.ToDictionary(
            static entry => entry.Key,
            entry => GetRecoveryTelemetry(entry.Key),
            StringComparer.Ordinal);
    }

    private void WakeProcessor()
    {
        // A full channel already guarantees that the processor is runnable.
        _inputChannel.Writer.TryWrite(OutputItem.WakeProcessor);
    }

    private void SendDeferredDataLoss(string sessionId)
    {
        if (!_deferredDataLoss.TryRemove(sessionId, out var loss))
        {
            return;
        }

        QueueFrame(
            MuxProtocol.CreateDataLossFrame(
                sessionId,
                loss.DroppedBytes,
                loss.Reason,
                loss.MissingSequenceStart,
                loss.MissingSequenceEndExclusive),
            sessionId);
    }

    /// <summary>
    /// Queue session buffer removal (thread-safe, processed by loop).
    /// </summary>
    public void RemoveSession(string sessionId)
    {
        _lastFlushDelayMs.TryRemove(sessionId, out _);
        _sessionsToRemove.Enqueue(sessionId);
        WakeProcessor();
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
                    _pausedSessions.TryRemove(sessionId, out _);
                    _deferredDataLoss.TryRemove(sessionId, out _);
                    _lastDeliveredSequences.TryRemove(sessionId, out _);
                    _recoveryCounters.TryRemove(sessionId, out _);
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

    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP001:Dispose created", Justification = "GetOrCreateSessionBuffer transfers any created buffer into _sessionBuffers, which disposes it on session removal or MuxClient disposal.")]
    private void BufferOutput(OutputItem item)
    {
        if (item.Buffer is null)
        {
            return;
        }

        try
        {
            var buffer = GetOrCreateSessionBuffer(item.SessionId);
            var itemLength = (ulong)item.Buffer.Length;
            var itemSequenceStart = item.SequenceEndExclusive >= itemLength
                ? item.SequenceEndExclusive - itemLength
                : 0;

            if (buffer.TotalBytes > 0 && itemSequenceStart > buffer.LastSequenceEndExclusive)
            {
                var missingStart = buffer.LastSequenceEndExclusive;
                var missingEnd = itemSequenceStart;
                var missingBytes = (int)Math.Min(int.MaxValue, missingEnd - missingStart);
                PauseSessionOutput(item.SessionId, item.SequenceEndExclusive, item.Buffer.Length);
                buffer.Reset();
                buffer.DroppedBytes = 0;
                NotifyDataLoss(
                    item.SessionId,
                    TerminalReplayReason.MuxOverflow,
                    missingBytes,
                    missingStart,
                    missingEnd);
                return;
            }

            var overlap = buffer.TotalBytes > 0 && itemSequenceStart < buffer.LastSequenceEndExclusive
                ? (int)Math.Min((ulong)item.Buffer.Length, buffer.LastSequenceEndExclusive - itemSequenceStart)
                : 0;
            if (overlap >= item.Buffer.Length)
            {
                return;
            }

            if (buffer.TotalBytes == 0)
            {
                buffer.QueuedAtTicks = Stopwatch.GetTimestamp();
            }
            buffer.Write(item.Buffer.Span[overlap..]);
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

        var created = new SessionBuffer();
        _sessionBuffers[sessionId] = created;
        return created;
    }

    private async Task FlushDueBuffersAsync(long nowTicks)
    {
        if (WebSocket.State != WebSocketState.Open) return;
        if (_flushSuspended) return;

        // Active session first — ensures it gets WebSocket priority ahead of background flushes
        var activeId = _activeSessionId;
        if (activeId is not null
            && !_activeRecoveries.ContainsKey(activeId)
            && _sessionBuffers.TryGetValue(activeId, out var activeBuffer)
            && (activeBuffer.TotalBytes > 0 || activeBuffer.DroppedBytes > 0))
        {
            var queuedDelay = Stopwatch.GetElapsedTime(activeBuffer.QueuedAtTicks, nowTicks);
            if (activeBuffer.DroppedBytes > 0
                || activeBuffer.TotalBytes >= FlushThresholdBytes
                || queuedDelay >= ActiveFlushInterval)
            {
                if (activeBuffer.TotalBytes > 0)
                {
                    var delayMs = (int)queuedDelay.TotalMilliseconds;
                    _lastFlushDelayMs[activeId] = delayMs;
                    if (delayMs > 50)
                    {
                        Log.Warn(() => string.Create(CultureInfo.InvariantCulture, $"[MuxClient] {Id}: Active session flush delayed {delayMs}ms"));
                    }
                }
                await FlushBufferAsync(activeId, activeBuffer, compress: false, flushAllAvailable: true, maxChunks: ActiveFlushMaxChunksPerPass).ConfigureAwait(false);
                activeBuffer.LastFlushTicks = nowTicks;
            }
        }

        // Background sessions: flush if size threshold OR time elapsed
        foreach (var (sessionId, buffer) in _sessionBuffers)
        {
            if ((buffer.TotalBytes == 0 && buffer.DroppedBytes == 0) || sessionId == activeId) continue;
            if (_activeRecoveries.ContainsKey(sessionId)) continue;
            if (IsTransportDegradedAt(Environment.TickCount64) && !ShouldDeliverSession(sessionId))
            {
                PauseSessionOutput(sessionId, buffer.LastSequenceEndExclusive, buffer.TotalBytes);
                buffer.Reset();
                buffer.DroppedBytes = 0;
                buffer.LastFlushTicks = nowTicks;
                continue;
            }

            var elapsed = Stopwatch.GetElapsedTime(buffer.LastFlushTicks, nowTicks);
            if (buffer.DroppedBytes > 0
                || buffer.TotalBytes >= FlushThresholdBytes
                || elapsed >= FlushInterval)
            {
                if (buffer.TotalBytes > 0)
                {
                    _lastFlushDelayMs[sessionId] = (int)Stopwatch.GetElapsedTime(buffer.QueuedAtTicks, nowTicks).TotalMilliseconds;
                }
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
            if (_activeRecoveries.ContainsKey(sessionId))
            {
                continue;
            }

            if (buffer.TotalBytes == 0)
            {
                if (buffer.DroppedBytes > 0 && ShouldDeliverSession(sessionId))
                {
                    return TimeSpan.Zero;
                }
                continue;
            }

            var remaining = string.Equals(sessionId, activeId, StringComparison.Ordinal)
                ? ActiveFlushInterval - Stopwatch.GetElapsedTime(buffer.QueuedAtTicks, nowTicks)
                : FlushInterval - Stopwatch.GetElapsedTime(buffer.LastFlushTicks, nowTicks);
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
        bool flushAllAvailable,
        int maxChunks = int.MaxValue)
    {
        // If data was dropped, notify client before sending (so client can request
        // resync) — even when no new output is pending yet.
        if (buffer.DroppedBytes > 0)
        {
            var bufferedSequenceStart = buffer.LastSequenceEndExclusive - (ulong)buffer.TotalBytes;
            var missingByteCount = (ulong)buffer.DroppedBytes;
            var missingSequenceStart = bufferedSequenceStart >= missingByteCount
                ? bufferedSequenceStart - missingByteCount
                : 0;
            NotifyDataLoss(
                sessionId,
                TerminalReplayReason.MuxOverflow,
                buffer.DroppedBytes,
                missingSequenceStart,
                bufferedSequenceStart);
            Log.Warn(() => string.Create(CultureInfo.InvariantCulture, $"[MuxClient] {Id}: Session {sessionId} lost {buffer.DroppedBytes} bytes before delivery"));
            buffer.DroppedBytes = 0;
            buffer.Reset();
            return;
        }

        var chunksFlushed = 0;
        while (buffer.TotalBytes > 0 && chunksFlushed < maxChunks)
        {
            // Get data directly from pooled buffer (zero-copy until frame creation)
            var totalBytes = buffer.TotalBytes;
            var sequenceStart = buffer.LastSequenceEndExclusive - (ulong)totalBytes;
            if (_lastDeliveredSequences.TryGetValue(sessionId, out var deliveredSequence)
                && deliveredSequence > sequenceStart)
            {
                var duplicateBytes = (int)Math.Min((ulong)totalBytes, deliveredSequence - sequenceStart);
                buffer.Consume(duplicateBytes);
                if (buffer.TotalBytes == 0)
                {
                    break;
                }
                totalBytes = buffer.TotalBytes;
                sequenceStart = buffer.LastSequenceEndExclusive - (ulong)totalBytes;
            }

            var data = buffer.GetData();
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
                if (!await SendFrameAsync(
                        frameBuffer.AsMemory(0, frameLength),
                        GetLiveWritePriority(sessionId)).ConfigureAwait(false))
                {
                    return;
                }
                _outputFrameSent?.Invoke(Id, sessionId, sequenceEndExclusive, Environment.TickCount64);
                RecordDeliveredSequence(sessionId, sequenceEndExclusive);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frameBuffer);
            }

            buffer.Consume(length);
            chunksFlushed++;

            if (!flushAllAvailable)
            {
                break;
            }
        }
    }

    private MuxWritePriority GetLiveWritePriority(string sessionId)
    {
        if (IsActiveSession(sessionId))
        {
            return MuxWritePriority.ActiveLive;
        }

        return _visibleSessionIds.Contains(sessionId)
            ? MuxWritePriority.VisibleLive
            : MuxWritePriority.BackgroundLive;
    }

    private ValueTask<bool> SendFrameAsync(
        ReadOnlyMemory<byte> data,
        MuxWritePriority priority)
    {
        return _writer.SendAsync(data, priority);
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
        _ = SendFrameAsync(frame, MuxWritePriority.Control).AsTask();
    }

    private bool CanAccessSession(string sessionId)
    {
        return _allowedSessionId is null || string.Equals(_allowedSessionId, sessionId, StringComparison.Ordinal);
    }

    public bool ShouldDeliverSession(string sessionId)
    {
        if (!CanAccessSession(sessionId))
        {
            return false;
        }

        if (!IsTransportDegradedAt(Environment.TickCount64))
        {
            return true;
        }

        if (IsActiveSession(sessionId) || _visibleSessionIds.Contains(sessionId))
        {
            return true;
        }

        return _activeSessionId is null && _visibleSessionIds.Count == 0;
    }

    public bool IsActiveSession(string sessionId)
    {
        var activeId = _activeSessionId;
        return activeId is not null && string.Equals(activeId, sessionId, StringComparison.Ordinal);
    }

    public bool ShouldUseQuickResume()
    {
        return _getResumeMode() == TerminalResumeModeSetting.QuickResume;
    }

    /// <summary>
    /// Send a frame directly (bypassing buffering) - used for init/sync frames.
    /// </summary>
    internal async Task<bool> TrySendAsync(
        byte[] data,
        MuxWritePriority priority = MuxWritePriority.Control)
    {
        return await SendFrameAsync(data, priority).ConfigureAwait(false);
    }

    /// <summary>
    /// Send a frame directly (bypassing buffering) - used for init/sync frames with pooled buffers.
    /// </summary>
    internal async Task<bool> TrySendAsync(
        byte[] data,
        int length,
        MuxWritePriority priority = MuxWritePriority.Control)
    {
        return await SendFrameAsync(data.AsMemory(0, length), priority).ConfigureAwait(false);
    }

    internal bool IsTransportDegraded => IsTransportDegradedAt(Environment.TickCount64);

    internal void MarkTransportDegradedForTests()
    {
        MarkTransportDegraded("test");
    }

    private bool IsTransportDegradedAt(long nowMs)
    {
        return Interlocked.Read(ref _transportDegradedUntilMs) > nowMs;
    }

    private void ObserveSendDuration(TimeSpan elapsed, int byteCount)
    {
        if (elapsed < SlowSendDegradedThreshold)
        {
            return;
        }

        MarkTransportDegraded(string.Create(
            CultureInfo.InvariantCulture,
            $"slow websocket send {elapsed.TotalMilliseconds:F0}ms for {byteCount} bytes"));
    }

    private void MarkTransportDegraded(string reason)
    {
        var now = Environment.TickCount64;
        var until = now + (long)TransportDegradedDuration.TotalMilliseconds;
        var currentUntil = Interlocked.Read(ref _transportDegradedUntilMs);
        if (until > currentUntil)
        {
            Interlocked.Exchange(ref _transportDegradedUntilMs, until);
        }

        var lastLog = Interlocked.Read(ref _lastDegradedLogAtMs);
        if (now - lastLog < (long)DegradedLogInterval.TotalMilliseconds)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastDegradedLogAtMs, now, lastLog) == lastLog)
        {
            Log.Warn(() => $"[MuxClient] {Id}: transport degraded ({reason}); suppressing hidden background terminal output");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _inputChannel.Writer.Complete();
        await _writer.DisposeAsync().ConfigureAwait(false);

        try
        {
            await _processor.ConfigureAwait(false);
        }
        catch
        {
            // Ignore shutdown errors
        }

        var reader = _inputChannel.Reader;
        while (reader.TryRead(out var item))
        {
            item.Buffer?.Release();
        }

        // Return all pooled buffers
        foreach (var buffer in _sessionBuffers.Values)
        {
            buffer.Dispose();
        }
        _sessionBuffers.Clear();

        _loopCtReg.Dispose();
        _loopTimeoutCts?.Dispose();
        _cts.Dispose();
    }
}
