using System.Globalization;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class OpenCodeAppServerControlAgentRuntime
{
    private void OpenRequest(JsonElement native, bool question)
    {
        var nativeSession = Text(native, "sessionID");
        if (nativeSession is null || !_nativeSessions.Contains(nativeSession)) return;
        var id = Text(native, "id");
        if (string.IsNullOrEmpty(id) || _requests.ContainsKey(id)) return;
        var questions = new List<AppServerControlQuestion>();
        if (question)
        {
            foreach (var item in Array(Value(native, "questions")))
            {
                questions.Add(new()
                {
                    Id = "question-" + questions.Count.ToString(CultureInfo.InvariantCulture),
                    Header = Text(item, "header") ?? string.Empty,
                    Question = Text(item, "question") ?? string.Empty,
                    MultiSelect = Value(item, "multiple").ValueKind == JsonValueKind.True,
                    AllowCustomAnswer = Value(item, "custom").ValueKind != JsonValueKind.False,
                    Options = Array(Value(item, "options")).Select(option => new AppServerControlQuestionOption { Label = Text(option, "label") ?? string.Empty, Value = Text(option, "label"), Description = Text(option, "description") ?? string.Empty }).ToList()
                });
            }
            if (questions.Count == 0) throw new InvalidOperationException("OpenCode sent a question request without questions.");
        }
        var toolMessage = Text(Value(native, "tool"), "messageID");
        var turn = nativeSession != _threadId ? _childTurns.GetValueOrDefault(nativeSession) ?? _turnId : toolMessage is null ? _turnId : TurnFor(toolMessage);
        _requests[id] = new(id, turn, question, questions);
        if (question)
            Publish("user-input.requested", e => e.UserInputRequested = new() { Questions = questions }, request: id, turn: turn);
        else
            Publish("request.opened", e => e.RequestOpened = new() { RequestType = "command_execution_approval", RequestTypeLabel = Text(native, "permission") ?? "Permission", Detail = string.Join("\n", Array(Value(native, "patterns")).Select(p => p.GetString())) }, request: id, turn: turn);
        PublishSession("waiting", question ? "Waiting for input" : "Waiting for approval");
    }

    private async Task<HostCommandOutcome> ResolveAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        var id = command.ResolveUserInput?.RequestId ?? command.ResolveRequest?.RequestId ?? throw new InvalidOperationException("A request ID is required.");
        PendingOpenCodeRequest pending;
        lock (_state)
        {
            if (!_requests.TryGetValue(id, out pending!)) throw new InvalidOperationException("The OpenCode request is no longer pending.");
        }
        var cancelled = command.ResolveRequest?.Decision is "decline" or "reject" or "cancel";
        if (pending.Question)
        {
            if (cancelled)
            {
                await SendAsync(HttpMethod.Post, "/question/" + Uri.EscapeDataString(id) + "/reject", null, ct).ConfigureAwait(false);
                lock (_state) CloseRequest(id, "cancelled");
            }
            else
            {
                var supplied = command.ResolveUserInput?.Answers ?? throw new InvalidOperationException("Answer the OpenCode questions explicitly.");
                if (supplied.Count != pending.Questions.Count || supplied.Select(a => a.QuestionId).Distinct(StringComparer.Ordinal).Count() != supplied.Count)
                    throw new InvalidOperationException("Answer each OpenCode question exactly once.");
                var ordered = new List<List<string>>();
                foreach (var question in pending.Questions)
                {
                    var answer = supplied.SingleOrDefault(a => a.QuestionId == question.Id) ?? throw new InvalidOperationException("Unknown OpenCode question ID.");
                    if (answer.Answers.Count == 0 || (!question.MultiSelect && answer.Answers.Count != 1) || answer.Answers.Any(string.IsNullOrWhiteSpace) || answer.Answers.Distinct(StringComparer.Ordinal).Count() != answer.Answers.Count ||
                        (!question.AllowCustomAnswer && answer.Answers.Any(a => !question.Options.Any(o => o.Label == a))))
                        throw new InvalidOperationException("The answer does not match the native OpenCode question choices.");
                    ordered.Add(answer.Answers);
                }
                await SendAsync(HttpMethod.Post, "/question/" + Uri.EscapeDataString(id) + "/reply", w =>
                {
                    w.WriteStartObject(); w.WriteStartArray("answers");
                    foreach (var answers in ordered) { w.WriteStartArray(); foreach (var answer in answers) w.WriteStringValue(answer); w.WriteEndArray(); }
                    w.WriteEndArray(); w.WriteEndObject();
                }, ct).ConfigureAwait(false);
                lock (_state)
                {
                    // Keep native ordering, including multiple choices and free text.
                    using var buffer = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(buffer))
                    {
                        writer.WriteStartArray(); foreach (var answers in ordered) { writer.WriteStartArray(); foreach (var answer in answers) writer.WriteStringValue(answer); writer.WriteEndArray(); }
                        writer.WriteEndArray();
                    }
                    using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
                    CloseRequest(id, "answered", document.RootElement);
                }
            }
        }
        else
        {
            var decision = command.ResolveRequest?.Decision;
            var reply = decision is "accept" or "approve" or "allow" ? "once" : cancelled ? "reject" : throw new InvalidOperationException("Unknown OpenCode permission decision.");
            await SendAsync(HttpMethod.Post, "/permission/" + Uri.EscapeDataString(id) + "/reply", w => { w.WriteStartObject(); w.WriteString("reply", reply); w.WriteEndObject(); }, ct).ConfigureAwait(false);
            lock (_state) CloseRequest(id, reply);
        }
        return Accepted(command);
    }

    private void CloseRequest(string? id, string decision, JsonElement answers = default)
    {
        if (id is null || !_requests.Remove(id, out var pending)) return;
        if (pending.Question)
        {
            var native = Array(answers);
            Publish("user-input.resolved", e => e.UserInputResolved = new()
            {
                Answers = pending.Questions.Select((q, index) => new AppServerControlAnsweredQuestion { QuestionId = q.Id, Answers = index < native.Length ? Array(native[index]).Select(a => a.GetString() ?? string.Empty).ToList() : [] }).ToList()
            }, request: id, turn: pending.Turn);
        }
        Publish("request.resolved", e => e.RequestResolved = new() { RequestType = pending.Question ? "user_input" : "command_execution_approval", Decision = decision }, request: id, turn: pending.Turn);
        if (_requests.Count == 0) PublishSession(_turnId is null ? "ready" : "running", _turnId is null ? "Ready" : "Running");
    }
}
