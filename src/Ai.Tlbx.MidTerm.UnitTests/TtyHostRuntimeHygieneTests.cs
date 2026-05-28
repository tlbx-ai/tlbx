using System.Threading.Channels;
using Xunit;
using TtyHostProgram = Ai.Tlbx.MidTerm.TtyHost.Program;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TtyHostRuntimeHygieneTests
{
    [Fact]
    public void ClientWriteChannel_UsesBackpressureInsteadOfSuccessfulDrop()
    {
        Assert.Equal(BoundedChannelFullMode.Wait, TtyHostProgram.ClientWriteChannelFullMode);
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
