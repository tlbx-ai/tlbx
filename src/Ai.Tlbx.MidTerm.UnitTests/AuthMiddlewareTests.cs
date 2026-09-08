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
    [InlineData("/api/certificate/download/crt")]
    [InlineData("/api/certificate/download/mobileconfig")]
    [InlineData("/favicon.svg")]
    [InlineData("/favicon.ico")]
    [InlineData("/site.webmanifest")]
    [InlineData("/sw.js")]
    [InlineData("/android-chrome-192x192.png")]
    [InlineData("/android-chrome-512x512.png")]
    [InlineData("/apple-touch-icon.png")]
    [InlineData("/favicon-16x16.png")]
    [InlineData("/favicon-32x32.png")]
    [InlineData("/img/logo.png")]
    [InlineData("/fonts/Terminus.woff2")]
    public void IsPublicPath_DiscoverabilityAssets_ArePublic(string path)
    {
        Assert.True(AuthMiddleware.IsPublicPath(path));
    }

    [Theory]
    [InlineData("/swagger")]
    [InlineData("/swagger/index.html")]
    [InlineData("/swagger/swagger-ui.css")]
    [InlineData("/swagger/swagger-ui-bundle.js")]
    [InlineData("/openapi/openapi.json")]
    [InlineData("/api/certificate/share-packet")]
    [InlineData("/api/auth/change-password")]
    [InlineData("/api/auth/refresh")]
    [InlineData("/api/auth/future-endpoint")]
    [InlineData("/api/health")]
    [InlineData("/api/version")]
    [InlineData("/api/security/status")]
    [InlineData("/api/shutdown")]
    [InlineData("/api/state")]
    [InlineData("/api/system")]
    [InlineData("/api/paths")]
    [InlineData("/api/sessions/abc/state")]
    [InlineData("/api/security/api-keys")]
    [InlineData("/api/browser/status")]
    [InlineData("/api/certificate/regenerate")]
    [InlineData("/api/certificate/unknown")]
    [InlineData("/api/bootstrap/login.png")]
    [InlineData("/api/sessions/not-a-real-id.ico")]
    [InlineData("/api/input-history/not-a-real-id.png")]
    [InlineData("/api/security/api-keys.woff2")]
    [InlineData("/api/sessions/not-a-real-id.woff")]
    [InlineData("/api/commands/file.webmanifest")]
    [InlineData("/api/sw.js")]
    [InlineData("/sw.js/private")]
    [InlineData("/favicon/android-chrome-192x192.png")]
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
    [InlineData("POST", "/api/auth/change-password", false)]
    [InlineData("POST", "/api/auth/logout", false)]
    [InlineData("POST", "/api/auth/future-endpoint", false)]
    [InlineData("GET", "/api/shutdown", false)]
    [InlineData("POST", "/api/shutdown", false)]
    [InlineData("POST", "/api/certificate/info", false)]
    [InlineData("GET", "/sw.js", true)]
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
