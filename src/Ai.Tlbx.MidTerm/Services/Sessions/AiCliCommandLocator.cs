using System.Text.RegularExpressions;
using Ai.Tlbx.MidTerm.Models.Sessions;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

internal static partial class AiCliCommandLocator
{
    [GeneratedRegex(@"(?ix)(?<path>[A-Z]:[^\r\n""']*?node_modules[\\/]@openai[\\/]codex[\\/]bin[\\/]codex\.js)", RegexOptions.None, 1000)]
    private static partial Regex CodexScriptPathRegex();

    [GeneratedRegex(@"[A-Za-z]:\\[^\r\n""']+", RegexOptions.None, 1000)]
    private static partial Regex AbsoluteWindowsPathTokenRegex();

    public static string? ResolveExecutablePath(string profile, SessionInfoDto session, string? userProfileDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return profile switch
        {
            AiCliProfileService.CodexProfile => ResolveCodexExecutablePath(session, userProfileDirectory),
            AiCliProfileService.ClaudeProfile => ResolveExecutablePathFromForegroundCommand(session.ForegroundCommandLine, "claude")
                                               ?? FindExecutableInPath("claude", userProfileDirectory),
            AiCliProfileService.GrokProfile => ResolveExecutablePathFromForegroundCommand(session.ForegroundCommandLine, "grok")
                                             ?? FindExecutableInPath("grok", userProfileDirectory),
            _ => null
        };
    }

    internal static string? ResolveExecutablePathFromForegroundCommand(string? commandLine, string commandName)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        foreach (var candidate in ExtractAbsolutePathTokens(commandLine))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var fileName = Path.GetFileNameWithoutExtension(candidate);
            if (string.Equals(fileName, commandName, StringComparison.OrdinalIgnoreCase))
            {
                var extension = Path.GetExtension(candidate);
                if (OperatingSystem.IsWindows() &&
                    !string.IsNullOrWhiteSpace(extension) &&
                    extension is not ".exe" and not ".cmd" and not ".bat" and not ".ps1")
                {
                    continue;
                }

                return candidate;
            }
        }

        return null;
    }

    internal static string? ResolveCodexWrapperFromScriptPath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var match = CodexScriptPathRegex().Match(commandLine);
        if (!match.Success)
        {
            return null;
        }

        var scriptPath = NormalizePath(match.Groups["path"].Value);
        if (!File.Exists(scriptPath))
        {
            return null;
        }

        var npmBinDirectory = TryResolveNpmBinDirectory(scriptPath);
        if (npmBinDirectory is null)
        {
            return null;
        }

        foreach (var candidate in GetPreferredCommandNames("codex"))
        {
            var fullPath = Path.Combine(npmBinDirectory, candidate);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return null;
    }

    internal static string? FindExecutableInPath(string commandName, string? userProfileDirectory = null)
    {
        if (Path.IsPathRooted(commandName) && File.Exists(commandName))
        {
            return commandName;
        }

        foreach (var directory in EnumerateSearchDirectories(userProfileDirectory))
        {
            foreach (var candidateName in GetPreferredCommandNames(commandName))
            {
                var fullPath = Path.Combine(directory, candidateName);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> GetUserCommandDirectories(string? userProfileDirectory)
    {
        var profileDirectory = ResolveEffectiveUserProfileDirectory(userProfileDirectory);
        if (string.IsNullOrWhiteSpace(profileDirectory))
        {
            return [];
        }

        var directories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void AddDirectory(List<string> target, HashSet<string> seenDirectories, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var normalized = value.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(normalized) || !seenDirectories.Add(normalized))
            {
                return;
            }

            target.Add(normalized);
        }

        AddDirectory(directories, seen, Path.Combine(profileDirectory, ".local", "bin"));
        AddDirectory(directories, seen, Path.Combine(profileDirectory, ".grok", "bin"));

        if (!OperatingSystem.IsWindows())
        {
            AddDirectory(directories, seen, Path.Combine(profileDirectory, "bin"));
            return directories;
        }

        if (OperatingSystem.IsWindows())
        {
            var appDataDirectory = Path.Combine(profileDirectory, "AppData", "Roaming");
            var localAppDataDirectory = Path.Combine(profileDirectory, "AppData", "Local");
            foreach (var directory in GetWellKnownWindowsCommandDirectories(appDataDirectory, localAppDataDirectory, profileDirectory))
            {
                AddDirectory(directories, seen, directory);
            }
        }

        return directories;
    }

    internal static IReadOnlyList<string> GetWellKnownUnixCommandDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            return [];
        }

        return
        [
            "/opt/homebrew/bin",
            "/opt/homebrew/sbin",
            "/usr/local/bin",
            "/usr/local/sbin",
            "/opt/local/bin",
            "/opt/local/sbin",
            "/usr/bin",
            "/bin",
            "/usr/sbin",
            "/sbin"
        ];
    }

    internal static string BuildFallbackPath(string? userProfileDirectory = null)
    {
        return string.Join(Path.PathSeparator, EnumerateFallbackDirectories(userProfileDirectory));
    }

    internal static IReadOnlyList<string> GetWellKnownWindowsCommandDirectories(
        string? appData = null,
        string? localAppData = null,
        string? userProfile = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var directories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void AddDirectory(List<string> target, HashSet<string> seenDirectories, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var normalized = value.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(normalized) || !seenDirectories.Add(normalized))
            {
                return;
            }

            target.Add(normalized);
        }

        AddDirectory(directories, seen, TryCombine(appData, "npm"));
        AddDirectory(directories, seen, TryCombine(localAppData, "Programs", "nodejs"));
        AddDirectory(directories, seen, TryCombine(userProfile, ".grok", "bin"));

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            AddDirectory(directories, seen, Path.Combine(userProfile, "AppData", "Roaming", "npm"));
            AddDirectory(directories, seen, Path.Combine(userProfile, "AppData", "Local", "Programs", "nodejs"));
            AddDirectory(directories, seen, Path.Combine(userProfile, ".grok", "bin"));
        }

        return directories;
    }

    private static string? ResolveCodexExecutablePath(SessionInfoDto session, string? userProfileDirectory)
    {
        return ResolveCodexWrapperFromScriptPath(session.ForegroundCommandLine)
               ?? ResolveExecutablePathFromForegroundCommand(session.ForegroundCommandLine, "codex")
               ?? FindExecutableInPath("codex", userProfileDirectory);
    }

    private static IEnumerable<string> ExtractAbsolutePathTokens(string commandLine)
    {
        foreach (Match match in AbsoluteWindowsPathTokenRegex().Matches(commandLine))
        {
            if (match.Success)
            {
                yield return NormalizePath(match.Value);
            }
        }
    }

    private static string NormalizePath(string path)
    {
        return path.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
    }

    private static IEnumerable<string> EnumerateSearchDirectories(string? userProfileDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in GetUserCommandDirectories(userProfileDirectory))
        {
            if (seen.Add(directory))
            {
                yield return directory;
            }
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathVar))
        {
            foreach (var rawDirectory in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var directory = rawDirectory.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(directory) && seen.Add(directory))
                {
                    yield return directory;
                }
            }
        }

        foreach (var directory in GetWellKnownWindowsCommandDirectories(
                     Environment.GetEnvironmentVariable("APPDATA"),
                     Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                     Environment.GetEnvironmentVariable("USERPROFILE")))
        {
            if (seen.Add(directory))
            {
                yield return directory;
            }
        }

        foreach (var directory in GetWellKnownUnixCommandDirectories())
        {
            if (seen.Add(directory))
            {
                yield return directory;
            }
        }
    }

    private static IEnumerable<string> EnumerateFallbackDirectories(string? userProfileDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in GetUserCommandDirectories(userProfileDirectory))
        {
            if (seen.Add(directory))
            {
                yield return directory;
            }
        }

        foreach (var directory in GetWellKnownWindowsCommandDirectories(
                     Environment.GetEnvironmentVariable("APPDATA"),
                     Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                     Environment.GetEnvironmentVariable("USERPROFILE")))
        {
            if (seen.Add(directory))
            {
                yield return directory;
            }
        }

        foreach (var directory in GetWellKnownUnixCommandDirectories())
        {
            if (seen.Add(directory))
            {
                yield return directory;
            }
        }
    }

    private static string? ResolveEffectiveUserProfileDirectory(string? userProfileDirectory)
    {
        if (!string.IsNullOrWhiteSpace(userProfileDirectory))
        {
            return userProfileDirectory;
        }

        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return home;
        }

        var currentProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(currentProfile) ? null : currentProfile;
    }

    private static string? TryCombine(string? root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        return Path.Combine([root, .. parts]);
    }

    private static string? TryResolveNpmBinDirectory(string scriptPath)
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(scriptPath)!);
        for (var i = 0; i < 4 && current.Parent is not null; i++)
        {
            current = current.Parent;
        }

        return Directory.Exists(current.FullName) ? current.FullName : null;
    }

    private static string[] GetPreferredCommandNames(string commandName)
    {
        if (!string.IsNullOrWhiteSpace(Path.GetExtension(commandName)))
        {
            return [commandName];
        }

        if (!OperatingSystem.IsWindows())
        {
            return [commandName];
        }

        var pathext = Environment.GetEnvironmentVariable("PATHEXT");
        var extensions = new List<string> { ".exe", ".cmd", ".bat", ".ps1" };
        if (!string.IsNullOrWhiteSpace(pathext))
        {
            extensions.AddRange(pathext.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return extensions
            .Select(ext => commandName + ext.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
