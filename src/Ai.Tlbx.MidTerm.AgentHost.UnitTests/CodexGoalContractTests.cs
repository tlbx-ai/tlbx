using System.Reflection;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.AgentHost.UnitTests;

public sealed class CodexGoalContractTests
{
    [Theory]
    [InlineData("set", true)]
    [InlineData("clear", true)]
    [InlineData("get", false)]
    public void NativeGoalWireUsesUserOriginOnlyForMutations(string operation, bool origin)
    {
        using var budget = JsonDocument.Parse("null");
        var request = new AppServerControlGoalSetRequest
        {
            Operation = operation, Objective = "Keep", Status = "paused", TokenBudget = budget.RootElement.Clone()
        };
        var build = typeof(CodexAppServerControlAgentRuntime).GetMethod("BuildCodexGoalRequest", BindingFlags.Static | BindingFlags.NonPublic)!;
        var json = (string)build.Invoke(null, ["request", "own", request])!;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("thread/goal/" + operation, root.GetProperty("method").GetString());
        var parameters = root.GetProperty("params");
        Assert.Equal("own", parameters.GetProperty("threadId").GetString());
        Assert.Equal(origin, parameters.TryGetProperty("origin", out var provenance));
        if (origin) Assert.Equal("user", provenance.GetString());
        Assert.Equal(operation == "set", parameters.TryGetProperty("tokenBudget", out var actualBudget));
        if (operation == "set") Assert.Equal(JsonValueKind.Null, actualBudget.ValueKind);
    }

    [Fact]
    public async Task GoalNotificationsKeepActualUsageAndRejectSiblingOrUnscopedClears()
    {
        var events = new List<AppServerControlProviderEvent>();
        await using var runtime = new CodexAppServerControlAgentRuntime(events.Add);
        typeof(CodexAppServerControlAgentRuntime).GetField("_providerThreadId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, "own");
        var handle = typeof(CodexAppServerControlAgentRuntime).GetMethod("HandleCodexNotification", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var actual = JsonDocument.Parse("""
            {"threadId":"own","turnId":"own-turn","goal":{"threadId":"own","objective":"Keep",
             "status":"budgetLimited","tokensUsed":31,"timeUsedSeconds":5,"createdAt":1,"updatedAt":8,"tokenBudget":30}}
            """);
        handle.Invoke(runtime, ["thread/goal/updated", actual.RootElement]);
        var state = Assert.Single(events).GoalUpdated!;
        Assert.Equal("confirmed", state.Confirmation);
        Assert.Equal("budgetLimited", state.Goal!.Status);
        Assert.Equal(31, state.Goal.TokensUsed);
        Assert.Equal(5, state.Goal.TimeUsedSeconds);
        Assert.All(events, e => Assert.Null(e.RuntimeMessage));
        using var sibling = JsonDocument.Parse("""{"threadId":"sibling"}""");
        using var missing = JsonDocument.Parse("{}");
        handle.Invoke(runtime, ["thread/goal/cleared", sibling.RootElement]);
        handle.Invoke(runtime, ["thread/goal/cleared", missing.RootElement]);
        Assert.Single(events);
        using var own = JsonDocument.Parse("""{"threadId":"own"}""");
        handle.Invoke(runtime, ["thread/goal/cleared", own.RootElement]);
        Assert.Null(events.Last().GoalUpdated!.Goal);
    }
}
