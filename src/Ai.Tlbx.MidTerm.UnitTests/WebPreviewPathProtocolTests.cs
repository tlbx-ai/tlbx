using Ai.Tlbx.MidTerm.Services.WebPreview;
using Ai.Tlbx.MidTerm.Startup;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using System.Text.RegularExpressions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class WebPreviewPathProtocolTests
{
    [Theory]
    [InlineData("/webpreview/route/_access/id/token")]
    [InlineData("/webpreview/outer/_access/parent/secret/webpreview/route/_access/id/token")]
    public void RedirectedCdnStylesheetsKeepImportsAndImagesAtTheirFinalDirectory(string prefix)
    {
        const string css = "@import './child.css';#css-probe{background-image:url('../img/probe.svg');width:200px;height:60px}";
        var rewritten = WebPreviewProxyMiddleware.RewriteStylesheetResources(css, prefix,
            "http://localhost:2314/cdn/styles/main.css", new Uri("http://127.0.0.1:2314/css"), "reload");
        var paths = StylesheetPaths(rewritten);
        Assert.Equal(2, paths.Length);
        string[] expected = ["http://localhost:2314/cdn/styles/child.css", "http://localhost:2314/cdn/img/probe.svg"];
        for (var index = 0; index < paths.Length; index++)
        {
            var transport = new Uri("https://preview.example" + paths[index]);
            Assert.Equal(prefix + "/_ext", transport.AbsolutePath);
            var query = QueryHelpers.ParseQuery(transport.Query);
            Assert.Equal(expected[index], query["u"].ToString());
            Assert.Equal("reload", query["__mtReloadToken"].ToString());
        }
        Assert.Equal(rewritten, WebPreviewProxyMiddleware.RewriteStylesheetResources(rewritten, prefix,
            "http://localhost:2314/cdn/styles/main.css", new Uri("http://127.0.0.1:2314/css"), "reload"));
    }

    [Theory]
    [InlineData("a{background:url(/root/image.svg#symbol)}", "https://cdn.example/root/image.svg", "#symbol")]
    [InlineData("@import url(//fonts.example/style.css) layer(theme) screen;", "https://fonts.example/style.css", "")]
    [InlineData("@import '?theme=dark';", "https://cdn.example/assets/main.css?theme=dark", "")]
    [InlineData("a{background:url('./image.svg?__mtReloadToken=site&x=a%20b#symbol')}", "https://cdn.example/assets/image.svg?__mtReloadToken=site&x=a%20b", "#symbol")]
    [InlineData("@font-face{src:local('Example'),url('../fonts/font.woff2#font') format('woff2')}", "https://cdn.example/fonts/font.woff2", "#font")]
    [InlineData("a{background:url(image\\ name\\(1\\).svg)}", "https://cdn.example/assets/image%20name(1).svg", "")]
    [InlineData("@import '.\\2f child.css' supports(display: grid);", "https://cdn.example/assets/child.css", "")]
    public void StylesheetResourceTokensPreserveUpstreamQueriesFragmentsFontsAndCssEscapes(string css, string expected, string fragment)
    {
        var rewritten = WebPreviewProxyMiddleware.RewriteStylesheetResources(css, "/webpreview/route/_access/id/token",
            "https://cdn.example/assets/main.css?old=1", new Uri("https://primary.example/"), "reload");
        var transport = new Uri("https://preview.example" + Assert.Single(StylesheetPaths(rewritten)));
        var query = QueryHelpers.ParseQuery(transport.Query);
        Assert.Equal(expected, query["u"].ToString());
        Assert.Equal("reload", query["__mtReloadToken"].ToString());
        Assert.Equal(fragment, transport.Fragment);
        if (css.Contains("layer(theme)", StringComparison.Ordinal)) Assert.Contains("layer(theme) screen;", rewritten, StringComparison.Ordinal);
        if (css.Contains("supports(", StringComparison.Ordinal)) Assert.Contains("supports(display: grid);", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void PrimaryStylesheetResourcesKeepTheirCapabilityAndRealTransportNamedQueriesStayInsideExternalUrls()
    {
        const string prefix = "/webpreview/route/_access/id/token";
        var rewritten = WebPreviewProxyMiddleware.RewriteStylesheetResources(
            "a{background:url('../image.svg?mode=one#symbol');mask:url('./mask.svg?__mtPreviewToken=site')}", prefix,
            "https://primary.example/assets/main.css", new Uri("https://primary.example/app"), "reload");
        var paths = StylesheetPaths(rewritten);
        var direct = new Uri("https://preview.example" + paths[0]);
        Assert.Equal(prefix + "/image.svg", direct.AbsolutePath);
        Assert.Equal("#symbol", direct.Fragment);
        Assert.Equal("one", QueryHelpers.ParseQuery(direct.Query)["mode"].ToString());
        var external = new Uri("https://preview.example" + paths[1]);
        Assert.Equal(prefix + "/_ext", external.AbsolutePath);
        Assert.Equal("https://primary.example/assets/mask.svg?__mtPreviewToken=site", QueryHelpers.ParseQuery(external.Query)["u"].ToString());
    }

    [Theory]
    [InlineData("a{content:\"url('../text') @import 'text.css'\"}/* url('../comment') */")]
    [InlineData("/* unterminated comment url('../comment')")]
    [InlineData("a{mask:url(#local);background:url(data:image/png;base64,abcd);cursor:url(blob:https://site/id)}")]
    [InlineData("a{background:url(custom:resource);mask:url(about:blank);cursor:url(file:///private)}")]
    [InlineData("a{background:url('');mask:url(var(--resource))}")]
    public void StylesheetRewritingPreservesNonresourcesAndOtherSchemes(string css)
    {
        Assert.Equal(css, WebPreviewProxyMiddleware.RewriteStylesheetResources(css, "/webpreview/route",
            "https://cdn.example/assets/main.css", new Uri("https://primary.example/")));
    }

    [Fact]
    public void MissingStylesheetBaseDoesNotGuessDocumentCoordinatesAndInlineImportsStayRelative()
    {
        const string css = "@import './child.css';a{background:url('../image.svg')}";
        Assert.Equal(css, WebPreviewProxyMiddleware.RewriteStylesheetResources(css, "/webpreview/route", null, null));
        Assert.Equal(css, WebPreviewProxyMiddleware.RewriteCssImports(css, "/webpreview/route", "https", "primary.example", null));
    }

    private static string[] StylesheetPaths(string css) => Regex.Matches(css, "\"(/webpreview/[^\"]+)\"", RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)).Select(match => match.Groups[1].Value).ToArray();

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task ScreenshotLibraryUsesTheOwnedStaticAssetInsideThePreviewRoute(string method)
    {
        var context = new DefaultHttpContext();
        const string originalPath = "/webpreview/preview-route/_assets/dom-capture.min.js";
        context.Request.Path = originalPath;
        context.Request.Method = method;
        context.Items[PreviewProxyAuthorization.RouteItemKey] = "preview-route";
        var calls = 0;
        var middleware = new WebPreviewProxyMiddleware(next =>
        {
            Assert.Equal("/js/dom-capture.min.js", next.Request.Path.Value);
            Assert.Equal(method, next.Request.Method);
            Assert.Equal("preview-route", next.Items[PreviewProxyAuthorization.RouteItemKey]);
            calls++;
            return Task.CompletedTask;
        }, new WebPreviewService(2000));

        await middleware.InvokeAsync(context);

        Assert.Equal(1, calls);
        Assert.Equal(originalPath, context.Request.Path.Value);
    }

    [Theory]
    [InlineData("POST", "preview-route", 405)]
    [InlineData("GET", "another-route", 403)]
    public async Task ScreenshotLibraryRetainsPreviewAuthorizationAndReadOnlyMethods(string method, string authorizedRoute, int status)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/webpreview/preview-route/_assets/dom-capture.min.js";
        context.Request.Method = method;
        context.Items[PreviewProxyAuthorization.RouteItemKey] = authorizedRoute;
        var middleware = new WebPreviewProxyMiddleware(_ => throw new InvalidOperationException("Must not serve the asset."), new WebPreviewService(2000));

        await middleware.InvokeAsync(context);

        Assert.Equal(status, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/_assets/terminal.min.js")]
    [InlineData("/_assets/../dom-capture.min.js")]
    [InlineData("/_assets/dom-capture.min.js/extra")]
    public async Task ScreenshotAssetRouteDoesNotExposeOtherHostFiles(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/webpreview/preview-route" + path;
        context.Response.Body = new MemoryStream();
        var middleware = new WebPreviewProxyMiddleware(_ => throw new InvalidOperationException("Must not serve another host asset."), new WebPreviewService(2000));

        await middleware.InvokeAsync(context);

        Assert.Equal(502, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/docs/relative.js", "/docs/relative.js")]
    [InlineData("/api/probe", "/api/probe")]
    [InlineData("/api/cookies", "/api/cookies")]
    [InlineData("/echo", "/echo")]
    [InlineData("/docs/page.html", "/docs/page.html")]
    [InlineData("/docs/page.html/action", "/docs/page.html/action")]
    [InlineData("docs/relative.js", "/docs/relative.js")]
    public void DocumentTargetDoesNotPrefixRootedAssetsApisOrSockets(string path, string expected)
    {
        var target = new Uri("http://127.0.0.1:53836/docs/page.html?tenant=connect#section");
        Assert.Equal(expected, WebPreviewProxyMiddleware.BuildUpstreamPath(target, path));
    }

    [Theory]
    [InlineData("http://upstream.example/docs/page.html?tenant=connect#section", "/docs/page.html")]
    [InlineData("http://upstream.example/docs/", "/docs/")]
    [InlineData("http://upstream.example/", "/")]
    public void BarePreviewRouteRetainsTheConfiguredEntryPath(string target, string expected)
    {
        Assert.Equal(expected, WebPreviewProxyMiddleware.BuildUpstreamPath(new Uri(target), ""));
    }

    [Theory]
    [InlineData("/webpreview/preview-route", "", "/login.html")]
    [InlineData("/webpreview/preview-route/", "/", "/")]
    [InlineData("/webpreview/preview-route/api/bootstrap", "/api/bootstrap", "/api/bootstrap")]
    public void LoginEntryAndExplicitOriginNavigationRemainDistinct(string proxyPath, string remaining, string upstream)
    {
        Assert.True(WebPreviewProxyMiddleware.TryParseProxyRoute(proxyPath, out var route, out var path));
        Assert.Equal("preview-route", route);
        Assert.Equal(remaining, path);
        Assert.Equal(upstream, WebPreviewProxyMiddleware.BuildUpstreamPath(new Uri("https://localhost:2199/login.html"), path));
    }

    [Theory]
    [InlineData("http://upstream.example/docs/page.html?tenant=connect#section")]
    [InlineData("http://upstream.example/docs/")]
    [InlineData("http://upstream.example/")]
    public void ExplicitRootNavigationDoesNotReloadTheConfiguredDocument(string target)
    {
        Assert.Equal("/", WebPreviewProxyMiddleware.BuildUpstreamPath(new Uri(target), "/"));
    }

    [Fact]
    public void InjectedDocumentBaseResolvesRelativeAssetBeforeUpstreamMapping()
    {
        const string prefix = "/webpreview/0123456789abcdefabcd";
        var target = new Uri("http://127.0.0.1:53836/docs/page.html?tenant=connect#section");
        var baseHref = WebPreviewProxyMiddleware.BuildInjectedBaseHref(prefix, target.AbsoluteUri, null, "<head></head>");
        var asset = new Uri(new Uri("https://localhost:2101" + baseHref), "relative.js?mode=one#v2");
        Assert.Equal("/docs/relative.js", WebPreviewProxyMiddleware.BuildUpstreamPath(target, asset.AbsolutePath[prefix.Length..]));
        Assert.Equal("?mode=one", asset.Query);
        Assert.Equal("#v2", asset.Fragment);

        var fragment = new Uri(new Uri("https://localhost:2101" + baseHref), "#next");
        Assert.Equal(prefix + target.AbsolutePath, fragment.AbsolutePath);
        Assert.Equal(target.Query, fragment.Query);
        Assert.Equal("#next", fragment.Fragment);

        foreach (var upstreamBase in new[] { "/docs/", "/docs/page.html?x=1&y=2" })
        {
            var explicitBase = WebPreviewProxyMiddleware.BuildInjectedBaseHref(prefix, target.AbsoluteUri,
                System.Net.WebUtility.HtmlEncode(upstreamBase), "<head></head>");
            Assert.Equal(prefix + upstreamBase, explicitBase);
        }
    }
}
