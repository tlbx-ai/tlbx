using System.Net;
using System.Diagnostics.CodeAnalysis;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Security;
using Ai.Tlbx.MidTerm.Services.Share;
using Ai.Tlbx.MidTerm.Settings;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Ai.Tlbx.MidTerm.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class WebPreviewSamePortProtocolTests
{
    [Theory]
    [InlineData("https://localhost:2000/")]
    [InlineData("https://127.0.0.1:2000/")]
    [InlineData("https://[::1]:2000/?session=test")]
    public void MainListenerCanPreviewItsOwnUi(string url)
    {
        var service = new WebPreviewService(2000, BrowserPreviewOriginService.Create(2000));
        Assert.True(service.SetTarget("session", "self", url));
        var preview = service.GetPreviewSession("session", "self");
        Assert.NotNull(preview);
        Assert.True(preview.Active);
        Assert.Equal(new Uri(url).ToString(), preview.Url);
    }

    [Theory]
    [InlineData("https://localhost:2000/webpreview/route/")]
    [InlineData("https://localhost:2000/%77ebpreview/route/")]
    [InlineData("https://127.0.0.1:2000/webpreview")]
    [InlineData("https://[::1]:2000/WebPreview/route/_access/owner/token/")]
    public void MainListenerRejectsRecursivePreviewRoutes(string url)
    {
        var service = new WebPreviewService(2000, BrowserPreviewOriginService.Create(2000));
        Assert.False(service.SetTarget("session", "self", url));
        Assert.Null(service.GetPreviewSession("session", "self"));
    }

    [Fact]
    public void CapabilityPathKeepsRelativeResourcesScopedAndRejectsWrongOwnerRoute()
    {
        var registry = new BrowserPreviewRegistry();
        var first = registry.Create("session-a", "first", "route-a", "owner-a");
        var second = registry.Create("session-b", "second", "route-b", "owner-b");
        var context = new DefaultHttpContext();
        context.Request.Path = $"/webpreview/route-a/_access/{first.PreviewId}/{first.PreviewToken}/docs/page.html";
        context.Request.Headers.Origin = "null";
        Assert.True(PreviewProxyAuthorization.TryAuthorize(context, registry));
        Assert.True(PreviewProxyAuthorization.TryParseAccessPath(context.Request.Path, out var route, out _, out _, out var prefix, out var logical));
        Assert.Equal("route-a", route);
        Assert.Equal("/webpreview/route-a/docs/page.html", logical.Value);
        Assert.Equal(prefix, PreviewProxyAuthorization.GetProxyPrefix(context, "route-a"));
        Assert.Equal("/webpreview/route-a/docs/page.html", PreviewProxyAuthorization.RedactAccessPath(context.Request.Path));
        var otherRoute = new DefaultHttpContext();
        otherRoute.Request.Path = $"/webpreview/route-a/_access/{second.PreviewId}/{second.PreviewToken}/secret.json";
        otherRoute.Request.Headers.Origin = "null";
        Assert.False(PreviewProxyAuthorization.TryAuthorize(otherRoute, registry));
        registry.ClearSession("session-a");
        Assert.False(PreviewProxyAuthorization.TryAuthorize(context, registry));
    }

    [Theory]
    [InlineData("_access")]
    [InlineData("%5Faccess")]
    [InlineData("_ac%63ess")]
    public void CapabilityRefererPreservesExternalTargetsAndEscapedUpstreamPaths(string marker)
    {
        var middleware = new WebPreviewProxyMiddleware(_ => Task.CompletedTask, new WebPreviewService(2000));
        var target = new Uri("https://upstream.example/docs/");
        var prefix = $"/webpreview/route/{marker}/owner/fixture-token";
        Assert.Equal("/webpreview/route/docs/a%2Fb.js", PreviewProxyAuthorization.RedactAccessPath(prefix + "/docs/a%2Fb.js"));
        Assert.Equal("https://cdn.example/main.js", middleware.RewriteRefererForUpstream(
            "https://native" + prefix + "/_ext?u=https%3A%2F%2Fcdn.example%2Fmain.js", "route", target));
        Assert.Equal("https://upstream.example/docs/a%2Fb.js", middleware.RewriteRefererForUpstream(
            "https://native" + prefix + "/docs/a%2Fb.js", "route", target));
    }

    [Fact]
    public void OpaqueOriginCannotBorrowAnotherPreviewsCookie()
    {
        var registry = new BrowserPreviewRegistry();
        var client = registry.Create("session", "preview", "route", "owner");
        var bootstrap = new DefaultHttpContext();
        bootstrap.Request.Path = "/webpreview/route/";
        bootstrap.Request.QueryString = new QueryString($"?__mtPreviewId={client.PreviewId}&__mtPreviewToken={client.PreviewToken}");
        Assert.True(PreviewProxyAuthorization.TryAuthorize(bootstrap, registry));
        var cookie = bootstrap.Response.Headers.SetCookie.ToString().Split(';')[0];
        var opaque = new DefaultHttpContext();
        opaque.Request.Path = "/webpreview/route/private.json";
        opaque.Request.Headers.Cookie = cookie;
        opaque.Request.Headers.Origin = "null";
        Assert.False(PreviewProxyAuthorization.TryAuthorize(opaque, registry));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void NativeApiRejectsOpaqueOriginEvenWithCookies(string method)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("192.0.2.5", 2000);
        context.Request.Path = "/api/sessions";
        context.Request.Headers.Origin = "null";
        context.Request.Headers.Cookie = "mt-session=fixture";
        Assert.False(RequestOriginPolicy.Allows(context.Request));
    }

    [Fact]
    public void DirectPreviewAdvertisesTheActualMainOriginIncludingIpAndReverseProxyPort()
    {
        var origins = BrowserPreviewOriginService.Create(2000);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("192.0.2.5", 8443);
        Assert.Equal("https://192.0.2.5:8443", origins.GetOrigin(context.Request));
        Assert.Equal(origins.MainPort, origins.PreviewPort);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/xhtml+xml")]
    [InlineData("image/svg+xml")]
    [InlineData("text/plain")]
    [SuppressMessage("Usage", "IDISP014:Use a single instance of HttpClient", Justification = "Each isolated loopback server fixture owns one client; no clients are created per request.")]
    public async Task HttpPolicyPreventsActiveDocumentsAndErrorsFromRegainingMainOrigin(string contentType)
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = new BrowserPreviewRegistry();
        var capability = registry.Create("session", "preview", "route", "owner");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.Run(async context =>
        {
            PreviewProxyAuthorization.ApplyResponsePolicy(context);
            if (!PreviewProxyAuthorization.TryAuthorize(context, registry))
            {
                context.Response.StatusCode = 401;
                return;
            }
            context.Response.Headers.ContentSecurityPolicy = "sandbox allow-same-origin";
            context.Response.Headers.AccessControlAllowOrigin = "*";
            context.Response.ContentType = contentType;
            await context.Response.WriteAsync("<svg>fixture</svg>", context.RequestAborted);
        });
        await app.StartAsync(ct);
        var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        using var http = new HttpClient();
        using var valid = new HttpRequestMessage(HttpMethod.Get, address + $"/webpreview/route/_access/{capability.PreviewId}/{capability.PreviewToken}/document");
        valid.Headers.Add("Origin", "null");
        using var response = await http.SendAsync(valid, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.DoesNotContain("allow-same-origin", policy, StringComparison.Ordinal);
        Assert.Contains("sandbox", policy, StringComparison.Ordinal);
        Assert.Equal("null", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        using var denied = new HttpRequestMessage(HttpMethod.Get, address + "/webpreview/route/document");
        denied.Headers.Add("Origin", "null");
        using var error = await http.SendAsync(denied, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.DoesNotContain("allow-same-origin", Assert.Single(error.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
        Assert.False(error.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SuppressMessage("Usage", "IDISP014:Use a single instance of HttpClient", Justification = "This temporary authentication host owns one client for all fixture requests.")]
    public async Task ExplicitCapabilityNeverInheritsValidNativeCookieRights(bool opaque)
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("This authentication fixture uses Windows settings/DPAPI.");
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-preview-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new SettingsService(directory);
            var values = settings.Load();
            values.PasswordHash = AuthService.HashPasswordStatic("fixture-local-password");
            settings.Save(values);
            var auth = new AuthService(settings, new ApiKeyService(settings));
            var registry = new BrowserPreviewRegistry();
            var capability = registry.Create("session", "preview", "route", "owner");
            var token = auth.CreateSessionToken();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            await using var app = builder.Build();
            AuthMiddleware.ConfigureAuthMiddleware(app, settings, auth, new ShareGrantService(settings), registry);
            app.Run(async context => await context.Response.WriteAsJsonAsync(RequestAccessContext.HasFullUserAccess(context), context.RequestAborted));
            await app.StartAsync(ct);
            var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            using var http = new HttpClient();
            using var fullRequest = new HttpRequestMessage(HttpMethod.Get, address + "/api/fixture");
            fullRequest.Headers.Add("Cookie", $"{AuthService.SessionCookieName}={token}");
            using var full = await http.SendAsync(fullRequest, ct);
            Assert.Equal(HttpStatusCode.OK, full.StatusCode);
            Assert.Equal("true", await full.Content.ReadAsStringAsync(ct));
            using var previewRequest = new HttpRequestMessage(HttpMethod.Get,
                address + $"/webpreview/route/_access/{capability.PreviewId}/{capability.PreviewToken}/document");
            previewRequest.Headers.Add("Cookie", $"{AuthService.SessionCookieName}={token}");
            if (opaque) previewRequest.Headers.Add("Origin", "null");
            using var preview = await http.SendAsync(previewRequest, ct);
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.Equal("false", await preview.Content.ReadAsStringAsync(ct));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    [SuppressMessage("Usage", "IDISP014:Use a single instance of HttpClient", Justification = "One client per isolated loopback authentication fixture.")]
    public async Task FreshCapabilitySeedsOnlyValidatedNativeCookieForExactSelfListener()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("This authentication fixture uses Windows settings/DPAPI.");
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-preview-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new SettingsService(directory);
            var values = settings.Load();
            values.PasswordHash = AuthService.HashPasswordStatic("fixture-local-password");
            settings.Save(values);
            var keys = new ApiKeyService(settings);
            var apiKey = keys.CreateApiKey("fixture").Token;
            var auth = new AuthService(settings, keys);
            var token = auth.CreateSessionToken();
            var registry = new BrowserPreviewRegistry();
            var service = new WebPreviewService(2000, BrowserPreviewOriginService.Create(2000), Path.Combine(directory, "cookies"));
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(auth);
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            await using var app = builder.Build();
            AuthMiddleware.ConfigureAuthMiddleware(app, settings, auth, new ShareGrantService(settings), registry);
            BrowserEndpoints.MapPreviewClientEndpoint(app, registry, new BrowserPreviewOwnerService(), BrowserPreviewOriginService.Create(2000), service);
            await app.StartAsync(ct);
            var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            using var handler = new HttpClientHandler { UseCookies = false };
            using var http = new HttpClient(handler);
            var scenarios = new (string Url, string? Cookie, bool Key, bool Seed)[]
            {
                ("https://localhost:2000/", token, false, true),
                ("https://127.0.0.1:2000/", token, false, true),
                ("https://[::1]:2000/", token, false, true),
                ("https://localhost:2000/", null, true, false),
                ("https://localhost:2000/", "invalid", true, false),
                ("https://localhost:2000/", null, false, false),
                ("https://localhost:2000/", "invalid", false, false),
                ("https://external.example:2000/", token, false, false),
                ("https://127.0.0.1.nip.io:2000/", token, false, false),
                ("https://localhost:2001/", token, false, false),
                ("http://localhost:2000/", token, false, false)
            };
            foreach (var (url, cookie, key, seed) in scenarios)
            {
                var name = Guid.NewGuid().ToString("N");
                Assert.True(service.SetTarget("session", name, url));
                var preview = service.GetPreviewSession("session", name)!;
                using var request = new HttpRequestMessage(HttpMethod.Post, address + "/api/browser/preview-client");
                request.Content = new StringContent("{\"sessionId\":\"session\",\"previewName\":\"" + name + "\"}", System.Text.Encoding.UTF8, "application/json");
                if (cookie is not null) request.Headers.Add("Cookie", $"{AuthService.SessionCookieName}={cookie}");
                if (key) request.Headers.Add("X-API-Key", apiKey);
                using var response = await http.SendAsync(request, ct);
                Assert.Equal(cookie == token || key ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
                var cookies = service.GetCookies("session", name).Cookies;
                Assert.True(seed == cookies.Any(c => c.Name == AuthService.SessionCookieName), url + " seed=" + seed);
                if (seed)
                {
                    var native = Assert.Single(cookies);
                    Assert.True(native.HttpOnly);
                    Assert.True(native.Secure);
                    Assert.Equal(token, native.Value);
                }
                Assert.Empty(service.GetBrowserCookies(preview.RouteKey).Cookies);
                if (seed && url == "https://localhost:2000/")
                    Assert.True(service.SetCookieFromRaw(preview.RouteKey, "fixture=retained; Path=/; Secure"));
                service.PersistCookies(preview.RouteKey);
            }
            foreach (var file in Directory.EnumerateFiles(Path.Combine(directory, "cookies"), "*", SearchOption.AllDirectories))
            {
                var persisted = await File.ReadAllTextAsync(file, ct);
                Assert.DoesNotContain(token, persisted, StringComparison.Ordinal);
                Assert.DoesNotContain(AuthService.SessionCookieName, persisted, StringComparison.Ordinal);
            }

            using var scoped = new HttpRequestMessage(HttpMethod.Post, address + "/api/browser/preview-client");
            scoped.Content = new StringContent("{\"sessionId\":\"session\"}", System.Text.Encoding.UTF8, "application/json");
            scoped.Headers.Add("Cookie", $"{AuthService.SessionCookieName}={token}");
            scoped.Headers.Add("Origin", "null");
            using var forbidden = await http.SendAsync(scoped, ct);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

}
