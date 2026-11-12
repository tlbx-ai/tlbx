using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class ClaudeAppServerControlAgentRuntime
{
    private AppServerControlUsageSummary? _usage;
    private readonly object _usageSync = new();
    private long? _messageInputTokens;
    private long? _messageOutputTokens;

    private void UpdateClaudeCompactionUsage(JsonElement root, string rawLine)
    {
        lock (_usageSync)
        {
        if (Traverse(root, "compact_metadata") is not { ValueKind: JsonValueKind.Object } metadata ||
            ReadClaudeUsageCount(metadata, "post_tokens") is not { } used) return;
        _usage = _usage?.Clone() ?? new AppServerControlUsageSummary();
        _usage.ContextUsedTokens = used;
        _messageInputTokens = used;
        _messageOutputTokens = 0;
        EmitClaudeUsage(root, rawLine);
        }
    }

    private void UpdateClaudeMessageUsage(JsonElement root, JsonElement? native, string rawLine)
    {
        lock (_usageSync)
        {
        if (GetString(root, "error") is not null) return;
        if (native is not { ValueKind: JsonValueKind.Object } value ||
            !string.IsNullOrEmpty(GetString(root, "parent_tool_use_id"))) return;
        if (ReadClaudeUsageCount(value, "input_tokens") is { } input)
        {
            var sum = (decimal)input + (ReadClaudeUsageCount(value, "cache_read_input_tokens") ?? 0) +
                (ReadClaudeUsageCount(value, "cache_creation_input_tokens") ?? 0);
            _messageInputTokens = sum <= long.MaxValue ? (long)sum : null;
            _messageOutputTokens = 0;
        }
        if (ReadClaudeUsageCount(value, "output_tokens") is { } output) _messageOutputTokens = output;
        var usage = _usage?.Clone() ?? new AppServerControlUsageSummary();
        if (_messageInputTokens is { } usedInput && _messageOutputTokens is { } usedOutput)
        {
            var sum = (decimal)usedInput + usedOutput;
            usage.ContextUsedTokens = sum <= long.MaxValue ? (long)sum : null;
        }
        _usage = usage;
        EmitClaudeUsage(root, rawLine);
        }
    }

    private void UpdateClaudeResultUsage(JsonElement root, string rawLine)
    {
        lock (_usageSync)
        {
        var usage = _usage?.Clone() ?? new AppServerControlUsageSummary();
        if (root.TryGetProperty("modelUsage", out var models) && models.ValueKind == JsonValueKind.Object)
        {
            var entries = new List<KeyValuePair<string, JsonElement>>();
            using var enumerator = models.EnumerateObject();
            foreach (var model in enumerator)
                if (model.Value.ValueKind == JsonValueKind.Object) entries.Add(new(model.Name, model.Value));
            // SDK modelUsage is cumulative across streaming turns. Never add
            // successive results or substitute per-turn main-loop usage for it.
            if (entries.Count == 0 && GetBoolean(root, "is_error")) return;
            usage.OutputTokens = SumClaudeModelUsage(entries, "outputTokens");
            usage.CachedReadTokens = SumClaudeModelUsage(entries, "cacheReadInputTokens");
            usage.CachedWriteTokens = SumClaudeModelUsage(entries, "cacheCreationInputTokens");
            if (SumClaudeModelUsage(entries, "inputTokens") is { } uncachedInput)
            {
                var sum = (decimal)uncachedInput + (usage.CachedReadTokens ?? 0) + (usage.CachedWriteTokens ?? 0);
                usage.InputTokens = sum <= long.MaxValue ? (long)sum : null;
            }
            else usage.InputTokens = null;
            usage.ReasoningTokens = SumClaudeModelUsage(entries, "thinkingTokens");
            var current = entries.FirstOrDefault(entry => string.Equals(entry.Key, _activeTurnModel, StringComparison.Ordinal) ||
                string.Equals(GetString(entry.Value, "canonicalModel"), _activeTurnModel, StringComparison.Ordinal));
            if (current.Value.ValueKind != JsonValueKind.Object && entries.Count == 1) current = entries[0];
            if (current.Value.ValueKind == JsonValueKind.Object)
                usage.ContextWindowTokens = ReadClaudeUsageCount(current.Value, "contextWindow");
        }
        if (root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number &&
            cost.TryGetDecimal(out var amount) && amount >= 0)
        {
            usage.CostAmount = amount;
            usage.CostCurrency = "USD";
        }
        _usage = usage;
        EmitClaudeUsage(root, rawLine);
        }
    }

    private static long? ReadClaudeUsageCount(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var count) && count >= 0 ? count : null;

    private static long? SumClaudeModelUsage(IEnumerable<KeyValuePair<string, JsonElement>> models, string field)
    {
        decimal total = 0;
        var reported = false;
        foreach (var model in models)
            if (ReadClaudeUsageCount(model.Value, field) is { } count)
            {
                reported = true;
                total += count;
            }
        return reported && total <= long.MaxValue ? (long)total : null;
    }

    private void EmitClaudeUsage(JsonElement root, string rawLine) =>
        _emit(CreateEvent("thread.token-usage.updated", _activeTurnId, null, null, "claude.agent-sdk", "usage", root,
            e => e.AgentMetadataUpdated = new AppServerControlProviderAgentMetadataPayload { Usage = _usage?.Clone() }, rawLine));
}
