using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.WebSockets;

namespace Ai.Tlbx.MidTerm.Services.WebSockets;

internal enum MuxWritePriority
{
    Control = 0,
    ActiveLive = 1,
    VisibleLive = 2,
    Recovery = 3,
    BackgroundLive = 4
}

/// <summary>
/// Owns all writes for one mux socket. The bounded priority queue prevents slow
/// clients from creating unbounded send tasks while allowing interactive and
/// control traffic to overtake other sessions between frames. Priority never
/// overtakes an older frame in the same session, including recovery barriers.
/// </summary>
internal sealed class PrioritizedWebSocketWriter : IAsyncDisposable
{
    internal const int MaxQueuedFrames = 2048;
    internal const int MaxQueuedBytes = 8 * 1024 * 1024;
    internal const int PriorityBurstLimit = 32;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    private sealed class PendingWrite
    {
        private byte[]? _ownedBuffer;
        private readonly Action<bool>? _completed;

        public PendingWrite(ReadOnlyMemory<byte> data, MuxWritePriority priority, long order, byte[]? ownedBuffer = null, Action<bool>? completed = null)
        {
            Data = data;
            Priority = priority;
            Order = order;
            _ownedBuffer = ownedBuffer;
            _completed = completed;
        }

        public ReadOnlyMemory<byte> Data { get; }
        public MuxWritePriority Priority { get; }
        public long Order { get; }
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(bool succeeded)
        {
            Completion.TrySetResult(succeeded);
            try
            {
                _completed?.Invoke(succeeded);
            }
            catch
            {
                // Completion telemetry must never stop the socket writer.
            }
            finally
            {
                var ownedBuffer = Interlocked.Exchange(ref _ownedBuffer, null);
                if (ownedBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(ownedBuffer);
                }
            }
        }
    }

    private sealed class SessionWrites(string? sessionId)
    {
        public string? SessionId { get; } = sessionId;
        public Queue<PendingWrite> Frames { get; } = new();
        public PendingWrite Head => Frames.Peek();
    }

    private readonly WebSocket _webSocket;
    private readonly Action<TimeSpan, int> _sendObserved;
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionWrites> _sessions = new(StringComparer.Ordinal);
    // Only heads are eligible. Remove a lane before changing its head, then
    // reinsert it so the set's ordering keys never mutate while registered.
    private readonly SortedSet<SessionWrites> _ready = new(Comparer<SessionWrites>.Create(
        static (a, b) => a.Head.Priority != b.Head.Priority
            ? a.Head.Priority.CompareTo(b.Head.Priority)
            : a.Head.Order.CompareTo(b.Head.Order)));
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processor;
    private CancellationTokenSource? _sendTimeoutCts;
    private long _nextOrder;
    private long _queuedBytes;
    private int _queuedFrames;
    private int _priorityBurst;
    private bool _stopped;
    private bool _disposed;

    public PrioritizedWebSocketWriter(WebSocket webSocket, Action<TimeSpan, int> sendObserved)
    {
        _webSocket = webSocket;
        _sendObserved = sendObserved;
        _processor = ProcessAsync();
    }

    public ValueTask<bool> SendAsync(ReadOnlyMemory<byte> data, MuxWritePriority priority, string? sessionId = null)
    {
        lock (_gate)
        {
            if (_disposed || _stopped
                || _cts.IsCancellationRequested
                || _webSocket.State != WebSocketState.Open
                || _queuedFrames >= MaxQueuedFrames
                || _queuedBytes + data.Length > MaxQueuedBytes)
            {
                return ValueTask.FromResult(false);
            }

            var pending = new PendingWrite(data, priority, _nextOrder++);
            Enqueue(pending, sessionId);
            // Dispose takes the same gate before marking the writer closed, so
            // the semaphore cannot be disposed between enqueue and release.
            _available.Release();
            return new ValueTask<bool>(pending.Completion.Task);
        }
    }

    public bool TryQueueCopy(ReadOnlySpan<byte> data, MuxWritePriority priority, Action<bool>? completed = null, string? sessionId = null)
    {
        lock (_gate)
        {
            if (_disposed || _stopped
                || _cts.IsCancellationRequested
                || _webSocket.State != WebSocketState.Open
                || _queuedFrames >= MaxQueuedFrames
                || _queuedBytes + data.Length > MaxQueuedBytes)
            {
                return false;
            }

            var ownedBuffer = ArrayPool<byte>.Shared.Rent(data.Length);
            data.CopyTo(ownedBuffer);
            var pending = new PendingWrite(ownedBuffer.AsMemory(0, data.Length), priority, _nextOrder++, ownedBuffer, completed);
            Enqueue(pending, sessionId);
            _available.Release();
            return true;
        }
    }

    // Called only under _gate. Unscoped connection control frames are independent.
    private void Enqueue(PendingWrite pending, string? sessionId)
    {
        if (sessionId is null || !_sessions.TryGetValue(sessionId, out var lane))
        {
            lane = new SessionWrites(sessionId);
            if (sessionId is not null) _sessions.Add(sessionId, lane);
        }
        lane.Frames.Enqueue(pending);
        if (lane.Frames.Count == 1) _ready.Add(lane);
        _queuedFrames++;
        _queuedBytes += pending.Data.Length;
    }

    private PendingWrite? Dequeue()
    {
        var lane = _ready.Min;
        if (lane is null) return null;
        if (_priorityBurst >= PriorityBurstLimit)
        {
            SessionWrites? oldestLowerPriority = null;
            foreach (var candidate in _ready)
            {
                if (candidate.Head.Priority > lane.Head.Priority &&
                    (oldestLowerPriority is null || candidate.Head.Order < oldestLowerPriority.Head.Order))
                {
                    oldestLowerPriority = candidate;
                }
            }
            if (oldestLowerPriority is not null)
            {
                lane = oldestLowerPriority;
                _priorityBurst = 0;
            }
        }
        _priorityBurst = Math.Min(_priorityBurst + 1, PriorityBurstLimit);
        _ready.Remove(lane);
        var pending = lane.Frames.Dequeue();
        if (lane.Frames.Count > 0) _ready.Add(lane);
        else if (lane.SessionId is not null) _sessions.Remove(lane.SessionId);
        _queuedFrames--;
        _queuedBytes -= pending.Data.Length;
        return pending;
    }

    private async Task ProcessAsync()
    {
        var passStartedAt = Stopwatch.GetTimestamp();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _available.WaitAsync(_cts.Token).ConfigureAwait(false);
                PendingWrite? pending;
                lock (_gate)
                {
                    pending = Dequeue();
                }

                if (pending is null)
                {
                    continue;
                }

                var succeeded = false;
                try
                {
                    succeeded = await SendCoreAsync(pending.Data).ConfigureAwait(false);
                }
                finally
                {
                    pending.Complete(succeeded);
                }
                if (!succeeded)
                {
                    return;
                }
                // SendAsync can complete synchronously on a fast connection.
                // Bound a flood's uninterrupted worker time so terminal input
                // and other sockets can run on the same constrained pool.
                if (Stopwatch.GetElapsedTime(passStartedAt).TotalMilliseconds >= 3)
                {
                    await Task.Yield();
                    passStartedAt = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        finally
        {
            FailPendingWrites();
        }
    }

    private async Task<bool> SendCoreAsync(ReadOnlyMemory<byte> data)
    {
        if (_webSocket.State != WebSocketState.Open)
        {
            return false;
        }

        if (_sendTimeoutCts is null || !_sendTimeoutCts.TryReset())
        {
            _sendTimeoutCts?.Dispose();
            _sendTimeoutCts = new CancellationTokenSource();
        }
        _sendTimeoutCts.CancelAfter(SendTimeout);

        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            await _webSocket.SendAsync(data, WebSocketMessageType.Binary, true, _sendTimeoutCts.Token).ConfigureAwait(false);
            _sendObserved(Stopwatch.GetElapsedTime(startedAt), data.Length);
            return true;
        }
        catch (OperationCanceledException)
        {
            AbortSocket();
            return false;
        }
        catch (WebSocketException)
        {
            AbortSocket();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void FailPendingWrites()
    {
        List<PendingWrite> failed = [];
        lock (_gate)
        {
            _stopped = true;
            foreach (var lane in _ready)
            {
                failed.AddRange(lane.Frames);
            }
            _ready.Clear();
            _sessions.Clear();
            _queuedFrames = 0;
            _queuedBytes = 0;
        }

        foreach (var pending in failed)
        {
            pending.Complete(false);
        }
    }

    private void AbortSocket()
    {
        try
        {
            _webSocket.Abort();
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        _cts.Cancel();
        _sendTimeoutCts?.Cancel();
        _available.Release();

        try
        {
            await _processor.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _sendTimeoutCts?.Dispose();
        _available.Dispose();
        _cts.Dispose();
    }
}
