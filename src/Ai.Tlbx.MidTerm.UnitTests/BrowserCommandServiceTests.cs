using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Browser;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public class BrowserCommandServiceTests
{
    [Fact]
    public void ResolveTimeoutSeconds_UsesLongerDefaultForScreenshots()
    {
        var screenshotTimeout = BrowserCommandService.ResolveTimeoutSeconds(new BrowserCommandRequest
        {
            Command = "screenshot"
        });
        var defaultTimeout = BrowserCommandService.ResolveTimeoutSeconds(new BrowserCommandRequest
        {
            Command = "url"
        });
        var explicitTimeout = BrowserCommandService.ResolveTimeoutSeconds(new BrowserCommandRequest
        {
            Command = "screenshot",
            Timeout = 7
        });

        Assert.Equal(30, screenshotTimeout);
        Assert.Equal(10, defaultTimeout);
        Assert.Equal(7, explicitTimeout);
    }

    [Fact]
    public async Task TryRegisterClient_ReplacesExistingPreviewClientOnReconnect()
    {
        var service = new BrowserCommandService();
        BrowserWsMessage? captured = null;

        var first = service.TryRegisterClient("c1", "session-a", "user1", "preview-a", _ => { });
        var duplicate = service.TryRegisterClient("c2", "session-a", "user1", "preview-a", msg =>
        {
            captured = msg;
            service.ReceiveResult(new BrowserWsResult
            {
                Id = msg.Id,
                Success = true,
                Result = "reconnected-ok",
                PreviewId = "preview-a"
            });
        });

        Assert.True(first);
        Assert.True(duplicate);
        Assert.Equal(1, service.ConnectedClientCount);

        var result = await service.ExecuteCommandAsync(new BrowserCommandRequest
        {
            Command = "url",
            PreviewId = "preview-a"
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("reconnected-ok", result.Result);
        Assert.NotNull(captured);
        Assert.Equal("preview-a", captured!.PreviewId);
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithMatchingSession_RoutesToCorrectPreview()
    {
        var service = new BrowserCommandService();
        BrowserWsMessage? captured = null;

        Assert.True(service.TryRegisterClient("c1", "session-a", "user1", "preview-a", _ => { }));
        Assert.True(service.TryRegisterClient("c2", "session-b", "user2", "preview-b", msg =>
        {
            captured = msg;
            service.ReceiveResult(new BrowserWsResult
            {
                Id = msg.Id,
                Success = true,
                Result = "ok",
                PreviewId = "preview-b"
            });
        }));

        var result = await service.ExecuteCommandAsync(new BrowserCommandRequest
        {
            Command = "url",
            SessionId = "session-b"
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Result);
        Assert.NotNull(captured);
        Assert.Equal("session-b", captured!.SessionId);
        Assert.Equal("user2", captured.PreviewName);
        Assert.Equal("preview-b", captured.PreviewId);
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithMultipleClientsAndNoSession_ReturnsHelpfulError()
    {
        var service = new BrowserCommandService();
        Assert.True(service.TryRegisterClient("c1", "session-a", "user1", "preview-a", _ => { }, browserId: "browser-a"));
        Assert.True(service.TryRegisterClient("c2", "session-b", "user2", "preview-b", _ => { }, browserId: "browser-b"));

        var result = await service.ExecuteCommandAsync(new BrowserCommandRequest
        {
            Command = "url"
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("--session", result.Error ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithSameBrowserDuplicates_PrefersNewestPreviewClient()
    {
        var service = new BrowserCommandService();
        BrowserWsMessage? captured = null;

        Assert.True(service.TryRegisterClient("c1", null, null, null, _ => { }, browserId: "browser-a"));
        Assert.True(service.TryRegisterClient("c2", "session-a", "user1", "preview-a", msg =>
        {
            captured = msg;
            service.ReceiveResult(new BrowserWsResult
            {
                Id = msg.Id,
                Success = true,
                Result = "ok",
                PreviewId = "preview-a"
            });
        }, browserId: "browser-a"));

        var result = await service.ExecuteCommandAsync(new BrowserCommandRequest
        {
            Command = "url"
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Result);
        Assert.NotNull(captured);
        Assert.Equal("preview-a", captured!.PreviewId);
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithHiddenNewerClient_PrefersVisibleClient()
    {
        var service = new BrowserCommandService();
        BrowserWsMessage? captured = null;

        Assert.True(service.TryRegisterClient(
            "visible-client",
            "session-a",
            "default",
            "preview-visible",
            msg =>
            {
                captured = msg;
                service.ReceiveResult(new BrowserWsResult
                {
                    Id = msg.Id,
                    Success = true,
                    Result = "visible-ok",
                    PreviewId = "preview-visible"
                });
            },
            browserId: "browser-a",
            isVisible: true));
        Assert.True(service.TryRegisterClient(
            "hidden-client",
            "session-a",
            "default",
            "preview-hidden",
            _ => { },
            browserId: "browser-a",
            isVisible: false));

        var result = await service.ExecuteCommandAsync(new BrowserCommandRequest
        {
            Command = "url",
            SessionId = "session-a",
            PreviewName = "default"
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("visible-ok", result.Result);
        Assert.NotNull(captured);
        Assert.Equal("preview-visible", captured!.PreviewId);
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithScopedOwner_RoutesToOwnedBrowser()
    {
        var ownerService = new BrowserPreviewOwnerService();
        ownerService.Claim("session-a", "default", "browser-owner");
        var service = new BrowserCommandService(previewOwnerService: ownerService);
        BrowserWsMessage? captured = null;

        Assert.True(service.TryRegisterClient(
            "owner-client",
            "session-a",
            "default",
            "preview-owner",
            msg =>
            {
                captured = msg;
                service.ReceiveResult(new BrowserWsResult
                {
                    Id = msg.Id,
                    Success = true,
                    Result = "owner-ok",
                    PreviewId = "preview-owner"
                });
            },
            browserId: "browser-owner",
            isVisible: false));
        Assert.True(service.TryRegisterClient(
            "follower-client",
            "session-a",
            "default",
            "preview-follower",
            _ => throw new Xunit.Sdk.XunitException("wrong browser"),
            browserId: "browser-follower",
            isVisible: true,
            hasFocus: true,
            isTopLevel: true));

        var result = await service.ExecuteCommandAsync(new BrowserCommandRequest
        {
            Command = "url",
            SessionId = "session-a",
            PreviewName = "default"
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("owner-ok", result.Result);
        Assert.NotNull(captured);
        Assert.Equal("preview-owner", captured!.PreviewId);
    }

    [Fact]
    public async Task UnregisterClient_CancelsOnlyPendingCommandsForThatPreview()
    {
        var service = new BrowserCommandService();
        BrowserWsMessage? keptMessage = null;

        Assert.True(service.TryRegisterClient("c1", "session-a", "user1", "preview-a", _ => { }));
        Assert.True(service.TryRegisterClient("c2", "session-b", "user2", "preview-b", msg =>
        {
            keptMessage = msg;
        }));

        var disconnectedTask = service.ExecuteCommandAsync(new BrowserCommandRequest
        {
            Command = "url",
            SessionId = "session-a"
        }, CancellationToken.None);

        var keptTask = service.ExecuteCommandAsync(new BrowserCommandRequest
        {
            Command = "url",
            SessionId = "session-b"
        }, CancellationToken.None);

        service.UnregisterClient("c1");
        var disconnected = await disconnectedTask;

        Assert.False(disconnected.Success);
        Assert.Equal("Browser disconnected.", disconnected.Error);

        Assert.NotNull(keptMessage);
        service.ReceiveResult(new BrowserWsResult
        {
            Id = keptMessage!.Id,
            Success = true,
            Result = "still-connected",
            PreviewId = "preview-b"
        });

        var kept = await keptTask;
        Assert.True(kept.Success);
        Assert.Equal("still-connected", kept.Result);
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithMatchingSessionAndPreviewName_RoutesToCorrectNamedPreview()
    {
        var service = new BrowserCommandService();
        BrowserWsMessage? captured = null;

        Assert.True(service.TryRegisterClient("c1", "session-a", "user1", "preview-a", _ => { }));
        Assert.True(service.TryRegisterClient("c2", "session-a", "user2", "preview-b", msg =>
        {
            captured = msg;
            service.ReceiveResult(new BrowserWsResult
            {
                Id = msg.Id,
                Success = true,
                Result = "user2-ok",
                PreviewId = "preview-b",
                PreviewName = "user2"
            });
        }));

        var result = await service.ExecuteCommandAsync(new BrowserCommandRequest
        {
            Command = "url",
            SessionId = "session-a",
            PreviewName = "user2"
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("user2-ok", result.Result);
        Assert.NotNull(captured);
        Assert.Equal("session-a", captured!.SessionId);
        Assert.Equal("user2", captured.PreviewName);
        Assert.Equal("preview-b", captured.PreviewId);
    }

    [Fact]
    public void GetStatus_WithScopedPreview_ReturnsOnlyMatchingClient()
    {
        var service = new BrowserCommandService();

        Assert.True(service.TryRegisterClient("c1", "session-a", "default", "preview-a", _ => { }));
        Assert.True(service.TryRegisterClient("c2", "session-a", "codex1", "preview-b", _ => { }));
        Assert.True(service.TryRegisterClient("c3", "session-b", "codex1", "preview-c", _ => { }));

        var status = service.GetStatus(
            "https://localhost:5001/teacher?dev=1",
            sessionId: "session-a",
            previewName: "codex1");

        Assert.True(status.Connected);
        Assert.True(status.Controllable);
        Assert.Equal("ready", status.State);
        Assert.Equal(1, status.ConnectedClientCount);
        Assert.Equal(3, status.TotalConnectedClientCount);
        Assert.NotNull(status.DefaultClient);
        Assert.Equal("session 'session-a', preview 'codex1'", status.ScopeDescription);
        Assert.Equal("session-a", status.DefaultClient!.SessionId);
        Assert.Equal("codex1", status.DefaultClient.PreviewName);
        Assert.Equal("preview-b", status.DefaultClient.PreviewId);
        Assert.Single(status.Clients);
    }

    [Fact]
    public void GetStatus_DefaultClient_ExposesInteractiveFlags()
    {
        var service = new BrowserCommandService();

        Assert.True(service.TryRegisterClient(
            "c1",
            "session-a",
            "default",
            "preview-a",
            _ => { },
            browserId: "browser-a",
            isVisible: true,
            hasFocus: true,
            isTopLevel: false));

        var status = service.GetStatus("http://192.168.178.1/", sessionId: "session-a");

        Assert.True(status.Connected);
        Assert.True(status.Controllable);
        Assert.Equal("ready", status.State);
        Assert.NotNull(status.DefaultClient);
        Assert.True(status.DefaultClient!.IsVisible);
        Assert.True(status.DefaultClient.HasFocus);
        Assert.False(status.DefaultClient.IsTopLevel);
    }

    [Fact]
    public void GetStatusText_WithScopedPreviewAndNoMatch_ReturnsHelpfulDisconnectedMessage()
    {
        var service = new BrowserCommandService();
        Assert.True(service.TryRegisterClient("c1", "session-a", "default", "preview-a", _ => { }));

        var status = service.GetStatusText(
            "https://localhost:5001/teacher?dev=1",
            sessionId: "session-a",
            previewName: "codex1",
            connectedUiClientCount: 1);

        Assert.Contains("disconnected", status, StringComparison.Ordinal);
        Assert.Contains("state: waiting", status, StringComparison.Ordinal);
        Assert.Contains("bridge phase: preview-frame-disconnected", status, StringComparison.Ordinal);
        Assert.Contains("controllable: no", status, StringComparison.Ordinal);
        Assert.Contains("ui clients: 1", status, StringComparison.Ordinal);
        Assert.Contains("target configured: yes", status, StringComparison.Ordinal);
        Assert.Contains("preview 'codex1' in session 'session-a'", status, StringComparison.Ordinal);
        Assert.Contains("no controllable browser has attached yet", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetStatusText_WithoutUiClient_ExplainsThatTheOwningMidTermTabIsMissing()
    {
        var service = new BrowserCommandService();

        var status = service.GetStatusText(
            "https://127.0.0.1:2100/",
            sessionId: "session-a",
            previewName: "default",
            connectedUiClientCount: 0);

        Assert.Contains("state: waiting", status, StringComparison.Ordinal);
        Assert.Contains("bridge phase: no-ui-client", status, StringComparison.Ordinal);
        Assert.Contains("ui clients: 0", status, StringComparison.Ordinal);
        Assert.Contains("/ws/state", status, StringComparison.Ordinal);
        Assert.Contains("dev browser cannot work", status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("owning MidTerm browser tab", status, StringComparison.Ordinal);
    }

    [Fact]
    public void GetStatus_WithMultipleBrowsersAndNoScope_IsAmbiguous()
    {
        var service = new BrowserCommandService();
        Assert.True(service.TryRegisterClient("c1", "session-a", "default", "preview-a", _ => { }, browserId: "browser-a"));
        Assert.True(service.TryRegisterClient("c2", "session-b", "default", "preview-b", _ => { }, browserId: "browser-b"));

        var status = service.GetStatus("https://localhost:5001/");

        Assert.True(status.Connected);
        Assert.False(status.Controllable);
        Assert.Equal("ambiguous", status.State);
        Assert.Null(status.DefaultClient);
        Assert.Contains("Multiple browser previews are connected", status.StatusMessage ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void GetStatus_WithOfflineOwnerAndMultipleFollowers_ReportsWaitingOwnerState()
    {
        var ownerService = new BrowserPreviewOwnerService();
        ownerService.Claim("session-a", "default", "browser-owner");
        var service = new BrowserCommandService(previewOwnerService: ownerService);

        Assert.True(service.TryRegisterClient(
            "c1",
            "session-a",
            "default",
            "preview-follower-a",
            _ => { },
            browserId: "browser-follower-a",
            isVisible: true,
            hasFocus: true,
            isTopLevel: true));
        Assert.True(service.TryRegisterClient(
            "c2",
            "session-a",
            "default",
            "preview-follower-b",
            _ => { },
            browserId: "browser-follower-b",
            isVisible: true,
            hasFocus: true,
            isTopLevel: true));

        var status = service.GetStatus(
            "https://localhost:5001/",
            sessionId: "session-a",
            previewName: "default",
            connectedUiClientCount: 1);

        Assert.True(status.Connected);
        Assert.False(status.Controllable);
        Assert.Equal("waiting", status.State);
        Assert.Equal("owner-offline", status.BridgePhase);
        Assert.Equal("browser-owner", status.OwnerBrowserId);
        Assert.False(status.OwnerConnected);
        Assert.Null(status.DefaultClient);
        Assert.Contains("owned by browser 'browser-owner'", status.StatusMessage ?? "", StringComparison.Ordinal);
        Assert.Contains("mt_claim_preview", status.RecoveryHint ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void GetStatus_WithOfflineOwnerAndMainPreviewClient_ReclaimsMainBrowser()
    {
        var mainBrowser = new MainBrowserService();
        var ownerService = new BrowserPreviewOwnerService();
        ownerService.Claim("session-a", "default", "stale-browser");
        var connectionToken = new object();
        mainBrowser.Register("browser-main:tab-1", connectionToken);
        mainBrowser.Claim("browser-main:tab-1");
        var service = new BrowserCommandService(mainBrowser, ownerService);

        Assert.True(service.TryRegisterClient(
            "c1",
            "session-a",
            "default",
            "preview-follower",
            _ => { },
            browserId: "browser-follower:tab-2",
            isVisible: true));
        Assert.True(service.TryRegisterClient(
            "c2",
            "session-a",
            "default",
            "preview-main",
            _ => { },
            browserId: "browser-main:tab-1",
            isVisible: true,
            hasFocus: true,
            isTopLevel: true));

        var status = service.GetStatus(
            "https://localhost:5001/",
            sessionId: "session-a",
            previewName: "default",
            connectedUiClientCount: 2);

        Assert.True(status.Connected);
        Assert.True(status.Controllable);
        Assert.Equal("ready", status.State);
        Assert.Equal("ready", status.BridgePhase);
        Assert.True(status.OwnerConnected);
        Assert.Equal("browser-main:tab-1", status.OwnerBrowserId);
        Assert.Equal("preview-main", status.DefaultClient?.PreviewId);
        Assert.Equal("browser-main:tab-1", ownerService.GetOwnerBrowserId("session-a", "default"));
    }

    [Fact]
    public void ClaimMainBrowser_WithScopedPreview_ClaimsResolvedBrowser()
    {
        var mainBrowser = new MainBrowserService();
        var service = new BrowserCommandService(mainBrowser);

        Assert.True(service.TryRegisterClient(
            "c1",
            "session-a",
            "default",
            "preview-a",
            _ => { },
            browserId: "browser-a:tab-1",
            isVisible: true));

        var result = service.ClaimMainBrowser(new BrowserCommandRequest
        {
            SessionId = "session-a",
            PreviewName = "default"
        });

        Assert.True(result.Success);
        Assert.Equal("browser-a:tab-1", mainBrowser.GetMainBrowserId());
    }

    [Fact]
    public void ClaimMainBrowser_WithBrowserId_ClaimsMatchingBrowserInsideScope()
    {
        var mainBrowser = new MainBrowserService();
        var service = new BrowserCommandService(mainBrowser);

        Assert.True(service.TryRegisterClient(
            "c1",
            "session-a",
            "default",
            "preview-a",
            _ => { },
            browserId: "browser-a:tab-1",
            isVisible: true));
        Assert.True(service.TryRegisterClient(
            "c2",
            "session-a",
            "default",
            "preview-b",
            _ => { },
            browserId: "browser-b:tab-2",
            isVisible: true));

        var result = service.ClaimMainBrowser(new BrowserCommandRequest
        {
            SessionId = "session-a",
            PreviewName = "default",
            Value = "browser-b"
        });

        Assert.True(result.Success);
        Assert.Equal("browser-b:tab-2", mainBrowser.GetMainBrowserId());
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithCookieOwnerAndTabScopedPreviewClient_RoutesByStableClientPart()
    {
        var ownerService = new BrowserPreviewOwnerService();
        ownerService.Claim("session-a", "default", "browser-a");
        var service = new BrowserCommandService(previewOwnerService: ownerService);
        BrowserWsMessage? captured = null;

        Assert.True(service.TryRegisterClient(
            "c1",
            "session-a",
            "default",
            "preview-a",
            msg =>
            {
                captured = msg;
                service.ReceiveResult(new BrowserWsResult
                {
                    Id = msg.Id,
                    Success = true,
                    Result = "ok",
                    PreviewId = "preview-a"
                });
            },
            browserId: "browser-a:tab-1"));

        var result = await service.ExecuteCommandAsync(new BrowserCommandRequest
        {
            Command = "url",
            SessionId = "session-a",
            PreviewName = "default"
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Result);
        Assert.NotNull(captured);
        Assert.Equal("preview-a", captured!.PreviewId);
    }

    [Fact]
    public async Task WaitForControllableAsync_ReturnsReadyAfterMatchingPreviewAttaches()
    {
        var service = new BrowserCommandService();

        var waitingTask = service.WaitForControllableAsync(
            "https://example.com/",
            sessionId: "session-a",
            previewName: "user1",
            timeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(10));

        await Task.Delay(40);
        Assert.True(service.TryRegisterClient("c1", "session-a", "user1", "preview-a", _ => { }));

        var status = await waitingTask;

        Assert.True(status.Connected);
        Assert.True(status.Controllable);
        Assert.Equal("ready", status.State);
        Assert.Equal("preview-a", status.DefaultClient?.PreviewId);
    }

    [Fact]
    public async Task WaitForControllableAsync_ReturnsLatestWaitingStatusOnTimeout()
    {
        var service = new BrowserCommandService();

        var status = await service.WaitForControllableAsync(
            "https://example.com/",
            sessionId: "session-a",
            previewName: "user1",
            connectedUiClientCountProvider: () => 1,
            timeout: TimeSpan.FromMilliseconds(40),
            pollInterval: TimeSpan.FromMilliseconds(10));

        Assert.False(status.Controllable);
        Assert.Equal("waiting", status.State);
        Assert.True(status.HasTarget);
        Assert.True(status.HasUiClient);
    }

    [Fact]
    public async Task WaitForControllableAsync_IgnoresPreexistingClientUntilNewAttachmentArrives()
    {
        var service = new BrowserCommandService();

        Assert.True(service.TryRegisterClient("c1", "session-a", "default", "preview-a", _ => { }));
        var notBefore = DateTimeOffset.UtcNow.AddMilliseconds(20);

        var waitingTask = service.WaitForControllableAsync(
            "https://example.com/",
            sessionId: "session-a",
            previewName: "default",
            requireClientConnectedAfterUtc: notBefore,
            timeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(10));

        await Task.Delay(50);
        Assert.True(service.TryRegisterClient("c2", "session-a", "default", "preview-a", _ => { }));

        var status = await waitingTask;

        Assert.True(status.Connected);
        Assert.True(status.Controllable);
        Assert.NotNull(status.DefaultClient);
        Assert.True(status.DefaultClient!.ConnectedAtUtc >= notBefore);
    }

    [Fact]
    public async Task WaitForControllableAsync_WhenVisibleClientRequired_IgnoresHiddenClientUntilVisibleAttach()
    {
        var service = new BrowserCommandService();

        var waitingTask = service.WaitForControllableAsync(
            "https://example.com/",
            sessionId: "session-a",
            previewName: "default",
            requireVisibleClient: true,
            timeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(10));

        await Task.Delay(40);
        Assert.True(service.TryRegisterClient(
            "hidden",
            "session-a",
            "default",
            "preview-hidden",
            _ => { },
            isVisible: false));

        await Task.Delay(40);
        Assert.False(waitingTask.IsCompleted);

        Assert.True(service.TryRegisterClient(
            "visible",
            "session-a",
            "default",
            "preview-visible",
            _ => { },
            isVisible: true));

        var status = await waitingTask;

        Assert.True(status.Connected);
        Assert.True(status.Controllable);
        Assert.True(status.DefaultClient?.IsVisible);
        Assert.Equal("preview-visible", status.DefaultClient?.PreviewId);
    }
}
