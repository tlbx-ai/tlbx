using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

[Collection(PathSensitiveEnvironmentCollection.Name)]
public sealed class AiCliCommandLocatorTests
{
    [Fact]
    public void ResolveExecutablePath_UsesAbsoluteForegroundExecutableWhenPathIsMissing()
    {
        using var fakeCodex = FakeCodexPathScope.Create();
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            var session = new SessionInfoDto
            {
                ForegroundCommandLine = $"\"{fakeCodex.ExecutablePath}\" --yolo"
            };

            var resolved = AiCliCommandLocator.ResolveExecutablePath(AiCliProfileService.CodexProfile, session);

            Assert.Equal(fakeCodex.ExecutablePath, resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public async Task ResolveExecutablePath_DerivesCodexWrapperFromForegroundScriptPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "midterm-codex-locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var npmBin = Path.Combine(root, "npm");
            var scriptDirectory = Path.Combine(npmBin, "node_modules", "@openai", "codex", "bin");
            Directory.CreateDirectory(scriptDirectory);

            var scriptPath = Path.Combine(scriptDirectory, "codex.js");
            await File.WriteAllTextAsync(scriptPath, "// fake codex");

            var wrapperPath = Path.Combine(npmBin, OperatingSystem.IsWindows() ? "codex.cmd" : "codex");
            await File.WriteAllTextAsync(wrapperPath, "@echo off");

            var originalPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", string.Empty);
                var session = new SessionInfoDto
                {
                    ForegroundCommandLine = $"node \"{scriptPath}\" --yolo"
                };

                var resolved = AiCliCommandLocator.ResolveExecutablePath(AiCliProfileService.CodexProfile, session);

                Assert.Equal(wrapperPath, resolved);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void ResolveExecutablePath_FallsBackToStandardWindowsNpmDirectory_WhenPathIsMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "midterm-codex-well-known-" + Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(root, "AppData", "Roaming");
        var npmDirectory = Path.Combine(appData, "npm");
        Directory.CreateDirectory(npmDirectory);

        var wrapperPath = Path.Combine(npmDirectory, "codex.cmd");
        File.WriteAllText(wrapperPath, "@echo off");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalAppData = Environment.GetEnvironmentVariable("APPDATA");
        var originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");

        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            Environment.SetEnvironmentVariable("APPDATA", appData);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(root, "AppData", "Local"));
            Environment.SetEnvironmentVariable("USERPROFILE", root);

            var resolved = AiCliCommandLocator.ResolveExecutablePath(
                AiCliProfileService.CodexProfile,
                new SessionInfoDto());

            Assert.Equal(wrapperPath, resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("APPDATA", originalAppData);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
            Environment.SetEnvironmentVariable("USERPROFILE", originalUserProfile);

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void ResolveExecutablePath_FallsBackToWindowsPowerShellNpmShim_WhenCmdShimIsMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "midterm-codex-ps1-shim-" + Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(root, "AppData", "Roaming");
        var npmDirectory = Path.Combine(appData, "npm");
        Directory.CreateDirectory(npmDirectory);

        var wrapperPath = Path.Combine(npmDirectory, "codex.ps1");
        File.WriteAllText(wrapperPath, "# fake codex");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalPathExt = Environment.GetEnvironmentVariable("PATHEXT");
        var originalAppData = Environment.GetEnvironmentVariable("APPDATA");
        var originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");

        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            Environment.SetEnvironmentVariable("PATHEXT", ".COM;.EXE;.BAT;.CMD");
            Environment.SetEnvironmentVariable("APPDATA", appData);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(root, "AppData", "Local"));
            Environment.SetEnvironmentVariable("USERPROFILE", root);

            var resolved = AiCliCommandLocator.ResolveExecutablePath(
                AiCliProfileService.CodexProfile,
                new SessionInfoDto());

            Assert.Equal(wrapperPath, resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("PATHEXT", originalPathExt);
            Environment.SetEnvironmentVariable("APPDATA", originalAppData);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
            Environment.SetEnvironmentVariable("USERPROFILE", originalUserProfile);

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void ResolveExecutablePath_UsesConfiguredUserProfileDirectories_WhenPathIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "midterm-cli-user-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var commandDirectory = Path.Combine(root, ".local", "bin");
        Directory.CreateDirectory(commandDirectory);
        var executablePath = Path.Combine(commandDirectory, OperatingSystem.IsWindows() ? "claude.exe" : "claude");
        File.WriteAllText(executablePath, "fake");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Empty);

            var resolved = AiCliCommandLocator.ResolveExecutablePath(
                AiCliProfileService.ClaudeProfile,
                new SessionInfoDto(),
                root);

            Assert.Equal(executablePath, resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void GetUserCommandDirectories_IncludesUnixHomeBinDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "midterm-cli-unix-home-" + Guid.NewGuid().ToString("N"));

        try
        {
            var directories = AiCliCommandLocator.GetUserCommandDirectories(root);

            Assert.Contains(Path.Combine(root, ".local", "bin"), directories, StringComparer.Ordinal);
            Assert.Contains(Path.Combine(root, "bin"), directories, StringComparer.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void BuildFallbackPath_IncludesCommonUnixInstallLocations()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = AiCliCommandLocator.BuildFallbackPath("/Users/tester");
        var entries = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Contains("/Users/tester/.local/bin", entries, StringComparer.Ordinal);
        Assert.Contains("/Users/tester/bin", entries, StringComparer.Ordinal);
        Assert.Contains("/opt/homebrew/bin", entries, StringComparer.Ordinal);
        Assert.Contains("/usr/local/bin", entries, StringComparer.Ordinal);
        Assert.Contains("/usr/bin", entries, StringComparer.Ordinal);
    }
}
