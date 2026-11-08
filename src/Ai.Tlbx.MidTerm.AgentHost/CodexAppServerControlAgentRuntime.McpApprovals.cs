using System.Globalization;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class CodexAppServerControlAgentRuntime
{
    private bool _mcpTurnStartPending;
    private bool _mcpTurnStartUnconfirmed;
    private const string McpToolApprovalType = "mcp_tool_approval";
    private const string McpElicitationMethod = "mcpServer/elicitation/request";

    private static bool IsMcpToolApproval(JsonElement payload)
    {
        if (GetString(payload, "mode") != "form" || GetString(payload, "_meta", "codex_approval_kind") != "mcp_tool_call" ||
            Traverse(payload, "message") is not { ValueKind: JsonValueKind.String } ||
            string.IsNullOrWhiteSpace(GetString(payload, "serverName")) || string.IsNullOrWhiteSpace(GetString(payload, "_meta", "tool_name")) ||
            Traverse(payload, "_meta", "tool_params") is not { ValueKind: JsonValueKind.Object } ||
            GetString(payload, "requestedSchema", "type") != "object" ||
            Traverse(payload, "requestedSchema", "properties") is not { ValueKind: JsonValueKind.Object } properties)
            return false;
        using var fields = properties.EnumerateObject();
        if (fields.MoveNext()) return false;
        var required = Traverse(payload, "requestedSchema", "required");
        return required is null or { ValueKind: JsonValueKind.Null } ||
            required is { ValueKind: JsonValueKind.Array } items && items.GetArrayLength() == 0;
    }

    private bool TryHandleMcpToolApproval(string method, JsonElement jsonRpcId, JsonElement payload)
    {
        if (method != McpElicitationMethod || jsonRpcId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number) ||
            jsonRpcId.ValueKind == JsonValueKind.Number && !jsonRpcId.TryGetInt64(out _) || !IsMcpToolApproval(payload)) return false;
        // Shared backends can broadcast requests for sibling clients. Never answer those requests.
        var threadId = GetString(payload, "threadId");
        var turnId = GetString(payload, "turnId");
        if (string.IsNullOrEmpty(threadId) || string.IsNullOrEmpty(turnId) ||
            !string.Equals(threadId, _providerThreadId, StringComparison.Ordinal) ||
            (!string.Equals(turnId, _activeTurnId, StringComparison.Ordinal) && !_mcpTurnStartPending)) return true;
        if (_pendingApprovals.Values.Any(p => p.RequestType == McpToolApprovalType && p.ThreadId == threadId && p.TurnId == turnId &&
            JsonElement.DeepEquals(p.JsonRpcId, jsonRpcId))) return true;
        var pending = new PendingCodexApproval
        {
            RequestId = "approval-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            JsonRpcId = jsonRpcId.Clone(), RequestType = McpToolApprovalType, RequestTypeLabel = "Tool approval",
            ThreadId = threadId, TurnId = turnId, McpAwaitingStartAck = _mcpTurnStartPending,
            Detail = AppServerControlProviderEventCompaction.CompactRequestDetail(
                GetString(payload, "message") + "\n" + GetString(payload, "serverName") + " / " +
                GetString(payload, "_meta", "tool_name") + "\n" + Traverse(payload, "_meta", "tool_params")!.Value.GetRawText(),
                McpToolApprovalType, pending: true)
        };
        _pendingApprovals[pending.RequestId] = pending;
        _emit(CreateEvent("request.opened", turnId, null, pending.RequestId, "codex.app-server.request", method, payload,
            e => e.RequestOpened = new() { RequestType = pending.RequestType, RequestTypeLabel = pending.RequestTypeLabel, Detail = pending.Detail }));
        if (CanAutoResolveMcpApproval(threadId, turnId)) _ = AutoResolveMcpApprovalAsync(pending);
        return true;
    }

    private bool CanAutoResolveMcpApproval(string threadId, string? turnId) =>
        !_mcpTurnStartPending && !_mcpTurnStartUnconfirmed && _quickSettings.PermissionMode == "auto" && _quickSettings.PlanMode == "off" && turnId is not null &&
        string.Equals(threadId, _providerThreadId, StringComparison.Ordinal) && string.Equals(turnId, _activeTurnId, StringComparison.Ordinal);

    private bool IsCurrentMcpApproval(PendingCodexApproval pending) =>
        !pending.McpAwaitingStartAck && pending.ThreadId is not null && pending.TurnId is not null &&
        string.Equals(pending.ThreadId, _providerThreadId, StringComparison.Ordinal) &&
        string.Equals(pending.TurnId, _activeTurnId, StringComparison.Ordinal) &&
        _pendingApprovals.TryGetValue(pending.RequestId, out var current) && ReferenceEquals(pending, current);

    private static string BuildMcpToolApprovalResponse(PendingCodexApproval pending, string decision) => BuildJsonString(writer =>
    {
        writer.WriteStartObject(); writer.WriteString("jsonrpc", "2.0");
        writer.WritePropertyName("id"); pending.JsonRpcId.WriteTo(writer);
        writer.WriteStartObject("result"); writer.WriteString("action", decision);
        if (decision == "accept") { writer.WriteStartObject("content"); writer.WriteEndObject(); }
        // No _meta/persist: this grants only the exact requested call, never session/always permission.
        writer.WriteEndObject(); writer.WriteEndObject();
    });

    private async Task<(bool Sent, bool Published)> SendMcpToolApprovalAsync(PendingCodexApproval pending, string decision, bool automatic, CancellationToken ct)
    {
        if (decision is not ("accept" or "decline" or "cancel"))
            throw new InvalidOperationException("MCP tool approval supports only accept, decline or cancel; persistence was not requested.");
        await _codexWriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsCurrentMcpApproval(pending) || automatic && !CanAutoResolveMcpApproval(pending.ThreadId!, pending.TurnId) ||
                Interlocked.CompareExchange(ref pending.McpResolutionState, 1, 0) != 0) return (false, false);
            // Keep the native request in the map while writing so provider cancellation can win.
            // A failed/uncertain write is never automatically replayed.
            await WriteCodexMessageCoreAsync(BuildMcpToolApprovalResponse(pending, decision), ct).ConfigureAwait(false);
            return CompleteMcpToolApprovalWrite(pending, decision);
        }
        finally { _codexWriteGate.Release(); }
    }

    private (bool Sent, bool Published) CompleteMcpToolApprovalWrite(PendingCodexApproval pending, string decision)
    {
        // A native resolution or turn-end cancellation may win after the successful write.
        // Delivery still succeeded; preserve that canonical settlement instead of throwing or replacing it.
        var published = _pendingApprovals.TryRemove(pending.RequestId, out _) &&
            string.Equals(pending.ThreadId, _providerThreadId, StringComparison.Ordinal);
        if (published)
            _emit(CreateEvent("request.resolved", pending.TurnId, null, pending.RequestId, "codex.app-server.request", McpElicitationMethod, default,
                e => e.RequestResolved = new() { RequestType = McpToolApprovalType, Decision = decision }));
        return (true, published);
    }

    private async Task ReconcileMcpStartApprovalsAsync(string threadId, string? acknowledgedTurnId, CancellationToken ct)
    {
        foreach (var pending in _pendingApprovals.Values.Where(p => p.RequestType == McpToolApprovalType && p.McpAwaitingStartAck))
        {
            if (pending.ThreadId == threadId && pending.TurnId == acknowledgedTurnId)
            {
                pending.McpAwaitingStartAck = false;
                continue;
            }
            if (!_pendingApprovals.TryRemove(pending.RequestId, out _) || pending.ThreadId != _providerThreadId) continue;
            try { await WriteCodexMessageAsync(BuildMcpToolApprovalResponse(pending, "cancel"), ct).ConfigureAwait(false); }
            catch (Exception error) when (error is InvalidOperationException or IOException or System.Net.WebSockets.WebSocketException or OperationCanceledException)
            {
                // The local start was not acknowledged for this ID; never retry an uncertain cancellation.
            }
            _emit(CreateEvent("request.resolved", pending.TurnId, null, pending.RequestId, "mtagenthost.codex", "turn/start-scope", default,
                e => e.RequestResolved = new() { RequestType = McpToolApprovalType, Decision = "cancel" }));
        }
    }

    private async Task<HostCommandOutcome> ResolveMcpToolApprovalAsync(AppServerControlHostCommandEnvelope command,
        PendingCodexApproval pending, string decision, CancellationToken ct)
    {
        var outcome = await SendMcpToolApprovalAsync(pending, decision, automatic: false, ct).ConfigureAwait(false);
        return McpApprovalOutcome(command, pending, decision, outcome);
    }

    private static HostCommandOutcome McpApprovalOutcome(AppServerControlHostCommandEnvelope command,
        PendingCodexApproval pending, string decision, (bool Sent, bool Published) outcome)
    {
        if (!outcome.Sent)
            throw new InvalidOperationException("MCP tool approval is settled, unconfirmed or outside the current thread/turn; no replacement decision was sent.");
        return Accepted(command.CommandId, command.SessionId, new() { SessionId = command.SessionId, Status = "accepted",
            RequestId = pending.RequestId, TurnId = pending.TurnId, Decision = outcome.Published ? decision : null });
    }

    private async Task AutoResolveMcpApprovalAsync(PendingCodexApproval pending)
    {
        try { await SendMcpToolApprovalAsync(pending, "accept", automatic: true, _shutdown.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is InvalidOperationException or IOException or System.Net.WebSockets.WebSocketException or OperationCanceledException)
        {
            if (!_shutdown.IsCancellationRequested && string.Equals(pending.ThreadId, _providerThreadId, StringComparison.Ordinal))
                _emit(CreateEvent("runtime.warning", pending.TurnId, null, pending.RequestId, "mtagenthost.codex", McpElicitationMethod, default,
                    e => e.RuntimeNoticeOnly = new() { Message = "MCP tool approval delivery is unconfirmed; no automatic retry was performed." }));
        }
    }

    private async Task ReleasePendingMcpApprovalsAsync(string threadId, string? turnId)
    {
        if (!CanAutoResolveMcpApproval(threadId, turnId)) return;
        foreach (var pending in _pendingApprovals.Values.Where(p => p.RequestType == McpToolApprovalType &&
            p.ThreadId == threadId && p.TurnId == turnId)) await AutoResolveMcpApprovalAsync(pending).ConfigureAwait(false);
    }
}
