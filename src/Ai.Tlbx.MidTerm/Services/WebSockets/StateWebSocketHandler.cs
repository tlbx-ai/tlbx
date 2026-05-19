using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Models;
using Ai.Tlbx.MidTerm.Models.Update;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Share;
using Ai.Tlbx.MidTerm.Services.Tmux;
using Ai.Tlbx.MidTerm.Settings;

using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Services.Updates;
using Ai.Tlbx.MidTerm.Models.Auth;
using Ai.Tlbx.MidTerm.Models.Certificates;
using Ai.Tlbx.MidTerm.Models.Files;
using Ai.Tlbx.MidTerm.Models.History;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Models.System;

namespace Ai.Tlbx.MidTerm.Services.WebSockets;

public sealed class StateWebSocketHandler
{
    private readonly TtyHostSessionManager _sessionManager;
    private readonly SessionSupervisorService _sessionSupervisor;
    private readonly SessionAppServerControlRuntimeService _appServerControlRuntime;
    private readonly UpdateService _updateService;
    private readonly SettingsService _settingsService;
    private readonly AuthService _authService;
    private readonly ShareGrantService _shareGrantService;
    private readonly ShutdownService _shutdownService;
    private readonly MainBrowserService _mainBrowserService;
    private readonly SessionLayoutStateService _sessionLayoutStateService;
    private readonly ManagerBarQueueService _managerBarQueueService;
    private readonly TmuxLayoutBridge? _tmuxLayoutBridge;
    private readonly BrowserUiBridge? _browserUiBridge;

    public StateWebSocketHandler(
        TtyHostSessionManager sessionManager,
        SessionSupervisorService sessionSupervisor,
        SessionAppServerControlRuntimeService appServerControlRuntime,
        UpdateService updateService,
        SettingsService settingsService,
        AuthService authService,
        ShareGrantService shareGrantService,
        ShutdownService shutdownService,
        MainBrowserService mainBrowserService,
        SessionLayoutStateService sessionLayoutStateService,
        ManagerBarQueueService managerBarQueueService,
        TmuxLayoutBridge? tmuxLayoutBridge = null,
        BrowserUiBridge? browserUiBridge = null)
    {
        _sessionManager = sessionManager;
        _sessionSupervisor = sessionSupervisor;
        _appServerControlRuntime = appServerControlRuntime;
        _updateService = updateService;
        _settingsService = settingsService;
        _authService = authService;
        _shareGrantService = shareGrantService;
        _shutdownService = shutdownService;
        _mainBrowserService = mainBrowserService;
        _sessionLayoutStateService = sessionLayoutStateService;
        _managerBarQueueService = managerBarQueueService;
        _tmuxLayoutBridge = tmuxLayoutBridge;
        _browserUiBridge = browserUiBridge;
    }

    public async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        var shareAccess = RequestAccessContext.GetShareAccess(context);
        var isShareConnection = string.Equals(path, "/ws/share/state", StringComparison.Ordinal);

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
        var sendLock = new SemaphoreSlim(1, 1);
        Timer? expiryTimer = null;
        Action<string>? revokeHandler = null;
        UpdateInfo? lastUpdate = null;
        var shutdownToken = _shutdownService.Token;
        using var stateSendCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        var stateSendToken = stateSendCts.Token;
        var stateSendGate = new object();
        var stateSendPending = false;
        var stateSendInFlight = false;
        Task? stateSendTask = null;

        async Task SendJsonAsync<T>(T payload, JsonTypeInfo<T> typeInfo)
        {
            if (ws.State != WebSocketState.Open) return;
            await sendLock.WaitAsync(shutdownToken);
            try
            {
                if (ws.State != WebSocketState.Open) return;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, typeInfo);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, shutdownToken);
            }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Verbose(() => $"[StateWS] SendJsonAsync failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                sendLock.Release();
            }
        }

        async Task SendStateAsync()
        {
            var sessionList = GetSessionListDto();
            if (shareAccess is not null)
            {
                sessionList.Sessions = sessionList.Sessions
                    .Where(s => string.Equals(s.Id, shareAccess.SessionId, StringComparison.Ordinal))
                    .ToList();
            }
            var state = new StateUpdate
            {
                Sessions = sessionList,
                Update = shareAccess is null ? lastUpdate : null,
                Layout = shareAccess is null
                    ? _sessionLayoutStateService.GetSnapshot(sessionList.Sessions.Select(s => s.Id))
                    : null,
                ManagerBarQueue = shareAccess is null
                    ? _managerBarQueueService.GetSnapshot(sessionList.Sessions.Select(s => s.Id)).ToList()
                    : []
            };
            await SendJsonAsync(state, AppJsonContext.Default.StateUpdate);
        }

        SessionListDto GetSessionListDto()
        {
            var response = _sessionManager.GetSessionList();
            foreach (var session in response.Sessions)
            {
                session.Supervisor = _sessionSupervisor.Describe(session);
                session.HasAppServerControlHistory = _appServerControlRuntime.HasHistory(session.Id);
            }

            return response;
        }

        async Task SendCommandResponseAsync(string id, bool success, object? data = null, string? error = null)
        {
            var response = new WsCommandResponse
            {
                Type = "response",
                Id = id,
                Success = success,
                Data = data,
                Error = error
            };
            await SendJsonAsync(response, AppJsonContext.Default.WsCommandResponse);
        }

        async Task SendStateWithRetryAsync()
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await SendStateAsync();
                    return;
                }
                catch (WebSocketException) when (attempt < 2)
                {
                    await Task.Delay(100, shutdownToken);
                }
                catch (Exception ex)
                {
                    Log.Verbose(() => $"[StateWS] SendStateWithRetry failed: {ex.GetType().Name}: {ex.Message}");
                    return;
                }
            }
        }

        async Task RunCoalescedStateSendAsync()
        {
            try
            {
                while (!stateSendToken.IsCancellationRequested)
                {
                    await SendStateWithRetryAsync().ConfigureAwait(false);

                    lock (stateSendGate)
                    {
                        if (!stateSendPending)
                        {
                            stateSendInFlight = false;
                            stateSendTask = null;
                            return;
                        }

                        stateSendPending = false;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Verbose(() => $"[StateWS] Coalesced state send failed: {ex.GetType().Name}: {ex.Message}");
            }

            lock (stateSendGate)
            {
                stateSendInFlight = false;
                stateSendTask = null;
            }
        }

        void RequestCoalescedStateSend()
        {
            lock (stateSendGate)
            {
                if (stateSendInFlight)
                {
                    stateSendPending = true;
                    return;
                }

                stateSendPending = false;
                stateSendInFlight = true;
                stateSendTask = Task.Run(RunCoalescedStateSendAsync, CancellationToken.None);
            }
        }

        void OnStateChange()
        {
            RequestCoalescedStateSend();
        }

        void OnUpdateAvailable(UpdateInfo update)
        {
            lastUpdate = update;
            RequestCoalescedStateSend();
        }

        void OnLayoutChanged()
        {
            RequestCoalescedStateSend();
        }

        void OnManagerBarQueueChanged()
        {
            RequestCoalescedStateSend();
        }

        var connectionToken = new object();
        var browserId = BuildBrowserConnectionId(context.Request);

        async Task SendMainBrowserStatusAsync()
        {
            var status = new MainBrowserStatusMessage
            {
                IsMain = _mainBrowserService.IsMain(browserId),
                ShowButton = _mainBrowserService.ShouldShowButton(browserId)
            };
            await SendJsonAsync(status, AppJsonContext.Default.MainBrowserStatusMessage);
        }

        void OnMainBrowserChanged()
        {
            _ = SendMainBrowserStatusAsync();
        }

        var sessionListenerId = _sessionManager.AddStateListener(OnStateChange);
        var updateListenerId = _updateService.AddUpdateListener(OnUpdateAvailable);
        _sessionLayoutStateService.OnChanged += OnLayoutChanged;
        _managerBarQueueService.OnChanged += OnManagerBarQueueChanged;
        var browserUiListenerId = Guid.NewGuid().ToString("N");

        void OnDockRequested(string newSessionId, string relativeToSessionId, string position)
        {
            var instruction = new TmuxDockInstruction
            {
                NewSessionId = newSessionId,
                RelativeToSessionId = relativeToSessionId,
                Position = position
            };
            _ = SendJsonAsync(instruction, TmuxJsonContext.Default.TmuxDockInstruction);
        }

        void OnFocusRequested(string sessionId)
        {
            var instruction = new TmuxFocusInstruction { SessionId = sessionId };
            _ = SendJsonAsync(instruction, TmuxJsonContext.Default.TmuxFocusInstruction);
        }

        void OnSwapRequested(string sessionIdA, string sessionIdB)
        {
            var instruction = new TmuxSwapInstruction { SessionIdA = sessionIdA, SessionIdB = sessionIdB };
            _ = SendJsonAsync(instruction, TmuxJsonContext.Default.TmuxSwapInstruction);
        }

        if (shareAccess is null && _tmuxLayoutBridge is not null)
        {
            _tmuxLayoutBridge.OnDockRequested += OnDockRequested;
            _tmuxLayoutBridge.OnFocusRequested += OnFocusRequested;
            _tmuxLayoutBridge.OnSwapRequested += OnSwapRequested;
        }

        void OnBrowserDetach(string? sessionId, string? previewName)
        {
            var instruction = new Models.Browser.BrowserUiInstruction
            {
                Command = "detach",
                SessionId = sessionId,
                PreviewName = previewName
            };
            _ = SendJsonAsync(instruction, AppJsonContext.Default.BrowserUiInstruction);
        }

        void OnBrowserDock(string? sessionId, string? previewName)
        {
            var instruction = new Models.Browser.BrowserUiInstruction
            {
                Command = "dock",
                SessionId = sessionId,
                PreviewName = previewName
            };
            _ = SendJsonAsync(instruction, AppJsonContext.Default.BrowserUiInstruction);
        }

        void OnBrowserViewport(string? sessionId, string? previewName, int width, int height)
        {
            var instruction = new Models.Browser.BrowserUiInstruction
            {
                Command = "viewport",
                SessionId = sessionId,
                PreviewName = previewName,
                Width = width,
                Height = height
            };
            _ = SendJsonAsync(instruction, AppJsonContext.Default.BrowserUiInstruction);
        }

        void OnBrowserOpen(string? sessionId, string? previewName, string url, bool activateSession)
        {
            var instruction = new Models.Browser.BrowserUiInstruction
            {
                Command = "open",
                Url = url,
                SessionId = sessionId,
                PreviewName = previewName,
                ActivateSession = activateSession
            };
            _ = SendJsonAsync(instruction, AppJsonContext.Default.BrowserUiInstruction);
        }

        if (shareAccess is null)
        {
            _browserUiBridge?.RegisterListener(
                connectionId: browserUiListenerId,
                browserId: browserId,
                detach: OnBrowserDetach,
                dock: OnBrowserDock,
                viewport: OnBrowserViewport,
                open: OnBrowserOpen);
        }

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

            lastUpdate = shareAccess is null ? _updateService.LatestUpdate : null;
            await SendStateAsync();
            if (shareAccess is null)
            {
                _mainBrowserService.OnMainBrowserChanged += OnMainBrowserChanged;
                _mainBrowserService.Register(browserId, connectionToken);
                await SendMainBrowserStatusAsync();
            }

            var buffer = new byte[8192];
            var messageBuffer = new List<byte>();

            while (ws.State == WebSocketState.Open && !shutdownToken.IsCancellationRequested)
            {
                try
                {
                    var result = await ws.ReceiveAsync(buffer, shutdownToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var existingLen = messageBuffer.Count;
                        CollectionsMarshal.SetCount(messageBuffer, existingLen + result.Count);
                        buffer.AsSpan(0, result.Count).CopyTo(
                            CollectionsMarshal.AsSpan(messageBuffer).Slice(existingLen));

                        if (result.EndOfMessage)
                        {
                            var messageJson = Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(messageBuffer));
                            messageBuffer.Clear();

                            await HandleCommandAsync(messageJson, SendCommandResponseAsync, browserId, connectionToken, shareAccess, shutdownToken);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    break;
                }
            }
        }
        finally
        {
            stateSendCts.Cancel();
            Task? pendingStateSendTask;
            lock (stateSendGate)
            {
                pendingStateSendTask = stateSendTask;
            }

            if (pendingStateSendTask is not null)
            {
                try
                {
                    await pendingStateSendTask.WaitAsync(TimeSpan.FromSeconds(2), shutdownToken);
                }
                catch
                {
                }
            }

            _sessionManager.RemoveStateListener(sessionListenerId);
            _updateService.RemoveUpdateListener(updateListenerId);
            _sessionLayoutStateService.OnChanged -= OnLayoutChanged;
            _managerBarQueueService.OnChanged -= OnManagerBarQueueChanged;
            if (shareAccess is null)
            {
                _mainBrowserService.OnMainBrowserChanged -= OnMainBrowserChanged;
                _mainBrowserService.Unregister(browserId, connectionToken);
            }

            if (shareAccess is null && _tmuxLayoutBridge is not null)
            {
                _tmuxLayoutBridge.OnDockRequested -= OnDockRequested;
                _tmuxLayoutBridge.OnFocusRequested -= OnFocusRequested;
                _tmuxLayoutBridge.OnSwapRequested -= OnSwapRequested;
            }

            if (shareAccess is null && _browserUiBridge is not null)
            {
                _browserUiBridge.UnregisterListener(browserUiListenerId);
            }

            if (revokeHandler is not null)
            {
                _shareGrantService.OnGrantRevoked -= revokeHandler;
            }
            expiryTimer?.Dispose();
            sendLock.Dispose();

            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var closeCode = shutdownToken.IsCancellationRequested
                        ? (WebSocketCloseStatus)MuxProtocol.CloseServerShutdown
                        : WebSocketCloseStatus.NormalClosure;
                    var closeMessage = shutdownToken.IsCancellationRequested
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

    private async Task HandleCommandAsync(
        string json,
        Func<string, bool, object?, string?, Task> sendResponse,
        string browserId,
        object connectionToken,
        ShareAccessContext? shareAccess,
        CancellationToken ct)
    {
        WsCommand? cmd;
        try
        {
            cmd = JsonSerializer.Deserialize(json, AppJsonContext.Default.WsCommand);
        }
        catch
        {
            return;
        }

        if (cmd is null || cmd.Type != "command" || string.IsNullOrEmpty(cmd.Id))
        {
            return;
        }

        if (shareAccess is not null)
        {
            await sendResponse(cmd.Id, false, null, "Shared sessions do not allow control commands");
            return;
        }

        try
        {
            switch (cmd.Action)
            {
                case "session.create":
                    await HandleSessionCreateAsync(cmd, sendResponse, ct);
                    break;

                case "session.close":
                    await HandleSessionCloseAsync(cmd, sendResponse, ct);
                    break;

                case "session.rename":
                    await HandleSessionRenameAsync(cmd, sendResponse, ct);
                    break;

                case "session.reorder":
                    await HandleSessionReorderAsync(cmd, sendResponse);
                    break;

                case "settings.save":
                    await HandleSettingsSaveAsync(cmd, sendResponse);
                    break;

                case "browser.claimMain":
                    _mainBrowserService.Claim(browserId);
                    await sendResponse(cmd.Id, true, null, null);
                    break;

                case "browser.releaseMain":
                    _mainBrowserService.Release(browserId);
                    await sendResponse(cmd.Id, true, null, null);
                    break;

                case "browser.setActivity":
                    _mainBrowserService.UpdateActivity(browserId, connectionToken, cmd.Payload?.IsActive == true);
                    await sendResponse(cmd.Id, true, null, null);
                    break;

                default:
                    await sendResponse(cmd.Id, false, null, $"Unknown action: {cmd.Action}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(() => $"Command handler error for {cmd.Action}: {ex.Message}");
            await sendResponse(cmd.Id, false, null, ex.Message);
        }
    }

    private async Task HandleSessionCreateAsync(
        WsCommand cmd,
        Func<string, bool, object?, string?, Task> sendResponse,
        CancellationToken ct)
    {
        var payload = cmd.Payload;
        var cols = payload?.Cols ?? 80;
        var rows = payload?.Rows ?? 24;
        var workingDir = payload?.WorkingDirectory;

        var creation = await _sessionManager.CreateSessionDetailedAsync(payload?.Shell, cols, rows, workingDir, ct);

        if (!creation.Succeeded)
        {
            var failure = creation.Failure;
            var error = failure?.Message ?? "Failed to create session";
            if (!string.IsNullOrWhiteSpace(failure?.Detail))
            {
                error += $"\n\n{failure.Detail}";
            }
            await sendResponse(cmd.Id, false, null, error);
            return;
        }

        var session = creation.Session!;
        _sessionManager.SetLaunchOrigin(session.Id, SessionLaunchOrigins.AdHoc);
        var data = new WsSessionCreatedData
        {
            Id = session.Id,
            Pid = session.Pid,
            ShellType = session.ShellType
        };

        await sendResponse(cmd.Id, true, data, null);
    }

    private async Task HandleSessionCloseAsync(
        WsCommand cmd,
        Func<string, bool, object?, string?, Task> sendResponse,
        CancellationToken ct)
    {
        var sessionId = cmd.Payload?.SessionId;
        if (string.IsNullOrEmpty(sessionId))
        {
            await sendResponse(cmd.Id, false, null, "sessionId required");
            return;
        }

        var closed = await _sessionManager.CloseSessionAsync(sessionId, ct);
        await sendResponse(cmd.Id, closed, null, closed ? null : "Session not found");
    }

    private async Task HandleSessionRenameAsync(
        WsCommand cmd,
        Func<string, bool, object?, string?, Task> sendResponse,
        CancellationToken ct)
    {
        var sessionId = cmd.Payload?.SessionId;
        if (string.IsNullOrEmpty(sessionId))
        {
            await sendResponse(cmd.Id, false, null, "sessionId required");
            return;
        }

        var name = cmd.Payload?.Name;
        var isManual = cmd.Payload?.Auto != true;
        var renamed = await _sessionManager.SetSessionNameAsync(sessionId, name, isManual, ct);
        await sendResponse(cmd.Id, renamed, null, renamed ? null : "Session not found");
    }

    private async Task HandleSessionReorderAsync(WsCommand cmd, Func<string, bool, object?, string?, Task> sendResponse)
    {
        var sessionIds = cmd.Payload?.SessionIds;
        if (sessionIds is null || sessionIds.Count == 0)
        {
            await sendResponse(cmd.Id, false, null, "sessionIds required");
            return;
        }

        var reordered = _sessionManager.ReorderSessions(sessionIds);
        await sendResponse(cmd.Id, reordered, null, reordered ? null : "Invalid session IDs");
    }

    private async Task HandleSettingsSaveAsync(WsCommand cmd, Func<string, bool, object?, string?, Task> sendResponse)
    {
        var publicSettings = cmd.Payload?.Settings;
        if (publicSettings is null)
        {
            await sendResponse(cmd.Id, false, null, "settings required");
            return;
        }

        try
        {
            var currentSettings = _settingsService.Load();
            publicSettings.ApplyTo(currentSettings);
            _settingsService.Save(currentSettings);
            await sendResponse(cmd.Id, true, null, null);
        }
        catch (ArgumentException ex)
        {
            await sendResponse(cmd.Id, false, null, ex.Message);
        }
    }

    private static string BuildBrowserConnectionId(HttpRequest request)
    {
        var clientId = request.Cookies["mt-client-id"];
        var tabId = request.Query["tabId"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(clientId))
        {
            clientId = Guid.NewGuid().ToString("N");
        }

        return BrowserIdentity.Build(clientId, tabId) ?? clientId;
    }
}
