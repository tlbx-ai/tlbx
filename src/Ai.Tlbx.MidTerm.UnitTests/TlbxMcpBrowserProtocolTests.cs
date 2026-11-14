using System.Globalization;
using System.Net;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Hosting;
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

    [Theory]
    [InlineData("status")]
    [InlineData("screenshot")]
    public async Task MobileControlAwaitsExactOwnerCommandPreviewAndRevision(string action)
    {
        var owners = new BrowserPreviewOwnerService();
        var previews = new WebPreviewService(2000);
        Assert.True(previews.SetTarget("mobile-session", "named", "https://example.invalid/"));
        var ui = new BrowserUiBridge(new MainBrowserService(), owners, previews);
        string? requestId = null;
        long revision = -1;
        ui.RegisterAcknowledgedListener("ui", "desktop:tab", (_, _, _) => { }, (_, _, _) => { },
            (_, _, _, _, _) => { }, (_, _, _, _, _, _) => { },
            mobileDevice: (id, session, name, receivedAction, _, targetRevision) =>
            {
                Assert.Equal("mobile-session", session); Assert.Equal("named", name);
                Assert.Equal(action, receivedAction); requestId = id; revision = targetRevision;
            });
        owners.Claim("mobile-session", "named", "desktop:tab");
        try
        {
            var pending = ui.RequestMobileDeviceAsync("mobile-session", "named", action, null, TestContext.Current.CancellationToken);
            Assert.False(pending.IsCompleted);
            Assert.NotNull(requestId);
            BrowserUiCommandResult Result(string command = "mobile-device", string session = "mobile-session", long? targetRevision = null) => new()
            {
                RequestId = requestId!, Command = command, SessionId = session, PreviewName = "named",
                TargetRevision = targetRevision ?? revision, Success = true,
                MobileDevice = new BrowserMobileDeviceState { Available = action == "screenshot", Connected = action == "screenshot", Open = action == "screenshot" ? true : null },
                ScreenshotDataUrl = action == "screenshot" ? Png : null
            };
            Assert.False(ui.CompleteUiCommand(Result(), "desktop:sibling"));
            Assert.False(ui.CompleteUiCommand(Result("viewport"), "desktop:tab"));
            Assert.False(ui.CompleteUiCommand(Result(session: "other-session"), "desktop:tab"));
            Assert.False(ui.CompleteUiCommand(Result(targetRevision: revision + 1), "desktop:tab"));
            Assert.False(pending.IsCompleted);
            Assert.True(ui.CompleteUiCommand(Result(), "desktop:tab"));
            var result = await pending;
            Assert.True(result.Success);
            Assert.Equal(action == "screenshot", result.MobileDevice!.Available);
            Assert.Equal(action == "screenshot" ? Png : null, result.ScreenshotDataUrl);
            Assert.False(ui.CompleteUiCommand(Result(), "desktop:tab"));
            if (action == "screenshot")
            {
                await using var services = new ServiceCollection().BuildServiceProvider();
                var image = await TlbxMcpBrowserTools.CommandResultAsync(services,
                    new BrowserCommandRequest { Command = "screenshot", SessionId = "mobile-session", PreviewName = "named" },
                    new BrowserWsResult { Success = true, Result = result.ScreenshotDataUrl }, false);
                Assert.IsType<ImageContentBlock>(Assert.Single(image.Content));
                Assert.DoesNotContain(image.Content.OfType<TextContentBlock>(), block => block.Text.Contains("base64", StringComparison.Ordinal));
            }
        }
        finally { ui.UnregisterListener("ui"); }
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("owner")]
    [InlineData("extension-error")]
    public async Task MobileControlRejectsStaleTargetsAndSurfacesExtensionFailures(string failure)
    {
        var owners = new BrowserPreviewOwnerService();
        var previews = new WebPreviewService(2000);
        Assert.True(previews.SetTarget("mobile-session", "named", "https://example.invalid/"));
        var ui = new BrowserUiBridge(new MainBrowserService(), owners, previews);
        ui.RegisterAcknowledgedListener("ui", "desktop:tab", (_, _, _) => { }, (_, _, _) => { },
            (_, _, _, _, _) => { }, (_, _, _, _, _, _) => { }, mobileDevice: (id, session, name, _, _, revision) =>
            {
                if (failure == "revision") Assert.True(previews.SetTarget("mobile-session", "named", "https://other.invalid/"));
                if (failure == "owner") owners.Claim("mobile-session", "named", "other:tab");
                ui.CompleteUiCommand(new BrowserUiCommandResult
                {
                    RequestId = id, Command = "mobile-device", SessionId = session, PreviewName = name, TargetRevision = revision,
                    Success = failure != "extension-error", Error = failure == "extension-error" ? "Chrome bridge disconnected" : null,
                    MobileDevice = new BrowserMobileDeviceState { Connected = true, Available = true }
                }, "desktop:tab");
            });
        owners.Claim("mobile-session", "named", "desktop:tab");
        try
        {
            var result = await ui.RequestMobileDeviceAsync("mobile-session", "named", "open", null, TestContext.Current.CancellationToken);
            Assert.False(result.Success);
            Assert.Contains(failure == "extension-error" ? "Chrome bridge disconnected" : "changed", result.Error, StringComparison.Ordinal);
        }
        finally { ui.UnregisterListener("ui"); }
    }

    [Fact]
    public void CooperativeFrameScopeIsLiteralAndRejectsUnsupportedNavigationOrCapture()
    {
        var command = TlbxMcpBrowserTools.BuildCommand("evaluate", Args(new { frame = "iframe[data-name='東京']", code = "document.title" }), "first001", "alpha", "parent-preview");
        Assert.Equal("iframe[data-name='東京']", command.Frame);
        Assert.Equal("parent-preview", command.PreviewId);
        Assert.Equal("exec", command.Command);
        var batch = TlbxMcpBrowserTools.ParseBatch(Args(new { commands = new[] { new { command = "query", frame = "#child", selector = "body" } } }), "first001", "alpha");
        Assert.Equal("#child", Assert.Single(batch.Commands).Frame);
        Assert.Throws<ArgumentException>(() => TlbxMcpBrowserTools.BuildCommand("screenshot", Args(new { frame = "#child" }), "first001", "alpha", null));
        Assert.Throws<ArgumentException>(() => TlbxMcpBrowserTools.BuildCommand("exec", Args(new { frame = "#child", code = "42", timeout = 16 }), "first001", "alpha", null));
        Assert.Throws<ArgumentException>(() => TlbxMcpBrowserTools.BuildCommand("click", Args(new { frame = "#child", selector = "button", waitForNavigation = true }), "first001", "alpha", null));
    }

    [Fact]
    public void DetailScreenshotPreservesSelectorAndRejectsConflictingCaptureModes()
    {
        const string selector = "canvas[data-chart='Grüße 東京']";
        var detail = TlbxMcpBrowserTools.BuildCommand("screenshot", Args(new { selector }), "first001", "alpha", "preview-a");
        Assert.Equal(selector, detail.Selector);
        Assert.False(detail.FullPage);
        Assert.Equal("preview-a", detail.PreviewId);
        var batch = TlbxMcpBrowserTools.ParseBatch(Args(new { commands = new[] { new { command = "screenshot", selector } } }), "first001", "alpha");
        Assert.Equal(selector, Assert.Single(batch.Commands).Selector);
        Assert.Throws<ArgumentException>(() => TlbxMcpBrowserTools.BuildCommand("screenshot", Args(new { selector, fullPage = true }), "first001", "alpha", null));
        Assert.Throws<ArgumentException>(() => TlbxMcpBrowserTools.BuildCommand("screenshot", Args(new { selector = " " }), "first001", "alpha", null));
        Assert.Throws<ArgumentException>(() => TlbxMcpBrowserTools.ParseBatch(Args(new { commands = new[] { new { command = "screenshot", selector, fullPage = true } } }), "first001", "alpha"));
        var viewport = TlbxMcpBrowserTools.BuildCommand("screenshot", Args(new { }), "first001", "alpha", null);
        Assert.Null(viewport.Selector);
        Assert.False(viewport.FullPage);
    }

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

    [Theory]
    [InlineData("attached")]
    [InlineData("visible")]
    [InlineData("hidden")]
    [InlineData("detached")]
    public void VisibilityWaitStatesPreserveTheExactBrowserScope(string state)
    {
        var request = TlbxMcpBrowserTools.BuildCommand("wait", Args(new { selector = "#menu", state }), "first001", "alpha", "client-a");
        Assert.Equal("wait", request.Command);
        Assert.Equal(state, request.Value);
        Assert.Equal("#menu", request.Selector);
        Assert.Equal("first001", request.SessionId);
        Assert.Equal("alpha", request.PreviewName);
        Assert.Equal("client-a", request.PreviewId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("visible")]
    public async Task ServerWaitDeadlinePreservesTheRequestedState(string? state)
    {
        var commands = new BrowserCommandService();
        Assert.True(commands.TryRegisterClient("wait-timeout", "wait-session", "alpha", "wait-preview", _ => { }));
        try
        {
            var result = await commands.ExecuteCommandAsync(new BrowserCommandRequest
            {
                Command = "wait", Selector = "#pending", SessionId = "wait-session", PreviewName = "alpha", Value = state, Timeout = 1
            }, TestContext.Current.CancellationToken);
            Assert.False(result.Success);
            Assert.Contains("timed out", result.Error, StringComparison.Ordinal);
            if (state is null) Assert.DoesNotContain("waiting for", result.Error, StringComparison.Ordinal);
            else Assert.Contains("waiting for visible: #pending", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            commands.UnregisterClient("wait-timeout");
        }
    }

    [Fact]
    public void DefaultWaitRetainsAttachmentAndMalformedWaitStopsTheWholeBatchBeforeDispatch()
    {
        var request = TlbxMcpBrowserTools.BuildCommand("wait", Args(new { selector = "#menu" }), "first001", "alpha", null);
        Assert.Null(request.Value);
        Assert.Throws<ArgumentException>(() => TlbxMcpBrowserTools.ParseBatch(Args(new
        {
            commands = new object[] { new { command = "click", selector = "#button" }, new { command = "wait", selector = "#menu", state = "visble" } }
        }), "first001", "alpha"));
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
                    Result = message.Command == "screenshot" ? Png : "{\"session\":\"" + session + "\"}",
                    Capture = message.Command == "screenshot" ? new BrowserCaptureInfo
                    {
                        Renderer = "dom-svg", PixelCoverage = "unverified", MediaPixels = "unverified"
                    } : null
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
                var child = await client.CallToolAsync("browser_evaluate", new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["previewName"] = preview, ["frame"] = "iframe[data-child='東京']", ["code"] = "document.title"
                }, cancellationToken: ct);
                Assert.NotEqual(true, child.IsError);
                Assert.Equal("iframe[data-child='東京']", sent[session].Frame);
                Assert.Equal(preview, sent[session].PreviewName);
                Assert.Equal("exec", sent[session].Command);
                var image = await client.CallToolAsync("browser_screenshot", new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["previewName"] = preview, ["fullPage"] = true
                }, cancellationToken: ct);
                Assert.NotEqual(true, image.IsError);
                Assert.IsType<ImageContentBlock>(Assert.Single(image.Content));
                Assert.True(sent[session].FullPage);
                Assert.Equal("unverified", image.StructuredContent!.Value.GetProperty("capture").GetProperty("pixelCoverage").GetString());
                var detail = await client.CallToolAsync("browser_screenshot", new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["previewName"] = preview, ["selector"] = "canvas[data-chart='Grüße 東京']"
                }, cancellationToken: ct);
                Assert.NotEqual(true, detail.IsError);
                Assert.IsType<ImageContentBlock>(Assert.Single(detail.Content));
                Assert.Equal("canvas[data-chart='Grüße 東京']", sent[session].Selector);
                Assert.Equal(preview, sent[session].PreviewName);
                Assert.False(sent[session].FullPage);
                var batch = await client.CallToolAsync("browser_batch", new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["previewName"] = preview,
                    ["commands"] = new[] { new { command = "screenshot", selector = "canvas" } }
                }, cancellationToken: ct);
                Assert.NotEqual(true, batch.IsError);
                Assert.Single(batch.Content.OfType<ImageContentBlock>());
                var captures = batch.StructuredContent!.Value.GetProperty("captures");
                Assert.Equal("unverified", captures[0].GetProperty("capture").GetProperty("capture").GetProperty("pixelCoverage").GetString());
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
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "IDISP014:Use a single instance of HttpClient", Justification = "One client belongs to the isolated loopback fixture for all CLI requests.")]
    public async Task InspectRetainsExplicitClientAcrossStatusDiagnosticsAndScreenshotWithoutFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "tlbx-inspect-protocol-" + Guid.NewGuid().ToString("N"));
        var manager = new TtyHostSessionManager(expectedVersion: "fixture", minCompatibleVersion: "fixture",
            instanceIdentity: MidTermInstanceIdentity.Load(root, 0));
        using var leases = new SessionCliContextService(() => [], _ => (1, new DateTime(2026, 10, 30)), TimeProvider.System);
        var token = leases.CreateHostLease("first001", 100).Split(':')[1];
        var owners = new BrowserPreviewOwnerService();
        var ui = new BrowserUiBridge(new MainBrowserService(), owners);
        ui.RegisterListener("ui", "desktop", (_, _) => { }, (_, _) => { }, (_, _, _, _) => { }, (_, _, _, _) => { });
        var previews = new WebPreviewService(2000);
        var commands = new BrowserCommandService(previewOwnerService: owners);
        var sent = new System.Collections.Concurrent.ConcurrentQueue<BrowserWsMessage>();
        void Register(string id, string session = "first001", string preview = "alpha", string browser = "desktop")
        {
            Assert.True(commands.TryRegisterClient(id, session, preview, id, message =>
            {
                sent.Enqueue(message);
                commands.ReceiveResult(new BrowserWsResult
                {
                    Id = message.Id, Success = true, Result = message.Command == "screenshot" ? Png : id + ":" + message.Command
                }, id);
            }, browserId: browser));
        }
        Register("client-a");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(leases);
        builder.Services.AddSingleton(manager);
        builder.Services.AddSingleton(commands);
        builder.Services.AddSingleton(previews);
        builder.Services.AddSingleton(ui);
        builder.Services.AddSingleton(new BrowserPreviewOperations(commands, previews, ui, new BrowserPreviewRegistry(), owners));
        builder.Services.AddMcpServer().WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools(TlbxMcpBrowserTools.CreateTools());
        await using var app = builder.Build();
        app.Use(async (context, next) => { RequestAccessContext.SetFullUser(context, true); await next(context); });
        TlbxMcpEndpoints.Map(app);
        BrowserEndpoints.MapCliEndpoint(app, commands, manager, previews, ui);
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
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            async Task<string> CliInspect(params string[] options)
            {
                var args = new[] { "inspect", "--session", "first001", "--preview", "alpha" }.Concat(options);
                using var body = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(string.Join('\0', args)));
                using var response = await http.PostAsync("/api/browser", body, ct);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct);
            }
            // The existing CLI protocol remains usable without a client ID when the scope is unambiguous.
            var implicitInspect = await CliInspect();
            Assert.Contains("selected preview: client-a", implicitInspect, StringComparison.Ordinal);
            Assert.Contains("client-a:exec", implicitInspect, StringComparison.Ordinal);
            sent.Clear();

            Register("client-b"); // Two clients in the same owner/session/named preview.
            Register("other-session", session: "second02");
            Register("other-preview", preview: "beta");
            Register("other-owner", browser: "different-tab");
            var inspected = await client.CallToolAsync("browser_inspect", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["previewName"] = "alpha", ["previewId"] = "client-b", ["screenshot"] = true
            }, cancellationToken: ct);
            Assert.NotEqual(true, inspected.IsError);
            var diagnostics = inspected.StructuredContent!.Value.GetProperty("diagnostics").GetString()!;
            Assert.Contains("state: ready", diagnostics, StringComparison.Ordinal);
            Assert.Contains("selected preview: client-b", diagnostics, StringComparison.Ordinal);
            foreach (var command in new[] { "exec", "outline", "forms", "log" })
                Assert.Contains("client-b:" + command, diagnostics, StringComparison.Ordinal);
            Assert.IsType<ImageContentBlock>(inspected.Content.Last());
            Assert.Equal(new[] { "exec", "outline", "forms", "log", "screenshot" }, sent.Select(message => message.Command), StringComparer.Ordinal);
            Assert.All(sent, message =>
            {
                Assert.Equal("client-b", message.PreviewId);
                Assert.Equal("first001", message.SessionId);
                Assert.Equal("alpha", message.PreviewName);
            });
            sent.Clear();

            // The shared CLI screenshot path must preserve the same exact identity too.
            var cliInspect = await CliInspect("--preview-id", "client-b", "--screenshot");
            Assert.Contains("selected preview: client-b", cliInspect, StringComparison.Ordinal);
            Assert.Equal(new[] { "exec", "outline", "forms", "log", "screenshot" }, sent.Select(message => message.Command), StringComparer.Ordinal);
            Assert.All(sent, message => Assert.Equal("client-b", message.PreviewId));
            sent.Clear();

            foreach (var id in new[] { "missing-client", "other-session", "other-preview", "other-owner" })
            {
                var unavailable = await client.CallToolAsync("browser_inspect", new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["previewName"] = "alpha", ["previewId"] = id
                }, cancellationToken: ct);
                var text = unavailable.StructuredContent!.Value.GetProperty("diagnostics").GetString()!;
                Assert.Contains("scope: preview '" + id + "'", text, StringComparison.Ordinal);
                Assert.Contains("controllable: no", text, StringComparison.Ordinal);
                Assert.Contains("browser: unavailable", text, StringComparison.Ordinal);
                Assert.DoesNotContain("client-a:exec", text, StringComparison.Ordinal);
                Assert.DoesNotContain("client-b:exec", text, StringComparison.Ordinal);
                Assert.Empty(sent);
            }
            var missingShot = await client.CallToolAsync("browser_inspect", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["previewName"] = "alpha", ["previewId"] = "missing-client", ["screenshot"] = true
            }, cancellationToken: ct);
            Assert.True(missingShot.IsError);
            Assert.DoesNotContain(missingShot.Content, item => item is ImageContentBlock);
            Assert.Empty(sent);
            var cliMissing = await CliInspect("--preview-id", "missing-client", "--screenshot");
            Assert.Contains("browser: unavailable", cliMissing, StringComparison.Ordinal);
            Assert.DoesNotContain("client-a:exec", cliMissing, StringComparison.Ordinal);
            Assert.Empty(sent);
        }
        finally
        {
            commands.CancelAllPending();
            foreach (var id in new[] { "client-a", "client-b", "other-session", "other-preview", "other-owner" }) commands.UnregisterClient(id);
            ui.UnregisterListener("ui");
            await app.StopAsync(ct);
            await manager.DisposeAsync();
            Directory.Delete(root, recursive: true);
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

    [Theory]
    [InlineData("soft")]
    [InlineData("force")]
    public async Task ReloadRecoversAnUnbridgedPreviewThroughItsExistingOwner(string mode)
    {
        var owners = new BrowserPreviewOwnerService();
        var ui = new BrowserUiBridge(new MainBrowserService(), owners);
        var previews = new WebPreviewService(2000);
        var commands = new BrowserCommandService();
        var operations = new BrowserPreviewOperations(commands, previews, ui, new BrowserPreviewRegistry(), owners);
        Assert.True(previews.SetTarget("first001", "named", "https://example.invalid/page.html"));
        Assert.True(previews.SetTarget("second001", "other", "https://other.invalid/app"));
        var opened = 0;
        var embeddedReloads = 0;
        ui.RegisterListener("ui", "desktop", (_, _) => { }, (_, _) => { }, (_, _, _, _) => { },
            (session, name, url, activate) =>
            {
                opened++;
                Assert.Equal("first001", session);
                Assert.Equal("named", name);
                Assert.False(activate);
                commands.TryRegisterClient("restored", session, name, "frame", message =>
                {
                    if (message.Command == "reload") embeddedReloads++;
                    commands.ReceiveResult(new BrowserWsResult { Id = message.Id, Success = true, Result = "ready" }, "restored");
                }, browserId: "desktop", targetRevision: previews.GetPreviewSession(session, name)?.TargetRevision,
                    ownershipGeneration: owners.GetGeneration(session, name));
            });
        try
        {
            var result = await TlbxMcpBrowserTools.ReloadAsync(commands, previews, operations, "first001", "named", null, mode, TestContext.Current.CancellationToken);
            Assert.NotEqual(true, result.IsError);
            Assert.Equal(1, opened);
            Assert.Equal(0, embeddedReloads);
            Assert.Equal("https://other.invalid/app", previews.GetTargetUrl("second001", "other"));
        }
        finally { commands.UnregisterClient("restored"); ui.UnregisterListener("ui"); }
    }

    [Fact]
    public async Task UncertainReloadIsNeverReplayedAsAnOpen()
    {
        var owners = new BrowserPreviewOwnerService();
        var ui = new BrowserUiBridge(new MainBrowserService(), owners);
        var previews = new WebPreviewService(2000);
        var commands = new BrowserCommandService();
        var operations = new BrowserPreviewOperations(commands, previews, ui, new BrowserPreviewRegistry(), owners);
        Assert.True(previews.SetTarget("first001", "named", "https://example.invalid/page.html"));
        var opened = 0;
        var reloaded = 0;
        ui.RegisterListener("ui", "desktop", (_, _) => { }, (_, _) => { }, (_, _, _, _) => { }, (_, _, _, _) => opened++);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        commands.TryRegisterClient("transport", "first001", "named", "exact-preview", message =>
        {
            reloaded++;
            cancelled.Cancel();
        });
        try
        {
            Assert.Equal("exact-preview", commands.GetStatus(previews.GetTargetUrl("first001", "named"), "first001", "named", "exact-preview").DefaultClient?.PreviewId);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TlbxMcpBrowserTools.ReloadAsync(commands, previews, operations,
                "first001", "named", "exact-preview", "force", cancelled.Token));
            Assert.Equal(1, reloaded);
            Assert.Equal(0, opened);
        }
        finally { commands.UnregisterClient("transport"); ui.UnregisterListener("ui"); }
    }

    [Fact]
    public async Task ReloadDoesNotRecoverAnExplicitStaleClient()
    {
        var owners = new BrowserPreviewOwnerService();
        var ui = new BrowserUiBridge(new MainBrowserService(), owners);
        var previews = new WebPreviewService(2000);
        var commands = new BrowserCommandService();
        var operations = new BrowserPreviewOperations(commands, previews, ui, new BrowserPreviewRegistry(), owners);
        Assert.True(previews.SetTarget("first001", "named", "https://example.invalid/page.html"));
        var result = await TlbxMcpBrowserTools.ReloadAsync(commands, previews, operations,
            "first001", "named", "stale", "force", TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
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
