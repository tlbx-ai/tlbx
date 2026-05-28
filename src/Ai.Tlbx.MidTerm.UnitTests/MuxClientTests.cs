using System.Net.WebSockets;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Services.WebSockets;
using Ai.Tlbx.MidTerm.Settings;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class MuxClientTests
{
    [Fact]
    public async Task FullReplayClient_DeliversAllAccessibleSessions()
    {
        using var socket = new FakeWebSocket();
        await using var client = new MuxClient(
            "client-1",
            socket,
            () => TerminalResumeModeSetting.FullReplay);

        client.SetActiveSession("active");
        client.SetVisibleSessions(new HashSet<string>(StringComparer.Ordinal) { "visible" });

        Assert.True(client.ShouldDeliverSession("active"));
        Assert.True(client.ShouldDeliverSession("visible"));
        Assert.True(client.ShouldDeliverSession("hidden"));
    }

    [Fact]
    public async Task ShareClient_DeliversOnlyAllowedSession()
    {
        using var socket = new FakeWebSocket();
        await using var client = new MuxClient(
            "client-1",
            socket,
            () => TerminalResumeModeSetting.QuickResume,
            allowedSessionId: "allowed");

        client.SetActiveSession("other");
        client.SetVisibleSessions(new HashSet<string>(StringComparer.Ordinal) { "visible" });

        Assert.True(client.ShouldDeliverSession("allowed"));
        Assert.False(client.ShouldDeliverSession("visible"));
        Assert.False(client.ShouldDeliverSession("other"));
    }

    [Fact]
    public async Task QueueOutput_WhenInputQueueIsFull_ReturnsFalseAndReleasesBuffer()
    {
        using var socket = new BlockingWebSocket();
        await using var client = new MuxClient(
            "client-1",
            socket,
            () => TerminalResumeModeSetting.FullReplay);

        client.SetActiveSession("session-1");

        var first = SharedOutputBuffer.Rent(32 * 1024);
        Assert.True(client.QueueOutput("session-1", 32 * 1024, 120, 30, first));
        await socket.SendStarted.WaitAsync(TimeSpan.FromSeconds(2));

        SharedOutputBuffer? rejected = null;
        for (var i = 0; i < 2_000; i++)
        {
            var buffer = SharedOutputBuffer.Rent(128);
            if (!client.QueueOutput("session-1", (ulong)(32 * 1024 + ((i + 1) * 128)), 120, 30, buffer))
            {
                rejected = buffer;
                break;
            }
        }

        Assert.NotNull(rejected);
        Assert.True(rejected.IsReleased);
        socket.ReleaseSends();
    }

    [Fact]
    public void ResolveViewportReplayBytes_ScalesWithRowsAndClamps()
    {
        var session = new SessionInfo
        {
            Cols = 120,
            Rows = 40,
            ShellType = "pwsh"
        };

        var small = MuxWebSocketHandler.ResolveViewportReplayBytes(session, replayRows: 20);
        var large = MuxWebSocketHandler.ResolveViewportReplayBytes(session, replayRows: 80);
        var huge = MuxWebSocketHandler.ResolveViewportReplayBytes(session, replayRows: 1000);

        Assert.InRange(small, 32 * 1024, 256 * 1024);
        Assert.True(large > small);
        Assert.Equal(256 * 1024, huge);
    }

    private class FakeWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort()
        {
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingWebSocket : FakeWebSocket
    {
        private readonly TaskCompletionSource _sendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSends = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendStarted => _sendStarted.Task;

        public void ReleaseSends() => _releaseSends.TrySetResult();

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            _sendStarted.TrySetResult();
            return _releaseSends.Task.WaitAsync(cancellationToken);
        }
    }
}
