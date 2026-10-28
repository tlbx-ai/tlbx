using Ai.Tlbx.MidTerm.Services.WebPreview;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class WebPreviewPathProtocolTests
{
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
        Assert.Equal(expected, WebPreviewProxyMiddleware.BuildUpstreamPath(new Uri(target), "/"));
        Assert.Equal(expected, WebPreviewProxyMiddleware.BuildUpstreamPath(new Uri(target), ""));
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
    }
}
