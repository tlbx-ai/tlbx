using System.Text;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TerminalCursorQueryGuardTests
{
    [Fact]
    public void LiveQuery_AllowsOnlyOneResponseAcrossBrowsers()
    {
        var guard = new TerminalCursorQueryGuard(new FakeTimeProvider());
        guard.ObservePtyOutput("\u001b[6n"u8);
        Assert.Null(guard.FilterResponse("\u001b[1;1R"u8));
        Assert.Empty(guard.FilterResponse("\u001b[1;1R"u8)!);
    }

    [Fact]
    public void ReplayedQuery_CannotAuthorizeAnotherResponse()
    {
        var time = new FakeTimeProvider();
        var guard = new TerminalCursorQueryGuard(time);
        guard.ObservePtyOutput("\u001b[6n"u8);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(guard.FilterResponse("\u001b[1;1R"u8)!);
        guard.ObservePtyOutput("\u001b[6n"u8);
        Assert.Null(guard.FilterResponse("\u001b[30;120R"u8));
    }

    [Fact]
    public void SplitQueryAndPrivateReports_AreSupported()
    {
        var guard = new TerminalCursorQueryGuard();
        foreach (var part in new[] { "text\u001b", "[?", "6", "ntext" })
            guard.ObservePtyOutput(Encoding.ASCII.GetBytes(part));
        Assert.Empty(guard.FilterResponse("\u001b[1;1R"u8)!);
        Assert.Null(guard.FilterResponse("\u001b[?2;3R"u8));
        Assert.Empty(guard.FilterResponse("\u001b[?2;3R"u8)!);
    }

    [Fact]
    public void StaleReports_AreRemovedWithoutDroppingOtherResponses()
    {
        var guard = new TerminalCursorQueryGuard();
        Assert.Equal("before\u001b[I\u001b[?1;2cafter", Encoding.ASCII.GetString(
            guard.FilterResponse("before\u001b[1;1R\u001b[I\u001b[?1;2c\u001b[?10;20Rafter"u8)!));
        Assert.Null(guard.FilterResponse("\u001bOR\u001b[A\u001b[;R\u001b[2;R"u8));
    }
}
