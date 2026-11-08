using System.Reflection;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.AgentHost.UnitTests;

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
}
