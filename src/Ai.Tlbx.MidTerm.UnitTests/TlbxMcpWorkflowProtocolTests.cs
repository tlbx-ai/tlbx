using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Mcp;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Ai.Tlbx.MidTerm.Models.Browser;
using System.Diagnostics;
using System.Text;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TlbxMcpWorkflowProtocolTests
{
    [Fact]
    public void PreviewScopeCannotRedirectBrowserButPreservesExplicitCrossDomainTargets()
    {
        var browser = TlbxMcpBrowserTools.CreateTools().Single(tool => tool.ProtocolTool.Name == "browser_fill").ProtocolTool;
        var session = new Tool
        {
            Name = "session_operation",
            InputSchema = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["type"] = "object", ["properties"] = new JsonObject { ["targetSessionId"] = new JsonObject { ["type"] = "string" } }
            }, McpJsonContext.Default.JsonObject)
        };
        var engine = new TlbxMcpWorkflowExecutor([browser, session]);
        var values = JsonNode.Parse("""
            {"targetSessionId":"preview-session","previewUrl":"https://example.invalid","steps":[
              {"tool":"browser_fill","arguments":{"selector":"input","value":"literal"}},
              {"tool":"session_operation","arguments":{"targetSessionId":"other-session"}}
            ]}
            """)!.AsObject();
        var prepared = engine.Prepare(values);
        Assert.Equal("preview-session", prepared[0].Arguments["targetSessionId"]!.GetValue<string>());
        Assert.Equal("other-session", prepared[1].Arguments["targetSessionId"]!.GetValue<string>());
        values["steps"]![0]!["arguments"]!["targetSessionId"] = "other-session";
        Assert.Throws<ArgumentException>(() => engine.Prepare(values));
    }

    [Fact]
    public async Task NativeWorkflowRunsLocalProcessThenRemoteBrowserAndCleansScopeAfterFailure()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "tlbx-native-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        var script = Path.Combine(cwd, OperatingSystem.IsWindows() ? "probe.ps1" : "probe.sh");
        var exe = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe") : "/bin/sh";
        File.WriteAllText(script, OperatingSystem.IsWindows() ? "[Console]::Write('native-local'); Start-Sleep -Milliseconds 150" : "printf native-local; sleep 0.15\n", new UTF8Encoding(OperatingSystem.IsWindows()));
        string[] argv = OperatingSystem.IsWindows() ? ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script] : [script];
        using var leases = new SessionCliContextService(() => [], _ => (1, new DateTime(2026, 10, 30)), TimeProvider.System);
        var lease = leases.CreateHostLease("first001", 100).Split(':')[1];
        var previews = new WebPreviewService(2000);
        var owners = new BrowserPreviewOwnerService();
        var ui = new BrowserUiBridge(new MainBrowserService(), owners);
        var commands = new BrowserCommandService();
        var operations = new BrowserPreviewOperations(commands, previews, ui, new BrowserPreviewRegistry(), owners);
        var mutations = new List<BrowserWsMessage>();
        ui.RegisterListener("native-ui", "desktop", (_, _) => { }, (_, _) => { }, (_, _, _, _) => { },
            (session, name, _, _) => commands.TryRegisterClient("native-preview", session, name, "frame", message =>
            {
                if (message.Command == "fill") mutations.Add(message);
                commands.ReceiveResult(new BrowserWsResult { Id = message.Id, Success = true, Result = "ready" }, "native-preview");
            }, browserId: "desktop", targetRevision: previews.GetPreviewSession(session, name)?.TargetRevision,
                ownershipGeneration: owners.GetGeneration(session, name)), close: (_, _) => commands.UnregisterClient("native-preview"));
        var fail = new EchoTool("intentional_failure", true);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(leases);
        builder.Services.AddSingleton(previews);
        builder.Services.AddSingleton(owners);
        builder.Services.AddSingleton(ui);
        builder.Services.AddSingleton(commands);
        builder.Services.AddSingleton(operations);
        builder.Services.AddMcpServer().WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools([.. TlbxMcpBrowserTools.CreateTools(), fail]);
        await using var app = builder.Build();
        app.Use(async (http, next) => { RequestAccessContext.SetFullUser(http, true); await next(http); });
        TlbxMcpEndpoints.Map(app);
        var ct = TestContext.Current.CancellationToken;
        IsolatedRunState? process = null;
        var verified = false;
        await app.StartAsync(ct);
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            await using var transport = new HttpClientTransport(new()
            {
                Endpoint = new Uri(address + TlbxMcpEndpoints.Path),
                AdditionalHeaders = new Dictionary<string, string>(StringComparer.Ordinal) { [SessionCliContextService.LeaseHeader] = lease }
            });
            await using var upstream = await McpClient.CreateAsync(transport, cancellationToken: ct);
            var json = JsonSerializer.SerializeToElement(new
            {
                previewUrl = "https://example.invalid", steps = new object[]
                {
                    new { tool = "process_run_isolated", arguments = new { executable = exe, arguments = argv, workingDirectory = cwd } },
                    new { tool = "tool_call_to_file", arguments = new { toolName = "browser_fill", path = Path.Combine(cwd, "browser-result.json"),
                        arguments = new { selector = "input", value = new Dictionary<string, object>(StringComparer.Ordinal) { ["$step"] = 0, ["path"] = "/runId" } } } },
                    new { tool = "intentional_failure", arguments = new { value = "stop" } },
                    new { tool = "browser_fill", arguments = new { selector = "input", value = "never" } }
                }
            });
            using var properties = json.EnumerateObject();
            var result = await TlbxMcpNativeWorkflow.InvokeAsync(new CallToolRequestParams
            {
                Name = "workflow_run", Arguments = properties.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal)
            }, upstream, ct);
            var outcome = result.StructuredContent!.Value;
            Assert.True(outcome.GetProperty("completedSteps").GetInt32() == 3, outcome.GetProperty("error").GetString());
            Assert.True(result.IsError);
            var first = outcome.GetProperty("steps")[0].GetProperty("structuredContent");
            var mutation = Assert.Single(mutations);
            Assert.Equal(first.GetProperty("runId").GetString(), mutation.Value);
            Assert.StartsWith("mcp-", mutation.PreviewName, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(cwd, "browser-result.json")));
            Assert.Empty(previews.ListPreviewSessions("first001").Previews);
            var fileArguments = Path.Combine(cwd, "browser-arguments.json");
            await File.WriteAllTextAsync(fileArguments, "{\"selector\":\"input\",\"value\":\"literal from file\"}", ct);
            async Task<CallToolResult> FileWorkflow(bool conflicting)
            {
                if (conflicting) await File.WriteAllTextAsync(fileArguments,
                    "{\"selector\":\"input\",\"value\":\"must not mutate\",\"previewName\":\"foreign\"}", ct);
                var inner = new JsonObject { ["toolName"] = "browser_fill", ["argumentsFile"] = fileArguments };
                var step = conflicting ? new JsonObject
                {
                    ["tool"] = "tool_call_to_file", ["arguments"] = new JsonObject
                    { ["toolName"] = "tool_call_from_file", ["path"] = Path.Combine(cwd, "scope-error.json"), ["arguments"] = inner }
                } : new JsonObject { ["tool"] = "tool_call_from_file", ["arguments"] = inner };
                var payload = JsonSerializer.SerializeToElement(new JsonObject
                    { ["previewUrl"] = "https://example.invalid", ["steps"] = new JsonArray(step) }, McpJsonContext.Default.JsonObject);
                using var values = payload.EnumerateObject();
                return await TlbxMcpNativeWorkflow.InvokeAsync(new CallToolRequestParams
                    { Name = "workflow_run", Arguments = values.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal) }, upstream, ct);
            }
            var fileResult = await FileWorkflow(false);
            Assert.NotEqual(true, fileResult.IsError);
            Assert.Equal("literal from file", mutations.Last().Value);
            Assert.StartsWith("mcp-", mutations.Last().PreviewName, StringComparison.Ordinal);
            var conflict = await FileWorkflow(true);
            Assert.True(conflict.IsError);
            Assert.Equal(2, mutations.Count);
            Assert.True(conflict.StructuredContent!.Value.GetProperty("steps")[0].GetProperty("structuredContent").GetProperty("saved").GetBoolean());
            using (var capturedError = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(cwd, "scope-error.json"), ct)))
                Assert.True(capturedError.RootElement.GetProperty("isError").GetBoolean());
            Assert.Empty(previews.ListPreviewSessions("first001").Previews);
            var run = Path.GetDirectoryName(first.GetProperty("statePath").GetString()!)!;
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(15))
            {
                process = TlbxMcpIsolatedProcessTool.ReadState(run);
                if (process?.CompletedAt is not null && !File.Exists(Path.Combine(run, "launch.json"))) break;
                await Task.Delay(50, ct);
            }
            Assert.NotNull(process);
            Assert.NotNull(process.CompletedAt);
            Assert.Null(process.Error);
            Assert.Equal("native-local", File.ReadAllText(Path.Combine(run, "stdout.log")));
            verified = true;
        }
        finally
        {
            await app.StopAsync(ct);
            commands.UnregisterClient("native-preview");
            ui.UnregisterListener("native-ui");
            if (verified && process is not null)
            {
                foreach (var file in Directory.GetFiles(process.RunDirectory)) File.Delete(file);
                Directory.Delete(process.RunDirectory);
                File.Delete(script);
                File.Delete(Path.Combine(cwd, "browser-result.json"));
                File.Delete(Path.Combine(cwd, "browser-arguments.json"));
                File.Delete(Path.Combine(cwd, "scope-error.json"));
                Directory.Delete(Path.Combine(cwd, ".tlbx", "runs"));
                Directory.Delete(Path.Combine(cwd, ".tlbx"));
                Directory.Delete(cwd);
            }
        }
    }

    [Fact]
    public void ArtifactRetentionExpiresFixedTtlAndDeletesOnlyGeneratedPayloads()
    {
        var http = new DefaultHttpContext();
        http.Items[TlbxMcpEndpoints.CallerIdentityItem] = "actor";
        var accessor = new HttpContextAccessor { HttpContext = http };
        var clock = new ArtifactClock();
        var workspaceOriginal = Path.GetTempFileName();
        string? cacheDirectory = null;
        try
        {
            File.WriteAllText(workspaceOriginal, "workspace original");
            using (var store = new TlbxMcpArtifactStore(accessor, clock, TimeSpan.FromHours(1), 2, 1000))
            {
                cacheDirectory = store.StorageDirectory;
                var first = store.Register([1, 2, 3], "first.png", "image/png");
                var second = store.Register([4, 5, 6], "second.png", "image/png");
                var third = store.Register([7, 8, 9], "third.png", "image/png");
                Assert.Throws<McpProtocolException>(() => store.Read(first.Uri, TestContext.Current.CancellationToken));
                Assert.Equal(2, Directory.GetFiles(cacheDirectory).Length);
                Assert.Single(store.Read(second.Uri, TestContext.Current.CancellationToken).Contents);
                clock.UtcNow += TimeSpan.FromMinutes(59);
                Assert.Single(store.Read(third.Uri, TestContext.Current.CancellationToken).Contents);
                clock.UtcNow += TimeSpan.FromMinutes(1);
                store.Sweep();
                Assert.Empty(Directory.GetFiles(cacheDirectory));
                Assert.Throws<McpProtocolException>(() => store.Read(third.Uri, TestContext.Current.CancellationToken));
                Assert.Equal("workspace original", File.ReadAllText(workspaceOriginal));
            }
            Assert.False(Directory.Exists(cacheDirectory));
            Assert.Equal("workspace original", File.ReadAllText(workspaceOriginal));
        }
        finally { File.Delete(workspaceOriginal); accessor.HttpContext = null; }
    }

    [Fact]
    public void ArtifactByteBudgetEvictsOldestButPreservesOneOversizedOriginalWhole()
    {
        var http = new DefaultHttpContext();
        http.Items[TlbxMcpEndpoints.CallerIdentityItem] = "actor";
        var accessor = new HttpContextAccessor { HttpContext = http };
        using var store = new TlbxMcpArtifactStore(accessor, new ArtifactClock(), TimeSpan.FromHours(1), 10, 5);
        var first = store.Register([1, 2, 3], "one", "application/octet-stream");
        var second = store.Register([4, 5, 6], "two", "application/octet-stream");
        Assert.Throws<McpProtocolException>(() => store.Read(first.Uri, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(store.StorageDirectory));
        byte[] original = [0, 1, 2, 3, 4, 5, 6, 7];
        var oversized = store.Register(original, "oversized", "application/octet-stream");
        Assert.Throws<McpProtocolException>(() => store.Read(second.Uri, TestContext.Current.CancellationToken));
        var content = Assert.IsType<BlobResourceContents>(Assert.Single(store.Read(oversized.Uri, TestContext.Current.CancellationToken).Contents));
        Assert.Equal(original, content.DecodedData.ToArray());
        Assert.Single(Directory.GetFiles(store.StorageDirectory));
        var newest = store.Register([9], "newest", "application/octet-stream");
        Assert.Throws<McpProtocolException>(() => store.Read(oversized.Uri, TestContext.Current.CancellationToken));
        Assert.Single(store.Read(newest.Uri, TestContext.Current.CancellationToken).Contents);
        Assert.Single(Directory.GetFiles(store.StorageDirectory));
        accessor.HttpContext = null;
    }

    private sealed class ArtifactClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    [Fact]
    public void ResourceHandlesAreImmutableAndCallerBoundWithLosslessLargeChunkReads()
    {
        var http = new DefaultHttpContext();
        http.Items[TlbxMcpEndpoints.CallerIdentityItem] = "runtime:one";
        var accessor = new HttpContextAccessor { HttpContext = http };
        using var store = new TlbxMcpArtifactStore(accessor);
        var bytes = new byte[5 * 1024 * 1024 + 17];
        new Random(42).NextBytes(bytes);
        var original = bytes.ToArray();
        var handle = store.Register(bytes, "capture.png", "image/png");
        bytes[0] ^= 255;
        Assert.Equal(original.Length, handle.Size);
        Assert.Throws<McpProtocolException>(() => store.Read(handle.Uri, TestContext.Current.CancellationToken));
        var first = Assert.IsType<BlobResourceContents>(Assert.Single(store.Read(handle.Uri + "?offset=0&length=4194304", TestContext.Current.CancellationToken).Contents));
        var rest = Assert.IsType<BlobResourceContents>(Assert.Single(store.Read(handle.Uri + "?offset=4194304&length=4194304", TestContext.Current.CancellationToken).Contents));
        Assert.Equal(original, first.DecodedData.ToArray().Concat(rest.DecodedData.ToArray()).ToArray());
        http.Items[TlbxMcpEndpoints.CallerIdentityItem] = "runtime:two";
        Assert.Throws<McpProtocolException>(() => store.Read(handle.Uri + "?offset=0&length=1", TestContext.Current.CancellationToken));
        Assert.Throws<McpProtocolException>(() => store.Read("C:\\private\\secret.png", TestContext.Current.CancellationToken));
        accessor.HttpContext = null;
    }

    [Fact]
    public async Task WorkflowPrevalidatesBeforeMutationPreservesLiteralReferencesAndPartialFailures()
    {
        using var leases = new SessionCliContextService(() => [], _ => (1, new DateTime(2026, 10, 30)), TimeProvider.System);
        var token = leases.CreateHostLease("first001", 100).Split(':')[1];
        var echo = new EchoTool("echo", false);
        var fail = new EchoTool("fail", true);
        var workflow = new TlbxMcpWorkflowTool([echo, fail]);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(leases);
        builder.Services.AddMcpServer().WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools([echo, fail, workflow]);
        await using var app = builder.Build();
        app.Use(async (context, next) => { RequestAccessContext.SetFullUser(context, true); await next(context); });
        TlbxMcpEndpoints.Map(app);
        var ct = TestContext.Current.CancellationToken;
        await app.StartAsync(ct);
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            await using var transport = new HttpClientTransport(new()
            {
                Endpoint = new Uri(address + TlbxMcpEndpoints.Path),
                AdditionalHeaders = new Dictionary<string, string>(StringComparer.Ordinal) { [SessionCliContextService.LeaseHeader] = token }
            });
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
            var invalid = await client.CallToolAsync("workflow_run", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["steps"] = new object[] { new { tool = "echo", arguments = new { value = "untouched" } }, new { tool = "unknown" } }
            }, cancellationToken: ct);
            Assert.True(invalid.IsError);
            Assert.Empty(echo.Values);
            var literal = "Grüße '$()\0\n" + new string('a', 70000);
            var result = await client.CallToolAsync("workflow_run", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["steps"] = new object[]
                {
                    new { tool = "echo", arguments = new { value = literal } },
                    new { tool = "echo", arguments = new { value = new Dictionary<string, object>(StringComparer.Ordinal) { ["$step"] = 0, ["path"] = "/value" } } },
                    new { tool = "fail", arguments = new { value = "failure" } },
                    new { tool = "echo", arguments = new { value = "never" } }
                }
            }, cancellationToken: ct);
            Assert.True(result.IsError);
            Assert.True(result.StructuredContent!.Value.GetProperty("completedSteps").GetInt32() == 3,
                "Workflow did not reach the intentional failing third step: " + result.StructuredContent.Value.GetProperty("error").GetString());
            Assert.Equal(new[] { literal, literal }, echo.Values, StringComparer.Ordinal);
            Assert.Single(fail.Values);
        }
        finally { await app.StopAsync(ct); }
    }

    private sealed class EchoTool(string name, bool fail) : McpServerTool
    {
        public List<string> Values { get; } = [];
        public override IReadOnlyList<object> Metadata => [];
        public override Tool ProtocolTool { get; } = new()
        {
            Name = name,
            InputSchema = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["type"] = "object", ["required"] = new JsonArray("value"), ["additionalProperties"] = false,
                ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "string" } }
            }, McpJsonContext.Default.JsonObject)
        };
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            var value = request.Params!.Arguments!["value"].GetString()!;
            Values.Add(value);
            return ValueTask.FromResult(McpApiResult.Structured(new JsonObject { ["value"] = value }, fail));
        }
    }
}
