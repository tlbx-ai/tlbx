using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class BoundedSessionOutputQueueTests
{
    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP016:Don't use disposed instance", Justification = "The post-disposal TryEnqueue assertion verifies the late-callback shutdown contract.")]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP017:Prefer using", Justification = "The explicit early Dispose is required to verify the post-disposal enqueue contract; the declaration also guarantees cleanup on earlier assertion failure.")]
    public void TryEnqueue_EnforcesOneGlobalCapacityAndDrainsExactlyOnce()
    {
        using var queue = new BoundedSessionOutputQueue<int>(capacity: 3, activeBurstLimit: 2);

        Assert.True(queue.TryEnqueue("one", 1));
        Assert.True(queue.TryEnqueue("two", 2));
        Assert.True(queue.TryEnqueue("three", 3));
        Assert.False(queue.TryEnqueue("four", 4));
        Assert.Equal(3, queue.Count);

        queue.Complete();
        Assert.False(queue.TryEnqueue("five", 5));

        var drained = queue.Drain();
        Assert.Equal(3, drained.Count);
        Assert.Equal(new[] { 1, 2, 3 }, drained.Order());
        Assert.Equal(0, queue.Count);
        Assert.Empty(queue.Drain());

        queue.Dispose();
        Assert.False(queue.TryEnqueue("late-callback", 6));
    }

    [Fact]
    public async Task ActiveSession_BypassesBackgroundBacklog_WithoutReorderingItsOwnFrames()
    {
        using var queue = new BoundedSessionOutputQueue<string>(capacity: 32, activeBurstLimit: 4);
        for (var i = 0; i < 10; i++)
        {
            Assert.True(queue.TryEnqueue("background", $"b{i.ToString(CultureInfo.InvariantCulture)}"));
        }
        Assert.True(queue.TryEnqueue("active", "a1"));
        Assert.True(queue.TryEnqueue("active", "a2"));

        var active = new HashSet<string>(StringComparer.Ordinal) { "active" };
        Assert.Equal("a1", await ReadAsync(queue, active));
        Assert.Equal("a2", await ReadAsync(queue, active));
        Assert.Equal("b0", await ReadAsync(queue, active));
    }

    [Fact]
    public async Task ActiveBurstLimit_GuaranteesBackgroundProgress()
    {
        using var queue = new BoundedSessionOutputQueue<string>(capacity: 32, activeBurstLimit: 3);
        for (var i = 0; i < 8; i++)
        {
            Assert.True(queue.TryEnqueue("active", $"a{i.ToString(CultureInfo.InvariantCulture)}"));
        }
        Assert.True(queue.TryEnqueue("background", "b0"));

        var active = new HashSet<string>(StringComparer.Ordinal) { "active" };
        Assert.Equal("a0", await ReadAsync(queue, active));
        Assert.Equal("a1", await ReadAsync(queue, active));
        Assert.Equal("a2", await ReadAsync(queue, active));
        Assert.Equal("b0", await ReadAsync(queue, active));
        Assert.Equal("a3", await ReadAsync(queue, active));
    }

    [Fact]
    public async Task EmptyWait_IsCancelable_AndCompletionWakesConsumer()
    {
        using var queue = new BoundedSessionOutputQueue<int>(capacity: 4, activeBurstLimit: 2);
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await queue.WaitToReadAsync(cts.Token));
        }

        var completionWait = queue.WaitToReadAsync(CancellationToken.None).AsTask();
        queue.Complete();
        Assert.False(await completionWait.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ByteCapacity_BoundsPayloadIndependentlyFromItemCount()
    {
        using var queue = new BoundedSessionOutputQueue<string>(
            capacity: 10,
            activeBurstLimit: 2,
            byteCapacity: 5,
            measureBytes: static item => item.Length);

        Assert.True(queue.TryEnqueue("one", "aa"));
        Assert.True(queue.TryEnqueue("two", "bbb"));
        Assert.False(queue.TryEnqueue("three", "c"));
        Assert.Equal(5, queue.QueuedBytes);

        Assert.Equal("aa", await ReadAsync(queue, new HashSet<string>(StringComparer.Ordinal)));
        Assert.Equal(3, queue.QueuedBytes);
        Assert.True(queue.TryEnqueue("three", "cc"));
    }

    [Fact]
    public async Task TryEnqueueOrMerge_CoalescesSameSessionBeyondItemCapacityWithoutBreakingByteLimit()
    {
        using var queue = new BoundedSessionOutputQueue<string>(
            capacity: 1,
            activeBurstLimit: 1,
            byteCapacity: 5,
            measureBytes: static item => item.Length);

        Assert.True(queue.TryEnqueue("session", "a"));
        Assert.True(queue.TryEnqueueOrMerge("session", "bc", Concatenate));
        Assert.Equal(1, queue.Count);
        Assert.Equal(3, queue.QueuedBytes);
        Assert.False(queue.TryEnqueueOrMerge("session", "def", Concatenate));
        Assert.False(queue.TryEnqueueOrMerge("other", "d", Concatenate));

        Assert.Equal("abc", await ReadAsync(queue, new HashSet<string>(StringComparer.Ordinal)));
        Assert.Equal(0, queue.QueuedBytes);
    }

    [Fact]
    public async Task RemoveSession_PurgesOnlyOwnedItemsAndKeepsQueueUsable()
    {
        using var queue = new BoundedSessionOutputQueue<string>(
            capacity: 10,
            activeBurstLimit: 2,
            byteCapacity: 20,
            measureBytes: static item => item.Length);

        Assert.True(queue.TryEnqueue("closed", "one"));
        Assert.True(queue.TryEnqueue("kept", "two"));
        Assert.True(queue.TryEnqueue("closed", "three"));

        Assert.Equal(new[] { "one", "three" }, queue.RemoveSession("closed"), StringComparer.Ordinal);
        Assert.Equal(1, queue.Count);
        Assert.Equal(3, queue.QueuedBytes);
        Assert.Equal("two", await ReadAsync(queue, new HashSet<string>(StringComparer.Ordinal)));

        Assert.True(queue.TryEnqueue("new", "four"));
        Assert.Equal("four", await ReadAsync(queue, new HashSet<string>(StringComparer.Ordinal)));
    }

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP013:Await in using", Justification = "The test awaits all producers and its single consumer before disposing the shared queue.")]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP017:Prefer using", Justification = "The timeout source is disposed in finally after every task using its token has completed or faulted.")]
    public async Task ConcurrentProducers_FinishWithoutLossDuplicatesOrDeadlock()
    {
        const int sessionCount = 4;
        const int itemsPerSession = 250;
        const int totalItems = sessionCount * itemsPerSession;
        using var queue = new BoundedSessionOutputQueue<SequenceItem>(capacity: 64, activeBurstLimit: 8);
        var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var active = new HashSet<string>(StringComparer.Ordinal) { "session-0" };
        var received = new List<SequenceItem>(totalItems);
        var sessionIds = new[] { "session-0", "session-1", "session-2", "session-3" };

        try
        {
            var consumer = Task.Run(async () =>
            {
                while (received.Count < totalItems)
                {
                    Assert.True(await queue.WaitToReadAsync(timeout.Token));
                    Assert.True(queue.TryDequeue(active, out var item));
                    received.Add(item);
                }
            }, timeout.Token);

            var producers = Enumerable.Range(0, sessionCount).Select(sessionNumber => Task.Run(async () =>
            {
                var sessionId = sessionIds[sessionNumber];
                for (var sequence = 0; sequence < itemsPerSession; sequence++)
                {
                    while (!queue.TryEnqueue(sessionId, new SequenceItem(sessionId, sequence)))
                    {
                        await Task.Yield();
                        timeout.Token.ThrowIfCancellationRequested();
                    }
                }
            }, timeout.Token)).ToArray();

            await Task.WhenAll(producers);
            await consumer;
        }
        finally
        {
            timeout.Dispose();
        }

        Assert.Equal(totalItems, received.Count);
        Assert.Equal(totalItems, received.Distinct().Count());
        Assert.Equal(0, queue.Count);
        for (var sessionNumber = 0; sessionNumber < sessionCount; sessionNumber++)
        {
            var sessionId = sessionIds[sessionNumber];
            Assert.Equal(
                Enumerable.Range(0, itemsPerSession),
                received.Where(item => item.SessionId == sessionId).Select(item => item.Sequence));
        }
    }

    private static async Task<T> ReadAsync<T>(
        BoundedSessionOutputQueue<T> queue,
        IReadOnlySet<string> activeSessionIds)
    {
        Assert.True(await queue.WaitToReadAsync(CancellationToken.None));
        Assert.True(queue.TryDequeue(activeSessionIds, out var item));
        return item;
    }

    private readonly record struct SequenceItem(string SessionId, int Sequence);

    private static bool Concatenate(string existing, string incoming, out string merged)
    {
        merged = existing + incoming;
        return true;
    }
}
