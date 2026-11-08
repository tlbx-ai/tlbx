using System.Reflection;
using System.Collections.Concurrent;
using Ai.Tlbx.MidTerm.UnitTests;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.AgentHost.UnitTests;

[Collection(PathSensitiveEnvironmentCollection.Name)]
public sealed class ClaudeGoalRuntimeMergeTests
{
    private static void Set(ClaudeAppServerControlAgentRuntime runtime, string name, object value) =>
        typeof(ClaudeAppServerControlAgentRuntime).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, value);

    private static void Line(ClaudeAppServerControlAgentRuntime runtime, string json) =>
        typeof(ClaudeAppServerControlAgentRuntime).GetMethod("HandleClaudeLine", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(runtime, [json]);

    [Fact]
    public async Task ImmediateGoalReadbackAfterReadyUsesTheAcknowledgedFreshNativeIdentity()
    {
        const string nativeId = "08c2861c-3236-4c12-b270-7bf1b96ce585";
        var events = new List<AppServerControlProviderEvent>();
        await using var runtime = new ClaudeAppServerControlAgentRuntime(events.Add);
        Set(runtime, "_sessionId", "host-short"); Set(runtime, "_newProviderSessionId", nativeId);
        Line(runtime, """{"type":"bridge.ready","sdkVersion":"0.3.300","models":[],"commands":[]}""");
        Line(runtime, """{"type":"bridge.goal.changed","session_id":"08c2861c-3236-4c12-b270-7bf1b96ce585","state":{"threadId":"08c2861c-3236-4c12-b270-7bf1b96ce585","confirmation":"confirmed","scope":"thread","goal":{"objective":"Keep","status":"active"}}}""");
        var goal = Assert.Single(events.Where(e => e.GoalUpdated is not null));
        Assert.Equal(nativeId, goal.ThreadId); Assert.Equal("Keep", goal.GoalUpdated!.Goal!.Objective);
        Assert.DoesNotContain(events, e => e.TurnStarted is not null);
    }

    [Fact]
    public async Task ExplicitGoalStopFinalizesActualAbortEvenWhenSteeringWasPending()
    {
        var events = new List<AppServerControlProviderEvent>();
        await using var runtime = new ClaudeAppServerControlAgentRuntime(events.Add);
        Set(runtime, "_sessionId", "host"); Set(runtime, "_providerThreadId", "own");
        Set(runtime, "_activeTurnId", "goal-turn"); Set(runtime, "_steeringPending", true);
        Line(runtime, """{"type":"bridge.goal.interrupting","turnId":"goal-turn"}""");
        Line(runtime, """{"type":"result","session_id":"own","subtype":"success","is_error":false,"queued_turn_count":0,"terminal_reason":"aborted_streaming","result":""}""");
        var completed = Assert.Single(events.Where(e => e.TurnCompleted is not null));
        Assert.Equal("aborted", completed.TurnCompleted!.State);
        Assert.Equal("goal_stop", completed.TurnCompleted.StopReason);
        Assert.DoesNotContain(events, e => e.TurnStarted is not null);
    }
    [Theory]
    [InlineData("turn.start", "own", "current", false, true)]
    [InlineData("turn.start", "own", "old", false, false)]
    [InlineData("turn.start", "foreign", "current", false, false)]
    [InlineData("turn.start", "own", "current", true, false)]
    [InlineData("turn.steer", "own", "current", true, false)]
    public async Task BlockedInputNeverFailsAnotherOrAlreadyStartedTurn(string type, string thread, string turn, bool started, bool completes)
    {
        var events = new List<AppServerControlProviderEvent>();
        await using var runtime = new ClaudeAppServerControlAgentRuntime(events.Add);
        Set(runtime, "_sessionId", "host"); Set(runtime, "_providerThreadId", "own");
        Set(runtime, "_activeTurnId", "current"); Set(runtime, "_turnStarted", started);
        Line(runtime, JsonSerializer.Serialize(new { type = "bridge.input_rejected", session_id = thread,
            commandType = type, turnId = turn, message = "Goal outcome unknown" }));
        Assert.Equal(completes ? 1 : 0, events.Count(e => e.TurnCompleted is not null));
        Assert.DoesNotContain(events, e => e.Type == "runtime.error");
        Assert.Equal(completes ? null : "current", typeof(ClaudeAppServerControlAgentRuntime).GetField("_activeTurnId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime));
    }

    [Fact]
    public async Task RecoveryRejectsWrongScopeWithoutStartingOrKillingAnyProcess()
    {
        var events = new List<AppServerControlProviderEvent>();
        await using var runtime = new ClaudeAppServerControlAgentRuntime(events.Add);
        Set(runtime, "_sessionId", "host"); Set(runtime, "_providerThreadId", "own"); Set(runtime, "_workingDirectory", Path.GetTempPath()); Set(runtime, "_binaryPath", "fixture");
        var result = await runtime.ExecuteAsync(new() { SessionId = "host", CommandId = "recover", Type = "runtime.recover",
            SetGoal = new() { ExpectedThreadId = "other" } }, TestContext.Current.CancellationToken);
        Assert.Equal("targetUnavailable", result.Result.Accepted!.Goal!.Status);
        Assert.Empty(events);
    }

    [Fact]
    public async Task RecoveryOfLostGoalResultResumesExactNativeIdentityAndOnlyReadsBackWithoutReplay()
    {
        using var fixture = FakeClaudePathScope.Create();
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "goal-contract-fixture"), "fixture", ct);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "lose-goal-result"), "fixture", ct);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "capture-runtime-contract"), "fixture", ct);
        var events = new ConcurrentQueue<AppServerControlProviderEvent>();
        await using var runtime = new ClaudeAppServerControlAgentRuntime(events.Enqueue);
        var attach = await runtime.ExecuteAsync(new() { SessionId = "host", CommandId = "attach", Type = "runtime.attach",
            AttachRuntime = new() { Provider = "claude", WorkingDirectory = fixture.Root, ExecutablePath = fixture.ExecutablePath } }, ct);
        var threadId = attach.Events.Single(e => e.ThreadState is not null).ThreadState!.ProviderThreadId!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!events.Any(e => e.GoalUpdated?.Confirmation == "confirmed") && !timeout.IsCancellationRequested) await Task.Delay(20, ct);
        Assert.True(events.Any(e => e.GoalUpdated?.Confirmation == "confirmed"), JsonSerializer.Serialize(events.ToArray()));
        await runtime.ExecuteAsync(new() { SessionId = "host", CommandId = "set", Type = "thread.goal.set",
            SetGoal = new() { Operation = "set", Objective = "Keep", ExpectedThreadId = threadId } }, ct);
        var inputsPath = Path.Combine(fixture.Root, "goal-inputs.jsonl");
        while ((await File.ReadAllLinesAsync(inputsPath, timeout.Token)).Length < 2) await Task.Delay(20, timeout.Token);
        var recovered = await runtime.ExecuteAsync(new() { SessionId = "host", CommandId = "recover", Type = "runtime.recover",
            SetGoal = new() { Operation = "recover", ExpectedThreadId = threadId } }, ct);
        Assert.Equal(threadId, recovered.Result.Accepted!.Goal!.ThreadId);
        while (!events.Any(e => e.GoalUpdated is { Confirmation: "confirmed", Goal.Objective: "Keep" })) await Task.Delay(20, timeout.Token);
        var inputs = await File.ReadAllLinesAsync(inputsPath, ct);
        Assert.Equal(3, inputs.Length);
        using var first = JsonDocument.Parse(inputs[0]); using var set = JsonDocument.Parse(inputs[1]); using var read = JsonDocument.Parse(inputs[2]);
        Assert.Equal("/goal", first.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("/goal Keep", set.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("/goal", read.RootElement.GetProperty("prompt").GetString());
        Assert.Equal(threadId, read.RootElement.GetProperty("sessionId").GetString());
        Assert.DoesNotContain(events, e => e.TurnStarted is not null);
        var log = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "runtime-contract.jsonl"), ct);
        Assert.Contains("\"resume\":\"" + threadId + "\"", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OldBridgeGenerationCannotReadLateResultsIntoTheReplacementTurn()
    {
        var events = new List<AppServerControlProviderEvent>();
        await using var runtime = new ClaudeAppServerControlAgentRuntime(events.Add);
        Set(runtime, "_sessionId", "host"); Set(runtime, "_providerThreadId", "own"); Set(runtime, "_activeTurnId", "replacement");
        using var process = new System.Diagnostics.Process();
        using var bytes = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"type\":\"result\",\"session_id\":\"own\",\"subtype\":\"success\"}\n"));
        using var output = new StreamReader(bytes); Set(runtime, "_output", output);
        var method = typeof(ClaudeAppServerControlAgentRuntime).GetMethod("ReadLoopAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(runtime, [process, -1, TestContext.Current.CancellationToken])!;
        Assert.Empty(events);
        Assert.Equal("replacement", typeof(ClaudeAppServerControlAgentRuntime).GetField("_activeTurnId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime));
    }

    [Fact]
    public async Task RecoveryClosesOwnedCanonicalCallbacksAsCancelledAndRejectsForeignScope()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-goal-recovery-" + Guid.NewGuid().ToString("N"));
        var history = new Ai.Tlbx.MidTerm.Services.Sessions.SessionAppServerControlHistoryService(storeDirectory: directory, enableScreenLogging: false);
        var sessionId = Guid.NewGuid().ToString("N");
        await using var server = new AppServerControlAgentHostServer(null);
        typeof(AppServerControlAgentHostServer).GetField("_history", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(server, history);
        try
        {
            history.Append(new() { SessionId = sessionId, Provider = "claude", ThreadId = "own", TurnId = "turn",
                Type = "request.opened", RequestId = "waiting", RequestOpened = new() { RequestType = "tool_approval" } });
            var window = history.GetSnapshotWindow(sessionId, 0, 1)!;
            var method = typeof(AppServerControlAgentHostServer).GetMethod("CancelRecoveredRequests", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var command = new AppServerControlHostCommandEnvelope { SessionId = sessionId, SetGoal = new() { ExpectedThreadId = "foreign" } };
            method.Invoke(server, [command, window, "claude"]);
            Assert.Equal("open", Assert.Single(history.GetSnapshot(sessionId)!.Requests).State);
            command.SetGoal.ExpectedThreadId = "own";
            method.Invoke(server, [command, window, "claude"]);
            var settled = Assert.Single(history.GetSnapshot(sessionId)!.Requests);
            Assert.Equal("resolved", settled.State); Assert.Equal("cancel", settled.Decision);
        }
        finally { history.Forget(sessionId); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

}
