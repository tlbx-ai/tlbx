using System.Collections.Concurrent;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class AcpAppServerControlAgentRuntime
{
    private readonly Lock _userInputLock = new();
    private readonly ConcurrentDictionary<string, PendingUserInputRequest> _pendingUserInputs = new(StringComparer.Ordinal);

    private async Task HandleGrokUserInputRequestAsync(JsonElement root, string rawLine, CancellationToken ct)
    {
        if (!root.TryGetProperty("id", out var rpcId))
        {
            return;
        }

        var parameters = Traverse(root, "params");
        if (parameters is { ValueKind: JsonValueKind.Object } wrapped && GetString(wrapped, "method") is
            "x.ai/ask_user_question" or "_x.ai/ask_user_question")
        {
            parameters = Traverse(wrapped, "params");
        }

        var questions = ReadGrokQuestions(parameters);
        if (questions is null || questions.Count == 0 ||
            !string.Equals(GetString(parameters, "sessionId"), _providerThreadId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(GetString(parameters, "toolCallId")))
        {
            await WriteJsonRpcErrorAsync(rpcId.Clone(), -32602, "Invalid Grok question request.", ct).ConfigureAwait(false);
            return;
        }

        PendingUserInputRequest? pending = null;
        var requestId = "acp-question:" + Guid.NewGuid().ToString("N");
        lock (_userInputLock)
        {
            var turnId = _activeTurnId;
            if (!string.IsNullOrWhiteSpace(turnId) && !string.Equals(turnId, _cancellingTurnId, StringComparison.Ordinal))
            {
                pending = new PendingUserInputRequest(rpcId.Clone(), turnId, questions);
                _pendingUserInputs[requestId] = pending;
                _emit(CreateEvent("user-input.requested", turnId, null, requestId, "acp", GetString(root, "method"), root, providerEvent =>
                {
                    providerEvent.UserInputRequested = new AppServerControlProviderUserInputRequestedPayload
                    {
                        Questions = questions.Select(static question => question.Canonical).ToList()
                    };
                }, rawLine));
            }
        }

        if (pending is null)
        {
            await WriteGrokUserInputResponseAsync(rpcId.Clone(), null, ct).ConfigureAwait(false);
        }
    }

    private async Task<HostCommandOutcome> ResolveUserInputAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        var resolution = command.ResolveUserInput ?? throw new InvalidOperationException("user-input.resolve payload is required.");
        if (_pendingChoiceQuestions.ContainsKey(resolution.RequestId))
            return await ResolveAcpChoiceQuestionAsync(command, ct).ConfigureAwait(false);
        if (!_pendingUserInputs.TryGetValue(resolution.RequestId, out var pending) ||
            !string.Equals(pending.TurnId, _activeTurnId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{_agentName} ACP question request was not found: {resolution.RequestId}");
        }

        var answersById = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var answer in resolution.Answers)
        {
            if (!pending.Questions.Any(question => string.Equals(question.Canonical.Id, answer.QuestionId, StringComparison.Ordinal)) ||
                !answersById.TryAdd(answer.QuestionId, answer.Answers.Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value.Trim()).ToList()))
            {
                throw new InvalidOperationException("ACP answers contain an unknown or duplicate question ID.");
            }
        }

        var nativeAnswers = new List<GrokAnswer>();
        foreach (var question in pending.Questions)
        {
            if (!answersById.TryGetValue(question.Canonical.Id, out var values) || values.Count == 0)
            {
                throw new InvalidOperationException("ACP answers must include a response to each question.");
            }

            var labels = new List<string>();
            var notes = new List<string>();
            string? preview = null;
            foreach (var value in values)
            {
                if (question.Options.TryGetValue(value, out var optionPreview))
                {
                    labels.Add(value);
                    if (!question.Canonical.MultiSelect && !string.IsNullOrWhiteSpace(optionPreview))
                    {
                        preview ??= optionPreview;
                    }
                }
                else
                {
                    notes.Add(value);
                }
            }
            nativeAnswers.Add(new GrokAnswer(question.Canonical.Question,
                labels.Count > 0 ? labels : ["Other"], preview, notes.Count > 0 ? string.Join('\n', notes) : null));
        }

        await WriteGrokUserInputResponseAsync(pending.RpcId, nativeAnswers, ct).ConfigureAwait(false);
        _pendingUserInputs.TryRemove(resolution.RequestId, out _);
        return Accepted(command.CommandId, command.SessionId,
            accepted: new AppServerControlCommandAcceptedResponse
            {
                SessionId = command.SessionId, Status = "accepted", RequestId = resolution.RequestId, TurnId = pending.TurnId
            },
            events: [CreateEvent("user-input.resolved", pending.TurnId, null, resolution.RequestId, "acp", "x.ai/ask_user_question", resolution, providerEvent =>
            {
                providerEvent.UserInputResolved = new AppServerControlProviderUserInputResolvedPayload { Answers = resolution.Answers };
            })]);
    }

    private async Task CancelUserInputRequestsAsync(string turnId, CancellationToken ct)
    {
        await CancelAcpChoiceQuestionsAsync(turnId, ct).ConfigureAwait(false);
        List<KeyValuePair<string, PendingUserInputRequest>> cancelled = [];
        lock (_userInputLock)
        {
            foreach (var pair in _pendingUserInputs.Where(pair => string.Equals(pair.Value.TurnId, turnId, StringComparison.Ordinal)))
            {
                if (_pendingUserInputs.TryRemove(pair.Key, out _))
                {
                    cancelled.Add(pair);
                }
            }
        }
        foreach (var pair in cancelled)
        {
            await WriteGrokUserInputResponseAsync(pair.Value.RpcId, null, ct).ConfigureAwait(false);
            EmitCancelledRequest(pair.Key, pair.Value.TurnId, "interview");
        }
    }

    private void EmitCancelledRequest(string requestId, string turnId, string kind)
    {
        _emit(CreateEvent("request.resolved", turnId, null, requestId, "acp", "session/cancel", null, providerEvent =>
        {
            providerEvent.RequestResolved = new AppServerControlProviderRequestResolvedPayload { RequestType = kind, Decision = "cancel" };
        }));
    }

    private static List<GrokQuestion>? ReadGrokQuestions(JsonElement? parameters)
    {
        if (Traverse(parameters, "questions") is not { ValueKind: JsonValueKind.Array } rawQuestions)
        {
            return null;
        }
        var result = new List<GrokQuestion>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var texts = new HashSet<string>(StringComparer.Ordinal);
        using var questionEnumerator = rawQuestions.EnumerateArray();
        while (questionEnumerator.MoveNext())
        {
            var rawQuestion = questionEnumerator.Current;
            var text = GetString(rawQuestion, "question");
            var id = GetString(rawQuestion, "id") ?? text;
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(id) || !ids.Add(id) || !texts.Add(text) ||
                Traverse(rawQuestion, "options") is not { ValueKind: JsonValueKind.Array } rawOptions)
            {
                return null;
            }
            var canonical = new AppServerControlQuestion
            {
                Id = id, Question = text, AllowCustomAnswer = true,
                MultiSelect = Traverse(rawQuestion, "multiSelect") is { ValueKind: JsonValueKind.True }
            };
            var options = new Dictionary<string, string?>(StringComparer.Ordinal);
            using var optionEnumerator = rawOptions.EnumerateArray();
            while (optionEnumerator.MoveNext())
            {
                var rawOption = optionEnumerator.Current;
                var label = GetString(rawOption, "label");
                if (string.IsNullOrWhiteSpace(label) || !options.TryAdd(label, GetString(rawOption, "preview")))
                {
                    return null;
                }
                canonical.Options.Add(new AppServerControlQuestionOption
                {
                    Label = label, Description = GetString(rawOption, "description") ?? label
                });
            }
            result.Add(new GrokQuestion(canonical, options));
        }
        return result;
    }

    private Task WriteGrokUserInputResponseAsync(JsonElement rpcId, List<GrokAnswer>? answers, CancellationToken ct)
    {
        return WriteJsonLineAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            rpcId.WriteTo(writer);
            writer.WriteStartObject("result");
            writer.WriteString("outcome", answers is null ? "cancelled" : "accepted");
            if (answers is not null)
            {
                writer.WriteStartObject("answers");
                foreach (var answer in answers)
                {
                    writer.WriteStartArray(answer.Question);
                    foreach (var label in answer.Labels) writer.WriteStringValue(label);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
                var annotations = answers.Where(static answer => answer.Preview is not null || answer.Notes is not null).ToList();
                if (annotations.Count > 0)
                {
                    writer.WriteStartObject("annotations");
                    foreach (var answer in annotations)
                    {
                        writer.WriteStartObject(answer.Question);
                        if (answer.Preview is not null) writer.WriteString("preview", answer.Preview);
                        if (answer.Notes is not null) writer.WriteString("notes", answer.Notes);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, ct);
    }

    private sealed record GrokQuestion(AppServerControlQuestion Canonical, Dictionary<string, string?> Options);
    private sealed record GrokAnswer(string Question, List<string> Labels, string? Preview, string? Notes);
    private sealed record PendingUserInputRequest(JsonElement RpcId, string TurnId, List<GrokQuestion> Questions);
}
