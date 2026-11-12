using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class CodexAppServerControlAgentRuntime
{
    private AppServerControlUsageSummary? _usage;
    private readonly object _usageSync = new();

    private void UpdateCodexTokenUsage(JsonElement payload)
    {
        lock (_usageSync)
        {
        var native = GetObject(payload, "tokenUsage") ?? payload;
        var usage = _usage?.Clone() ?? new AppServerControlUsageSummary();
        // Last describes the current context. Total accumulates repeated input
        // across requests and must not be displayed as context occupancy.
        usage.ContextUsedTokens = ReadCodexUsageCount(native, "last", "totalTokens", "total_tokens");
        usage.ContextWindowTokens = ReadCodexUsageCount(native, null, "modelContextWindow", "model_context_window");
        usage.InputTokens = ReadCodexUsageCount(native, "total", "inputTokens", "input_tokens");
        usage.OutputTokens = ReadCodexUsageCount(native, "total", "outputTokens", "output_tokens");
        usage.CachedReadTokens = ReadCodexUsageCount(native, "total", "cachedInputTokens", "cached_input_tokens");
        usage.CachedWriteTokens = ReadCodexUsageCount(native, "total", "cacheWriteInputTokens", "cache_write_input_tokens");
        usage.ReasoningTokens = ReadCodexUsageCount(native, "total", "reasoningOutputTokens", "reasoning_output_tokens");
        _usage = usage;
        }
    }

    private void UpdateCodexRateLimits(JsonElement payload)
    {
        lock (_usageSync)
        {
        var native = GetObject(payload, "rateLimits") ?? payload;
        var usage = _usage?.Clone() ?? new AppServerControlUsageSummary();
        ApplyCodexLimits(usage, native);
        usage.LimitsStatus = "reported";
        usage.LimitsCheckedAt = DateTimeOffset.UtcNow;
        usage.PrimaryRateLimitUsedPercent = ReadCodexUsedPercent(native, "primary") ?? usage.PrimaryRateLimitUsedPercent;
        usage.SecondaryRateLimitUsedPercent = ReadCodexUsedPercent(native, "secondary") ?? usage.SecondaryRateLimitUsedPercent;
        _usage = usage;
        }
    }

    private static long? ReadCodexUsageCount(JsonElement native, string? section, string name, string alternate)
    {
        var value = section is null
            ? GetLong(native, name) ?? GetLong(native, alternate)
            : GetLong(native, section, name) ?? GetLong(native, section, alternate);
        return value is >= 0 ? value : null;
    }

    private static double? ReadCodexUsedPercent(JsonElement native, string window)
    {
        var value = GetObject(native, window);
        if (value is { } limit &&
            (limit.TryGetProperty("usedPercent", out var percent) || limit.TryGetProperty("used_percent", out percent)) &&
            percent.ValueKind == JsonValueKind.Number && percent.TryGetDouble(out var used) &&
            double.IsFinite(used) && used >= 0)
        {
            return used;
        }

        return null;
    }
}
