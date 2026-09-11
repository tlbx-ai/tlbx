using System.Text;
using Ai.Tlbx.MidTerm.TtyHost;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TerminalColorQueryGuardTests
{
    [Fact]
    public void FreshColorResponse_IsForwardedOnce()
    {
        var time = new FakeTimeProvider();
        var guard = new TerminalColorQueryGuard(time);
        guard.ObservePtyOutput(Encoding.ASCII.GetBytes("\x1b]10;?\x1b\\"));
        var response = Encoding.ASCII.GetBytes("\x1b]10;rgb:f2f2/f2f2/f2f2\x1b\\");

        Assert.Null(guard.FilterClientInput(response));
        Assert.Empty(guard.FilterClientInput(response)!);
    }

    [Fact]
    public void ReplayedColorQueryResponse_AfterApplicationTimeout_IsSuppressed()
    {
        var time = new FakeTimeProvider();
        var guard = new TerminalColorQueryGuard(time);
        guard.ObservePtyOutput(Encoding.ASCII.GetBytes("\x1b]11;?\x1b\\"));

        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Empty(guard.FilterClientInput(
            Encoding.ASCII.GetBytes("\x1b]11;rgb:0c0c/0c0c/0c0c\x1b\\"))!);
    }

    [Fact]
    public void QueryScanner_HandlesSplitOscAndBellTerminator()
    {
        var time = new FakeTimeProvider();
        var guard = new TerminalColorQueryGuard(time);
        guard.ObservePtyOutput(Encoding.ASCII.GetBytes("before\x1b]1"));
        guard.ObservePtyOutput(Encoding.ASCII.GetBytes("2;?\u0007after"));

        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Empty(guard.FilterClientInput(
            Encoding.ASCII.GetBytes("\x1b]12;rgb:ffff/0000/ffff\x07"))!);
    }

    [Fact]
    public void WindowsStartupReply_AfterShortProbeWindow_IsSuppressed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var time = new FakeTimeProvider();
        var guard = new TerminalColorQueryGuard(time);
        guard.ObservePtyOutput("\x1b]10;?;?\x1b\\"u8);
        time.Advance(TimeSpan.FromMilliseconds(75));

        Assert.Empty(guard.FilterClientInput(
            "\x1b]10;rgb:f2f2/f2f2/f2f2\x1b\\\x1b]11;rgb:0c0c/0c0c/0c0c\x1b\\"u8)!);
    }

    [Fact]
    public void StackedForegroundAndBackgroundQueries_AreTrackedSeparately()
    {
        var time = new FakeTimeProvider();
        var guard = new TerminalColorQueryGuard(time);
        guard.ObservePtyOutput(Encoding.ASCII.GetBytes("\x1b]10;?;?\x1b\\"));

        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Empty(guard.FilterClientInput(
            Encoding.ASCII.GetBytes("\x1b]10;rgb:ffff/ffff/ffff\x1b\\"))!);
        Assert.Empty(guard.FilterClientInput(
            Encoding.ASCII.GetBytes("\x1b]11;rgb:0000/0000/0000\x1b\\"))!);
    }

    [Fact]
    public void OrdinaryInputIsPreservedAndUnsolicitedResponsesAreSuppressed()
    {
        var guard = new TerminalColorQueryGuard(new FakeTimeProvider());
        guard.ObservePtyOutput(Encoding.ASCII.GetBytes("\x1b]10;?\x1b\\"));

        Assert.Null(guard.FilterClientInput("hello"u8));
        Assert.Null(guard.FilterClientInput("\x1b[A"u8));
        Assert.Empty(guard.FilterClientInput(
            Encoding.ASCII.GetBytes("\x1b]11;rgb:0c0c/0c0c/0c0c\x1b\\"))!);
    }

    [Fact]
    public void ConcatenatedReplayedResponsesAreRemovedWithoutDroppingOrdinaryInput()
    {
        var guard = new TerminalColorQueryGuard(new FakeTimeProvider());
        var input = Encoding.ASCII.GetBytes(
            "before\x1b]10;rgb:f2f2/f2f2/f2f2\x1b\\\x1b]11;rgb:0c0c/0c0c/0c0c\u0007after");

        Assert.Equal("beforeafter", Encoding.ASCII.GetString(guard.FilterClientInput(input)!));
    }

    [Fact]
    public void ConcatenatedFreshResponsesAreForwardedOnceThenFilteredAsDuplicates()
    {
        var guard = new TerminalColorQueryGuard(new FakeTimeProvider());
        guard.ObservePtyOutput(Encoding.ASCII.GetBytes("\x1b]10;?;?\x1b\\"));
        var responses = Encoding.ASCII.GetBytes(
            "\x1b]10;rgb:f2f2/f2f2/f2f2\x1b\\\x1b]11;rgb:0c0c/0c0c/0c0c\x1b\\");

        Assert.Null(guard.FilterClientInput(responses));
        Assert.Empty(guard.FilterClientInput(responses)!);
    }
}
