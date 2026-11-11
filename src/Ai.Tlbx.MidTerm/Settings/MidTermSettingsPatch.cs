using System.Runtime.InteropServices;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Services;

namespace Ai.Tlbx.MidTerm.Settings;

public static class MidTermSettingsPatch
{
    public static MidTermSettingsPublic Replace(
        MidTermSettingsPublic current,
        JsonElement replacement)
    {
        ArgumentNullException.ThrowIfNull(current);
        var contract = GetContract(current);
        var suppliedProperties = ValidatePropertyNames(replacement, contract, "PUT /api/settings");
        var missingProperties = contract
            .Select(entry => entry.Key)
            .Where(name => !suppliedProperties.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToList();

        if (missingProperties.Count > 0)
        {
            var shownProperties = string.Join(", ", missingProperties.Take(8));
            var remainder = missingProperties.Count > 8
                ? $" (and {(missingProperties.Count - 8).ToString(CultureInfo.InvariantCulture)} more)"
                : string.Empty;
            throw new ArgumentException(
                $"PUT /api/settings requires a complete settings document. Missing: {shownProperties}{remainder}. " +
                "Use PATCH /api/settings for partial updates.",
                nameof(replacement));
        }

        return Merge(current, replacement);
    }

    public static MidTermSettingsPublic Merge(
        MidTermSettingsPublic current,
        JsonElement patch)
    {
        ArgumentNullException.ThrowIfNull(current);
        var merged = GetContract(current);
        ValidatePropertyNames(patch, merged, "PATCH /api/settings");

        using var properties = patch.EnumerateObject();
        while (properties.MoveNext())
        {
            var property = properties.Current;
            var existingName = merged
                .Select(entry => entry.Key)
                .FirstOrDefault(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase));
            var propertyName = existingName ?? property.Name;
            merged[propertyName] = JsonNode.Parse(JsonMarshal.GetRawUtf8Value(property.Value));
        }

        return merged.Deserialize(AppJsonContext.Default.MidTermSettingsPublic)
            ?? throw new ArgumentException("Settings payload could not be deserialized.", nameof(patch));
    }

    private static JsonObject GetContract(MidTermSettingsPublic current)
    {
        return JsonSerializer.SerializeToNode(
            current,
            AppJsonContext.Default.MidTermSettingsPublic) as JsonObject
            ?? throw new InvalidOperationException("Failed to serialize current public settings.");
    }

    private static HashSet<string> ValidatePropertyNames(
        JsonElement payload,
        JsonObject contract,
        string operation)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Settings payload must be a JSON object.", nameof(payload));
        }

        var contractNames = contract.Select(entry => entry.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suppliedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknownNames = new List<string>();

        using var properties = payload.EnumerateObject();
        while (properties.MoveNext())
        {
            var property = properties.Current;
            if (!suppliedNames.Add(property.Name))
            {
                throw new ArgumentException(
                    $"{operation} contains the duplicate property '{property.Name}'.",
                    nameof(payload));
            }

            if (!contractNames.Contains(property.Name))
            {
                unknownNames.Add(property.Name);
            }
        }

        if (unknownNames.Count > 0)
        {
            throw new ArgumentException(
                $"{operation} contains unknown settings: {string.Join(", ", unknownNames.Order(StringComparer.Ordinal))}.",
                nameof(payload));
        }

        return suppliedNames;
    }
}
