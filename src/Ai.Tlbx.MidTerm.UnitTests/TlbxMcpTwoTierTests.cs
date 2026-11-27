using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Services.Mcp;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TlbxMcpTwoTierTests
{
    [Fact]
    public async Task Tier1BaseContextStaysUnder5000TokensAndListsAllSubtoolsInDiscoveryCatalog()
    {
        using var leases = CreateLeases();
        var token = leases.CreateHostLease("tier0001", 401).Split(':')[1];
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        TlbxMcpEndpoints.AddServices(builder.Services);
        builder.Services.AddSingleton(leases);
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            Ai.Tlbx.MidTerm.Services.RequestAccessContext.SetFullUser(context, true);
            await next(context);
        });
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
            var tools = await client.ListToolsAsync(cancellationToken: ct);

            // Compact Tier-1 surface: must include discovery, call gateway, workflow, and high-frequency orchestration tools.
            var names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var expected in new[]
            {
                TlbxMcpDiscoverTool.ToolName,
                TlbxMcpCallTool.ToolName,
                "workflow_run",
                "context_get",
                "sessions_list",
                "session_set_topic",
                "repos_list",
                "repo_bind",
                "agent_session_create",
                "agent_history",
                "agent_turn",
                "terminal_prompt"
            })
            {
                Assert.Contains(expected, names);
            }
            Assert.True(tools.Count <= 16, $"Tier-1 tool count {tools.Count} should stay compact (<= 16).");

            // Measure serialized Tier-1 schema + ServerInstructions UTF-8 payload:
            // < 14,500 bytes guarantees < 5,000 tokens even at conservative 3.0 chars/token.
            var listJson = JsonSerializer.Serialize(
                new ListToolsResult { Tools = tools.Select(t => t.ProtocolTool).ToList() },
                McpClientJsonContext.Default.ListToolsResult);
            var totalBytes = Encoding.UTF8.GetByteCount(listJson) + Encoding.UTF8.GetByteCount(client.ServerInstructions ?? string.Empty);
            Assert.True(totalBytes < 14_500, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Tier-1 MCP base context is {totalBytes} bytes (~{totalBytes / 3.2:F0} tokens); must stay well under 5,000 tokens (< 14,500 bytes)."));

            // Verify dense domain capability list in tools_discover covers EVERY registered subtool.
            var discoverDescription = tools.Single(t => t.Name == TlbxMcpDiscoverTool.ToolName).Description;
            var catalog = app.Services.GetRequiredService<TlbxMcpCatalog>();
            Assert.True(catalog.Subtools.Count >= 130, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Expected full subtool catalog (>= 130), got {catalog.Subtools.Count}."));
            foreach (var entry in catalog.Subtools)
            {
                Assert.Contains(entry.Name, discoverDescription, StringComparison.Ordinal);
            }

            // Tier-2 discovery by domain, query, explicit tools, and unfiltered summary mode.
            var byQuery = await client.CallToolAsync(TlbxMcpDiscoverTool.ToolName, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["query"] = "fork"
            }, cancellationToken: ct);
            Assert.NotEqual(true, byQuery.IsError);
            using var queryToolsEnum = byQuery.StructuredContent!.Value.GetProperty("tools").EnumerateArray();
            var forkEntry = queryToolsEnum.Single(e => e.GetProperty("name").GetString() == "agent_session_fork");
            Assert.Equal("agent", forkEntry.GetProperty("domain").GetString());
            Assert.True(forkEntry.TryGetProperty("inputSchema", out var forkSchema) && forkSchema.GetProperty("properties").TryGetProperty("cursorId", out _));

            var byNames = await client.CallToolAsync(TlbxMcpDiscoverTool.ToolName, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tools"] = new[] { "browser_click", "agent_configure", "does_not_exist" }
            }, cancellationToken: ct);
            Assert.NotEqual(true, byNames.IsError);
            Assert.Equal(2, byNames.StructuredContent!.Value.GetProperty("matchedCount").GetInt32());
            using var unknownEnum = byNames.StructuredContent.Value.GetProperty("unknownTools").EnumerateArray();
            Assert.Contains("does_not_exist", unknownEnum.Select(e => e.GetString()), StringComparer.Ordinal);

            var unfiltered = await client.CallToolAsync(TlbxMcpDiscoverTool.ToolName, cancellationToken: ct);
            Assert.NotEqual(true, unfiltered.IsError);
            Assert.False(unfiltered.StructuredContent!.Value.GetProperty("includeSchemas").GetBoolean());
            Assert.Equal(catalog.Subtools.Count, unfiltered.StructuredContent.Value.GetProperty("matchedCount").GetInt32());
            Assert.False(unfiltered.StructuredContent.Value.GetProperty("tools")[0].TryGetProperty("inputSchema", out _));

            // Invoke a Tier-2 subtool via tool_call and verify caller identity + validation.
            var exported = await client.CallToolAsync(TlbxMcpCallTool.ToolName, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tool"] = "context_export",
                ["arguments"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["format"] = "text", ["previewName"] = "p2" }
            }, cancellationToken: ct);
            Assert.NotEqual(true, exported.IsError);
            Assert.Equal("tier0001", exported.StructuredContent!.Value.GetProperty("sessionId").GetString());
            Assert.Equal("p2", exported.StructuredContent.Value.GetProperty("previewName").GetString());

            // Recursive tool_call and unknown subtool are cleanly rejected.
            var recursive = await client.CallToolAsync(TlbxMcpCallTool.ToolName, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tool"] = TlbxMcpCallTool.ToolName
            }, cancellationToken: ct);
            Assert.True(recursive.IsError);

            var unknown = await client.CallToolAsync(TlbxMcpCallTool.ToolName, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tool"] = "no_such_subtool"
            }, cancellationToken: ct);
            Assert.True(unknown.IsError);
        }
        finally
        {
            await app.StopAsync(ct);
        }
    }

    [Fact]
    public async Task NativeStdioProxyPreservesTwoTierCompactListAndRoutesLocalSubtoolsViaToolCall()
    {
        using var leases = CreateLeases();
        var token = leases.CreateHostLease("stdio001", 402).Split(':')[1];
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        TlbxMcpEndpoints.AddServices(builder.Services);
        builder.Services.AddSingleton(leases);
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            Ai.Tlbx.MidTerm.Services.RequestAccessContext.SetFullUser(context, true);
            await next(context);
        });
        TlbxMcpEndpoints.Map(app);
        var ct = TestContext.Current.CancellationToken;
        await app.StartAsync(ct);
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            await using var upstreamTransport = new HttpClientTransport(new()
            {
                Endpoint = new Uri(address + TlbxMcpEndpoints.Path),
                AdditionalHeaders = new Dictionary<string, string>(StringComparer.Ordinal) { [SessionCliContextService.LeaseHeader] = token }
            });
            await using var upstream = await McpClient.CreateAsync(upstreamTransport, cancellationToken: ct);

            var clientToProxy = new Pipe();
            var proxyToClient = new Pipe();
            await using var proxyIn = clientToProxy.Reader.AsStream();
            await using var proxyOut = proxyToClient.Writer.AsStream();
            await using var clientOut = clientToProxy.Writer.AsStream();
            await using var clientIn = proxyToClient.Reader.AsStream();
            var services = new ServiceCollection();
            services.AddLogging();
            var proxyBuilder = services.AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "TLBX native MCP adapter", Version = "test" };
                o.ServerInstructions = upstream.ServerInstructions;
            }).WithStreamServerTransport(proxyIn, proxyOut);
            TlbxMcpStdioCommand.ConfigureForwarding(proxyBuilder, upstream);
            await using var provider = services.BuildServiceProvider();
            var server = provider.GetRequiredService<McpServer>();
            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var serverTask = server.RunAsync(runCts.Token);
            try
            {
                await using var client = await McpClient.CreateAsync(
                    new StreamClientTransport(clientOut, clientIn),
                    cancellationToken: ct);
                var tools = await client.ListToolsAsync(cancellationToken: ct);
                Assert.True(tools.Count <= 16);
                Assert.Contains(tools, t => t.Name == TlbxMcpDiscoverTool.ToolName);
                Assert.DoesNotContain(tools, t => t.Name == "process_run_isolated");

                // tools_discover patches native schemas (e.g., update_apply includes expectedVersion).
                var discovered = await client.CallToolAsync(TlbxMcpDiscoverTool.ToolName, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["tools"] = new[] { "update_apply", "process_run_isolated" }
                }, cancellationToken: ct);
                Assert.NotEqual(true, discovered.IsError);
                using var toolsEnum = discovered.StructuredContent!.Value.GetProperty("tools").EnumerateArray();
                var map = toolsEnum.ToDictionary(e => e.GetProperty("name").GetString()!, StringComparer.Ordinal);
                Assert.True(map["update_apply"].GetProperty("inputSchema").GetProperty("properties").TryGetProperty("expectedVersion", out _));
                Assert.True(map["process_run_isolated"].GetProperty("inputSchema").GetProperty("properties").TryGetProperty("executable", out _));

                // Calling context_get via tool_call goes through native freshness decorator.
                var ctx = await client.CallToolAsync(TlbxMcpCallTool.ToolName, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["tool"] = "context_get"
                }, cancellationToken: ct);
                Assert.NotEqual(true, ctx.IsError);
                Assert.Equal("stdio001", ctx.StructuredContent!.Value.GetProperty("sessionId").GetString());
                Assert.True(ctx.StructuredContent.Value.TryGetProperty("adapterVersion", out _));

                // Calling a local tool (process_wait with invalid statePath) runs natively instead of returning TLBX_MCP_CLIENT_LOCAL_REQUIRED.
                var waitErr = await client.CallToolAsync(TlbxMcpCallTool.ToolName, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["tool"] = TlbxMcpProcessWaitTool.ToolName,
                    ["arguments"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["statePath"] = Path.Combine(Path.GetTempPath(), "nonexistent-tlbx-state-" + Guid.NewGuid().ToString("N") + ".json"), ["timeoutSeconds"] = 0 }
                }, cancellationToken: ct);
                Assert.True(waitErr.IsError);
                var errText = waitErr.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "";
                Assert.DoesNotContain("TLBX_MCP_CLIENT_LOCAL_REQUIRED", errText, StringComparison.Ordinal);
            }
            finally
            {
                await runCts.CancelAsync();
                try { await serverTask; } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            await app.StopAsync(ct);
        }
    }

    private static SessionCliContextService CreateLeases()
    {
        return new SessionCliContextService(() => [], _ => (1, new DateTime(2026, 10, 30)), TimeProvider.System);
    }
}
