using System.Net;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Services.Mcp;
using Ai.Tlbx.MidTerm.Services.Connect;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TlbxMcpProtocolTests
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task SharedDiscoveryCanHandshakeWithRealSdkButCannotExecuteToolsOrEnumerateArtifacts()
    {
        var started = new DateTime(2026, 10, 30);
        var runtime = new TlbxMcpClientContextService(() => [],
            pid => pid switch { 100 => (200, started.AddMinutes(1)), 200 => (1, started), _ => null },
            () => System.Text.Encoding.UTF8.GetBytes("discovery-fixture-key-with-32bytes"), TimeProvider.System,
            pid => pid == 200);
        var credential = runtime.Bind(100);
        Assert.Equal("discovery", credential.Mode);
        Assert.Null(credential.SessionId);
        using var leases = CreateLeases();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        TlbxMcpEndpoints.AddServices(builder.Services);
        builder.Services.AddSingleton(runtime);
        builder.Services.AddSingleton(leases);
        await using var app = builder.Build();
        app.Use(async (context, next) => { Ai.Tlbx.MidTerm.Services.RequestAccessContext.SetFullUser(context, true); await next(context); });
        TlbxMcpEndpoints.Map(app);
        var ct = TestContext.Current.CancellationToken;
        await app.StartAsync(ct);
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            await using var transport = new HttpClientTransport(new()
            {
                Endpoint = new Uri(address + TlbxMcpEndpoints.Path),
                AdditionalHeaders = new Dictionary<string, string>(StringComparer.Ordinal) { [TlbxMcpClientContextService.RuntimeHeader] = credential.ContextToken }
            });
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
            Assert.Contains(await client.ListToolsAsync(cancellationToken: ct), tool => tool.Name == "terminal_prompt");
            foreach (var method in new[] { "tools/call", "resources/list", "resources/read" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, address + TlbxMcpEndpoints.Path);
                request.Headers.Add(TlbxMcpClientContextService.RuntimeHeader, credential.ContextToken);
                request.Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"" + method + "\",\"params\":{}}", System.Text.Encoding.UTF8, "application/json");
                using var response = await Http.SendAsync(request, ct);
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Contains("TLBX_MCP_THREAD_PROOF_REQUIRED", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
            }
        }
        finally { await app.StopAsync(ct); }
    }

    [Theory]
    [InlineData("initialize", true)]
    [InlineData("server/discover", true)]
    [InlineData("tools/list", true)]
    [InlineData("resources/templates/list", true)]
    [InlineData("tools/call", false)]
    [InlineData("resources/list", false)]
    [InlineData("resources/read", false)]
    [InlineData("prompts/get", false)]
    [InlineData("subscriptions/listen", false)]
    public async Task DiscoveryOnlyAllowsDescriptorMethodsAndRestoresTheRequestBody(string method, bool allowed)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"" + method + "\",\"id\":1}");
        await using var body = new MemoryStream(bytes);
        context.Request.Body = body;
        var error = await TlbxMcpEndpoints.ValidateDiscoveryRequestAsync(context);
        Assert.Equal(allowed, error is null);
        Assert.Equal(0, body.Position);
    }

    [Fact]
    public async Task DiscoveryRejectsBatchesAndOversizedBodiesWithoutTrustingContentLength()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        await using var large = new MemoryStream(new byte[65537]);
        context.Request.Body = large;
        Assert.Equal(413, (await TlbxMcpEndpoints.ValidateDiscoveryRequestAsync(context))!.Value.Status);
        await using var batch = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("[{\"jsonrpc\":\"2.0\",\"method\":\"tools/list\",\"id\":1}]"));
        context.Request.Body = batch;
        Assert.Equal(409, (await TlbxMcpEndpoints.ValidateDiscoveryRequestAsync(context))!.Value.Status);
    }

    [Fact]
    public async Task RealHttpClientDiscoversToolsAndKeepsTwoCallingSessionsSeparate()
    {
        using var leases = CreateLeases();
        var first = leases.CreateHostLease("first001", 100).Split(':')[1];
        var second = leases.CreateHostLease("second02", 101).Split(':')[1];
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
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            async Task Check(string token, string expected)
            {
                await using var transport = new HttpClientTransport(new()
                {
                    Endpoint = new Uri(address + TlbxMcpEndpoints.Path),
                    AdditionalHeaders = new Dictionary<string, string>(StringComparer.Ordinal) { [SessionCliContextService.LeaseHeader] = token }
                });
                await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
                var tools = await client.ListToolsAsync(cancellationToken: ct);
                Assert.True(tools.Count > 40);
                Assert.Empty(TlbxMcpCliParity.MissingTools(tools.Select(tool => tool.Name)));
                Assert.Contains(tools, t => t.Name == "browser_screenshot");
                var result = await client.CallToolAsync("context_get", cancellationToken: ct);
                Assert.Equal(expected, result.StructuredContent!.Value.GetProperty("sessionId").GetString());
                var repaired = await client.CallToolAsync("context_repair", cancellationToken: ct);
                Assert.Equal("verified", repaired.StructuredContent!.Value.GetProperty("repair").GetString());
                Assert.Equal(expected, repaired.StructuredContent.Value.GetProperty("sessionId").GetString());
                var exported = await client.CallToolAsync("context_export", new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["format"] = "pwsh", ["previewName"] = "a'b ü"
                }, cancellationToken: ct);
                Assert.Equal("a'b ü", exported.StructuredContent!.Value.GetProperty("previewName").GetString());
                Assert.Contains("$env:MT_PREVIEW_NAME='a''b ü'", exported.StructuredContent.Value.GetProperty("export").GetString(), StringComparison.Ordinal);
                Assert.DoesNotContain(token, exported.StructuredContent.Value.GetRawText(), StringComparison.Ordinal);
                var spoof = await client.CallToolAsync("context_get", new Dictionary<string, object?>(StringComparer.Ordinal) { ["sessionId"] = "sibling" }, cancellationToken: ct);
                Assert.True(spoof.IsError);
            }
            await Task.WhenAll(Check(first, "first001"), Check(second, "second02"));
            using var body = new StringContent("{}");
            using var missing = await Http.PostAsync(address + TlbxMcpEndpoints.Path, body, ct);
            Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
            Assert.Equal("text/plain", missing.Content.Headers.ContentType?.MediaType);
            Assert.Contains("TLBX_MCP_CONTEXT_REQUIRED", await missing.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
            Assert.False(AuthMiddleware.IsPublicPath(TlbxMcpEndpoints.Path));
        }
        finally { await app.StopAsync(ct); }
    }

    [Fact]
    public void BindingRequiresOneLiveLeaseAndLoopbackAndCannotChooseSelf()
    {
        var live = true;
        using var leases = CreateLeases(() => live);
        var token = leases.CreateHostLease("first001", 100).Split(':')[1];
        var context = new DefaultHttpContext();
        Ai.Tlbx.MidTerm.Services.RequestAccessContext.SetFullUser(context, true);
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        Assert.NotNull(TlbxMcpEndpoints.Authorize(context, leases));
        context.Request.Headers[SessionCliContextService.LeaseHeader] = token;
        Assert.Null(TlbxMcpEndpoints.Authorize(context, leases));
        Assert.Equal("first001", context.Items[TlbxMcpEndpoints.SessionItem]);
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        Assert.Equal(403, TlbxMcpEndpoints.Authorize(context, leases)!.Value.Status);
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers[SessionCliContextService.LeaseHeader] = new[] { token, token };
        Assert.NotNull(TlbxMcpEndpoints.Authorize(context, leases));
        context.Request.Headers[SessionCliContextService.LeaseHeader] = token;
        live = false;
        Assert.NotNull(TlbxMcpEndpoints.Authorize(context, leases));
        using var restarted = CreateLeases();
        Assert.NotNull(TlbxMcpEndpoints.Authorize(context, restarted));
    }

    [Fact]
    public void ConnectCannotTurnRemoteMcpRequestsIntoLoopbackRequests()
    {
        foreach (var path in new[] { "/api/mcp", "/API/MCP/", "/api/%6dcp", "/api/%256dcp", "/api/mcp?x=1" })
            Assert.Throws<InvalidDataException>(() => ConnectRequestPolicy.ResolvePath(new Uri("https://localhost:2000"), path));
    }

    [Theory]
    [InlineData("Forwarded")]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-MidTerm-Internal-Proxy")]
    public void ProxyHeadersCannotEstablishLocalIdentity(string header)
    {
        using var leases = CreateLeases();
        var context = new DefaultHttpContext();
        Ai.Tlbx.MidTerm.Services.RequestAccessContext.SetFullUser(context, true);
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers[SessionCliContextService.LeaseHeader] = leases.CreateHostLease("first001", 100).Split(':')[1];
        context.Request.Headers[header] = "untrusted";
        Assert.Equal(403, TlbxMcpEndpoints.Authorize(context, leases)!.Value.Status);
    }

    [Fact]
    public void ExpiredLeaseCannotBeReusedForMcp()
    {
        var clock = new FakeTimeProvider();
        using var leases = new SessionCliContextService(() => [],
            _ => (1, new DateTime(2026, 10, 30)), clock);
        var token = leases.CreateHostLease("first001", 100).Split(':')[1];
        var context = new DefaultHttpContext();
        Ai.Tlbx.MidTerm.Services.RequestAccessContext.SetFullUser(context, true);
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers[SessionCliContextService.LeaseHeader] = token;
        Assert.Null(TlbxMcpEndpoints.Authorize(context, leases));
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.NotNull(TlbxMcpEndpoints.Authorize(context, leases));
    }

    [Fact]
    public void ScreenshotsAreInlineImagesAndRejectInvalidOrUnboundedPayloads()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=";
        var result = TlbxMcpTool.Screenshot("data:image/png;base64," + png);
        var image = Assert.IsType<ImageContentBlock>(Assert.Single(result.Content));
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(Convert.FromBase64String(png), image.DecodedData.ToArray());
        Assert.True(TlbxMcpTool.Screenshot("data:image/png;base64,invalid").IsError);
        Assert.True(TlbxMcpTool.Screenshot("data:image/png;base64,aGVsbG8=").IsError);
        Assert.True(TlbxMcpTool.Screenshot("Q:\\untrusted.png").IsError);
        Assert.True(TlbxMcpTool.Screenshot("data:image/png;base64," + new string('A', 16_000_000)).IsError);
    }

    private static SessionCliContextService CreateLeases(Func<bool>? live = null) => new(
        () => [], _ => live?.Invoke() == false ? null : (1, new DateTime(2026, 10, 30)), TimeProvider.System);
}
