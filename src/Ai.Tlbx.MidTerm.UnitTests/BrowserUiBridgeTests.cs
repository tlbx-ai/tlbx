using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Browser;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class BrowserUiBridgeTests
{
    [Fact]
    public void RequestOpen_WithoutListeners_ReturnsHelpfulError()
    {
        var mainBrowser = new MainBrowserService();
        var bridge = new BrowserUiBridge(mainBrowser);

        var ok = bridge.RequestOpen(null, null, "https://example.com", true, out var error);

        Assert.False(ok);
        Assert.Contains("No MidTerm browser UI is connected", error, StringComparison.Ordinal);
        Assert.Contains("/ws/state", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestOpen_PrefersMainBrowserNewestListener()
    {
        var mainBrowser = new MainBrowserService();
        var bridge = new BrowserUiBridge(mainBrowser);
        var connectionToken = new object();
        string? openedUrl = null;

        mainBrowser.Register("browser-a", connectionToken);
        mainBrowser.Claim("browser-a");

        bridge.RegisterListener("l1", "browser-b", (_, _) => { }, (_, _) => { }, (_, _, _, _) => { }, (_, _, _, _) => throw new Xunit.Sdk.XunitException("wrong listener"));
        bridge.RegisterListener("l2", "browser-a", (_, _) => { }, (_, _) => { }, (_, _, _, _) => { }, (_, _, url, _) => openedUrl = "old:" + url);
        bridge.RegisterListener("l3", "browser-a", (_, _) => { }, (_, _) => { }, (_, _, _, _) => { }, (_, _, url, _) => openedUrl = url);

        var ok = bridge.RequestOpen(null, null, "https://example.com", true, out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.Equal("https://example.com", openedUrl);
    }

    [Fact]
    public void RequestOpen_WithPreviewOwner_UsesOwnedBrowser()
    {
        var mainBrowser = new MainBrowserService();
        var ownerService = new BrowserPreviewOwnerService();
        ownerService.Claim("session-a", "default", "browser-owner");
        var bridge = new BrowserUiBridge(mainBrowser, ownerService);
        string? openedUrl = null;

        bridge.RegisterListener("l1", "browser-follower", (_, _) => { }, (_, _) => { }, (_, _, _, _) => { }, (_, _, _, _) => throw new Xunit.Sdk.XunitException("wrong listener"));
        bridge.RegisterListener("l2", "browser-owner", (_, _) => { }, (_, _) => { }, (_, _, _, _) => { }, (_, _, url, _) => openedUrl = url);

        var ok = bridge.RequestOpen("session-a", "default", "https://example.com", true, out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.Equal("https://example.com", openedUrl);
    }

    [Fact]
    public void RequestOpen_WithoutPreviewOwner_ClaimsSelectedBrowser()
    {
        var mainBrowser = new MainBrowserService();
        var ownerService = new BrowserPreviewOwnerService();
        var bridge = new BrowserUiBridge(mainBrowser, ownerService);
        var connectionToken = new object();

        mainBrowser.Register("browser-a", connectionToken);
        mainBrowser.Claim("browser-a");

        bridge.RegisterListener("l1", "browser-a", (_, _) => { }, (_, _) => { }, (_, _, _, _) => { }, (_, _, _, _) => { });
        bridge.RegisterListener("l2", "browser-b", (_, _) => { }, (_, _) => { }, (_, _, _, _) => { }, (_, _, _, _) => { });

        var ok = bridge.RequestOpen("session-a", "default", "https://example.com", true, out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.Equal("browser-a", ownerService.GetOwnerBrowserId("session-a", "default"));
    }

    [Fact]
    public void RequestClaim_ReassignsPreviewToConnectedMainBrowser()
    {
        var mainBrowser = new MainBrowserService();
        var ownerService = new BrowserPreviewOwnerService();
        ownerService.Claim("session-a", "default", "stale-browser");
        var bridge = new BrowserUiBridge(mainBrowser, ownerService);
        var connectionToken = new object();

        mainBrowser.Register("browser-a", connectionToken);
        mainBrowser.Claim("browser-a");
        bridge.RegisterListener("l1", "browser-a", (_, _) => { }, (_, _) => { }, (_, _, _, _) => { }, (_, _, _, _) => { });

        var ok = bridge.RequestClaim("session-a", "default", out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.Equal("browser-a", ownerService.GetOwnerBrowserId("session-a", "default"));
    }

    [Fact]
    public void RequestClaimMain_WithBrowserId_ClaimsMatchingConnectedUiBrowser()
    {
        var mainBrowser = new MainBrowserService();
        var bridge = new BrowserUiBridge(mainBrowser);

        bridge.RegisterListener("l1", "browser-a:tab-1", (_, _) => { }, (_, _) => { }, (_, _, _, _) => { }, (_, _, _, _) => { });
        bridge.RegisterListener("l2", "browser-b:tab-2", (_, _) => { }, (_, _) => { }, (_, _, _, _) => { }, (_, _, _, _) => { });

        var ok = bridge.RequestClaimMain("browser-b", out var claimedBrowserId, out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.Equal("browser-b:tab-2", claimedBrowserId);
        Assert.Equal("browser-b:tab-2", mainBrowser.GetMainBrowserId());
    }

    [Fact]
    public void RequestClaimMain_WithoutBrowserIdRejectsAmbiguousBrowsers()
    {
        var mainBrowser = new MainBrowserService();
        var bridge = new BrowserUiBridge(mainBrowser);

        bridge.RegisterListener("l1", "browser-a:tab-1", (_, _) => { }, (_, _) => { }, (_, _, _, _) => { }, (_, _, _, _) => { });
        bridge.RegisterListener("l2", "browser-b:tab-2", (_, _) => { }, (_, _) => { }, (_, _, _, _) => { }, (_, _, _, _) => { });

        var ok = bridge.RequestClaimMain(null, out var claimedBrowserId, out var error);

        Assert.False(ok);
        Assert.Equal("", claimedBrowserId);
        Assert.Contains("--browser", error, StringComparison.Ordinal);
        Assert.Null(mainBrowser.GetMainBrowserId());
    }

    [Fact]
    public void RequestOpen_WithOfflineOwner_ReclaimsConnectedMainBrowser()
    {
        var mainBrowser = new MainBrowserService();
        var ownerService = new BrowserPreviewOwnerService();
        ownerService.Claim("session-a", "default", "stale-browser");
        var bridge = new BrowserUiBridge(mainBrowser, ownerService);
        var connectionToken = new object();
        string? openedUrl = null;

        mainBrowser.Register("browser-main:tab-1", connectionToken);
        mainBrowser.Claim("browser-main:tab-1");
        bridge.RegisterListener(
            "l1",
            "browser-follower:tab-2",
            (_, _) => { },
            (_, _) => { },
            (_, _, _, _) => { },
            (_, _, _, _) => throw new Xunit.Sdk.XunitException("wrong listener"));
        bridge.RegisterListener(
            "l2",
            "browser-main:tab-1",
            (_, _) => { },
            (_, _) => { },
            (_, _, _, _) => { },
            (_, _, url, _) => openedUrl = url);

        var ok = bridge.RequestOpen("session-a", "default", "https://example.com", false, out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.Equal("https://example.com", openedUrl);
        Assert.Equal("browser-main:tab-1", ownerService.GetOwnerBrowserId("session-a", "default"));
    }

    [Fact]
    public void RequestOpen_MatchesOwnerByStableClientPartDuringUpgrade()
    {
        var mainBrowser = new MainBrowserService();
        var ownerService = new BrowserPreviewOwnerService();
        ownerService.Claim("session-a", "default", "browser-a");
        var bridge = new BrowserUiBridge(mainBrowser, ownerService);
        string? openedUrl = null;

        bridge.RegisterListener(
            "l1",
            "browser-a:tab-1",
            (_, _) => { },
            (_, _) => { },
            (_, _, _, _) => { },
            (_, _, url, _) => openedUrl = url);

        var ok = bridge.RequestOpen("session-a", "default", "https://example.com", false, out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.Equal("https://example.com", openedUrl);
    }

    [Fact]
    public void RequestOpen_WithOfflineOwnerAndMultipleNonLeadingBrowsers_ReturnsHelpfulError()
    {
        var mainBrowser = new MainBrowserService();
        var ownerService = new BrowserPreviewOwnerService();
        ownerService.Claim("session-a", "default", "stale-browser");
        var bridge = new BrowserUiBridge(mainBrowser, ownerService);

        bridge.RegisterListener("l1", "browser-a:tab-1", (_, _) => { }, (_, _) => { }, (_, _, _, _) => { }, (_, _, _, _) => { });
        bridge.RegisterListener("l2", "browser-b:tab-2", (_, _) => { }, (_, _) => { }, (_, _, _, _) => { }, (_, _, _, _) => { });

        var ok = bridge.RequestOpen("session-a", "default", "https://example.com", false, out var error);

        Assert.False(ok);
        Assert.Contains("leading browser", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("stale-browser", ownerService.GetOwnerBrowserId("session-a", "default"));
    }
}
