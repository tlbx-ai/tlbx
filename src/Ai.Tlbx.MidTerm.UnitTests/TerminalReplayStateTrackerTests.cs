using System.Text;
using Xunit;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.TtyHost;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TerminalReplayStateTrackerTests
{
    [Fact]
    public void InputModes_SurviveTrimAndSplitCommands_AtExactReplayBoundary()
    {
        var tracker = new TerminalReplayStateTracker();
        var first = Encoding.ASCII.GetBytes("\u001b[?1049h\u001b[?1003;100");
        var second = Encoding.ASCII.GetBytes("6;2004;1004h");
        tracker.Consume(first, 0);
        tracker.TrimBefore((ulong)first.Length);
        Assert.Equal(new TerminalReplayState(1049, 0x8000), tracker.GetStateAt((ulong)first.Length));
        tracker.Consume(second, (ulong)first.Length);
        var end = (ulong)(first.Length + second.Length);
        Assert.Equal(new TerminalReplayState(1049, 0x8000), tracker.GetStateAt(end - 1));
        Assert.Equal(new TerminalReplayState(1049, 0x8194), tracker.GetStateAt(end));
        tracker.TrimBefore(end);
        Assert.Equal(new TerminalReplayState(1049, 0x8194), tracker.GetStateAt(end + 100));
    }

    [Fact]
    public void InputModes_FollowXtermProtocolSelectionAndReset()
    {
        var tracker = new TerminalReplayStateTracker();
        var data = Encoding.ASCII.GetBytes("\u001b[?1003;1006;2004h\u001b[?1002h\u001b[?1000l\u001b[?1005h");
        tracker.Consume(data, 0);
        var end = (ulong)data.Length;
        // Resetting any tracking mode disables tracking, while unsupported UTF8
        // encoding must leave the active SGR encoding unchanged, like xterm.
        Assert.Equal(new TerminalReplayState(0, 0x8090), tracker.GetStateAt(end));
        tracker.Consume(Encoding.ASCII.GetBytes("\u001bc"), end);
        Assert.Equal(new TerminalReplayState(0, 0x8000), tracker.GetStateAt(end + 2));
    }
}
