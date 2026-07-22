using System.Diagnostics;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Settings;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class AppServerControlHostEnvironmentResolverTests
{
    [Fact]
    public void ApplyProfileEnvironment_PopulatesEnvironmentAndCollectsExistingPathEntries()
    {
        var profileDirectory = Path.Combine(Path.GetTempPath(), $"midterm-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profileDirectory);

        try
        {
            var expectedPathEntries = AiCliCommandLocator.GetUserCommandDirectories(profileDirectory)
                .Where(Directory.Exists)
                .ToList();
            var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = @"C:\Windows\System32"
            };
            var pathPrependEntries = new List<string>();

            AppServerControlHostEnvironmentResolver.ApplyProfileEnvironment(
                environment,
                profileDirectory,
                pathPrependEntries);

            Assert.Equal(profileDirectory, environment["USERPROFILE"]);
            Assert.Equal(profileDirectory, environment["HOME"]);
            Assert.Equal(Path.Combine(profileDirectory, ".codex"), environment["CODEX_HOME"]);
            Assert.Equal(Path.Combine(profileDirectory, "AppData", "Roaming"), environment["APPDATA"]);
            Assert.Equal(Path.Combine(profileDirectory, "AppData", "Local"), environment["LOCALAPPDATA"]);
            Assert.Equal(expectedPathEntries, pathPrependEntries);
        }
        finally
        {
            try
            {
                Directory.Delete(profileDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void ApplyUserProfileEnvironment_DoesNothing_WhenRunAsUserMissing()
    {
        var startInfo = new ProcessStartInfo();
        var settings = new MidTermSettings();
        var originalUserProfile = startInfo.Environment.TryGetValue("USERPROFILE", out var userProfile)
            ? userProfile
            : null;
        var originalCodexHome = startInfo.Environment.TryGetValue("CODEX_HOME", out var codexHome)
            ? codexHome
            : null;

        AppServerControlHostEnvironmentResolver.ApplyUserProfileEnvironment(startInfo, settings);

        Assert.Equal(originalUserProfile, startInfo.Environment.TryGetValue("USERPROFILE", out var currentUserProfile) ? currentUserProfile : null);
        Assert.Equal(originalCodexHome, startInfo.Environment.TryGetValue("CODEX_HOME", out var currentCodexHome) ? currentCodexHome : null);
    }

    [Fact]
    public void ResolveWindowsProfileDirectory_FallsBackToUsersRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var profileDirectory = AppServerControlHostEnvironmentResolver.ResolveWindowsProfileDirectory("johan", userSid: null);

        Assert.NotNull(profileDirectory);
        Assert.EndsWith(Path.Combine("Users", "johan"), profileDirectory!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveWindowsProfileDirectory_StripsWindowsDomainWhenFallingBackToUsersRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var profileDirectory = AppServerControlHostEnvironmentResolver.ResolveWindowsProfileDirectory(
            @"CONTOSO\johan",
            userSid: null);

        Assert.NotNull(profileDirectory);
        Assert.EndsWith(Path.Combine("Users", "johan"), profileDirectory!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveWindowsProfileDirectory_DoesNotFallbackToSystemProfileParent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var profileDirectory = AppServerControlHostEnvironmentResolver.ResolveWindowsProfileDirectory(
            @"CONTOSO\johannes.schmidt",
            userSid: null);

        Assert.NotNull(profileDirectory);
        Assert.DoesNotContain(
            Path.Combine("system32", "config"),
            profileDirectory!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveWindowsProfileDirectoryFromExecutablePath_ReturnsOwningUserProfile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var profileDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var executablePath = Path.Combine(profileDirectory, "AppData", "Roaming", "npm", "codex.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);

        try
        {
            var resolved = AppServerControlHostEnvironmentResolver.ResolveWindowsProfileDirectoryFromExecutablePath(executablePath);

            Assert.Equal(profileDirectory, resolved);
        }
        finally
        {
            if (File.Exists(executablePath))
            {
                File.Delete(executablePath);
            }
        }
    }

    [Fact]
    public void ApplyUserProfileEnvironment_PrependsCommonUserCliPaths()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var currentProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userName = Path.GetFileName(currentProfile.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["PATH"] = @"C:\Windows\System32";

        var settings = new MidTermSettings
        {
            RunAsUser = userName
        };

        AppServerControlHostEnvironmentResolver.ApplyUserProfileEnvironment(startInfo, settings);

        Assert.StartsWith(
            Path.Combine(currentProfile, ".local", "bin") + Path.PathSeparator,
            startInfo.Environment["PATH"],
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            Path.PathSeparator + Path.Combine(currentProfile, "AppData", "Roaming", "npm") + Path.PathSeparator,
            startInfo.Environment["PATH"],
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            Path.PathSeparator + Path.Combine(currentProfile, "AppData", "Local", "Programs", "nodejs") + Path.PathSeparator,
            startInfo.Environment["PATH"],
            StringComparison.OrdinalIgnoreCase);
    }
}
