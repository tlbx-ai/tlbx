using Ai.Tlbx.MidTerm.Startup;
using Ai.Tlbx.MidTerm.Services.Browser;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public class AuthMiddlewareTests
{
    [Theory]
    [InlineData("/api/bootstrap/login")]
    [InlineData("/api/certificate/info")]
    [InlineData("/api/certificate/download/pem")]
    [InlineData("/sw.js")]
    [InlineData("/img/logo.png")]
    [InlineData("/fonts/Terminus.woff2")]
    public void IsPublicPath_DiscoverabilityAssets_ArePublic(string path)
    {
        Assert.True(AuthMiddleware.IsPublicPath(path));
    }

    [Theory]
    [InlineData("/swagger")]
    [InlineData("/openapi/openapi.json")]
    [InlineData("/api/auth/future-endpoint")]
    [InlineData("/api/sessions/abc/state")]
    [InlineData("/api/bootstrap/login.png")]
    [InlineData("/api/security/api-keys.woff2")]
    [InlineData("/api/commands/file.webmanifest")]
    [InlineData("/sw.js/private")]
    [InlineData("/uploads/private.png")]
    [InlineData("/ws/state")]
    public void IsPublicPath_RemoteControlEndpoints_RemainProtected(string path)
    {
        Assert.False(AuthMiddleware.IsPublicPath(path));
    }

    [Theory]
    [InlineData("GET", "/api/bootstrap/login", true)]
    [InlineData("POST", "/api/bootstrap/login", false)]
    [InlineData("POST", "/api/auth/login", true)]
    [InlineData("GET", "/api/auth/login", false)]
    [InlineData("POST", "/api/auth/future-endpoint", false)]
    [InlineData("HEAD", "/sw.js", true)]
    [InlineData("POST", "/sw.js", false)]
    public void PublicAccess_RequiresAnExplicitMethodAndPath(string method, string path, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        Assert.Equal(expected, AuthMiddleware.IsPublicRequest(context.Request));
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

}
