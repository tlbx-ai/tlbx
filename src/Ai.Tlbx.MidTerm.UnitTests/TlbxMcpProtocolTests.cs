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
                Assert.Equal(4, tools.Count);
                Assert.Contains(tools, t => t.Name == "browser_screenshot");
                var result = await client.CallToolAsync("context_get", cancellationToken: ct);
                Assert.Equal(expected, result.StructuredContent!.Value.GetProperty("sessionId").GetString());
                var spoof = await client.CallToolAsync("context_get", new Dictionary<string, object?>(StringComparer.Ordinal) { ["sessionId"] = "sibling" }, cancellationToken: ct);
                Assert.True(spoof.IsError);
            }
            await Task.WhenAll(Check(first, "first001"), Check(second, "second02"));
            using var body = new StringContent("{}");
            using var missing = await Http.PostAsync(address + TlbxMcpEndpoints.Path, body, ct);
            Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
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
