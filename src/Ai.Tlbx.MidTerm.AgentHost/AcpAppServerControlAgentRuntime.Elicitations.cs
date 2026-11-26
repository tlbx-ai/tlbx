using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class AcpAppServerControlAgentRuntime
{
    private readonly ConcurrentDictionary<string, PendingElicitationRequest> _pendingElicitations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _activeUrlElicitations = new(StringComparer.Ordinal);

    private async Task HandleElicitationCreateAsync(JsonElement root, string rawLine, CancellationToken ct)
    {
        if (!root.TryGetProperty("id", out var rpcId))
        {
            return;
        }

        var parameters = Traverse(root, "params");
        if (parameters is not { ValueKind: JsonValueKind.Object } payload)
        {
            await WriteJsonRpcErrorAsync(rpcId.Clone(), -32602, "Invalid elicitation/create payload.", ct).ConfigureAwait(false);
            return;
        }

        var message = GetString(payload, "message") ?? $"{_agentName} requested user input.";
        var mode = GetString(payload, "mode") ?? "form";
        var sessionScopeId = GetString(payload, "sessionId");
        if (!string.IsNullOrWhiteSpace(sessionScopeId) &&
            !string.IsNullOrWhiteSpace(_providerThreadId) &&
            !string.Equals(sessionScopeId, _providerThreadId, StringComparison.Ordinal))
        {
            await WriteElicitationResponseAsync(rpcId.Clone(), "cancel", null, ct).ConfigureAwait(false);
            return;
        }

        var requestId = "acp-elicitation:" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var turnId = _activeTurnId ?? $"acp-background:{requestId}";
        var questions = new List<AppServerControlQuestion>();
        var fields = new Dictionary<string, ElicitationFieldMeta>(StringComparer.Ordinal);
        string? elicitationId = null;

        if (string.Equals(mode, "form", StringComparison.Ordinal))
        {
            var requestedSchema = Traverse(payload, "requestedSchema");
            var requiredSet = new HashSet<string>(StringComparer.Ordinal);
            if (Traverse(requestedSchema, "required") is { ValueKind: JsonValueKind.Array } reqArray)
            {
                using var reqEnum = reqArray.EnumerateArray();
                while (reqEnum.MoveNext())
                {
                    if (reqEnum.Current.ValueKind == JsonValueKind.String && reqEnum.Current.GetString() is { Length: > 0 } reqName)
                    {
                        requiredSet.Add(reqName);
                    }
                }
            }

            if (Traverse(requestedSchema, "properties") is { ValueKind: JsonValueKind.Object } props)
            {
                using var propEnum = props.EnumerateObject();
                while (propEnum.MoveNext())
                {
                    var prop = propEnum.Current;
                    var schema = prop.Value;
                    var schemaType = GetString(schema, "type") ?? "string";
                    var title = GetString(schema, "title") ?? prop.Name;
                    var description = GetString(schema, "description");
                    var format = GetString(schema, "format");
                    var isRequired = requiredSet.Contains(prop.Name);
                    var options = ReadElicitationOptions(schema);
                    var multiSelect = string.Equals(schemaType, "array", StringComparison.Ordinal);

                    var question = new AppServerControlQuestion
                    {
                        Id = prop.Name,
                        Header = title,
                        Question = string.IsNullOrWhiteSpace(description) ? message : $"{title}: {description}",
                        MultiSelect = multiSelect,
                        AllowCustomAnswer = options.Count == 0,
                        IsSecret = string.Equals(format, "password", StringComparison.OrdinalIgnoreCase),
                        Options = options
                    };
                    questions.Add(question);
                    fields[prop.Name] = new ElicitationFieldMeta(prop.Name, schemaType, isRequired);
                }
            }

            if (questions.Count == 0)
            {
                questions.Add(new AppServerControlQuestion
                {
                    Id = "response",
                    Header = "Input",
                    Question = message,
                    AllowCustomAnswer = true
                });
                fields["response"] = new ElicitationFieldMeta("response", "string", Required: false);
            }
        }
        else if (string.Equals(mode, "url", StringComparison.Ordinal))
        {
            elicitationId = GetString(payload, "elicitationId");
            var url = GetString(payload, "url") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(elicitationId))
            {
                _activeUrlElicitations[elicitationId] = requestId;
            }

            questions.Add(new AppServerControlQuestion
            {
                Id = "url_action",
                Header = "External Action Required",
                Question = string.IsNullOrWhiteSpace(url) ? message : $"{message}\n\nURL: {url}",
                AllowCustomAnswer = false,
                Options =
                [
                    new AppServerControlQuestionOption { Value = "accept", Label = "Accept / Open URL", Description = url },
                    new AppServerControlQuestionOption { Value = "decline", Label = "Decline", Description = "Decline this external action." },
                    new AppServerControlQuestionOption { Value = "cancel", Label = "Cancel", Description = "Cancel this request." }
                ]
            });
        }
        else
        {
            // Custom or future elicitation mode: preserve raw payload and expose explicit decline/cancel.
            questions.Add(new AppServerControlQuestion
            {
                Id = "other_action",
                Header = $"Elicitation ({mode})",
                Question = $"{message}\n\n{payload.GetRawText()}",
                AllowCustomAnswer = false,
                Options =
                [
                    new AppServerControlQuestionOption { Value = "decline", Label = "Decline", Description = "Decline unsupported elicitation mode." },
                    new AppServerControlQuestionOption { Value = "cancel", Label = "Cancel", Description = "Cancel this request." }
                ]
            });
        }

        var pending = new PendingElicitationRequest(rpcId.Clone(), turnId, mode, elicitationId, fields, questions);
        _pendingElicitations[requestId] = pending;
        _emit(CreateEvent("user-input.requested", turnId, null, requestId, "acp", "elicitation/create", root,
            e => e.UserInputRequested = new AppServerControlProviderUserInputRequestedPayload { Questions = questions }, rawLine));
    }

    private void HandleElicitationComplete(JsonElement root, string rawLine)
    {
        var elicitationId = GetString(root, "params", "elicitationId");
        if (string.IsNullOrWhiteSpace(elicitationId))
        {
            return;
        }

        _activeUrlElicitations.TryRemove(elicitationId, out _);
        EmitRuntimeMessage(
            "runtime.notice",
            $"{_agentName} completed external elicitation '{elicitationId}'.",
            rawLine,
            "acp",
            "elicitation/complete",
            root,
            rawLine,
            timelineVisible: false);
    }

    private async Task<HostCommandOutcome> ResolveElicitationAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        var resolution = command.ResolveUserInput!;
        if (!_pendingElicitations.TryGetValue(resolution.RequestId, out var pending))
        {
            throw new InvalidOperationException($"{_agentName} ACP elicitation request was not found: {resolution.RequestId}");
        }

        var answersById = resolution.Answers.ToDictionary(
            static a => a.QuestionId,
            static a => a.Answers.Where(static v => v is not null).ToList(),
            StringComparer.Ordinal);

        string action = "accept";
        Dictionary<string, JsonElement>? content = null;

        if (string.Equals(pending.Mode, "url", StringComparison.Ordinal) ||
            !string.Equals(pending.Mode, "form", StringComparison.Ordinal))
        {
            var key = string.Equals(pending.Mode, "url", StringComparison.Ordinal) ? "url_action" : "other_action";
            var selected = answersById.TryGetValue(key, out var values) && values.Count > 0 ? values[0].Trim().ToLowerInvariant() : "cancel";
            action = selected is "accept" or "decline" or "cancel" ? selected : "cancel";
        }
        else
        {
            if (answersById.TryGetValue("__action", out var explicitAction) && explicitAction.Count > 0)
            {
                var requested = explicitAction[0].Trim().ToLowerInvariant();
                if (requested is "decline" or "cancel")
                {
                    action = requested;
                }
            }

            if (action == "accept")
            {
                content = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var (fieldName, meta) in pending.Fields)
                {
                    if (!answersById.TryGetValue(fieldName, out var values) || values.Count == 0)
                    {
                        if (meta.Required)
                        {
                            throw new InvalidOperationException($"ACP elicitation field '{fieldName}' is required.");
                        }
                        continue;
                    }

                    content[fieldName] = ConvertElicitationFieldValue(meta.SchemaType, values);
                }
            }
        }

        await WriteElicitationResponseAsync(pending.RpcId, action, content, ct).ConfigureAwait(false);
        _pendingElicitations.TryRemove(resolution.RequestId, out _);
        return Accepted(command.CommandId, command.SessionId,
            new AppServerControlCommandAcceptedResponse
            {
                SessionId = command.SessionId,
                Status = "accepted",
                RequestId = resolution.RequestId,
                TurnId = pending.TurnId
            },
            [CreateEvent("user-input.resolved", pending.TurnId, null, resolution.RequestId, "acp", "elicitation/create", resolution,
                e => e.UserInputResolved = new AppServerControlProviderUserInputResolvedPayload { Answers = resolution.Answers })]);
    }

    private async Task CancelElicitationsAsync(string turnId, CancellationToken ct)
    {
        foreach (var pair in _pendingElicitations.Where(p => string.Equals(p.Value.TurnId, turnId, StringComparison.Ordinal)).ToArray())
        {
            if (!_pendingElicitations.TryRemove(pair.Key, out var pending))
            {
                continue;
            }

            await WriteElicitationResponseAsync(pending.RpcId, "cancel", null, ct).ConfigureAwait(false);
            EmitCancelledRequest(pair.Key, pending.TurnId, "interview");
        }
    }

    private bool TryCancelElicitationByRpcId(string rpcIdKey)
    {
        foreach (var pair in _pendingElicitations.ToArray())
        {
            if (string.Equals(pair.Value.RpcId.ToString(), rpcIdKey, StringComparison.Ordinal) &&
                _pendingElicitations.TryRemove(pair.Key, out var pending))
            {
                EmitCancelledRequest(pair.Key, pending.TurnId, "interview");
                return true;
            }
        }

        return false;
    }

    private Task WriteElicitationResponseAsync(
        JsonElement rpcId,
        string action,
        IReadOnlyDictionary<string, JsonElement>? content,
        CancellationToken ct)
    {
        return WriteJsonLineAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            rpcId.WriteTo(writer);
            writer.WriteStartObject("result");
            writer.WriteString("action", action);
            if (action == "accept" && content is not null)
            {
                writer.WriteStartObject("content");
                foreach (var (key, value) in content)
                {
                    writer.WritePropertyName(key);
                    value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, ct);
    }

    private static List<AppServerControlQuestionOption> ReadElicitationOptions(JsonElement schema)
    {
        var result = new List<AppServerControlQuestionOption>();
        if (schema.TryGetProperty("oneOf", out var oneOf) && oneOf.ValueKind == JsonValueKind.Array)
        {
            using var enumerator = oneOf.EnumerateArray();
            while (enumerator.MoveNext())
            {
                var item = enumerator.Current;
                var val = GetString(item, "const");
                if (val is null) continue;
                result.Add(new AppServerControlQuestionOption
                {
                    Value = val,
                    Label = GetString(item, "title") ?? val,
                    Description = GetString(item, "description") ?? string.Empty
                });
            }
            return result;
        }

        if (schema.TryGetProperty("enum", out var enumArray) && enumArray.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            if (schema.TryGetProperty("enumNames", out var enumNames) && enumNames.ValueKind == JsonValueKind.Array)
            {
                using var nameEnum = enumNames.EnumerateArray();
                while (nameEnum.MoveNext())
                {
                    names.Add(nameEnum.Current.ValueKind == JsonValueKind.String ? nameEnum.Current.GetString() ?? string.Empty : string.Empty);
                }
            }

            var index = 0;
            using var valEnum = enumArray.EnumerateArray();
            while (valEnum.MoveNext())
            {
                var val = valEnum.Current.ValueKind == JsonValueKind.String ? valEnum.Current.GetString() : valEnum.Current.GetRawText();
                if (val is null) { index++; continue; }
                var label = index < names.Count && !string.IsNullOrWhiteSpace(names[index]) ? names[index] : val;
                result.Add(new AppServerControlQuestionOption { Value = val, Label = label, Description = string.Empty });
                index++;
            }
            return result;
        }

        if (string.Equals(GetString(schema, "type"), "boolean", StringComparison.Ordinal))
        {
            result.Add(new AppServerControlQuestionOption { Value = "true", Label = "Yes", Description = string.Empty });
            result.Add(new AppServerControlQuestionOption { Value = "false", Label = "No", Description = string.Empty });
        }

        return result;
    }

    private static JsonElement ConvertElicitationFieldValue(string schemaType, IReadOnlyList<string> values)
    {
        if (string.Equals(schemaType, "array", StringComparison.Ordinal))
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray();
                foreach (var v in values) writer.WriteStringValue(v);
                writer.WriteEndArray();
            }
            using var doc = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
            return doc.RootElement.Clone();
        }

        var raw = values[0];
        if (string.Equals(schemaType, "boolean", StringComparison.Ordinal))
        {
            var flag = raw.Trim().ToLowerInvariant() is "true" or "yes" or "1";
            return JsonElement.Parse(flag ? "true" : "false");
        }

        if (string.Equals(schemaType, "integer", StringComparison.Ordinal) &&
            long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var intVal))
        {
            return JsonElement.Parse(intVal.ToString(CultureInfo.InvariantCulture));
        }

        if (string.Equals(schemaType, "number", StringComparison.Ordinal) &&
            double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var numVal) &&
            double.IsFinite(numVal))
        {
            return JsonElement.Parse(numVal.ToString(CultureInfo.InvariantCulture));
        }

        return JsonSerializer.SerializeToElement(raw, AppServerControlHostJsonContext.Default.String);
    }

    private sealed record ElicitationFieldMeta(string Name, string SchemaType, bool Required);

    private sealed record PendingElicitationRequest(
        JsonElement RpcId,
        string TurnId,
        string Mode,
        string? ElicitationId,
        Dictionary<string, ElicitationFieldMeta> Fields,
        List<AppServerControlQuestion> Questions);
}
