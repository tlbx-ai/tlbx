using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class CodexAppServerControlAgentRuntime
{
    private async Task<JsonElement?> TryReadUsageDataAsync(string method, CancellationToken ct)
    {
        try
        {
            return await SendCodexRequestAsync(method, id => BuildJsonString(writer =>
            {
                writer.WriteStartObject(); writer.WriteString("id", id); writer.WriteString("method", method);
                writer.WritePropertyName("params"); writer.WriteStartObject();
                if (method == "account/read") writer.WriteBoolean("refreshToken", false);
                writer.WriteEndObject(); writer.WriteEndObject();
            }), ct, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    private async Task<HostCommandOutcome> ReadUsageAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        var threadId = _providerThreadId;
        var accountRead = TryReadUsageDataAsync("account/read", ct);
        var limitsRead = TryReadUsageDataAsync("account/rateLimits/read", ct);
        await Task.WhenAll(accountRead, limitsRead).ConfigureAwait(false);
        if (!string.Equals(threadId, _providerThreadId, StringComparison.Ordinal))
            throw new InvalidOperationException("Session changed while reading usage.");
        var account = await accountRead.ConfigureAwait(false);
        var limits = await limitsRead.ConfigureAwait(false);
        AppServerControlUsageSummary usage;
        lock (_usageSync)
        {
            usage = _usage?.Clone() ?? new();
            usage.AccountLabel = account is { } a ? GetString(a, "account", "email") ?? GetString(a, "account", "type") : null;
            usage.AccountPlan = account is { } plan ? GetString(plan, "account", "planType") : null;
            usage.LimitsStatus = limits is null ? "unavailable" : "reported";
            usage.LimitsCheckedAt = DateTimeOffset.UtcNow;
            if (limits is { } result) ApplyCodexLimits(usage, GetObject(result, "rateLimits") ?? result);
            else usage.Limits = [];
            _usage = usage;
        }
        _emit(CreateEvent("agent.metadata.updated", null, null, null, "codex.app-server", "usage.read", default,
            e => e.AgentMetadataUpdated = new() { Usage = usage.Clone() }));
        return new() { Result = new() { CommandId = command.CommandId, SessionId = command.SessionId, Usage = usage.Clone() } };
    }

    private static void ApplyCodexLimits(AppServerControlUsageSummary usage, JsonElement native)
    {
        foreach (var name in new[] { "primary", "secondary" })
        {
            var limit = ProviderUsageLimits.Read(native, name, name, "usedPercent");
            if (limit is null) continue;
            usage.Limits.RemoveAll(existing => existing.Id == name);
            usage.Limits.Add(limit);
        }
    }
}
