using System.Diagnostics;
using System.Runtime.Versioning;
using Ai.Tlbx.MidTerm.Settings;
using Ai.Tlbx.MidTerm.Services.Security;

#if WINDOWS
using Microsoft.Win32;
#endif

namespace Ai.Tlbx.MidTerm.Services.Sessions;

internal static class AppServerControlHostEnvironmentResolver
{
    public static void ApplyUserProfileEnvironment(ProcessStartInfo startInfo, MidTermSettings settings)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(settings);

        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(settings.RunAsUser))
        {
            return;
        }

        var profileDirectory = ResolveWindowsProfileDirectory(settings.RunAsUser, settings.RunAsUserSid);
        ApplyProfileEnvironment(startInfo, profileDirectory);
    }

    internal static void ApplyProfileEnvironment(ProcessStartInfo startInfo, string? profileDirectory)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ApplyProfileEnvironment(startInfo.Environment, profileDirectory);
    }

    internal static void ApplyProfileEnvironment(
        IDictionary<string, string?> environment,
        string? profileDirectory,
        IList<string>? pathPrependEntries = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (string.IsNullOrWhiteSpace(profileDirectory) || !Directory.Exists(profileDirectory))
        {
            return;
        }

        environment["USERPROFILE"] = profileDirectory;
        environment["HOME"] = profileDirectory;
        environment["CODEX_HOME"] = Path.Combine(profileDirectory, ".codex");

        var root = Path.GetPathRoot(profileDirectory);
        if (!string.IsNullOrWhiteSpace(root))
        {
            environment["HOMEDRIVE"] = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            environment["HOMEPATH"] = profileDirectory[root.Length..];
        }

        var appDataDirectory = Path.Combine(profileDirectory, "AppData", "Roaming");
        var localAppDataDirectory = Path.Combine(profileDirectory, "AppData", "Local");
        environment["APPDATA"] = appDataDirectory;
        environment["LOCALAPPDATA"] = localAppDataDirectory;

        // AppServerControl runtimes often rely on per-user command shims and local bins.
        // Service environments do not always inherit those PATH entries, so add the
        // common user-local locations explicitly for standalone AppServerControl sessions.
        foreach (var directory in AiCliCommandLocator.GetUserCommandDirectories(profileDirectory).Reverse())
        {
            if (pathPrependEntries is null)
            {
                PrependPath(environment, directory);
            }
            else if (!string.IsNullOrWhiteSpace(directory) &&
                     Directory.Exists(directory) &&
                     !pathPrependEntries.Contains(directory, StringComparer.OrdinalIgnoreCase))
            {
                pathPrependEntries.Add(directory);
            }
        }
    }

    internal static string? ResolveCurrentWindowsProfileDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(userProfile) && Directory.Exists(userProfile))
        {
            return userProfile;
        }

        var currentUserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(currentUserProfile) ? currentUserProfile : null;
    }

    internal static string? ResolveWindowsProfileDirectoryFromExecutablePath(string? executablePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        var normalizedPath = executablePath.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
        if (!Path.IsPathRooted(normalizedPath))
        {
            return null;
        }

        var root = Path.GetPathRoot(normalizedPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var relative = normalizedPath[root.Length..];
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2 || !string.Equals(segments[0], "Users", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var profileDirectory = Path.Combine(root, segments[0], segments[1]);
        return Directory.Exists(profileDirectory) ? profileDirectory : null;
    }

    internal static string? ResolveConfiguredWindowsProfileDirectory(MidTermSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(settings.RunAsUser)
            ? ResolveWindowsProfileDirectory(settings.RunAsUser, settings.RunAsUserSid)
            : null;
    }

    /// <summary>
    /// The profile a provider runtime is launched with: the configured run-as user, else the
    /// profile owning the provider executable, else the current process profile. Under an
    /// installed LocalSystem service the current profile is the system profile, so it is last.
    /// </summary>
    internal static string? ResolvePreferredWindowsProfileDirectory(MidTermSettings settings, string? executablePath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return ResolveConfiguredWindowsProfileDirectory(settings)
               ?? ResolveWindowsProfileDirectoryFromExecutablePath(executablePath)
               ?? ResolveCurrentWindowsProfileDirectory();
    }

    internal static string? ResolveWindowsProfileDirectory(string? userName, string? userSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

#if WINDOWS
        if (!string.IsNullOrWhiteSpace(userSid))
        {
            var profilePath = TryReadProfileDirectoryFromProfileList(userSid);
            if (!string.IsNullOrWhiteSpace(profilePath))
            {
                return profilePath;
            }
        }

        if (!string.IsNullOrWhiteSpace(userName))
        {
            var profilePath = TryResolveWindowsProfileDirectoryFromRegistry(userName);
            if (!string.IsNullOrWhiteSpace(profilePath))
            {
                return profilePath;
            }
        }
#endif

        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        var usersRoot = ResolveWindowsProfilesRoot();
        if (string.IsNullOrWhiteSpace(usersRoot))
        {
            return null;
        }

        var candidates = new List<string>();
        var trimmedUserName = userName.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedUserName))
        {
            candidates.Add(trimmedUserName);
        }

        var normalizedUserName = SystemUserProvider.NormalizeWindowsUsername(userName);
        if (!string.IsNullOrWhiteSpace(normalizedUserName) &&
            !candidates.Contains(normalizedUserName, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(normalizedUserName);
        }

        foreach (var candidate in candidates)
        {
            var candidatePath = Path.Combine(usersRoot, candidate);
            if (Directory.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        var fallbackLeaf = candidates.LastOrDefault();
        return string.IsNullOrWhiteSpace(fallbackLeaf)
            ? null
            : Path.Combine(usersRoot, fallbackLeaf);
    }

    private static string? ResolveWindowsProfilesRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

#if WINDOWS
        const string profileListRoot = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
        using var profileListKey = Registry.LocalMachine.OpenSubKey(profileListRoot);
        var configuredProfilesDirectory = profileListKey?.GetValue("ProfilesDirectory") as string;
        if (!string.IsNullOrWhiteSpace(configuredProfilesDirectory))
        {
            return Environment.ExpandEnvironmentVariables(configuredProfilesDirectory);
        }
#endif

        var currentUserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(currentUserProfile))
        {
            var parentDirectory = Directory.GetParent(currentUserProfile)?.FullName;
            if (!string.IsNullOrWhiteSpace(parentDirectory) &&
                !currentUserProfile.Contains(
                    $"{Path.DirectorySeparatorChar}system32{Path.DirectorySeparatorChar}config{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
            {
                return parentDirectory;
            }
        }

        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive");
        if (!string.IsNullOrWhiteSpace(systemDrive))
        {
            return Path.Combine(systemDrive, "Users");
        }

        return Directory.GetParent(currentUserProfile)?.FullName;
    }

#if WINDOWS
    [SupportedOSPlatform("windows")]
    private static string? TryResolveWindowsProfileDirectoryFromRegistry(string userName)
    {
        const string profileListRoot = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
        using var profileListKey = Registry.LocalMachine.OpenSubKey(profileListRoot);
        if (profileListKey is null)
        {
            return null;
        }

        var candidates = BuildWindowsProfileLeafCandidates(userName);
        if (candidates.Count == 0)
        {
            return null;
        }

        foreach (var subKeyName in profileListKey.GetSubKeyNames())
        {
            var profilePath = TryReadProfileDirectoryFromProfileList(subKeyName);
            if (string.IsNullOrWhiteSpace(profilePath))
            {
                continue;
            }

            var leafName = Path.GetFileName(
                profilePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (candidates.Contains(leafName, StringComparer.OrdinalIgnoreCase))
            {
                return profilePath;
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? TryReadProfileDirectoryFromProfileList(string subKeyName)
    {
        const string profileListRoot = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
        using var profileKey = Registry.LocalMachine.OpenSubKey(Path.Combine(profileListRoot, subKeyName));
        var profilePath = profileKey?.GetValue("ProfileImagePath") as string;
        return string.IsNullOrWhiteSpace(profilePath)
            ? null
            : Environment.ExpandEnvironmentVariables(profilePath);
    }
#endif

    private static List<string> BuildWindowsProfileLeafCandidates(string userName)
    {
        var candidates = new List<string>();
        var trimmedUserName = userName.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedUserName))
        {
            candidates.Add(trimmedUserName);
        }

        var normalizedUserName = SystemUserProvider.NormalizeWindowsUsername(userName);
        if (!string.IsNullOrWhiteSpace(normalizedUserName) &&
            !candidates.Contains(normalizedUserName, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(normalizedUserName);
        }

        return candidates;
    }

    private static void PrependPath(IDictionary<string, string?> environment, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var existingPath = environment.TryGetValue("PATH", out var currentPath)
            ? currentPath ?? string.Empty
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        var parts = existingPath
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Contains(directory, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        environment["PATH"] = string.IsNullOrWhiteSpace(existingPath)
            ? directory
            : directory + Path.PathSeparator + existingPath;
    }
}
