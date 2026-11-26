using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class AcpAppServerControlAgentRuntime
{
    private readonly Dictionary<string, AcpSubagentState> _subagents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AcpSessionMessageState> _sessionMessages = new(StringComparer.Ordinal);

    private void ResetSubagentState()
    {
        _subagents.Clear();
        _sessionMessages.Clear();
    }

    private void HandleSubagentUpdate(JsonElement update, JsonElement root, string rawLine, string? turnId)
    {
        var childSessionId = GetString(update, "sessionId");
        if (string.IsNullOrWhiteSpace(childSessionId))
        {
            return;
        }

        if (!_subagents.TryGetValue(childSessionId, out var subagent))
        {
            subagent = new AcpSubagentState
            {
                SessionId = childSessionId,
                ParentSessionId = GetString(root, "params", "sessionId") ?? _providerThreadId ?? string.Empty,
                TurnId = turnId ?? _activeTurnId ?? $"acp-subagent:{childSessionId}"
            };
            _subagents[childSessionId] = subagent;
        }

        if (update.TryGetProperty("title", out var titleEl))
        {
            subagent.Title = titleEl.ValueKind == JsonValueKind.String ? titleEl.GetString() : null;
        }

        if (update.TryGetProperty("description", out var descEl))
        {
            subagent.Description = descEl.ValueKind == JsonValueKind.String ? descEl.GetString() : null;
        }

        if (update.TryGetProperty("cwd", out var cwdEl))
        {
            subagent.Cwd = cwdEl.ValueKind == JsonValueKind.String ? cwdEl.GetString() : null;
        }

        if (update.TryGetProperty("capabilities", out var capsEl))
        {
            subagent.CanCancel = capsEl.ValueKind == JsonValueKind.Object &&
                Traverse(capsEl, "cancel") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
        }

        if (update.TryGetProperty("state", out var stateEl))
        {
            ApplySubagentStatePatch(subagent, stateEl);
        }

        EmitSubagentItem(subagent, "subagent_update", root, rawLine);
    }

    private void HandleChildSessionUpdate(string childSessionId, JsonElement root, string rawLine)
    {
        if (!_subagents.TryGetValue(childSessionId, out var subagent))
        {
            return;
        }

        var update = Traverse(root, "params", "update");
        if (update is not { ValueKind: JsonValueKind.Object } updateElement)
        {
            return;
        }

        var updateType = GetString(updateElement, "sessionUpdate");
        switch (updateType)
        {
            case "state_update":
                ApplySubagentStatePatch(subagent, updateElement);
                EmitSubagentItem(subagent, "state_update", root, rawLine);
                break;
            case "subagent_update":
                HandleSubagentUpdate(updateElement, root, rawLine, subagent.TurnId);
                break;
            case "session_message":
            case "session_message_chunk":
                HandleSessionMessageUpdate(updateType, updateElement, root, rawLine, subagent.TurnId, childSessionId);
                break;
            case "agent_message":
            case "agent_message_chunk":
            {
                var text = ExtractContentText(updateElement);
                if (!string.IsNullOrEmpty(text))
                {
                    if (updateType == "agent_message")
                    {
                        subagent.Output = new BoundedToolOutputAccumulator();
                    }
                    subagent.Output.Append(text);
                    EmitSubagentItem(subagent, updateType, root, rawLine);
                }
                break;
            }
            case "tool_call":
            case "tool_call_update":
            {
                var toolTitle = GetString(updateElement, "title") ?? GetString(updateElement, "toolCallId");
                var toolStatus = GetString(updateElement, "status");
                if (!string.IsNullOrWhiteSpace(toolTitle))
                {
                    subagent.LastActivity = string.IsNullOrWhiteSpace(toolStatus) ? toolTitle : $"{toolTitle} ({toolStatus})";
                    EmitSubagentItem(subagent, updateType, root, rawLine);
                }
                break;
            }
            case "session_info_update":
            {
                var title = GetString(updateElement, "title");
                if (!string.IsNullOrWhiteSpace(title))
                {
                    subagent.Title = title;
                    EmitSubagentItem(subagent, updateType, root, rawLine);
                }
                break;
            }
        }
    }

    private void HandleSessionMessageUpdate(
        string updateType,
        JsonElement update,
        JsonElement root,
        string rawLine,
        string? turnId,
        string? enclosingSessionId = null)
    {
        var messageId = GetString(update, "messageId");
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return;
        }

        var scope = enclosingSessionId ?? GetString(root, "params", "sessionId") ?? _providerThreadId ?? string.Empty;
        var key = $"{scope}:{messageId}";
        if (!_sessionMessages.TryGetValue(key, out var msg))
        {
            msg = new AcpSessionMessageState
            {
                MessageId = messageId,
                TurnId = turnId ?? _activeTurnId ?? $"acp-session-msg:{messageId}"
            };
            _sessionMessages[key] = msg;
        }

        if (updateType == "session_message")
        {
            if (update.TryGetProperty("senderSessionId", out var senderEl))
                msg.SenderSessionId = senderEl.ValueKind == JsonValueKind.String ? senderEl.GetString() : null;
            if (update.TryGetProperty("recipientSessionId", out var recipientEl))
                msg.RecipientSessionId = recipientEl.ValueKind == JsonValueKind.String ? recipientEl.GetString() : null;
            if (update.TryGetProperty("content", out var contentEl))
            {
                msg.Body = contentEl.ValueKind == JsonValueKind.Null ? string.Empty : ExtractText(contentEl);
            }
        }
        else
        {
            msg.Body += ExtractContentText(update);
        }

        const int maxSessionMessageChars = 64 * 1024;
        if (msg.Body.Length > maxSessionMessageChars)
        {
            msg.Body = msg.Body[^maxSessionMessageChars..];
        }

        var senderLabel = FormatSessionDisplayName(msg.SenderSessionId);
        var recipientLabel = FormatSessionDisplayName(msg.RecipientSessionId);
        var title = string.IsNullOrWhiteSpace(senderLabel) && string.IsNullOrWhiteSpace(recipientLabel)
            ? "Session message"
            : $"{senderLabel ?? "session"} → {recipientLabel ?? "session"}";

        var presentation = new AppServerControlToolPresentation
        {
            Category = "subagent",
            Label = "Subagent message",
            ToolName = "session_message",
            Subject = title,
            Evidence = msg.Body,
            EvidenceKind = "text",
            TotalLineCount = msg.Body.Length == 0 ? 0 : 1 + msg.Body.Count(static ch => ch == '\n')
        };

        _emit(CreateEvent("item.updated", msg.TurnId, $"acp:session-msg:{key}", null, "acp", updateType, root,
            e => e.Item = new AppServerControlProviderItemPayload
            {
                Replace = true,
                ItemType = "subagent_message",
                Status = "completed",
                Title = title,
                Detail = msg.Body,
                ToolPresentation = presentation
            }, rawLine));
    }

    private async Task CancelActiveSubagentsAsync(CancellationToken ct)
    {
        var active = _subagents.Values
            .Where(static s => s.CanCancel && s.State is "running" or "requires_action")
            .ToArray();
        foreach (var child in active)
        {
            try
            {
                await WriteJsonRpcNotificationAsync(
                    "session/cancel",
                    writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteString("sessionId", child.SessionId);
                        writer.WriteEndObject();
                    },
                    ct).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort child cancel propagation.
            }
        }
    }

    private static void ApplySubagentStatePatch(AcpSubagentState subagent, JsonElement stateEl)
    {
        if (stateEl.ValueKind == JsonValueKind.Null)
        {
            subagent.State = null;
            subagent.StopReason = null;
            subagent.ErrorMessage = null;
            return;
        }

        if (stateEl.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        subagent.State = GetString(stateEl, "state") ?? subagent.State;
        subagent.StopReason = GetString(stateEl, "stopReason");
        var err = Traverse(stateEl, "error");
        subagent.ErrorMessage = err is { ValueKind: JsonValueKind.Object } errObj ? ReadErrorMessage(errObj) : null;
    }

    private void EmitSubagentItem(AcpSubagentState subagent, string method, JsonElement root, string rawLine)
    {
        var status = subagent.State switch
        {
            "running" or "requires_action" => "in_progress",
            "idle" when subagent.StopReason == "cancelled" => "cancelled",
            "idle" when subagent.StopReason == "error" => "failed",
            "idle" => "completed",
            _ => "in_progress"
        };

        var title = !string.IsNullOrWhiteSpace(subagent.Title)
            ? subagent.Title!
            : $"Subagent {subagent.SessionId}";
        var presentation = new AppServerControlToolPresentation
        {
            Category = "subagent",
            Label = "Delegated task",
            ToolName = subagent.SessionId,
            Subject = string.IsNullOrWhiteSpace(subagent.Description) ? title : $"{title}: {subagent.Description}",
            Outcome = subagent.ErrorMessage ?? subagent.LastActivity ?? subagent.StopReason ?? subagent.State
        };
        subagent.Output.ApplyTo(presentation, status);

        var eventType = status is "completed" or "failed" or "cancelled" ? "item.completed" : "item.updated";
        _emit(CreateEvent(eventType, subagent.TurnId, $"acp:subagent:{subagent.SessionId}", null, "acp", method, root,
            e => e.Item = new AppServerControlProviderItemPayload
            {
                Replace = true,
                ItemType = "subagent",
                Status = status,
                Title = title,
                Detail = presentation.Subject,
                ToolPresentation = presentation
            }, rawLine));
    }

    private string? FormatSessionDisplayName(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        if (string.Equals(sessionId, _providerThreadId, StringComparison.Ordinal)) return _agentName;
        if (_subagents.TryGetValue(sessionId, out var sub) && !string.IsNullOrWhiteSpace(sub.Title)) return sub.Title;
        return sessionId;
    }

    private sealed class AcpSubagentState
    {
        public string SessionId { get; init; } = string.Empty;
        public string ParentSessionId { get; init; } = string.Empty;
        public string TurnId { get; init; } = string.Empty;
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? Cwd { get; set; }
        public bool CanCancel { get; set; }
        public string? State { get; set; }
        public string? StopReason { get; set; }
        public string? ErrorMessage { get; set; }
        public string? LastActivity { get; set; }
        public BoundedToolOutputAccumulator Output { get; set; } = new();
    }

    private sealed class AcpSessionMessageState
    {
        public string MessageId { get; init; } = string.Empty;
        public string TurnId { get; init; } = string.Empty;
        public string? SenderSessionId { get; set; }
        public string? RecipientSessionId { get; set; }
        public string Body { get; set; } = string.Empty;
    }
}
