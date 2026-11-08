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

    private async Task<HostCommandOutcome> RecoverGoalAdapterAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        var request = command.SetGoal ?? throw new InvalidOperationException("Recovery scope is required.");
        string threadId;
        string? interruptedTurnId;
        lock (_bridgeReadSync)
        {
            if (!AppServerControlGoals.Matches(request, _providerThreadId, _activeTurnId))
                return ClaudeGoalOutcome(command, new() { SessionId = command.SessionId, ThreadId = _providerThreadId ?? string.Empty,
                    TurnId = request.ExpectedTurnId, Status = "targetUnavailable" });
            // Capture native identity and exclude old pipe events atomically before terminating this owned adapter.
            threadId = _providerThreadId!; interruptedTurnId = _activeTurnId;
            ++_bridgeGeneration;
        }
        await DisposeProcessAsync().ConfigureAwait(false);
        _providerThreadId = threadId;
        _goalStopTurnId = null;
        _goalState = new() { ThreadId = threadId, Confirmation = "unconfirmed", PendingOperation = "get",
            Goal = _goalState?.Goal?.Clone(), Detail = "recovering" };
        _emit(CreateEvent("thread.goal.changed", null, null, null, "mtagenthost.claude", "runtime.recover", request,
            e => e.GoalUpdated = _goalState.Clone()));
        if (interruptedTurnId is not null)
            _emit(CreateEvent("turn.completed", interruptedTurnId, null, null, "mtagenthost.claude", "runtime.recover", request,
                e => e.TurnCompleted = new() { State = "aborted", StateLabel = "Aborted", StopReason = "goal_recovery" }));
        try
        {
            await EnsureClaudeBridgeAsync(ct, loadHistory: false).ConfigureAwait(false);
            // The new Query acknowledged its initialization defaults; these are not inference/settings readback.
            _quickSettings.AcknowledgedFields = ["model", "effort", "planMode", "permissionMode"];
            _emit(CreateQuickSettingsUpdatedEvent(_quickSettings, "mtagenthost.claude", "runtime.recover", request));
            _emit(CreateEvent("session.state.changed", null, null, null, "mtagenthost.claude", "runtime.recover", request,
                e => e.SessionState = new() { State = "ready", StateLabel = "Ready" }));
        }
        catch
        {
            _goalState.Confirmation = "unconfirmed"; _goalState.PendingOperation = null; _goalState.Detail = "recoveryFailed";
            _emit(CreateEvent("thread.goal.changed", null, null, null, "mtagenthost.claude", "runtime.recover", request,
                e => e.GoalUpdated = _goalState.Clone()));
            _emit(CreateEvent("session.state.changed", null, null, null, "mtagenthost.claude", "runtime.recover", request,
                e => e.SessionState = new() { State = "error", StateLabel = "Error", Reason = "Goal adapter recovery failed." }));
            throw;
        }
        return ClaudeGoalOutcome(command, new() { SessionId = command.SessionId, ThreadId = threadId,
            TurnId = request.ExpectedTurnId, Status = "pending", State = _goalState.Clone() });
    }

    private void HandleBlockedGoalInput(JsonElement root, string rawLine)
    {
        if (!string.Equals(GetString(root, "session_id"), _providerThreadId, StringComparison.Ordinal)) return;
        var turnId = GetString(root, "turnId");
        // An unsent steer failure must never finalize the inference it tried to steer.
        if (GetString(root, "commandType") == "turn.start" && !_turnStarted && turnId is not null &&
            string.Equals(turnId, _activeTurnId, StringComparison.Ordinal))
        {
            ResetTurnState();
            _emit(CreateEvent("turn.completed", turnId, null, null, "claude.agent-sdk", "goal/input-rejected", root,
                e => e.TurnCompleted = new() { State = "failed", StateLabel = "Failed", StopReason = "goal_input_blocked",
                    ErrorMessage = GetString(root, "message") }, rawLine));
            _emit(CreateEvent("session.state.changed", turnId, null, null, "claude.agent-sdk", "goal/input-rejected", root,
                e => e.SessionState = new() { State = "ready", StateLabel = "Ready", Reason = GetString(root, "message") }));
        }
        _emit(CreateEvent("runtime.warning", turnId, null, null, "claude.agent-sdk", "goal/input-rejected", root,
            e => e.RuntimeMessage = new() { Message = "Input was not submitted while native Goal outcome is unresolved.",
                Detail = GetString(root, "message") }, rawLine));
    }

    private static HostCommandOutcome ClaudeGoalOutcome(AppServerControlHostCommandEnvelope command, AppServerControlGoalResponse response) =>
        new() { Result = new() { CommandId = command.CommandId, SessionId = command.SessionId, Status = "accepted",
            Accepted = new() { SessionId = command.SessionId, Status = "accepted", Goal = response } } };
}
