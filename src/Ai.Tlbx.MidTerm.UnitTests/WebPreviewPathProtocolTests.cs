using Ai.Tlbx.MidTerm.Services.WebPreview;
using Ai.Tlbx.MidTerm.Startup;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class WebPreviewPathProtocolTests
{
    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task ScreenshotLibraryUsesTheOwnedStaticAssetInsideThePreviewRoute(string method)
    {
        var context = new DefaultHttpContext();
        const string originalPath = "/webpreview/preview-route/_assets/html2canvas.min.js";
        context.Request.Path = originalPath;
        context.Request.Method = method;
        context.Items[PreviewProxyAuthorization.RouteItemKey] = "preview-route";
        var calls = 0;
        var middleware = new WebPreviewProxyMiddleware(next =>
        {
            Assert.Equal("/js/html2canvas.min.js", next.Request.Path.Value);
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
        context.Request.Path = "/webpreview/preview-route/_assets/html2canvas.min.js";
        context.Request.Method = method;
        context.Items[PreviewProxyAuthorization.RouteItemKey] = authorizedRoute;
        var middleware = new WebPreviewProxyMiddleware(_ => throw new InvalidOperationException("Must not serve the asset."), new WebPreviewService(2000));

        await middleware.InvokeAsync(context);

        Assert.Equal(status, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/_assets/terminal.min.js")]
    [InlineData("/_assets/../html2canvas.min.js")]
    [InlineData("/_assets/html2canvas.min.js/extra")]
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
