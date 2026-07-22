using System.Runtime.Versioning;
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
}
