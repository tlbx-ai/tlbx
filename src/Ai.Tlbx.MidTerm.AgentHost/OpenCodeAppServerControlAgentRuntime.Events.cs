using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class OpenCodeAppServerControlAgentRuntime
{
    private bool ProjectEvent(JsonElement evt)
    {
        var type = Text(evt, "type");
        var properties = Value(evt, "properties");
        var session = Text(properties, "sessionID") ?? Text(Value(properties, "info"), "sessionID") ?? Text(Value(properties, "part"), "sessionID");
        if (type is "session.created" or "session.updated")
        {
            var info = Value(properties, "info");
            var parent = Text(info, "parentID");
            var child = Text(info, "id");
            if (parent is not null && child is not null && _nativeSessions.Contains(parent))
            {
                _nativeSessions.Add(child);
                if (_turnId is not null) _childTurns.TryAdd(child, _turnId);
            }
            return false;
        }
        if (session is not null && !_nativeSessions.Contains(session)) return false;
        if (session is not null && session != _threadId)
        {
            // A delegated agent can ask the operator a question or request
            // permission. Its idle/error events must never finish its parent.
            if (type == "question.asked") OpenRequest(properties, question: true);
            else if (type == "permission.asked") OpenRequest(properties, question: false);
            else if (type == "question.replied") CloseRequest(Text(properties, "requestID"), "answered", Value(properties, "answers"));
            else if (type == "question.rejected") CloseRequest(Text(properties, "requestID"), "cancelled");
            else if (type == "permission.replied") CloseRequest(Text(properties, "requestID"), Text(properties, "reply") ?? "resolved");
            else if (type is "session.status" or "session.idle" or "session.error")
            {
                var status = type == "session.error" ? "failed" : type == "session.idle" || Text(Value(properties, "status"), "type") == "idle" ? "completed" : "running";
                Publish("task.updated", e => e.Task = new() { TaskId = session, TaskType = "subagent", Status = status, Description = "OpenCode subagent" }, turn: _childTurns.GetValueOrDefault(session) ?? _turnId);
            }
            return false;
        }
        switch (type)
        {
            case "message.updated":
                ProjectMessage(Value(properties, "info"), replay: false);
                break;
            case "message.part.updated":
                ProjectPart(Value(properties, "part"));
                break;
            case "message.part.delta":
                var id = Text(properties, "partID");
                if (id is not null && _parts.TryGetValue(id, out var part) && Text(properties, "field") == "text")
                {
                    using var buffer = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(buffer))
                    {
                        writer.WriteStartObject();
                        foreach (var property in Properties(part)) if (property.Name != "text") property.WriteTo(writer);
                        writer.WriteString("text", (Text(part, "text") ?? string.Empty) + Text(properties, "delta"));
                        writer.WriteEndObject();
                    }
                    using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
                    ProjectPart(document.RootElement);
                }
                break;
            case "message.part.removed":
                var removedId = Text(properties, "partID");
                if (removedId is not null && _parts.Remove(removedId, out var removed))
                {
                    var kind = Text(removed, "type");
                    if (kind is "text" or "reasoning")
                        Publish("content.delta", e => e.ContentDelta = new() { StreamKind = kind == "text" ? "assistant_text" : "reasoning_text", Delta = string.Empty, Replace = true }, item: removedId, turn: TurnFor(Text(removed, "messageID")));
                }
                break;
            case "session.status":
                var status = Text(Value(properties, "status"), "type");
                if (status == "busy") _busy = true;
                if (status == "retry") Publish("runtime.warning", e => e.RuntimeMessage = new() { Message = "OpenCode is retrying the provider request.", Detail = Text(Value(properties, "status"), "message") });
                return status == "idle" && CanFinishFromIdle();
            case "session.idle":
                return CanFinishFromIdle();
            case "session.error":
                var error = Value(properties, "error");
                if (IsExpectedCancellation(error)) break;
                _error = Text(Value(error, "data"), "message") ?? Text(error, "name") ?? "OpenCode reported a session error.";
                if (_turnId is not null) _busy = true;
                else Publish("runtime.error", e => e.RuntimeMessage = new() { Message = _error });
                break;
            case "permission.asked": OpenRequest(properties, question: false); break;
            case "question.asked": OpenRequest(properties, question: true); break;
            case "permission.replied": CloseRequest(Text(properties, "requestID"), Text(properties, "reply") ?? "resolved"); break;
            case "question.replied": CloseRequest(Text(properties, "requestID"), "answered", Value(properties, "answers")); break;
            case "question.rejected": CloseRequest(Text(properties, "requestID"), "cancelled"); break;
            case "session.compacted":
                Publish("runtime.notice", e => e.RuntimeMessage = new() { Message = "Conversation compacted" });
                break;
        }
        return false;
    }

    private string TurnFor(string? message)
    {
        if (message is not null && _messageTurns.TryGetValue(message, out var turn)) return turn;
        return _turnId ?? "opencode-history:" + message;
    }

    private void ProjectMessage(JsonElement message, bool replay)
    {
        var id = Text(message, "id");
        if (id is null) return;
        var role = Text(message, "role");
        if (role == "user" && id == _userMessageId) _userReceipt = true;
        _messages[id] = message.Clone();
        if (!_messageTurns.ContainsKey(id))
        {
            var parent = Text(message, "parentID");
            _messageTurns[id] = parent is not null && _messageTurns.TryGetValue(parent, out var parentTurn) ? parentTurn : replay || _turnId is null ? "opencode-history:" + (parent ?? id) : _turnId;
        }
        if (role == "assistant")
        {
            var current = !replay && _turnId is not null && TurnFor(id) == _turnId;
            if (current) _assistantSeen = true;
            var error = Value(message, "error");
            if (current && error.ValueKind == JsonValueKind.Object && !IsExpectedCancellation(error))
                _error = Text(Value(error, "data"), "message") ?? Text(error, "name");
            PublishUsage();
        }
    }

    private bool IsExpectedCancellation(JsonElement error) =>
        _interrupting && Text(error, "name") == "MessageAbortedError";

    private bool CanFinishFromIdle() => _turnId is not null && _requests.Count == 0 &&
        (_assistantSeen || (_busy && _userReceipt) || (_interrupting && (_userReceipt || _userMessageId is null)) || _error is not null);

    private void ProjectPart(JsonElement part)
    {
        var id = Text(part, "id");
        var messageId = Text(part, "messageID");
        if (id is null || messageId is null) return;
        _parts[id] = part.Clone();
        var kind = Text(part, "type");
        var turn = TurnFor(messageId);
        var user = _messages.TryGetValue(messageId, out var message) && Text(message, "role") == "user";
        if (user)
        {
            // Publish a whole native user message, not one row per text/file part.
            // The common projector reconciles this with the accepted local row.
            var ownParts = _parts.Values.Where(p => Text(p, "messageID") == messageId).ToArray();
            var text = string.Join("\n", ownParts.Where(p => Text(p, "type") == "text" && Value(p, "synthetic").ValueKind != JsonValueKind.True && Value(p, "ignored").ValueKind != JsonValueKind.True).Select(p => Text(p, "text")));
            Publish("item.completed", e => e.Item = new()
            {
                Replace = true,
                ItemType = "user_message",
                Status = "completed",
                Title = "You",
                Detail = text,
                Attachments = ownParts.Where(p => Text(p, "type") == "file").Select(ReadAttachment).Where(a => a is not null).Select(a => a!).ToList()
            }, item: messageId, turn: turn, at: Timestamp(Value(message, "time", "created")));
            return;
        }
        if (kind is "text" or "reasoning")
        {
            Publish("content.delta", e => e.ContentDelta = new() { StreamKind = kind == "text" ? "assistant_text" : "reasoning_text", Delta = Text(part, "text") ?? string.Empty, Replace = true }, item: id, turn: turn);
        }
        else if (kind == "tool")
        {
            var state = Value(part, "state");
            var nativeStatus = Text(state, "status");
            var status = nativeStatus == "error" ? "failed" : nativeStatus == "completed" ? "completed" : "running";
            var name = Text(part, "tool") ?? "Tool";
            var detail = Text(state, "output") ?? Text(state, "error") ?? Text(state, "title");
            var nativeDiff = Text(Value(state, "metadata"), "diff");
            if (!string.IsNullOrEmpty(nativeDiff))
            {
                detail = string.IsNullOrEmpty(detail) ? nativeDiff : detail + "\n" + nativeDiff;
                Publish("diff.updated", e => e.DiffUpdated = new() { UnifiedDiff = nativeDiff }, item: id, turn: turn);
            }
            var input = Value(state, "input");
            var childSession = Text(Value(state, "metadata"), "sessionId") ?? Text(Value(state, "metadata"), "sessionID");
            if (childSession is not null && _nativeSessions.Contains(childSession)) _childTurns[childSession] = turn;
            var presentation = AppServerControlToolPresentationProjector.FromAcp("tool", status, name, name, input);
            presentation.Evidence = detail;
            Publish(status == "running" ? "item.started" : "item.completed", e => e.Item = new() { Replace = true, ItemType = "tool", Status = status, Title = Text(state, "title") ?? name, Detail = detail, ToolPresentation = presentation }, item: id, turn: turn);
        }
    }

    private AppServerControlAttachmentReference? ReadAttachment(JsonElement part)
    {
        var url = Text(part, "url");
        if (string.IsNullOrEmpty(url)) return null;
        if (_submittedAttachments.TryGetValue((Text(part, "messageID") ?? string.Empty, url), out var submitted) &&
            File.Exists(submitted.Path)) return submitted;
        var mime = Text(part, "mime") ?? "application/octet-stream";
        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(url, UriKind.Absolute, out var file))
            return new() { Kind = mime.StartsWith("image/", StringComparison.Ordinal) ? "image" : "file", Path = file.LocalPath, MimeType = mime, DisplayName = Text(part, "filename") };
        // Native data URLs are retained by OpenCode. Materialize only bounded
        // image parts in the owned cache, never arbitrary remote URLs.
        if (!mime.StartsWith("image/", StringComparison.Ordinal) || !url.StartsWith("data:" + mime + ";base64,", StringComparison.Ordinal) || url.Length > 14 * 1024 * 1024) return null;
        try
        {
            var bytes = Convert.FromBase64String(url[(url.IndexOf(',', StringComparison.Ordinal) + 1)..]);
            var directory = Path.Combine(Path.GetTempPath(), "tlbx-opencode-images", _sessionId);
            Directory.CreateDirectory(directory);
            var filename = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) + (mime == "image/jpeg" ? ".jpg" : mime == "image/webp" ? ".webp" : mime == "image/gif" ? ".gif" : ".png");
            var path = Path.Combine(directory, filename);
            if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
            return new() { Kind = "image", Path = path, MimeType = mime, DisplayName = Text(part, "filename") };
        }
        catch (FormatException) { return null; }
    }

    private async Task RefreshHistoryAsync(bool replay, CancellationToken ct)
    {
        var session = await SendAsync(HttpMethod.Get, SessionPath(), null, ct).ConfigureAwait(false);
        var revertedMessage = Text(Value(session, "revert"), "messageID");
        var messages = await SendAsync(HttpMethod.Get, SessionPath() + "/message", null, ct).ConfigureAwait(false);
        var records = Array(messages);
        lock (_state)
        {
            var replayTurns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in records)
            {
                var info = Value(record, "info");
                // OpenCode keeps reverted records in storage. They must not
                // reappear when tlbx resumes a conversation after a native undo.
                if (Text(info, "id") == revertedMessage && revertedMessage is not null) break;
                ProjectMessage(info, replay);
                var turn = TurnFor(Text(info, "id"));
                if (replay && replayTurns.Add(turn)) Publish("turn.started", e => e.TurnStarted = new() { IsReplay = true }, turn: turn, at: Timestamp(Value(info, "time", "created")));
                foreach (var part in Array(Value(record, "parts"))) ProjectPart(part);
            }
            foreach (var turn in replayTurns) Publish("turn.completed", e => e.TurnCompleted = new() { State = "completed", StateLabel = "Restored", StopReason = "history_import" }, turn: turn);
            PublishUsage();
        }
    }

    private void PublishUsage()
    {
        var assistants = _messages.Values.Where(m => Text(m, "role") == "assistant" && HasUsage(m)).ToArray();
        if (assistants.Length == 0) return;
        long Sum(string key) => assistants.Sum(m => Number(Value(m, "tokens", key)) ?? 0);
        long Cache(string key) => assistants.Sum(m => Number(Value(m, "tokens", "cache", key)) ?? 0);
        var last = assistants.OrderBy(m => Number(Value(m, "time", "created")) ?? 0).Last();
        var tokens = Value(last, "tokens");
        var context = Number(Value(tokens, "input")) + Number(Value(tokens, "cache", "read")) + Number(Value(tokens, "cache", "write")) + Number(Value(tokens, "output")) + Number(Value(tokens, "reasoning"));
        var modelKey = Text(last, "providerID") + "/" + Text(last, "modelID");
        var costs = assistants.Select(m => Value(m, "cost")).Where(v => v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var cost) && cost > 0).Select(v => v.GetDecimal()).ToArray();
        var usage = new AppServerControlUsageSummary
        {
            InputTokens = Sum("input"),
            OutputTokens = Sum("output"),
            ReasoningTokens = Sum("reasoning"),
            CachedReadTokens = Cache("read"),
            CachedWriteTokens = Cache("write"),
            ContextUsedTokens = context,
            ContextWindowTokens = _models.TryGetValue(modelKey, out var model) ? Number(Value(model, "limit", "context")) : null,
            CostAmount = costs.Length > 0 ? costs.Sum() : null,
            CostCurrency = costs.Length > 0 ? "USD" : null
        };
        Publish("agent.metadata.updated", e => e.AgentMetadataUpdated = new() { Usage = usage });
    }

    private static long? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && n >= 0 ? n : null;
    private static bool HasUsage(JsonElement message) =>
        (Number(Value(message, "tokens", "input")) ?? 0) > 0 ||
        (Number(Value(message, "tokens", "output")) ?? 0) > 0 ||
        (Number(Value(message, "tokens", "reasoning")) ?? 0) > 0 ||
        (Number(Value(message, "tokens", "cache", "read")) ?? 0) > 0 ||
        (Number(Value(message, "tokens", "cache", "write")) ?? 0) > 0;
    private static DateTimeOffset? Timestamp(JsonElement value)
    {
        var milliseconds = Number(value);
        return milliseconds is >= 0 and <= 253402300799999 ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds.Value) : null;
    }
}
