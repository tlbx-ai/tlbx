using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Models;
using Ai.Tlbx.MidTerm.Models.Update;
using Ai.Tlbx.MidTerm.Settings;

using Ai.Tlbx.MidTerm.Services.Updates;
using Ai.Tlbx.MidTerm.Models.Auth;
using Ai.Tlbx.MidTerm.Models.Certificates;
using Ai.Tlbx.MidTerm.Models.Files;
using Ai.Tlbx.MidTerm.Models.History;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Models.System;
namespace Ai.Tlbx.MidTerm.Services.WebSockets;

public sealed class SettingsWebSocketHandler
{
    private readonly SettingsService _settingsService;
    private readonly UpdateService _updateService;
    private readonly AuthService _authService;
    private readonly ShutdownService _shutdownService;

    public SettingsWebSocketHandler(
        SettingsService settingsService,
        UpdateService updateService,
        AuthService authService,
        ShutdownService shutdownService)
    {
        _settingsService = settingsService;
        _updateService = updateService;
        _authService = authService;
        _shutdownService = shutdownService;
    }

    public async Task HandleAsync(HttpContext context)
    {
        // SECURITY: Validate auth before accepting WebSocket
        var authentication = _authService.AuthenticateRequestWithContext(context.Request);
        if (authentication.Method == RequestAuthMethod.None)
        {
            context.Response.StatusCode = 401;
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        using var authLease = _authService.TrackWebSocketAuthentication(authentication, ws);
        var sendLock = new SemaphoreSlim(1, 1);
        var shutdownToken = _shutdownService.Token;

        async Task SendMessageAsync(SettingsWsMessage message)
        {
            if (ws.State != WebSocketState.Open)
            {
                return;
            }

            await sendLock.WaitAsync(shutdownToken);
            try
            {
                if (ws.State != WebSocketState.Open)
                {
                    return;
                }

                var json = JsonSerializer.Serialize(message, AppJsonContext.Default.SettingsWsMessage);
                var bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, shutdownToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
            finally
            {
                sendLock.Release();
            }
        }

        void OnSettingsChange(MidTermSettings s) => _ = SendMessageAsync(new SettingsWsMessage
        {
            Type = "settings",
            Settings = MidTermSettingsPublic.FromSettings(s)
        });

        void OnUpdateChange(UpdateInfo u) => _ = SendMessageAsync(new SettingsWsMessage
        {
            Type = "update",
            Update = u
        });

        var settingsListenerId = _settingsService.AddSettingsListener(OnSettingsChange);
        var updateListenerId = _updateService.AddUpdateListener(OnUpdateChange);

        try
        {
            await SendMessageAsync(new SettingsWsMessage
            {
                Type = "settings",
                Settings = MidTermSettingsPublic.FromSettings(_settingsService.Load())
            });

            var latestUpdate = _updateService.LatestUpdate;
            if (latestUpdate is not null)
            {
                await SendMessageAsync(new SettingsWsMessage
                {
                    Type = "update",
                    Update = latestUpdate
                });
            }

            var buffer = new byte[1024];
            while (ws.State == WebSocketState.Open && !shutdownToken.IsCancellationRequested)
            {
                try
                {
                    var result = await ws.ReceiveAsync(buffer, shutdownToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
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
            _settingsService.RemoveSettingsListener(settingsListenerId);
            _updateService.RemoveUpdateListener(updateListenerId);
            sendLock.Dispose();

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
}
