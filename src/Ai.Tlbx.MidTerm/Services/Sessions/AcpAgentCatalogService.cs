using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed partial class AcpAgentCatalogService(SettingsService settingsService)
{
    public const string ManifestFileName = "acp-agents.json";

    private static readonly IReadOnlyList<AcpAgentDefinition> BuiltInDefinitions =
    [
        new("grok", "Grok Build", "grok", ["agent", "stdio"], RegistryId: "grok-build"),
        new("opencode", "OpenCode", "opencode", [], RuntimeKind: "opencode-http", RegistryId: "opencode"),
        new("antigravity", "Google Antigravity", "agy", ["acp"], RegistryId: "antigravity-acp"),
        new("gemini", "Gemini CLI", "gemini", ["--acp"], RegistryId: "gemini"),
        new("copilot", "GitHub Copilot CLI", "copilot", ["--acp"], RegistryId: "github-copilot-cli")
    ];

    public string ManifestPath => Path.Combine(settingsService.SettingsDirectory, ManifestFileName);

    public IReadOnlyList<AcpAgentDefinition> GetDefinitions()
    {
        var definitions = new Dictionary<string, AcpAgentDefinition>(StringComparer.Ordinal);
        foreach (var definition in BuiltInDefinitions)
        {
            definitions[definition.Profile] = definition;
        }

        foreach (var definition in LoadCustomDefinitions())
        {
            definitions[definition.Profile] = definition;
        }

        return definitions.Values.OrderBy(static definition => definition.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool ContainsProfile(string? profile)
    {
        return TryGetDefinition(profile, out _);
    }

    public bool TryGetDefinition(string? profile, out AcpAgentDefinition definition)
    {
        definition = null!;
        var normalized = NormalizeProfile(profile);
        if (normalized is null)
        {
            return false;
        }

        var candidate = GetDefinitions().FirstOrDefault(definition =>
            string.Equals(definition.Profile, normalized, StringComparison.Ordinal));
        if (candidate is null)
        {
            return false;
        }

        definition = candidate;
        return true;
    }

    public bool TryResolve(
        string? profile,
        string? userProfileDirectory,
        out ResolvedAcpAgentDefinition definition)
    {
        definition = null!;
        if (!TryGetDefinition(profile, out var catalogDefinition))
        {
            return false;
        }

        var executablePath = AiCliCommandLocator.FindExecutableInPath(
            catalogDefinition.CommandName,
            userProfileDirectory);
        if (executablePath is null)
        {
            return false;
        }

        definition = new ResolvedAcpAgentDefinition(
            catalogDefinition.Profile,
            catalogDefinition.Name,
            executablePath,
            catalogDefinition.Arguments,
            catalogDefinition.EnvironmentVariables,
            catalogDefinition.AuthenticationMethodId,
            catalogDefinition.RuntimeKind);
        return true;
    }

    private IReadOnlyList<AcpAgentDefinition> LoadCustomDefinitions()
    {
        if (!File.Exists(ManifestPath))
        {
            return [];
        }

        try
        {
            var manifest = JsonSerializer.Deserialize(
                File.ReadAllText(ManifestPath),
                AcpAgentCatalogJsonContext.Default.AcpAgentManifest);
            if (manifest?.Agents is null)
            {
                return [];
            }

            var definitions = new List<AcpAgentDefinition>();
            foreach (var entry in manifest.Agents)
            {
                if (entry.Enabled is false)
                {
                    continue;
                }

                var profile = NormalizeProfile(entry.Profile);
                var name = entry.Name?.Trim();
                var command = entry.Command?.Trim().Trim('"');
                var arguments = entry.Arguments?
                    .Where(static argument => argument is not null)
                    .Select(static argument => argument!.Trim())
                    .ToArray() ?? [];
                if (profile is null ||
                    string.IsNullOrWhiteSpace(name) || name.Length > 100 ||
                    string.IsNullOrWhiteSpace(command) || command.Length > 1024 ||
                    arguments.Length > 64 ||
                    arguments.Any(static argument => argument.Length > 4096))
                {
                    Log.Warn(() => $"Ignoring invalid ACP agent entry in '{ManifestPath}'.");
                    continue;
                }

                if ((entry.RuntimeKind is not (null or "acp-v1") && !(profile == "opencode" && entry.RuntimeKind == "opencode-http")) ||
                    entry.EnvironmentVariables is null || entry.AuthenticationMethodId?.Length > 256 ||
                    entry.EnvironmentVariables.Count > 64 || entry.EnvironmentVariables.Any(pair =>
                        !EnvironmentKeyPattern().IsMatch(pair.Key) || pair.Value?.Length > 32768))
                {
                    Log.Warn(() => $"Ignoring invalid ACP agent environment in '{ManifestPath}'.");
                    continue;
                }
                definitions.Add(new AcpAgentDefinition(profile, name, command, arguments,
                    new Dictionary<string, string?>(entry.EnvironmentVariables, StringComparer.Ordinal),
                    string.IsNullOrWhiteSpace(entry.AuthenticationMethodId) ? null : entry.AuthenticationMethodId.Trim(),
                    profile == "opencode" && entry.RuntimeKind == "opencode-http" ? "opencode-http" : "acp-v1"));
            }

            return definitions;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn(() => $"Could not load ACP agent manifest '{ManifestPath}': {ex.Message}");
            return [];
        }
    }

    private static string? NormalizeProfile(string? profile)
    {
        var normalized = profile?.Trim().ToLowerInvariant();
        return !string.IsNullOrWhiteSpace(normalized) && ProfilePattern().IsMatch(normalized)
            ? normalized
            : null;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ProfilePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex EnvironmentKeyPattern();
}

public sealed record AcpAgentDefinition(
    string Profile,
    string Name,
    string CommandName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?>? EnvironmentVariables = null,
    string? AuthenticationMethodId = null,
    string RuntimeKind = "acp-v1",
    string? RegistryId = null);

public sealed record ResolvedAcpAgentDefinition(
    string Profile,
    string Name,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?>? EnvironmentVariables = null,
    string? AuthenticationMethodId = null,
    string RuntimeKind = "acp-v1");

internal sealed class AcpAgentManifest
{
    public List<AcpAgentManifestEntry> Agents { get; set; } = [];
}

internal sealed class AcpAgentManifestEntry
{
    public string? Profile { get; set; }
    public string? Name { get; set; }
    public string? Command { get; set; }
    public List<string?> Arguments { get; set; } = [];
    public Dictionary<string, string?> EnvironmentVariables { get; set; } = new(StringComparer.Ordinal);
    public string? AuthenticationMethodId { get; set; }
    public string? RuntimeKind { get; set; }
    public bool? Enabled { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AcpAgentManifest))]
internal sealed partial class AcpAgentCatalogJsonContext : JsonSerializerContext;
