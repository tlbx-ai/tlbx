using System.Net.WebSockets;
using System.Diagnostics.CodeAnalysis;

namespace Ai.Tlbx.MidTerm.Services.Connect;

/// <summary>Relays one local socket and reports its terminal state exactly once to the browser.</summary>
internal sealed class ConnectWebSocketRelay : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly string _id;
    private readonly Func<ConnectEnvelope, CancellationToken, Task> _send;
    private readonly int _maximumBody;
    private int _closeNotified;
    private int _disposed;
    public Task ReceiveTask { get; set; } = Task.CompletedTask;

    // This class takes ownership of socket; its caller must not dispose it separately.
    public ConnectWebSocketRelay(WebSocket socket, string id,
        Func<ConnectEnvelope, CancellationToken, Task> send, int maximumBody)
    {
        _socket = socket;
        _id = id;
        _send = send;
        _maximumBody = maximumBody;
    }

    public void Abort()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _socket.Abort(); } catch (ObjectDisposedException) { }
    }

    public async Task ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[48 * 1024];
        var closeCode = 1006;
        var closeReason = "Local connection ended unexpectedly.";
        try
        {
            while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer.AsMemory(), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        closeCode = NormalizeCloseCode((int?)_socket.CloseStatus ?? 1000);
                        closeReason = closeCode == 1006 ? "Local connection ended unexpectedly." : "Local connection closed.";
                        return;
                    }
                    if (message.Length > _maximumBody - result.Count)
                        throw new InvalidDataException("Local WebSocket message exceeds the Connect transport limit.");
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                await _send(new ConnectEnvelope
                {
                    Type = "ws-data", Id = _id, Binary = result.MessageType == WebSocketMessageType.Binary,
                    Data = Convert.ToBase64String(message.GetBuffer(), 0, checked((int)message.Length))
                }, ct);
            }
            if (ct.IsCancellationRequested)
            {
                closeCode = 1001;
                closeReason = "Connection closed.";
            }
        }
        catch (InvalidDataException)
        {
            closeCode = 1009;
            closeReason = "Local message exceeded the transport limit.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            closeCode = 1001;
            closeReason = "Connection closed.";
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException) { }
        finally
        {
            await NotifyCloseAsync(closeCode, closeReason, ct);
            Abort();
            DisposeSocket();
        }
    }

    public async Task DispatchAsync(ConnectEnvelope message, CancellationToken ct)
    {
        // The companion's stdout reader dispatches incoming peer messages serially.
        if (message.Type == "ws-close")
        {
            // Claim the normal close before Abort wakes the receiver with an I/O error.
            var close = NotifyCloseAsync(1000, "Closed by browser.", ct);
            Abort();
            await close;
            return;
        }
        if (_socket.State != WebSocketState.Open || message.Data is null)
        {
            await NotifyCloseAsync(1006, "Local connection is no longer open.", ct);
            return;
        }
        try
        {
            var data = Convert.FromBase64String(message.Data);
            if (data.Length > _maximumBody) throw new InvalidDataException("Connect WebSocket payload is too large.");
            await _socket.SendAsync(data, message.Binary == true ? WebSocketMessageType.Binary : WebSocketMessageType.Text,
                endOfMessage: true, ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException)
        {
            await NotifyCloseAsync(1009, "Message exceeded the transport limit or was invalid.", ct);
            Abort();
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            await NotifyCloseAsync(1006, "Local connection ended unexpectedly.", ct);
            Abort();
        }
    }

    private async Task NotifyCloseAsync(int code, string reason, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _closeNotified, 1) != 0) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await _send(new ConnectEnvelope { Type = "ws-close", Id = _id, Code = code, Reason = reason }, deadline.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException or ObjectDisposedException)
        {
            // A failed overall transport cannot deliver a close; never retry or duplicate it.
        }
    }

    private static int NormalizeCloseCode(int code) => code is 1000 or 1001 or 1002 or 1003 or 1007 or 1008 or 1009 or 1010 or 1011 or 1012 or 1013 or 1014
        || code is >= 3000 and <= 4999 ? code : 1006;

    public async ValueTask DisposeAsync()
    {
        Abort();
        await ReceiveTask;
        DisposeSocket();
    }

    [SuppressMessage("Usage", "IDISP007:Don't dispose injected", Justification = "The constructor explicitly takes ownership of the local WebSocket.")]
    private void DisposeSocket()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _socket.Dispose();
    }
}
