using System.Text.Json;
using Acornima;
using Acornima.Ast;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class WebPreviewImportMapProtocolTests
{
    [Theory]
    [InlineData("/webpreview/route/_access/id/token")]
    [InlineData("/webpreview/outer/_access/parent/secret/webpreview/route/_access/id/token")]
    public void CdnImportsResolveAgainstFinalScriptUrlAndKeepThePrimaryTarget(string prefix)
    {
        const string source = """
            import main from "./first.js";
            import "./side.js";
            export {name} from "../shared.js?cache=a%20b#named";
            export * from "/assets/all.js";
            const deferred = () => import("./lazy.js", {with:{type:"javascript"}});
            import "https://github.com/application.js?site=1#primary";
            """;
        // The original request may redirect to this CDN directory. Its authority
        // must not become the preview's primary target when constructing routes.
        var rewritten = WebPreviewProxyMiddleware.RewriteJavaScriptModuleSpecifiers(
            source, prefix, "https://github.githubassets.com/assets/redirected/main.js?v=1",
            new Uri("https://github.com/login"), "reload");
        var specifiers = ModuleSources(rewritten);
        string[] expected = [
            "https://github.githubassets.com/assets/redirected/first.js",
            "https://github.githubassets.com/assets/redirected/side.js",
            "https://github.githubassets.com/assets/shared.js?cache=a%20b#named",
            "https://github.githubassets.com/assets/all.js",
            "https://github.githubassets.com/assets/redirected/lazy.js"
        ];
        Assert.Equal(expected.Length + 1, specifiers.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            var transport = new Uri("https://preview.example" + specifiers[index]);
            Assert.Equal(prefix + "/_ext", transport.AbsolutePath);
            var query = QueryHelpers.ParseQuery(transport.Query);
            Assert.Equal(expected[index], query["u"].ToString());
            Assert.Equal("reload", query["__mtReloadToken"].ToString());
        }
        var primary = new Uri("https://preview.example" + specifiers[^1]);
        Assert.Equal(prefix + "/application.js", primary.AbsolutePath);
        Assert.Equal("1", QueryHelpers.ParseQuery(primary.Query)["site"].ToString());
        Assert.Equal("reload", QueryHelpers.ParseQuery(primary.Query)["__mtReloadToken"].ToString());
        Assert.Equal("#primary", primary.Fragment);
        Assert.Contains("{with:{type:\"javascript\"}}", rewritten, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("./part.js?__mtReloadToken=site&x=a%20b#part", "https://cdn.example/assets/part.js?__mtReloadToken=site&x=a%20b#part")]
    [InlineData("//other.example/part.js?q=1#part", "https://other.example/part.js?q=1#part")]
    [InlineData("https://other.example/part.js", "https://other.example/part.js")]
    public void NetworkImportQueriesFragmentsAndProtocolRelativeUrlsRemainUpstream(string specifier, string expected)
    {
        const string prefix = "/webpreview/route/_access/id/token";
        var rewritten = WebPreviewProxyMiddleware.RewriteJavaScriptModuleSpecifiers(
            "import " + JsonSerializer.Serialize(specifier) + ";", prefix,
            "https://cdn.example/assets/main.js?old=1", new Uri("https://github.com/"), "reload");
        var transport = new Uri("https://preview.example" + Assert.Single(ModuleSources(rewritten)));
        var query = QueryHelpers.ParseQuery(transport.Query);
        Assert.Equal(expected, query["u"].ToString());
        Assert.Equal("reload", query["__mtReloadToken"].ToString());
    }

    [Fact]
    public void BareImportMapKeysNonnetworkUrlsComputedImportsAndDecoyTextKeepTheirBytes()
    {
        const string source = """
            import "react";
            export * from "@scope/library";
            import "#mapped";
            import "?mapped";
            import "data:text/javascript,export default 1";
            import "blob:https://github.com/id";
            import "node:fs";
            const computed = () => import(getUrl(), {with:{type:"json"}});
            const template = () => import(`./${name}.js`);
            const text = "import './decoy.js'";
            const pattern = /import\(['"]\.\/decoy/;
            // export * from './comment.js';
            const meta = import.meta.url;
            """;
        Assert.Equal(source, WebPreviewProxyMiddleware.RewriteJavaScriptModuleSpecifiers(
            source, "/webpreview/route", "https://cdn.example/assets/main.js", new Uri("https://github.com/")));
    }

    [Fact]
    public void ImportAttributesAndEscapedLiteralsSurviveSourceSpanEdits()
    {
        const string source = """
            import data from ".\u002fdata.json" with { type: "json" };
            export {default} from './other.json' with { type: "json" };
            const decoy = "export * from './unchanged.js'";
            """;
        var rewritten = WebPreviewProxyMiddleware.RewriteJavaScriptModuleSpecifiers(
            source, "/webpreview/route", "https://cdn.example/assets/main.js", new Uri("https://github.com/"));
        var specifiers = ModuleSources(rewritten);
        Assert.Equal(2, specifiers.Count);
        Assert.All(specifiers, value => Assert.StartsWith("/webpreview/route/_ext?u=", value, StringComparison.Ordinal));
        Assert.Contains("with { type: \"json\" }", rewritten, StringComparison.Ordinal);
        Assert.Contains("const decoy = \"export * from './unchanged.js'\";", rewritten, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("import './part.js'; const =", "https://cdn.example/assets/main.js")]
    [InlineData("import './part.js';", null)]
    [InlineData("import './part.js';", "file:///private/main.js")]
    public void InvalidSyntaxOrMissingNetworkBaseDoesNotGuessAResolution(string source, string? finalUrl)
    {
        Assert.Equal(source, WebPreviewProxyMiddleware.RewriteJavaScriptModuleSpecifiers(
            source, "/webpreview/route", finalUrl, new Uri("https://github.com/")));
    }

    [Fact]
    public void AlreadyRoutedImportsDoNotAcquireAnotherCapabilityPrefix()
    {
        const string prefix = "/webpreview/route/_access/id/token";
        const string finalUrl = "https://cdn.example/assets/main.js";
        var first = WebPreviewProxyMiddleware.RewriteJavaScriptModuleSpecifiers(
            "import './part.js';", prefix, finalUrl, new Uri("https://github.com/"), "reload");
        Assert.Equal(first, WebPreviewProxyMiddleware.RewriteJavaScriptModuleSpecifiers(
            first, prefix, finalUrl, new Uri("https://github.com/"), "reload"));
    }

    private static IReadOnlyList<string> ModuleSources(string script)
    {
        var pending = new Stack<Node>();
        var sources = new List<(int Start, string Value)>();
        pending.Push(new Parser().ParseModule(script));
        while (pending.TryPop(out var node))
        {
            var source = node switch
            {
                ImportDeclaration import => import.Source,
                ExportNamedDeclaration export => export.Source,
                ExportAllDeclaration export => export.Source,
                ImportExpression { Source: StringLiteral literal } => literal,
                _ => null
            };
            if (source is not null) sources.Add((source.Start, source.Value));
            foreach (var child in node.ChildNodes) pending.Push(child);
        }
        return sources.OrderBy(source => source.Start).Select(source => source.Value).ToArray();
    }

    [Theory]
    [InlineData("type=importmap")]
    [InlineData("type='importmap'")]
    [InlineData("TYPE=\"IMPORTMAP\"")]
    public void RewrittenModulesDoNotUseUpstreamByteHashes(string attributes)
    {
        const string json = """
            {"imports":{"./component.js":"./component.fingerprint.js"},"scopes":{"./area/":{"shared":"./shared.js"}},"integrity":{"./component.fingerprint.js":"sha256-upstream"},"extension":true}
            """;
        var rewritten = WebPreviewProxyMiddleware.StripImportMapIntegrity($"<script {attributes}>{json}</script>");
        using var map = JsonDocument.Parse(rewritten[(rewritten.IndexOf(">", StringComparison.Ordinal) + 1)..rewritten.LastIndexOf("<", StringComparison.Ordinal)]);
        Assert.False(map.RootElement.TryGetProperty("integrity", out _));
        Assert.Equal("./component.fingerprint.js", map.RootElement.GetProperty("imports").GetProperty("./component.js").GetString());
        Assert.Equal("./shared.js", map.RootElement.GetProperty("scopes").GetProperty("./area/").GetProperty("shared").GetString());
        Assert.True(map.RootElement.GetProperty("extension").GetBoolean());
    }

    [Theory]
    [InlineData("<script type='application/json'>{\"integrity\":{}}</script>")]
    [InlineData("<script data-type='importmap'>{\"integrity\":{}}</script>")]
    [InlineData("<script type='importmap'>{\"imports\":{}}</script>")]
    [InlineData("<script type='importmap'>invalid</script>")]
    public void OtherOrInvalidScriptDataIsPreserved(string html)
    {
        Assert.Equal(html, WebPreviewProxyMiddleware.StripImportMapIntegrity(html));
    }
}
