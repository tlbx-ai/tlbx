using Ai.Tlbx.MidTerm.Services.WebPreview;
using Ai.Tlbx.MidTerm.Services.Browser;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public class WebPreviewServiceTests
{
    [Fact]
    public void SetTarget_PathWithTrailingSlash_PreservesTrailingSlash()
    {
        var service = new WebPreviewService(serverPort: 2000);

        var ok = service.SetTarget("session-1", null, "https://example.com/coaching/plans/");

        Assert.True(ok);
        var targetUri = service.GetTargetUri("session-1");
        Assert.NotNull(targetUri);
        Assert.Equal("/coaching/plans/", targetUri!.AbsolutePath);
    }

    [Fact]
    public void ShouldAcceptPreviewCertificate_AllowsRemoteHttpsCertificateErrors()
    {
        var target = new Uri("https://syno.kunzebau.de:5001/sharing/jh1IVMrgW");

        var accepted = WebPreviewService.ShouldAcceptPreviewCertificate(
            target,
            SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.True(accepted);
    }

    [Fact]
    public void ShouldAcceptPreviewCertificate_RequiresTargetForCertificateErrors()
    {
        var accepted = WebPreviewService.ShouldAcceptPreviewCertificate(
            null,
            SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.False(accepted);
    }

    [Fact]
    public void ShouldAcceptPreviewCertificate_AlwaysAcceptsCleanCertificate()
    {
        var accepted = WebPreviewService.ShouldAcceptPreviewCertificate(
            null,
            SslPolicyErrors.None);

        Assert.True(accepted);
    }

    [Fact]
    public async Task ConnectPreviewSocketAsync_ConnectsToLocalhostListener()
    {
        using var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync();

        await using var stream = await WebPreviewService.ConnectPreviewSocketAsync(
            "localhost",
            port,
            CancellationToken.None);
        using var accepted = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(stream.CanRead);
        Assert.True(accepted.Connected);
    }

    [Fact]
    public void GetBrowserCookies_ExcludesHttpOnlyCookies()
    {
        var service = new WebPreviewService(serverPort: 2000);
        Assert.True(service.SetTarget("session-1", null, "https://example.com"));
        Assert.True(service.TryGetPreviewRouteKey("session-1", null, out var routeKey));
        Assert.True(service.SetCookieFromRaw(routeKey, "theme=dark; Path=/"));
        Assert.True(service.SetCookieFromRaw(routeKey, "session=abc123; Path=/; HttpOnly"));

        var browserCookies = service.GetBrowserCookies(routeKey, new Uri("https://example.com/"));
        var allCookies = service.GetCookies("session-1");

        Assert.Contains("theme=dark", browserCookies.Header, StringComparison.Ordinal);
        Assert.DoesNotContain("session=abc123", browserCookies.Header, StringComparison.Ordinal);
        Assert.Contains(allCookies.Cookies, cookie => cookie.Name == "session" && cookie.HttpOnly);
    }

    [Fact]
    public void GetBrowserCookies_UsesCurrentDocumentUrlScope()
    {
        var service = new WebPreviewService(serverPort: 2000);
        Assert.True(service.SetTarget("session-1", null, "https://example.com/app"));
        Assert.True(service.TryGetPreviewRouteKey("session-1", null, out var routeKey));
        Assert.True(service.SetCookieFromRaw(routeKey, "theme=dark; Path=/app"));
        Assert.True(service.SetCookieFromRaw(routeKey, "csrf=abc123; Path=/api"));

        var browserCookies = service.GetBrowserCookies(routeKey, new Uri("https://example.com/app/task/1"));

        Assert.Contains("theme=dark", browserCookies.Header, StringComparison.Ordinal);
        Assert.DoesNotContain("csrf=abc123", browserCookies.Header, StringComparison.Ordinal);
    }

    [Fact]
    public void GetForwardedCookieHeader_IncludesHttpOnlyCookiesForMatchingRequestScope()
    {
        var service = new WebPreviewService(serverPort: 2000);
        Assert.True(service.SetTarget("session-1", null, "https://example.com/app"));
        Assert.True(service.TryGetPreviewRouteKey("session-1", null, out var routeKey));
        Assert.True(service.SetCookieFromRaw(routeKey, "theme=dark; Path=/app"));
        Assert.True(service.SetCookieFromRaw(routeKey, "session=abc123; Path=/app; HttpOnly"));

        var header = service.GetForwardedCookieHeader(routeKey, new Uri("https://example.com/app/api"));

        Assert.NotNull(header);
        Assert.Contains("theme=dark", header, StringComparison.Ordinal);
        Assert.Contains("session=abc123", header, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreResponseCookies_CapturesSetCookieForForwardedRequests()
    {
        var service = new WebPreviewService(serverPort: 2000);
        Assert.True(service.SetTarget("session-1", null, "https://example.com/app"));
        Assert.True(service.TryGetPreviewRouteKey("session-1", null, out var routeKey));
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("Set-Cookie", "session=abc123; Path=/app; HttpOnly");

        service.StoreResponseCookies(routeKey, new Uri("https://example.com/app/login"), response);

        var forwarded = service.GetForwardedCookieHeader(routeKey, new Uri("https://example.com/app/api"));
        var browserCookies = service.GetBrowserCookies(routeKey, new Uri("https://example.com/app/api"));
        Assert.NotNull(forwarded);
        Assert.Contains("session=abc123", forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain("session=abc123", browserCookies.Header ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void SetTarget_DifferentPort_ResetsCookieJar()
    {
        var service = new WebPreviewService(serverPort: 2000);
        Assert.True(service.SetTarget("session-1", null, "https://example.com:3000"));
        Assert.True(service.TryGetPreviewRouteKey("session-1", null, out var routeKey));
        Assert.True(service.SetCookieFromRaw(routeKey, "theme=dark; Path=/"));

        Assert.True(service.SetTarget("session-1", null, "https://example.com:4000"));

        var cookies = service.GetCookies("session-1");
        Assert.True(string.IsNullOrEmpty(cookies.Header));
        Assert.Empty(cookies.Cookies);
    }

    [Fact]
    public void SetTarget_SelfTargetWithPreviewOriginEnabled_AllowsMidTermInMidTerm()
    {
        var previewOrigin = new BrowserPreviewOriginService(mainPort: 2000, previewPort: 2001, isEnabled: true);
        var service = new WebPreviewService(serverPort: 2000, previewOriginService: previewOrigin);

        var ok = service.SetTarget("session-1", null, "https://localhost:2000");

        Assert.True(ok);
        Assert.Equal("https://localhost:2000/", service.GetTargetUrl("session-1"));
    }

    [Fact]
    public void SetTarget_SelfTargetWithoutPreviewOrigin_RemainsBlocked()
    {
        var service = new WebPreviewService(serverPort: 2000);

        var ok = service.SetTarget("session-1", null, "https://localhost:2000");

        Assert.False(ok);
    }

    [Fact]
    public void SetTarget_LocalFileUrl_IsAllowed()
    {
        var service = new WebPreviewService(serverPort: 2000);
        var localPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "midterm-webpreview-local-file", "index.html"));
        var localFileUri = new Uri(localPath);

        var ok = service.SetTarget("session-1", null, localFileUri.AbsoluteUri);

        Assert.True(ok);
        Assert.Equal(localFileUri.AbsoluteUri, service.GetTargetUrl("session-1"));
    }

    [Fact]
    public void SetTarget_RemoteFileUrl_IsBlocked()
    {
        var service = new WebPreviewService(serverPort: 2000);

        var ok = service.SetTarget("session-1", null, "file://remote-host/share/index.html");

        Assert.False(ok);
    }

    [Fact]
    public void SyncSessionCookieForSelfTarget_DoesNotExposeAuthCookieToBrowser()
    {
        var previewOrigin = new BrowserPreviewOriginService(mainPort: 2000, previewPort: 2001, isEnabled: true);
        var service = new WebPreviewService(serverPort: 2000, previewOriginService: previewOrigin);
        Assert.True(service.SetTarget("session-1", null, "https://localhost:2000"));
        Assert.True(service.TryGetPreviewRouteKey("session-1", null, out var routeKey));

        service.SyncSessionCookieForSelfTarget(routeKey, "token-123");

        var allCookies = service.GetCookies("session-1");
        var browserCookies = service.GetBrowserCookies(routeKey, new Uri("https://localhost:2000/"));

        Assert.Contains(allCookies.Cookies, cookie => cookie.Name == "mm-session" && cookie.HttpOnly);
        Assert.DoesNotContain("mm-session=token-123", browserCookies.Header ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void PersistCookies_SelfTargetSkipsAuthCookieOnDisk()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "midterm-webpreview-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var previewOrigin = new BrowserPreviewOriginService(mainPort: 2000, previewPort: 2001, isEnabled: true);
            var service = new WebPreviewService(serverPort: 2000, previewOriginService: previewOrigin, cookiesDirectory: tempDir);
            Assert.True(service.SetTarget("session-1", null, "https://localhost:2000"));
            Assert.True(service.TryGetPreviewRouteKey("session-1", null, out var routeKey));
            Assert.True(service.SetCookieFromRaw(routeKey, "theme=dark; Path=/"));
            service.SyncSessionCookieForSelfTarget(routeKey, "token-123");
            service.PersistCookies(routeKey);

            var reloaded = new WebPreviewService(serverPort: 2000, previewOriginService: previewOrigin, cookiesDirectory: tempDir);
            Assert.True(reloaded.SetTarget("session-1", null, "https://localhost:2000"));

            var cookies = reloaded.GetCookies("session-1");

            Assert.Contains("theme=dark", cookies.Header ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain("mm-session=token-123", cookies.Header ?? "", StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void NamedPreviews_IsolateTargetsAndCookiesWithinOneTerminalSession()
    {
        var service = new WebPreviewService(serverPort: 2000);

        Assert.True(service.SetTarget("session-1", "default", "https://example.com/app-a"));
        Assert.True(service.SetTarget("session-1", "user2", "https://example.com/app-b"));
        Assert.True(service.TryGetPreviewRouteKey("session-1", "default", out var defaultRouteKey));
        Assert.True(service.TryGetPreviewRouteKey("session-1", "user2", out var user2RouteKey));
        Assert.True(service.SetCookieFromRaw(defaultRouteKey, "theme=dark; Path=/"));
        Assert.True(service.SetCookieFromRaw(user2RouteKey, "theme=light; Path=/"));

        var defaultCookies = service.GetCookies("session-1", "default");
        var user2Cookies = service.GetCookies("session-1", "user2");

        Assert.Equal("https://example.com/app-a", service.GetTargetUrl("session-1", "default"));
        Assert.Equal("https://example.com/app-b", service.GetTargetUrl("session-1", "user2"));
        Assert.Contains(defaultCookies.Cookies, cookie => cookie.Name == "theme" && cookie.Value == "dark");
        Assert.DoesNotContain(defaultCookies.Cookies, cookie => cookie.Name == "theme" && cookie.Value == "light");
        Assert.Contains(user2Cookies.Cookies, cookie => cookie.Name == "theme" && cookie.Value == "light");
        Assert.DoesNotContain(user2Cookies.Cookies, cookie => cookie.Name == "theme" && cookie.Value == "dark");
    }

    [Fact]
    public void ListPreviewSessions_ReturnsNamedPreviewEntriesPerTerminalSession()
    {
        var service = new WebPreviewService(serverPort: 2000);

        service.EnsurePreviewSession("session-1", "default");
        service.EnsurePreviewSession("session-1", "user2");
        service.EnsurePreviewSession("session-2", "user2");

        var session1Previews = service.ListPreviewSessions("session-1");
        var session2Previews = service.ListPreviewSessions("session-2");

        Assert.Equal(2, session1Previews.Previews.Count);
        Assert.Contains(session1Previews.Previews, preview => preview.PreviewName == "default");
        Assert.Contains(session1Previews.Previews, preview => preview.PreviewName == "user2");
        Assert.Single(session2Previews.Previews);
        Assert.Equal("user2", session2Previews.Previews[0].PreviewName);
    }

    [Fact]
    public void RememberLeakedPathRoute_TracksRouteKeyForLaterResolution()
    {
        var service = new WebPreviewService(serverPort: 2000);
        Assert.True(service.SetTarget("session-1", null, "https://example.com"));
        Assert.True(service.TryGetPreviewRouteKey("session-1", null, out var routeKey));

        service.RememberLeakedPathRoute(routeKey, "/js/login.js?v=3");

        Assert.True(service.TryGetRouteKeyByLeakedPath("/js/login.js", out var resolvedRouteKey));
        Assert.Equal(routeKey, resolvedRouteKey);
    }

    [Fact]
    public void DeletePreviewSession_ClearsRememberedLeakedPathsForThatRoute()
    {
        var service = new WebPreviewService(serverPort: 2000);
        Assert.True(service.SetTarget("session-1", "user2", "https://example.com"));
        Assert.True(service.TryGetPreviewRouteKey("session-1", "user2", out var routeKey));
        service.RememberLeakedPathRoute(routeKey, "/js/login.js");

        Assert.True(service.DeletePreviewSession("session-1", "user2"));

        Assert.False(service.TryGetRouteKeyByLeakedPath("/js/login.js", out _));
    }

    [Fact]
    public void SetTarget_IncrementsTargetRevisionForSamePreview()
    {
        var service = new WebPreviewService(serverPort: 2000);

        Assert.True(service.SetTarget("session-1", null, "https://example.com"));
        var first = service.GetPreviewSession("session-1");
        Assert.NotNull(first);

        Assert.True(service.SetTarget("session-1", null, "https://example.org"));
        var second = service.GetPreviewSession("session-1");
        Assert.NotNull(second);

        Assert.Equal(1, first!.TargetRevision);
        Assert.Equal(2, second!.TargetRevision);
    }

    [Fact]
    public void ClearState_PreservesTargetAndBumpsRevisionWhileResettingCookies()
    {
        var service = new WebPreviewService(serverPort: 2000);
        Assert.True(service.SetTarget("session-1", "default", "https://example.com/app"));
        Assert.True(service.TryGetPreviewRouteKey("session-1", "default", out var routeKey));
        Assert.True(service.SetCookieFromRaw(routeKey, "theme=dark; Path=/"));

        var before = service.GetPreviewSession("session-1", "default");
        Assert.NotNull(before);

        Assert.True(service.ClearState("session-1", "default"));

        var after = service.GetPreviewSession("session-1", "default");
        var cookies = service.GetCookies("session-1", "default");

        Assert.NotNull(after);
        Assert.Equal("https://example.com/app", service.GetTargetUrl("session-1", "default"));
        Assert.Equal(before!.TargetRevision + 1, after!.TargetRevision);
        Assert.True(string.IsNullOrEmpty(cookies.Header));
        Assert.Empty(cookies.Cookies);
    }
}
