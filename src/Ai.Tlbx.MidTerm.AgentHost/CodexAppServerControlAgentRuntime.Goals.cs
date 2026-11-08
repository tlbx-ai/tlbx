using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class CodexAppServerControlAgentRuntime
{
    private long _goalNotificationRevision;
    private AppServerControlGoalState? _latestCodexGoal;

    private async Task ReadInitialCodexGoalAsync(CancellationToken ct)
    {
        var threadId = _providerThreadId!;
        var revision = Interlocked.Read(ref _goalNotificationRevision);
        try
        {
            var result = await SendCodexRequestAsync("thread/goal/get",
                id => BuildCodexGoalRequest(id, threadId, new() { Operation = "get" }), ct).ConfigureAwait(false);
            if (!result.TryGetProperty("goal", out var goal)) throw new InvalidOperationException("Native goal response omitted goal.");
            if (!string.Equals(threadId, _providerThreadId, StringComparison.Ordinal) ||
                revision != Interlocked.Read(ref _goalNotificationRevision)) return;
            PublishCodexGoal(goal.ValueKind == JsonValueKind.Null ? null : goal, "thread/goal/get", result);
        }
        catch (Exception error) when (error is not OperationCanceledException ||
            (!ct.IsCancellationRequested && !_shutdown.IsCancellationRequested))
        {
            if (!string.Equals(threadId, _providerThreadId, StringComparison.Ordinal) ||
                revision != Interlocked.Read(ref _goalNotificationRevision)) return;
            var state = _latestCodexGoal?.Clone() ?? new AppServerControlGoalState { ThreadId = threadId };
            state.Confirmation = "unconfirmed";
            state.Detail = error is OperationCanceledException ? "timeout" : error.Message;
            _emit(CreateEvent("thread.goal.changed", null, null, null, "codex.app-server", "thread/goal/get", default,
                e => e.GoalUpdated = state));
        }
    }

    private async Task RefreshCodexGoalAfterTurnAsync(string threadId)
    {
        try
        {
            // Never await a response inside the notification reader itself. Serialize
            // this local read with user controls so an old get cannot undo a new set.
            await _gate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                if (string.Equals(threadId, _providerThreadId, StringComparison.Ordinal))
                    await ReadInitialCodexGoalAsync(_shutdown.Token).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task<HostCommandOutcome> ControlGoalAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        var request = command.SetGoal ?? throw new InvalidOperationException("Goal request is required.");
        request.Operation = command.Type["thread.goal.".Length..];
        AppServerControlGoals.Validate(request);
        var response = new AppServerControlGoalResponse
        {
            SessionId = command.SessionId, ThreadId = _providerThreadId!, TurnId = request.ExpectedTurnId
        };
        if (!AppServerControlGoals.Matches(request, _providerThreadId, _activeTurnId))
        {
            response.Status = "targetUnavailable";
            return GoalOutcome(command, response);
        }
        var revision = Interlocked.Read(ref _goalNotificationRevision);
        var operation = "thread/goal/" + request.Operation;
        var result = await SendCodexRequestAsync(operation,
            id => BuildCodexGoalRequest(id, _providerThreadId!, request), ct).ConfigureAwait(false);
        if (request.Operation == "clear")
        {
            // cleared:false is not proof of no goal; perform the native readback too.
            result = await SendCodexRequestAsync("thread/goal/get",
                id => BuildCodexGoalRequest(id, _providerThreadId!, new() { Operation = "get" }), ct).ConfigureAwait(false);
        }
        var goal = result.TryGetProperty("goal", out var value) && value.ValueKind != JsonValueKind.Null ? value : (JsonElement?)null;
        if (!result.TryGetProperty("goal", out _)) throw new InvalidOperationException("Native goal response omitted goal.");
        response.State = revision == Interlocked.Read(ref _goalNotificationRevision)
            ? PublishCodexGoal(goal, operation, result) : _latestCodexGoal?.Clone();
        var clearUnconfirmed = request.Operation == "clear" && response.State?.Goal is not null;
        response.Status = clearUnconfirmed ? "unconfirmed" : "applied";
        if (clearUnconfirmed) response.State!.Detail = "clearReadbackMismatch";
        return GoalOutcome(command, response);
    }

    private AppServerControlGoalState PublishCodexGoal(JsonElement? value, string method, JsonElement payload, bool notification = false)
    {
        var state = new AppServerControlGoalState { ThreadId = _providerThreadId!, Confirmation = "confirmed" };
        if (value is { } goal)
        {
            if (!string.Equals(GetString(goal, "threadId"), _providerThreadId, StringComparison.Ordinal))
                throw new InvalidOperationException("Native goal belongs to a different thread.");
            state.Goal = new()
            {
                Objective = GetString(goal, "objective") ?? throw new InvalidOperationException("Native goal omitted objective."),
                Status = GetString(goal, "status") ?? throw new InvalidOperationException("Native goal omitted status."),
                TokenBudget = GoalNumber(goal, "tokenBudget"), TokensUsed = GoalNumber(goal, "tokensUsed"),
                TimeUsedSeconds = GoalNumber(goal, "timeUsedSeconds"), CreatedAt = GoalNumber(goal, "createdAt"),
                UpdatedAt = GoalNumber(goal, "updatedAt")
            };
        }
        _latestCodexGoal = state.Clone();
        if (notification) Interlocked.Increment(ref _goalNotificationRevision);
        _emit(CreateEvent("thread.goal.changed", null, null, null, "codex.app-server", method, payload,
            e => e.GoalUpdated = state));
        return state;
    }

    private static long? GoalNumber(JsonElement goal, string key) =>
        goal.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static string BuildCodexGoalRequest(string id, string threadId, AppServerControlGoalSetRequest request) =>
        BuildJsonString(writer =>
        {
            writer.WriteStartObject(); writer.WriteString("jsonrpc", "2.0"); writer.WriteString("id", id);
            writer.WriteString("method", "thread/goal/" + request.Operation); writer.WriteStartObject("params");
            writer.WriteString("threadId", threadId);
            if (request.Operation != "get") writer.WriteString("origin", "user");
            if (request.Operation == "set")
            {
                if (request.Objective is not null) writer.WriteString("objective", request.Objective);
                if (request.Status is not null) writer.WriteString("status", request.Status);
                if (request.TokenBudget.ValueKind != JsonValueKind.Undefined) { writer.WritePropertyName("tokenBudget"); request.TokenBudget.WriteTo(writer); }
            }
            writer.WriteEndObject(); writer.WriteEndObject();
        });

    private static HostCommandOutcome GoalOutcome(AppServerControlHostCommandEnvelope command, AppServerControlGoalResponse response) =>
        new() { Result = new() { CommandId = command.CommandId, SessionId = command.SessionId, Status = "accepted",
            Accepted = new() { SessionId = command.SessionId, Status = "accepted", Goal = response } } };
}
