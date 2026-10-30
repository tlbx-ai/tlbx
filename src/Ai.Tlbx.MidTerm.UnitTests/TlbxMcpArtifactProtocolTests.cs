using System.Net;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Mcp;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TlbxMcpArtifactProtocolTests
{
    [Fact]
    public async Task ToolBasedResourceReadsPreserveExactChunksAndDenySiblingAndFilePathAccess()
    {
        var ct = TestContext.Current.CancellationToken;
        using var leases = new SessionCliContextService(() => [], _ => (1, new DateTime(2026, 10, 30)), TimeProvider.System);
        var first = leases.CreateHostLease("first001", 100).Split(':')[1];
        var second = leases.CreateHostLease("second02", 101).Split(':')[1];
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        TlbxMcpEndpoints.AddServices(builder.Services);
        builder.Services.AddSingleton(leases);
        await using var app = builder.Build();
        app.Use(async (context, next) => { RequestAccessContext.SetFullUser(context, true); await next(context); });
        TlbxMcpEndpoints.Map(app);
        app.MapPost("/api/sessions/{id}/input/prompt", (HttpContext context) =>
        {
            var link = context.RequestServices.GetRequiredService<TlbxMcpArtifactStore>().Register([0, 255, 127, 128], "capture.bin", "application/octet-stream");
            return Results.Json(new JsonObject { ["uri"] = link.Uri }, McpJsonContext.Default.JsonObject);
        });
        await app.StartAsync(ct);
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            HttpClientTransport Transport(string token) => new(new()
            {
                Endpoint = new Uri(address + TlbxMcpEndpoints.Path),
                AdditionalHeaders = new Dictionary<string, string>(StringComparer.Ordinal) { [SessionCliContextService.LeaseHeader] = token }
            });
            await using var firstTransport = Transport(first);
            await using var secondTransport = Transport(second);
            await using var owner = await McpClient.CreateAsync(firstTransport, cancellationToken: ct);
            await using var sibling = await McpClient.CreateAsync(secondTransport, cancellationToken: ct);
            var captured = await owner.CallToolAsync("terminal_prompt", new Dictionary<string, object?>(StringComparer.Ordinal) { ["text"] = "capture" }, cancellationToken: ct);
            var uri = captured.StructuredContent!.Value.GetProperty("data").GetProperty("uri").GetString();
            var arguments = new Dictionary<string, object?>(StringComparer.Ordinal) { ["uri"] = uri, ["offset"] = 1, ["length"] = 2 };
            var read = await owner.CallToolAsync("artifact_read", arguments, cancellationToken: ct);
            var resource = Assert.IsType<EmbeddedResourceBlock>(Assert.Single(read.Content));
            Assert.Equal(new byte[] { 255, 127 }, Assert.IsType<BlobResourceContents>(resource.Resource).DecodedData.ToArray());
            var denied = await sibling.CallToolAsync("artifact_read", arguments, cancellationToken: ct);
            Assert.True(denied.IsError);
            var mine = await owner.CallToolAsync("artifacts_list", cancellationToken: ct);
            var theirs = await sibling.CallToolAsync("artifacts_list", cancellationToken: ct);
            Assert.Equal(1, mine.StructuredContent!.Value.GetProperty("resources").GetArrayLength());
            Assert.Equal(0, theirs.StructuredContent!.Value.GetProperty("resources").GetArrayLength());
            var path = await owner.CallToolAsync("artifact_read", new Dictionary<string, object?>(StringComparer.Ordinal) { ["uri"] = "C:\\Windows\\win.ini" }, cancellationToken: ct);
            Assert.True(path.IsError);
        }
        finally { await app.StopAsync(ct); }
    }
}
