using Ai.Tlbx.MidTerm.Models.Sessions;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed class AiCliProfileService
{
    public const string ShellProfile = "shell";
    public const string UnknownProfile = "unknown";
    public const string CodexProfile = "codex";
    public const string ClaudeProfile = "claude";
    public const string GrokProfile = "grok";
    public const string OpenCodeProfile = "open-code";
    public const string GenericAiProfile = "generic-ai";

    public string DetectProfile(SessionInfoDto session)
    {
        if (session is null)
        {
            return UnknownProfile;
        }

        var hintedProfile = NormalizeProfile(session.ProfileHint);
        if (hintedProfile != UnknownProfile)
        {
            return hintedProfile;
        }

        var shellIdentity = NormalizeExecutableIdentity(session.ShellType);
        var foregroundIdentity = NormalizeExecutableIdentity(session.ForegroundName);
        if (!string.IsNullOrEmpty(shellIdentity) && shellIdentity == foregroundIdentity)
        {
            return ShellProfile;
        }

        var haystack = string.Join('\n', new[]
        {
            session.ForegroundName,
            session.ForegroundCommandLine,
            session.TerminalTitle,
            session.Name
        }.Where(static value => !string.IsNullOrWhiteSpace(value)))
            .ToLowerInvariant();

        if (haystack.Contains("codex", StringComparison.Ordinal))
        {
            return CodexProfile;
        }

        if (haystack.Contains("claude", StringComparison.Ordinal))
        {
            return ClaudeProfile;
        }

        if (haystack.Contains("grok", StringComparison.Ordinal))
        {
            return GrokProfile;
        }

        if (haystack.Contains("opencode", StringComparison.Ordinal) ||
            haystack.Contains("open code", StringComparison.Ordinal))
        {
            return OpenCodeProfile;
        }

        if (haystack.Contains("assistant", StringComparison.Ordinal) ||
            haystack.Contains("agent", StringComparison.Ordinal))
        {
            return GenericAiProfile;
        }

        if (string.IsNullOrWhiteSpace(session.ForegroundName) &&
            string.IsNullOrWhiteSpace(session.ForegroundCommandLine))
        {
            return ShellProfile;
        }

        return UnknownProfile;
    }

    public string NormalizeProfile(string? requestedProfile, SessionInfoDto? session = null)
    {
        var normalized = (requestedProfile ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "" when session is not null => DetectProfile(session),
            "" => UnknownProfile,
            "codex" => CodexProfile,
            "claude" => ClaudeProfile,
            "grok" or "grok-build" => GrokProfile,
            "open-code" or "opencode" => OpenCodeProfile,
            "generic-ai" or "generic" or "ai" => GenericAiProfile,
            "shell" => ShellProfile,
            _ => normalized
        };
    }

    public bool IsInteractiveAi(string? profile)
    {
        var normalized = NormalizeProfile(profile);
        return normalized is CodexProfile or ClaudeProfile or GrokProfile or OpenCodeProfile or GenericAiProfile;
    }

    public string? GetDefaultLaunchCommand(string? profile)
    {
        return NormalizeProfile(profile) switch
        {
            CodexProfile => "codex --yolo",
            ClaudeProfile => "claude --dangerously-skip-permissions",
            GrokProfile => "grok",
            _ => null
        };
    }

    public List<string> NormalizeSlashCommands(string? profile, IEnumerable<string>? slashCommands)
    {
        _ = NormalizeProfile(profile);

        return (slashCommands ?? [])
            .Select(static command => command?.Trim())
            .Where(static command => !string.IsNullOrWhiteSpace(command))
            .Select(static command => command!.StartsWith("/", StringComparison.Ordinal) ? command : "/" + command)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string NormalizeExecutableIdentity(string? value)
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
        return token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? token[..^4].ToLowerInvariant()
            : token.ToLowerInvariant();
    }
}
