using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.UnitTests;
using Xunit;

namespace Ai.Tlbx.MidTerm.AgentHost.UnitTests;

public sealed class CodexMcpToolApprovalTests
{
    private const string AutoSettings = """{"approvalPolicy":"never","sandboxPolicy":{"type":"dangerFullAccess"},"collaborationMode":{"mode":"default"}}""";
    private const string Elicitation = """
        {"threadId":"own","turnId":"turn","serverName":"tlbx","mode":"form",
         "_meta":{"codex_approval_kind":"mcp_tool_call","persist":["session","always"],"tool_name":"browser_open",
         "tool_params":{"previewName":"fixture-preview","url":"https://localhost:2180/","activate":true}},
         "message":"Allow this exact MCP tool call?","requestedSchema":{"type":"object","properties":{}}}
        """;

    private static void Notification(CodexAppServerControlAgentRuntime runtime, string method, string payload)
    {
        using var json = JsonDocument.Parse(payload);
        typeof(CodexAppServerControlAgentRuntime).GetMethod("HandleCodexNotification", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(runtime, [method, json.RootElement]);
    }

    private static void Request(CodexAppServerControlAgentRuntime runtime, string payload = Elicitation, string id = "0")
    {
        using var json = JsonDocument.Parse(payload); using var rpc = JsonDocument.Parse(id);
        typeof(CodexAppServerControlAgentRuntime).GetMethod("HandleCodexServerRequest", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, ["mcpServer/elicitation/request", rpc.RootElement, json.RootElement]);
    }

    private static async Task AttachAsync(CodexAppServerControlAgentRuntime runtime, FakeCodexWebSocketServer server)
    {
        await runtime.ExecuteAsync(new() { SessionId = "session", CommandId = "attach", Type = "runtime.attach",
            AttachRuntime = new() { Provider = "codex", WorkingDirectory = Path.GetTempPath(), ResumeThreadId = "own",
                AttachPoint = new() { Provider = "codex", Endpoint = server.Endpoint, SharedRuntime = true,
                    TransportKind = SessionAgentAttachPoint.CodexAppServerWebSocketTransport } } }, TestContext.Current.CancellationToken);
        Notification(runtime, "thread/settings/updated", """{"threadId":"own","threadSettings":{"approvalPolicy":"on-request","sandboxPolicy":{"type":"workspaceWrite"},"collaborationMode":{"mode":"default"}}}""");
        Notification(runtime, "turn/started", """{"threadId":"own","turn":{"id":"turn"}}""");
    }

    private static Task<HostCommandOutcome> Resolve(CodexAppServerControlAgentRuntime runtime, string requestId, string decision) =>
        runtime.ExecuteAsync(new() { SessionId = "session", CommandId = "resolve", Type = "request.resolve",
            ResolveRequest = new() { RequestId = requestId, Decision = decision } }, TestContext.Current.CancellationToken);

    private static async Task WaitForResponseAsync(FakeCodexWebSocketServer server)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (server.ServerRequestResponses.IsEmpty) await Task.Delay(10, timeout.Token);
    }

    private static Task<HostCommandOutcome> ConfigureAutoAsync(CodexAppServerControlAgentRuntime runtime)
    {
        using var value = JsonDocument.Parse("\"auto\"");
        return runtime.ExecuteAsync(new() { SessionId = "session", CommandId = "configure", Type = "runtime.configure",
            Configure = new() { ExpectedThreadId = "own", ExpectedTurnId = "turn",
                Settings = new(StringComparer.Ordinal) { ["permissionMode"] = value.RootElement.Clone() } } }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("accept", "0", JsonValueKind.Number)]
    [InlineData("decline", "0", JsonValueKind.Number)]
    [InlineData("cancel", "0", JsonValueKind.Number)]
    [InlineData("accept", "\"0\"", JsonValueKind.String)]
    public async Task ManualRepliesUseRealElicitationContractAndPreserveRpcIdentity(string decision, string id, JsonValueKind kind)
    {
        await using var server = FakeCodexWebSocketServer.Start("own", "unused");
        var events = new ConcurrentQueue<AppServerControlProviderEvent>();
        await using var runtime = new CodexAppServerControlAgentRuntime(events.Enqueue);
        await AttachAsync(runtime, server);
        Request(runtime, id: id); Request(runtime, id: id); // A duplicate outstanding native request is one UI request.
        var opened = Assert.Single(events.Where(e => e.RequestOpened is not null));
        Assert.Equal("mcp_tool_approval", opened.RequestOpened!.RequestType);
        Assert.Contains("browser_open", opened.RequestOpened.Detail, StringComparison.Ordinal);
        Assert.Contains("\"activate\":true", opened.RequestOpened.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e.Type == "server.request.unsupported");
        var result = await Resolve(runtime, opened.RequestId!, decision);
        Assert.Equal(decision, result.Result.Accepted!.Decision);
        await WaitForResponseAsync(server);
        var response = Assert.Single(server.ServerRequestResponses);
        Assert.Equal(kind, response.GetProperty("id").ValueKind);
        Assert.Equal("0", response.GetProperty("id").ToString());
        var actual = response.GetProperty("result");
        Assert.Equal(decision, actual.GetProperty("action").GetString());
        Assert.False(actual.TryGetProperty("decision", out _)); Assert.False(actual.TryGetProperty("_meta", out _));
        Assert.Equal(decision == "accept", actual.TryGetProperty("content", out var content));
        if (decision == "accept") Assert.Equal("{}", content.GetRawText());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Resolve(runtime, opened.RequestId!, "accept"));
        Assert.Single(events.Where(e => e.RequestResolved is not null));
        Assert.DoesNotContain(events, e => e.Type == "runtime.error");
    }

    [Theory]
    [InlineData("sibling", "turn")]
    [InlineData("own", "ended")]
    [InlineData("own", null)]
    public async Task ForeignOrStaleRequestsNeverOpenOrReceiveAnAutomaticAnswer(string thread, string? turn)
    {
        await using var server = FakeCodexWebSocketServer.Start("own", "unused");
        var events = new ConcurrentQueue<AppServerControlProviderEvent>();
        await using var runtime = new CodexAppServerControlAgentRuntime(events.Enqueue); await AttachAsync(runtime, server);
        var payload = System.Text.Json.Nodes.JsonNode.Parse(Elicitation)!;
        payload["threadId"] = thread; payload["turnId"] = turn;
        Request(runtime, payload.ToJsonString());
        Assert.DoesNotContain(events, e => e.RequestOpened is not null);
        Assert.Empty(server.ServerRequestResponses);
    }

    [Theory]
    [InlineData("ended")]
    [InlineData("provider-resolved")]
    [InlineData("changed-thread")]
    public async Task LateManualResolveCannotWriteOrReplaceCanonicalSettlement(string reason)
    {
        await using var server = FakeCodexWebSocketServer.Start("own", "unused");
        var events = new ConcurrentQueue<AppServerControlProviderEvent>();
        await using var runtime = new CodexAppServerControlAgentRuntime(events.Enqueue); await AttachAsync(runtime, server); Request(runtime);
        var opened = Assert.Single(events.Where(e => e.RequestOpened is not null));
        if (reason == "ended") Notification(runtime, "turn/completed", """{"threadId":"own","turn":{"id":"turn","status":"completed"}}""");
        else if (reason == "provider-resolved") Notification(runtime, "serverRequest/resolved", """{"threadId":"own","requestId":0}""");
        else typeof(CodexAppServerControlAgentRuntime).GetField("_providerThreadId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(runtime, "replacement");
        var settled = events.Count(e => e.RequestResolved is not null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Resolve(runtime, opened.RequestId!, "accept"));
        Assert.Empty(server.ServerRequestResponses);
        Assert.Equal(settled, events.Count(e => e.RequestResolved is not null));
        Assert.DoesNotContain(events, e => e.Type == "runtime.error");
    }

    [Theory]
    [InlineData("url")]
    [InlineData("nonempty")]
    [InlineData("generic")]
    [InlineData("required")]
    public async Task GenericInteractiveAndUrlElicitationAreNotToolApprovalsEvenInAuto(string shape)
    {
        await using var server = FakeCodexWebSocketServer.Start("own", "unused");
        var events = new ConcurrentQueue<AppServerControlProviderEvent>();
        await using var runtime = new CodexAppServerControlAgentRuntime(events.Enqueue); await AttachAsync(runtime, server);
        Notification(runtime, "thread/settings/updated", "{\"threadId\":\"own\",\"threadSettings\":" + AutoSettings + "}");
        var payload = System.Text.Json.Nodes.JsonNode.Parse(Elicitation)!;
        if (shape == "url")
        {
            payload["mode"] = "url"; payload["url"] = "https://example.invalid/consent"; payload["elicitationId"] = "fixture-elicitation";
            ((System.Text.Json.Nodes.JsonObject)payload).Remove("requestedSchema");
        }
        if (shape == "nonempty") payload["requestedSchema"]!["properties"] = System.Text.Json.Nodes.JsonNode.Parse("""{"answer":{"type":"string"}}""");
        if (shape == "generic") payload["_meta"] = new System.Text.Json.Nodes.JsonObject();
        if (shape == "required") payload["requestedSchema"]!["required"] = new System.Text.Json.Nodes.JsonArray("answer");
        Request(runtime, payload.ToJsonString()); await WaitForResponseAsync(server);
        Assert.DoesNotContain(events, e => e.RequestOpened is not null);
        Assert.True(Assert.Single(server.ServerRequestResponses).TryGetProperty("error", out _));
    }

    [Fact]
    public async Task AutoOnArrivalAnswersOnlyTheExactTypedToolCallOnce()
    {
        await using var server = FakeCodexWebSocketServer.Start("own", "unused");
        var events = new ConcurrentQueue<AppServerControlProviderEvent>();
        await using var runtime = new CodexAppServerControlAgentRuntime(events.Enqueue); await AttachAsync(runtime, server);
        Notification(runtime, "thread/settings/updated", "{\"threadId\":\"own\",\"threadSettings\":" + AutoSettings + "}");
        Request(runtime); await WaitForResponseAsync(server);
        using var settledTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        settledTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!events.Any(e => e.RequestResolved is not null)) await Task.Delay(10, settledTimeout.Token);
        Assert.Equal("accept", Assert.Single(server.ServerRequestResponses).GetProperty("result").GetProperty("action").GetString());
        Assert.Equal("accept", Assert.Single(events.Where(e => e.RequestResolved is not null)).RequestResolved!.Decision);
        Assert.DoesNotContain(events, e => e.Type == "turn.started" && e.TurnId != "turn");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task WaitingToolApprovalRequiresSuccessfulAutoAckAndStillCurrentTurn(bool reject, bool turnEnds)
    {
        await using var server = FakeCodexWebSocketServer.Start("own", "unused");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ThreadSettingsUpdateJson = AutoSettings; server.RejectThreadSettingsUpdate = reject; server.ThreadSettingsUpdateGate = release.Task;
        var events = new ConcurrentQueue<AppServerControlProviderEvent>();
        await using var runtime = new CodexAppServerControlAgentRuntime(events.Enqueue); await AttachAsync(runtime, server); Request(runtime);
        var configured = ConfigureAutoAsync(runtime);
        try
        {
            await server.ThreadSettingsUpdateReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Empty(server.ServerRequestResponses);
            if (turnEnds) Notification(runtime, "turn/completed", """{"threadId":"own","turn":{"id":"turn","status":"completed"}}""");
        }
        finally { release.TrySetResult(); }
        var result = await configured;
        if (!reject && !turnEnds)
        {
            await WaitForResponseAsync(server);
            Assert.Single(events.Where(e => e.RequestResolved?.Decision == "accept"));
            Assert.Equal("subsequentTurns", result.Result.Configured!.ThreadScope);
        }
        else
        {
            Assert.Empty(server.ServerRequestResponses);
            Assert.DoesNotContain(events, e => e.RequestResolved?.Decision == "accept");
        }
    }

    [Fact]
    public async Task PreviousAutoDefaultsCannotAnswerBeforeNewTurnSettingsAreAcknowledged()
    {
        await using var server = FakeCodexWebSocketServer.Start("own", "unused");
        var events = new ConcurrentQueue<AppServerControlProviderEvent>();
        await using var runtime = new CodexAppServerControlAgentRuntime(events.Enqueue); await AttachAsync(runtime, server);
        Notification(runtime, "thread/settings/updated", "{\"threadId\":\"own\",\"threadSettings\":" + AutoSettings + "}");
        var pending = typeof(CodexAppServerControlAgentRuntime).GetField("_mcpTurnStartPending", BindingFlags.NonPublic | BindingFlags.Instance)!;
        pending.SetValue(runtime, true); Request(runtime);
        Assert.Empty(server.ServerRequestResponses);
        Notification(runtime, "thread/settings/updated", """{"threadId":"own","threadSettings":{"approvalPolicy":"on-request","sandboxPolicy":{"type":"workspaceWrite"}}}""");
        pending.SetValue(runtime, false);
        await (Task)typeof(CodexAppServerControlAgentRuntime).GetMethod("ReleasePendingMcpApprovalsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(runtime, ["own", "turn"])!;
        Assert.Empty(server.ServerRequestResponses);
        await Resolve(runtime, Assert.Single(events.Where(e => e.RequestOpened is not null)).RequestId!, "decline");
    }

    [Fact]
    public async Task SessionPersistenceDecisionIsNotSilentlyGranted()
    {
        await using var server = FakeCodexWebSocketServer.Start("own", "unused");
        var events = new ConcurrentQueue<AppServerControlProviderEvent>();
        await using var runtime = new CodexAppServerControlAgentRuntime(events.Enqueue); await AttachAsync(runtime, server); Request(runtime);
        var opened = Assert.Single(events.Where(e => e.RequestOpened is not null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Resolve(runtime, opened.RequestId!, "acceptForSession"));
        Assert.Empty(server.ServerRequestResponses); Assert.DoesNotContain(events, e => e.RequestResolved is not null);
        await Resolve(runtime, opened.RequestId!, "decline");
    }

    [Fact]
    public async Task PlanModeKeepsAnArrivingTypedApprovalManualEvenWithAutoDefaults()
    {
        await using var server = FakeCodexWebSocketServer.Start("own", "unused");
        var events = new ConcurrentQueue<AppServerControlProviderEvent>();
        await using var runtime = new CodexAppServerControlAgentRuntime(events.Enqueue); await AttachAsync(runtime, server);
        Notification(runtime, "thread/settings/updated", "{\"threadId\":\"own\",\"threadSettings\":" + AutoSettings.Replace("default", "plan", StringComparison.Ordinal) + "}");
        Request(runtime);
        Assert.Empty(server.ServerRequestResponses);
        await Resolve(runtime, Assert.Single(events.Where(e => e.RequestOpened is not null)).RequestId!, "decline");
        await WaitForResponseAsync(server);
        Assert.Equal("decline", Assert.Single(server.ServerRequestResponses).GetProperty("result").GetProperty("action").GetString());
    }

    [Fact]
    public async Task ManualAndAutoInterleavingSendsOneActualDecisionAndNeverOverwritesTheCanonicalWinner()
    {
        await using var server = FakeCodexWebSocketServer.Start("own", "unused");
        var events = new ConcurrentQueue<AppServerControlProviderEvent>();
        await using var runtime = new CodexAppServerControlAgentRuntime(events.Enqueue); await AttachAsync(runtime, server); Request(runtime);
        var opened = Assert.Single(events.Where(e => e.RequestOpened is not null));
        var gate = (SemaphoreSlim)typeof(CodexAppServerControlAgentRuntime).GetField("_codexWriteGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!;
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        Task<HostCommandOutcome> manual;
        Task automatic;
        try
        {
            manual = Resolve(runtime, opened.RequestId!, "decline");
            Notification(runtime, "thread/settings/updated", "{\"threadId\":\"own\",\"threadSettings\":" + AutoSettings + "}");
            automatic = (Task)typeof(CodexAppServerControlAgentRuntime).GetMethod("ReleasePendingMcpApprovalsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(runtime, ["own", "turn"])!;
        }
        finally { gate.Release(); }
        try { await manual; } catch (InvalidOperationException) { /* Auto may settle before the manual write claims this request. */ }
        await automatic; await WaitForResponseAsync(server);
        var actual = Assert.Single(server.ServerRequestResponses).GetProperty("result").GetProperty("action").GetString();
        Assert.Equal(actual, Assert.Single(events.Where(e => e.RequestResolved is not null)).RequestResolved!.Decision);
    }

    [Fact]
    public async Task ScopeIsRecheckedAfterWaitingForTheTransportWriteGate()
    {
        await using var server = FakeCodexWebSocketServer.Start("own", "unused");
        var events = new ConcurrentQueue<AppServerControlProviderEvent>();
        await using var runtime = new CodexAppServerControlAgentRuntime(events.Enqueue); await AttachAsync(runtime, server);
        Notification(runtime, "thread/settings/updated", "{\"threadId\":\"own\",\"threadSettings\":" + AutoSettings + "}");
        var gate = (SemaphoreSlim)typeof(CodexAppServerControlAgentRuntime).GetField("_codexWriteGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!;
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            Request(runtime);
            Notification(runtime, "turn/completed", """{"threadId":"own","turn":{"id":"turn","status":"completed"}}""");
        }
        finally { gate.Release(); }
        await gate.WaitAsync(TestContext.Current.CancellationToken); gate.Release();
        Assert.Empty(server.ServerRequestResponses);
        Assert.DoesNotContain(events, e => e.RequestResolved?.Decision == "accept");
        Assert.Equal("cancel", Assert.Single(events.Where(e => e.RequestResolved is not null)).RequestResolved!.Decision);
    }

    [Fact]
    public async Task ProviderSettlementRequiresTheCorrectThreadAndExactNumericRequestId()
    {
        await using var server = FakeCodexWebSocketServer.Start("own", "unused");
        var events = new ConcurrentQueue<AppServerControlProviderEvent>();
        await using var runtime = new CodexAppServerControlAgentRuntime(events.Enqueue); await AttachAsync(runtime, server); Request(runtime);
        Notification(runtime, "serverRequest/resolved", """{"threadId":"sibling","requestId":0}""");
        Notification(runtime, "serverRequest/resolved", """{"threadId":"own","requestId":"0"}""");
        Assert.DoesNotContain(events, e => e.RequestResolved is not null);
        await Resolve(runtime, Assert.Single(events.Where(e => e.RequestOpened is not null)).RequestId!, "accept");
        await WaitForResponseAsync(server);
        Assert.Equal(JsonValueKind.Number, Assert.Single(server.ServerRequestResponses).GetProperty("id").ValueKind);
    }

    [Fact]
    public void PendingToolArgumentsSurviveRetentionAndCompactOnlyAfterResolution()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-mcp-approval-" + Guid.NewGuid().ToString("N"));
        var history = new SessionAppServerControlHistoryService(storeDirectory: directory, enableScreenLogging: false);
        var detail = "server/tool " + new string('x', 6000) + " exact-tail";
        try
        {
            history.Append(new() { SessionId = "s", Provider = "codex", ThreadId = "own", TurnId = "turn", Type = "request.opened", RequestId = "approval",
                RequestOpened = new() { RequestType = "mcp_tool_approval", Detail = detail } });
            Assert.Equal(detail, Assert.Single(history.GetSnapshotWindow("s", 0, 1)!.Requests).Detail);
            history.Append(new() { SessionId = "s", Provider = "codex", ThreadId = "own", TurnId = "turn", Type = "request.resolved", RequestId = "approval",
                RequestResolved = new() { RequestType = "mcp_tool_approval", Decision = "decline" } });
            Assert.True(Assert.Single(history.GetSnapshot("s")!.Requests).Detail!.Length <= 2048);
        }
        finally { history.Forget("s"); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
