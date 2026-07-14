using Ai.Tlbx.MidTerm.Startup;
using Ai.Tlbx.MidTerm.Services.Browser;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public class AuthMiddlewareTests
{
    [Theory]
    [InlineData("/swagger")]
    [InlineData("/swagger/index.html")]
    [InlineData("/swagger/swagger-ui.css")]
    [InlineData("/swagger/swagger-ui-bundle.js")]
    [InlineData("/openapi/openapi.json")]
    [InlineData("/api/certificate/info")]
    [InlineData("/api/certificate/download/pem")]
    [InlineData("/api/certificate/download/crt")]
    [InlineData("/api/certificate/download/mobileconfig")]
    [InlineData("/api/certificate/share-packet")]
    public void IsPublicPath_DiscoverabilityAssets_ArePublic(string path)
    {
        Assert.True(AuthMiddleware.IsPublicPath(path));
    }

    [Theory]
    [InlineData("/api/state")]
    [InlineData("/api/system")]
    [InlineData("/api/paths")]
    [InlineData("/api/sessions/abc/state")]
    [InlineData("/api/security/api-keys")]
    [InlineData("/api/browser/status")]
    [InlineData("/api/certificate/regenerate")]
    [InlineData("/api/certificate/unknown")]
    [InlineData("/ws/state")]
    public void IsPublicPath_RemoteControlEndpoints_RemainProtected(string path)
    {
        Assert.False(AuthMiddleware.IsPublicPath(path));
    }

    [Fact]
    public void AllowsBrowserPreviewWebSocket_WithValidPreviewToken_ReturnsTrue()
    {
        var registry = new BrowserPreviewRegistry();
        var created = registry.Create("session-a", "default", "route-a");
        var context = new DefaultHttpContext();
        context.Request.Path = "/ws/browser";
        context.Request.QueryString = new QueryString(
            $"?previewId={created.PreviewId}&token={created.PreviewToken}");

        var allowed = AuthMiddleware.AllowsBrowserPreviewWebSocket(context.Request, registry);

        Assert.True(allowed);
    }

    [Fact]
    public void AllowsBrowserPreviewWebSocket_WithWrongToken_ReturnsFalse()
    {
        var registry = new BrowserPreviewRegistry();
        var created = registry.Create("session-a", "default", "route-a");
        var context = new DefaultHttpContext();
        context.Request.Path = "/ws/browser";
        context.Request.QueryString = new QueryString(
            $"?previewId={created.PreviewId}&token=wrong");

        var allowed = AuthMiddleware.AllowsBrowserPreviewWebSocket(context.Request, registry);

        Assert.False(allowed);
    }

    [Fact]
    public void AllowsPreviewOriginProxyRequest_WebPreviewRouteOnPreviewOrigin_ReturnsTrue()
    {
        var previewOrigin = new BrowserPreviewOriginService(mainPort: 2000, previewPort: 2001, isEnabled: true);
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("midterm.local", 2001);
        context.Request.Path = "/webpreview/route-a/";

        var allowed = AuthMiddleware.AllowsPreviewOriginProxyRequest(context.Request, previewOrigin);

        Assert.True(allowed);
    }

    [Fact]
    public void AllowsPreviewOriginProxyRequest_LeakedLoginPathWithPreviewReferer_ReturnsTrue()
    {
        var previewOrigin = new BrowserPreviewOriginService(mainPort: 2000, previewPort: 2001, isEnabled: true);
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("midterm.local", 2001);
        context.Request.Path = "/login.html";
        context.Request.Headers.Referer = "https://midterm.local:2001/webpreview/route-a/";

        var allowed = AuthMiddleware.AllowsPreviewOriginProxyRequest(context.Request, previewOrigin);

        Assert.True(allowed);
    }

    [Fact]
    public void AllowsPreviewOriginProxyRequest_MainOriginLeakedPath_ReturnsFalse()
    {
        var previewOrigin = new BrowserPreviewOriginService(mainPort: 2000, previewPort: 2001, isEnabled: true);
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("midterm.local", 2000);
        context.Request.Path = "/login.html";
        context.Request.Headers.Referer = "https://midterm.local:2001/webpreview/route-a/";

        var allowed = AuthMiddleware.AllowsPreviewOriginProxyRequest(context.Request, previewOrigin);

        Assert.False(allowed);
    }
}
