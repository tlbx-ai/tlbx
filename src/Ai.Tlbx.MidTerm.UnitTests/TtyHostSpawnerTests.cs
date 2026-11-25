using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Ai.Tlbx.MidTerm.Common.Shells;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TtyHostSpawnerTests : IDisposable
{
    private readonly string _tempDir;

    public TtyHostSpawnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"midterm_ttyhost_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task DetachForIpcKeepsStderrReadableAfterLaunchHandleDisposal()
    {
        var windows = OperatingSystem.IsWindows();
        var start = new ProcessStartInfo(windows ? "cmd.exe" : "/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (windows) start.ArgumentList.Add("/d");
        start.ArgumentList.Add(windows ? "/c" : "-c");
        start.ArgumentList.Add("echo agent-host-stderr 1>&2");

        Assert.True(TtyHostSpawner.RedirectedProcessHandle.TryStartOwnedDirect(start, out var launched, out var failure), failure);
        var resources = launched!.DetachForIpc();
        launched.Dispose();

#pragma warning disable IDISP007 // DetachForIpc transfers ownership to the caller
        using var process = resources.Process;
        using var stderr = resources.Error;
#pragma warning restore IDISP007
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal("agent-host-stderr", (await stderr.ReadLineAsync(timeout.Token))?.Trim());
        await process.WaitForExitAsync(timeout.Token);
    }

    [Fact]
    public void ResolveTtyHostPath_PrefersEnvironmentOverride()
    {
        var overridePath = Path.Combine(_tempDir, "installed", OperatingSystem.IsWindows() ? "mthost.exe" : "mthost");
        Directory.CreateDirectory(Path.GetDirectoryName(overridePath)!);
        File.WriteAllText(overridePath, "stub");

        var resolved = TtyHostSpawner.ResolveTtyHostPath(
            currentExePath: Path.Combine(_tempDir, "debug", OperatingSystem.IsWindows() ? "mt.exe" : "mt"),
            overridePath: overridePath);

        Assert.Equal(Path.GetFullPath(overridePath), resolved);
    }

    [Fact]
    public void ResolveTtyHostPath_UsesSiblingHostInCurrentBinaryDirectory_WhenPresent()
    {
        var binDir = Path.Combine(_tempDir, "publish");
        Directory.CreateDirectory(binDir);
        var currentExePath = Path.Combine(binDir, OperatingSystem.IsWindows() ? "mt.exe" : "mt");
        var ttyHostPath = Path.Combine(binDir, OperatingSystem.IsWindows() ? "mthost.exe" : "mthost");
        File.WriteAllText(currentExePath, "mt");
        File.WriteAllText(ttyHostPath, "mthost");

        var resolved = TtyHostSpawner.ResolveTtyHostPath(currentExePath);

        Assert.Equal(ttyHostPath, resolved);
    }

    [Theory]
    [InlineData(@"CONTOSO\johannes.schmidt", "johannes.schmidt")]
    [InlineData("johannes.schmidt@contoso.local", "johannes.schmidt")]
    [InlineData("johannes.schmidt", "johannes.schmidt")]
    [SupportedOSPlatform("windows")]
    public void IsMatchingWindowsUsername_NormalizesConfiguredIdentityFormats(string configuredUser, string sessionUser)
    {
        Assert.True(TtyHostSpawner.IsMatchingWindowsUsername(configuredUser, sessionUser));
    }

    [Fact]
    public void AddTtyHostEnvironmentOverrides_EncodesInitialCommandWithoutMarkingItForShellInheritance()
    {
        const string initialCommand = "codex --yolo";

        var environment = TtyHostSpawner.AddTtyHostEnvironmentOverrides(
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["TLBX_TEST"] = "present" },
            initialCommand);

        Assert.NotNull(environment);
        Assert.Equal("present", environment["TLBX_TEST"]);
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(initialCommand)),
            environment[TtyHostSpawner.InitialCommandEnvironmentVariable]);
        Assert.DoesNotContain(
            TtyHostSpawner.InitialCommandEnvironmentVariable,
            environment[TerminalEnvironmentOverrides.OverrideKeysEnvironmentVariable],
            StringComparison.Ordinal);
        Assert.Contains(
            "TLBX_TEST",
            environment[TerminalEnvironmentOverrides.OverrideKeysEnvironmentVariable],
            StringComparison.Ordinal);
    }
}
