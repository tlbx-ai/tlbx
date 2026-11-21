using System.Collections.Concurrent;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class AcpAppServerControlAgentRuntime
{
    private readonly ConcurrentDictionary<string, PendingChoiceQuestion> _pendingChoiceQuestions = new(StringComparer.Ordinal);

    private async Task HandleAcpChoiceQuestionAsync(JsonElement root, JsonElement rpcId, string turnId,
        IReadOnlyList<AppServerControlPermissionOption> options, string rawLine, CancellationToken ct)
    {
        if (options.Count == 0 || options.Any(option => string.IsNullOrWhiteSpace(option.OptionId)) ||
            options.Select(option => option.OptionId).Distinct(StringComparer.Ordinal).Count() != options.Count)
        {
            await WriteJsonRpcErrorAsync(rpcId, -32602, "Invalid choice question options.", ct).ConfigureAwait(false);
            return;
        }
        var question = new AppServerControlQuestion
        {
            Id = GetString(root, "params", "toolCall", "toolCallId")!,
            Header = "Question",
            Question = GetString(root, "params", "toolCall", "title") ?? "Choose an option.",
            Options = options.Select(option => new AppServerControlQuestionOption
            {
                Value = option.OptionId,
                Label = option.Name ?? option.OptionId,
                Description = string.Empty
            }).ToList()
        };
        var requestId = $"acp-question:{Guid.NewGuid():N}";
        lock (_userInputLock)
        {
            if (_activeTurnId == turnId && _cancellingTurnId != turnId)
            {
                _pendingChoiceQuestions[requestId] = new(rpcId.Clone(), turnId, question);
                _emit(CreateEvent("user-input.requested", turnId, null, requestId, "acp", "session/request_permission", root,
                    e => e.UserInputRequested = new AppServerControlProviderUserInputRequestedPayload { Questions = [question] }, rawLine));
                return;
            }
        }
        await WritePermissionResponseAsync(rpcId, null, cancelled: true, ct).ConfigureAwait(false);
    }

    private async Task<HostCommandOutcome> ResolveAcpChoiceQuestionAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        var resolution = command.ResolveUserInput!;
        if (!_pendingChoiceQuestions.TryGetValue(resolution.RequestId, out var pending) || pending.TurnId != _activeTurnId)
            throw new InvalidOperationException("The native choice question is no longer active.");
        if (resolution.Answers.Count != 1 || resolution.Answers[0].QuestionId != pending.Question.Id ||
            resolution.Answers[0].Answers.Count != 1 ||
            !pending.Question.Options.Any(option => option.Value == resolution.Answers[0].Answers[0]))
            throw new InvalidOperationException("Select exactly one of the native question options.");
        await WritePermissionResponseAsync(pending.RpcId, resolution.Answers[0].Answers[0], cancelled: false, ct).ConfigureAwait(false);
        _pendingChoiceQuestions.TryRemove(resolution.RequestId, out _);
        return Accepted(command.CommandId, command.SessionId,
            new AppServerControlCommandAcceptedResponse
            {
                SessionId = command.SessionId,
                Status = "accepted",
                RequestId = resolution.RequestId,
                TurnId = pending.TurnId
            },
            [CreateEvent("user-input.resolved", pending.TurnId, null, resolution.RequestId, "acp", "session/request_permission", resolution,
                e => e.UserInputResolved = new AppServerControlProviderUserInputResolvedPayload { Answers = resolution.Answers })]);
    }

    private async Task CancelAcpChoiceQuestionsAsync(string turnId, CancellationToken ct)
    {
        foreach (var pair in _pendingChoiceQuestions.Where(pair => pair.Value.TurnId == turnId))
        {
            if (!_pendingChoiceQuestions.TryRemove(pair.Key, out var pending)) continue;
            await WritePermissionResponseAsync(pending.RpcId, null, cancelled: true, ct).ConfigureAwait(false);
            EmitCancelledRequest(pair.Key, turnId, "interview");
        }
    }

    private sealed record PendingChoiceQuestion(JsonElement RpcId, string TurnId, AppServerControlQuestion Question);
}
