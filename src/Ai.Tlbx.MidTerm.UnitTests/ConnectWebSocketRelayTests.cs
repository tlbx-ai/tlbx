using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using Ai.Tlbx.MidTerm.Services.Connect;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class ConnectWebSocketRelayTests
{
    [Fact]
    public async Task BrowserCloseReceivesOneNormalAcknowledgementEvenWhenAbortWakesReceiver()
    {
        using var socket = new FakeSocket();
        var messages = new ConcurrentQueue<ConnectEnvelope>();
        await using var relay = Create(socket, messages);
        relay.ReceiveTask = relay.ReceiveAsync(TestContext.Current.CancellationToken);
        await relay.DispatchAsync(new ConnectEnvelope { Type = "ws-close", Id = "socket" }, TestContext.Current.CancellationToken);
        await relay.ReceiveTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var close = Assert.Single(messages);
        Assert.Equal("ws-close", close.Type);
        Assert.Equal(1000, close.Code);
        Assert.True(socket.Disposed);
    }

    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(1005, 1006)]
    [InlineData(4001, 4001)]
    public async Task LocalCloseReportsOneSafeTerminalCode(int actual, int expected)
    {
        using var socket = new FakeSocket();
        socket.EnqueueClose((WebSocketCloseStatus)actual);
        var messages = new ConcurrentQueue<ConnectEnvelope>();
        await using var relay = Create(socket, messages);
        relay.ReceiveTask = relay.ReceiveAsync(TestContext.Current.CancellationToken);
        await relay.ReceiveTask;
        Assert.Equal(expected, Assert.Single(messages).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedLocalFailureClosesBrowserInsteadOfLeavingItOpen(bool ioFailure)
    {
        using var socket = new FakeSocket();
        socket.EnqueueFailure(ioFailure ? new IOException("fixture") : new WebSocketException("fixture"));
        var messages = new ConcurrentQueue<ConnectEnvelope>();
        await using var relay = Create(socket, messages);
        relay.ReceiveTask = relay.ReceiveAsync(TestContext.Current.CancellationToken);
        await relay.ReceiveTask;
        var close = Assert.Single(messages);
        Assert.Equal("ws-close", close.Type);
        Assert.Equal(1006, close.Code);
        Assert.DoesNotContain("fixture", close.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedFragmentedLocalMessageClosesWith1009WithoutLeakingPartialData()
    {
        using var socket = new FakeSocket();
        socket.EnqueueData(new byte[800], end: false);
        socket.EnqueueData(new byte[800], end: true);
        var messages = new ConcurrentQueue<ConnectEnvelope>();
        await using var relay = Create(socket, messages);
        relay.ReceiveTask = relay.ReceiveAsync(TestContext.Current.CancellationToken);
        await relay.ReceiveTask;
        var close = Assert.Single(messages);
        Assert.Equal("ws-close", close.Type);
        Assert.Equal(1009, close.Code);
    }

    [Fact]
    public async Task LocalSendFailureAlsoClosesBrowserExactlyOnce()
    {
        using var socket = new FakeSocket { FailSend = true };
        var messages = new ConcurrentQueue<ConnectEnvelope>();
        await using var relay = Create(socket, messages);
        relay.ReceiveTask = relay.ReceiveAsync(TestContext.Current.CancellationToken);
        await relay.DispatchAsync(new ConnectEnvelope
        {
            Type = "ws-data", Id = "socket", Data = Convert.ToBase64String([1, 2, 3]), Binary = true
        }, TestContext.Current.CancellationToken);
        await relay.ReceiveTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(1006, Assert.Single(messages).Code);
    }

    [Fact]
    public async Task StalledCloseNotificationIsBoundedAndNeverRetried()
    {
        using var socket = new FakeSocket();
        socket.EnqueueClose(WebSocketCloseStatus.NormalClosure);
        var attempts = 0;
        await using var relay = new ConnectWebSocketRelay(socket, "socket", async (_, ct) =>
        {
            Interlocked.Increment(ref attempts);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, maximumBody: 1024);
        relay.ReceiveTask = relay.ReceiveAsync(TestContext.Current.CancellationToken);
        await relay.ReceiveTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, attempts);
        Assert.True(socket.Disposed);
    }

    private static ConnectWebSocketRelay Create(FakeSocket socket, ConcurrentQueue<ConnectEnvelope> messages) =>
        new(socket, "socket", (message, _) => { messages.Enqueue(message); return Task.CompletedTask; }, maximumBody: 1024);

    private sealed class FakeSocket : WebSocket
    {
        private readonly Channel<Step> _steps = Channel.CreateUnbounded<Step>();
        private WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        public bool Disposed { get; private set; }
        public bool FailSend { get; init; }
        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => new('x', 500);
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void EnqueueClose(WebSocketCloseStatus status)
        {
            _closeStatus = status;
            _steps.Writer.TryWrite(new Step([], WebSocketMessageType.Close, true));
        }
        public void EnqueueData(byte[] data, bool end) => _steps.Writer.TryWrite(new Step(data, WebSocketMessageType.Binary, end));
        public void EnqueueFailure(Exception error) => _steps.Writer.TryWrite(new Step([], WebSocketMessageType.Binary, true, error));
        public override void Abort()
        {
            _state = WebSocketState.Aborted;
            EnqueueFailure(new WebSocketException("aborted"));
        }
        public override void Dispose() { Disposed = true; _state = WebSocketState.Closed; }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var result = await ReceiveAsync(buffer.AsMemory(), cancellationToken);
            return new WebSocketReceiveResult(result.Count, result.MessageType, result.EndOfMessage);
        }
        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var step = await _steps.Reader.ReadAsync(cancellationToken);
            if (step.Error is not null) throw step.Error;
            step.Data.CopyTo(buffer);
            if (step.Type == WebSocketMessageType.Close) _state = WebSocketState.CloseReceived;
            return new ValueWebSocketReceiveResult(step.Data.Length, step.Type, step.End);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            FailSend ? Task.FromException(new WebSocketException("fixture send failure")) : Task.CompletedTask;
        private sealed record Step(byte[] Data, WebSocketMessageType Type, bool End, Exception? Error = null);
    }
}
