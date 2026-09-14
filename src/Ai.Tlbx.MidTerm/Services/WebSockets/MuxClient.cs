using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.WebSockets;
using System.Threading;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.WebSockets;

/// <summary>
/// Reference-counted pooled buffer shared across mux clients to avoid per-client copies.
/// </summary>
internal sealed class SharedOutputBuffer
{
    private readonly ArrayPool<byte> _pool;
    private byte[] _buffer;
    private int _length;
    private int _refCount;

    private SharedOutputBuffer(ArrayPool<byte> pool, byte[] buffer, int length)
    {
        _pool = pool;
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
        return Rent(ArrayPool<byte>.Shared, length);
    }

    public static SharedOutputBuffer Rent(ArrayPool<byte> pool, int length)
    {
        var buffer = pool.Rent(length);
        return new SharedOutputBuffer(pool, buffer, length);
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
            _pool.Return(buffer);
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
    private const int ForegroundFlushThresholdBytes = MuxProtocol.CompressionThreshold;
    private const int BackgroundFlushThresholdBytes = 64 * 1024;
    private const int MaxBufferBytesPerSession = 256 * 1024; // 256KB per session
    private const int InitialBufferBytesPerSession = 8 * 1024;
    private const int MaxQueuedItems = 1000;
    private const int MaxQueuedBytes = 4 * 1024 * 1024;
    private const int MaxCoalescedInputBytes = 32 * 1024;
    private const int InputDrainMaxItemsPerPass = 64;
    private const int MaxFrameChunkBytes = 32 * 1024;
    private const int ActiveFlushMaxChunksPerPass = 8;
    private static readonly TimeSpan ActiveFlushInterval = TimeSpan.FromMilliseconds(12);
    private static readonly TimeSpan VisibleFlushInterval = TimeSpan.FromMilliseconds(15);
    private static readonly TimeSpan BackgroundFlushInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SlowSendDegradedThreshold = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan TransportDegradedDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DegradedLogInterval = TimeSpan.FromSeconds(5);

    private readonly PrioritizedWebSocketWriter _writer;
    private readonly BoundedSessionOutputQueue<OutputItem> _inputQueue;
    private readonly Dictionary<string, SessionBuffer> _sessionBuffers = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<SessionRemoval> _sessionsToRemove = new();
    private readonly ConcurrentDictionary<string, PausedSessionOutput> _pausedSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DeferredDataLoss> _deferredDataLoss = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ulong> _lastDeliveredSequences = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RecoveryOperation> _activeRecoveries = new(StringComparer.Ordinal);
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
    private readonly Func<string, bool> _sessionExists;
    private readonly Action<string, string, ulong, long>? _outputFrameSent;
    private FrozenSet<string> _visibleSessionIds = FrozenSet.ToFrozenSet<string>([], StringComparer.Ordinal);
    private FrozenSet<string> _backgroundSessionIds = FrozenSet.ToFrozenSet<string>([], StringComparer.Ordinal);
    private long _transportDegradedUntilMs;
    private long _lastDegradedLogAtMs;
    private int _nextRecoveryGeneration;

    public string Id { get; }
    public WebSocket WebSocket { get; }
    internal bool HasSessionBufferForTests(string sessionId) => _sessionBuffers.ContainsKey(sessionId);

    private readonly record struct OutputItem(
        string SessionId,
        ulong SequenceEndExclusive,
        int Cols,
        int Rows,
        SharedOutputBuffer? Buffer)
    {
        public static OutputItem WakeProcessor => new(string.Empty, 0, 0, 0, null);
    }

    private readonly record struct SessionRemoval(string SessionId, TaskCompletionSource Completion);

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

    private sealed class RecoveryOperation : IDisposable
    {
        private readonly CancellationTokenSource _cts;

        public RecoveryOperation(CancellationToken requestToken, CancellationToken clientToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(requestToken, clientToken);
        }

        public CancellationToken Token => _cts.Token;
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Cancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose() => _cts.Dispose();
    }

    /// <summary>
    /// Adaptive contiguous buffer for one session's pending output.
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
            // This buffer can live for the whole browser/session pairing. Keep
            // it adaptive and GC-owned so closing the session can actually
            // return large buffers instead of pinning them in ArrayPool.Shared.
            _buffer = GC.AllocateUninitializedArray<byte>(InitialBufferBytesPerSession);
        }

        public void Write(ReadOnlySpan<byte> data)
        {
            if (_disposed) return;

            if (data.Length > MaxBufferBytesPerSession)
            {
                DroppedBytes += data.Length - MaxBufferBytesPerSession;
                data = data.Slice(data.Length - MaxBufferBytesPerSession);
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

            if (_end + incomingBytes <= _buffer.Length || _buffer.Length >= MaxBufferBytesPerSession)
            {
                return;
            }

            var required = Math.Min(MaxBufferBytesPerSession, _end + incomingBytes);
            var nextSize = Math.Min(
                MaxBufferBytesPerSession,
                Math.Max(required, _buffer.Length * 2));
            var replacement = GC.AllocateUninitializedArray<byte>(nextSize);
            _buffer.AsSpan(0, _end).CopyTo(replacement);
            _buffer = replacement;
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

            _buffer = Array.Empty<byte>();
        }
    }

    public MuxClient(
        string id,
        WebSocket webSocket,
        Func<TerminalResumeModeSetting> getResumeMode,
        string? allowedSessionId = null,
        Action<string, string, ulong, long>? outputFrameSent = null,
        Func<string, bool>? sessionExists = null,
        bool startFlushSuspended = false)
    {
        Id = id;
        WebSocket = webSocket;
        _getResumeMode = getResumeMode;
        _sessionExists = sessionExists ?? (_ => true);
        _allowedSessionId = allowedSessionId;
        _outputFrameSent = outputFrameSent;
        _writer = new PrioritizedWebSocketWriter(webSocket, ObserveSendDuration);
        _inputQueue = new BoundedSessionOutputQueue<OutputItem>(
            MaxQueuedItems,
            InputDrainMaxItemsPerPass,
            MaxQueuedBytes,
            static item => item.Buffer?.Length ?? 0);
        _flushSuspended = startFlushSuspended;
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
        if (!_sessionExists(sessionId))
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

        var outputItem = new OutputItem(sessionId, sequenceEndExclusive, cols, rows, buffer);
        if (!_inputQueue.TryEnqueueOrMerge(sessionId, outputItem, TryMergeOutputItems))
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

    private static bool TryMergeOutputItems(OutputItem existing, OutputItem incoming, out OutputItem merged)
    {
        merged = default;
        if (existing.Buffer is null || incoming.Buffer is null)
        {
            return false;
        }

        var incomingLength = incoming.Buffer.Length;
        var incomingSequenceStart = incoming.SequenceEndExclusive >= (ulong)incomingLength
            ? incoming.SequenceEndExclusive - (ulong)incomingLength
            : 0;
        var combinedLength = existing.Buffer.Length + incomingLength;
        if (existing.Cols != incoming.Cols
            || existing.Rows != incoming.Rows
            || existing.SequenceEndExclusive != incomingSequenceStart
            || combinedLength > MaxCoalescedInputBytes)
        {
            return false;
        }

        var combined = SharedOutputBuffer.Rent(combinedLength);
        existing.Buffer.Span.CopyTo(combined.WriteSpan);
        incoming.Buffer.Span.CopyTo(combined.WriteSpan[existing.Buffer.Length..]);
        merged = new OutputItem(
            incoming.SessionId,
            incoming.SequenceEndExclusive,
            incoming.Cols,
            incoming.Rows,
            combined);
        existing.Buffer.Release();
        incoming.Buffer.Release();
        return true;
    }

    /// <summary>
    /// Set the active session for priority delivery.
    /// </summary>
    public void SetActiveSession(string? sessionId)
    {
        _activeSessionId = sessionId is not null && CanAccessSession(sessionId) ? sessionId : null;
        WakeProcessor();
    }

    public void SetVisibleSessions(HashSet<string> sessionIds)
    {
        if (sessionIds.Count == 0)
        {
            _visibleSessionIds = FrozenSet.ToFrozenSet<string>([], StringComparer.Ordinal);
            WakeProcessor();
            return;
        }

        var visibleSessions = sessionIds
            .Where(CanAccessSession)
            .ToArray();
        _visibleSessionIds = visibleSessions.Length == 0
            ? FrozenSet.ToFrozenSet<string>([], StringComparer.Ordinal)
            : FrozenSet.ToFrozenSet(visibleSessions, StringComparer.Ordinal);
        WakeProcessor();
    }

    public void SetBackgroundSessions(HashSet<string> sessionIds)
    {
        if (sessionIds.Count == 0)
        {
            _backgroundSessionIds = FrozenSet.ToFrozenSet<string>([], StringComparer.Ordinal);
            WakeProcessor();
            return;
        }

        var backgroundSessions = sessionIds
            .Where(CanAccessSession)
            .ToArray();
        _backgroundSessionIds = backgroundSessions.Length == 0
            ? FrozenSet.ToFrozenSet<string>([], StringComparer.Ordinal)
            : FrozenSet.ToFrozenSet(backgroundSessions, StringComparer.Ordinal);
        WakeProcessor();
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

    internal IEnumerable<KeyValuePair<string, PausedSessionOutput>> GetBackgroundPausedSessions()
    {
        foreach (var entry in _pausedSessions)
        {
            if (_backgroundSessionIds.Contains(entry.Key))
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

        var operation = new RecoveryOperation(ct, _cts.Token);
        bool ownsRecovery;
        lock (_recoveryGate)
        {
            ownsRecovery = _sessionExists(sessionId)
                && _activeRecoveries.TryAdd(sessionId, operation);
        }
        if (!ownsRecovery)
        {
            operation.Dispose();
            Interlocked.Increment(ref counters.Coalesced);
            return false;
        }

        // Close can remove the session between the locked preflight and the
        // dictionary add. Do not let that race start a new replay transaction.
        if (!_sessionExists(sessionId))
        {
            operation.Cancel();
        }

        try
        {
            var generation = unchecked((uint)Interlocked.Increment(ref _nextRecoveryGeneration));
            operation.Token.ThrowIfCancellationRequested();
            var result = await recoverAsync(generation, operation.Token).ConfigureAwait(false);
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
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            Interlocked.Increment(ref counters.Failed);
            if (ct.IsCancellationRequested)
            {
                throw;
            }
            return false;
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
                if (_activeRecoveries.TryGetValue(sessionId, out var current)
                    && ReferenceEquals(current, operation))
                {
                    _activeRecoveries.TryRemove(sessionId, out _);
                }
            }
            operation.Completion.TrySetResult();
            operation.Dispose();
            if (_sessionExists(sessionId))
            {
                SendDeferredDataLoss(sessionId);
            }
            else
            {
                _deferredDataLoss.TryRemove(sessionId, out _);
            }
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
        // A full queue already guarantees that the processor is runnable.
        _inputQueue.TryEnqueue("__control__", OutputItem.WakeProcessor);
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
    public Task RemoveSessionAsync(string sessionId)
    {
        _lastFlushDelayMs.TryRemove(sessionId, out _);
        var recoveryCompletion = Task.CompletedTask;
        if (_activeRecoveries.TryGetValue(sessionId, out var recovery))
        {
            recoveryCompletion = recovery.Completion.Task;
            recovery.Cancel();
        }

        foreach (var item in _inputQueue.RemoveSession(sessionId))
        {
            item.Buffer?.Release();
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sessionsToRemove.Enqueue(new SessionRemoval(sessionId, completion));
        WakeProcessor();
        return Task.WhenAll(completion.Task, recoveryCompletion);
    }

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        var hasAvailableItem = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 1. Process pending session removals (dispose buffers to return to pool)
                while (_sessionsToRemove.TryDequeue(out var removal))
                {
                    var sessionId = removal.SessionId;
                    _pausedSessions.TryRemove(sessionId, out _);
                    _deferredDataLoss.TryRemove(sessionId, out _);
                    _lastDeliveredSequences.TryRemove(sessionId, out _);
                    _recoveryCounters.TryRemove(sessionId, out _);
                    if (_sessionBuffers.Remove(sessionId, out var buffer))
                    {
                        buffer.Dispose();
                    }
                    removal.Completion.TrySetResult();
                }

                // 2. Bound queue work so a continuously replenished background
                // stream cannot indefinitely postpone an active-session flush.
                var drainedItems = 0;
                IReadOnlySet<string> activeSessionIds = _activeSessionId is { } activeSessionId
                    ? new HashSet<string>(StringComparer.Ordinal) { activeSessionId }
                    : FrozenSet.ToFrozenSet<string>([], StringComparer.Ordinal);
                if (hasAvailableItem && _inputQueue.TryDequeue(activeSessionIds, out var firstItem))
                {
                    BufferOutput(firstItem);
                    drainedItems++;
                }
                hasAvailableItem = false;

                while (drainedItems < InputDrainMaxItemsPerPass && _inputQueue.TryAcquireAvailableItem())
                {
                    if (!_inputQueue.TryDequeue(activeSessionIds, out var item))
                    {
                        break;
                    }

                    BufferOutput(item);
                    drainedItems++;
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
                        hasAvailableItem = await _inputQueue.WaitToReadAsync(ct).ConfigureAwait(false);
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
                        hasAvailableItem = await _inputQueue.WaitToReadAsync(_loopTimeoutCts.Token).ConfigureAwait(false);
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
            if (!_sessionExists(item.SessionId))
            {
                return;
            }

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
                || activeBuffer.TotalBytes >= ForegroundFlushThresholdBytes
                || GetActiveFlushDelay(activeBuffer.LastFlushTicks, nowTicks) == TimeSpan.Zero)
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

        // Non-active sessions are split into visible panes and low-frequency
        // hidden terminal ingest. xterm parses hidden batches while its own
        // IntersectionObserver keeps renderer refreshes paused.
        foreach (var (sessionId, buffer) in _sessionBuffers)
        {
            if ((buffer.TotalBytes == 0 && buffer.DroppedBytes == 0) || sessionId == activeId) continue;
            if (_activeRecoveries.ContainsKey(sessionId)) continue;
            if (!ShouldDeliverSession(sessionId))
            {
                PauseSessionOutput(sessionId, buffer.LastSequenceEndExclusive, buffer.TotalBytes);
                buffer.Reset();
                buffer.DroppedBytes = 0;
                buffer.LastFlushTicks = nowTicks;
                continue;
            }

            var isVisible = _visibleSessionIds.Contains(sessionId);
            var flushInterval = isVisible ? VisibleFlushInterval : BackgroundFlushInterval;
            var flushThreshold = isVisible ? ForegroundFlushThresholdBytes : BackgroundFlushThresholdBytes;
            var elapsed = Stopwatch.GetElapsedTime(buffer.LastFlushTicks, nowTicks);
            if (buffer.DroppedBytes > 0
                || buffer.TotalBytes >= flushThreshold
                || elapsed >= flushInterval)
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
                ? GetActiveFlushDelay(buffer.LastFlushTicks, nowTicks)
                : (_visibleSessionIds.Contains(sessionId) ? VisibleFlushInterval : BackgroundFlushInterval)
                    - Stopwatch.GetElapsedTime(buffer.LastFlushTicks, nowTicks);
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

    internal static TimeSpan GetActiveFlushDelay(long lastFlushTicks, long nowTicks)
    {
        // Rate-limit a continuous stream without adding a fresh batching wait
        // to every isolated keystroke echo after the terminal has been idle.
        var remaining = ActiveFlushInterval - Stopwatch.GetElapsedTime(lastFlushTicks, nowTicks);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
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

                // Transfer an owned copy to the socket writer. Waiting for the
                // physical WebSocket send here used to stop this same loop from
                // draining raw PTY frames, so a brief client stall could overflow
                // a queue containing only a few kilobytes of tiny reads.
                Action<bool>? sent = _outputFrameSent is null
                    ? null
                    : succeeded =>
                    {
                        if (succeeded)
                        {
                            _outputFrameSent(Id, sessionId, sequenceEndExclusive, Environment.TickCount64);
                        }
                    };
                if (!_writer.TryQueueCopy(
                        frameBuffer.AsSpan(0, frameLength),
                        GetLiveWritePriority(sessionId),
                        sent,
                        sessionId))
                {
                    MarkTransportDegraded("websocket writer queue full");
                    await Task.Delay(ActiveFlushInterval, _cts.Token).ConfigureAwait(false);
                    return;
                }
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
        string? orderingSession = null;
        if (MuxProtocol.TryParseFrame(data.Span, out var type, out var sessionId, out _) &&
            type is MuxProtocol.TypeTerminalOutput or MuxProtocol.TypeCompressedOutput or
                MuxProtocol.TypeRecoveryBegin or MuxProtocol.TypeRecoveryEnd or
                MuxProtocol.TypeDataLoss or MuxProtocol.TypeForegroundChange or MuxProtocol.TypeInputTraceResult)
        {
            orderingSession = sessionId;
        }
        return _writer.SendAsync(data, priority, orderingSession);
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

        if (IsActiveSession(sessionId) || _visibleSessionIds.Contains(sessionId))
        {
            return true;
        }

        if (_backgroundSessionIds.Contains(sessionId))
        {
            return !IsTransportDegraded;
        }

        // Before browser visibility hints arrive, preserve the compatibility
        // behavior and deliver every accessible session. Once a browser names
        // an active, visible, or background terminal, unmounted sessions stay
        // live in mthost but their bytes are held at the last delivered cursor.
        return _activeSessionId is null
            && _visibleSessionIds.Count == 0
            && _backgroundSessionIds.Count == 0;
    }

    public bool IsActiveSession(string sessionId)
    {
        var activeId = _activeSessionId;
        return activeId is not null && string.Equals(activeId, sessionId, StringComparison.Ordinal);
    }

    internal string? ActiveSessionId => _activeSessionId;

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
        var recoveryCompletions = _activeRecoveries.Values.Select(operation =>
        {
            operation.Cancel();
            return operation.Completion.Task;
        }).ToArray();
        _inputQueue.Complete();
        await _writer.DisposeAsync().ConfigureAwait(false);

        try
        {
            await _processor.ConfigureAwait(false);
        }
        catch
        {
            // Ignore shutdown errors
        }

        await Task.WhenAll(recoveryCompletions).ConfigureAwait(false);

        foreach (var item in _inputQueue.Drain())
        {
            item.Buffer?.Release();
        }
        _inputQueue.Dispose();

        while (_sessionsToRemove.TryDequeue(out var removal))
        {
            removal.Completion.TrySetResult();
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
