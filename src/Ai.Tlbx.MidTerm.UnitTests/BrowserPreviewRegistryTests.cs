using Ai.Tlbx.MidTerm.Services.Browser;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public class BrowserPreviewRegistryTests
{

    [Fact]
    public void TryValidate_WithWrongToken_Fails()
    {
        var registry = new BrowserPreviewRegistry();
        var created = registry.Create("session-1", "default", "route-1");

        var ok = registry.TryValidate(created.PreviewId, "wrong-token", out _);

        Assert.False(ok);
    }

    [Fact]
    public void Remove_RevokesOnlyTheClosedNamedPreviewRegistrations()
    {
        var registry = new BrowserPreviewRegistry();
        var closed = registry.Create("session-1", "dai-e2e", "route-1");
        var parallel = registry.Create("session-1", "parallel", "route-2");

        var removed = registry.Remove("session-1", "dai-e2e");

        Assert.Equal(1, removed);
        Assert.False(registry.TryValidate(closed.PreviewId, closed.PreviewToken, out _));
        Assert.True(registry.TryValidate(parallel.PreviewId, parallel.PreviewToken, out _));
    }

    [Fact]
    public void OwnerRelease_RemovesClosedPreviewWithoutDisturbingParallelOwner()
    {
        var owners = new BrowserPreviewOwnerService();
        owners.Claim("session-1", "dai-e2e", "browser-a");
        owners.Claim("session-1", "parallel", "browser-b");

        Assert.True(owners.Release("session-1", "dai-e2e"));

        Assert.Null(owners.GetOwnerBrowserId("session-1", "dai-e2e"));
        Assert.Equal("browser-b", owners.GetOwnerBrowserId("session-1", "parallel"));
    }
}
