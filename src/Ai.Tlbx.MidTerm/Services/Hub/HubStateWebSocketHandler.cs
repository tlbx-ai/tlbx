using System.Net.WebSockets;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Hosting;

namespace Ai.Tlbx.MidTerm.Services.Hub;

/// <summary>
/// Bridges the size-control state channel to the terminal's actual MidTerm host.
/// The remote host remains authoritative for ownership, epochs, and PTY dimensions.
/// </summary>
public sealed class HubStateWebSocketHandler
{
    private readonly HubService _hubService;
    private readonly AuthService _authService;
    private readonly ShutdownService _shutdownService;
    private readonly MidTermInstanceIdentity _instanceIdentity;

    public HubStateWebSocketHandler(
        HubService hubService,
        AuthService authService,
        ShutdownService shutdownService,
        MidTermInstanceIdentity instanceIdentity)
    {
        _hubService = hubService;
        _authService = authService;
        _shutdownService = shutdownService;
        _instanceIdentity = instanceIdentity;
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (_authService.AuthenticateRequest(context.Request) == RequestAuthMethod.None)
        {
            context.Response.StatusCode = 401;
            return;
        }

        var machineId = context.Request.Query["machineId"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(machineId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("machineId is required.", context.RequestAborted);
            return;
        }

        var machine = _hubService.GetMachine(machineId);
        if (machine is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        using var localSocket = await context.WebSockets.AcceptWebSocketAsync();
        using var remoteSocket = new ClientWebSocket();
        var localBrowserId = BrowserIdentity.BuildFromRequest(context.Request);
        var browserLabel = BrowserIdentity.GetDeviceLabel(context.Request);

        try
        {
            await _hubService.ConfigureRemoteWebSocketAsync(
                machineId,
                remoteSocket,
                $"hub-{_instanceIdentity.InstanceId}-{BrowserIdentity.GetClientPart(localBrowserId)}",
                _shutdownService.Token);
            var remoteUri = BuildRemoteStateUri(machine.BaseUrl, localBrowserId, browserLabel);
            await remoteSocket.ConnectAsync(remoteUri, _shutdownService.Token);
        }
        catch (Exception ex)
        {
            await CloseBadRequestAsync(localSocket, ex.Message);
            return;
        }

        using var bridgeCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownService.Token);
        var localToRemote = BridgeAsync(localSocket, remoteSocket, bridgeCts.Token);
        var remoteToLocal = BridgeAsync(remoteSocket, localSocket, bridgeCts.Token);
        await Task.WhenAny(localToRemote, remoteToLocal);
        bridgeCts.Cancel();
        await Task.WhenAll(SwallowAsync(localToRemote), SwallowAsync(remoteToLocal));
        await TryCloseAsync(remoteSocket);
        await TryCloseAsync(localSocket);
    }

    private static Uri BuildRemoteStateUri(string baseUrl, string browserId, string? browserLabel)
    {
        var query = $"tabId={Uri.EscapeDataString(browserId)}&sizeControlOnly=true";
        if (!string.IsNullOrWhiteSpace(browserLabel))
        {
            query += $"&deviceLabel={Uri.EscapeDataString(browserLabel)}";
        }

        var builder = new UriBuilder(baseUrl)
        {
            Scheme = new Uri(baseUrl).Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ? "ws" : "wss",
            Path = "/ws/state",
            Query = query
        };
        return builder.Uri;
    }

    private static async Task BridgeAsync(WebSocket source, WebSocket destination, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested &&
               source.State == WebSocketState.Open &&
               destination.State == WebSocketState.Open)
        {
            var result = await source.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            await destination.SendAsync(
                new ArraySegment<byte>(buffer, 0, result.Count),
                result.MessageType,
                result.EndOfMessage,
                ct);
        }
    }

    private static async Task CloseBadRequestAsync(WebSocket socket, string message)
    {
        if (socket.State == WebSocketState.Open)
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, message, CancellationToken.None);
        }
    }

    private static async Task TryCloseAsync(WebSocket socket)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        catch
        {
        }
    }

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }
}
