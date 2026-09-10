using System.Net.WebSockets;
using System.Text;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Services.WebSockets;
using Ai.Tlbx.MidTerm.Settings;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

[Collection(TimingSensitiveCollection.Name)]
public sealed class MuxClientTests
{
    [Fact]
    public async Task HintedClient_DeliversActiveVisibleAndSubscribedBackgroundSessions()
    {
        using var socket = new FakeWebSocket();
        await using var client = new MuxClient(
            "client-1",
            socket,
            () => TerminalResumeModeSetting.FullReplay);

        client.SetActiveSession("active");
        client.SetVisibleSessions(new HashSet<string>(StringComparer.Ordinal) { "visible" });
        client.SetBackgroundSessions(new HashSet<string>(StringComparer.Ordinal) { "background" });

        Assert.True(client.ShouldDeliverSession("active"));
        Assert.True(client.ShouldDeliverSession("visible"));
        Assert.True(client.ShouldDeliverSession("background"));
        Assert.False(client.ShouldDeliverSession("hidden"));
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
        client.SetBackgroundSessions(new HashSet<string>(StringComparer.Ordinal) { "background" });
        client.MarkTransportDegradedForTests();

        Assert.True(client.IsTransportDegraded);
        Assert.True(client.ShouldDeliverSession("active"));
        Assert.True(client.ShouldDeliverSession("visible"));
        Assert.False(client.ShouldDeliverSession("background"));
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
    public async Task HiddenSession_PausesAtDeliveredCursorUntilRecoveryCompletes()
    {
        using var socket = new RecordingWebSocket();
        await using var client = new MuxClient(
            "client-1",
            socket,
            () => TerminalResumeModeSetting.FullReplay);

        client.SetActiveSession("active01");
        client.SetVisibleSessions(new HashSet<string>(StringComparer.Ordinal) { "visible1" });
        Assert.False(client.QueueOutput("hidden01", 4, 120, 30, RentOutput("lost")));
        Assert.True(client.TryGetPausedSession("hidden01", out var paused));
        Assert.Equal(0UL, paused.ResumeSequence);
        Assert.Equal(4UL, paused.SourceSequenceEndExclusive);

        client.SetBackgroundSessions(new HashSet<string>(StringComparer.Ordinal) { "hidden01" });
        Assert.False(client.QueueOutput("hidden01", 9, 120, 30, RentOutput("after")));

        Assert.True(await client.ExecuteRecoveryAsync(
            "hidden01",
            static (_, _) => Task.FromResult(new MuxClient.RecoveryResult(true, 9, 9, true)),
            CancellationToken.None));
        Assert.False(client.TryGetPausedSession("hidden01", out _));

        Assert.True(client.QueueOutput("hidden01", 14, 120, 30, RentOutput("fresh")));
        await WaitForAsync(() => socket.SentFrames.Count >= 1);
        await Task.Delay(30);

        var frame = Assert.Single(socket.SentFrames);
        Assert.True(MuxProtocol.TryParseFrame(frame, out var outputType, out var outputSessionId, out var outputPayload));
        Assert.Equal(MuxProtocol.TypeTerminalOutput, outputType);
        Assert.Equal("hidden01", outputSessionId);
        Assert.Equal("fresh", Encoding.UTF8.GetString(MuxProtocol.GetOutputData(outputPayload)));
    }

    [Fact]
    public async Task Recovery_HoldsLiveOutputAndCoalescesConcurrentRequests()
    {
        using var socket = new RecordingWebSocket();
        await using var client = new MuxClient(
            "client-1",
            socket,
            () => TerminalResumeModeSetting.FullReplay);
        const string sessionId = "session1";
        client.SetActiveSession(sessionId);

        var recoveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRecovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovery = client.ExecuteRecoveryAsync(
            sessionId,
            async (_, ct) =>
            {
                recoveryStarted.TrySetResult();
                await releaseRecovery.Task.WaitAsync(ct);
                return new MuxClient.RecoveryResult(true, 0, 0, false);
            },
            CancellationToken.None);
        await recoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(client.QueueOutput(sessionId, 4, 120, 30, RentOutput("live")));
        Assert.False(await client.ExecuteRecoveryAsync(
            sessionId,
            static (_, _) => Task.FromResult(new MuxClient.RecoveryResult(true, 0, 0, false)),
            CancellationToken.None));
        await Task.Delay(30);
        Assert.Empty(socket.SentFrames);

        releaseRecovery.TrySetResult();
        Assert.True(await recovery);
        await WaitForAsync(() => socket.SentFrames.Count == 1);

        var telemetry = client.GetRecoveryTelemetry(sessionId);
        Assert.Equal(2, telemetry.Requested);
        Assert.Equal(1, telemetry.Coalesced);
        Assert.Equal(1, telemetry.Completed);
        Assert.Equal(0, telemetry.Failed);

        await client.RemoveSessionAsync(sessionId);
        await WaitForAsync(() => client.GetRecoveryTelemetry(sessionId).Requested == 0);
    }

    [Fact]
    public async Task StartupSuspension_HoldsLiveOutputUntilInitialReplayReleasesIt()
    {
        using var socket = new RecordingWebSocket();
        await using var client = new MuxClient(
            "client-1",
            socket,
            () => TerminalResumeModeSetting.FullReplay,
            startFlushSuspended: true);
        const string sessionId = "session1";
        client.SetActiveSession(sessionId);

        Assert.True(client.QueueOutput(sessionId, 4, 120, 30, RentOutput("live")));
        await Task.Delay(30);
        Assert.Empty(socket.SentFrames);

        client.ResumeFlush();
        await WaitForAsync(() => socket.SentFrames.Count == 1);

        var frame = Assert.Single(socket.SentFrames);
        Assert.True(MuxProtocol.TryParseFrame(frame, out var type, out var sentSessionId, out var payload));
        Assert.Equal(MuxProtocol.TypeTerminalOutput, type);
        Assert.Equal(sessionId, sentSessionId);
        Assert.Equal("live", Encoding.UTF8.GetString(MuxProtocol.GetOutputData(payload)));
    }

    [Fact]
    public async Task Recovery_DefersNewDataLossUntilAfterTransactionEnd()
    {
        using var socket = new RecordingWebSocket();
        await using var client = new MuxClient(
            "client-1",
            socket,
            () => TerminalResumeModeSetting.FullReplay);
        const string sessionId = "session1";
        var recoveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRecovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var recovery = client.ExecuteRecoveryAsync(
            sessionId,
            async (generation, ct) =>
            {
                Assert.True(await client.TrySendAsync(MuxProtocol.CreateRecoveryBeginFrame(
                    sessionId,
                    generation,
                    false,
                    TerminalReplayReason.QuickResumeTailReplay,
                    0,
                    10)));
                recoveryStarted.TrySetResult();
                await releaseRecovery.Task.WaitAsync(ct);
                Assert.True(await client.TrySendAsync(MuxProtocol.CreateRecoveryEndFrame(sessionId, generation, 10, 10)));
                return new MuxClient.RecoveryResult(true, 10, 10, false);
            },
            CancellationToken.None);
        await recoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        client.NotifyDataLoss(
            sessionId,
            TerminalReplayReason.MuxOverflow,
            2,
            missingSequenceStart: 10,
            missingSequenceEndExclusive: 12);
        await Task.Delay(30);
        Assert.Single(socket.SentFrames);

        releaseRecovery.TrySetResult();
        Assert.True(await recovery);
        await WaitForAsync(() => socket.SentFrames.Count == 3);

        var frameTypes = socket.SentFrames.Select(static frame => frame[0]).ToArray();
        Assert.Equal(
            new[] { MuxProtocol.TypeRecoveryBegin, MuxProtocol.TypeRecoveryEnd, MuxProtocol.TypeDataLoss },
            frameTypes);
        Assert.True(client.TryGetPausedSession(sessionId, out var paused));
        Assert.Equal(12UL, paused.SourceSequenceEndExclusive);
    }

    [Fact]
    public async Task RemoveSession_CancelsAndAwaitsActiveRecovery()
    {
        var sessionActive = true;
        using var socket = new RecordingWebSocket();
        await using var client = new MuxClient(
            "client-1",
            socket,
            () => TerminalResumeModeSetting.FullReplay,
            sessionExists: _ => sessionActive);
        const string sessionId = "closing1";
        var recoveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var recovery = client.ExecuteRecoveryAsync(
            sessionId,
            async (_, ct) =>
            {
                recoveryStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new MuxClient.RecoveryResult(true, 0, 0, false);
            },
            CancellationToken.None);
        await recoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        sessionActive = false;
        await client.RemoveSessionAsync(sessionId).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(await recovery);
        Assert.False(client.TryGetPausedSession(sessionId, out _));
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
    public async Task QueueOutput_ThousandsOfTinyContiguousFrames_DoNotTripItemCountOverflow()
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

        var accepted = 0;
        for (var i = 0; i < 2_000; i++)
        {
            var buffer = SharedOutputBuffer.Rent(128);
            Assert.True(client.QueueOutput(
                "session-1",
                (ulong)(32 * 1024 + ((i + 1) * 128)),
                120,
                30,
                buffer));
            accepted++;
        }

        Assert.Equal(2_000, accepted);
        socket.ReleaseSends();
    }

    [Fact]
    public async Task RemoveSession_PurgesBufferAndRejectsLateOutput()
    {
        var sessionActive = true;
        using var socket = new RecordingWebSocket();
        await using var client = new MuxClient(
            "client-1",
            socket,
            () => TerminalResumeModeSetting.FullReplay,
            sessionExists: _ => sessionActive);
        const string sessionId = "closing1";
        client.SetActiveSession(sessionId);

        Assert.True(client.QueueOutput(sessionId, 4, 120, 30, RentOutput("data")));
        await WaitForAsync(() => socket.SentFrames.Count == 1);
        Assert.True(client.HasSessionBufferForTests(sessionId));

        sessionActive = false;
        await client.RemoveSessionAsync(sessionId).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(client.HasSessionBufferForTests(sessionId));

        var late = RentOutput("late");
        Assert.False(client.QueueOutput(sessionId, 8, 120, 30, late));
        Assert.True(late.IsReleased);
    }

    [Fact]
    public async Task ContinuousBackgroundBacklog_CannotStarveActiveFlush()
    {
        const string activeSessionId = "active01";
        const string backgroundSessionId = "backgr01";
        using var socket = new ActiveFrameGateWebSocket(activeSessionId);
        var client = new MuxClient(
            "client-1",
            socket,
            () => TerminalResumeModeSetting.FullReplay);
        SharedOutputBuffer? lateBackgroundBuffer = null;

        try
        {
            client.SetActiveSession(activeSessionId);
            client.SetBackgroundSessions(new HashSet<string>(StringComparer.Ordinal) { backgroundSessionId });

            Assert.True(client.QueueOutput(activeSessionId, 1, 120, 30, RentOutput("1")));
            await socket.FirstActiveFrameStarted.WaitAsync(TimeSpan.FromSeconds(2));

            for (var sequence = 1; sequence <= 63; sequence++)
            {
                Assert.True(client.QueueOutput(backgroundSessionId, (ulong)sequence, 120, 30, RentOutput("b")));
            }

            var activePayload = new string('2', MuxProtocol.CompressionThreshold);
            Assert.True(client.QueueOutput(
                activeSessionId,
                1 + MuxProtocol.CompressionThreshold,
                120,
                30,
                RentOutput(activePayload)));
            for (var sequence = 64; sequence <= 500; sequence++)
            {
                var buffer = RentOutput("b");
                Assert.True(client.QueueOutput(backgroundSessionId, (ulong)sequence, 120, 30, buffer));
                lateBackgroundBuffer = buffer;
            }

            Assert.NotNull(lateBackgroundBuffer);
            socket.ReleaseFirstActiveFrame();
            await socket.SecondActiveFrameStarted.WaitAsync(TimeSpan.FromSeconds(2));

            // The live pump may drain and transfer background buffers to the
            // owned writer queue while the first physical send is blocked. The
            // writer must still select the second active frame ahead of them.
            Assert.True(lateBackgroundBuffer.IsReleased);
            socket.ReleaseSecondActiveFrame();
        }
        finally
        {
            socket.ReleaseAll();
            await client.DisposeAsync();
        }

        Assert.NotNull(lateBackgroundBuffer);
        Assert.True(lateBackgroundBuffer.IsReleased);
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
    public void ResolveReplayMaxBytes_DoesNotTruncateRetainedCursorDeltas()
    {
        var session = new SessionInfo { Cols = 120, Rows = 40, ShellType = "pwsh" };
        var maxBytes = MuxWebSocketHandler.ResolveReplayMaxBytes(
            session,
            replayRows: 40,
            quickResume: true,
            configuredScrollbackBytes: 2 * 1024 * 1024,
            sinceSequence: 12345);

        Assert.Null(maxBytes);
    }

    [Fact]
    public void AlternateScreenReplay_RequestsRedrawWhenFullRingLostInitialFrame()
    {
        var snapshot = new TtyHostBufferSnapshot
        {
            SequenceStart = 2_000_000,
            TerminalState = new TerminalReplayState(1049),
            Data = [1, 2, 3]
        };

        Assert.True(MuxWebSocketHandler.RequiresScreenRedraw(null, snapshot));
    }

    [Fact]
    public void AlternateScreenReplay_RequestsRedrawWhenResumeCursorCannotBeReconciled()
    {
        var snapshot = new TtyHostBufferSnapshot
        {
            SequenceStart = 2_000_000,
            TerminalState = new TerminalReplayState(1049),
            Data = [1, 2, 3]
        };

        Assert.True(MuxWebSocketHandler.RequiresScreenRedraw(1_900_000, snapshot));
        Assert.False(MuxWebSocketHandler.RequiresScreenRedraw(2_000_000, snapshot));
    }

    [Fact]
    public void AlternateScreenReplay_DoesNotRedrawCompleteOrLineBasedSnapshots()
    {
        var completeAlternateScreen = new TtyHostBufferSnapshot
        {
            SequenceStart = 0,
            TerminalState = new TerminalReplayState(1049),
            Data = [1, 2, 3]
        };
        var lineBasedTail = new TtyHostBufferSnapshot
        {
            SequenceStart = 2_000_000,
            TerminalState = TerminalReplayState.Default,
            Data = [1, 2, 3]
        };

        Assert.False(MuxWebSocketHandler.RequiresScreenRedraw(null, completeAlternateScreen));
        Assert.False(MuxWebSocketHandler.RequiresScreenRedraw(null, lineBasedTail));
    }

    [Fact]
    public void InlineAnimatedReplay_RequestsRedrawAfterSparkleEvictsStaticScreen()
    {
        // Real Codex composer frames are cursor-addressed Braille/color deltas in
        // the NORMAL buffer. There is no alternate-screen entry and no newline.
        var snapshot = new TtyHostBufferSnapshot
        {
            SequenceStart = 2_000_000,
            TerminalState = TerminalReplayState.Default,
            Data = Encoding.UTF8.GetBytes("\x1b[?2026h\x1b[48;1H\x1b[38;2;90;90;90m⠁\x1b[?2026l")
        };

        Assert.True(MuxWebSocketHandler.RequiresScreenRedraw(null, snapshot));
        Assert.True(MuxWebSocketHandler.RequiresScreenRedraw(1_900_000, snapshot));
        // A browser retaining its screen can apply a contiguous sparkle delta.
        Assert.False(MuxWebSocketHandler.RequiresScreenRedraw(2_000_000, snapshot));
    }

    [Fact]
    public void InlineAnimatedReplay_DoesNotRedrawWhenInitialScreenIsRetained()
    {
        var snapshot = new TtyHostBufferSnapshot
        {
            SequenceStart = 0,
            TerminalState = TerminalReplayState.Default,
            Data = Encoding.UTF8.GetBytes("Static conversation\r\n\x1b[?2026h⠁\x1b[?2026l")
        };

        Assert.False(MuxWebSocketHandler.RequiresScreenRedraw(null, snapshot));
        Assert.False(MuxWebSocketHandler.RequiresScreenRedraw(0, snapshot));
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

    [Fact]
    public async Task BackgroundSessionOutput_IsBatchedAtLowFrequency()
    {
        using var socket = new RecordingWebSocket();
        await using var client = new MuxClient(
            "client-1",
            socket,
            () => TerminalResumeModeSetting.FullReplay);

        const string sessionId = "session1";
        client.SetActiveSession("active01");
        client.SetBackgroundSessions(new HashSet<string>(StringComparer.Ordinal) { sessionId });

        Assert.True(client.QueueOutput(sessionId, 1, 120, 30, RentOutput("a")));
        await Task.Delay(80);
        Assert.Empty(socket.SentFrames);

        await WaitForAsync(() => socket.SentFrames.Count == 1);
        Assert.InRange(client.GetFlushDelay(sessionId), 150, 1_000);
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

    private sealed class ActiveFrameGateWebSocket : FakeWebSocket
    {
        private readonly string _activeSessionId;
        private readonly TaskCompletionSource _firstActiveFrameStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondActiveFrameStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstActiveFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSecondActiveFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeFrameCount;

        public ActiveFrameGateWebSocket(string activeSessionId)
        {
            _activeSessionId = activeSessionId;
        }

        public Task FirstActiveFrameStarted => _firstActiveFrameStarted.Task;
        public Task SecondActiveFrameStarted => _secondActiveFrameStarted.Task;

        public void ReleaseFirstActiveFrame() => _releaseFirstActiveFrame.TrySetResult();
        public void ReleaseSecondActiveFrame() => _releaseSecondActiveFrame.TrySetResult();

        public void ReleaseAll()
        {
            ReleaseFirstActiveFrame();
            ReleaseSecondActiveFrame();
        }

        public override async Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            var frame = buffer.AsSpan();
            if (!MuxProtocol.TryParseFrame(frame, out var type, out var sessionId, out _)
                || type != MuxProtocol.TypeTerminalOutput
                || !string.Equals(sessionId, _activeSessionId, StringComparison.Ordinal))
            {
                return;
            }

            var activeFrameNumber = Interlocked.Increment(ref _activeFrameCount);
            if (activeFrameNumber == 1)
            {
                _firstActiveFrameStarted.TrySetResult();
                await _releaseFirstActiveFrame.Task.WaitAsync(cancellationToken);
            }
            else if (activeFrameNumber == 2)
            {
                _secondActiveFrameStarted.TrySetResult();
                await _releaseSecondActiveFrame.Task.WaitAsync(cancellationToken);
            }
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
