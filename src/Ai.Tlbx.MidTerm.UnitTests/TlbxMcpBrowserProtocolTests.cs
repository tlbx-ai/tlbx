using System.Globalization;
using System.Net;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Mcp;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TlbxMcpBrowserProtocolTests
{
    private const string Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=";

    [Fact]
    public void TypedCommandsPreserveUnicodeAndFractionalNumbersWithoutShellParsing()
    {
        var text = "quotes '\" $()`n\0 Grüße 東京\n" + new string('x', 70_000);
        var fill = TlbxMcpBrowserTools.BuildCommand("fill", Args(new { selector = "input[data-x='a b']", value = text }), "first001", "alpha", "preview-a");
        Assert.Equal(text, fill.Value);
        Assert.Equal("preview-a", fill.PreviewId);
        Assert.Equal("alpha", fill.PreviewName);
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var scroll = TlbxMcpBrowserTools.BuildCommand("scroll", Args(new { deltaY = 1.25, deltaX = -2.5 }), "first001", "alpha", null);
            Assert.Equal("1.25 -2.5", scroll.Value);
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
        var css = TlbxMcpBrowserTools.BuildCommand("css", Args(new { selector = "body", properties = new[] { "font-family", "--theme" } }), "first001", null, null);
        Assert.Equal("font-family,--theme", css.Value);
        var evaluate = TlbxMcpBrowserTools.BuildCommand("exec", Args(new { code = "({value: `\"'\\n Grüße`})" }), "first001", null, null);
        Assert.Equal("({value: `\"'\\n Grüße`})", evaluate.Value);
    }

    [Fact]
    public void BatchValidationRejectsScopeOverridesAndMalformedLaterStepsBeforeDispatch()
    {
        var overrides = Args(new { commands = new object[] { new { command = "click", selector = "#go" }, new { command = "fill", selector = "input", value = "x", sessionId = "sibling" } } });
        Assert.Throws<ArgumentException>(() => TlbxMcpBrowserTools.ParseBatch(overrides, "first001", "alpha"));
        var malformed = Args(new { commands = new object[] { new { command = "click", selector = "#go" }, new { command = "fill", selector = "input", value = 123 } } });
        Assert.Throws<ArgumentException>(() => TlbxMcpBrowserTools.ParseBatch(malformed, "first001", "alpha"));
        var request = TlbxMcpBrowserTools.ParseBatch(Args(new { commands = new object[] { new { command = "evaluate", code = "document.title" }, new { command = "screenshot", fullPage = true } } }), "first001", "alpha");
        Assert.Equal("exec", request.Commands[0].Command);
        Assert.True(request.Commands[1].FullPage);
        Assert.All(request.Commands, command => Assert.Equal("first001", command.SessionId));
        var longStep = TlbxMcpBrowserTools.ParseBatch(Args(new { timeout = 120,
            commands = new object[] { new { command = "wait", selector = "#ready", timeout = 300 } } }), "first001", "alpha");
        Assert.Equal(300, longStep.Commands[0].Timeout);
        Assert.Throws<ArgumentException>(() => TlbxMcpBrowserTools.ParseBatch(Args(new { timeout = 121,
            commands = new object[] { new { command = "wait", selector = "#ready" } } }), "first001", "alpha"));
    }

    [Fact]
    public async Task RealMcpClientsKeepNamedPreviewAndLiteralPayloadsSeparateAndReceiveInlinePng()
    {
        using var leases = new SessionCliContextService(() => [], _ => (1, new DateTime(2026, 10, 30)), TimeProvider.System);
        var first = leases.CreateHostLease("first001", 100).Split(':')[1];
        var second = leases.CreateHostLease("second02", 101).Split(':')[1];
        var commands = new BrowserCommandService();
        var sent = new System.Collections.Concurrent.ConcurrentDictionary<string, BrowserWsMessage>(StringComparer.Ordinal);
        void Register(string id, string session, string preview)
        {
            commands.TryRegisterClient(id, session, preview, id, message =>
            {
                sent[session] = message;
                commands.ReceiveResult(new BrowserWsResult
                {
                    Id = message.Id, Success = true,
                    Result = message.Command == "screenshot" ? Png : "{\"session\":\"" + session + "\"}"
                }, id);
            });
        }
        Register("client-a", "first001", "alpha");
        Register("client-b", "second02", "beta");
        var owners = new BrowserPreviewOwnerService();
        var ui = new BrowserUiBridge(new MainBrowserService(), owners);
        ui.RegisterListener("ui", "desktop", (_, _) => { }, (_, _) => { }, (_, _, _, _) => { }, (_, _, _, _) => { });
        var previews = new WebPreviewService(2000);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(leases);
        builder.Services.AddSingleton(commands);
        builder.Services.AddSingleton(previews);
        builder.Services.AddSingleton(ui);
        builder.Services.AddSingleton(new BrowserPreviewOperations(commands, previews, ui, new BrowserPreviewRegistry(), owners));
        builder.Services.AddMcpServer().WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools(TlbxMcpBrowserTools.CreateTools());
        await using var app = builder.Build();
        app.Use(async (context, next) => { RequestAccessContext.SetFullUser(context, true); await next(context); });
        TlbxMcpEndpoints.Map(app);
        var ct = TestContext.Current.CancellationToken;
        await app.StartAsync(ct);
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            async Task Check(string token, string session, string preview)
            {
                await using var transport = new HttpClientTransport(new()
                {
                    Endpoint = new Uri(address + TlbxMcpEndpoints.Path),
                    AdditionalHeaders = new Dictionary<string, string>(StringComparer.Ordinal) { [SessionCliContextService.LeaseHeader] = token }
                });
                await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
                var tools = await client.ListToolsAsync(cancellationToken: ct);
                Assert.Contains(tools, tool => tool.Name == "browser_with_preview");
                Assert.True(tools.Single(tool => tool.Name == "browser_wait").ProtocolTool.InputSchema
                    .GetProperty("properties").GetProperty("timeout").GetProperty("maximum").GetInt32() >= 300);
                Assert.Equal(120, tools.Single(tool => tool.Name == "browser_batch").ProtocolTool.InputSchema
                    .GetProperty("properties").GetProperty("timeout").GetProperty("maximum").GetInt32());
                foreach (var invalid in new[] { (Field: "url", Value: (object)123), (Field: "activate", Value: (object)"yes") })
                {
                    var invalidOpen = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["url"] = "https://example.invalid", ["claim"] = true, ["browserId"] = "desktop", ["previewName"] = preview,
                        [invalid.Field] = invalid.Value
                    };
                    var rejected = await client.CallToolAsync("browser_open", invalidOpen, cancellationToken: ct);
                    Assert.True(rejected.IsError);
                    Assert.Null(owners.GetOwnerBrowserId(session, preview));
                }
                var denied = await client.CallToolAsync("browser_fill", new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["targetSessionId"] = session == "first001" ? "second02" : "first001",
                    ["previewName"] = preview, ["selector"] = "input", ["value"] = "forbidden"
                }, cancellationToken: ct);
                Assert.True(denied.IsError);
                Assert.DoesNotContain(sent.Values, message => message.Value == "forbidden");
                var payload = "literal '\" $()\0 Grüße\n" + new string('a', 70_000);
                var result = await client.CallToolAsync("browser_fill", new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["previewName"] = preview, ["selector"] = "input", ["value"] = payload
                }, cancellationToken: ct);
                Assert.NotEqual(true, result.IsError);
                Assert.Equal(session, result.StructuredContent!.Value.GetProperty("sessionId").GetString());
                Assert.Equal(payload, sent[session].Value);
                // Clearing an input and filling whitespace are literal CLI use
                // cases too; neither may be rejected as a missing argument.
                foreach (var blank in new[] { "", " \t\r\n" })
                {
                    var cleared = await client.CallToolAsync("browser_fill", new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["previewName"] = preview, ["selector"] = "input", ["value"] = blank
                    }, cancellationToken: ct);
                    Assert.NotEqual(true, cleared.IsError);
                    Assert.Equal(blank, sent[session].Value);
                    Assert.Equal(preview, sent[session].PreviewName);
                }
                var longWait = await client.CallToolAsync("browser_wait", new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["previewName"] = preview, ["selector"] = "input", ["timeout"] = 300
                }, cancellationToken: ct);
                Assert.NotEqual(true, longWait.IsError);
                Assert.Equal(300, sent[session].Timeout);
                var image = await client.CallToolAsync("browser_screenshot", new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["previewName"] = preview, ["fullPage"] = true
                }, cancellationToken: ct);
                Assert.NotEqual(true, image.IsError);
                Assert.IsType<ImageContentBlock>(Assert.Single(image.Content));
                Assert.True(sent[session].FullPage);
            }
            await Task.WhenAll(Check(first, "first001", "alpha"), Check(second, "second02", "beta"));
        }
        finally
        {
            commands.CancelAllPending();
            commands.UnregisterClient("client-a");
            commands.UnregisterClient("client-b");
            ui.UnregisterListener("ui");
            await app.StopAsync(ct);
        }
    }

    [Fact]
    public void BatchFillAndEvaluatePreserveEmptyAndWhitespacePayloads()
    {
        foreach (var literal in new[] { "", " \t\r\n" })
        {
            var batch = TlbxMcpBrowserTools.ParseBatch(Args(new
            {
                commands = new object[]
                {
                    new { command = "fill", selector = "input", value = literal },
                    new { command = "exec", value = literal },
                    new { command = "evaluate", code = literal }
                }
            }), "first001", "alpha");
            Assert.All(batch.Commands, command => Assert.Equal(literal, command.Value));
            Assert.All(batch.Commands, command => Assert.Equal("alpha", command.PreviewName));
        }
    }

    [Fact]
    public async Task SharedOpenVerifiesReadyWithoutActivationAndScopedCancellationStillCloses()
    {
        var owners = new BrowserPreviewOwnerService();
        var main = new MainBrowserService();
        var ui = new BrowserUiBridge(main, owners);
        var previews = new WebPreviewService(2000);
        var registry = new BrowserPreviewRegistry();
        var commands = new BrowserCommandService();
        var operations = new BrowserPreviewOperations(commands, previews, ui, registry, owners);
        var activated = false;
        var readyProbed = false;
        ui.RegisterListener("ui", "desktop", (_, _) => { }, (_, _) => { }, (_, _, _, _) => { },
            (session, name, _, activate) =>
            {
                activated = activate;
                commands.TryRegisterClient("preview", session, name, "frame", message =>
                {
                    readyProbed |= message.Command == "ready";
                    commands.ReceiveResult(new BrowserWsResult { Id = message.Id, Success = true, Result = "ready" }, "preview");
                }, browserId: "desktop", targetRevision: previews.GetPreviewSession(session, name)?.TargetRevision,
                    ownershipGeneration: owners.GetGeneration(session, name));
            });
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var opened = await operations.OpenAsync("first001", "named", "https://example.invalid", cancellationToken: ct);
            Assert.True(opened.Success, opened.Text);
            Assert.False(activated);
            Assert.True(readyProbed);
            await operations.CloseAsync("first001", "named", ct);
            Assert.Null(previews.GetPreviewSession("first001", "named"));

            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operations.WithPreviewAsync<int>("first001", "temporary", "https://example.invalid", bodyCt =>
            {
                Assert.NotNull(previews.GetPreviewSession("first001", "temporary"));
                cancelled.Cancel();
                return Task.FromCanceled<int>(bodyCt);
            }, cancelled.Token));
            Assert.Null(previews.GetPreviewSession("first001", "temporary"));
            var scope = await operations.OpenScopeAsync("actor-a", "first001", "temporary", "https://example.invalid", ct);
            try
            {
                await Assert.ThrowsAsync<ArgumentException>(() => operations.OpenScopeAsync("actor-b", "first001", "temporary", "https://example.invalid", ct));
                await Assert.ThrowsAsync<ArgumentException>(() => operations.CloseScopeAsync("actor-b", scope.Token));
                Assert.NotNull(previews.GetPreviewSession("first001", "temporary"));
            }
            finally { await operations.CloseScopeAsync("actor-a", scope.Token); }
            Assert.Null(previews.GetPreviewSession("first001", "temporary"));
            // A closed lease releases its name; a stale/foreign opaque token cannot close its successor.
            var successor = await operations.OpenScopeAsync("actor-b", "first001", "temporary", "https://example.invalid", ct);
            try
            {
                await operations.CloseScopeAsync("actor-a", scope.Token);
                Assert.NotNull(previews.GetPreviewSession("first001", "temporary"));
            }
            finally { await operations.CloseScopeAsync("actor-b", successor.Token); }
            var oldIdentity = await operations.OpenScopeAsync("actor-a", "first001", "temporary", "https://example.invalid", ct);
            await operations.CloseAsync("first001", "temporary", ct);
            var replacement = await operations.OpenAsync("first001", "temporary", "https://replacement.invalid", cancellationToken: ct);
            Assert.True(replacement.Success, replacement.Text);
            await operations.CloseScopeAsync("actor-a", oldIdentity.Token);
            Assert.NotNull(previews.GetPreviewSession("first001", "temporary"));
            await operations.CloseAsync("first001", "temporary", ct);
        }
        finally
        {
            ui.UnregisterListener("ui");
            commands.UnregisterClient("preview");
            previews.ClearSession("first001");
        }
    }

    [Fact]
    public async Task ClearingPreviewStateKeepsTheCurrentBridgeButRetargetingInvalidatesIt()
    {
        var previews = new WebPreviewService(2000);
        var session = "state-" + Guid.NewGuid().ToString("N");
        Assert.True(previews.SetTarget(session, "alpha", "https://example.invalid/first"));
        var revision = previews.GetPreviewSession(session, "alpha")!.TargetRevision;
        var commands = new BrowserCommandService(webPreviewService: previews);
        Assert.True(commands.TryRegisterClient("state-client", session, "alpha", "state-preview", message =>
            commands.ReceiveResult(new BrowserWsResult { Id = message.Id, Success = true, Result = "ready" }, "state-client"),
            targetRevision: revision));
        try
        {
            Assert.True(previews.ClearState(session, "alpha"));
            Assert.Equal(revision, previews.GetPreviewSession(session, "alpha")!.TargetRevision);
            var current = await commands.ExecuteCommandAsync(new BrowserCommandRequest
            {
                Command = "query", Selector = "body", SessionId = session, PreviewName = "alpha", Timeout = 1
            }, TestContext.Current.CancellationToken);
            Assert.True(current.Success, current.Error);
            Assert.True(previews.SetTarget(session, "alpha", "https://example.invalid/second"));
            Assert.NotEqual(revision, previews.GetPreviewSession(session, "alpha")!.TargetRevision);
            Assert.Null(commands.GetStatus(targetUrl: null, sessionId: session, previewName: "alpha").DefaultClient);
        }
        finally
        {
            commands.UnregisterClient("state-client");
            previews.ClearSession(session);
        }
    }

    private static Dictionary<string, JsonElement> Args<T>(T value)
    {
        using var properties = JsonSerializer.SerializeToElement(value).EnumerateObject();
        return properties.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
    }
}
