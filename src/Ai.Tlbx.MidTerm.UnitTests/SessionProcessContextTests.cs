using System.Diagnostics;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class SessionProcessContextTests
{
    [Fact]
    public void ResolvesNearestOwningAncestorAndNeverUsesWorkingDirectory()
    {
        var roots = new Dictionary<int, string> { [10] = "owner123", [20] = "sibling1" };
        var started = DateTime.UtcNow;
        Assert.Equal("owner123", SessionProcessContext.Resolve(12, roots,
            pid => pid switch { 12 => (11, started), 11 => (10, started), 10 => (1, started), _ => null }));
        Assert.Null(SessionProcessContext.Resolve(30, roots, _ => (1, started)));
    }

    [Fact]
    public void RejectsReusedParentPidMissingProcessAndCycles()
    {
        var roots = new Dictionary<int, string> { [10] = "owner123" };
        var started = DateTime.UtcNow;
        Assert.Null(SessionProcessContext.Resolve(12, roots,
            pid => pid == 12 ? (10, started) : (1, started.AddSeconds(1))));
        Assert.Null(SessionProcessContext.Resolve(12, roots, _ => null));
        Assert.Null(SessionProcessContext.Resolve(12, roots, _ => (12, started)));
    }

    [Fact]
    public void NativeAncestryResolvesRealChildProcess()
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
          RedirectStandardOutput = true, RedirectStandardError = true };
        if (!OperatingSystem.IsWindows()) { start.ArgumentList.Add("-c"); start.ArgumentList.Add("read line"); }
        using var child = Process.Start(start)!;
        try
        {
            Assert.Equal("owner123", SessionProcessContext.Resolve(child.Id,
                new Dictionary<int, string> { [Environment.ProcessId] = "owner123" }));
        }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
    }
}
