using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.AgentHost;
using Ai.Tlbx.MidTerm.Common.Protocol;
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
                            result = new { thread = new { id = "canonical-A", turns = Array.Empty<object>() } };
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
        var runtime = new CodexAppServerControlAgentRuntime(e =>
        {
            events.Enqueue(e);
            scope.Observe(e);
            if (e.Type == "turn.completed" && e.TurnId == "own-turn") ownCompleted.TrySetResult();
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
