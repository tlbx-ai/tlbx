using System.Net;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Mcp;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Services.Hosting;
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
    [Theory]
    [InlineData(false, "missing-session")]
    [InlineData(true, "missing-session")]
    [InlineData(true, "other001")]
    public async Task OversizedScreenshotKeepsCompleteOriginalAndHonorsOnlyTheRequestedWorkspace(bool save, string target)
    {
        var root = Path.Combine(Path.GetTempPath(), "tlbx-screenshot-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var manager = new TtyHostSessionManager(expectedVersion: "fixture", minCompatibleVersion: "fixture",
            instanceIdentity: MidTermInstanceIdentity.Load(root, 0));
        var otherWorkspace = Path.Combine(root, "other-workspace");
        Directory.CreateDirectory(otherWorkspace);
        var cache = (System.Collections.Concurrent.ConcurrentDictionary<string, Ai.Tlbx.MidTerm.Common.Protocol.SessionInfo>)
            typeof(TtyHostSessionManager).GetField("_sessionCache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(manager)!;
        cache["other001"] = new() { Id = "other001", CurrentDirectory = otherWorkspace };
        var context = new DefaultHttpContext();
        context.Items[TlbxMcpEndpoints.CallerIdentityItem] = "owner";
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context });
        services.AddSingleton<TlbxMcpArtifactStore>();
        // With no explicit save, even resolving the workspace services is forbidden.
        if (save) { services.AddSingleton(manager); services.AddSingleton(new WebPreviewService(2000)); }
        await using var provider = services.BuildServiceProvider();
        try
        {
            byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");
            var original = new byte[12_000_100];
            png.CopyTo(original, 0); // Valid PNG plus trailing bytes, preserving the entire original.
            var result = await TlbxMcpBrowserTools.CommandResultAsync(provider,
                new BrowserCommandRequest { Command = "screenshot", SessionId = target },
                new BrowserWsResult { Success = true, Result = "data:image/png;base64," + Convert.ToBase64String(original) }, save);
            var data = result.StructuredContent!.Value;
            var failedSave = save && target == "missing-session";
            Assert.Equal(failedSave, result.IsError == true);
            Assert.Equal(!failedSave, data.GetProperty("success").GetBoolean());
            var uri = data.GetProperty("resourceUri").GetString()!;
            var link = Assert.Single(result.Content.OfType<ResourceLinkBlock>());
            Assert.Equal(original.Length, link.Size);
            var store = provider.GetRequiredService<TlbxMcpArtifactStore>();
            var tail = Assert.IsType<BlobResourceContents>(Assert.Single(store.Read(uri + "?offset=12000084&length=16", TestContext.Current.CancellationToken).Contents));
            Assert.Equal(original.AsSpan(original.Length - 16).ToArray(), tail.DecodedData.ToArray());
            if (save && target == "other001")
            {
                var path = data.GetProperty("artifactPath").GetString()!;
                Assert.StartsWith(otherWorkspace + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
                Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            }
            else
            {
                Assert.Equal(System.Text.Json.JsonValueKind.Null, data.GetProperty("artifactPath").ValueKind);
                Assert.Empty(Directory.GetFileSystemEntries(otherWorkspace));
            }
            if (failedSave) Assert.Contains(result.Content.OfType<TextContentBlock>(), content => content.Text.Contains("Do not replay", StringComparison.Ordinal));
        }
        finally { await manager.DisposeAsync(); Directory.Delete(root, recursive: true); }
    }

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
