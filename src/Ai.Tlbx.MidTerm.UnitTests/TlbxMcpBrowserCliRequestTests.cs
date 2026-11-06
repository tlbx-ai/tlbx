using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Mcp;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TlbxMcpBrowserCliRequestTests
{
    [Fact]
    public void EveryDomToolPreservesTheLegacyBrowserRequestOptionsAndSharedDefaults()
    {
        var tools = TlbxMcpBrowserTools.CreateTools().OfType<TlbxMcpBrowserTools>()
            .Where(tool => tool.BrowserCommand is not null).ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);
        (string Tool, string[] Cli, object Arguments)[] cases =
        [
            ("browser_query", ["query", "body"], new { selector = "body" }),
            ("browser_query", ["query", "#root", "--depth", "7", "--text", "--timeout", "300"], new { selector = "#root", maxDepth = 7, textOnly = true, timeout = 300 }),
            ("browser_click", ["click", "[data-name='Grüße 東京']"], new { selector = "[data-name='Grüße 東京']" }),
            ("browser_fill", ["fill", "input", ""], new { selector = "input", value = "" }),
            ("browser_fill", ["fill", "input", " \t\r\n"], new { selector = "input", value = " \t\r\n" }),
            ("browser_evaluate", ["exec", "({text: `Grüße 東京\n'\"`})", "--timeout", "300"], new { code = "({text: `Grüße 東京\n'\"`})", timeout = 300 }),
            ("browser_screenshot", ["screenshot", "--full-page"], new { fullPage = true }),
            ("browser_snapshot", ["snapshot"], new { }),
            ("browser_wait", ["wait", "#ready"], new { selector = "#ready" }),
            ("browser_wait", ["wait", "#ready", "--timeout", "300"], new { selector = "#ready", timeout = 300 }),
            ("browser_scroll", ["scroll", "window", "600"], new { }),
            ("browser_scroll", ["scroll", "window", "1.25", "-2.5"], new { deltaY = 1.25, deltaX = -2.5 }),
            ("browser_scroll", ["scroll", "bottom"], new { to = "bottom" }),
            ("browser_wheel", ["wheel", "window", "down", "1"], new { }),
            ("browser_wheel", ["wheel", "up", "2"], new { direction = "up", steps = 2 }),
            ("browser_wheel", ["wheel", "#pane", "-1.25", "3"], new { selector = "#pane", deltaY = -1.25, steps = 3 }),
            ("browser_outline", ["outline"], new { }),
            ("browser_outline", ["outline", "--depth", "7"], new { maxDepth = 7 }),
            ("browser_attributes", ["attrs", "input"], new { selector = "input" }),
            ("browser_css", ["css", "body", "font-size,--theme"], new { selector = "body", properties = new[] { "font-size", "--theme" } }),
            ("browser_css", ["css", "body", "font-size,--theme"], new { selector = "body", props = "font-size,--theme" }),
            ("browser_console", ["log"], new { }),
            ("browser_console", ["log", "error"], new { filter = "error" }),
            ("browser_console", ["log", "warn"], new { filter = "warn" }),
            ("browser_links", ["links"], new { }),
            ("browser_submit", ["submit"], new { }),
            ("browser_submit", ["submit", "#form"], new { selector = "#form" }),
            ("browser_forms", ["forms"], new { }),
            ("browser_forms", ["forms", "#form"], new { selector = "#form" }),
            ("browser_url", ["url"], new { })
        ];
        Assert.All(tools.Keys, tool => Assert.Contains(cases, item => item.Tool == tool));
        Assert.Multiple(cases.Select(item => (Action)(() =>
        {
            var cli = BrowserEndpoints.ParseCliArgs(item.Cli[0], [.. item.Cli, "--session", "first001", "--preview", "alpha"]);
            Assert.NotNull(cli);
            using var properties = JsonSerializer.SerializeToElement(item.Arguments).EnumerateObject();
            var values = properties.ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
            var native = TlbxMcpBrowserTools.BuildCommand(tools[item.Tool].BrowserCommand!, values, "first001", "alpha", null);
            Assert.True(JsonNode.DeepEquals(EffectiveRequest(cli), EffectiveRequest(native)),
                $"{item.Tool}: CLI {EffectiveRequest(cli)}; MCP {EffectiveRequest(native)}");
        })).ToArray());
    }

    private static JsonObject EffectiveRequest(BrowserCommandRequest request)
    {
        var value = JsonSerializer.SerializeToNode(request, AppJsonContext.Default.BrowserCommandRequest)!.AsObject();
        value["timeout"] = BrowserCommandService.ResolveTimeoutSeconds(request);
        if (request.Command is "scroll" or "wheel" && request.Selector is null) value["selector"] = "window";
        if (request.Command == "scroll")
        {
            // The shared proxy's scroll handler reads axes from value, not the
            // wheel-only delta fields. An omitted horizontal delta means zero.
            value.Remove("deltaX");
            value.Remove("deltaY");
            if (double.TryParse(request.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dy))
                value["value"] = string.Create(CultureInfo.InvariantCulture, $"{dy} 0");
        }
        return value;
    }
}
