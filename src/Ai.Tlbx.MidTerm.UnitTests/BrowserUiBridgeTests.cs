using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Browser;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class BrowserUiBridgeTests
{
    [Fact]
    public void RequestClose_BroadcastsExactPreviewToEveryConnectedUi()
    {
        var bridge = new BrowserUiBridge(new MainBrowserService());
        var closed = new List<string>();
        bridge.RegisterListener(
            "l1",
            "browser-a",
            (_, _) => { },
            (_, _) => { },
            (_, _, _, _) => { },
            (_, _, _, _) => { },
            close: (sessionId, previewName) => closed.Add($"a:{sessionId}/{previewName}"));
        bridge.RegisterListener(
            "l2",
            "browser-b",
            (_, _) => { },
            (_, _) => { },
            (_, _, _, _) => { },
            (_, _, _, _) => { },
            close: (sessionId, previewName) => closed.Add($"b:{sessionId}/{previewName}"));

        var count = bridge.RequestClose("session-a", "dai-e2e");

        Assert.Equal(2, count);
        Assert.Equal(["a:session-a/dai-e2e", "b:session-a/dai-e2e"], closed);
    }

    [Fact]
    public async Task RequestOpenWhenAvailableAsync_WaitsForReconnectAndDispatchesExactlyOnce()
    {
        var mainBrowser = new MainBrowserService();
        var bridge = new BrowserUiBridge(mainBrowser);
        var openCount = 0;

        var pending = bridge.RequestOpenWhenAvailableAsync(
            "session-a",
            "default",
            "https://example.com",
            activateSession: true,
            timeout: TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromMilliseconds(10));

        await Task.Delay(50);
        bridge.RegisterListener(
            "connection-a",
            "browser-a",
            (_, _) => { },
            (_, _) => { },
            (_, _, _, _) => { },
            (_, _, _, _) => openCount += 1);

        var result = await pending;

        Assert.True(result.Success);
        Assert.Equal("", result.Error);
        Assert.Equal(1, openCount);
    }

    [Fact]
    public async Task RequestDetachAsync_ReturnsTheBrowserUiFailure()
    {
        var bridge = new BrowserUiBridge(new MainBrowserService());
        bridge.RegisterAcknowledgedListener(
            "connection-a",
            "browser-a",
            (requestId, _, _) => bridge.CompleteUiCommand(new BrowserUiCommandResult
            {
                RequestId = requestId,
                Command = "detach",
                Success = false,
                Error = "Popup blocked"
            }),
            (_, _, _) => { },
            (_, _, _, _, _) => { },
            (_, _, _, _, _, _) => { });

        var result = await bridge.RequestDetachAsync("session-a", "default");

        Assert.False(result.Success);
        Assert.Equal("Popup blocked", result.Error);
    }

    [Fact]
    public async Task RequestOpenAsync_ForwardsTheExpectedTargetRevision()
    {
        var bridge = new BrowserUiBridge(new MainBrowserService());
        long? receivedRevision = null;
        bridge.RegisterAcknowledgedListener(
            "connection-a",
            "browser-a",
            (_, _, _) => { },
            (_, _, _) => { },
            (_, _, _, _, _) => { },
            (requestId, _, _, _, _, targetRevision) =>
            {
                receivedRevision = targetRevision;
                bridge.CompleteUiCommand(new BrowserUiCommandResult
                {
                    RequestId = requestId,
                    Command = "open",
                    Success = true
                });
            });

        var result = await bridge.RequestOpenAsync(
            "session-a",
            "default",
            "https://example.com",
            activateSession: false,
            targetRevision: 42);

        Assert.True(result.Success);
        Assert.Equal(42, receivedRevision);
    }

    [Fact]
    public async Task SerializeTargetOperationAsync_OrdersOpenAndCloseForTheSamePreview()
    {
        var bridge = new BrowserUiBridge(new MainBrowserService());
        var releaseOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var openEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();

        var open = bridge.SerializeTargetOperationAsync(
            "session-a",
            "default",
            async () =>
            {
                order.Add("open-start");
                openEntered.SetResult();
                await releaseOpen.Task;
                order.Add("open-end");
                return true;
            });
        await openEntered.Task;

        var close = bridge.SerializeTargetOperationAsync(
            "session-a",
            "default",
            () =>
            {
                order.Add("close");
                return Task.FromResult(true);
            });

        await Task.Delay(25);
        Assert.Equal(["open-start"], order);
        releaseOpen.SetResult();
        await Task.WhenAll(open, close);

        Assert.Equal(["open-start", "open-end", "close"], order);
    }

    [Fact]
    public void RequestMobileDevice_ForwardsToSelectedBrowserUi()
    {
        var bridge = new BrowserUiBridge(new MainBrowserService());
        string? requested = null;
        bridge.RegisterListener(
            "l1",
            "browser-a",
            (_, _) => { },
            (_, _) => { },
            (_, _, _, _) => { },
            (_, _, _, _) => { },
            (sessionId, previewName, action, profile) =>
                requested = $"{sessionId}/{previewName}/{action}/{profile}");

        var ok = bridge.RequestMobileDevice(
            "session-a",
            "default",
            "ROTATE",
            "pixel-8",
            out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.Equal("session-a/default/rotate/pixel-8", requested);
    }

    [Fact]
    public void RequestMobileDevice_RejectsUnsupportedAction()
    {
        var bridge = new BrowserUiBridge(new MainBrowserService());

        var ok = bridge.RequestMobileDevice("session-a", "default", "launch", null, out var error);

        Assert.False(ok);
        Assert.Contains("Unsupported", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestAgentWheelAsync_UsesExplicitOwnerWithoutTryingOtherTabs()
    {
        var bridge = new BrowserUiBridge(new MainBrowserService());
        var dispatched = 0;

        bridge.RegisterListener(
            "visible",
            "browser-a:tab-visible",
            (_, _) => { },
            (_, _) => { },
            (_, _, _, _) => { },
            (_, _, _, _) => { },
            agentWheel: (requestId, sessionId, _, _) =>
            {
                dispatched++;
                bridge.CompleteAgentWheel(new AgentHistoryWheelResult
                {
                    RequestId = requestId,
                    SessionId = sessionId,
                    Success = true
                });
            });
        await Task.Delay(5);
        bridge.RegisterListener(
            "hidden",
            "browser-b:tab-hidden",
            (_, _) => { },
            (_, _) => { },
            (_, _, _, _) => { },
            (_, _, _, _) => { },
            agentWheel: (requestId, sessionId, _, _) =>
            {
                dispatched++;
                bridge.CompleteAgentWheel(new AgentHistoryWheelResult
                {
                    RequestId = requestId,
                    SessionId = sessionId,
                    Success = false,
                    Error = "The requested ACP history is not visible in this tlbx browser UI."
                });
            });

        Assert.True(bridge.RequestClaim("session-a", "default", out _, "browser-a:tab-visible"));
        var result = await bridge.RequestAgentWheelAsync("session-a", -320, 1, default);

        Assert.True(result.Success);
        Assert.Equal(1, dispatched);
    }

    [Fact]
    public void RequestOpen_WithoutListeners_ReturnsHelpfulError()
    {
        var mainBrowser = new MainBrowserService();
        var bridge = new BrowserUiBridge(mainBrowser);

        var ok = bridge.RequestOpen(null, null, "https://example.com", true, out var error);

        Assert.False(ok);
        Assert.Contains("No tlbx browser UI is connected", error, StringComparison.Ordinal);
        Assert.Contains("/ws/state", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestOpen_RejectsAmbiguousUiEvenWithGlobalMain()
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

        Assert.False(ok);
        Assert.Contains("--browser", error, StringComparison.Ordinal);
        Assert.Null(openedUrl);
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
    public void RequestOpen_WithoutPreviewOwner_RequiresExplicitSelection()
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

        Assert.False(ok);
        Assert.Contains("--browser", error, StringComparison.Ordinal);
        Assert.Null(ownerService.GetOwnerBrowserId("session-a", "default"));
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

        var ok = bridge.RequestClaimMain("browser-b:tab-2", out var claimedBrowserId, out var error);

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
    public void RequestOpen_WithOfflineOwner_DoesNotReclaimMainBrowser()
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

        Assert.False(ok);
        Assert.Contains("--browser", error, StringComparison.Ordinal);
        Assert.Null(openedUrl);
        Assert.Equal("stale-browser", ownerService.GetOwnerBrowserId("session-a", "default"));
    }

    [Fact]
    public void RequestOpen_RequiresExactTabIdentity()
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

        Assert.False(ok);
        Assert.Contains("exact tab", error, StringComparison.Ordinal);
        Assert.Null(openedUrl);
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
        Assert.Contains("--browser", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("stale-browser", ownerService.GetOwnerBrowserId("session-a", "default"));
    }
}
