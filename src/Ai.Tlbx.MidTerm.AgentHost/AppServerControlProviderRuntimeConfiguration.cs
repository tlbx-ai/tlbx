using System.Diagnostics;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal static class AppServerControlProviderRuntimeConfiguration
{
    private const string CodexYoloDefaultEnvironmentVariable = "MIDTERM_APP_SERVER_CONTROL_CODEX_YOLO_DEFAULT";
    private const string CodexDefaultModelEnvironmentVariable = "MIDTERM_APP_SERVER_CONTROL_CODEX_DEFAULT_MODEL";
    private const string CodexEnvironmentVariablesEnvironmentVariable = "MIDTERM_APP_SERVER_CONTROL_CODEX_ENVIRONMENT_VARIABLES";
    private const string ClaudeDefaultModelEnvironmentVariable = "MIDTERM_APP_SERVER_CONTROL_CLAUDE_DEFAULT_MODEL";
    private const string ClaudeEnvironmentVariablesEnvironmentVariable = "MIDTERM_APP_SERVER_CONTROL_CLAUDE_ENVIRONMENT_VARIABLES";
    private const string ClaudeDangerouslySkipPermissionsEnvironmentVariable = "MIDTERM_APP_SERVER_CONTROL_CLAUDE_DANGEROUSLY_SKIP_PERMISSIONS";

    public static void ApplyUserProfileEnvironment(ProcessStartInfo startInfo, string? profileDirectory)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        if (!OperatingSystem.IsWindows() ||
            string.IsNullOrWhiteSpace(profileDirectory) ||
            !Directory.Exists(profileDirectory))
        {
            return;
        }

        startInfo.Environment["USERPROFILE"] = profileDirectory;
        startInfo.Environment["HOME"] = profileDirectory;
        startInfo.Environment["CODEX_HOME"] = Path.Combine(profileDirectory, ".codex");

        var root = Path.GetPathRoot(profileDirectory);
        if (!string.IsNullOrWhiteSpace(root))
        {
            startInfo.Environment["HOMEDRIVE"] = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            startInfo.Environment["HOMEPATH"] = profileDirectory[root.Length..];
        }

        var appDataDirectory = Path.Combine(profileDirectory, "AppData", "Roaming");
        var localAppDataDirectory = Path.Combine(profileDirectory, "AppData", "Local");
        startInfo.Environment["APPDATA"] = appDataDirectory;
        startInfo.Environment["LOCALAPPDATA"] = localAppDataDirectory;

        foreach (var directory in GetUserCommandDirectories(profileDirectory).Reverse())
        {
            PrependPath(startInfo, directory);
        }
    }

    public static void ApplyEnvironmentVariables(ProcessStartInfo startInfo, string provider)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        foreach (var pair in ReadEnvironmentVariables(provider))
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }
    }

    public static bool GetClaudeDangerouslySkipPermissionsDefault()
    {
        return bool.TryParse(
                   Environment.GetEnvironmentVariable(ClaudeDangerouslySkipPermissionsEnvironmentVariable),
                   out var enabled) &&
               enabled;
    }

    public static bool GetCodexYoloDefault()
    {
        return bool.TryParse(
            Environment.GetEnvironmentVariable(CodexYoloDefaultEnvironmentVariable),
            out var enabled) &&
               enabled;
    }

    public static string? GetCodexDefaultModel()
    {
        return NormalizeOptionalValue(Environment.GetEnvironmentVariable(CodexDefaultModelEnvironmentVariable))
               ?? "gpt-5.5";
    }

    public static string? GetClaudeDefaultModel()
    {
        return NormalizeOptionalValue(Environment.GetEnvironmentVariable(ClaudeDefaultModelEnvironmentVariable));
    }

    private static IReadOnlyDictionary<string, string> ReadEnvironmentVariables(string provider)
    {
        var raw = provider switch
        {
            "codex" => Environment.GetEnvironmentVariable(CodexEnvironmentVariablesEnvironmentVariable),
            "claude" => Environment.GetEnvironmentVariable(ClaudeEnvironmentVariablesEnvironmentVariable),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(raw))
        {
            return Empty;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IsValidEnvironmentVariableLine(rawLine))
            {
                continue;
            }

            var separator = rawLine.IndexOf("=", StringComparison.Ordinal);
            var key = rawLine[..separator];
            var value = rawLine[(separator + 1)..];
            result[key] = value;
        }

        return result;
    }

    private static bool IsValidEnvironmentVariableLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var separator = line.IndexOf("=", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        var key = line[..separator];
        if (!(char.IsLetter(key[0]) || key[0] == '_'))
        {
            return false;
        }

        for (var i = 1; i < key.Length; i++)
        {
            var ch = key[i];
            if (!(char.IsLetterOrDigit(ch) || ch == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static string? NormalizeOptionalValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static IReadOnlyDictionary<string, string> Empty { get; } = new Dictionary<string, string>(0, StringComparer.Ordinal);

    private static IEnumerable<string> GetUserCommandDirectories(string profileDirectory)
    {
        yield return Path.Combine(profileDirectory, "AppData", "Roaming", "npm");
        yield return Path.Combine(profileDirectory, "AppData", "Local", "Programs", "nodejs");
        yield return Path.Combine(profileDirectory, ".local", "bin");
    }

    private static void PrependPath(ProcessStartInfo startInfo, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        var existingPath = startInfo.Environment.TryGetValue("PATH", out var currentPath)
            ? currentPath ?? string.Empty
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        var parts = existingPath
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Contains(directory, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        startInfo.Environment["PATH"] = string.IsNullOrWhiteSpace(existingPath)
            ? directory
            : directory + Path.PathSeparator + existingPath;
    }
}
