using System.Globalization;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal static class ProviderUsageLimits
{
    internal static AppServerControlUsageLimit? Read(JsonElement root, string name, string label,
        string percentName, int? minutes = null)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) return null;
        double? percent = value.TryGetProperty(percentName, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var number) &&
            double.IsFinite(number) && number >= 0 ? number : null;
        DateTimeOffset? reset = null;
        if (value.TryGetProperty("resetsAt", out var stamp) && stamp.ValueKind == JsonValueKind.Number &&
            stamp.TryGetInt64(out var seconds) && seconds is >= 0 and <= 253402300799)
            reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
        else if (value.TryGetProperty("resets_at", out stamp) && stamp.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(stamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) reset = parsed;
        if (value.TryGetProperty("windowDurationMins", out var window) && window.ValueKind == JsonValueKind.Number && window.TryGetInt32(out var duration) && duration > 0)
            minutes = duration;
        return new() { Id = name, Label = label, UsedPercent = percent, WindowMinutes = minutes, ResetsAt = reset };
    }
}
