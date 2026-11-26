using System.Globalization;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class AcpAppServerControlAgentRuntime
{
    private int _protocolGeneration = 1;
    private string _foregroundState = "idle";
    private readonly Dictionary<(string Kind, string Id), AcpMessageState> _nativeMessages = [];
    private readonly Dictionary<string, string> _insertedUserTurns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(JsonElement Update, JsonElement Root, string Raw)>> _earlyUserMessages = new(StringComparer.Ordinal);
    private JsonElement? _loadedStateSnapshot;

    private void ResetProtocolState()
    {
        _protocolGeneration = 1;
        _foregroundState = "idle";
        _nativeMessages.Clear();
        _insertedUserTurns.Clear();
        _earlyUserMessages.Clear();
        _nativeTerminals.Clear();
        _nativeCompactions.Clear();
        ResetSubagentState();
        ResetUnstableState();
        _loadedStateSnapshot = null;
        _lastPlanMarkdown = null;
        _tools.Clear();
        _configOptionIds.Clear();
        _currentModeId = _planModeId = _defaultModeId = null;
        _antigravityModeBeforePlan = "default";
        _availableCommandsSignature = _lastSessionNotificationSignature = null;
        _availableCommands.Clear();
        _usage = null;
    }

    private void HandleV2PromptResponse(string turnId, JsonElement root, string raw)
    {
        if (root.TryGetProperty("error", out var error))
        {
            if (string.Equals(_activeTurnId, turnId, StringComparison.Ordinal))
            {
                if (_foregroundState == "idle")
                {
                    var cancelled = _cancellingTurnId == turnId;
                    CompleteTurn(turnId, cancelled ? "interrupted" : "failed", cancelled ? "Interrupted" : "Failed",
                        cancelled ? "cancelled" : "error", cancelled ? null : ReadErrorMessage(error), raw);
                }
                else EmitRuntimeMessage("runtime.error", ReadErrorMessage(error), raw);
            }
            return;
        }

        var messageId = GetString(root, "result", "messageId");
        if (string.IsNullOrWhiteSpace(messageId))
        {
            EmitRuntimeMessage("runtime.error", $"{_agentName} returned an invalid ACP v2 prompt acknowledgement without messageId.", raw);
            return;
        }

        _insertedUserTurns[messageId] = turnId;
        if (_earlyUserMessages.Remove(messageId, out var early))
        {
            foreach (var item in early) HandleV2Message("user", item.Update, item.Root, item.Raw, turnId);
        }
        // This is insertion, not completion. Only state_update idle settles work.
    }

    private bool HandleV2Update(string? type, JsonElement update, JsonElement root, string raw, string? turnId)
    {
        switch (type)
        {
            case "state_update":
                if (_applyingHistoryReplay) _loadedStateSnapshot = update.Clone();
                else HandleV2State(update, root, raw);
                return true;
            case "user_message":
            case "user_message_chunk":
                HandleV2Message("user", update, root, raw, turnId);
                return true;
            case "agent_message":
            case "agent_message_chunk":
                HandleV2Message("assistant", update, root, raw, turnId);
                return true;
            case "agent_thought":
            case "agent_thought_chunk":
                HandleV2Message("reasoning", update, root, raw, turnId);
                return true;
            case "plan_update":
                HandleV2Plan(update, root, raw, turnId);
                return true;
            case "plan_removed":
                HandlePlanRemoved(update, root, raw, turnId);
                return true;
            case "replay_complete":
                EmitRuntimeMessage("runtime.notice", $"{_agentName} completed ACP history replay.", string.Empty, "acp", "replay_complete", root, raw, timelineVisible: false);
                return true;
            case "tool_call_update":
                HandleToolCallUpdate(ResolveV2ToolTurn(update, turnId), update, root, raw);
                return true;
            case "tool_call_content_chunk":
                using (var stream = new MemoryStream())
                {
                    using (var writer = new Utf8JsonWriter(stream))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("toolCallId", GetString(update, "toolCallId"));
                        writer.WritePropertyName("content");
                        writer.WriteStartArray();
                        if (update.TryGetProperty("content", out var content)) content.WriteTo(writer);
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }
                    using var document = JsonDocument.Parse(stream.GetBuffer().AsMemory(0, checked((int)stream.Length)));
                    HandleToolCallUpdate(ResolveV2ToolTurn(update, turnId), document.RootElement, root, raw, appendContent: true);
                }
                return true;
            case "notice":
                EmitRuntimeMessage(GetString(update, "severity") == "error" ? "runtime.error" : "runtime.notice",
                    GetString(update, "title") ?? "Agent notice", GetString(update, "description") ?? string.Empty, timelineVisible: false);
                return true;
            case "terminal_update":
            case "terminal_output_chunk":
                HandleV2Terminal(type, update, root, raw, turnId);
                return true;
            case "compaction_update":
            case "compaction_summary_chunk":
                HandleV2Compaction(type, update, root, raw, turnId);
                return true;
            case "subagent_update":
                HandleSubagentUpdate(update, root, raw, turnId);
                return true;
            case "session_message":
            case "session_message_chunk":
                HandleSessionMessageUpdate(type, update, root, raw, turnId);
                return true;
            case "prompt_suggestions_update":
                HandlePromptSuggestionsUpdate(update, root, raw);
                return true;
            case "cursor_update":
                HandleCursorUpdate(update, root, raw);
                return true;
            default:
                return false;
        }
    }

    private string ResolveV2ToolTurn(JsonElement update, string? turnId)
    {
        var toolId = GetString(update, "toolCallId") ?? "unknown-tool";
        return _tools.TryGetValue(toolId, out var state) && state.TurnId is { } owner
            ? owner : turnId ?? _activeTurnId ?? $"acp-background:{toolId}";
    }

    private void HandleV2Plan(JsonElement update, JsonElement root, string raw, string? turnId)
    {
        if (Traverse(update, "plan") is not { ValueKind: JsonValueKind.Object } plan ||
            GetString(plan, "planId") is not { Length: > 0 } id)
        {
            EmitRuntimeMessage("runtime.notice", "ACP plan update omitted plan.planId.", raw, timelineVisible: false);
            return;
        }

        if (!_nativeMessages.TryGetValue(("plan", id), out var entity))
        {
            entity = new AcpMessageState { TurnId = turnId ?? _activeTurnId ?? $"acp-background:{id}" };
            _nativeMessages[("plan", id)] = entity;
        }

        var planType = GetString(plan, "type");
        string status;
        string title;
        string detail;
        if (string.Equals(planType, "items", StringComparison.Ordinal) &&
            Traverse(plan, "entries") is { ValueKind: JsonValueKind.Array } entries)
        {
            var completed = true;
            var hasEntries = false;
            using (var enumerator = entries.EnumerateArray())
            {
                while (enumerator.MoveNext())
                {
                    hasEntries = true;
                    completed &= GetString(enumerator.Current, "status") is "completed" or "cancelled";
                }
            }

            status = hasEntries && completed ? "completed" : "in_progress";
            title = "Plan";
            detail = BuildPlanMarkdown(plan);
        }
        else if (string.Equals(planType, "markdown", StringComparison.Ordinal))
        {
            var markdown = GetString(plan, "content") ?? string.Empty;
            var path = GetString(plan, "path");
            status = "in_progress";
            title = string.IsNullOrWhiteSpace(path) ? "Plan" : $"Plan ({path})";
            detail = string.IsNullOrWhiteSpace(path) ? markdown : $"<!-- path: {path} -->\n{markdown}";
        }
        else
        {
            // Preserve custom/future ("other") plan variants cleanly rather than dropping them.
            status = "in_progress";
            title = string.IsNullOrWhiteSpace(planType) ? "Plan" : $"Plan ({planType})";
            detail = plan.GetRawText();
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            _lastPlanMarkdown = detail;
        }

        _emit(CreateEvent("item.updated", entity.TurnId, $"acp:plan:{id}", null, "acp", "plan_update", root,
            e => e.Item = new AppServerControlProviderItemPayload
            {
                Replace = true,
                ItemType = "plan",
                Status = status,
                Title = title,
                Detail = detail
            }, raw));
    }

    private void HandlePlanRemoved(JsonElement update, JsonElement root, string raw, string? turnId)
    {
        var id = GetString(update, "planId");
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var resolvedTurnId = _nativeMessages.Remove(("plan", id), out var entity)
            ? entity.TurnId
            : turnId ?? _activeTurnId ?? $"acp-background:{id}";

        _emit(CreateEvent("item.completed", resolvedTurnId, $"acp:plan:{id}", null, "acp", "plan_removed", root,
            e => e.Item = new AppServerControlProviderItemPayload
            {
                Replace = true,
                ItemType = "plan",
                Status = "cancelled",
                Title = "Plan (removed)",
                Detail = string.Empty
            }, raw));
    }

    private async Task<HostCommandOutcome> CancelToolCallAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        if (_protocolGeneration != 2)
        {
            throw new InvalidOperationException($"{_agentName} tool_call/cancel requires ACP v2.");
        }

        var toolCallId = command.CancelTarget?.TargetId?.Trim();
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            throw new InvalidOperationException("toolCallId is required.");
        }

        if (!_tools.TryGetValue(toolCallId, out var tool))
        {
            throw new InvalidOperationException($"{_agentName} tool call '{toolCallId}' was not found.");
        }

        if (IsTerminalToolStatus(tool.Status))
        {
            throw new InvalidOperationException($"{_agentName} tool call '{toolCallId}' is already {tool.Status}.");
        }

        if (!tool.CanCancel)
        {
            throw new InvalidOperationException($"{_agentName} tool call '{toolCallId}' does not advertise capabilities.cancel.");
        }

        await SendRequestAsync("tool_call/cancel", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", _providerThreadId);
            writer.WriteString("toolCallId", toolCallId);
            writer.WriteEndObject();
        }, AttachRequestTimeout, ct).ConfigureAwait(false);

        return Accepted(command.CommandId, command.SessionId);
    }

    private async Task<HostCommandOutcome> CancelTerminalAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        if (_protocolGeneration != 2)
        {
            throw new InvalidOperationException($"{_agentName} terminal/cancel requires ACP v2.");
        }

        var terminalId = command.CancelTarget?.TargetId?.Trim();
        if (string.IsNullOrWhiteSpace(terminalId))
        {
            throw new InvalidOperationException("terminalId is required.");
        }

        if (!_nativeTerminals.TryGetValue(terminalId, out var terminal))
        {
            throw new InvalidOperationException($"{_agentName} terminal '{terminalId}' was not found.");
        }

        if (terminal.Exited)
        {
            throw new InvalidOperationException($"{_agentName} terminal '{terminalId}' has already exited.");
        }

        if (!terminal.CanCancel)
        {
            throw new InvalidOperationException($"{_agentName} terminal '{terminalId}' does not advertise capabilities.cancel.");
        }

        await SendRequestAsync("terminal/cancel", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", _providerThreadId);
            writer.WriteString("terminalId", terminalId);
            writer.WriteEndObject();
        }, AttachRequestTimeout, ct).ConfigureAwait(false);

        return Accepted(command.CommandId, command.SessionId);
    }

    private void HandleV2Message(string kind, JsonElement update, JsonElement root, string raw, string? turnId)
    {
        var id = GetString(update, "messageId");
        if (string.IsNullOrWhiteSpace(id))
        {
            EmitRuntimeMessage("runtime.notice", $"{_agentName} omitted a required ACP v2 messageId.", raw, timelineVisible: false);
            return;
        }

        if (kind == "user" && !_applyingHistoryReplay && !_insertedUserTurns.TryGetValue(id, out turnId) && !_pendingPromptTurns.IsEmpty)
        {
            // Insertion echoes may precede the RPC response. Reconcile by the
            // returned native ID, never by content or arrival order.
            if (!_earlyUserMessages.TryGetValue(id, out var early))
                _earlyUserMessages[id] = early = [];
            early.Add((update.Clone(), root.Clone(), raw));
            return;
        }
        var first = false;
        if (!_nativeMessages.TryGetValue((kind, id), out var message))
        {
            first = true;
            message = new AcpMessageState { TurnId = turnId ?? _activeTurnId ?? $"acp-background:{id}" };
            _nativeMessages[(kind, id)] = message;
        }
        if (!update.TryGetProperty("content", out _)) return;

        var text = ExtractContentText(update);
        var replace = first || GetString(update, "sessionUpdate")?.EndsWith("_chunk", StringComparison.Ordinal) != true;
        if (kind == "user")
        {
            // The host also renders local submissions immediately. Each native
            // user snapshot replaces that provisional row, including when an
            // echo is split around the prompt acknowledgement.
            message.Text = replace ? text : message.Text + text;
            const int maxUserMessageChars = 256 * 1024;
            if (message.Text.Length > maxUserMessageChars) message.Text = message.Text[^maxUserMessageChars..];
            text = message.Text;
            replace = true;
        }
        var streamKind = kind switch { "user" => "user_text", "reasoning" => "reasoning_text", _ => "assistant_text" };
        _emit(CreateEvent("content.delta", message.TurnId, $"acp:{kind}:{id}", null, "acp", GetString(update, "sessionUpdate"), root,
            e => e.ContentDelta = new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = streamKind,
                Delta = text,
                Replace = replace
            }, raw));
    }

    private void HandleV2State(JsonElement update, JsonElement root, string raw)
    {
        var state = GetString(update, "state");
        if (state is not ("running" or "requires_action" or "idle")) return;
        var wasForeground = _foregroundState != "idle";
        _foregroundState = state;
        if (state == "idle")
        {
            if (!wasForeground) return;
            if (_activeTurnId is not { } activeTurnId) return;
            var reason = GetString(update, "stopReason");
            var error = Traverse(update, "error");
            var (status, label) = reason switch
            {
                "cancelled" => ("interrupted", "Interrupted"),
                "error" => ("failed", "Failed"),
                "end_turn" or "max_tokens" or "max_turn_requests" or "refusal" => ("completed", "Completed"),
                _ => ("stopped", "Stopped")
            };
            CompleteTurn(activeTurnId, status, label, reason,
                reason == "error" && error is { ValueKind: JsonValueKind.Object } detail ? ReadErrorMessage(detail) : null, raw);
            return;
        }

        if (_activeTurnId is null)
        {
            _activeTurnId = "turn-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            _activeTurnCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _emit(CreateEvent("turn.started", _activeTurnId, null, null, "acp", "state_update", root,
                e => e.TurnStarted = new AppServerControlProviderTurnStartedPayload(), raw));
        }
        _emit(CreateEvent("session.state.changed", _activeTurnId, null, null, "acp", "state_update", root,
            e => e.SessionState = new AppServerControlProviderSessionStatePayload
            {
                State = state == "running" ? "running" : "waiting",
                StateLabel = state == "running" ? "Running" : "Waiting",
                Reason = state == "running" ? "Agent foreground work is running." : "Agent foreground work requires user action."
            }, raw));
    }

    private sealed class AcpMessageState
    {
        public string TurnId { get; init; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }
}
