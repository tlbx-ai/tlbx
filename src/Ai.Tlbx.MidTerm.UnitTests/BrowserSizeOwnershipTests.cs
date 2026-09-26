using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class BrowserSizeOwnershipTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tlbx-browser-owner-" + Guid.NewGuid());
    private readonly TerminalSizeControlService _size;
    public BrowserSizeOwnershipTests() => _size = new(_directory, TimeProvider.System);

    private static void AddUi(BrowserUiBridge bridge, string id, Action? open = null) =>
        bridge.RegisterListener(id, id, (_, _) => { }, (_, _) => { }, (_, _, _, _) => { }, (_, _, _, _) => open?.Invoke());

    [Fact]
    public void FirstUseWithSoleUiWorksWhenSizeOwnerOfflineAndRemainsSticky()
    {
        _size.AssignNewSession("s", "offline:tab");
        var owners = new BrowserPreviewOwnerService(_size);
        var bridge = new BrowserUiBridge(new(), owners);
        AddUi(bridge, "home:tab");
        Assert.True(bridge.RequestOpen("s", "default", "https://example.com", false, out _));
        var generation = owners.GetGeneration("s", "default");
        AddUi(bridge, "work:tab");
        _size.AssignNewSession("s", "work:tab");
        Assert.Equal("home:tab", owners.ResolveOwnerBrowserId("s", "default"));
        Assert.Equal(generation, owners.GetGeneration("s", "default"));
        bridge.UnregisterListener("home:tab");
        Assert.False(bridge.RequestOpen("s", "default", "https://example.com", false, out var error));
        Assert.Contains("--browser", error, StringComparison.Ordinal);
        Assert.Equal("home:tab", owners.GetOwnerBrowserId("s", "default"));
        Assert.True(bridge.RequestClaim("s", "default", out _, "work:tab"));
        Assert.True(owners.GetGeneration("s", "default") > generation);
    }

    [Fact]
    public void ConnectedSizeOwnerInitializesOnlyItsPreviewAndExactSiblingClaimIsRequired()
    {
        _size.AssignNewSession("s", "profile:one");
        var owners = new BrowserPreviewOwnerService(_size);
        var bridge = new BrowserUiBridge(new(), owners);
        AddUi(bridge, "profile:one"); AddUi(bridge, "profile:two");
        Assert.Equal("profile:one", owners.ResolveOwnerBrowserId("s", "first"));
        Assert.False(bridge.RequestClaim("s", "first", out _, "profile"));
        Assert.False(bridge.RequestClaim("s", "first", out _));
        Assert.True(bridge.RequestClaim("s", "second", out _, "profile:two"));
        Assert.Equal("profile:one", owners.GetOwnerBrowserId("s", "first"));
        Assert.Equal("profile:two", owners.GetOwnerBrowserId("s", "second"));
    }

    [Fact]
    public async Task CommandsStatusAndUiUseSameOwnerAndHandoffFencesLateResponse()
    {
        var owners = new BrowserPreviewOwnerService(_size);
        var bridge = new BrowserUiBridge(new(), owners);
        AddUi(bridge, "a:tab"); AddUi(bridge, "a:sibling");
        bridge.RequestClaim("s", "default", out _, "a:tab");
        var service = new BrowserCommandService(previewOwnerService: owners);
        BrowserWsMessage? pending = null;
        service.TryRegisterClient("c", "s", "default", "p", msg => pending = msg, "a:tab");
        service.TryRegisterClient("other", "s", "default", "other", _ => throw new InvalidOperationException("wrong tab"), "a:sibling", true, true);
        var status = service.GetStatus("https://example.com", "s", "default", connectedUiClientCount: 2);
        Assert.Equal("a:tab", status.DefaultClient?.BrowserId);
        var command = service.ExecuteCommandAsync(new() { SessionId = "s", PreviewName = "default", Command = "click" }, default);
        Assert.NotNull(pending);
        bridge.RequestClaim("s", "default", out _, "a:sibling");
        service.ReceiveResult(new() { Id = pending.Id, PreviewId = "p", Success = true });
        Assert.False((await command).Success);
        Assert.Equal("a:sibling", service.GetStatus("https://example.com", "s", "default", connectedUiClientCount: 2).DefaultClient?.BrowserId);
    }

    [Fact]
    public async Task UiAcknowledgementFromPreviousGenerationCannotSucceed()
    {
        var owners = new BrowserPreviewOwnerService();
        var bridge = new BrowserUiBridge(new(), owners);
        string? requestId = null;
        bridge.RegisterAcknowledgedListener("a", "a:tab", (_, _, _) => { }, (_, _, _) => { }, (_, _, _, _, _) => { },
            (id, _, _, _, _, _) => requestId = id);
        AddUi(bridge, "b:tab");
        bridge.RequestClaim("s", "default", out _, "a:tab");
        var pending = bridge.RequestOpenAsync("s", "default", "https://example.com", false, 1);
        Assert.NotNull(requestId);
        bridge.RequestClaim("s", "default", out _, "b:tab");
        bridge.RequestClaim("s", "default", out _, "a:tab"); // Returning to the same tab is a new generation.
        bridge.CompleteUiCommand(new() { RequestId = requestId, Command = "open", Success = true });
        Assert.False((await pending).Success);
    }

    [Fact]
    public void OldBridgeCannotBecomeReadyAfterOwnershipReturnsToSameTab()
    {
        var owners = new BrowserPreviewOwnerService();
        owners.RegisterUi("a", "a:tab"); owners.Claim("s", "default", "a:tab");
        var service = new BrowserCommandService(previewOwnerService: owners);
        service.TryRegisterClient("c", "s", "default", "p", _ => { }, "a:tab", ownershipGeneration: owners.GetGeneration("s", "default"));
        Assert.True(service.GetStatus("https://example.com", "s", "default").Controllable);
        owners.Claim("s", "default", "a:tab");
        Assert.False(service.GetStatus("https://example.com", "s", "default").Controllable);
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public async Task ReadinessTimeoutNeverReportsStaleOrHiddenClientReady(bool visible, long revision)
    {
        var service = new BrowserCommandService();
        service.TryRegisterClient("c", "s", "default", "p", _ => { }, "a", isVisible: visible, targetRevision: revision);
        var status = await service.WaitForControllableAsync("https://example.com", "s", "default",
            requireVisibleClient: true, requiredTargetRevision: 1,
            timeout: TimeSpan.FromMilliseconds(25), pollInterval: TimeSpan.FromMilliseconds(5));
        Assert.False(status.Controllable);
        Assert.Equal("waiting", status.State);
    }

    public void Dispose()
    {
        _size.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
