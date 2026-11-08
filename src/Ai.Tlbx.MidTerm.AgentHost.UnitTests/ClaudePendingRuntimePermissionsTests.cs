using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.AgentHost.UnitTests;

public sealed class ClaudePendingRuntimePermissionsTests
{
    private static void Set(ClaudeAppServerControlAgentRuntime runtime, string name, object value) =>
        typeof(ClaudeAppServerControlAgentRuntime).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, value);

    private static void Bridge(ClaudeAppServerControlAgentRuntime runtime, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        typeof(ClaudeAppServerControlAgentRuntime).GetMethod("HandleBridgeMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, [root.GetProperty("type").GetString(), root, json]);
    }

    private static void Prepare(ClaudeAppServerControlAgentRuntime runtime, StreamWriter input)
    {
        Set(runtime, "_sessionId", "session"); Set(runtime, "_workingDirectory", Path.GetTempPath());
        Set(runtime, "_binaryPath", "no-provider-process"); Set(runtime, "_providerThreadId", "own");
        Set(runtime, "_activeTurnId", "turn"); Set(runtime, "_input", input);
    }

    private static AppServerControlHostCommandEnvelope Configure()
    {
        using var value = JsonDocument.Parse("\"auto\"");
        return new()
        {
            CommandId = "configure", SessionId = "session", Type = "runtime.configure",
            Configure = new() { ExpectedThreadId = "own", ExpectedTurnId = "turn",
                Settings = new() { ["permissionMode"] = value.RootElement.Clone() } }
        };
    }

    private const string Accepted = """
        {"type":"bridge.configured","requestId":"configure","confirmed":{"permissionMode":"auto","planMode":"off"},"errors":{}}
        """;

    [Fact]
    public async Task AcknowledgedDefaultsPublishCanonicalAutoWithoutClaimingEffectiveReadback()
    {
        var events = new List<AppServerControlProviderEvent>();
        using var stream = new MemoryStream(); using var input = new StreamWriter(stream);
        await using var runtime = new ClaudeAppServerControlAgentRuntime(events.Add);
        Prepare(runtime, input);
        var pending = runtime.ExecuteAsync(Configure(), CancellationToken.None);
        Bridge(runtime, Accepted);
        var receipt = (await pending).Result.Configured!;
        Assert.Equal("acknowledged", receipt.Status);
        Assert.Empty(receipt.EffectiveSettings);
        var payload = Assert.Single(events).QuickSettingsUpdated!;
        Assert.Equal("auto", payload.PermissionMode);
        Assert.Contains("permissionMode", payload.AcknowledgedFields);
        Assert.Contains("planMode", payload.AcknowledgedFields);
    }

    [Fact]
    public async Task RejectedModeDoesNotPublishAutoDefaults()
    {
        var events = new List<AppServerControlProviderEvent>();
        using var stream = new MemoryStream(); using var input = new StreamWriter(stream);
        await using var runtime = new ClaudeAppServerControlAgentRuntime(events.Add);
        Prepare(runtime, input);
        var pending = runtime.ExecuteAsync(Configure(), CancellationToken.None);
        Bridge(runtime, """{"type":"bridge.configured","requestId":"configure","confirmed":{},"errors":{"permissionMode":"policy rejected"}}""");
        var receipt = (await pending).Result.Configured!;
        Assert.Equal("unconfirmed", receipt.Status);
        Assert.Empty(receipt.EffectiveSettings);
        Assert.Empty(events);
    }

    [Fact]
    public async Task TurnEndKeepsAcknowledgedDefaultsButReportsUnavailableLiveTarget()
    {
        var events = new List<AppServerControlProviderEvent>();
        using var stream = new MemoryStream(); using var input = new StreamWriter(stream);
        await using var runtime = new ClaudeAppServerControlAgentRuntime(events.Add);
        Prepare(runtime, input);
        var pending = runtime.ExecuteAsync(Configure(), CancellationToken.None);
        Set(runtime, "_activeTurnId", "replacement-turn");
        Bridge(runtime, Accepted);
        var receipt = (await pending).Result.Configured!;
        Assert.Equal("partial", receipt.Status);
        Assert.Equal("targetUnavailable", receipt.TurnStatus);
        Assert.Empty(receipt.EffectiveSettings);
        Assert.Equal("auto", Assert.Single(events).QuickSettingsUpdated!.PermissionMode);
    }

    [Fact]
    public async Task ThreadReplacementCannotPublishOldAcknowledgementIntoNewDefaults()
    {
        var events = new List<AppServerControlProviderEvent>();
        using var stream = new MemoryStream(); using var input = new StreamWriter(stream);
        await using var runtime = new ClaudeAppServerControlAgentRuntime(events.Add);
        Prepare(runtime, input);
        var pending = runtime.ExecuteAsync(Configure(), CancellationToken.None);
        Set(runtime, "_providerThreadId", "replacement-thread"); Bridge(runtime, Accepted);
        Assert.Equal("targetUnavailable", (await pending).Result.Configured!.Status);
        Assert.Empty(events);
    }

    [Fact]
    public async Task AutoSettlementIsCanonicalResolutionAndDoesNotCompleteAnExplicitResolveAck()
    {
        var events = new List<AppServerControlProviderEvent>();
        await using var runtime = new ClaudeAppServerControlAgentRuntime(events.Add);
        Set(runtime, "_providerThreadId", "own"); Set(runtime, "_activeTurnId", "later-turn");
        var acks = (ConcurrentDictionary<string, TaskCompletionSource>)typeof(ClaudeAppServerControlAgentRuntime)
            .GetField("_bridgeResolutionAcks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
        var manualAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        acks["permission:request"] = manualAck;
        Bridge(runtime, """{"type":"bridge.permission_auto_resolved","session_id":"foreign","turnId":"turn","requestId":"request","decision":"accept"}""");
        Assert.Empty(events);
        Bridge(runtime, """{"type":"bridge.permission_auto_resolved","session_id":"own","turnId":"turn","requestId":"request","decision":"accept"}""");
        var resolved = Assert.Single(events);
        Assert.Equal("request.resolved", resolved.Type); Assert.Equal("turn", resolved.TurnId);
        Assert.Equal("request", resolved.RequestId); Assert.Equal("accept", resolved.RequestResolved!.Decision);
        Assert.False(manualAck.Task.IsCompleted);
    }

    [Fact]
    public async Task SignalCancellationClosesTheCanonicalRequestWithoutApproval()
    {
        var events = new List<AppServerControlProviderEvent>();
        await using var runtime = new ClaudeAppServerControlAgentRuntime(events.Add);
        Set(runtime, "_providerThreadId", "own");
        Bridge(runtime, """{"type":"bridge.permission_cancelled","session_id":"own","turnId":"turn","requestId":"request","decision":"cancel"}""");
        var resolved = Assert.Single(events);
        Assert.Equal("request.resolved", resolved.Type);
        Assert.Equal("cancel", resolved.RequestResolved!.Decision);
    }

    [Fact]
    public async Task CanonicalDefaultsAndClosedApprovalSurvivePatchAndPersistedReload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-permission-test-" + Guid.NewGuid().ToString("N"));
        var history = new SessionAppServerControlHistoryService(storeDirectory: directory);
        SessionAppServerControlHistoryService? loaded = null;
        try
        {
            using var patches = history.SubscribeHistoryPatches("session");
            history.Append(new() { SessionId = "session", Provider = "claude", ThreadId = "own", TurnId = "turn", RequestId = "request",
                Type = "request.opened", RequestOpened = new() { RequestType = "tool_approval", Detail = "Bash: marker write" } });
            history.Append(new() { SessionId = "session", Provider = "claude", ThreadId = "own", TurnId = "turn", RequestId = "request",
                Type = "request.resolved", RequestResolved = new() { RequestType = "tool_approval", Decision = "accept" } });
            var settings = new AppServerControlQuickSettingsPayload { PermissionMode = "auto", AcknowledgedFields = ["permissionMode", "planMode"] };
            history.Append(new() { SessionId = "session", Provider = "claude", ThreadId = "own", Type = "quick-settings.updated", QuickSettingsUpdated = settings });
            settings.AcknowledgedFields.Clear();
            AppServerControlHistoryPatch? last = null;
            while (patches.Reader.TryRead(out var patch)) last = patch;
            Assert.Equal("auto", last!.QuickSettings.PermissionMode);
            Assert.Contains("permissionMode", last.QuickSettings.AcknowledgedFields);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (!File.Exists(Path.Combine(directory, "session.json"))) await Task.Delay(20, timeout.Token);
            loaded = new SessionAppServerControlHistoryService(storeDirectory: directory);
            var snapshot = loaded.GetSnapshot("session")!;
            Assert.Equal("auto", snapshot.QuickSettings.PermissionMode);
            Assert.Contains("permissionMode", snapshot.QuickSettings.AcknowledgedFields);
            Assert.Equal("resolved", Assert.Single(snapshot.Requests).State);
        }
        finally
        {
            history.Forget("session"); loaded?.Forget("session");
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
