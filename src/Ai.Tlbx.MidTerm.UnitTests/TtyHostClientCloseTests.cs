using System.Diagnostics;
using System.Reflection;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;
namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TtyHostClientCloseTests
{
    [Fact]
    public async Task DisconnectedUnverifiedProcessIsNotReportedClosedOrKilled()
    {
        await using var client = new TtyHostClient("unverified", Environment.ProcessId);
        Assert.False(await client.CloseAsync());
    }

    [Fact]
    public async Task ExplicitCloseTerminatesVerifiedOriginalHostWhenIpcIsDisconnected()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = Process.Start(new ProcessStartInfo("pwsh", "-NoProfile -Command Start-Sleep -Seconds 60")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        using var outputLifetime = new CancellationTokenSource();
        var stdout = host.StandardOutput.ReadToEndAsync(outputLifetime.Token);
        var stderr = host.StandardError.ReadToEndAsync(outputLifetime.Token);
        try
        {
            await using var client = new TtyHostClient("verified", host.Id);
            typeof(TtyHostClient).GetMethod("RememberHostCapabilities", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(client, [new SessionInfo { Id = "verified" }]);
            Assert.True(await client.CloseAsync(outputLifetime.Token).WaitAsync(TimeSpan.FromSeconds(5), outputLifetime.Token));
            Assert.True(host.HasExited);
        }
        finally
        {
            if (!host.HasExited) host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync(outputLifetime.Token);
            // A Windows console helper can retain an inherited pipe handle after
            // the verified child exits. Its lifetime must not keep this test alive.
            outputLifetime.Cancel();
            try { await Task.WhenAll(stdout, stderr); }
            catch (OperationCanceledException) { }
        }
    }
}
