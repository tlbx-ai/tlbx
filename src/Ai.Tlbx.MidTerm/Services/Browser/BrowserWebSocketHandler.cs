using System.Net.WebSockets;
using System.Globalization;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Settings;
using Ai.Tlbx.MidTerm.Services.WebPreview;

namespace Ai.Tlbx.MidTerm.Services.Browser;

public sealed class BrowserWebSocketHandler
{
    private readonly BrowserCommandService _commandService;
    private readonly BrowserPreviewRegistry _previewRegistry;
    private readonly WebPreviewService _webPreviewService;
    private readonly SettingsService _settingsService;
    private readonly AuthService _authService;
    private readonly ShutdownService _shutdownService;
    private readonly WebPreviewStorageService? _storageService;

    public BrowserWebSocketHandler(
        BrowserCommandService commandService,
        BrowserPreviewRegistry previewRegistry,
        WebPreviewService webPreviewService,
        SettingsService settingsService,
        AuthService authService,
        ShutdownService shutdownService,
        WebPreviewStorageService? storageService = null)
    {
        _commandService = commandService;
        _previewRegistry = previewRegistry;
        _webPreviewService = webPreviewService;
        _settingsService = settingsService;
        _authService = authService;
        _shutdownService = shutdownService;
        _storageService = storageService;
    }

    public async Task HandleAsync(HttpContext context)
    {
        var queryPreviewId = context.Request.Query["previewId"].FirstOrDefault();
        var queryPreviewToken = context.Request.Query["token"].FirstOrDefault();
        var queryRouteKey = context.Request.Query["routeKey"].FirstOrDefault();
        var hasPreviewAuth = _previewRegistry.TryValidate(queryPreviewId, queryPreviewToken, out var previewClient);
        var previewSession = !string.IsNullOrWhiteSpace(queryRouteKey)
            ? _webPreviewService.GetPreviewSessionByRouteKey(queryRouteKey)
            : null;

        if (!hasPreviewAuth)
        {
            if (_authService.AuthenticateRequest(context.Request) == RequestAuthMethod.None)
            {
                context.Response.StatusCode = 401;
                return;
            }
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        using var sendLock = new SemaphoreSlim(1, 1);
        var shutdownToken = _shutdownService.Token;
        var connectionId = Guid.NewGuid().ToString("N");
        var sessionId = previewClient?.SessionId
            ?? previewSession?.SessionId
            ?? context.Request.Query["sessionId"].FirstOrDefault();
        var previewName = previewClient?.PreviewName ?? previewSession?.PreviewName;
        var previewId = previewClient?.PreviewId ?? queryPreviewId;
        var browserId = previewClient?.BrowserId ?? context.Request.Cookies["mt-client-id"];
        var isVisible = ParseBooleanQuery(context.Request.Query["visible"].FirstOrDefault());
        var hasFocus = ParseBooleanQuery(context.Request.Query["focus"].FirstOrDefault());
        var isTopLevel = ParseBooleanQuery(context.Request.Query["topLevel"].FirstOrDefault());

        if (!_commandService.TryRegisterClient(
                connectionId,
                sessionId,
                previewName,
                previewId,
                OnCommandReady,
                browserId,
                isVisible,
                hasFocus,
                isTopLevel,
                long.TryParse(context.Request.Query["targetRevision"].FirstOrDefault(), CultureInfo.InvariantCulture, out var revision) ? revision : null,
                previewClient?.OwnershipGeneration,
                ParseOptionalBooleanQuery(context.Request.Query["panelVisible"].FirstOrDefault()),
                ParseOptionalBooleanQuery(context.Request.Query["documentVisible"].FirstOrDefault()),
                context.Request.Query["documentVisibilityState"].FirstOrDefault(),
                context.Request.Query["documentId"].FirstOrDefault()))
        {
            BrowserLog.Info($"Rejected duplicate browser client for preview '{previewId}'");
            await ws.CloseAsync(
                WebSocketCloseStatus.PolicyViolation,
                "Duplicate preview connection",
                shutdownToken);
            return;
        }

        BrowserLog.Info(
            $"Browser WebSocket connected (preview={previewId ?? "(anonymous)"}, name={previewName ?? "(default)"}, session={sessionId ?? "(none)"}, browser={browserId ?? "(none)"})");

        async Task SendCommandAsync(BrowserWsMessage message)
        {
            if (ws.State != WebSocketState.Open)
                return;

            await sendLock.WaitAsync(shutdownToken);
            try
            {
                if (ws.State != WebSocketState.Open)
                    return;

                var bytes = JsonSerializer.SerializeToUtf8Bytes(message, AppJsonContext.Default.BrowserWsMessage);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, shutdownToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                BrowserLog.Error($"SendCommand failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                sendLock.Release();
            }
        }

        void OnCommandReady(BrowserWsMessage msg) => _ = SendCommandAsync(msg);

        try
        {
            var buffer = new byte[65536];
            using var messageBuffer = new MemoryStream();

            while (ws.State == WebSocketState.Open && !shutdownToken.IsCancellationRequested)
            {
                try
                {
                    var result = await ws.ReceiveAsync(buffer, shutdownToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        messageBuffer.Write(buffer.AsSpan(0, result.Count));

                        if (result.EndOfMessage)
                        {
                            var json = messageBuffer.GetBuffer().AsSpan(0, checked((int)messageBuffer.Length));

                            try
                            {
                                var wsResult = JsonSerializer.Deserialize(json, AppJsonContext.Default.BrowserWsResult);
                                if (wsResult is not null)
                                {
                                    if (wsResult.Type == "browser-state")
                                    {
                                        _commandService.UpdateClientState(connectionId, wsResult);
                                        _commandService.AttachStorageForCurrentClient(connectionId, _storageService, wsResult.StorageToken);
                                    }
                                    else
                                        _commandService.ReceiveResult(wsResult, connectionId);
                                }
                            }
                            catch (JsonException ex)
                            {
                                BrowserLog.Error($"Failed to parse browser result: {ex.Message}");
                            }
                            finally
                            {
                                messageBuffer.SetLength(0);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException)
                {
                    break;
                }
            }
        }
        finally
        {
            _commandService.UnregisterClient(connectionId);
            BrowserLog.Info(
                $"Browser WebSocket disconnected (preview={previewId ?? "(anonymous)"}, name={previewName ?? "(default)"}, session={sessionId ?? "(none)"}, browser={browserId ?? "(none)"})");

            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
                }
                catch
                {
                }
            }
        }
    }

    private static bool ParseBooleanQuery(string? value)
    {
        return string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool? ParseOptionalBooleanQuery(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (ParseBooleanQuery(value)) return true;
        return value == "0" || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase) ? false : null;
    }
}
