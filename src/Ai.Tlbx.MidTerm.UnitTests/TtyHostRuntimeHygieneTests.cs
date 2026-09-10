using System.Buffers;
using System.Threading.Channels;
using Xunit;
using TtyHostProgram = Ai.Tlbx.MidTerm.TtyHost.Program;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TtyHostRuntimeHygieneTests
{
    [Fact]
    public async Task ClientWriteChannel_WaitsForCapacityInsteadOfDroppingOutput()
    {
        Assert.Equal(BoundedChannelFullMode.Wait, TtyHostProgram.ClientWriteChannelFullMode);

        var channel = Channel.CreateBounded<TtyHostProgram.PooledFrame>(new BoundedChannelOptions(1)
        {
            FullMode = TtyHostProgram.ClientWriteChannelFullMode,
            SingleReader = true,
            SingleWriter = false
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.True(TtyHostProgram.EnqueueFrame(channel.Writer, "first"u8, cts.Token));

        var blockedWrite = Task.Run(() =>
            TtyHostProgram.EnqueueFrame(channel.Writer, "second"u8, cts.Token), CancellationToken.None);
        var earlyCompletion = await Task.WhenAny(blockedWrite, Task.Delay(100, cts.Token));
        Assert.NotSame(blockedWrite, earlyCompletion);

        Assert.True(channel.Reader.TryRead(out var first));
        try
        {
            Assert.Equal("first"u8.ToArray(), first.Buffer.AsSpan(0, first.Length).ToArray());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(first.Buffer);
        }

        Assert.True(await blockedWrite.WaitAsync(cts.Token));
        Assert.True(channel.Reader.TryRead(out var second));
        try
        {
            Assert.Equal("second"u8.ToArray(), second.Buffer.AsSpan(0, second.Length).ToArray());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(second.Buffer);
        }
    }

    [Fact]
    public async Task ClientWriteChannel_CancellationReleasesBlockedProducerWithoutDeadlock()
    {
        var channel = Channel.CreateBounded<TtyHostProgram.PooledFrame>(new BoundedChannelOptions(1)
        {
            FullMode = TtyHostProgram.ClientWriteChannelFullMode,
            SingleReader = true,
            SingleWriter = false
        });
        using var cts = new CancellationTokenSource();
        Assert.True(TtyHostProgram.EnqueueFrame(channel.Writer, "held"u8, CancellationToken.None));

        var blockedWrite = Task.Run(() =>
            TtyHostProgram.EnqueueFrame(channel.Writer, "cancelled"u8, cts.Token), CancellationToken.None);
        await Task.Delay(50, cts.Token);
        Assert.False(blockedWrite.IsCompleted);
        cts.Cancel();
        Assert.False(await blockedWrite.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None));

        Assert.True(channel.Reader.TryRead(out var held));
        ArrayPool<byte>.Shared.Return(held.Buffer);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public void CurrentClientPromotion_CancelsPreviousClientWithoutDisposingIt()
    {
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();

        TtyHostProgram.PromoteCurrentClient(first);
        TtyHostProgram.PromoteCurrentClient(second);

        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);

        first.Token.Register(static () => { }).Dispose();

        TtyHostProgram.ClearCurrentClientIfCurrent(second);
    }

    [Fact]
    public void CurrentClientClear_DoesNotClearNewerClient()
    {
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();

        TtyHostProgram.PromoteCurrentClient(first);
        TtyHostProgram.PromoteCurrentClient(second);
        TtyHostProgram.ClearCurrentClientIfCurrent(first);
        TtyHostProgram.PromoteCurrentClient(first);

        Assert.True(second.IsCancellationRequested);

        TtyHostProgram.ClearCurrentClientIfCurrent(first);
    }
}
