using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Settings;
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

    [Fact]
    public void StatusSnapshotRevisionAdvancesWithVisibleBrowserState()
    {
        var service = new MainBrowserService();
        var connection = new object();
        service.Register("browser-a:tab-1", connection);
        var registered = service.GetStatus("browser-a:tab-1");

        service.UpdateActivity(
            "browser-a:tab-1",
            connection,
            true,
            "session-1",
            "agent:codex");
        var active = service.GetStatus("browser-a:tab-1");

        Assert.True(active.Revision > registered.Revision);
        Assert.True(active.IsMain);
        Assert.Single(active.Browsers);
        Assert.Equal("agent:codex", active.Browsers[0].ActiveSurface);

        service.UpdateActivity(
            "browser-a:tab-1",
            connection,
            false,
            "session-1",
            "agent:codex");
        var inactive = service.GetStatus("browser-a:tab-1");

        Assert.True(inactive.Revision > active.Revision);
        Assert.False(inactive.Browsers[0].IsActive);
        Assert.Null(inactive.Browsers[0].ActiveSessionId);
        Assert.Null(inactive.Browsers[0].ActiveSurface);
    }

    [Fact]
    public void OlderConnectionCannotClearNewerActiveSurface()
    {
        var service = new MainBrowserService();
        var oldConnection = new object();
        var newConnection = new object();
        service.Register("browser-a:tab-1", oldConnection);
        service.Register("browser-a:tab-1", newConnection);
        service.UpdateActivity(
            "browser-a:tab-1",
            oldConnection,
            true,
            "session-1",
            "terminal");
        service.UpdateActivity(
            "browser-a:tab-1",
            newConnection,
            true,
            "session-2",
            "agent:codex");

        service.UpdateActivity("browser-a:tab-1", oldConnection, false);
        var status = service.GetStatus("browser-a:tab-1");

        Assert.True(status.Browsers[0].IsActive);
        Assert.Equal(1, status.Browsers[0].ActiveConnectionCount);
        Assert.Equal("session-2", status.Browsers[0].ActiveSessionId);
        Assert.Equal("agent:codex", status.Browsers[0].ActiveSurface);
    }

    [Fact]
    public void Register_RestoresStickyMainBrowserAfterRuntimeRestart()
    {
        var settingsDirectory = CreateTempDirectory();
        try
        {
            var firstRun = new MainBrowserService(new SettingsService(settingsDirectory));
            firstRun.Register("work-client:tab-1", new object());
            firstRun.Claim("work-client:tab-1");

            var secondRun = new MainBrowserService(new SettingsService(settingsDirectory));
            secondRun.Register("home-client:tab-1", new object());

            Assert.False(secondRun.IsMain("home-client:tab-1"));
            Assert.True(secondRun.ShouldShowButton("home-client:tab-1"));

            secondRun.Register("work-client:tab-1", new object());

            Assert.True(secondRun.IsMain("work-client:tab-1"));
            Assert.False(secondRun.IsMain("home-client:tab-1"));
        }
        finally
        {
            try
            {
                Directory.Delete(settingsDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"midterm_main_browser_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
