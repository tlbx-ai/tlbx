using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Services.Browser;

namespace Ai.Tlbx.MidTerm.Services.Boards;

/// <summary>Board notifications and the existing browser UI protocol, without a terminal size owner.</summary>
internal static class BoardSocket
{
    internal static async Task HandleAsync(HttpContext context)
    {
        var boards = context.RequestServices.GetRequiredService<BoardService>();
        var auth = context.RequestServices.GetRequiredService<AuthService>();
        var bridge = context.RequestServices.GetRequiredService<BrowserUiBridge>();
        var shutdown = context.RequestServices.GetRequiredService<ShutdownService>();
        var id = context.Request.Query["boardId"].ToString();
        var authentication = auth.AuthenticateRequestWithContext(context.Request);
        if (authentication.Method == RequestAuthMethod.None) { context.Response.StatusCode = 401; return; }
        if (boards.Get(id) is null) { context.Response.StatusCode = 404; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var authenticationScope = auth.TrackWebSocketAuthentication(authentication, socket);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, shutdown.Token);
        var token = lifetime.Token;
        var outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var connection = Guid.NewGuid().ToString("N");
        var browser = BrowserIdentity.BuildFromRequest(context.Request);

        void Push<T>(T value, JsonTypeInfo<T> type)
        {
            if (token.IsCancellationRequested) return;
            if (!outgoing.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(value, type)))
                AbortSocket();
        }
        void AbortSocket()
        {
            try { socket.Abort(); }
            catch (ObjectDisposedException) { }
        }
        void Instruction(BrowserUiInstruction instruction) => Push(instruction, AppJsonContext.Default.BrowserUiInstruction);
        void Changed(string boardId)
        {
            if (boardId == id) Push(new BoardSignal("board-changed", id), BoardJsonContext.Default.BoardSignal);
        }
        async Task SendAsync()
        {
            try
            {
                await foreach (var bytes in outgoing.Reader.ReadAllAsync(token).ConfigureAwait(false))
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                AbortSocket();
            }
        }
        var sender = SendAsync();
        bridge.RegisterAcknowledgedListener(connection, browser,
            detach: (request, session, preview) => Instruction(new() { Command = "detach", RequestId = request, SessionId = session, PreviewName = preview }),
            dock: (request, session, preview) => Instruction(new() { Command = "dock", RequestId = request, SessionId = session, PreviewName = preview }),
            viewport: (request, session, preview, width, height) => Instruction(new() { Command = "viewport", RequestId = request, SessionId = session, PreviewName = preview, Width = width, Height = height }),
            open: (request, session, preview, url, activate, revision) => Instruction(new() { Command = "open", RequestId = request, SessionId = session, PreviewName = preview, Url = url, ActivateSession = activate, TargetRevision = revision }),
            close: (request, session, preview) => Instruction(new() { Command = "close", RequestId = request, SessionId = session, PreviewName = preview }));
        boards.OnChanged += Changed;
        Push(new BoardSignal("board-ready", id), BoardJsonContext.Default.BoardSignal);
        try
        {
            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var message = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (message.MessageType == WebSocketMessageType.Close) break;
                // This channel is server-driven; client results use the canonical authenticated HTTP API.
                if (message.Count > 0)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "No client messages expected.", token).ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
        finally
        {
            boards.OnChanged -= Changed;
            bridge.UnregisterListener(connection);
            outgoing.Writer.TryComplete();
            await lifetime.CancelAsync().ConfigureAwait(false);
            await sender.ConfigureAwait(false);
        }
    }
}
