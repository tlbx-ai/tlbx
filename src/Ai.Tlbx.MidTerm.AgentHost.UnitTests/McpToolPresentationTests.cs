using System.Text.Json;
using Ai.Tlbx.MidTerm.AgentHost;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.AgentHost.UnitTests;

public sealed class McpToolPresentationTests
{
    [Fact]
    public void NativeCodexIdentityAndArgumentsSurviveTheBinaryHistoryContract()
    {
        using var item = JsonDocument.Parse("""
            {"server":"inventory","tool":"find","arguments":{"query":"Grüße 😀","limit":2}}
            """);
        var presentation = AppServerControlToolPresentationProjector.FromCodex(
            "mcp_tool_call", "completed", null, item.RootElement, _ => "https://inventory.example");
        var payload = NativeHistoryCodec.Encode(presentation,
            AppServerControlHostJsonContext.Default.AppServerControlToolPresentation);
        var copy = NativeHistoryCodec.Decode(payload,
            AppServerControlHostJsonContext.Default.AppServerControlToolPresentation);
        Assert.Equal("find", copy.ToolName);
        Assert.Equal("inventory", copy.Mcp?.ServerName);
        Assert.Equal("https://inventory.example", copy.Mcp?.ServerAddress);
        using var parameters = JsonDocument.Parse(copy.Mcp!.Parameters!);
        Assert.Equal("Grüße 😀", parameters.RootElement.GetProperty("query").GetString());
        Assert.Equal(2, parameters.RootElement.GetProperty("limit").GetInt32());
    }

    [Fact]
    public void NamespacedProviderToolsKeepMcpIdentityWhenOutputHasNoArguments()
    {
        using var input = JsonDocument.Parse("""{"path":"folder/file.txt"}""");
        var presentation = AppServerControlToolPresentationProjector.FromClaude(
            "mcp__files__read", "in_progress", input.RootElement, _ => "files-server");
        AppServerControlToolPresentationProjector.Merge(presentation,
            AppServerControlToolPresentationProjector.FromAcp(
                "tool", "completed", "mcp__files__read", "read", null));
        Assert.Equal("read", presentation.ToolName);
        Assert.Equal("files", presentation.Mcp?.ServerName);
        Assert.Equal("files-server", presentation.Mcp?.ServerAddress);
        Assert.Equal(input.RootElement.GetRawText(), presentation.Mcp?.Parameters);
        Assert.Null(AppServerControlToolPresentationProjector.FromClaude("Read", "completed", input.RootElement).Mcp);
    }

    [Fact]
    public void OversizedParametersAreBoundedWithAnExplicitRecordingMarker()
    {
        var source = new AppServerControlToolPresentation
        {
            Mcp = new() { ServerName = "large", Parameters = new string('x', 40_000) }
        };
        var copy = AppServerControlToolPresentationProtocol.Clone(source)!;
        Assert.True(copy.Mcp!.ParametersTruncated);
        Assert.Equal(32_768, copy.Mcp.Parameters!.Length);
        Assert.NotSame(source.Mcp, copy.Mcp);
    }

    [Fact]
    public void DisplayAddressesExcludeUrlCredentialsAndPreferTheNativeTlbxEndpoint()
    {
        var registry = new AppServerControlMcpToolRegistry();
        registry.Configure(new() { Command = "mt", DisplayAddress = "https://localhost:2199" });
        registry.Register("remote", "https://owner:secret@example.test:8443/private/token?key=secret#secret");
        registry.Register("tlbx", "dotnet");
        Assert.Equal("https://example.test:8443", registry.Resolve("remote"));
        Assert.Equal("https://localhost:2199", registry.Resolve("tlbx"));
    }
}
