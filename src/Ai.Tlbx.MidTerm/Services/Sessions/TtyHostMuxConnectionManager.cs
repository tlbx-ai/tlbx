using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Services.WebSockets;
using Ai.Tlbx.MidTerm.Settings;
namespace Ai.Tlbx.MidTerm.Services.Sessions;

/// <summary>
/// WebSocket mux manager for con-host mode.
/// </summary>
public sealed class TtyHostMuxConnectionManager : IDisposable, IAsyncDisposable
{
    private sealed class ArchivedRecoveryCounters
    {
        public long Requested;
        public long Coalesced;
        public long Completed;
        public long Resets;
        public long ReplayBytes;
        public long Failed;
    }

    private readonly record struct PooledOutputItem(
        string SessionId,
        ulong SequenceEndExclusive,
        int Cols,
        int Rows,
        SharedOutputBuffer Buffer);

    private sealed class InputLatencyTrace
    {
        public readonly object Gate = new();

        public InputLatencyTrace(string clientId, string sessionId, uint traceId, long markerReceivedAtMs)
        {
            ClientId = clientId;
            SessionId = sessionId;
            TraceId = traceId;
            MuxMarkerReceivedAtMs = markerReceivedAtMs;
        }

        public string ClientId { get; }
        public string SessionId { get; }
        public uint TraceId { get; }
        public long MuxMarkerReceivedAtMs { get; }
        public long MuxInputReceivedAtMs { get; set; }
        public long IpcWriteStartAtMs { get; set; }
        public long IpcWriteDoneAtMs { get; set; }
        public long MthostMarkerReceivedAtMs { get; set; }
        public long MthostInputReceivedAtMs { get; set; }
        public long PtyWriteDoneAtMs { get; set; }
        public long PtyOutputReadAtMs { get; set; }
        public long MthostIpcOutputEnqueuedAtMs { get; set; }
        public long MthostIpcOutputWriteDoneAtMs { get; set; }
        public long MthostIpcOutputFlushDoneAtMs { get; set; }
        public long OutputObservedAtMs { get; set; }
        public long MuxQueueEnqueuedAtMs { get; set; }
        public long ClientQueuedAtMs { get; set; }
        public long WsFlushAtMs { get; set; }
        public ulong FirstOutputSequenceEndExclusive { get; set; }
        public bool Reported { get; set; }
    }

    private readonly TtyHostSessionManager _sessionManager;
    private readonly ConcurrentDictionary<string, MuxClient> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _inputTimestamps = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _lastServerRttMs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string ClientId, string SessionId), InputLatencyTrace> _inputTraceMarkers = new();
    private readonly ConcurrentDictionary<(string SessionId, uint TraceId), InputLatencyTrace> _activeInputTraces = new();
    private readonly ConcurrentDictionary<string, ArchivedRecoveryCounters> _archivedRecoveryCounters = new(StringComparer.Ordinal);
    private const int MaxInputLatencyTraces = 256;
    private const long InputLatencyTraceTimeoutMs = 10_000;
    private const int MaxQueuedOutputs = 1000;
    private const int OutputQueuePriorityBatchSize = 256;
    private readonly Channel<PooledOutputItem> _outputQueue =
        Channel.CreateBounded<PooledOutputItem>(
            new BoundedChannelOptions(MaxQueuedOutputs) { FullMode = BoundedChannelFullMode.Wait });
    private Task? _outputProcessor;
    private CancellationTokenSource? _cts;
    private readonly SettingsService _settingsService;
    private readonly Action<string, ulong, int, int, ReadOnlyMemory<byte>> _outputHandler;
    private readonly Action<string> _sessionClosedHandler;
    private readonly Action<string, ForegroundChangePayload> _foregroundChangedHandler;
    private readonly Action<string, TtyHostInputTraceReport> _inputTraceHandler;
    private readonly Action<string, TtyHostDataLossPayload> _dataLossHandler;
    private readonly string _settingsListenerId;
    private TerminalResumeModeSetting _resumeMode;
    private bool _disposed;

    public TtyHostMuxConnectionManager(TtyHostSessionManager sessionManager, SettingsService settingsService)
    {
        _sessionManager = sessionManager;
        _settingsService = settingsService;
        _resumeMode = settingsService.Load().ResumeMode;
        _settingsListenerId = settingsService.AddSettingsListener(settings => _resumeMode = settings.ResumeMode);
        _outputHandler = HandleOutput;
        _sessionClosedHandler = HandleSessionClosed;
        _foregroundChangedHandler = HandleForegroundChanged;
        _inputTraceHandler = HandleInputTrace;
        _dataLossHandler = HandleDataLoss;
        _sessionManager.OnOutput += _outputHandler;
        _sessionManager.OnSessionClosed += _sessionClosedHandler;
        _sessionManager.OnForegroundChanged += _foregroundChangedHandler;
        _sessionManager.OnInputTrace += _inputTraceHandler;
        _sessionManager.OnDataLoss += _dataLossHandler;

        _cts = new CancellationTokenSource();
        _outputProcessor = ProcessOutputQueueAsync(_cts.Token);
    }

    private void HandleSessionClosed(string sessionId)
    {
        _inputTimestamps.TryRemove(sessionId, out _);
        _lastServerRttMs.TryRemove(sessionId, out _);
        RemoveInputTracesForSession(sessionId);
        _archivedRecoveryCounters.TryRemove(sessionId, out _);
        MuxProtocol.ClearSessionCache(sessionId);

        foreach (var client in _clients.Values)
        {
            client.RemoveSession(sessionId);
        }
    }

    private void HandleOutput(string sessionId, ulong sequenceStart, int cols, int rows, ReadOnlyMemory<byte> data)
    {
        if (_inputTimestamps.TryRemove(sessionId, out var inputTicks))
        {
            _lastServerRttMs[sessionId] = (int)(Environment.TickCount64 - inputTicks);
        }

        var shared = SharedOutputBuffer.Rent(data.Length);
        data.Span.CopyTo(shared.WriteSpan);

        var sequenceEndExclusive = sequenceStart + (ulong)data.Length;
        var trace = MarkInputTraceOutputObserved(sessionId, sequenceEndExclusive);
        if (!_outputQueue.Writer.TryWrite(new PooledOutputItem(sessionId, sequenceEndExclusive, cols, rows, shared)))
        {
            if (trace is not null)
            {
                RemoveInputTrace(trace);
            }
            Log.Warn(() => $"[MuxManager] Output queue full, dropping frame for {sessionId} ({data.Length} bytes)");
            foreach (var client in _clients.Values)
            {
                if (client.WebSocket.State == WebSocketState.Open)
                {
                    client.NotifyDataLoss(
                        sessionId,
                        TerminalReplayReason.MuxOverflow,
                        data.Length,
                        sequenceStart,
                        sequenceEndExclusive);
                }
            }
            shared.Release();
            return;
        }

        if (trace is not null)
        {
            lock (trace.Gate)
            {
                trace.MuxQueueEnqueuedAtMs = Environment.TickCount64;
            }
        }
    }

    private void HandleDataLoss(string sessionId, TtyHostDataLossPayload payload)
    {
        foreach (var client in _clients.Values)
        {
            if (client.WebSocket.State == WebSocketState.Open)
            {
                client.NotifyDataLoss(
                    sessionId,
                    payload.Reason,
                    payload.DroppedBytes,
                    payload.MissingSequenceStart,
                    payload.MissingSequenceEndExclusive);
            }
        }
    }

    private void HandleForegroundChanged(string sessionId, ForegroundChangePayload payload)
    {
        var jsonPayload = JsonSerializer.SerializeToUtf8Bytes(payload, TtyHostJsonContext.Default.ForegroundChangePayload);
        var frame = MuxProtocol.CreateForegroundChangeFrame(sessionId, jsonPayload);

        foreach (var client in _clients.Values)
        {
            if (client.WebSocket.State == WebSocketState.Open)
            {
                client.QueueFrame(frame, sessionId);
            }
        }
    }

    private async Task ProcessOutputQueueAsync(CancellationToken ct)
    {
        var batch = new List<PooledOutputItem>(OutputQueuePriorityBatchSize);
        while (await _outputQueue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Count < OutputQueuePriorityBatchSize && _outputQueue.Reader.TryRead(out var item))
            {
                batch.Add(item);
            }

            var activeInterest = new bool[batch.Count];
            for (var i = 0; i < batch.Count; i++)
            {
                activeInterest[i] = HasActiveClientInterest(batch[i].SessionId);
            }

            for (var i = 0; i < batch.Count; i++)
            {
                if (activeInterest[i])
                {
                    QueueOutputToClients(batch[i]);
                }
            }

            for (var i = 0; i < batch.Count; i++)
            {
                if (!activeInterest[i])
                {
                    QueueOutputToClients(batch[i]);
                }
            }
        }
    }

    private bool HasActiveClientInterest(string sessionId)
    {
        foreach (var client in _clients.Values)
        {
            if (client.WebSocket.State == WebSocketState.Open && client.IsActiveSession(sessionId))
            {
                return true;
            }
        }

        return false;
    }

    private void QueueOutputToClients(PooledOutputItem item)
    {
        try
        {
            if (item.Buffer.Length < 50)
            {
                Log.Verbose(() => $"[WS-OUTPUT] {item.SessionId}: {BitConverter.ToString(item.Buffer.Memory[..item.Buffer.Length].Span.ToArray())}");
            }

            // Queue raw data to each client - clients handle buffering and framing
            foreach (var client in _clients.Values)
            {
                if (client.WebSocket.State == WebSocketState.Open)
                {
                    item.Buffer.AddRef();
                    if (client.QueueOutput(item.SessionId, item.SequenceEndExclusive, item.Cols, item.Rows, item.Buffer))
                    {
                        MarkInputTraceClientQueued(client.Id, item.SessionId, item.SequenceEndExclusive);
                    }
                }
            }
        }
        finally
        {
            item.Buffer.Release();
        }
    }

    public MuxClient AddClient(string clientId, WebSocket webSocket, string? allowedSessionId = null)
    {
        var client = new MuxClient(clientId, webSocket, GetResumeMode, allowedSessionId, HandleClientOutputFrameSent);
        _clients[clientId] = client;
        return client;
    }

    private TerminalResumeModeSetting GetResumeMode() => _resumeMode;

    public async Task RemoveClientAsync(string clientId)
    {
        if (_clients.TryRemove(clientId, out var client))
        {
            RemoveInputTracesForClient(clientId);
            ArchiveRecoveryTelemetry(client);
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal MuxClient.RecoveryTelemetrySnapshot GetRecoveryTelemetry(string sessionId)
    {
        long requested = 0;
        long coalesced = 0;
        long completed = 0;
        long resets = 0;
        long replayBytes = 0;
        long failed = 0;

        if (_archivedRecoveryCounters.TryGetValue(sessionId, out var archived))
        {
            requested += Interlocked.Read(ref archived.Requested);
            coalesced += Interlocked.Read(ref archived.Coalesced);
            completed += Interlocked.Read(ref archived.Completed);
            resets += Interlocked.Read(ref archived.Resets);
            replayBytes += Interlocked.Read(ref archived.ReplayBytes);
            failed += Interlocked.Read(ref archived.Failed);
        }

        foreach (var client in _clients.Values)
        {
            var current = client.GetRecoveryTelemetry(sessionId);
            requested += current.Requested;
            coalesced += current.Coalesced;
            completed += current.Completed;
            resets += current.Resets;
            replayBytes += current.ReplayBytes;
            failed += current.Failed;
        }

        return new MuxClient.RecoveryTelemetrySnapshot(
            requested,
            coalesced,
            completed,
            resets,
            replayBytes,
            failed);
    }

    private void ArchiveRecoveryTelemetry(MuxClient client)
    {
        foreach (var (sessionId, snapshot) in client.GetAllRecoveryTelemetry())
        {
            var archived = _archivedRecoveryCounters.GetOrAdd(sessionId, static _ => new ArchivedRecoveryCounters());
            Interlocked.Add(ref archived.Requested, snapshot.Requested);
            Interlocked.Add(ref archived.Coalesced, snapshot.Coalesced);
            Interlocked.Add(ref archived.Completed, snapshot.Completed);
            Interlocked.Add(ref archived.Resets, snapshot.Resets);
            Interlocked.Add(ref archived.ReplayBytes, snapshot.ReplayBytes);
            Interlocked.Add(ref archived.Failed, snapshot.Failed);
        }
    }

    public void BeginInputTrace(string clientId, string sessionId, uint traceId)
    {
        if (traceId == 0)
        {
            return;
        }

        var now = Environment.TickCount64;
        PruneExpiredInputTraces(now);
        if (_activeInputTraces.Count + _inputTraceMarkers.Count >= MaxInputLatencyTraces)
        {
            return;
        }

        _inputTraceMarkers[(clientId, sessionId)] = new InputLatencyTrace(clientId, sessionId, traceId, now);
    }

    public async Task HandleInputAsync(string clientId, string sessionId, ReadOnlyMemory<byte> data)
    {
        var inputReceivedAtMs = Environment.TickCount64;
        _inputTimestamps[sessionId] = inputReceivedAtMs;

        if (!_inputTraceMarkers.TryRemove((clientId, sessionId), out var trace))
        {
            await _sessionManager.SendInputAsync(sessionId, data, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
            return;
        }

        lock (trace.Gate)
        {
            trace.MuxInputReceivedAtMs = inputReceivedAtMs;
        }
        _activeInputTraces[(sessionId, trace.TraceId)] = trace;

        var timing = await _sessionManager.SendInputWithTraceAsync(
            sessionId,
            data,
            trace.TraceId,
            _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        if (timing is null)
        {
            RemoveInputTrace(trace);
            return;
        }

        lock (trace.Gate)
        {
            trace.IpcWriteStartAtMs = timing.Value.IpcWriteStartAtMs;
            trace.IpcWriteDoneAtMs = timing.Value.IpcWriteDoneAtMs;
        }
    }

    private void HandleInputTrace(string sessionId, TtyHostInputTraceReport report)
    {
        if (!_activeInputTraces.TryGetValue((sessionId, report.TraceId), out var trace))
        {
            return;
        }

        lock (trace.Gate)
        {
            trace.MthostMarkerReceivedAtMs = report.MarkerReceivedAtMs;
            trace.MthostInputReceivedAtMs = report.InputReceivedAtMs;
            trace.PtyWriteDoneAtMs = report.PtyWriteDoneAtMs;
            if (report.FirstOutputSequenceEndExclusive != 0)
            {
                trace.FirstOutputSequenceEndExclusive = report.FirstOutputSequenceEndExclusive;
                trace.PtyOutputReadAtMs = report.PtyOutputReadAtMs;
                trace.MthostIpcOutputEnqueuedAtMs = report.IpcOutputEnqueuedAtMs;
                trace.MthostIpcOutputWriteDoneAtMs = report.IpcOutputWriteDoneAtMs;
                trace.MthostIpcOutputFlushDoneAtMs = report.IpcOutputFlushDoneAtMs;
            }
        }

        TrySendInputTraceResult(trace);
    }

    private InputLatencyTrace? MarkInputTraceOutputObserved(string sessionId, ulong sequenceEndExclusive)
    {
        var now = Environment.TickCount64;
        foreach (var trace in _activeInputTraces.Values)
        {
            if (!string.Equals(trace.SessionId, sessionId, StringComparison.Ordinal))
            {
                continue;
            }

            lock (trace.Gate)
            {
                if (trace.FirstOutputSequenceEndExclusive != 0 || trace.Reported)
                {
                    continue;
                }

                trace.FirstOutputSequenceEndExclusive = sequenceEndExclusive;
                trace.OutputObservedAtMs = now;
                return trace;
            }
        }

        return null;
    }

    private void MarkInputTraceClientQueued(string clientId, string sessionId, ulong sequenceEndExclusive)
    {
        var trace = FindClientTraceForOutput(clientId, sessionId, sequenceEndExclusive);
        if (trace is null)
        {
            return;
        }

        lock (trace.Gate)
        {
            if (trace.ClientQueuedAtMs == 0)
            {
                trace.ClientQueuedAtMs = Environment.TickCount64;
            }
        }
    }

    private void HandleClientOutputFrameSent(
        string clientId,
        string sessionId,
        ulong sequenceEndExclusive,
        long sentAtMs)
    {
        var trace = FindClientTraceForOutput(clientId, sessionId, sequenceEndExclusive);
        if (trace is null)
        {
            return;
        }

        lock (trace.Gate)
        {
            trace.WsFlushAtMs = sentAtMs;
        }

        TrySendInputTraceResult(trace);
    }

    private void TrySendInputTraceResult(InputLatencyTrace trace)
    {
        MuxInputTraceResult result;
        lock (trace.Gate)
        {
            if (trace.Reported ||
                trace.FirstOutputSequenceEndExclusive == 0 ||
                trace.WsFlushAtMs == 0 ||
                trace.MthostIpcOutputFlushDoneAtMs == 0)
            {
                return;
            }

            trace.Reported = true;
            result = BuildInputTraceResult(trace);
        }

        RemoveInputTrace(trace);

        if (_clients.TryGetValue(trace.ClientId, out var client) && client.WebSocket.State == WebSocketState.Open)
        {
            client.QueueFrame(MuxProtocol.CreateInputTraceResultFrame(trace.SessionId, result), trace.SessionId);
        }
    }

    private InputLatencyTrace? FindClientTraceForOutput(
        string clientId,
        string sessionId,
        ulong sequenceEndExclusive)
    {
        foreach (var trace in _activeInputTraces.Values)
        {
            if (!string.Equals(trace.ClientId, clientId, StringComparison.Ordinal) ||
                !string.Equals(trace.SessionId, sessionId, StringComparison.Ordinal))
            {
                continue;
            }

            lock (trace.Gate)
            {
                if (trace.Reported ||
                    trace.FirstOutputSequenceEndExclusive == 0 ||
                    trace.FirstOutputSequenceEndExclusive > sequenceEndExclusive)
                {
                    continue;
                }

                return trace;
            }
        }

        return null;
    }

    private static MuxInputTraceResult BuildInputTraceResult(InputLatencyTrace trace)
    {
        return new MuxInputTraceResult(
            trace.TraceId,
            trace.FirstOutputSequenceEndExclusive,
            ElapsedMs(trace.MuxInputReceivedAtMs, trace.IpcWriteStartAtMs),
            ElapsedMs(trace.IpcWriteStartAtMs, trace.IpcWriteDoneAtMs),
            ElapsedMs(trace.MuxInputReceivedAtMs, trace.MthostInputReceivedAtMs),
            ElapsedMs(trace.MuxInputReceivedAtMs, trace.PtyWriteDoneAtMs),
            ElapsedMs(trace.PtyWriteDoneAtMs, trace.PtyOutputReadAtMs),
            ElapsedMs(trace.PtyOutputReadAtMs, trace.MthostIpcOutputEnqueuedAtMs),
            ElapsedMs(trace.MthostIpcOutputEnqueuedAtMs, trace.MthostIpcOutputWriteDoneAtMs),
            ElapsedMs(trace.MthostIpcOutputWriteDoneAtMs, trace.MthostIpcOutputFlushDoneAtMs),
            ElapsedMs(trace.MthostIpcOutputEnqueuedAtMs, trace.OutputObservedAtMs),
            ElapsedMs(trace.MuxInputReceivedAtMs, trace.OutputObservedAtMs),
            ElapsedMs(trace.OutputObservedAtMs, trace.MuxQueueEnqueuedAtMs),
            ElapsedMs(trace.MuxQueueEnqueuedAtMs, trace.ClientQueuedAtMs),
            ElapsedMs(trace.ClientQueuedAtMs, trace.WsFlushAtMs),
            ElapsedMs(trace.MuxInputReceivedAtMs, trace.WsFlushAtMs));
    }

    private static int ElapsedMs(long startAtMs, long endAtMs)
    {
        if (startAtMs <= 0 || endAtMs <= 0 || endAtMs < startAtMs)
        {
            return -1;
        }

        return (int)Math.Clamp(endAtMs - startAtMs, 0, int.MaxValue);
    }

    private void PruneExpiredInputTraces(long nowMs)
    {
        foreach (var (key, trace) in _inputTraceMarkers)
        {
            if (nowMs - trace.MuxMarkerReceivedAtMs > InputLatencyTraceTimeoutMs)
            {
                _inputTraceMarkers.TryRemove(key, out _);
            }
        }

        foreach (var (key, trace) in _activeInputTraces)
        {
            if (nowMs - trace.MuxMarkerReceivedAtMs > InputLatencyTraceTimeoutMs)
            {
                _activeInputTraces.TryRemove(key, out _);
            }
        }
    }

    private void RemoveInputTrace(InputLatencyTrace trace)
    {
        _inputTraceMarkers.TryRemove((trace.ClientId, trace.SessionId), out _);
        _activeInputTraces.TryRemove((trace.SessionId, trace.TraceId), out _);
    }

    private void RemoveInputTracesForClient(string clientId)
    {
        foreach (var (key, trace) in _inputTraceMarkers)
        {
            if (string.Equals(trace.ClientId, clientId, StringComparison.Ordinal))
            {
                _inputTraceMarkers.TryRemove(key, out _);
            }
        }

        foreach (var (key, trace) in _activeInputTraces)
        {
            if (string.Equals(trace.ClientId, clientId, StringComparison.Ordinal))
            {
                _activeInputTraces.TryRemove(key, out _);
            }
        }
    }

    private void RemoveInputTracesForSession(string sessionId)
    {
        foreach (var (key, trace) in _inputTraceMarkers)
        {
            if (string.Equals(trace.SessionId, sessionId, StringComparison.Ordinal))
            {
                _inputTraceMarkers.TryRemove(key, out _);
            }
        }

        foreach (var (key, trace) in _activeInputTraces)
        {
            if (string.Equals(trace.SessionId, sessionId, StringComparison.Ordinal))
            {
                _activeInputTraces.TryRemove(key, out _);
            }
        }
    }

    public int GetServerRtt(string sessionId)
    {
        return _lastServerRttMs.TryGetValue(sessionId, out var rtt) ? rtt : -1;
    }

    public async Task HandlePingAsync(string sessionId, byte[] pingData, MuxClient client)
    {
        var pongData = await _sessionManager.PingAsync(sessionId, pingData, _cts?.Token ?? CancellationToken.None);
        if (pongData is null) return;

        var pong = new byte[MuxProtocol.HeaderSize + 1 + pongData.Length];
        pong[0] = MuxProtocol.TypePong;
        MuxProtocol.WriteSessionId(pong.AsSpan(1, 8), sessionId);
        pong[MuxProtocol.HeaderSize] = 1; // mode = mthost
        pongData.CopyTo(pong.AsSpan(MuxProtocol.HeaderSize + 1));
        await client.TrySendAsync(pong);
    }

    public async Task HandleResizeAsync(string sessionId, int cols, int rows)
    {
        await _sessionManager.ResizeSessionAsync(sessionId, cols, rows, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
    }

    public void BroadcastTerminalOutput(string sessionId, ReadOnlyMemory<byte> data)
    {
        var sessionInfo = _sessionManager.GetSession(sessionId);
        var cols = sessionInfo?.Cols ?? 80;
        var rows = sessionInfo?.Rows ?? 24;
        var sequenceEndExclusive = _sessionManager.GetTransportRuntimeSnapshot(sessionId).SourceSeq;

        // Queue raw data to each client - clients handle buffering and framing
        var buffer = SharedOutputBuffer.Rent(data.Length);
        data.Span.CopyTo(buffer.WriteSpan);
        foreach (var client in _clients.Values)
        {
            if (client.WebSocket.State == WebSocketState.Open)
            {
                buffer.AddRef();
                client.QueueOutput(sessionId, sequenceEndExclusive, cols, rows, buffer);
            }
        }
        buffer.Release();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        _outputQueue.Writer.TryComplete();
        if (_outputProcessor is not null)
        {
            try { await _outputProcessor.ConfigureAwait(false); } catch { }
        }
        while (_outputQueue.Reader.TryRead(out var item))
        {
            item.Buffer.Release();
        }
        cts?.Dispose();

        var clients = _clients.ToArray();
        _clients.Clear();
        foreach (var (_, client) in clients)
        {
            try
            {
                ArchiveRecoveryTelemetry(client);
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _sessionManager.OnOutput -= _outputHandler;
        _sessionManager.OnSessionClosed -= _sessionClosedHandler;
        _sessionManager.OnForegroundChanged -= _foregroundChangedHandler;
        _sessionManager.OnInputTrace -= _inputTraceHandler;
        _sessionManager.OnDataLoss -= _dataLossHandler;
        _inputTraceMarkers.Clear();
        _activeInputTraces.Clear();
        _archivedRecoveryCounters.Clear();
        _settingsService.RemoveSettingsListener(_settingsListenerId);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
