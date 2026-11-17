using Ai.Tlbx.MidTerm.Services.Connect;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class ConnectFrameSenderTests
{
    [Fact]
    public async Task ReadyInteractiveBurstDrainsBeforeTheNextBulkFragment()
    {
        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = new List<byte[]>();
        using var sender = new ConnectFrameSender(async (frame, ct) =>
        {
            frames.Add(frame.ToArray());
            if (frames.Count == 1)
            {
                firstFrame.SetResult();
                await resume.Task.WaitAsync(ct);
            }
        }, CancellationToken.None);
        var bulk = sender.SendAsync(new byte[200000], CancellationToken.None);
        await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var replies = Enumerable.Range(0, 8)
            .Select(async index => await sender.SendAsync([(byte)index], CancellationToken.None)).ToArray();
        resume.SetResult();
        await Task.WhenAll(replies.Append(bulk))
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var receiver = new ConnectFrames();
        var complete = frames.Select(frame => receiver.Receive(frame)).OfType<byte[]>().ToArray();
        Assert.Equal(9, complete.Length);
        Assert.All(complete.Take(8), reply => Assert.Single(reply));
        Assert.Equal(200000, complete[8].Length);
        // All eight queued replies must pass before a second bulk fragment.
        Assert.All(frames.Skip(1).Take(8), frame => Assert.Equal(14, frame.Length));
    }

    [Fact]
    public async Task SmallReplyCompletesBeforeAnInFlightBulkMessage()
    {
        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = new List<byte[]>();
        using var sender = new ConnectFrameSender(async (frame, ct) =>
        {
            frames.Add(frame.ToArray());
            if (frames.Count == 1)
            {
                firstFrame.SetResult();
                await resume.Task.WaitAsync(ct);
            }
        }, CancellationToken.None);
        var bulk = new byte[200000];
        Random.Shared.NextBytes(bulk);
        var reply = new byte[] { 1, 2, 3 };
        var bulkSend = sender.SendAsync(bulk, CancellationToken.None);
        await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var replySend = sender.SendAsync(reply, CancellationToken.None);
        resume.SetResult();
        await Task.WhenAll(bulkSend, replySend).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Reassembly uses the unchanged receiver rules and must retain every byte.
        var receiver = new ConnectFrames();
        var complete = frames.Select(frame => receiver.Receive(frame)).OfType<byte[]>().ToArray();
        Assert.Equal(2, complete.Length);
        Assert.Equal(reply, complete[0]);
        Assert.Equal(bulk, complete[1]);
    }

    [Fact]
    public async Task CallerCancellationAfterFirstFragmentDoesNotStrandItsAssembly()
    {
        using var caller = new CancellationTokenSource();
        var receiver = new ConnectFrames();
        byte[]? complete = null;
        using var sender = new ConnectFrameSender((frame, _) =>
        {
            complete = receiver.Receive(frame.Span) ?? complete;
            caller.Cancel();
            return Task.CompletedTask;
        }, CancellationToken.None);
        var payload = new byte[100000];
        Random.Shared.NextBytes(payload);
        await sender.SendAsync(payload, caller.Token);
        Assert.Equal(payload, complete);
    }

    [Fact]
    public void ReceiverAcceptsLegacyAndSmallFragmentsInterleaved()
    {
        var encoder = new ConnectFrames();
        var legacy = new byte[100000];
        var small = new byte[20000];
        Random.Shared.NextBytes(legacy);
        Random.Shared.NextBytes(small);
        var legacyFrames = encoder.Encode(legacy).ToArray();
        var smallFrames = encoder.Encode(small, ConnectFrameSender.FragmentPayloadSize).ToArray();
        Assert.Equal(ConnectFrames.HeaderSize + 48 * 1024, legacyFrames[0].Length);
        var receiver = new ConnectFrames();
        Assert.Null(receiver.Receive(legacyFrames[0]));
        byte[]? result = null;
        foreach (var frame in smallFrames) result = receiver.Receive(frame) ?? result;
        Assert.Equal(small, result);
        foreach (var frame in legacyFrames.Skip(1)) result = receiver.Receive(frame) ?? result;
        Assert.Equal(legacy, result);
    }
}
