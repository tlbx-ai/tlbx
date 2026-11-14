using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Mcp;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class BrowserCapabilityProtocolTests
{
    private const string Page = """
        {"opaqueOrigin":true,"secureContext":true,"visibility":"hidden",
         "localStorage":"getter-accessible","sessionStorage":"getter-accessible","indexedDB":"denied",
         "webglApi":"present","webgl2Api":"present","mediaElements":2,"mediaSampled":2,
         "mediaUnpaused":1,"mediaErrors":0,"mediaMaxReadyState":4}
        """;

    [Fact]
    public async Task PassiveProbeUsesExactScopeAndKeepsPresenceSeparateFromUsability()
    {
        var commands = new BrowserCommandService();
        BrowserWsMessage? sent = null;
        commands.TryRegisterClient("bridge", "session", "alpha", "frame", message =>
        {
            sent = message;
            commands.ReceiveResult(new BrowserWsResult { Id = message.Id, Success = true, Result = Page }, "bridge");
        });
        var status = commands.GetStatus("https://example.test/", "session", "alpha", "frame", 1);

        var result = await BrowserCapabilityDiagnostics.ReadAsync(commands, status, "session", "alpha", "frame", TestContext.Current.CancellationToken);

        Assert.NotNull(sent);
        Assert.Equal("exec", sent.Command);
        Assert.Equal("session", sent.SessionId);
        Assert.Equal("alpha", sent.PreviewName);
        Assert.Equal("frame", sent.PreviewId);
        Assert.Equal("observed", result["page"]?["state"]?.GetValue<string>());
        Assert.Equal("denied", result["page"]?["indexedDB"]?.GetValue<string>());
        Assert.Equal("hidden", result["page"]?["visibility"]?.GetValue<string>());
        Assert.Contains("unverified", result["limits"]!.GetValue<string>());
        Assert.Null(result["status"]?["clients"]);
        Assert.Null(result["status"]?["defaultClient"]);
    }

    [Fact]
    public async Task BridgeFailurePreservesOwnerDiagnosisAndOmitsArbitraryPageError()
    {
        var commands = new BrowserCommandService();
        commands.TryRegisterClient("bridge", "session", "alpha", "frame", message =>
            commands.ReceiveResult(new BrowserWsResult { Id = message.Id, Success = false, Error = "private page content" }, "bridge"));
        var status = commands.GetStatus("https://example.test/", "session", "alpha", "frame", 1);

        var result = await BrowserCapabilityDiagnostics.ReadAsync(commands, status, "session", "alpha", "frame", TestContext.Current.CancellationToken);

        Assert.True(result["status"]?["controllable"]?.GetValue<bool>());
        Assert.Equal("unavailable", result["page"]?["state"]?.GetValue<string>());
        Assert.DoesNotContain("private page content", result.ToJsonString());
    }

    [Fact]
    public async Task UnresponsivePageTimesOutWithoutDiscardingStatus()
    {
        var commands = new BrowserCommandService();
        commands.TryRegisterClient("bridge", "session", "alpha", "frame", _ => { });
        var status = commands.GetStatus("https://example.test/", "session", "alpha", "frame", 1);

        var result = await BrowserCapabilityDiagnostics.ReadAsync(commands, status, "session", "alpha", "frame", TestContext.Current.CancellationToken);

        Assert.Equal("timeout", result["page"]?["state"]?.GetValue<string>());
        Assert.True(result["status"]?["controllable"]?.GetValue<bool>());
    }

    [Fact]
    public async Task MissingBridgeDoesNotProbeOrSuggestAutomaticOwnershipTakeover()
    {
        var status = new BrowserStatusResponse
        {
            State = "waiting", HasTarget = true, HasUiClient = true,
            OwnerBrowserId = "offline-owner", OwnerConnected = false
        };
        var result = await BrowserCapabilityDiagnostics.ReadAsync(new(), status, "session", "alpha", null, TestContext.Current.CancellationToken);

        Assert.Equal("not-probed", result["page"]?["state"]?.GetValue<string>());
        Assert.Equal("offline-owner", result["status"]?["owner"]?.GetValue<string>());
        Assert.Contains("browser_claim", result["recovery"]!.ToJsonString());
        Assert.Contains("do not auto-claim", result["recovery"]!.ToJsonString());
        Assert.DoesNotContain("mt_", result.ToJsonString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{malformed")]
    public void InvalidPageReplyIsBoundedAndDoesNotBecomeAnObservedCapability(string value)
    {
        Assert.Equal("invalid-response", BrowserCapabilityDiagnostics.ParsePage(value)["state"]!.GetValue<string>());
        Assert.Equal("invalid-response", BrowserCapabilityDiagnostics.ParsePage(new string('x', 2049))["state"]!.GetValue<string>());
        Assert.Equal("invalid-response", BrowserCapabilityDiagnostics.ParsePage(Page.Replace("\"mediaSampled\":2", "\"mediaSampled\":99", StringComparison.Ordinal))["state"]!.GetValue<string>());
        Assert.Equal("invalid-response", BrowserCapabilityDiagnostics.ParsePage(Page.Replace("\"mediaErrors\":0", "\"mediaErrors\":\"secret\"", StringComparison.Ordinal))["state"]!.GetValue<string>());
        Assert.Null(BrowserCapabilityDiagnostics.ParsePage(Page[..^1] + ",\"unexpectedSecret\":\"private\"}")["unexpectedSecret"]);
    }

    [Fact]
    public void NativeProjectionIsSubstantiallySmallerAndCliProjectionRemainsCliSpecific()
    {
        var commands = new BrowserCommandService();
        commands.TryRegisterClient("bridge", "session", "alpha", "frame", _ => { });
        var previews = new WebPreviewService(2000);
        var cli = BrowserEndpoints.BuildCapabilitiesResponse(commands, previews, null, "session", "alpha", "https://example.test/");
        var native = BrowserCapabilityDiagnostics.Project(cli.Status, "session", "alpha", null, BrowserCapabilityDiagnostics.ParsePage(Page));
        var cliJson = JsonSerializer.Serialize(cli, AppJsonContext.Default.BrowserCapabilitiesResponse);
        var nativeJson = JsonSerializer.Serialize(native, McpJsonContext.Default.JsonObject);

        Assert.True(Encoding.UTF8.GetByteCount(nativeJson) < Encoding.UTF8.GetByteCount(cliJson) / 2);
        Assert.Contains("mt_status", cli.FastCommands);
        Assert.Contains("browser_outline", nativeJson);
        Assert.DoesNotContain("mt_", nativeJson);
    }
}
