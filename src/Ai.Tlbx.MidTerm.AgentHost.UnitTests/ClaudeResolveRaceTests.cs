using System.Reflection;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.AgentHost.UnitTests;

public sealed class ClaudeResolveRaceTests
{
    private static void Set(ClaudeAppServerControlAgentRuntime runtime, string name, object value) =>
        typeof(ClaudeAppServerControlAgentRuntime).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, value);

    private static void Prepare(ClaudeAppServerControlAgentRuntime runtime, StreamWriter input)
    {
        Set(runtime, "_sessionId", "session"); Set(runtime, "_workingDirectory", Path.GetTempPath());
        Set(runtime, "_binaryPath", "no-provider-process"); Set(runtime, "_providerThreadId", "own");
        Set(runtime, "_activeTurnId", "turn"); Set(runtime, "_input", input);
    }

    private static void Bridge(ClaudeAppServerControlAgentRuntime runtime, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        typeof(ClaudeAppServerControlAgentRuntime).GetMethod("HandleBridgeMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, [root.GetProperty("type").GetString(), root, json]);
    }

    private static void AssertTurnAlive(ClaudeAppServerControlAgentRuntime runtime, List<AppServerControlProviderEvent> events)
    {
        Assert.Equal("turn", typeof(ClaudeAppServerControlAgentRuntime).GetField("_activeTurnId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime));
        Assert.Empty(events);
    }

    private static AppServerControlHostCommandEnvelope Permission(string decision = "decline") => new()
    {
        SessionId = "session", CommandId = "resolve", Type = "request.resolve",
        ResolveRequest = new() { RequestId = "request", Decision = decision }
    };

    [Theory]
    [InlineData("permission.resolve")]
    [InlineData("user_input.resolve")]
    public async Task MissingCallbackErrorsRejectOnlyTheCommandAndLeaveTheTurnAlive(string commandType)
    {
        var events = new List<AppServerControlProviderEvent>();
        using var stream = new MemoryStream(); using var input = new StreamWriter(stream);
        await using var runtime = new ClaudeAppServerControlAgentRuntime(events.Add);
        Prepare(runtime, input);
        var command = commandType == "permission.resolve" ? Permission() : new AppServerControlHostCommandEnvelope
        {
            SessionId = "session", CommandId = "resolve", Type = "user-input.resolve",
            ResolveUserInput = new() { RequestId = "request", Answers = [] }
        };
        var pending = runtime.ExecuteAsync(command, CancellationToken.None);
        Bridge(runtime, "{\"type\":\"bridge.error\",\"commandType\":\"" + commandType + "\",\"requestId\":\"request\",\"detail\":\"callback already settled\"}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        AssertTurnAlive(runtime, events);
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("decline")]
    [InlineData("cancel")]
    public async Task LateExplicitResolveReturnsActualDecisionWithoutWritingTheRequestedDecision(string actual)
    {
        var events = new List<AppServerControlProviderEvent>();
        using var stream = new MemoryStream(); using var input = new StreamWriter(stream);
        await using var runtime = new ClaudeAppServerControlAgentRuntime(events.Add);
        Prepare(runtime, input);
        var pending = runtime.ExecuteAsync(Permission(actual == "decline" ? "accept" : "decline"), CancellationToken.None);
        Bridge(runtime, "{\"type\":\"bridge.permission_resolved\",\"requestId\":\"request\",\"session_id\":\"own\",\"turnId\":\"turn\",\"status\":\"alreadySettled\",\"decision\":\"" + actual + "\"}");
        var outcome = await pending;
        Assert.Equal("alreadySettled", outcome.Result.Accepted!.Status);
        Assert.Equal(actual, outcome.Result.Accepted.Decision);
        Assert.Empty(outcome.Events);
        AssertTurnAlive(runtime, events);
    }

    [Fact]
    public async Task FirstExplicitAckProjectsItsActualDecisionOnTheCapturedTurn()
    {
        var events = new List<AppServerControlProviderEvent>();
        using var stream = new MemoryStream(); using var input = new StreamWriter(stream);
        await using var runtime = new ClaudeAppServerControlAgentRuntime(events.Add);
        Prepare(runtime, input);
        var pending = runtime.ExecuteAsync(Permission("accept"), CancellationToken.None);
        Bridge(runtime, """{"type":"bridge.permission_resolved","requestId":"request","session_id":"own","turnId":"turn","status":"applied","decision":"decline"}""");
        var outcome = await pending;
        Assert.Equal("decline", outcome.Result.Accepted!.Decision);
        var resolution = Assert.Single(outcome.Events);
        Assert.Equal("decline", resolution.RequestResolved!.Decision); Assert.Equal("turn", resolution.TurnId);
        AssertTurnAlive(runtime, events);
    }

    [Theory]
    [InlineData("unconfirmed", "own")]
    [InlineData("alreadySettled", "foreign")]
    public async Task UnknownOrWrongScopeReceiptCannotApproveOrFailTheTurn(string status, string threadId)
    {
        var events = new List<AppServerControlProviderEvent>();
        using var stream = new MemoryStream(); using var input = new StreamWriter(stream);
        await using var runtime = new ClaudeAppServerControlAgentRuntime(events.Add);
        Prepare(runtime, input);
        var pending = runtime.ExecuteAsync(Permission("accept"), CancellationToken.None);
        Bridge(runtime, "{\"type\":\"bridge.permission_resolved\",\"requestId\":\"request\",\"session_id\":\"" + threadId + "\",\"turnId\":\"turn\",\"status\":\"" + status + "\",\"decision\":\"accept\"}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        AssertTurnAlive(runtime, events);
    }

    [Theory]
    [InlineData("applied")]
    [InlineData("alreadySettled")]
    public async Task QuestionResolveUsesActualAnswersAndPreservesAlreadySettledHistory(string status)
    {
        var events = new List<AppServerControlProviderEvent>();
        using var stream = new MemoryStream(); using var input = new StreamWriter(stream);
        await using var runtime = new ClaudeAppServerControlAgentRuntime(events.Add);
        Prepare(runtime, input);
        var pending = runtime.ExecuteAsync(new()
        {
            SessionId = "session", CommandId = "answer", Type = "user-input.resolve",
            ResolveUserInput = new() { RequestId = "question", Answers = [new() { QuestionId = "q", Answers = ["new"] }] }
        }, CancellationToken.None);
        Bridge(runtime, """{"type":"bridge.user_input_resolved","requestId":"question","session_id":"own","turnId":"turn","status":"alreadySettled","decision":"answered","answers":{"q":["original"]}}""".Replace("alreadySettled", status));
        var outcome = await pending;
        Assert.Equal("original", Assert.Single(Assert.Single(outcome.Result.Accepted!.Answers!).Answers));
        if (status == "alreadySettled") Assert.Empty(outcome.Events);
        else Assert.Equal("original", Assert.Single(Assert.Single(Assert.Single(outcome.Events).UserInputResolved!.Answers).Answers));
        AssertTurnAlive(runtime, events);
    }
    [Fact]
    public async Task AcknowledgementCannotReattributeTheDecisionToANewerThread()
    {
        var events = new List<AppServerControlProviderEvent>();
        using var stream = new MemoryStream(); using var input = new StreamWriter(stream);
        await using var runtime = new ClaudeAppServerControlAgentRuntime(events.Add);
        Prepare(runtime, input);
        var pending = runtime.ExecuteAsync(Permission("accept"), CancellationToken.None);
        Set(runtime, "_providerThreadId", "replacement");
        Bridge(runtime, """{"type":"bridge.permission_resolved","requestId":"request","session_id":"own","turnId":"turn","status":"applied","decision":"accept"}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        AssertTurnAlive(runtime, events);
    }

}
