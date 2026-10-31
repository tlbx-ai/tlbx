using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class CodexAppServerControlAgentRuntime
{
    private async Task<JsonElement> EnsureMcpBindingAsync(AppServerControlAttachRuntimeRequest attach,
        JsonElement threadResult, string threadId, string permissionMode, CancellationToken ct, bool allowIdleReload = false)
    {
        if (attach.McpServer is null) return threadResult;
        if (!_mcpBindingMustReload && await HasExpectedMcpBindingAsync(threadId, ct).ConfigureAwait(false)) return threadResult;

        if (attach.AttachPoint is null && !allowIdleReload)
            throw new InvalidOperationException("TLBX_MCP_PROVIDER_BINDING: Codex MCP discovery did not prove this runtime's tlbx session; no model turn was started.");

        // A loaded thread ignores new resume config. Native unsubscribe can unload
        // an idle last-subscriber thread; its status alone does not prove unload.
        // Never interrupt a turn or stop the shared backend to force this handoff.
        var current = await SendCodexRequestAsync("thread/read",
            id => BuildMcpNativeRequest(id, "thread/read", threadId), ct).ConfigureAwait(false);
        if (!string.Equals(GetString(current, "thread", "status", "type"), "idle", StringComparison.Ordinal))
            throw new InvalidOperationException("TLBX_MCP_PROVIDER_BUSY: MCP registration needs the canonical Codex thread to become idle; its conversation was preserved.");

        var unsubscribed = await SendCodexRequestAsync("thread/unsubscribe",
            id => BuildMcpNativeRequest(id, "thread/unsubscribe", threadId), ct).ConfigureAwait(false);
        var status = GetString(unsubscribed, "status");
        if (status is not ("unsubscribed" or "notLoaded"))
            throw new InvalidOperationException("TLBX_MCP_PROVIDER_SUBSCRIPTION: Codex did not release this connection's thread subscription; its conversation was preserved.");

        var resumed = await SendCodexRequestAsync("thread/resume",
            id => BuildCodexThreadResumeRequest(id, threadId, attach.WorkingDirectory, permissionMode, attach.McpServer),
            ct).ConfigureAwait(false);
        if (!string.Equals(GetString(resumed, "thread", "id"), threadId, StringComparison.Ordinal) ||
            !await HasExpectedMcpBindingAsync(threadId, ct).ConfigureAwait(false))
            throw new InvalidOperationException("TLBX_MCP_PROVIDER_RELOAD_PENDING: The idle Codex thread retained its existing MCP connection, possibly because another client remains subscribed. Its canonical conversation and shared backend were preserved; reconnect after the owning client releases the idle thread.");
        _mcpBindingMustReload = false;
        return resumed;
    }

    private async Task<bool> HasExpectedMcpBindingAsync(string threadId, CancellationToken ct)
    {
        try
        {
            // Native status waits for startup and is thread-specific. The actual
            // read-only tool call then proves the binding, beyond name discovery.
            await SendCodexRequestAsync("mcpServerStatus/list",
                id => BuildMcpNativeRequest(id, "mcpServerStatus/list", threadId), ct, TimeSpan.FromSeconds(45)).ConfigureAwait(false);
            var result = await SendCodexRequestAsync("mcpServer/tool/call",
                id => BuildMcpNativeRequest(id, "mcpServer/tool/call", threadId), ct, TimeSpan.FromSeconds(45)).ConfigureAwait(false);
            return !(result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True) &&
                string.Equals(GetString(result, "structuredContent", "sessionId"), _sessionId, StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            // Provider error text may contain echoed configuration. Do not copy
            // it into the canonical UI history or the model-visible error path.
            return false;
        }
    }

    private static string BuildMcpNativeRequest(string id, string method, string threadId) => BuildJsonString(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WriteString("id", id);
        writer.WriteString("method", method);
        writer.WritePropertyName("params");
        writer.WriteStartObject();
        writer.WriteString("threadId", threadId);
        if (method == "thread/read") writer.WriteBoolean("includeTurns", false);
        if (method == "mcpServerStatus/list")
        {
            writer.WriteString("serverName", "tlbx");
            writer.WriteString("detail", "toolsAndAuthOnly");
        }
        if (method == "mcpServer/tool/call")
        {
            writer.WriteString("server", "tlbx");
            writer.WriteString("tool", "context_get");
            writer.WritePropertyName("arguments");
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    });
}
