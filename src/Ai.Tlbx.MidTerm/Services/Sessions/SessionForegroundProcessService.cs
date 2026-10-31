using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed class SessionForegroundProcessService
{
    private static readonly HashSet<string> RuntimeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node",
        "python",
        "python3",
        "ruby",
        "java",
        "deno",
        "bun"
    };

    private static readonly HashSet<string> ShellWrapperNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd",
        "pwsh",
        "powershell",
        "bash",
        "sh",
        "zsh",
        "fish",
        "nu"
    };

    private static readonly HashSet<string> GenericScriptNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "main.js",
        "index.js",
        "cli.js",
        "main.ts",
        "index.ts",
        "cli.ts",
        "main.py",
        "index.py",
        "cli.py",
        "__main__.py",
        "main.rb",
        "index.rb",
        "cli.rb"
    };

    private static readonly HashSet<string> SkipParentDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "src",
        "lib",
        "dist",
        "build",
        "scripts"
    };

    public SessionForegroundDescriptor Describe(
        string? processName,
        string? commandLine,
        SessionAgentAttachPoint? attachPoint = null)
    {
        var displayName = FormatRuntimeDisplay(processName, commandLine);
        var processIdentity = GetProcessIdentity(processName, commandLine);
        var providerIdentity = NormalizeIdentity(attachPoint?.Provider);
        if (!string.IsNullOrWhiteSpace(providerIdentity))
        {
            processIdentity = providerIdentity;
        }

        return new SessionForegroundDescriptor(displayName, processIdentity);
    }

    public string FormatRuntimeDisplay(string? processName, string? commandLine)
    {
        var safeProcessName = processName ?? string.Empty;
        var raw = string.IsNullOrWhiteSpace(commandLine) ? safeProcessName : commandLine;
        var trimmed = raw.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        var wrapperDisplay = TryFormatShellWrapperDisplay(safeProcessName, trimmed);
        if (!string.IsNullOrWhiteSpace(wrapperDisplay))
        {
            return wrapperDisplay;
        }

        var runtimeName = ExtractRuntimeName(safeProcessName);
        if (runtimeName is null)
        {
            var baseName = NormalizeIdentity(Basename(safeProcessName));
            if (!string.IsNullOrWhiteSpace(commandLine))
            {
                var firstToken = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                var firstBase = NormalizeIdentity(Basename(firstToken));
                if (!string.Equals(firstBase, baseName, StringComparison.Ordinal))
                {
                    var stripped = StripExecutablePath(trimmed);
                    return string.IsNullOrWhiteSpace(stripped)
                        ? baseName
                        : $"{baseName} {stripped}".Trim();
                }
            }

            return StripExecutablePath(trimmed);
        }

        var tokens = Tokenize(trimmed);
        if (tokens.Count == 0)
        {
            return StripExecutablePath(trimmed);
        }

        var index = 0;
        if (IsRuntimeToken(tokens[0]))
        {
            index++;
        }

        if (index >= tokens.Count)
        {
            return runtimeName;
        }

        var current = tokens[index];
        if (runtimeName.StartsWith("python", StringComparison.Ordinal) &&
            string.Equals(current, "-m", StringComparison.Ordinal) &&
            index + 1 < tokens.Count)
        {
            var moduleName = tokens[index + 1];
            var rest = FilterDisplayArgs(tokens.Skip(index + 2));
            return string.IsNullOrWhiteSpace(rest) ? moduleName : $"{moduleName} {rest}";
        }

        if (string.Equals(current, "-e", StringComparison.Ordinal) || string.Equals(current, "-c", StringComparison.Ordinal))
        {
            return $"{runtimeName} {current} ...";
        }

        while (index < tokens.Count && tokens[index].StartsWith("-", StringComparison.Ordinal))
        {
            index++;
        }

        if (index >= tokens.Count)
        {
            var startIndex = IsRuntimeToken(tokens[0]) ? 1 : 0;
            var flags = FilterDisplayArgs(tokens.Skip(startIndex));
            return string.IsNullOrWhiteSpace(flags) ? runtimeName : $"{runtimeName} {flags}";
        }

        var scriptPath = tokens[index];
        var displayName = ExtractScriptDisplayName(scriptPath);
        var args = FilterDisplayArgs(tokens.Skip(index + 1));
        return string.IsNullOrWhiteSpace(args) ? displayName : $"{displayName} {args}";
    }

    public string GetProcessIdentity(string? processName, string? commandLine)
    {
        var display = FormatRuntimeDisplay(processName, commandLine).Trim();
        if (string.IsNullOrWhiteSpace(display))
        {
            return NormalizeIdentity(processName);
        }

        var tokens = Tokenize(display);
        var firstToken = tokens.Count > 0
            ? tokens[0]
            : display.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return NormalizeIdentity(Basename(firstToken));
    }

    public bool HasIdentity(
        string? processName,
        string? commandLine,
        SessionAgentAttachPoint? attachPoint,
        string expectedIdentity)
    {
        if (string.IsNullOrWhiteSpace(expectedIdentity))
        {
            return false;
        }

        var normalizedExpectedIdentity = NormalizeIdentity(expectedIdentity);
        if (string.IsNullOrWhiteSpace(normalizedExpectedIdentity))
        {
            return false;
        }

        var descriptor = Describe(processName, commandLine, attachPoint);
        return string.Equals(descriptor.ProcessIdentity, normalizedExpectedIdentity, StringComparison.Ordinal);
    }

    private string? TryFormatShellWrapperDisplay(string processName, string commandLine)
    {
        if (!IsShellWrapper(processName))
        {
            return null;
        }

        var tokens = Tokenize(commandLine);
        if (tokens.Count == 0)
        {
            return null;
        }

        var index = IsSameExecutableToken(tokens[0], processName) ? 1 : 0;
        while (index < tokens.Count && IsShellWrapperControlToken(processName, tokens[index]))
        {
            index++;
        }

        if (index >= tokens.Count)
        {
            return null;
        }

        var nestedCommand = ReconstructNestedCommand(tokens, index);
        if (string.IsNullOrWhiteSpace(nestedCommand))
        {
            return null;
        }

        var nestedProcessName = tokens[index];
        if (string.Equals(
            NormalizeIdentity(Basename(nestedProcessName)),
            NormalizeIdentity(Basename(processName)),
            StringComparison.Ordinal))
        {
            return null;
        }

        return FormatRuntimeDisplay(nestedProcessName, nestedCommand);
    }

    private static bool IsShellWrapper(string processName)
    {
        return ShellWrapperNames.Contains(NormalizeIdentity(Basename(processName)));
    }

    private static bool IsSameExecutableToken(string token, string processName)
    {
        return string.Equals(
            NormalizeIdentity(Basename(token)),
            NormalizeIdentity(Basename(processName)),
            StringComparison.Ordinal);
    }

    private static bool IsShellWrapperControlToken(string processName, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return true;
        }

        if (token is "&" or "&&" or "||" or "|" or ";" or "--" or "--%")
        {
            return true;
        }

        if (token.StartsWith("-", StringComparison.Ordinal))
        {
            return true;
        }

        var normalizedWrapper = NormalizeIdentity(Basename(processName));
        return normalizedWrapper == "cmd" && token.StartsWith("/", StringComparison.Ordinal);
    }

    private static string? ReconstructNestedCommand(IReadOnlyList<string> tokens, int startIndex)
    {
        if (startIndex >= tokens.Count)
        {
            return null;
        }

        return string.Join(" ", tokens.Skip(startIndex));
    }

    private static string NormalizeIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var candidate = value.Trim();
        var firstChar = candidate[0];
        if ((firstChar == '"' || firstChar == '\'') && candidate.Length > 1)
        {
            var closingQuote = candidate.IndexOf(firstChar, 1);
            if (closingQuote > 1)
            {
                candidate = candidate[1..closingQuote];
            }
        }

        candidate = candidate.Replace('\\', '/');
        var basename = candidate.Split('/').LastOrDefault() ?? candidate;
        var token = basename.Trim().Split(' ', '\t').FirstOrDefault() ?? basename.Trim();
        return TrimLauncherSuffix(token).ToLowerInvariant();
    }

    private static string StripExecutablePath(string commandLine)
    {
        var trimmed = commandLine.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return trimmed;
        }

        if (trimmed.StartsWith('"'))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            if (endQuote > 1)
            {
                var quotedPath = trimmed[1..endQuote];
                var rest = trimmed[(endQuote + 1)..];
                var quotedExeName = NormalizeIdentity(Basename(quotedPath));
                return (quotedExeName + rest).Trim();
            }
        }

        var spaceIndex = trimmed.IndexOf(' ', StringComparison.Ordinal);
        if (spaceIndex < 0)
        {
            return NormalizeIdentity(Basename(trimmed));
        }

        var exePart = trimmed[..spaceIndex];
        var argsPart = trimmed[spaceIndex..];
        var executableName = NormalizeIdentity(Basename(exePart));
        return (executableName + argsPart).Trim();
    }

    private static string? ExtractRuntimeName(string processName)
    {
        var name = NormalizeIdentity(Basename(processName));
        return RuntimeNames.Contains(name) ? name : null;
    }

    private static bool IsRuntimeToken(string token)
    {
        return RuntimeNames.Contains(NormalizeIdentity(Basename(token)));
    }

    private static string ExtractScriptDisplayName(string scriptPath)
    {
        var normalized = scriptPath.Replace('\\', '/');
        var parts = normalized.Split('/');
        var filename = parts.Length == 0 ? scriptPath : parts[^1];

        var nodeModulesIndex = normalized.LastIndexOf("node_modules/", StringComparison.OrdinalIgnoreCase);
        if (nodeModulesIndex >= 0)
        {
            var afterNodeModules = normalized[(nodeModulesIndex + "node_modules/".Length)..];
            var segments = afterNodeModules.Split('/');
            var first = segments.Length > 0 ? segments[0] : string.Empty;
            if (first.StartsWith("@", StringComparison.Ordinal) && segments.Length >= 2)
            {
                return FilenameWithoutExtension(segments[1]);
            }

            return string.IsNullOrWhiteSpace(first) ? FilenameWithoutExtension(filename) : FilenameWithoutExtension(first);
        }

        if (GenericScriptNames.Contains(filename))
        {
            for (var index = parts.Length - 2; index >= 0; index--)
            {
                var dir = parts[index];
                if (!SkipParentDirs.Contains(dir))
                {
                    return dir;
                }
            }
        }

        return FilenameWithoutExtension(filename);
    }

    private static string FilenameWithoutExtension(string filename)
    {
        var dotIndex = filename.LastIndexOf('.');
        return dotIndex > 0 ? filename[..dotIndex] : filename;
    }

    private static string TrimLauncherSuffix(string token)
    {
        foreach (var suffix in new[] { ".exe", ".cmd", ".bat", ".ps1", ".psm1", ".sh" })
        {
            if (token.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return token[..^suffix.Length];
            }
        }

        return token;
    }

    private static string FilterDisplayArgs(IEnumerable<string> tokens)
    {
        return string.Join(
            " ",
            tokens.Where(static token =>
            {
                if (token.StartsWith("-", StringComparison.Ordinal))
                {
                    return true;
                }

                if (token.StartsWith("/", StringComparison.Ordinal) ||
                    (token.Length >= 3 && char.IsAsciiLetter(token[0]) && token[1] == ':' && (token[2] == '/' || token[2] == '\\')))
                {
                    return false;
                }

                return true;
            }));
    }

    private static string Basename(string path)
    {
        return path.Replace('\\', '/').Split('/').LastOrDefault() ?? path;
    }

    internal static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var ch = commandLine[i];
            if (quote is not null)
            {
                if (ch == quote.Value)
                {
                    quote = null;
                    continue;
                }

                if (ch == '\\' &&
                    i + 1 < commandLine.Length &&
                    commandLine[i + 1] == quote.Value)
                {
                    current.Append(commandLine[i + 1]);
                    i++;
                    continue;
                }

                current.Append(ch);
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                FlushCurrent(tokens, current);
                continue;
            }

            current.Append(ch);
        }

        FlushCurrent(tokens, current);
        return tokens;
    }

    private static void FlushCurrent(List<string> tokens, System.Text.StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        tokens.Add(current.ToString());
        current.Clear();
    }
}

public sealed record SessionForegroundDescriptor(
    string DisplayName,
    string ProcessIdentity);
