using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.AgentHost.UnitTests;

public sealed class NativeGoalProtocolTests
{
    [Theory]
    [InlineData("own", null, "own", null, true)]
    [InlineData("own", "turn", "own", "turn", true)]
    [InlineData("own", null, "own", "new-turn", false)]
    [InlineData("own", "ended", "own", null, false)]
    [InlineData("sibling", "turn", "own", "turn", false)]
    [InlineData("", null, "own", null, false)]
    public void GoalGuardIncludesIdleAndActiveTurn(string expectedThread, string? expectedTurn,
        string thread, string? turn, bool matches)
    {
        Assert.Equal(matches, AppServerControlGoals.Matches(new()
        {
            ExpectedThreadId = expectedThread, ExpectedTurnId = expectedTurn
        }, thread, turn));
    }

    [Theory]
    [InlineData("", JsonValueKind.Undefined)]
    [InlineData(",\"tokenBudget\":null", JsonValueKind.Null)]
    [InlineData(",\"tokenBudget\":17", JsonValueKind.Number)]
    public void BudgetRetainsOmitVersusExplicitNullThroughHostWire(string budget, JsonValueKind kind)
    {
        var json = "{\"operation\":\"set\",\"expectedThreadId\":\"own\",\"expectedTurnId\":null,\"objective\":\"Keep\"" + budget + "}";
        var request = JsonSerializer.Deserialize(json, AppServerControlHostJsonContext.Default.AppServerControlGoalSetRequest)!;
        AppServerControlGoals.Validate(request);
        Assert.Equal(kind, request.TokenBudget.ValueKind);
        var encoded = JsonSerializer.Serialize(request, AppServerControlHostJsonContext.Default.AppServerControlGoalSetRequest);
        using var document = JsonDocument.Parse(encoded);
        Assert.Equal(kind != JsonValueKind.Undefined, document.RootElement.TryGetProperty("tokenBudget", out var actual));
        if (kind != JsonValueKind.Undefined) Assert.Equal(kind, actual.ValueKind);
    }

    [Fact]
    public void CanonicalGoalSurvivesRetentionAndSnapshotCloningWithoutTimelineNoise()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-goal-test-" + Guid.NewGuid().ToString("N"));
        var history = new SessionAppServerControlHistoryService(storeDirectory: directory);
        var imported = new SessionAppServerControlHistoryService(storeDirectory: Path.Combine(directory, "import"));
        try
        {
            using var subscription = history.SubscribeHistoryPatches("session", TestContext.Current.CancellationToken);
            var state = new AppServerControlGoalState
            {
                ThreadId = "own", Confirmation = "confirmed",
                Goal = new() { Objective = "Keep", Status = "paused", TokensUsed = 19, TokenBudget = null }
            };
            history.Append(new()
            {
                SessionId = "session", Provider = "codex", ThreadId = "own", Type = "thread.goal.changed",
                GoalUpdated = state, CreatedAt = DateTimeOffset.UtcNow
            });
            state.Goal.Objective = "Mutated caller";
            var snapshot = history.GetSnapshot("session")!;
            Assert.Equal("Keep", snapshot.Thread.Goal!.Goal!.Objective);
            Assert.Equal(19, snapshot.Thread.Goal.Goal.TokensUsed);
            Assert.Empty(snapshot.History);
            Assert.True(subscription.Reader.TryRead(out var patch));
            Assert.Equal("Keep", patch!.Thread.Goal!.Goal!.Objective);
            imported.ImportHistoryWindow(snapshot);
            Assert.Equal("Keep", imported.GetSnapshot("session")!.Thread.Goal!.Goal!.Objective);
            patch.Thread.Goal.Goal.Objective = "Patch actual";
            imported.ApplyHistoryPatch(patch);
            patch.Thread.Goal.Goal.Objective = "Mutated patch";
            Assert.Equal("Patch actual", imported.GetSnapshot("session")!.Thread.Goal!.Goal!.Objective);
            snapshot.Thread.Goal.Goal.Objective = "Mutated snapshot";
            Assert.Equal("Keep", history.GetSnapshot("session")!.Thread.Goal!.Goal!.Objective);
            history.Append(new()
            {
                SessionId = "session", Provider = "codex", ThreadId = "own", Type = "thread.goal.changed",
                GoalUpdated = new() { ThreadId = "own", Confirmation = "confirmed", Goal = null }
            });
            Assert.Null(history.GetSnapshot("session")!.Thread.Goal!.Goal);
            history.Append(new()
            {
                SessionId = "session", Provider = "codex", Type = "thread.started",
                ThreadState = new() { ProviderThreadId = "replacement", State = "active" }
            });
            Assert.Null(history.GetSnapshot("session")!.Thread.Goal);
        }
        finally
        {
            history.Forget("session"); imported.Forget("session");
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
