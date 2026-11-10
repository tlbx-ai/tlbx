using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services;

internal static class LauncherPathResolver
{
    public static string ResolveHomePath(MidTermSettings settings)
    {
        if (OperatingSystem.IsWindows())
        {
            var configuredProfile = AppServerControlHostEnvironmentResolver.ResolveWindowsProfileDirectory(
                settings.RunAsUser,
                settings.RunAsUserSid);

            if (!string.IsNullOrWhiteSpace(configuredProfile) && Directory.Exists(configuredProfile))
            {
                return configuredProfile;
            }
        }
        else if (!string.IsNullOrWhiteSpace(settings.RunAsUser))
        {
            var unixHome = OperatingSystem.IsMacOS()
                ? Path.Combine("/Users", settings.RunAsUser)
                : Path.Combine("/home", settings.RunAsUser);
            if (Directory.Exists(unixHome))
            {
                return unixHome;
            }
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public static string ResolveStartPath(MidTermSettings settings)
    {
        var homePath = ResolveHomePath(settings);
        return TryResolveConfiguredStartPath(settings.DefaultWorkingDirectory, homePath) ?? homePath;
    }

    internal static string? TryResolveConfiguredStartPath(string? configuredPath, string homePath)
    {
        var expandedPath = ExpandLauncherPath(configuredPath, homePath);
        if (string.IsNullOrWhiteSpace(expandedPath) || !Path.IsPathRooted(expandedPath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(expandedPath);
        return Directory.Exists(fullPath) ? fullPath : null;
    }

    private static string ExpandLauncherPath(string? path, string homePath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') ||
             (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            trimmed = trimmed[1..^1];
        }

        if (!string.IsNullOrWhiteSpace(homePath) && trimmed.StartsWith('~', StringComparison.Ordinal))
        {
            trimmed = trimmed.Length == 1
                ? homePath
                : trimmed[1] is '/' or '\\'
                    ? Path.Combine(homePath, trimmed[2..])
                    : trimmed;
        }

        return Environment.ExpandEnvironmentVariables(trimmed);
    }
}
