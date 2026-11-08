using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class ClaudeAppServerControlAgentRuntime
{
    private async Task<HostCommandOutcome> ResolvePermissionRequestAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        var request = command.ResolveRequest ?? throw new InvalidOperationException("request.resolve payload is required.");
        var threadId = _providerThreadId;
        var turnId = _activeTurnId;
        var receipt = await WriteBridgeResolutionCommandAsync("permission", request.RequestId, writer =>
        {
            writer.WriteStartObject(); writer.WriteString("type", "permission.resolve");
            writer.WriteString("requestId", request.RequestId); writer.WriteString("decision", request.Decision);
            writer.WriteString("expectedThreadId", threadId); writer.WriteString("expectedTurnId", turnId);
            writer.WriteEndObject();
        }, ct).ConfigureAwait(false);
        var status = ValidateResolutionReceipt(receipt, threadId, turnId);
        var decision = GetString(receipt, "decision");
        if (decision is not ("accept" or "decline" or "cancel"))
            throw new InvalidOperationException("Claude permission decision was not confirmed.");
        var events = new List<AppServerControlProviderEvent>();
        if (status == "applied")
            events.Add(CreateEvent("request.resolved", turnId, null, request.RequestId, "claude.agent-sdk", "canUseTool/decision", receipt,
                e => e.RequestResolved = new() { RequestType = "tool_approval", Decision = decision }));
        return Accepted(command.CommandId, command.SessionId, new()
        {
            SessionId = command.SessionId, Status = status == "applied" ? "accepted" : status, RequestId = request.RequestId, TurnId = turnId, Decision = decision
        }, events);
    }

    private async Task<HostCommandOutcome> ResolveUserInputAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        var request = command.ResolveUserInput ?? throw new InvalidOperationException("user-input.resolve payload is required.");
        var threadId = _providerThreadId;
        var turnId = _activeTurnId;
        var receipt = await WriteBridgeResolutionCommandAsync("user_input", request.RequestId, writer =>
        {
            writer.WriteStartObject(); writer.WriteString("type", "user_input.resolve"); writer.WriteString("requestId", request.RequestId);
            writer.WriteString("expectedThreadId", threadId); writer.WriteString("expectedTurnId", turnId);
            writer.WriteStartObject("answers");
            foreach (var answer in request.Answers)
            {
                writer.WriteStartArray(answer.QuestionId);
                foreach (var value in answer.Answers) writer.WriteStringValue(value);
                writer.WriteEndArray();
            }
            writer.WriteEndObject(); writer.WriteEndObject();
        }, ct).ConfigureAwait(false);
        var status = ValidateResolutionReceipt(receipt, threadId, turnId);
        var decision = GetString(receipt, "decision");
        if (decision is not ("answered" or "cancel"))
            throw new InvalidOperationException("Claude user input was not confirmed.");
        List<AppServerControlAnsweredQuestion> answers = decision == "answered" ? ReadResolutionAnswers(receipt) : [];
        var events = new List<AppServerControlProviderEvent>();
        if (status == "applied")
            events.Add(CreateEvent("user-input.resolved", turnId, null, request.RequestId, "claude.agent-sdk", "AskUserQuestion/resolved", receipt,
                e =>
                {
                    if (decision == "cancel") e.RequestResolved = new() { RequestType = "interview", Decision = "cancel" };
                    else e.UserInputResolved = new() { Answers = answers };
                }));
        return Accepted(command.CommandId, command.SessionId, new()
        {
            SessionId = command.SessionId, Status = status == "applied" ? "accepted" : status, RequestId = request.RequestId, TurnId = turnId,
            Decision = decision, Answers = decision == "answered" ? answers : null
        }, events);
    }

    private string ValidateResolutionReceipt(JsonElement receipt, string? threadId, string? turnId)
    {
        var status = GetString(receipt, "status");
        if (status is not ("applied" or "alreadySettled") || string.IsNullOrEmpty(threadId) || string.IsNullOrEmpty(turnId) ||
            !string.Equals(_providerThreadId, threadId, StringComparison.Ordinal) ||
            !string.Equals(GetString(receipt, "session_id"), threadId, StringComparison.Ordinal) ||
            !string.Equals(GetString(receipt, "turnId"), turnId, StringComparison.Ordinal))
            throw new InvalidOperationException("Claude request settlement is unconfirmed or its scope is no longer available.");
        return status;
    }

    private static List<AppServerControlAnsweredQuestion> ReadResolutionAnswers(JsonElement receipt)
    {
        if (!receipt.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Claude did not return the actual answers.");
        var result = new List<AppServerControlAnsweredQuestion>();
        foreach (var answer in answers.EnumerateObject())
        {
            if (answer.Value.ValueKind != JsonValueKind.Array || answer.Value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
                throw new InvalidOperationException("Claude returned invalid answers.");
            result.Add(new() { QuestionId = answer.Name, Answers = answer.Value.EnumerateArray().Select(v => v.GetString()!).ToList() });
        }
        return result;
    }
}
