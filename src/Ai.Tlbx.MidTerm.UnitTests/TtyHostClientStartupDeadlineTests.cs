using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using Ai.Tlbx.MidTerm.Common.Ipc;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

[Collection(TimingSensitiveCollection.Name)]
public sealed class TtyHostClientStartupDeadlineTests
{
    [Fact]
    [SuppressMessage("Usage", "MA0040:Use a cancellation token", Justification = "The assertion must observe the server task's own cancellation, not cancel the wait independently.")]
    public async Task ConnectAsync_BoundsAttachHandshakeWhenHostNeverAcknowledges()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var instanceId = Guid.NewGuid().ToString("N");
        var hostPid = Environment.ProcessId;
        var endpoint = IpcEndpoint.GetSessionEndpoint(instanceId, sessionId, hostPid);
        await using var server = new NamedPipeServerStream(
            endpoint,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        using var serverCancellation = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(serverCancellation.Token);
            var request = new byte[512];
            _ = await server.ReadAsync(request, serverCancellation.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, serverCancellation.Token);
        }, serverCancellation.Token);

        await using var client = new TtyHostClient(
            sessionId,
            hostPid,
            instanceId,
            "owner-token",
            initialHandshakeTimeoutMs: 100);
        var timer = Stopwatch.StartNew();

        var connected = await client.ConnectAsync(timeoutMs: 500, maxAttempts: 1);

        Assert.False(connected);
        Assert.InRange(timer.ElapsedMilliseconds, 50, 1000);
        serverCancellation.Cancel();
        try
        {
            await serverTask;
            Assert.Fail("The deliberately stalled pipe server should be cancelled.");
        }
        catch (OperationCanceledException)
        {
            // Expected: cancellation tears down the test server after the client deadline fired.
        }
    }
}
