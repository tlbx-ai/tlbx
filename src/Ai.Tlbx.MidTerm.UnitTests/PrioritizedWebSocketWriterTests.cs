using System.Collections.Concurrent;
using System.Net.WebSockets;
using Ai.Tlbx.MidTerm.Services.WebSockets;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class PrioritizedWebSocketWriterTests
{
    [Fact]
    public async Task Writer_SerializesFramesAndHonorsPriorityBetweenSends()
    {
        using var socket = new GateWebSocket();
        await using var writer = new PrioritizedWebSocketWriter(socket, static (_, _) => { });

        var first = writer.SendAsync(new byte[] { 1 }, MuxWritePriority.BackgroundLive).AsTask();
        await socket.FirstSendStarted.WaitAsync(TimeSpan.FromSeconds(2));
        var background = writer.SendAsync(new byte[] { 2 }, MuxWritePriority.BackgroundLive).AsTask();
        var recovery = writer.SendAsync(new byte[] { 3 }, MuxWritePriority.Recovery).AsTask();
        var control = writer.SendAsync(new byte[] { 4 }, MuxWritePriority.Control).AsTask();
        var active = writer.SendAsync(new byte[] { 5 }, MuxWritePriority.ActiveLive).AsTask();

        socket.ReleaseFirstSend();
        Assert.All(await Task.WhenAll(first, background, recovery, control, active), Assert.True);
        Assert.Equal(new byte[] { 1, 4, 5, 3, 2 }, socket.CompletedFrames);
    }

    [Fact]
    public async Task Writer_BoundsPendingFramesAndCompletesThemOnDispose()
    {
        using var socket = new GateWebSocket();
        var writer = new PrioritizedWebSocketWriter(socket, static (_, _) => { });
        var sends = new List<Task<bool>>
        {
            writer.SendAsync(new byte[] { 1 }, MuxWritePriority.BackgroundLive).AsTask()
        };
        await socket.FirstSendStarted.WaitAsync(TimeSpan.FromSeconds(2));

        for (var i = 0; i < PrioritizedWebSocketWriter.MaxQueuedFrames; i++)
        {
            sends.Add(writer.SendAsync(new byte[] { 2 }, MuxWritePriority.BackgroundLive).AsTask());
        }
        Assert.False(await writer.SendAsync(new byte[] { 3 }, MuxWritePriority.Control));

        await writer.DisposeAsync();
        Assert.All(await Task.WhenAll(sends), Assert.False);
    }

    [Fact]
    public async Task Writer_BoundsRetainedPayloadBytes()
    {
        using var socket = new GateWebSocket();
        var writer = new PrioritizedWebSocketWriter(socket, static (_, _) => { });
        var first = writer.SendAsync(new byte[] { 1 }, MuxWritePriority.BackgroundLive).AsTask();
        await socket.FirstSendStarted.WaitAsync(TimeSpan.FromSeconds(2));

        var frame = new byte[64 * 1024];
        var accepted = new List<Task<bool>>();
        while (true)
        {
            var send = writer.SendAsync(frame, MuxWritePriority.BackgroundLive).AsTask();
            if (send.IsCompletedSuccessfully && !await send)
            {
                break;
            }
            accepted.Add(send);
        }

        Assert.Equal(PrioritizedWebSocketWriter.MaxQueuedBytes / frame.Length, accepted.Count);
        await writer.DisposeAsync();
        Assert.False(await first);
        Assert.All(await Task.WhenAll(accepted), Assert.False);
    }

    private sealed class GateWebSocket : WebSocket
    {
        private readonly TaskCompletionSource _firstSendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _sendCount;

        public Task FirstSendStarted => _firstSendStarted.Task;
        public ConcurrentQueue<byte> CompletedFrames { get; } = new();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public void ReleaseFirstSend() => _releaseFirstSend.TrySetResult();
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();

        public override async Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _sendCount) == 1)
            {
                _firstSendStarted.TrySetResult();
                await _releaseFirstSend.Task.WaitAsync(cancellationToken);
            }
            CompletedFrames.Enqueue(buffer[0]);
        }
    }
}
