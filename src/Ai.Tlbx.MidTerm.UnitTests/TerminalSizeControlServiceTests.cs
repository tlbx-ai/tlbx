using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TerminalSizeControlServiceTests
{
    [Fact]
    public async Task Interaction_ClaimsUnownedSession()
    {
        using var fixture = new Fixture();

        var result = await fixture.Service.RequestControlAsync("session-1", "browser-a:tab-1", false);

        Assert.True(result.OwnershipChanged);
        Assert.True(result.Status.IsOwner);
        Assert.Equal(1, result.Status.Epoch);
    }

    [Fact]
    public async Task Interaction_DoesNotTakeOverFreshOnlineOwner()
    {
        using var fixture = new Fixture();
        var ownerConnection = new object();
        fixture.Service.RegisterBrowser("browser-a:tab-1", ownerConnection);
        await fixture.Service.RequestControlAsync("session-1", "browser-a:tab-1", true);

        var result = await fixture.Service.RequestControlAsync("session-1", "browser-b:tab-2", false);

        Assert.False(result.OwnershipChanged);
        Assert.False(result.Status.IsOwner);
        Assert.True(result.Status.OwnerOnline);
    }

    [Fact]
    public async Task Interaction_TakesOverOnlineOwnerAfterGenuineWorkLeaseExpires()
    {
        using var fixture = new Fixture();
        fixture.Service.RegisterBrowser("browser-a:tab-1", new object());
        await fixture.Service.RequestControlAsync("session-1", "browser-a:tab-1", true);
        fixture.Time.Advance(TerminalSizeControlService.ConnectedIdleTakeoverDelay);

        var result = await fixture.Service.RequestControlAsync("session-1", "browser-b:tab-2", false);

        Assert.True(result.OwnershipChanged);
        Assert.True(result.Status.IsOwner);
        Assert.Equal(2, result.Status.Epoch);
    }

    [Fact]
    public async Task Interaction_TakesOverOfflineOwnerAfterShortGracePeriod()
    {
        using var fixture = new Fixture();
        var ownerConnection = new object();
        fixture.Service.RegisterBrowser("browser-a:tab-1", ownerConnection);
        await fixture.Service.RequestControlAsync("session-1", "browser-a:tab-1", true);
        fixture.Service.UnregisterBrowser("browser-a:tab-1", ownerConnection);

        fixture.Time.Advance(TerminalSizeControlService.OfflineTakeoverDelay - TimeSpan.FromSeconds(1));
        var early = await fixture.Service.RequestControlAsync("session-1", "browser-b:tab-2", false);
        Assert.False(early.OwnershipChanged);

        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var eligible = await fixture.Service.RequestControlAsync("session-1", "browser-b:tab-2", false);

        Assert.True(eligible.OwnershipChanged);
        Assert.True(eligible.Status.IsOwner);
    }

    [Fact]
    public async Task ReopenedTabInSameBrowserProfile_InheritsOfflineOwnerImmediately()
    {
        using var fixture = new Fixture();
        var ownerConnection = new object();
        fixture.Service.RegisterBrowser("browser-a:tab-1", ownerConnection);
        await fixture.Service.RequestControlAsync(
            "session-1",
            "browser-a:tab-1",
            true,
            "Windows PC · Chrome");
        fixture.Service.UnregisterBrowser("browser-a:tab-1", ownerConnection);

        var result = await fixture.Service.RequestControlAsync(
            "session-1",
            "browser-a:tab-2",
            false,
            "Windows PC · Chrome");

        Assert.True(result.OwnershipChanged);
        Assert.True(result.Status.IsOwner);
        Assert.Equal("Windows PC · Chrome", result.Status.OwnerLabel);
    }

    [Fact]
    public async Task ConnectedSiblingTab_IsReportedAsSameBrowserProfile()
    {
        using var fixture = new Fixture();
        fixture.Service.RegisterBrowser("browser-a:tab-1", new object());
        await fixture.Service.RequestControlAsync("session-1", "browser-a:tab-1", true);

        var sibling = fixture.Service.GetStatus("session-1", "browser-a:tab-2");

        Assert.True(sibling.OwnerOnline);
        Assert.True(sibling.OwnerInSameBrowserProfile);
        Assert.False(sibling.IsOwner);
    }

    [Fact]
    public async Task OwnerLabel_IdentifiesDeviceThatWouldLoseControl()
    {
        using var fixture = new Fixture();
        fixture.Service.RegisterBrowser("browser-a:tab-1", new object());

        await fixture.Service.RequestControlAsync(
            "session-1",
            "browser-a:tab-1",
            true,
            "Work PC · Chrome");
        var follower = fixture.Service.GetStatus("session-1", "browser-b:tab-2");

        Assert.False(follower.IsOwner);
        Assert.Equal("Work PC · Chrome", follower.OwnerLabel);
    }

    [Fact]
    public async Task ExplicitClaim_ImmediatelyOverridesFreshOwner()
    {
        using var fixture = new Fixture();
        fixture.Service.RegisterBrowser("browser-a:tab-1", new object());
        await fixture.Service.RequestControlAsync("session-1", "browser-a:tab-1", true);

        var result = await fixture.Service.RequestControlAsync("session-1", "browser-b:tab-2", true);

        Assert.True(result.OwnershipChanged);
        Assert.True(result.Status.IsOwner);
    }

    [Fact]
    public async Task AutomaticHandoff_DoesNotImmediatelyPingPongBack()
    {
        using var fixture = new Fixture();
        fixture.Service.RegisterBrowser("browser-a:tab-1", new object());
        fixture.Service.RegisterBrowser("browser-b:tab-2", new object());
        await fixture.Service.RequestControlAsync("session-1", "browser-a:tab-1", true);
        fixture.Time.Advance(TerminalSizeControlService.ConnectedIdleTakeoverDelay);

        var handoff = await fixture.Service.RequestControlAsync(
            "session-1",
            "browser-b:tab-2",
            false);
        var oldOwnerInput = await fixture.Service.RequestControlAsync(
            "session-1",
            "browser-a:tab-1",
            false);

        Assert.True(handoff.Status.IsOwner);
        Assert.False(oldOwnerInput.OwnershipChanged);
        Assert.False(oldOwnerInput.Status.IsOwner);
        Assert.Equal(handoff.Status.Epoch, oldOwnerInput.Status.Epoch);
    }

    [Fact]
    public async Task OwnerInput_RenewsConnectedLease()
    {
        using var fixture = new Fixture();
        fixture.Service.RegisterBrowser("browser-a:tab-1", new object());
        await fixture.Service.RequestControlAsync("session-1", "browser-a:tab-1", true);
        fixture.Time.Advance(TimeSpan.FromMinutes(4));
        await fixture.Service.RequestControlAsync("session-1", "browser-a:tab-1", false);
        fixture.Time.Advance(TimeSpan.FromMinutes(2));

        var result = await fixture.Service.RequestControlAsync(
            "session-1",
            "browser-b:tab-2",
            false);

        Assert.False(result.OwnershipChanged);
        Assert.False(result.Status.IsOwner);
    }

    [Fact]
    public async Task Resize_RequiresCurrentOwnerAndEpoch()
    {
        using var fixture = new Fixture();
        var owner = await fixture.Service.RequestControlAsync("session-1", "browser-a:tab-1", true);
        var resizeCalls = 0;

        var accepted = await fixture.Service.ResizeAsync(
            "session-1",
            "browser-a:tab-1",
            owner.Status.Epoch,
            120,
            30,
            _ =>
            {
                resizeCalls++;
                return Task.FromResult(true);
            });
        var stale = await fixture.Service.ResizeAsync(
            "session-1",
            "browser-a:tab-1",
            owner.Status.Epoch - 1,
            100,
            25,
            _ =>
            {
                resizeCalls++;
                return Task.FromResult(true);
            });
        var follower = await fixture.Service.ResizeAsync(
            "session-1",
            "browser-b:tab-2",
            owner.Status.Epoch,
            80,
            20,
            _ =>
            {
                resizeCalls++;
                return Task.FromResult(true);
            });

        Assert.True(accepted.ResizeApplied);
        Assert.False(stale.ResizeApplied);
        Assert.False(follower.ResizeApplied);
        Assert.Equal(1, resizeCalls);
    }

    [Theory]
    [InlineData(9, 30)]
    [InlineData(301, 30)]
    [InlineData(120, 4)]
    [InlineData(120, 101)]
    public async Task Resize_RejectsOutOfRangeDimensions(int cols, int rows)
    {
        using var fixture = new Fixture();
        var owner = await fixture.Service.RequestControlAsync("session-1", "browser-a:tab-1", true);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Service.ResizeAsync(
            "session-1",
            "browser-a:tab-1",
            owner.Status.Epoch,
            cols,
            rows,
            _ => Task.FromResult(true)));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"midterm-size-control-{Guid.NewGuid():N}");

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));
            Service = new TerminalSizeControlService(_directory, Time);
        }

        public ManualTimeProvider Time { get; }
        public TerminalSizeControlService Service { get; }

        public void Dispose()
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed)
        {
            _utcNow += elapsed;
        }
    }
}
