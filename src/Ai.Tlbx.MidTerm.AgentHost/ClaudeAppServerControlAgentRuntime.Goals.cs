using System.Collections.Concurrent;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class ClaudeAppServerControlAgentRuntime
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _goalReceipts = new(StringComparer.Ordinal);
    private AppServerControlGoalState? _goalState;
    private string? _goalStopTurnId;

    private async Task<HostCommandOutcome> ControlGoalAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        var request = command.SetGoal ?? throw new InvalidOperationException("Goal request is required.");
        request.Operation = command.Type["thread.goal.".Length..];
        AppServerControlGoals.Validate(request);
        var response = new AppServerControlGoalResponse
        {
            SessionId = command.SessionId, ThreadId = _providerThreadId ?? string.Empty, TurnId = request.ExpectedTurnId
        };
        if (!AppServerControlGoals.Matches(request, _providerThreadId, _activeTurnId))
        {
            response.Status = "targetUnavailable";
            return ClaudeGoalOutcome(command, response);
        }
        if (request.Status is not null || request.TokenBudget.ValueKind != JsonValueKind.Undefined)
            throw new InvalidOperationException("The verified Claude /goal command accepts a condition, not status or token-budget overrides.");
        if (request.Operation == "clear" && _activeTurnId is not null && !request.InterruptActiveTurn)
            throw new InvalidOperationException("Stopping an active Claude goal requires the explicit interrupt-and-clear action.");
        var receipt = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_goalReceipts.TryAdd(command.CommandId, receipt)) throw new InvalidOperationException("Goal command is already pending.");
        try
        {
            await WriteBridgeCommandAsync(writer =>
            {
                writer.WriteStartObject(); writer.WriteString("type", "thread.goal");
                writer.WriteString("requestId", command.CommandId); writer.WriteString("operation", request.Operation);
                writer.WriteString("expectedThreadId", request.ExpectedThreadId);
                writer.WriteString("expectedTurnId", request.ExpectedTurnId);
                if (request.Objective is not null) writer.WriteString("objective", request.Objective);
                writer.WriteBoolean("interruptActiveTurn", request.InterruptActiveTurn); writer.WriteEndObject();
            }, ct).ConfigureAwait(false);
            var ack = await receipt.Task.WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            response.Status = GetString(ack, "status") ?? "unconfirmed";
            response.State = _goalState?.Clone();
        }
        catch (TimeoutException)
        {
            response.Status = "unconfirmed";
            // Delivery may have occurred; never replay a native command after a missing receipt.
            response.State = _goalState?.Clone();
        }
        finally { _goalReceipts.TryRemove(command.CommandId, out _); }
        return ClaudeGoalOutcome(command, response);
    }

    private bool HandleGoalBridgeMessage(string type, JsonElement root)
    {
        switch (type)
        {
            case "bridge.goal.receipt":
                if (GetString(root, "requestId") is { } id && _goalReceipts.TryGetValue(id, out var receipt))
                    receipt.TrySetResult(root.Clone());
                return true;
            case "bridge.goal.changed":
                var state = JsonSerializer.Deserialize(root.GetProperty("state"), AppServerControlHostJsonContext.Default.AppServerControlGoalState);
                if (state is null || !string.Equals(state.ThreadId, _providerThreadId, StringComparison.Ordinal)) return true;
                _goalState = state;
                _emit(CreateEvent("thread.goal.changed", null, null, null, "claude.agent-sdk", "/goal/readback", root,
                    e => e.GoalUpdated = state.Clone()));
                return true;
            case "bridge.goal.interrupting":
                _goalStopTurnId = GetString(root, "turnId");
                return true;
            case "bridge.goal.interrupt.failed":
                if (string.Equals(_goalStopTurnId, GetString(root, "turnId"), StringComparison.Ordinal)) _goalStopTurnId = null;
                return true;
            case "bridge.goal.turn.started":
                ResetTurnState();
                _activeTurnId = GetString(root, "turnId");
                _activeTurnModel = _quickSettings.Model; _activeTurnEffort = _quickSettings.Effort;
                _turnStarted = true;
                _emit(CreateEvent("turn.started", _activeTurnId, null, null, "claude.agent-sdk", "/goal/set", root,
                    e => e.TurnStarted = new() { Model = _activeTurnModel, Effort = _activeTurnEffort }));
                _emit(CreateEvent("session.state.changed", _activeTurnId, null, null, "claude.agent-sdk", "/goal/set", root,
                    e => e.SessionState = new() { State = "running", StateLabel = "Running" }));
                return true;
            default: return false;
        }
    }

    private static HostCommandOutcome ClaudeGoalOutcome(AppServerControlHostCommandEnvelope command, AppServerControlGoalResponse response) =>
        new() { Result = new() { CommandId = command.CommandId, SessionId = command.SessionId, Status = "accepted",
            Accepted = new() { SessionId = command.SessionId, Status = "accepted", Goal = response } } };
}
