using System.Net.WebSockets;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
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

    public BrowserWebSocketHandler(
        BrowserCommandService commandService,
        BrowserPreviewRegistry previewRegistry,
        WebPreviewService webPreviewService,
        SettingsService settingsService,
        AuthService authService,
        ShutdownService shutdownService)
    {
        _commandService = commandService;
        _previewRegistry = previewRegistry;
        _webPreviewService = webPreviewService;
        _settingsService = settingsService;
        _authService = authService;
        _shutdownService = shutdownService;
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
                previewClient?.OwnershipGeneration))
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
            var messageBuffer = new List<byte>();

            while (ws.State == WebSocketState.Open && !shutdownToken.IsCancellationRequested)
            {
                try
                {
                    var result = await ws.ReceiveAsync(buffer, shutdownToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var existingLen = messageBuffer.Count;
                        CollectionsMarshal.SetCount(messageBuffer, existingLen + result.Count);
                        buffer.AsSpan(0, result.Count).CopyTo(
                            CollectionsMarshal.AsSpan(messageBuffer).Slice(existingLen));

                        if (result.EndOfMessage)
                        {
                            var json = Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(messageBuffer));
                            messageBuffer.Clear();

                            try
                            {
                                var wsResult = JsonSerializer.Deserialize(json, AppJsonContext.Default.BrowserWsResult);
                                if (wsResult is not null)
                                {
                                    if (wsResult.Type == "browser-state")
                                        _commandService.UpdateClientState(connectionId, wsResult);
                                    else
                                        _commandService.ReceiveResult(wsResult, connectionId);
                                }
                            }
                            catch (JsonException ex)
                            {
                                BrowserLog.Error($"Failed to parse browser result: {ex.Message}");
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
}
