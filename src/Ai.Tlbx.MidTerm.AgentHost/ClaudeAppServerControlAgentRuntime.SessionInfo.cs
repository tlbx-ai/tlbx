using System.Collections.Concurrent;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class ClaudeAppServerControlAgentRuntime
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _usageReceipts = new(StringComparer.Ordinal);

    private async Task<HostCommandOutcome> ReadUsageAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        var threadId = _providerThreadId;
        var receipt = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_usageReceipts.TryAdd(command.CommandId, receipt)) throw new InvalidOperationException("Usage read is already pending.");
        try
        {
            await WriteBridgeCommandAsync(writer =>
            {
                writer.WriteStartObject(); writer.WriteString("type", "usage.read");
                writer.WriteString("requestId", command.CommandId); writer.WriteEndObject();
            }, ct).ConfigureAwait(false);
            var response = await receipt.Task.WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            if (!string.Equals(threadId, _providerThreadId, StringComparison.Ordinal))
                throw new InvalidOperationException("Session changed while reading usage.");
            AppServerControlUsageSummary usage;
            lock (_usageSync)
            {
                usage = _usage?.Clone() ?? new();
                var account = response.TryGetProperty("account", out var a) ? a : default;
                usage.AccountLabel = GetString(account, "email") ?? GetString(account, "apiProvider");
                usage.AccountPlan = GetString(account, "subscriptionType");
                usage.LimitsCheckedAt = DateTimeOffset.UtcNow;
                usage.LimitsStatus = GetBoolean(response, "available") ? "reported" : "unavailable";
                usage.Limits = [];
                if (response.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Object)
                {
                    foreach (var (name, label, minutes) in new[] {
                        ("five_hour", "5h", 300), ("seven_day", "7d", 10080),
                        ("seven_day_opus", "Opus 7d", 10080), ("seven_day_sonnet", "Sonnet 7d", 10080),
                        ("seven_day_oauth_apps", "OAuth 7d", 10080) })
                    {
                        var limit = ProviderUsageLimits.Read(limits, name, label, "utilization", minutes);
                        if (limit is not null) usage.Limits.Add(limit);
                    }
                }
                _usage = usage;
            }
            _emit(CreateEvent("agent.metadata.updated", null, null, null, "claude.agent-sdk", "usage.read", response,
                e => e.AgentMetadataUpdated = new() { Usage = usage.Clone() }));
            return new() { Result = new() { CommandId = command.CommandId, SessionId = command.SessionId, Usage = usage.Clone() } };
        }
        finally { _usageReceipts.TryRemove(command.CommandId, out _); }
    }
}
