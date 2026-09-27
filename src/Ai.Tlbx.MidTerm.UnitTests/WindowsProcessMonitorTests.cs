using System.Runtime.Versioning;
using Ai.Tlbx.MidTerm.TtyHost.Process;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

[SupportedOSPlatform("windows")]
public sealed class WindowsProcessMonitorTests
{
    [Theory]
    [InlineData(0, 1999, false, WindowsProcessMonitor.ActivePollIntervalMs)]
    [InlineData(0, 2000, false, Timeout.Infinite)]
    [InlineData(0, 2000, true, WindowsProcessMonitor.IdlePollIntervalMs)]
    public void ResolvePollDelay_StopsIdleShellsAndRetainsFallbackForChildProcesses(
        long lastActivityMs,
        long nowMs,
        bool hasForegroundChild,
        int expectedDelayMs)
    {
        Assert.Equal(
            expectedDelayMs,
            WindowsProcessMonitor.ResolvePollDelay(lastActivityMs, nowMs, hasForegroundChild));
    }
}
