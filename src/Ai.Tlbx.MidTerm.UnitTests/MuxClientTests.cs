using System.Net.WebSockets;
using Ai.Tlbx.MidTerm.Services.WebSockets;
using Ai.Tlbx.MidTerm.Settings;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class MuxClientTests
{
    [Fact]
    public async Task FullReplayClient_DeliversOnlyActiveOrVisibleSessions()
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
        Assert.False(client.ShouldDeliverSession("hidden"));
    }

    private sealed class FakeWebSocket : WebSocket
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
}
