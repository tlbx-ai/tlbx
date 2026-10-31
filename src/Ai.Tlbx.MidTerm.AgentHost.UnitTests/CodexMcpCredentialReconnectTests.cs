using System.Net;
using System.Net.WebSockets;
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

public sealed class CodexMcpCredentialReconnectTests
{
    [Fact]
    public void PreparingReplacementRotatesBeforeCredentialsAndReconnectPreservesEpoch()
    {
        var scope = new AppServerControlMcpRuntimeScope();
        scope.Observe(new AppServerControlProviderEvent { ThreadState = new() { ProviderThreadId = "first" } });
        scope.Observe(new AppServerControlProviderEvent { SessionState = new() { State = "ready" } });
        var first = scope.Identity;
        scope.Prepare("first", startsNewThread: false);
        scope.Observe(new AppServerControlProviderEvent { SessionState = new() { State = "starting" } });
        Assert.Equal(first, scope.Identity);
        scope.Prepare("replacement", startsNewThread: false);
        var replacement = scope.Identity;
        Assert.NotEqual(first, replacement, StringComparer.Ordinal);
        scope.Observe(new AppServerControlProviderEvent { SessionState = new() { State = "starting" } });
        scope.Observe(new AppServerControlProviderEvent { ThreadState = new() { ProviderThreadId = "replacement" } });
        Assert.Equal(replacement, scope.Identity);
        scope.Observe(new AppServerControlProviderEvent { ThreadState = new() { ProviderThreadId = "unexpected" } });
        Assert.NotEqual(replacement, scope.Identity, StringComparer.Ordinal);
    }

    [Fact]
    public void ActualBackendExitRevokesScopeWhileTransportErrorPreservesIt()
    {
        var scope = new AppServerControlMcpRuntimeScope();
        scope.Observe(new AppServerControlProviderEvent { SessionState = new() { State = "ready" } });
        var live = scope.Identity;
        scope.Observe(new AppServerControlProviderEvent { Type = "runtime.error", RuntimeMessage = new() { Message = "WebSocket transport disconnected" } });
        Assert.Equal(live, scope.Identity);
        scope.Observe(new AppServerControlProviderEvent { Type = "session.exited", RuntimeMessage = new() { Message = "Owned backend exited" } });
        Assert.NotEqual(live, scope.Identity, StringComparer.Ordinal);
    }

    [Fact]
    public async Task PrivateCredentialRefreshWaitsForTransientReaderAndRejectsCrossEpochSecrets()
    {
        AppServerControlMcpServer Source(string? epoch, string secret) => new()
        {
            ScopeIdentity = epoch, Command = "fixture-mt", Arguments = ["mcp", "--stdio"],
            EnvironmentVariables = new(StringComparer.Ordinal) { ["MT_TOKEN"] = secret }
        };
        using var credentials = new AppServerControlMcpCredentials(Source("first", "old-secret"));
        if (OperatingSystem.IsWindows())
        {
            Task refresh;
            using (var reader = new FileStream(credentials.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                refresh = Task.Run(() => credentials.Refresh(Source("first", "new-secret")), TestContext.Current.CancellationToken);
                await Task.Delay(100, TestContext.Current.CancellationToken);
                Assert.False(refresh.IsCompleted);
            }
            await refresh;
        }
        else credentials.Refresh(Source("first", "new-secret"));
        Assert.Throws<InvalidOperationException>(() => credentials.Refresh(Source("second", "other-scope-secret")));
        Assert.Throws<InvalidOperationException>(() => credentials.Refresh(Source(null, "unscoped-secret")));
        using var document = JsonDocument.Parse(File.ReadAllBytes(credentials.FilePath));
        Assert.Equal("new-secret", document.RootElement.GetProperty("MT_TOKEN").GetString());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(credentials.FilePath)!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadedCanonicalThreadCanRestartItsNativeMcpAfterTransportReconnect(bool replaceScope)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WebSocket? firstSocket = null;
        string? registeredPath = null;
        var resumes = 0;
        var nativeProbes = 0;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            firstSocket ??= socket;
            var buffer = new byte[64 * 1024];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var payload = new MemoryStream();
                    ValueWebSocketReceiveResult received;
                    do
                    {
                        received = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                        if (received.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", ct);
                            return;
                        }
                        await payload.WriteAsync(buffer.AsMemory(0, received.Count), ct);
                    } while (!received.EndOfMessage);
                    using var message = JsonDocument.Parse(payload.ToArray());
                    var root = message.RootElement;
                    if (!root.TryGetProperty("id", out var id)) continue;
                    var method = root.GetProperty("method").GetString();
                    object result;
                    switch (method)
                    {
                        case "thread/resume":
                            Interlocked.Increment(ref resumes);
                            var args = root.GetProperty("params").GetProperty("config").GetProperty("mcp_servers").GetProperty("tlbx").GetProperty("args");
                            // A loaded canonical thread keeps its original native configuration.
                            registeredPath ??= args[args.GetArrayLength() - 1].GetString();
                            result = new { thread = new { id = "canonical-thread", status = new { type = "idle" } } };
                            break;
                        case "mcpServer/tool/call":
                            Interlocked.Increment(ref nativeProbes);
                            // The already running MCP child has cached credentials, so this
                            // succeeds even if its registered restart file was deleted.
                            result = new { isError = false, structuredContent = new { sessionId = "owned-session" } };
                            break;
                        case "thread/read":
                            result = new { thread = new { id = "canonical-thread", status = new { type = "idle" }, turns = Array.Empty<object>() } };
                            break;
                        case "thread/unsubscribe":
                            registeredPath = null;
                            result = new { status = "unsubscribed" };
                            break;
                        case "model/list":
                        case "thread/turns/list":
                            result = new { data = Array.Empty<object>() };
                            break;
                        default:
                            result = new { };
                            break;
                    }
                    var response = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = id.Clone(), result });
                    await socket.SendAsync(response.AsMemory(), WebSocketMessageType.Text, true, ct);
                }
            }
            catch (WebSocketException) { }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        });
        await app.StartAsync(ct);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var endpoint = "ws" + address[4..];
        var runtime = new CodexAppServerControlAgentRuntime(e =>
        {
            if (e.Type == "runtime.error") disconnected.TrySetResult();
        });
        try
        {
            await runtime.ExecuteAsync(Attach(endpoint, "bootstrap-before-reconnect", "trusted-epoch-one"), ct);
            Assert.NotNull(registeredPath);
            Assert.True(File.Exists(registeredPath));
            var originalPath = registeredPath;
            firstSocket!.Abort();
            await disconnected.Task.WaitAsync(ct);
            await runtime.ExecuteAsync(Attach(endpoint, "bootstrap-after-reconnect", replaceScope ? "trusted-epoch-two" : "trusted-epoch-one"), ct);
            Assert.Equal(replaceScope ? 3 : 2, resumes);
            Assert.Equal(2, nativeProbes);
            if (replaceScope)
            {
                Assert.NotEqual(originalPath, registeredPath, StringComparer.Ordinal);
                Assert.False(File.Exists(originalPath));
            }
            else Assert.Equal(originalPath, registeredPath);
            // Reproduce a native MCP child restart by reading precisely the file
            // that the still-loaded canonical thread originally registered.
            Assert.True(File.Exists(registeredPath), "Reconnect deleted the loaded thread's native MCP credential file.");
            using var credentials = JsonDocument.Parse(File.ReadAllBytes(registeredPath));
            Assert.Equal("bootstrap-after-reconnect", credentials.RootElement.GetProperty("TLBX_MCP_BOOTSTRAP").GetString());
        }
        finally
        {
            await runtime.DisposeAsync();
            await app.StopAsync(CancellationToken.None);
        }
        Assert.False(File.Exists(registeredPath));
    }

    private static AppServerControlHostCommandEnvelope Attach(string endpoint, string bootstrap, string scope) => new()
    {
        Type = "runtime.attach", CommandId = Guid.NewGuid().ToString("N"), SessionId = "owned-session",
        AttachRuntime = new AppServerControlAttachRuntimeRequest
        {
            Provider = "codex", WorkingDirectory = Path.GetTempPath(), ResumeThreadId = "canonical-thread",
            AttachPoint = new SessionAgentAttachPoint
            {
                Provider = "codex", Endpoint = endpoint, SharedRuntime = true,
                TransportKind = SessionAgentAttachPoint.CodexAppServerWebSocketTransport
            },
            McpServer = new AppServerControlMcpServer
            {
                ScopeIdentity = scope,
                Command = Path.Combine(Path.GetTempPath(), "mt-fixture.exe"), Arguments = ["mcp", "--stdio"],
                EnvironmentVariables = new(StringComparer.Ordinal)
                {
                    ["MT_BASE_URL"] = "https://localhost:2000", ["TLBX_MCP_BOOTSTRAP"] = bootstrap
                }
            }
        }
    };
}
