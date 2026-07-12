using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.WebSockets;

public sealed class AppServerControlWebSocketHandler
{
    private readonly TtyHostSessionManager _sessionManager;
    private readonly SessionSupervisorService _sessionSupervisor;
    private readonly SessionAppServerControlRuntimeService _appServerControlRuntime;
    private readonly SessionCodexHandoffService _codexHandoff;
    private readonly AiCliProfileService _aiCliProfileService;
    private readonly AuthService _authService;
    private readonly ShutdownService _shutdownService;

    public AppServerControlWebSocketHandler(
        TtyHostSessionManager sessionManager,
        SessionSupervisorService sessionSupervisor,
        SessionAppServerControlRuntimeService appServerControlRuntime,
        SessionCodexHandoffService codexHandoff,
        AiCliProfileService aiCliProfileService,
        AuthService authService,
        ShutdownService shutdownService)
    {
        _sessionManager = sessionManager;
        _sessionSupervisor = sessionSupervisor;
        _appServerControlRuntime = appServerControlRuntime;
        _codexHandoff = codexHandoff;
        _aiCliProfileService = aiCliProfileService;
        _authService = authService;
        _shutdownService = shutdownService;
    }

    public async Task HandleAsync(HttpContext context)
    {
        var authentication = _authService.AuthenticateRequestWithContext(context.Request);
        if (authentication.Method == RequestAuthMethod.None)
        {
            context.Response.StatusCode = 401;
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        using var authLease = _authService.TrackWebSocketAuthentication(authentication, ws);
        var sendLock = new SemaphoreSlim(1, 1);
        var subscriptions = new Dictionary<string, AppServerControlSocketSubscription>(StringComparer.Ordinal);
        var shutdownToken = _shutdownService.Token;

        async Task SendJsonAsync<T>(T payload, JsonTypeInfo<T> typeInfo)
        {
            if (ws.State != WebSocketState.Open)
            {
                return;
            }

            await sendLock.WaitAsync(shutdownToken).ConfigureAwait(false);
            try
            {
                if (ws.State != WebSocketState.Open)
                {
                    return;
                }

                var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, typeInfo);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, shutdownToken).ConfigureAwait(false);
            }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Log.Verbose(() => $"[AppServerControlWS] SendJsonAsync failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                sendLock.Release();
            }
        }

        async Task SendErrorAsync(string? id, string? action, string? sessionId, string message)
        {
            await SendJsonAsync(
                new AppServerControlWsErrorMessage
                {
                    Id = id,
                    Action = action,
                    SessionId = sessionId,
                    Message = message
                },
                AppJsonContext.Default.AppServerControlWsErrorMessage).ConfigureAwait(false);
        }

        async Task RemoveSubscriptionAsync(string sessionId)
        {
            if (subscriptions.Remove(sessionId, out var existing))
            {
                existing.Dispose();
            }

            await SendJsonAsync(
                new AppServerControlWsAckMessage
                {
                    Id = $"unsubscribe:{sessionId}",
                    Action = "unsubscribe",
                    SessionId = sessionId
                },
                AppJsonContext.Default.AppServerControlWsAckMessage).ConfigureAwait(false);
        }

        async Task ReplaceSubscriptionAsync(
            string sessionId,
            long afterSequence,
            AppServerControlHistoryWindowRequest? historyWindow)
        {
            if (subscriptions.Remove(sessionId, out var existing))
            {
                existing.Dispose();
            }

            var requestedWindow = historyWindow is null
                ? null
                : new AppServerControlHistoryWindowRequest
                {
                    StartIndex = historyWindow.StartIndex,
                    Count = historyWindow.Count,
                    ViewportWidth = historyWindow.ViewportWidth,
                    WindowRevision = historyWindow.WindowRevision
                };

            AppServerControlHistoryWindowResponse? currentHistoryWindow;
            try
            {
                currentHistoryWindow = await _appServerControlRuntime.GetHistoryWindowAsync(
                    sessionId,
                    requestedWindow?.StartIndex,
                    requestedWindow?.Count,
                    requestedWindow?.ViewportWidth,
                    shutdownToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                Log.Verbose(() => $"[AppServerControlWS] Subscription refused for {sessionId}: {ex.Message}");
                await SendErrorAsync(null, "subscribe", sessionId, ex.Message).ConfigureAwait(false);
                return;
            }

            var state = AppServerControlSocketSubscription.Create(_appServerControlRuntime, sessionId, shutdownToken);
            var cancellation = state.Cancellation;
            var subscription = state.Subscription;
            subscriptions[sessionId] = state;
            state.ReaderTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var patch in subscription.Reader.ReadAllAsync(cancellation.Token).ConfigureAwait(false))
                    {
                        await SendJsonAsync(
                            new AppServerControlWsHistoryPatchMessage
                            {
                                SessionId = sessionId,
                                Patch = patch
                            },
                            AppJsonContext.Default.AppServerControlWsHistoryPatchMessage).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log.Verbose(() => $"[AppServerControlWS] Stream subscription failed for {sessionId}: {ex.Message}");
                }
            }, CancellationToken.None);

            if (currentHistoryWindow is not null)
            {
                afterSequence = Math.Max(afterSequence, currentHistoryWindow.LatestSequence);
                await SendJsonAsync(
                    new AppServerControlWsHistoryWindowMessage
                    {
                        SessionId = sessionId,
                        WindowRevision = requestedWindow?.WindowRevision,
                        HistoryWindow = currentHistoryWindow
                    },
                    AppJsonContext.Default.AppServerControlWsHistoryWindowMessage).ConfigureAwait(false);
            }

            await SendJsonAsync(
                new AppServerControlWsAckMessage
                {
                    Id = $"subscribe:{sessionId}",
                    Action = "subscribe",
                    SessionId = sessionId
                },
                AppJsonContext.Default.AppServerControlWsAckMessage).ConfigureAwait(false);
        }

        try
        {
            var buffer = new byte[8192];
            var messageBuffer = new List<byte>();

            while (ws.State == WebSocketState.Open && !shutdownToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await ws.ReceiveAsync(buffer, shutdownToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                messageBuffer.AddRange(buffer.AsSpan(0, result.Count).ToArray());
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                messageBuffer.Clear();

                try
                {
                    using var document = JsonDocument.Parse(json);
                    var root = document.RootElement;
                    var type = root.TryGetProperty("type", out var typeProperty)
                        ? typeProperty.GetString()
                        : null;

                    switch (type)
                    {
                        case "subscribe":
                        {
                            var message = JsonSerializer.Deserialize(root.GetRawText(), AppJsonContext.Default.AppServerControlWsSubscriptionMessage);
                            if (message is null || string.IsNullOrWhiteSpace(message.SessionId))
                            {
                                continue;
                            }

                            await ReplaceSubscriptionAsync(
                                message.SessionId,
                                Math.Max(0, message.AfterSequence),
                                message.HistoryWindow).ConfigureAwait(false);
                            continue;
                        }
                        case "unsubscribe":
                        {
                            var message = JsonSerializer.Deserialize(root.GetRawText(), AppJsonContext.Default.AppServerControlWsSubscriptionMessage);
                            if (message is null || string.IsNullOrWhiteSpace(message.SessionId))
                            {
                                continue;
                            }

                            await RemoveSubscriptionAsync(message.SessionId).ConfigureAwait(false);
                            continue;
                        }
                        case "request":
                        {
                            var request = JsonSerializer.Deserialize(root.GetRawText(), AppJsonContext.Default.AppServerControlWsRequestMessage);
                            if (request is null)
                            {
                                continue;
                            }

                            await HandleRequestAsync(
                                request,
                                SendJsonAsync,
                                SendJsonAsync,
                                SendJsonAsync,
                                SendJsonAsync,
                                SendErrorAsync).ConfigureAwait(false);
                            continue;
                        }
                    }
                }
                catch (JsonException ex)
                {
                    Log.Verbose(() => $"[AppServerControlWS] Failed to parse client message: {ex.Message}");
                }
            }
        }
        finally
        {
            foreach (var subscription in subscriptions.Values)
            {
                subscription.Dispose();
            }

            subscriptions.Clear();
            sendLock.Dispose();

            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var closeCode = shutdownToken.IsCancellationRequested
                        ? (WebSocketCloseStatus)MuxProtocol.CloseServerShutdown
                        : WebSocketCloseStatus.NormalClosure;
                    await ws.CloseAsync(closeCode, shutdownToken.IsCancellationRequested ? "Server shutting down" : null, cts.Token).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    private async Task HandleRequestAsync(
        AppServerControlWsRequestMessage request,
        Func<AppServerControlWsAckMessage, JsonTypeInfo<AppServerControlWsAckMessage>, Task> sendAck,
        Func<AppServerControlWsHistoryWindowMessage, JsonTypeInfo<AppServerControlWsHistoryWindowMessage>, Task> sendHistoryWindow,
        Func<AppServerControlWsTurnStartedMessage, JsonTypeInfo<AppServerControlWsTurnStartedMessage>, Task> sendTurnStarted,
        Func<AppServerControlWsCommandAcceptedMessage, JsonTypeInfo<AppServerControlWsCommandAcceptedMessage>, Task> sendCommandAccepted,
        Func<string?, string?, string?, string, Task> sendError)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId) || _sessionManager.GetSession(request.SessionId) is null)
        {
            await sendError(request.Id, request.Action, request.SessionId, "App Server Controller session was not found.").ConfigureAwait(false);
            return;
        }

        try
        {
            switch (request.Action)
            {
                case "attach":
                    await EnsureAppServerControlAttachedAsync(request.SessionId, CancellationToken.None).ConfigureAwait(false);
                    await sendAck(
                        new AppServerControlWsAckMessage
                        {
                            Id = request.Id,
                            Action = request.Action,
                            SessionId = request.SessionId
                        },
                        AppJsonContext.Default.AppServerControlWsAckMessage).ConfigureAwait(false);
                    break;

                case "detach":
                    await DetachAppServerControlAsync(request.SessionId, CancellationToken.None).ConfigureAwait(false);
                    await sendAck(
                        new AppServerControlWsAckMessage
                        {
                            Id = request.Id,
                            Action = request.Action,
                            SessionId = request.SessionId
                        },
                        AppJsonContext.Default.AppServerControlWsAckMessage).ConfigureAwait(false);
                    break;

                case "history.window.get":
                {
                    var historyWindow = await _appServerControlRuntime.GetHistoryWindowAsync(
                        request.SessionId,
                        request.HistoryWindow?.StartIndex,
                        request.HistoryWindow?.Count,
                        request.HistoryWindow?.ViewportWidth,
                        CancellationToken.None).ConfigureAwait(false);
                    if (historyWindow is null)
                    {
                        await sendError(request.Id, request.Action, request.SessionId, "App Server Controller history window is not available.").ConfigureAwait(false);
                        return;
                    }

                    await sendHistoryWindow(
                        new AppServerControlWsHistoryWindowMessage
                        {
                            Id = request.Id,
                            SessionId = request.SessionId,
                            WindowRevision = request.HistoryWindow?.WindowRevision,
                            HistoryWindow = historyWindow
                        },
                        AppJsonContext.Default.AppServerControlWsHistoryWindowMessage).ConfigureAwait(false);
                    break;
                }

                case "turn.submit":
                {
                    var session = await EnsureAppServerControlAttachedAsync(request.SessionId, CancellationToken.None).ConfigureAwait(false);
                    var response = await _appServerControlRuntime.StartTurnAsync(
                        request.SessionId,
                        request.Turn ?? new AppServerControlTurnRequest(),
                        CancellationToken.None).ConfigureAwait(false);
                    await sendTurnStarted(
                        new AppServerControlWsTurnStartedMessage
                        {
                            Id = request.Id,
                            SessionId = session.Id,
                            Response = response
                        },
                        AppJsonContext.Default.AppServerControlWsTurnStartedMessage).ConfigureAwait(false);
                    break;
                }

                case "turn.interrupt":
                {
                    var response = await _appServerControlRuntime.InterruptTurnAsync(
                        request.SessionId,
                        request.Interrupt ?? new AppServerControlInterruptRequest(),
                        CancellationToken.None).ConfigureAwait(false);
                    await sendCommandAccepted(
                        new AppServerControlWsCommandAcceptedMessage
                        {
                            Id = request.Id,
                            SessionId = request.SessionId,
                            Response = response
                        },
                        AppJsonContext.Default.AppServerControlWsCommandAcceptedMessage).ConfigureAwait(false);
                    break;
                }

                case "thread.goal.set":
                {
                    var response = await _appServerControlRuntime.SetGoalAsync(
                        request.SessionId,
                        request.GoalSet ?? new AppServerControlGoalSetRequest(),
                        CancellationToken.None).ConfigureAwait(false);
                    await sendCommandAccepted(
                        new AppServerControlWsCommandAcceptedMessage
                        {
                            Id = request.Id,
                            SessionId = request.SessionId,
                            Response = response
                        },
                        AppJsonContext.Default.AppServerControlWsCommandAcceptedMessage).ConfigureAwait(false);
                    break;
                }

                case "request.approve":
                {
                    var response = await _appServerControlRuntime.ResolveRequestAsync(
                        request.SessionId,
                        request.RequestId ?? string.Empty,
                        new AppServerControlRequestDecisionRequest { Decision = "accept" },
                        CancellationToken.None).ConfigureAwait(false);
                    await sendCommandAccepted(
                        new AppServerControlWsCommandAcceptedMessage
                        {
                            Id = request.Id,
                            SessionId = request.SessionId,
                            Response = response
                        },
                        AppJsonContext.Default.AppServerControlWsCommandAcceptedMessage).ConfigureAwait(false);
                    break;
                }

                case "request.decline":
                case "request.resolve":
                {
                    var decision = request.RequestDecision ?? new AppServerControlRequestDecisionRequest
                    {
                        Decision = request.Action == "request.decline" ? "decline" : "accept"
                    };
                    if (request.Action == "request.decline" && string.IsNullOrWhiteSpace(decision.Decision))
                    {
                        decision.Decision = "decline";
                    }

                    var response = await _appServerControlRuntime.ResolveRequestAsync(
                        request.SessionId,
                        request.RequestId ?? string.Empty,
                        decision,
                        CancellationToken.None).ConfigureAwait(false);
                    await sendCommandAccepted(
                        new AppServerControlWsCommandAcceptedMessage
                        {
                            Id = request.Id,
                            SessionId = request.SessionId,
                            Response = response
                        },
                        AppJsonContext.Default.AppServerControlWsCommandAcceptedMessage).ConfigureAwait(false);
                    break;
                }

                case "userInput.resolve":
                {
                    var response = await _appServerControlRuntime.ResolveUserInputAsync(
                        request.SessionId,
                        request.RequestId ?? string.Empty,
                        request.UserInputAnswer ?? new AppServerControlUserInputAnswerRequest(),
                        CancellationToken.None).ConfigureAwait(false);
                    await sendCommandAccepted(
                        new AppServerControlWsCommandAcceptedMessage
                        {
                            Id = request.Id,
                            SessionId = request.SessionId,
                            Response = response
                        },
                        AppJsonContext.Default.AppServerControlWsCommandAcceptedMessage).ConfigureAwait(false);
                    break;
                }

                default:
                    await sendError(request.Id, request.Action, request.SessionId, $"Unknown App Server Controller action '{request.Action}'.").ConfigureAwait(false);
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            await sendError(request.Id, request.Action, request.SessionId, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"App Server Controller WebSocket request '{request.Action}' failed for {request.SessionId}: {ex.Message}");
            await sendError(request.Id, request.Action, request.SessionId, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task<SessionInfoDto> EnsureAppServerControlAttachedAsync(string sessionId, CancellationToken ct)
    {
        var session = GetSessionDto(sessionId);
        var resumeThreadId = session.AppServerControlResumeThreadId;
        if (!session.AppServerControlOnly &&
            _aiCliProfileService.NormalizeProfile(null, session) == AiCliProfileService.CodexProfile)
        {
            resumeThreadId = await _codexHandoff.PrepareForAppServerControlAsync(session, ct).ConfigureAwait(false);
        }

        var attached = await _appServerControlRuntime.EnsureAttachedAsync(sessionId, session, resumeThreadId, ct).ConfigureAwait(false);
        if (!attached && !_appServerControlRuntime.HasHistory(sessionId))
        {
            throw new InvalidOperationException("App Server Controller native runtime is not available for this session.");
        }

        return session;
    }

    private async Task DetachAppServerControlAsync(string sessionId, CancellationToken ct)
    {
        var session = GetSessionDto(sessionId);
        if (session.AppServerControlOnly ||
            _aiCliProfileService.NormalizeProfile(null, session) != AiCliProfileService.CodexProfile)
        {
            await _appServerControlRuntime.DetachAsync(sessionId, ct).ConfigureAwait(false);
            return;
        }

        await _codexHandoff.RestoreTerminalAsync(session, ct).ConfigureAwait(false);
    }

    private SessionInfoDto GetSessionDto(string sessionId)
    {
        var session = _sessionManager.GetSessionList().Sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal))
                      ?? throw new InvalidOperationException("App Server Controller session was not found.");
        session.Supervisor = _sessionSupervisor.Describe(session);
        session.HasAppServerControlHistory = _appServerControlRuntime.HasHistory(session.Id);
        return session;
    }

    private sealed class AppServerControlSocketSubscription : IDisposable
    {
        private AppServerControlSocketSubscription(AppServerControlHistoryPatchSubscription subscription, CancellationTokenSource cancellation)
        {
            Subscription = subscription;
            Cancellation = cancellation;
        }

        public static AppServerControlSocketSubscription Create(
            SessionAppServerControlRuntimeService appServerControlRuntime,
            string sessionId,
            CancellationToken shutdownToken)
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
            var subscription = appServerControlRuntime.SubscribeHistoryPatches(sessionId, cancellation.Token);
            return new AppServerControlSocketSubscription(subscription, cancellation);
        }

        public AppServerControlHistoryPatchSubscription Subscription { get; }
        public CancellationTokenSource Cancellation { get; }
        public Task? ReaderTask { get; set; }

        public void Dispose()
        {
            Cancellation.Cancel();
            Subscription.Dispose();
            Cancellation.Dispose();
        }
    }
}
