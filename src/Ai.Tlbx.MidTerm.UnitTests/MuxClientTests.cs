using System.Net.WebSockets;
using System.Text;
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
    public async Task DegradedClient_DeliversOnlyActiveAndVisibleSessions()
    {
        using var socket = new FakeWebSocket();
        await using var client = new MuxClient(
            "client-1",
            socket,
            () => TerminalResumeModeSetting.FullReplay);

        client.SetActiveSession("active");
        client.SetVisibleSessions(new HashSet<string>(StringComparer.Ordinal) { "visible" });
        client.MarkTransportDegradedForTests();

        Assert.True(client.IsTransportDegraded);
        Assert.True(client.ShouldDeliverSession("active"));
        Assert.True(client.ShouldDeliverSession("visible"));
        Assert.False(client.ShouldDeliverSession("hidden"));
    }

    [Fact]
    public async Task DegradedUnhintedClient_DeliversAllSessionsUntilBrowserHintsArrive()
    {
        using var socket = new FakeWebSocket();
        await using var client = new MuxClient(
            "client-1",
            socket,
            () => TerminalResumeModeSetting.FullReplay);

        client.MarkTransportDegradedForTests();

        Assert.True(client.ShouldDeliverSession("session-1"));
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

    [Fact]
    public void ResolveReplayMaxBytes_IgnoresReplayRowsForFullReplay()
    {
        var session = new SessionInfo
        {
            Cols = 120,
            Rows = 40,
            ShellType = "pwsh"
        };

        var maxBytes = MuxWebSocketHandler.ResolveReplayMaxBytes(
            session,
            replayRows: 40,
            quickResume: false,
            configuredScrollbackBytes: 2 * 1024 * 1024);

        Assert.Null(maxBytes);
    }

    [Fact]
    public void ResolveReplayMaxBytes_UsesReplayRowsForQuickResume()
    {
        var session = new SessionInfo
        {
            Cols = 120,
            Rows = 40,
            ShellType = "pwsh"
        };

        var maxBytes = MuxWebSocketHandler.ResolveReplayMaxBytes(
            session,
            replayRows: 40,
            quickResume: true,
            configuredScrollbackBytes: 2 * 1024 * 1024);

        Assert.Equal(MuxWebSocketHandler.ResolveViewportReplayBytes(session, replayRows: 40), maxBytes);
    }

    [Fact]
    public void ShouldSendResyncForBufferRequest_AlwaysResyncsFullReplay()
    {
        var snapshot = new TtyHostBufferSnapshot
        {
            SequenceStart = 100,
            Data = [1, 2, 3]
        };

        Assert.True(MuxWebSocketHandler.ShouldSendResyncForBufferRequest(
            quickResume: false,
            sinceSequence: null,
            snapshot));
    }

    [Fact]
    public void ShouldSendResyncForBufferRequest_OnlyResyncsQuickResumeOnCursorMismatch()
    {
        var matchingSnapshot = new TtyHostBufferSnapshot
        {
            SequenceStart = 100,
            Data = [1, 2, 3]
        };
        var mismatchedSnapshot = new TtyHostBufferSnapshot
        {
            SequenceStart = 90,
            Data = [1, 2, 3]
        };

        Assert.False(MuxWebSocketHandler.ShouldSendResyncForBufferRequest(
            quickResume: true,
            sinceSequence: 100,
            matchingSnapshot));
        Assert.True(MuxWebSocketHandler.ShouldSendResyncForBufferRequest(
            quickResume: true,
            sinceSequence: 100,
            mismatchedSnapshot));
    }

    [Fact]
    public async Task ActiveSessionOutput_CoalescesSmallAdjacentChunks()
    {
        using var socket = new RecordingWebSocket();
        await using var client = new MuxClient(
            "client-1",
            socket,
            () => TerminalResumeModeSetting.FullReplay);

        const string sessionId = "session1";

        client.SetActiveSession(sessionId);

        Assert.True(client.QueueOutput(sessionId, 1, 120, 30, RentOutput("a")));
        Assert.True(client.QueueOutput(sessionId, 2, 120, 30, RentOutput("b")));

        await WaitForAsync(() => socket.SentFrames.Count >= 1);
        await Task.Delay(30);

        var frame = Assert.Single(socket.SentFrames);
        Assert.True(MuxProtocol.TryParseFrame(frame, out var type, out var parsedSessionId, out var payload));
        Assert.Equal(MuxProtocol.TypeTerminalOutput, type);
        Assert.Equal(sessionId, parsedSessionId);
        Assert.Equal((ulong)2, MuxProtocol.ParseOutputSequenceEnd(payload));
        Assert.Equal("ab", Encoding.UTF8.GetString(MuxProtocol.GetOutputData(payload)));
    }

    private static SharedOutputBuffer RentOutput(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var buffer = SharedOutputBuffer.Rent(bytes.Length);
        bytes.CopyTo(buffer.WriteSpan);
        return buffer;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
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

    private sealed class RecordingWebSocket : FakeWebSocket
    {
        public List<byte[]> SentFrames { get; } = [];

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            SentFrames.Add(buffer.AsSpan().ToArray());
            return Task.CompletedTask;
        }
    }
}
