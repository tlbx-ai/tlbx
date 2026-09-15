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

    [Fact]
    public void OpenAndClaimFollowSessionSizeOwnerDespiteStalePreviewAndGlobalOwners()
    {
        var main = new MainBrowserService();
        main.Claim("work:tab");
        var owners = new BrowserPreviewOwnerService(_size);
        owners.Claim("session", "default", "work:tab");
        var bridge = new BrowserUiBridge(main, owners);
        var opened = new List<string>();
        foreach (var id in new[] { "home:tab", "work:tab", "home:sibling" })
            bridge.RegisterListener(id, id, (_, _) => { }, (_, _) => { }, (_, _, _, _) => { },
                (_, _, _, _) => opened.Add(id));

        _size.AssignNewSession("session", "home:tab");
        Assert.True(bridge.RequestClaim("session", "default", out _));
        Assert.True(bridge.RequestOpen("session", "default", "https://example.com", true, out _));
        Assert.Equal(new[] { "home:tab" }, opened);
        Assert.Equal("home:tab", owners.ResolveOwnerBrowserId("session", "default", ["work:tab"]));

        _size.AssignNewSession("session", "work:tab");
        Assert.True(bridge.RequestOpen("session", "default", "https://example.com", true, out _));
        Assert.Equal(new[] { "home:tab", "work:tab" }, opened);
        _size.AssignNewSession("other", "home:sibling");
        Assert.True(bridge.RequestOpen("other", "default", "https://example.com", true, out _));
        Assert.Equal("home:sibling", opened[^1]);
    }

    [Fact]
    public async Task CommandsFollowExactSizeOwnerAndNeverFallBackToPassiveTabs()
    {
        var main = new MainBrowserService();
        main.Claim("work:tab");
        var owners = new BrowserPreviewOwnerService(_size);
        var service = new BrowserCommandService(main, owners);
        var bridge = new BrowserUiBridge(main, owners);
        foreach (var id in new[] { "home:tab", "work:tab", "home:sibling" })
        {
            service.TryRegisterClient(id, "session", "default", id, msg =>
                service.ReceiveResult(new BrowserWsResult { Id = msg.Id, PreviewId = id, Success = true, Result = id }), id);
            bridge.RegisterListener(id, id, (_, _) => { }, (_, _) => { }, (_, _, _, _) => { }, (_, _, _, _) => { });
        }
        _size.AssignNewSession("session", "home:tab");
        var request = new BrowserCommandRequest { SessionId = "session", PreviewName = "default", Command = "url" };
        Assert.Equal("home:tab", (await service.ExecuteCommandAsync(request, CancellationToken.None)).Result);
        _size.AssignNewSession("session", "work:tab");
        Assert.Equal("work:tab", (await service.ExecuteCommandAsync(request, CancellationToken.None)).Result);
        _size.AssignNewSession("session", "offline:tab");
        Assert.False((await service.ExecuteCommandAsync(request, CancellationToken.None)).Success);
        Assert.False(bridge.RequestOpen("session", "default", "https://example.com", true, out _));
    }

    public void Dispose()
    {
        _size.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
