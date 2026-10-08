using System.Diagnostics;
using Ai.Tlbx.MidTerm.Common.Process;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class MidTermProcessPriorityTests
{
    [Fact]
    public async Task WindowsPriorityJob_CoversChildrenAndGrandchildrenAndCanBeDisabled()
    {
        if (!OperatingSystem.IsWindows()) return;
        var gate = Path.Combine(Path.GetTempPath(), $"tlbx-priority-{Guid.NewGuid():N}");
        var pidFile = gate + ".pid";
        var grandchildPidFile = gate + ".grandchild";
        var childScript = gate + ".ps1";
        File.WriteAllText(childScript, $"[IO.File]::WriteAllText('{pidFile}',[string]$PID); while(!(Test-Path -LiteralPath '{gate}')){{Start-Sleep -Milliseconds 20}}; $p=Start-Process pwsh -WindowStyle Hidden -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 30' -PassThru; [IO.File]::WriteAllText('{grandchildPidFile}',[string]$p.Id); Start-Sleep -Seconds 30");
        using var job = new WindowsProcessPriorityJob();
        job.SetPriority(ProcessPriorityClass.AboveNormal);
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"Start-Process pwsh -WindowStyle Hidden -ArgumentList '-NoProfile','-File','{childScript}' -PassThru | Out-Null; Start-Sleep -Seconds 30");
        using var parent = System.Diagnostics.Process.Start(start)!;
        System.Diagnostics.Process? child = null;
        System.Diagnostics.Process? grandchild = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(pidFile)) await Task.Delay(20, timeout.Token);
            child = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture));
            job.Assign(parent);
            File.WriteAllText(gate, "go");
            while (!File.Exists(grandchildPidFile)) await Task.Delay(20, timeout.Token);
            grandchild = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(grandchildPidFile), System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(ProcessPriorityClass.AboveNormal, parent.PriorityClass);
            Assert.Equal(ProcessPriorityClass.AboveNormal, child.PriorityClass);
            Assert.Equal(ProcessPriorityClass.AboveNormal, grandchild.PriorityClass);
            try
            {
                job.SetPriority(ProcessPriorityClass.High);
                parent.Refresh();
                child.Refresh();
                grandchild.Refresh();
                Assert.Equal(ProcessPriorityClass.High, parent.PriorityClass);
                Assert.Equal(ProcessPriorityClass.High, child.PriorityClass);
                Assert.Equal(ProcessPriorityClass.High, grandchild.PriorityClass);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 1300 or 1314)
            {
                // High job priority requires an administrator/service token.
                Assert.Equal(ProcessPriorityClass.AboveNormal, child.PriorityClass);
            }
            job.SetPriority(ProcessPriorityClass.Normal);
            parent.Refresh();
            child.Refresh();
            grandchild.Refresh();
            Assert.Equal(ProcessPriorityClass.Normal, parent.PriorityClass);
            Assert.Equal(ProcessPriorityClass.Normal, child.PriorityClass);
            Assert.Equal(ProcessPriorityClass.Normal, grandchild.PriorityClass);
        }
        finally
        {
            if (!parent.HasExited) parent.Kill(entireProcessTree: true);
            child?.Dispose();
            grandchild?.Dispose();
            File.Delete(gate);
            File.Delete(pidFile);
            File.Delete(grandchildPidFile);
            File.Delete(childScript);
        }
    }

    [Theory]
    [InlineData("normal", ProcessPriorityClass.Normal)]
    [InlineData("realtime", ProcessPriorityClass.AboveNormal)]
    public void ResolvePriorityClass_AllowsOnlyConservativeKnownClasses(
        string? input,
        ProcessPriorityClass expected)
    {
        Assert.Equal(expected, MidTermProcessPriority.ResolvePriorityClass(input));
    }

    [Fact]
    public void ShouldSetPriority_AllowsUserToLowerHighButPreservesRealtimeProcesses()
    {
        Assert.True(MidTermProcessPriority.ShouldSetPriority(
            ProcessPriorityClass.High,
            ProcessPriorityClass.AboveNormal));
        Assert.False(MidTermProcessPriority.ShouldSetPriority(
            ProcessPriorityClass.RealTime,
            ProcessPriorityClass.AboveNormal));
    }

    [Fact]
    public void ShouldSetPriority_RaisesNormalToAboveNormal()
    {
        Assert.True(MidTermProcessPriority.ShouldSetPriority(
            ProcessPriorityClass.Normal,
            ProcessPriorityClass.AboveNormal));
    }
}
