using System.Collections.Concurrent;
using Ai.Tlbx.MidTerm.Services.WebSockets;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class MuxInboundDispatcherTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task SlowReplayAndSlowIpcDoNotBlockPeerInputAndMarkersStayOrdered()
    {
        await using var dispatcher = new MuxInboundDispatcher(_ => Assert.Fail("Unexpected work failure"), CancellationToken.None);
        var replay = Signal();
        var input = Signal();
        var peer = Signal();
        var order = new ConcurrentQueue<int>();
        await dispatcher.EnqueueAsync("recovery:a", async ct => { replay.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); });
        await dispatcher.EnqueueAsync("input:a", async ct => { input.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }, 10);
        await Task.WhenAll(replay.Task, input.Task).WaitAsync(TimeSpan.FromSeconds(2));
        await dispatcher.EnqueueAsync("input:b", _ => { order.Enqueue(1); return Task.CompletedTask; }, 4);
        await dispatcher.EnqueueAsync("input:b", _ => { order.Enqueue(2); peer.TrySetResult(); return Task.CompletedTask; }, 10);
        await peer.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { 1, 2 }, order);
    }

    [Fact]
    public async Task SaturationWaitsAndResumesAdmissionWithoutDroppingTheWaitingWork()
    {
        await using var dispatcher = new MuxInboundDispatcher(_ => Assert.Fail("Unexpected work failure"), CancellationToken.None);
        var release = Signal();
        var entered = Signal();
        var all = Signal();
        var completed = 0;
        await dispatcher.EnqueueAsync("input:a", async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); }, MuxInboundDispatcher.MaxBytes);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var admission = dispatcher.EnqueueAsync("input:b", _ => { Interlocked.Increment(ref completed); all.TrySetResult(); return Task.CompletedTask; }, 1).AsTask();
        Assert.False(admission.IsCompleted);
        release.TrySetResult();
        await admission.WaitAsync(TimeSpan.FromSeconds(2));
        await all.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, completed);
    }

    [Theory]
    [InlineData(1, 0)] // Full replay cannot be replaced by a delta request.
    [InlineData(0, -1)] // An explicit delta cannot be replaced by a visibility hint.
    public async Task RepeatedRecoveryRequestsKeepOneStrongestFollowup(int strongest, int weaker)
    {
        await using var dispatcher = new MuxInboundDispatcher(_ => Assert.Fail("Unexpected work failure"), CancellationToken.None);
        var release = Signal();
        var entered = Signal();
        var followup = Signal();
        var calls = new ConcurrentQueue<string>();
        await dispatcher.EnqueueAsync("recovery:a", async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); }, mergePriority: 0);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await dispatcher.EnqueueAsync("recovery:a", _ => { calls.Enqueue("strongest"); followup.TrySetResult(); return Task.CompletedTask; }, mergePriority: strongest);
        for (var i = 0; i < 100; i++)
            await dispatcher.EnqueueAsync("recovery:a", _ => { calls.Enqueue("weaker"); return Task.CompletedTask; }, mergePriority: weaker);
        release.TrySetResult();
        await followup.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "strongest" }, calls, StringComparer.Ordinal);
    }

    [Fact]
    public async Task ConcurrentProducersDrainEveryAcceptedItemInSessionOrder()
    {
        await using var dispatcher = new MuxInboundDispatcher(_ => Assert.Fail("Unexpected work failure"), CancellationToken.None);
        var done = new TaskCompletionSource[4];
        var results = new ConcurrentQueue<int>[4];
        var producers = Enumerable.Range(0, 4).Select(async index =>
        {
            done[index] = Signal();
            results[index] = new ConcurrentQueue<int>();
            for (var i = 0; i < 250; i++)
            {
                var value = i;
                await dispatcher.EnqueueAsync(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"input:{index}"), _ =>
                {
                    results[index].Enqueue(value);
                    if (value == 249) done[index].TrySetResult();
                    return Task.CompletedTask;
                }, 32);
            }
        });
        await Task.WhenAll(producers).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(done.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var result in results) Assert.Equal(Enumerable.Range(0, 250), result);
    }

    [Fact]
    public async Task CompletionDrainsAcceptedInputInsteadOfCancellingIt()
    {
        await using var dispatcher = new MuxInboundDispatcher(_ => Assert.Fail("Unexpected work failure"), CancellationToken.None);
        var entered = Signal();
        var release = Signal();
        var output = new ConcurrentQueue<int>();
        await dispatcher.EnqueueAsync("input:a", async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); output.Enqueue(1); }, 10);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await dispatcher.EnqueueAsync("input:a", _ => { output.Enqueue(2); return Task.CompletedTask; }, 10);
        var closing = dispatcher.CompleteAsync(CancellationToken.None);
        Assert.False(closing.IsCompleted);
        release.TrySetResult();
        await closing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { 1, 2 }, output);
    }

    [Fact]
    public async Task DisposeCancelsBlockedWorkAndPendingAdmission()
    {
        var dispatcher = new MuxInboundDispatcher(_ => Assert.Fail("Unexpected work failure"), CancellationToken.None);
        var entered = Signal();
        await dispatcher.EnqueueAsync("input:a", async ct => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }, MuxInboundDispatcher.MaxBytes);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var admission = dispatcher.EnqueueAsync("input:b", _ => Task.CompletedTask, 1).AsTask();
        await dispatcher.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admission);
    }
}
