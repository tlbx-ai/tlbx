using Ai.Tlbx.MidTerm.Services;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class MainBrowserServiceTests
{
    [Fact]
    public void Unregister_PreservesMainOwnershipUntilAnotherBrowserExplicitlyClaimsIt()
    {
        var service = new MainBrowserService();
        var mainConnection = new object();
        var followerConnection = new object();

        service.Register("browser-a:tab-1", mainConnection);
        service.Claim("browser-a:tab-1");
        service.Register("browser-b:tab-2", followerConnection);

        service.Unregister("browser-a:tab-1", mainConnection);

        Assert.Equal("browser-a:tab-1", service.GetMainBrowserId());
        Assert.False(service.IsMain("browser-b:tab-2"));
        Assert.True(service.ShouldShowButton("browser-b:tab-2"));

        service.Claim("browser-b:tab-2");

        Assert.Equal("browser-b:tab-2", service.GetMainBrowserId());
        Assert.True(service.IsMain("browser-b:tab-2"));
    }

    [Fact]
    public void Register_AutoPromotesOnlyTheFirstBrowserSeenAfterRuntimeStart()
    {
        var service = new MainBrowserService();
        var firstConnection = new object();
        var followerConnection = new object();

        service.Register("browser-a:tab-1", firstConnection);
        service.Register("browser-b:tab-2", followerConnection);

        Assert.True(service.IsMain("browser-a:tab-1"));
        Assert.False(service.IsMain("browser-b:tab-2"));
    }

    [Fact]
    public void UpdateActivity_DoesNotAutoPromoteAnotherBrowserAfterInactivity()
    {
        var service = new MainBrowserService();
        var mainConnection = new object();
        var followerConnection = new object();

        service.Register("browser-a:tab-1", mainConnection);
        service.UpdateActivity("browser-a:tab-1", mainConnection, true);
        service.Claim("browser-a:tab-1");

        service.UpdateActivity("browser-a:tab-1", mainConnection, false);
        service.Register("browser-b:tab-2", followerConnection);
        service.UpdateActivity("browser-b:tab-2", followerConnection, true);

        Assert.True(service.IsMain("browser-a:tab-1"));
        Assert.False(service.IsMain("browser-b:tab-2"));
    }

    [Fact]
    public void GetBrowserStatuses_IncludesLeadingBrowserAndActiveSession()
    {
        var service = new MainBrowserService();
        var mainConnection = new object();
        var followerConnection = new object();

        service.Register("browser-a:tab-1", mainConnection);
        service.Register("browser-b:tab-2", followerConnection);
        service.UpdateActivity("browser-b:tab-2", followerConnection, true, "session-2", "agent:codex");

        var statuses = service.GetBrowserStatuses();

        Assert.Collection(
            statuses,
            leading =>
            {
                Assert.Equal("browser-a:tab-1", leading.BrowserId);
                Assert.True(leading.IsMain);
                Assert.False(leading.IsActive);
            },
            follower =>
            {
                Assert.Equal("browser-b:tab-2", follower.BrowserId);
                Assert.False(follower.IsMain);
                Assert.True(follower.IsActive);
                Assert.Equal("session-2", follower.ActiveSessionId);
                Assert.Equal("agent:codex", follower.ActiveSurface);
                Assert.Equal(1, follower.ConnectionCount);
                Assert.Equal(1, follower.ActiveConnectionCount);
            });
    }

    [Fact]
    public void Register_DoesNotImplicitlyReassignMainBrowserAfterRelease()
    {
        var service = new MainBrowserService();
        var firstConnection = new object();
        var secondConnection = new object();

        service.Register("browser-a:tab-1", firstConnection);
        service.Release("browser-a:tab-1");
        service.Register("browser-b:tab-2", secondConnection);

        Assert.Null(service.GetMainBrowserId());
        Assert.False(service.IsMain("browser-b:tab-2"));
        Assert.True(service.ShouldShowButton("browser-b:tab-2"));
    }
}
