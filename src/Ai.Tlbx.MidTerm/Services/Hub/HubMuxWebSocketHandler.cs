using System.Globalization;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Hosting;
using System.Net.WebSockets;
using Ai.Tlbx.MidTerm.Services.WebSockets;

namespace Ai.Tlbx.MidTerm.Services.Hub;

public sealed class HubMuxWebSocketHandler
{
    private readonly HubService _hubService;
    private readonly AuthService _authService;
    private readonly ShutdownService _shutdownService;
    private readonly MidTermInstanceIdentity _instanceIdentity;

    public HubMuxWebSocketHandler(
        HubService hubService,
        AuthService authService,
        ShutdownService shutdownService, MidTermInstanceIdentity instanceIdentity)
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
        var sessionId = context.Request.Query["sessionId"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(machineId) || string.IsNullOrWhiteSpace(sessionId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("machineId and sessionId are required.", context.RequestAborted);
            return;
        }

        using var localSocket = await context.WebSockets.AcceptWebSocketAsync();
        using var remoteSocket = new ClientWebSocket();
        var machine = _hubService.GetMachine(machineId);
        if (machine is null)
        {
            await CloseBadRequestAsync(localSocket, "Hub machine not found.");
            return;
        }

        try
        {
            var browserId = BrowserIdentity.BuildFromRequest(context.Request);
            await _hubService.ConfigureRemoteWebSocketAsync(machineId, remoteSocket,
                $"hub-{_instanceIdentity.InstanceId}-{BrowserIdentity.GetClientPart(browserId)}", _shutdownService.Token);
            var resumeSequence = ulong.TryParse(
                context.Request.Query["resumeSequence"].ToString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedResumeSequence)
                ? parsedResumeSequence
                : (ulong?)null;
            var remoteUri = new Uri(BuildRemoteMuxUri(machine.BaseUrl, sessionId, resumeSequence).AbsoluteUri
                + "&tabId=" + Uri.EscapeDataString(browserId)
                + "&deviceLabel=" + Uri.EscapeDataString(BrowserIdentity.GetDeviceLabel(context.Request) ?? ""));
            await remoteSocket.ConnectAsync(remoteUri, _shutdownService.Token);
        }
        catch (Exception ex)
        {
            await CloseBadRequestAsync(localSocket, ex.Message);
            return;
        }

        using var bridgeCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownService.Token);
        var localToRemote = BridgeLocalToRemoteAsync(localSocket, remoteSocket, bridgeCts.Token);
        var remoteToLocal = BridgeRemoteToLocalAsync(localSocket, remoteSocket, sessionId, bridgeCts.Token);
        await Task.WhenAny(localToRemote, remoteToLocal);
        bridgeCts.Cancel();
        await Task.WhenAll(SwallowAsync(localToRemote), SwallowAsync(remoteToLocal));
        await TryCloseAsync(remoteSocket);
        await TryCloseAsync(localSocket);
    }

    internal static Uri BuildRemoteMuxUri(string baseUrl, string sessionId, ulong? resumeSequence)
    {
        var builder = new UriBuilder(baseUrl);
        builder.Scheme = builder.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ? "ws" : "wss";
        builder.Path = "/ws/mux";
        var escapedSessionId = Uri.EscapeDataString(sessionId);
        var query = $"activeSessionId={escapedSessionId}&visibleSessionIds={escapedSessionId}";
        if (resumeSequence is > 0)
        {
            var resumeCursor = $"{sessionId}:{resumeSequence.Value.ToString(CultureInfo.InvariantCulture)}";
            query += $"&resumeCursors={Uri.EscapeDataString(resumeCursor)}";
        }
        builder.Query = query;
        return builder.Uri;
    }

    private static async Task BridgeLocalToRemoteAsync(
        WebSocket localSocket,
        ClientWebSocket remoteSocket,
        CancellationToken ct)
    {
        var buffer = new byte[MuxProtocol.MaxFrameSize];
        while (!ct.IsCancellationRequested &&
               localSocket.State == WebSocketState.Open &&
               remoteSocket.State == WebSocketState.Open)
        {
            var result = await localSocket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            if (result.MessageType != WebSocketMessageType.Binary || result.Count == 0)
            {
                continue;
            }

            await remoteSocket.SendAsync(
                new ArraySegment<byte>(buffer, 0, result.Count),
                WebSocketMessageType.Binary,
                result.EndOfMessage,
                ct);
        }
    }

    private static async Task BridgeRemoteToLocalAsync(
        WebSocket localSocket,
        ClientWebSocket remoteSocket,
        string sessionId,
        CancellationToken ct)
    {
        var buffer = new byte[MuxProtocol.MaxFrameSize];
        while (!ct.IsCancellationRequested &&
               localSocket.State == WebSocketState.Open &&
               remoteSocket.State == WebSocketState.Open)
        {
            var result = await MuxWebSocketHandler.ReceiveMuxMessageAsync(remoteSocket, buffer, ct);
            if (result.TooLarge)
            {
                await remoteSocket.CloseOutputAsync(
                    WebSocketCloseStatus.MessageTooBig,
                    $"Mux frame exceeds {MuxProtocol.MaxFrameSize} bytes",
                    ct);
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            if (result.MessageType != WebSocketMessageType.Binary || result.Count == 0)
            {
                continue;
            }

            if (ShouldForwardFrame(buffer.AsSpan(0, result.Count), sessionId))
            {
                await localSocket.SendAsync(
                    new ArraySegment<byte>(buffer, 0, result.Count),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    ct);
            }
        }
    }

    private static bool ShouldForwardFrame(ReadOnlySpan<byte> frame, string sessionId)
    {
        if (frame.Length == 0)
        {
            return false;
        }

        if (frame[0] == 0xff || frame[0] == MuxProtocol.TypeSyncComplete)
        {
            return true;
        }

        return MuxProtocol.TryParseFrame(frame, out _, out var parsedSessionId, out _) &&
               string.Equals(parsedSessionId, sessionId, StringComparison.Ordinal);
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
        if (socket.State == WebSocketState.Open)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch
            {
            }
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
