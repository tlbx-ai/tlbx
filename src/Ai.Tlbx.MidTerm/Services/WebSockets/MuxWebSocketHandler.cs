using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Services.Share;
using Ai.Tlbx.MidTerm.Settings;
using Ai.Tlbx.MidTerm.Services.Sessions;
namespace Ai.Tlbx.MidTerm.Services.WebSockets;

public sealed class MuxWebSocketHandler
{
    private const int ReplayFrameChunkBytes = 32 * 1024;
    private const int QuickResumeShellBurstBytes = 192 * 1024;
    private const int QuickResumeInteractiveBurstBytes = 64 * 1024;
    private const int ReplayRowsMinimum = 10;
    private const int ReplayRowsMaximum = 500;
    private const int ReplayRowsOverscan = 12;
    private const int ReplayBytesPerCell = 24;
    private const int ReplayBytesPadding = 32 * 1024;
    private const int ReplayBytesMinimum = 32 * 1024;
    private const int ReplayBytesMaximum = 256 * 1024;
    private readonly TtyHostSessionManager _sessionManager;
    private readonly TtyHostMuxConnectionManager _muxManager;
    private readonly SettingsService _settingsService;
    private readonly AuthService _authService;
    private readonly ShareGrantService _shareGrantService;
    private readonly ShutdownService _shutdownService;

    public MuxWebSocketHandler(
        TtyHostSessionManager sessionManager,
        TtyHostMuxConnectionManager muxManager,
        SettingsService settingsService,
        AuthService authService,
        ShareGrantService shareGrantService,
        ShutdownService shutdownService)
    {
        _sessionManager = sessionManager;
        _muxManager = muxManager;
        _settingsService = settingsService;
        _authService = authService;
        _shareGrantService = shareGrantService;
        _shutdownService = shutdownService;
    }

    public async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        var shareAccess = RequestAccessContext.GetShareAccess(context);
        var isShareConnection = string.Equals(path, "/ws/share/mux", StringComparison.Ordinal);

        if (isShareConnection)
        {
            if (shareAccess is null || shareAccess.IsExpired(DateTime.UtcNow))
            {
                context.Response.StatusCode = 401;
                return;
            }
        }
        else
        {
            if (_authService.AuthenticateRequest(context.Request) == RequestAuthMethod.None)
            {
                context.Response.StatusCode = 401;
                return;
            }
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        var clientId = Guid.NewGuid().ToString("N");
        Timer? expiryTimer = null;
        Action<string>? revokeHandler = null;

        var client = _muxManager.AddClient(clientId, ws, shareAccess?.SessionId);
        var initialPrioritySessionId = ResolveInitialPrioritySessionId(context, shareAccess);
        var initialVisibleSessionIds = ResolveInitialVisibleSessionIds(context, shareAccess);
        var initialReplayRows = ResolveInitialReplayRows(context);
        Task? deferredReplayTask = null;
        using var deferredReplayCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownService.Token);

        try
        {
            if (shareAccess is not null)
            {
                revokeHandler = grantId =>
                {
                    if (string.Equals(grantId, shareAccess.GrantId, StringComparison.Ordinal))
                    {
                        try
                        {
                            ws.Abort();
                        }
                        catch
                        {
                        }
                    }
                };
                _shareGrantService.OnGrantRevoked += revokeHandler;

                var delay = shareAccess.ExpiresAtUtc - DateTime.UtcNow;
                if (delay <= TimeSpan.Zero)
                {
                    ws.Abort();
                    return;
                }

                expiryTimer = new Timer(_ =>
                {
                    try
                    {
                        ws.Abort();
                    }
                    catch
                    {
                    }
                }, null, delay, Timeout.InfiniteTimeSpan);
            }

            if (initialPrioritySessionId is not null)
            {
                client.SetActiveSession(initialPrioritySessionId);
            }
            client.SetVisibleSessions(initialVisibleSessionIds);

            client.SuspendFlush();
            await SendInitFrameAsync(client, clientId);
            var quickResumeEnabled = client.ShouldUseQuickResume();
            if (initialPrioritySessionId is null || quickResumeEnabled)
            {
                await SendInitialBuffersAsync(
                    client,
                    shareAccess?.SessionId,
                    prioritySessionId: initialPrioritySessionId,
                    replayMode: InitialReplayMode.All,
                    quickResumeEnabled,
                    initialReplayRows,
                    deferredReplayCts.Token);
            }
            else
            {
                await SendInitialBuffersAsync(
                    client,
                    shareAccess?.SessionId,
                    initialPrioritySessionId,
                    InitialReplayMode.PriorityOnly,
                    quickResumeEnabled,
                    initialReplayRows,
                    deferredReplayCts.Token);
            }
            await client.TrySendAsync(MuxProtocol.CreateSyncCompleteFrame());
            client.ResumeFlush();
            if (initialPrioritySessionId is not null && !quickResumeEnabled)
            {
                deferredReplayTask = SendInitialBuffersAsync(
                    client,
                    shareAccess?.SessionId,
                    initialPrioritySessionId,
                    InitialReplayMode.NonPriorityOnly,
                    quickResumeEnabled,
                    initialReplayRows,
                    deferredReplayCts.Token);
            }
            await ProcessMessagesAsync(ws, clientId, client, shareAccess);
        }
        finally
        {
            deferredReplayCts.Cancel();
            if (deferredReplayTask is not null)
            {
                try
                {
                    await deferredReplayTask;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Log.Warn(() => $"[MuxHandler] Deferred replay ended with error: {ex.Message}");
                }
            }
            if (revokeHandler is not null)
            {
                _shareGrantService.OnGrantRevoked -= revokeHandler;
            }
            expiryTimer?.Dispose();
            await _muxManager.RemoveClientAsync(clientId);
            await CloseWebSocketAsync(ws);
        }
    }

    private async Task SendInitFrameAsync(MuxClient client, string clientId)
    {
        // Init frame format: [0xFF][clientId:8][protocolVersion:2][fullClientId:32]
        var initFrame = new byte[MuxProtocol.HeaderSize + 2 + 32];
        initFrame[0] = 0xFF;
        Encoding.ASCII.GetBytes(clientId.AsSpan(0, 8), initFrame.AsSpan(1, 8));
        BitConverter.TryWriteBytes(initFrame.AsSpan(MuxProtocol.HeaderSize, 2), MuxProtocol.ProtocolVersion);
        Encoding.UTF8.GetBytes(clientId, initFrame.AsSpan(MuxProtocol.HeaderSize + 2));
        await client.TrySendAsync(initFrame);
    }

    private static readonly byte[] SgrReset = Encoding.ASCII.GetBytes("\x1b[0m");

    private async Task SendSgrResetFrameAsync(MuxClient client, string sessionId, int cols, int rows)
    {
        var frame = MuxProtocol.CreateOutputFrame(sessionId, 0, cols, rows, SgrReset);
        await client.TrySendAsync(frame);
    }

    private enum InitialReplayMode
    {
        All,
        PriorityOnly,
        NonPriorityOnly
    }

    private static string? ResolveInitialPrioritySessionId(HttpContext context, ShareAccessContext? shareAccess)
    {
        if (shareAccess is not null)
        {
            return shareAccess.SessionId;
        }

        var sessionId = context.Request.Query["activeSessionId"].ToString();
        return string.IsNullOrWhiteSpace(sessionId) ? null : sessionId;
    }

    private static HashSet<string> ResolveInitialVisibleSessionIds(HttpContext context, ShareAccessContext? shareAccess)
    {
        var visibleSessionIds = new HashSet<string>(StringComparer.Ordinal);
        if (shareAccess is not null)
        {
            visibleSessionIds.Add(shareAccess.SessionId);
            return visibleSessionIds;
        }

        var raw = context.Request.Query["visibleSessionIds"].ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return visibleSessionIds;
        }

        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                visibleSessionIds.Add(token);
            }
        }

        return visibleSessionIds;
    }

    private static int? ResolveInitialReplayRows(HttpContext context)
    {
        var raw = context.Request.Query["replayRows"].ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rows) && rows > 0
            ? rows
            : null;
    }

    private IEnumerable<SessionInfo> GetInitialReplaySessions(
        string? allowedSessionId,
        string? prioritySessionId,
        InitialReplayMode replayMode)
    {
        var sessions = _sessionManager.GetAllSessions();
        var deferredSessions = new List<SessionInfo>();
        SessionInfo? prioritySession = null;

        foreach (var sessionInfo in sessions)
        {
            if (allowedSessionId is not null &&
                !string.Equals(sessionInfo.Id, allowedSessionId, StringComparison.Ordinal))
            {
                continue;
            }

            if (prioritySessionId is not null &&
                string.Equals(sessionInfo.Id, prioritySessionId, StringComparison.Ordinal))
            {
                prioritySession = sessionInfo;
                continue;
            }

            if (replayMode != InitialReplayMode.PriorityOnly)
            {
                deferredSessions.Add(sessionInfo);
            }
        }

        if (prioritySession is not null && replayMode != InitialReplayMode.NonPriorityOnly)
        {
            yield return prioritySession;
        }

        foreach (var sessionInfo in deferredSessions)
        {
            yield return sessionInfo;
        }
    }

    private async Task SendInitialBuffersAsync(
        MuxClient client,
        string? allowedSessionId,
        string? prioritySessionId,
        InitialReplayMode replayMode,
        bool quickResumeEnabled,
        int? replayRows,
        CancellationToken ct)
    {
        foreach (var sessionInfo in GetInitialReplaySessions(allowedSessionId, prioritySessionId, replayMode))
        {
            if (!client.ShouldDeliverSession(sessionInfo.Id))
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();
            try
            {
                var snapshot = await _sessionManager.GetBufferAsync(
                    sessionInfo.Id,
                    maxBytes: ResolveReplayMaxBytes(sessionInfo, replayRows, quickResumeEnabled),
                    TerminalReplayReason.ReconnectTailReplay,
                    ct);
                if (snapshot is null || snapshot.Data.Length == 0) continue;

                await SendSgrResetFrameAsync(client, sessionInfo.Id, sessionInfo.Cols, sessionInfo.Rows);

                if (!await SendSnapshotAsync(client, sessionInfo.Id, sessionInfo.Cols, sessionInfo.Rows, snapshot, ct))
                {
                    Log.Warn(() => $"[MuxHandler] Initial sync aborted for {sessionInfo.Id}: queue full");
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error(() => $"[MuxHandler] Failed to get buffer for {sessionInfo.Id}: {ex.Message}");
            }
        }
    }

    private async Task<bool> SendSnapshotAsync(
        MuxClient client,
        string sessionId,
        int cols,
        int rows,
        TtyHostBufferSnapshot snapshot,
        CancellationToken ct)
    {
        for (var offset = 0; offset < snapshot.Data.Length; offset += ReplayFrameChunkBytes)
        {
            ct.ThrowIfCancellationRequested();

            var length = Math.Min(ReplayFrameChunkBytes, snapshot.Data.Length - offset);
            var chunk = snapshot.Data.AsSpan(offset, length);
            var sequenceEndExclusive = snapshot.SequenceStart + (ulong)offset + (ulong)length;

            var useCompression = length > MuxProtocol.CompressionThreshold;
            var maxFrameSize = useCompression
                ? MuxProtocol.CompressedOutputHeaderSize + length + 100
                : MuxProtocol.OutputHeaderSize + length;

            var frameBuffer = ArrayPool<byte>.Shared.Rent(maxFrameSize);
            try
            {
                var frameLength = useCompression
                    ? MuxProtocol.WriteCompressedOutputFrameInto(sessionId, sequenceEndExclusive, cols, rows, chunk, frameBuffer)
                    : MuxProtocol.WriteOutputFrameInto(sessionId, sequenceEndExclusive, cols, rows, chunk, frameBuffer);

                if (!await client.TrySendAsync(frameBuffer, frameLength))
                {
                    return false;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frameBuffer);
            }

            if (offset + length < snapshot.Data.Length)
            {
                await Task.Yield();
            }
        }

        return true;
    }

    private async Task ProcessMessagesAsync(
        WebSocket ws,
        string clientId,
        MuxClient client,
        ShareAccessContext? shareAccess)
    {
        var receiveBuffer = new byte[MuxProtocol.MaxFrameSize];
        var shutdownToken = _shutdownService.Token;

        while (ws.State == WebSocketState.Open && !shutdownToken.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(receiveBuffer, shutdownToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Binary && result.Count >= MuxProtocol.HeaderSize)
            {
                await ProcessFrameAsync(new ReadOnlyMemory<byte>(receiveBuffer, 0, result.Count), client, shareAccess);
            }

            var droppedSessions = client.DrainDroppedSessions();
            if (droppedSessions is not null)
            {
                foreach (var sessionId in droppedSessions)
                {
                    var lossFrame = MuxProtocol.CreateDataLossFrame(sessionId, 0, TerminalReplayReason.MuxOverflow);
                    await client.TrySendAsync(lossFrame);
                }
            }
        }
    }

    private async Task ProcessFrameAsync(
        ReadOnlyMemory<byte> data,
        MuxClient client,
        ShareAccessContext? shareAccess)
    {
        if (!MuxProtocol.TryParseFrame(data.Span, out var type, out var sessionId, out var payload))
        {
            return;
        }

        if (shareAccess is not null &&
            !string.Equals(sessionId, shareAccess.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        switch (type)
        {
            case MuxProtocol.TypeTerminalInput:
                if (shareAccess is not null && !ShareGrantService.CanWrite(shareAccess))
                {
                    return;
                }
                client.SetActiveSession(sessionId);
                var payloadMemory = data.Slice(MuxProtocol.HeaderSize);
                if (payloadMemory.Length < 20)
                {
                    Log.Verbose(() => $"[WS-INPUT] {sessionId}: {BitConverter.ToString(payloadMemory.ToArray())}");
                }
                await _muxManager.HandleInputAsync(client.Id, sessionId, payloadMemory);
                break;

            case MuxProtocol.TypeResize:
                if (shareAccess is not null && !ShareGrantService.CanWrite(shareAccess))
                {
                    return;
                }
                var (cols, rows) = MuxProtocol.ParseResizePayload(payload);
                await _muxManager.HandleResizeAsync(sessionId, cols, rows);
                break;

            case MuxProtocol.TypeBufferRequest:
                var bufferRequest = MuxProtocol.ParseBufferRequestOptions(payload);
                await SendBufferForSessionAsync(
                    client,
                    sessionId,
                    bufferRequest.QuickResume,
                    bufferRequest.ReplayRows);
                break;

            case MuxProtocol.TypeActiveSessionHint:
                client.SetActiveSession(sessionId);
                break;

            case MuxProtocol.TypeVisibleSessionsHint:
                client.SetVisibleSessions(MuxProtocol.ParseVisibleSessionsHintPayload(payload));
                break;

            case MuxProtocol.TypeInputTraceMarker:
                if (MuxProtocol.TryParseInputTraceMarker(payload, out var traceId))
                {
                    _muxManager.BeginInputTrace(client.Id, sessionId, traceId);
                }
                break;

            case MuxProtocol.TypePing:
                await HandlePingAsync(sessionId, data.Slice(MuxProtocol.HeaderSize), client);
                break;

            default:
                Log.Warn(() => $"[Mux] Unknown frame type 0x{type:X2} from {client.Id}");
                break;
        }
    }

    private async Task HandlePingAsync(string sessionId, ReadOnlyMemory<byte> payload, MuxClient client)
    {
        if (payload.Length < 1) return;

        var span = payload.Span;
        var mode = span[0];
        var pingData = payload.Length > 1 ? payload.Slice(1).ToArray() : Array.Empty<byte>();

        if (mode == 0)
        {
            // Server echo: respond with pong + diagnostics (flush delay + server input→output RTT)
            var flushDelay = (ushort)Math.Clamp(client.GetFlushDelay(sessionId), 0, 65535);
            var serverRtt = (ushort)Math.Clamp(_muxManager.GetServerRtt(sessionId), 0, 65535);
            var pong = new byte[MuxProtocol.HeaderSize + 1 + pingData.Length + 4];
            pong[0] = MuxProtocol.TypePong;
            MuxProtocol.WriteSessionId(pong.AsSpan(1, 8), sessionId);
            pong[MuxProtocol.HeaderSize] = 0; // mode = server
            if (pingData.Length > 0)
            {
                pingData.CopyTo(pong.AsSpan(MuxProtocol.HeaderSize + 1));
            }
            // Append diagnostics as uint16 LE: [flushDelay:2][serverRtt:2]
            var diagOffset = MuxProtocol.HeaderSize + 1 + pingData.Length;
            pong[diagOffset] = (byte)(flushDelay & 0xFF);
            pong[diagOffset + 1] = (byte)((flushDelay >> 8) & 0xFF);
            pong[diagOffset + 2] = (byte)(serverRtt & 0xFF);
            pong[diagOffset + 3] = (byte)((serverRtt >> 8) & 0xFF);
            await client.TrySendAsync(pong);
        }
        else if (mode == 1)
        {
            // MTHost echo: forward to mthost via IPC
            await _muxManager.HandlePingAsync(sessionId, pingData, client);
        }
    }

    private async Task SendBufferForSessionAsync(
        MuxClient client,
        string sessionId,
        bool quickResumeRequested,
        int? replayRows)
    {
        try
        {
            var session = _sessionManager.GetSession(sessionId);
            if (session is null)
            {
                Log.Warn(() => $"[MuxHandler] BufferRequest for unknown session: {sessionId}");
                return;
            }

            var quickResume = quickResumeRequested && client.ShouldUseQuickResume();
            var snapshot = await _sessionManager.GetBufferAsync(
                sessionId,
                maxBytes: ResolveReplayMaxBytes(session, replayRows, quickResume),
                quickResume ? TerminalReplayReason.QuickResumeTailReplay : TerminalReplayReason.BufferRefreshTailReplay,
                _shutdownService.Token);
            if (snapshot is null)
            {
                Log.Warn(() => $"MuxHandler: BufferRequest for {sessionId}: IPC returned null (session disconnected?)");
                return;
            }
            if (snapshot.Data.Length > 0)
            {
                await SendSgrResetFrameAsync(client, sessionId, session.Cols, session.Rows);

                await SendSnapshotAsync(client, sessionId, session.Cols, session.Rows, snapshot, CancellationToken.None);
                Log.Verbose(() => $"[MuxHandler] Sent buffer for {sessionId}: {snapshot.Data.Length} bytes");
            }
        }
        catch (Exception ex)
        {
            Log.Error(() => $"[MuxHandler] BufferRequest failed for {sessionId}: {ex.Message}");
        }
    }

    private int? ResolveReplayMaxBytes(SessionInfo session, int? replayRows, bool quickResume)
    {
        var configuredScrollbackBytes = Math.Clamp(
            _settingsService.Load().ScrollbackBytes,
            MidTermSettings.MinScrollbackBytes,
            MidTermSettings.MaxScrollbackBytes);

        if (replayRows is > 0)
        {
            return Math.Min(configuredScrollbackBytes, ResolveViewportReplayBytes(session, replayRows.Value));
        }

        return quickResume ? ResolveQuickResumeBurstBytes(session, configuredScrollbackBytes) : null;
    }

    private static int ResolveQuickResumeBurstBytes(SessionInfo session, int configuredScrollbackBytes)
    {
        var burstBytes = IsLikelyLineBasedShell(session)
            ? QuickResumeShellBurstBytes
            : QuickResumeInteractiveBurstBytes;

        return Math.Min(configuredScrollbackBytes, burstBytes);
    }

    internal static int ResolveViewportReplayBytes(SessionInfo session, int replayRows)
    {
        var cols = Math.Clamp(session.Cols > 0 ? session.Cols : 80, 40, 300);
        var rows = Math.Clamp(replayRows, ReplayRowsMinimum, ReplayRowsMaximum) + ReplayRowsOverscan;
        var estimatedBytes = ((long)cols * rows * ReplayBytesPerCell) + ReplayBytesPadding;
        return (int)Math.Clamp(estimatedBytes, ReplayBytesMinimum, ReplayBytesMaximum);
    }

    private static bool IsLikelyLineBasedShell(SessionInfo session)
    {
        var shellIdentity = NormalizeExecutableIdentity(session.ShellType);
        var foregroundIdentity = NormalizeExecutableIdentity(session.ForegroundName);

        if (string.IsNullOrEmpty(foregroundIdentity))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(shellIdentity) &&
            string.Equals(shellIdentity, foregroundIdentity, StringComparison.Ordinal))
        {
            return true;
        }

        return foregroundIdentity is "pwsh" or "powershell" or "cmd" or "bash" or "zsh" or "sh" or "fish" or "nu";
    }

    private static string NormalizeExecutableIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var candidate = value.Trim().Replace('\\', '/');
        var basename = candidate.Split('/').LastOrDefault() ?? candidate;
        var token = basename.Split(' ', '\t').FirstOrDefault() ?? basename;
        return token.EndsWith(".exe", true, CultureInfo.InvariantCulture)
            ? token[..^4].ToLowerInvariant()
            : token.ToLowerInvariant();
    }

    private async Task CloseWebSocketAsync(WebSocket ws)
    {
        if (ws.State == WebSocketState.Open)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var closeCode = _shutdownService.Token.IsCancellationRequested
                    ? (WebSocketCloseStatus)MuxProtocol.CloseServerShutdown
                    : WebSocketCloseStatus.NormalClosure;
                var closeMessage = _shutdownService.Token.IsCancellationRequested
                    ? "Server shutting down"
                    : null;
                await ws.CloseAsync(closeCode, closeMessage, cts.Token);
            }
            catch
            {
            }
        }
    }
}
