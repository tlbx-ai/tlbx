using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.AgentHost;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ai.Tlbx.MidTerm.AgentHost.UnitTests;

public sealed class CodexNotificationScopeTests
{
    [Theory]
    [InlineData("manual", "on-request", "workspaceWrite")]
    [InlineData("auto", "never", "dangerFullAccess")]
    public async Task BroadcastSiblingNotificationsCannotChangeCanonicalThreadStateOrNextTurnTarget(
        string permissionMode, string approvalPolicy, string sandboxType)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var events = new ConcurrentQueue<AppServerControlProviderEvent>();
        var initialBatches = new List<IReadOnlyList<AppServerControlProviderEvent>>();
        var ownCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scope = new AppServerControlMcpRuntimeScope();
        var nextTarget = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var turnParameters = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeCount = 0;
        WebSocket? connected = null;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
#pragma warning disable IDISP001, IDISP003 // Borrowed observation reference; this handler's using scope owns and disposes every accepted socket.
            connected = socket;
#pragma warning restore IDISP001, IDISP003
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var message = await ReadAsync(socket, ct);
                    if (message is null) return;
                    var root = message.RootElement;
                    if (!root.TryGetProperty("id", out var id)) continue;
                    var method = root.GetProperty("method").GetString();
                    object result;
                    switch (method)
                    {
                        case "thread/resume":
                            Interlocked.Increment(ref resumeCount);
                            // A sibling can broadcast while this runtime has no correlated owner yet.
                            await SendAsync(socket, """{"method":"thread/started","params":{"thread":{"id":"sibling-B"}}}""", ct);
                            result = new { thread = new { id = "canonical-A", status = new { type = "idle" } } };
                            break;
                        case "thread/read":
                            result = new { thread = new { id = "canonical-A", turns = new[]
                            {
                                new { id = "history-turn", status = "completed", items = new object[]
                                {
                                    new { id = "history-user", type = "userMessage", content = new[] { new { type = "inputText", text = "Historical question" } } },
                                    new { id = "history-tool", type = "commandExecution", command = "echo historic", aggregatedOutput = "historic output", exitCode = 0 },
                                    new { id = "history-assistant", type = "agentMessage", text = "Historical answer" }
                                } }
                            } } };
                            break;
                        case "model/list":
                        case "thread/turns/list":
                            result = new { data = Array.Empty<object>() };
                            break;
                        case "turn/start":
                            turnParameters.TrySetResult(root.GetProperty("params").Clone());
                            nextTarget.TrySetResult(root.GetProperty("params").GetProperty("threadId").GetString()!);
                            result = new { turn = new { id = "next-own-turn" } };
                            break;
                        default:
                            result = new { };
                            break;
                    }
                    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { id = id.Clone(), result }).AsMemory(), WebSocketMessageType.Text, true, ct);
                }
            }
            catch (WebSocketException) { }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        });
        await app.StartAsync(ct);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        void Observe(AppServerControlProviderEvent e)
        {
            events.Enqueue(e);
            scope.Observe(e);
            if (e.Type == "turn.completed" && e.TurnId == "own-turn") ownCompleted.TrySetResult();
        }
        var runtime = new CodexAppServerControlAgentRuntime(Observe, batch =>
        {
            initialBatches.Add(batch);
            foreach (var e in batch) Observe(e);
        });
        try
        {
            await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
            {
                Type = "runtime.attach", CommandId = "attach", SessionId = "owned-session",
                AttachRuntime = new()
                {
                    Provider = "codex", WorkingDirectory = Path.GetTempPath(), ResumeThreadId = "canonical-A",
                    AttachPoint = new SessionAgentAttachPoint
                    {
                        Provider = "codex", Endpoint = "ws" + address[4..], SharedRuntime = true,
                        TransportKind = SessionAgentAttachPoint.CodexAppServerWebSocketTransport
                    }
                }
            }, ct);
            var initialBatch = Assert.Single(initialBatches);
            var replay = initialBatch.Where(e => e.TurnId == "history-turn").ToArray();
            Assert.Equal(new[] { "turn.started", "item.completed", "item.completed", "content.delta", "item.completed", "turn.completed" },
                replay.Select(e => e.Type), StringComparer.Ordinal);
            Assert.True(replay[0].TurnStarted!.IsReplay);
            Assert.Equal("Historical answer", replay[3].ContentDelta!.Delta);
            var originalScope = scope.Identity;
            foreach (var notification in new[]
            {
                """{"method":"thread/started","params":{"thread":{"id":"sibling-B"}}}""",
                """{"method":"thread/status/changed","params":{"threadId":"sibling-B","status":{"type":"active"}}}""",
                """{"method":"thread/tokenUsage/updated","params":{"threadId":"sibling-B","tokenUsage":{"total":{"inputTokens":999},"modelContextWindow":9999}}}""",
                """{"method":"turn/started","params":{"threadId":"sibling-B","turn":{"id":"sibling-turn"}}}""",
                """{"method":"item/agentMessage/delta","params":{"threadId":"sibling-B","turnId":"sibling-turn","itemId":"sibling-item","delta":"foreign history"}}""",
                """{"method":"turn/completed","params":{"threadId":"sibling-B","turn":{"id":"sibling-turn","status":"completed"}}}""",
                """{"method":"thread/started","params":{"thread":{"id":"canonical-A"}}}""",
                """{"method":"thread/status/changed","params":{"threadId":"canonical-A","status":{"type":"idle"}}}""",
                """{"method":"turn/started","params":{"threadId":"canonical-A","turn":{"id":"own-turn"}}}""",
                // A parent-owned tool can legitimately refer to a child thread in its item payload.
                """{"method":"item/agentMessage/delta","params":{"threadId":"canonical-A","turnId":"own-turn","itemId":"own-item","delta":"own history","item":{"childThreadId":"sibling-B"}}}""",
                """{"method":"turn/completed","params":{"threadId":"canonical-A","turn":{"id":"own-turn","status":"completed"}}}"""
            }) await SendAsync(connected!, notification, ct);
            await ownCompleted.Task.WaitAsync(ct);
            Assert.Single(initialBatches); // Post-attach events use the ordinary live callback.
            Assert.Equal(originalScope, scope.Identity);
            Assert.All(events.Where(e => e.ThreadState is not null), e => Assert.Equal("canonical-A", e.ThreadState!.ProviderThreadId));
            Assert.DoesNotContain(events, e => e.TurnId == "sibling-turn" || e.ItemId == "sibling-item" || e.AgentMetadataUpdated?.Usage is not null);
            Assert.Contains(events, e => e.ContentDelta?.Delta == "own history");
            Assert.Contains(events, e => e.Type == "turn.started" && e.TurnId == "own-turn");
            var outcome = await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
            {
                Type = "turn.start", CommandId = "next", SessionId = "owned-session", StartTurn = new() { Text = "Fake protocol turn; no model exists.", PermissionMode = permissionMode }
            }, ct);
            Assert.Equal("canonical-A", await nextTarget.Task.WaitAsync(ct));
            Assert.Equal("canonical-A", outcome.Result.TurnStarted!.ThreadId);
            var parameters = await turnParameters.Task.WaitAsync(ct);
            Assert.Equal(approvalPolicy, parameters.GetProperty("approvalPolicy").GetString());
            Assert.Equal(sandboxType, parameters.GetProperty("sandboxPolicy").GetProperty("type").GetString());
            Assert.Equal(1, resumeCount); // Only the initial attach; permissions must not reload the thread/MCP.
        }
        finally
        {
            await runtime.DisposeAsync();
            await app.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("applied")]
    [InlineData("targetUnavailable")]
    public async Task ConfigureUsesExactGuardsActualThreadReadbackAndRestrictedLivePatch(string liveStatus)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var methods = new ConcurrentQueue<string>();
        var starts = new ConcurrentQueue<JsonElement>();
        var liveParameters = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var threadParameters = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var message = await ReadAsync(socket, ct);
                    if (message is null) return;
                    var root = message.RootElement;
                    if (!root.TryGetProperty("id", out var id)) continue;
                    var method = root.GetProperty("method").GetString()!;
                    methods.Enqueue(method);
                    object result;
                    switch (method)
                    {
                        case "initialize":
                            await SendAsync(socket, """{"method":"mcpServer/startupStatus/updated","params":{"name":"tlbx","status":"ready"}}""", ct);
                            result = new { };
                            break;
                        case "thread/start":
                        case "thread/resume":
                            result = new { model = "opened-native-model", reasoningEffort = "high",
                                thread = new { id = "canonical-A", status = new { type = "idle" } } };
                            break;
                        case "thread/read":
                            result = new { thread = new { id = "canonical-A", turns = Array.Empty<object>() } };
                            break;
                        case "model/list":
                        case "thread/turns/list":
                            result = new { data = Array.Empty<object>() };
                            break;
                        case "turn/start":
                            starts.Enqueue(root.GetProperty("params").Clone());
                            result = new { turn = new { id = "own-turn" } };
                            break;
                        case "thread/settings/update":
                            threadParameters.TrySetResult(root.GetProperty("params").Clone());
                            await SendAsync(socket, """{"method":"thread/settings/updated","params":{"threadId":"sibling-B","threadSettings":{"model":"foreign-model","effort":"low"}}}""", ct);
                            await SendAsync(socket, """{"method":"thread/settings/updated","params":{"threadId":"canonical-A","threadSettings":{"model":"provider-normalized-model","effort":"medium","serviceTier":null,"collaborationMode":{"mode":"plan"},"approvalPolicy":"on-request","sandboxPolicy":{"type":"workspaceWrite"}}}}""", ct);
                            result = new { };
                            break;
                        case "turn/settings/update":
                            liveParameters.TrySetResult(root.GetProperty("params").Clone());
                            await SendAsync(socket, """{"method":"turn/completed","params":{"threadId":"canonical-A","turn":{"id":"own-turn","status":"completed"}}}""", ct);
                            result = new { status = liveStatus };
                            break;
                        default: result = new { }; break;
                    }
                    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { id = id.Clone(), result }).AsMemory(), WebSocketMessageType.Text, true, ct);
                }
            }
            catch (WebSocketException) { }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        });
        await app.StartAsync(ct);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var historyDirectory = Path.Combine(Path.GetTempPath(), "tlbx-ready-controls", Guid.NewGuid().ToString("N"));
        var history = new SessionAppServerControlHistoryService(storeDirectory: historyDirectory, enableScreenLogging: false);
        var runtime = new CodexAppServerControlAgentRuntime(history.Append, history.AppendInitialReplay);
        try
        {
            await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
            {
                Type = "runtime.attach", CommandId = "attach", SessionId = "s",
                AttachRuntime = new()
                {
                    Provider = "codex", WorkingDirectory = Path.GetTempPath(),
                    AttachPoint = new SessionAgentAttachPoint
                    {
                        Provider = "codex", Endpoint = "ws" + address[4..], SharedRuntime = true,
                        TransportKind = SessionAgentAttachPoint.CodexAppServerWebSocketTransport
                    }
                }
            }, ct);
            var ready = history.GetSnapshotWindow("s")!;
            Assert.Equal("ready", ready.Session.State);
            Assert.Equal("canonical-A", ready.Thread.ThreadId);
            Assert.Equal("opened-native-model", ready.QuickSettings.Model);
            Assert.Equal("high", ready.QuickSettings.Effort);
            Assert.DoesNotContain("turn/start", methods, StringComparer.Ordinal);
            using var idlePatch = JsonDocument.Parse("""{"model":"requested-model","planMode":"on"}""");
            using var idleProperties = idlePatch.RootElement.EnumerateObject();
            var idleConfigured = await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
            {
                Type = "runtime.configure", CommandId = "configure-before-prompt", SessionId = "s",
                Configure = new()
                {
                    ExpectedThreadId = ready.Thread.ThreadId, ExpectedTurnId = null,
                    Settings = idleProperties.ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal)
                }
            }, ct);
            Assert.Equal("applied", idleConfigured.Result.Configured!.ThreadStatus);
            Assert.Equal("canonical-A", history.GetSnapshotWindow("s")!.Thread.ThreadId);
            Assert.DoesNotContain("turn/start", methods, StringComparer.Ordinal);
            await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
            {
                Type = "turn.start", CommandId = "start", SessionId = "s",
                StartTurn = new() { Text = "Fake protocol turn; no inference." }
            }, ct);
            using var patchDocument = JsonDocument.Parse("""{"model":"requested-model","effort":"high","planMode":"on","permissionMode":"manual","serviceTier":null}""");
            using var patchProperties = patchDocument.RootElement.EnumerateObject();
            var patch = patchProperties.ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
            foreach (var guard in new[] { ("sibling-B", "own-turn"), ("canonical-A", "stale-turn"), ("canonical-A", (string?)null) })
            {
                var rejected = await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
                {
                    Type = "runtime.configure", CommandId = Guid.NewGuid().ToString("N"), SessionId = "s",
                    Configure = new() { ExpectedThreadId = guard.Item1, ExpectedTurnId = guard.Item2, Settings = patch }
                }, ct);
                Assert.Equal("targetUnavailable", rejected.Result.Configured!.Status);
            }
            Assert.Equal(1, methods.Count(method => method == "thread/settings/update"));
            var outcome = await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
            {
                Type = "runtime.configure", CommandId = "configure", SessionId = "s",
                Configure = new() { ExpectedThreadId = "canonical-A", ExpectedTurnId = "own-turn", Settings = patch }
            }, ct);
            var receipt = outcome.Result.Configured!;
            Assert.Equal("applied", receipt.ThreadStatus);
            Assert.Equal(liveStatus, receipt.TurnStatus);
            Assert.Equal("partial", receipt.Status); // Plan/permissions are future-turn defaults.
            Assert.Equal("provider-normalized-model", receipt.EffectiveSettings["model"].GetString());
            Assert.Equal("medium", receipt.EffectiveSettings["effort"].GetString());
            Assert.Contains("collaborationMode", receipt.DeferredFields, StringComparer.Ordinal);
            Assert.Equal(liveStatus == "applied", receipt.ConfirmedSettings.ContainsKey("model"));
            var live = await liveParameters.Task.WaitAsync(ct);
            Assert.Equal("canonical-A", live.GetProperty("threadId").GetString());
            Assert.Equal("own-turn", live.GetProperty("turnId").GetString());
            Assert.Equal(JsonValueKind.Null, live.GetProperty("serviceTier").ValueKind);
            Assert.False(live.TryGetProperty("collaborationMode", out _));
            Assert.False(live.TryGetProperty("approvalPolicy", out _));
            Assert.False(live.TryGetProperty("sandboxPolicy", out _));
            Assert.False(live.TryGetProperty("permissions", out _));
            Assert.False(live.TryGetProperty("summary", out _)); // Omission stays omitted.
            Assert.Equal("plan", (await threadParameters.Task.WaitAsync(ct)).GetProperty("collaborationMode").GetProperty("mode").GetString());
            Assert.Equal(1, methods.Count(method => method == "turn/start"));
            Assert.DoesNotContain("turn/interrupt", methods, StringComparer.Ordinal);
            Assert.DoesNotContain("turn/steer", methods, StringComparer.Ordinal);
            await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
            {
                Type = "turn.start", CommandId = "next-start", SessionId = "s",
                StartTurn = new() { Text = "Continue with configured defaults." }
            }, ct);
            var subsequent = starts.Last();
            foreach (var nativeDefault in new[] { "model", "effort", "collaborationMode", "serviceTier", "approvalPolicy", "sandboxPolicy" })
                Assert.False(subsequent.TryGetProperty(nativeDefault, out _));
        }
        finally
        {
            await runtime.DisposeAsync();
            await app.StopAsync(CancellationToken.None);
            history.Forget("s");
            if (Directory.Exists(historyDirectory)) Directory.Delete(historyDirectory, recursive: true);
        }
    }

    private static ValueTask SendAsync(WebSocket socket, string json, CancellationToken ct)
        => socket.SendAsync(Encoding.UTF8.GetBytes(json).AsMemory(), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonDocument?> ReadAsync(WebSocket socket, CancellationToken ct)
    {
        using var payload = new MemoryStream();
        var buffer = new byte[64 * 1024];
        ValueWebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", ct);
                return null;
            }
            await payload.WriteAsync(buffer.AsMemory(0, received.Count), ct);
        } while (!received.EndOfMessage);
        return JsonDocument.Parse(payload.ToArray());
    }
}
