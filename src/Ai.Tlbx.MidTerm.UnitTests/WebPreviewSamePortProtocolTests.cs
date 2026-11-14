using System.Net;
using System.Net.WebSockets;
using Ai.Tlbx.MidTerm.Services.Certificates;
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
    [InlineData("application/javascript", true, false)]
    [InlineData("text/javascript", true, false)]
    [InlineData("text/css", true, false)]
    [InlineData("text/plain", true, false)]
    [InlineData("application/problem+json", true, false)]
    [InlineData("application/xml", true, false)]
    [InlineData("application/atom+xml", true, false)]
    [InlineData("image/svg+xml", true, false)]
    [InlineData("image/png", false, true)]
    [InlineData("image/webp", false, true)]
    [InlineData("application/octet-stream", false, false)]
    [InlineData("text/html", false, false)]
    public void ResourceViewersRequireActualNavigationAndPreserveDownloadsAndSubresources(string mediaType, bool text, bool image)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Headers["Sec-Fetch-Mode"] = "navigate";
        context.Request.Headers["Sec-Fetch-Dest"] = "iframe";
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentType = new(mediaType);
        Assert.Equal(text, WebPreviewProxyMiddleware.ShouldRenderTextDocument(context.Request, response));
        Assert.Equal(image, WebPreviewProxyMiddleware.ShouldRenderImageDocument(context.Request, response));
        foreach (var destination in new[] { "script", "style", "image", "empty" })
        {
            context.Request.Headers["Sec-Fetch-Dest"] = destination;
            Assert.False(WebPreviewProxyMiddleware.ShouldRenderTextDocument(context.Request, response));
            Assert.False(WebPreviewProxyMiddleware.ShouldRenderImageDocument(context.Request, response));
        }
        context.Request.Headers["Sec-Fetch-Dest"] = "document";
        context.Request.Headers["Sec-Fetch-Mode"] = "cors";
        Assert.False(WebPreviewProxyMiddleware.ShouldRenderTextDocument(context.Request, response));
        Assert.False(WebPreviewProxyMiddleware.ShouldRenderImageDocument(context.Request, response));
        context.Request.Headers["Sec-Fetch-Mode"] = "navigate";
        foreach (var disposition in new[] { "attachment", "unknown" })
        {
            response.Content.Headers.ContentDisposition = new(disposition);
            Assert.False(WebPreviewProxyMiddleware.ShouldRenderTextDocument(context.Request, response));
            Assert.False(WebPreviewProxyMiddleware.ShouldRenderImageDocument(context.Request, response));
        }
        response.Content.Headers.ContentDisposition = new("inline");
        Assert.Equal(text, WebPreviewProxyMiddleware.ShouldRenderTextDocument(context.Request, response));
        Assert.Equal(image, WebPreviewProxyMiddleware.ShouldRenderImageDocument(context.Request, response));
        foreach (var status in new[] { HttpStatusCode.PartialContent, HttpStatusCode.NoContent, HttpStatusCode.NotModified })
        {
            response.StatusCode = status;
            Assert.False(WebPreviewProxyMiddleware.ShouldRenderTextDocument(context.Request, response));
            Assert.False(WebPreviewProxyMiddleware.ShouldRenderImageDocument(context.Request, response));
        }
    }

    [Fact]
    public void SourceDocumentsEscapeActiveMarkupAndImageDocumentsPreserveRasterBytes()
    {
        const string source = "</pre><script>alert('source')</script><svg onload='source()'/>&entity; a{background:url(/source.svg)}";
        var text = WebPreviewProxyMiddleware.RenderTextDocument(source, "https://user:password@example.test/source.js", "application/javascript");
        var start = text.IndexOf("<pre>", StringComparison.Ordinal) + 5;
        var end = text.IndexOf("</pre>", start, StringComparison.Ordinal);
        Assert.Equal(source, WebUtility.HtmlDecode(text[start..end]));
        Assert.DoesNotContain(source, text, StringComparison.Ordinal);
        Assert.DoesNotContain("password", text, StringComparison.Ordinal);
        Assert.Contains("application/javascript", text, StringComparison.Ordinal);
        var bytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        var image = WebPreviewProxyMiddleware.RenderImageDocument(bytes, "image/png", "https://example.test/image.png");
        Assert.Contains("src=\"data:image/png;base64," + Convert.ToBase64String(bytes) + "\"", image, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => WebPreviewProxyMiddleware.RenderImageDocument(bytes, "image/svg+xml", "https://example.test/image.svg"));
    }

    [Fact]
    public async Task DocumentByteLimitAppliesAfterDecompressionWithoutReturningPartialSource()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(new string('x', 4096));
        using var packed = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(packed, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
            gzip.Write(bytes);
        using var exact = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(packed.ToArray()) };
        exact.Content.Headers.ContentEncoding.Add("gzip");
        var complete = await WebPreviewProxyMiddleware.ReadDocumentBodyAsync(exact, bytes.Length, TestContext.Current.CancellationToken);
        Assert.Null(complete.Error);
        Assert.Equal(bytes, complete.Body);
        using var over = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(packed.ToArray()) };
        over.Content.Headers.ContentEncoding.Add("gzip");
        var rejected = await WebPreviewProxyMiddleware.ReadDocumentBodyAsync(over, bytes.Length - 1, TestContext.Current.CancellationToken);
        Assert.Null(rejected.Body);
        Assert.Contains("exceeds", rejected.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://stream.example/master.m3u8?quality=high&__mtReloadToken=site", "https://stream.example/master.m3u8?quality=high&__mtReloadToken=site")]
    [InlineData("https://user:password@stream.example/redirected/master.m3u8", "https://stream.example/redirected/master.m3u8")]
    [InlineData(null, null)]
    [InlineData("invalid", null)]
    [InlineData("file:///private", null)]
    public void FinalUpstreamMetadataOverwritesSpoofedHeadersAndRemovesCredentials(string? finalUrl, string? expected)
    {
        var context = new DefaultHttpContext();
        context.Response.Headers[WebPreviewProxyMiddleware.UpstreamUrlHeaderName] = "https://spoof.example/";
        WebPreviewProxyMiddleware.SetUpstreamUrlHeader(context.Response, finalUrl);
        Assert.Equal(expected, context.Response.Headers[WebPreviewProxyMiddleware.UpstreamUrlHeaderName].FirstOrDefault());
    }

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
            context.Response.Headers.AccessControlExposeHeaders = "X-Upstream-Spoofed";
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
        Assert.Equal(WebPreviewProxyMiddleware.UpstreamUrlHeaderName,
            Assert.Single(response.Headers.GetValues("Access-Control-Expose-Headers")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        using var denied = new HttpRequestMessage(HttpMethod.Get, address + "/webpreview/route/document");
        denied.Headers.Add("Origin", "null");
        using var error = await http.SendAsync(denied, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.DoesNotContain("allow-same-origin", Assert.Single(error.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
        Assert.False(error.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(error.Headers.Contains("Access-Control-Expose-Headers"));
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
            using var mainCertificate = CertificateGenerator.GenerateSelfSigned(["localhost"], ["127.0.0.1"]);
            var service = new WebPreviewService(2000, BrowserPreviewOriginService.Create(2000), Path.Combine(directory, "cookies"), () => mainCertificate);
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
            var persistedFile = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(directory, "cookies"), "*", SearchOption.AllDirectories));
            Assert.Equal(".txt", Path.GetExtension(persistedFile));
            var persisted = await File.ReadAllTextAsync(persistedFile, ct);
            Assert.Contains("fixture=retained;", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain(token, persisted, StringComparison.Ordinal);
            Assert.DoesNotContain(AuthService.SessionCookieName, persisted, StringComparison.Ordinal);

            using var scoped = new HttpRequestMessage(HttpMethod.Post, address + "/api/browser/preview-client");
            scoped.Content = new StringContent("{\"sessionId\":\"session\"}", System.Text.Encoding.UTF8, "application/json");
            scoped.Headers.Add("Cookie", $"{AuthService.SessionCookieName}={token}");
            scoped.Headers.Add("Origin", "null");
            using var forbidden = await http.SendAsync(scoped, ct);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("127.0.0.2")]
    [InlineData("::1")]
    public async Task NativeCookieRequiresMainListenerCertificateForHttpAndWebSocket(string unrelatedAddress)
    {
        if (unrelatedAddress == "::1" && !System.Net.Sockets.Socket.OSSupportsIPv6)
            Assert.Skip("IPv6 is unavailable.");
        var ct = TestContext.Current.CancellationToken;
        using var mainCertificate = CertificateGenerator.GenerateSelfSigned(["localhost"], ["127.0.0.1"]);
        using var unrelatedCertificate = CertificateGenerator.GenerateSelfSigned(["localhost"], [unrelatedAddress]);
        const string token = "native-cookie-fixture";
        var mainRequests = 0;
        var unrelatedRequests = 0;
        var mainBuilder = WebApplication.CreateBuilder();
        mainBuilder.Logging.ClearProviders();
        mainBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(mainCertificate)));
        await using var main = mainBuilder.Build();
        main.UseWebSockets();
        main.Run(async context =>
        {
            var generic = context.Request.Path == "/generic";
            if (generic) Assert.Null(context.Request.Cookies[AuthService.SessionCookieName]);
            else
            {
                Assert.Equal(token, context.Request.Cookies[AuthService.SessionCookieName]);
                Interlocked.Increment(ref mainRequests);
            }
            if (context.WebSockets.IsWebSocketRequest)
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fixture", ct);
            }
            else await context.Response.WriteAsync(generic ? "generic-preview" : "authenticated-self", ct);
        });
        await main.StartAsync(ct);
        var address = new Uri(Assert.Single(main.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
        var origins = BrowserPreviewOriginService.Create(address.Port);
        var certificateAvailable = false;
        var service = new WebPreviewService(address.Port, origins, mainServerCertificate: () => certificateAvailable ? mainCertificate : null);
        Assert.True(service.SetTarget("session", "before-certificate", address.AbsoluteUri));
        var uninitializedRoute = service.GetPreviewSession("session", "before-certificate")!.RouteKey;
        service.SyncSessionCookieForSelfTarget(uninitializedRoute, token);
        Assert.Empty(service.GetCookies("session", "before-certificate").Cookies);
        certificateAvailable = true;

        async Task Exercise(string host, bool allowed)
        {
            var target = new UriBuilder("https", host, address.Port).Uri;
            var name = Guid.NewGuid().ToString("N");
            Assert.True(service.SetTarget("session", name, target.AbsoluteUri));
            var route = service.GetPreviewSession("session", name)!.RouteKey;
            service.SyncSessionCookieForSelfTarget(route, token);
            if (!allowed)
                Assert.True(service.SetTarget("session", name, "https://external.example/", preserveCookies: true));
            var client = service.GetHttpClient(route);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, target);
                var nativeHeader = service.GetForwardedCookieHeader(route, target);
                Assert.Equal(AuthService.SessionCookieName + "=" + token, nativeHeader);
                request.Headers.Add("Cookie", nativeHeader);
                if (allowed)
                {
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
                    Assert.Equal("authenticated-self", await response.Content.ReadAsStringAsync(ct));
                }
                else await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct));

                using var socket = new ClientWebSocket();
                var socketUri = new UriBuilder(target) { Scheme = "wss" }.Uri;
                service.ConfigureWebSocket(route, socket, socketUri);
                if (allowed) await socket.ConnectAsync(socketUri, ct);
                else await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(socketUri, ct));
                socket.Abort();
                Assert.Null(service.GetForwardedCookieHeader(route, new UriBuilder(target) { Port = address.Port + 1 }.Uri));
                Assert.Null(service.GetForwardedCookieHeader(route, new UriBuilder(target) { Scheme = "http" }.Uri));
            }
            finally { service.ClearTarget("session", name); client.Dispose(); }
        }

        var genericService = new WebPreviewService(address.Port, origins);
        var genericTarget = new Uri(address, "/generic");
        Assert.True(genericService.SetTarget("session", "generic", genericTarget.AbsoluteUri));
        var genericRoute = genericService.GetPreviewSession("session", "generic")!.RouteKey;
        genericService.SyncSessionCookieForSelfTarget(genericRoute, token);
        Assert.Null(genericService.GetForwardedCookieHeader(genericRoute, genericTarget));
        var genericClient = genericService.GetHttpClient(genericRoute);
        try
        {
            Assert.Equal("generic-preview", await genericClient.GetStringAsync(genericTarget.AbsoluteUri, ct));
            using var genericSocket = new ClientWebSocket();
            var genericSocketUri = new UriBuilder(genericTarget) { Scheme = "wss" }.Uri;
            genericService.ConfigureWebSocket(genericRoute, genericSocket, genericSocketUri);
            await genericSocket.ConnectAsync(genericSocketUri, ct);
            genericSocket.Abort();
        }
        finally { genericService.ClearTarget("session", "generic"); genericClient.Dispose(); }

        await Exercise("localhost", true);
        await Exercise("127.0.0.1", true);
        var otherBuilder = WebApplication.CreateBuilder();
        otherBuilder.Logging.ClearProviders();
        otherBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Parse(unrelatedAddress), address.Port, listen => listen.UseHttps(unrelatedCertificate)));
        await using var other = otherBuilder.Build();
        other.Run(async context =>
        {
            Interlocked.Increment(ref unrelatedRequests);
            await context.Response.WriteAsync("unrelated", ct);
        });
        await other.StartAsync(ct);
        await Exercise(unrelatedAddress, false);
        Assert.Equal(4, mainRequests);
        Assert.Equal(0, unrelatedRequests);
    }

    [Fact]
    public void SeededNativeCookieCannotPersistAfterTargetChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-preview-cookie-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var certificate = CertificateGenerator.GenerateSelfSigned(["localhost"], ["127.0.0.1"]);
            var service = new WebPreviewService(2000, BrowserPreviewOriginService.Create(2000), directory, () => certificate);
            Assert.True(service.SetTarget("session", "self", "https://localhost:2000/"));
            var route = service.GetPreviewSession("session", "self")!.RouteKey;
            service.SyncSessionCookieForSelfTarget(route, "native-fixture");
            Assert.True(service.SetTarget("session", "self", "https://external.example/", preserveCookies: true));
            Assert.True(service.SetCookieFromRaw(route, "fixture=retained; Path=/"));
            service.PersistCookies(route);
            var persisted = File.ReadAllText(Assert.Single(Directory.EnumerateFiles(directory)));
            Assert.Contains("fixture=retained;", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("native-fixture", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain(AuthService.SessionCookieName, persisted, StringComparison.Ordinal);
            service.ClearTarget("session", "self");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

}
