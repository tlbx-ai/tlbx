using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TerminalSizeControlServiceTests
{
    [Fact]
    public async Task Interaction_ClaimsUnownedSession()
    {
        using var fixture = new Fixture();

        var result = await fixture.Service.RecordInputAsync("session-1", "browser-a:tab-1");

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

        var result = await fixture.Service.RecordInputAsync("session-1", "browser-b:tab-2");

        Assert.False(result.OwnershipChanged);
        Assert.False(result.Status.IsOwner);
        Assert.True(result.Status.OwnerOnline);
    }

    [Fact]
    public async Task Interaction_TakesOverDifferentOnlineDeviceAfterProtectionWindow()
    {
        using var fixture = new Fixture();
        fixture.Service.RegisterBrowser("browser-a:tab-1", new object());
        fixture.Service.RegisterBrowser("browser-b:tab-2", new object());
        await fixture.Service.RequestControlAsync("session-1", "browser-a:tab-1", true);
        fixture.Time.Advance(TerminalSizeControlService.ConnectedOwnerProtectionDelay);

        var result = await fixture.Service.RecordInputAsync("session-1", "browser-b:tab-2");

        Assert.True(result.OwnershipChanged);
        Assert.True(result.Status.IsOwner);
        Assert.True(result.Status.OwnerOnline);
    }

    [Fact]
    public async Task ConnectedSiblingTab_NeverAutomaticallyStealsAfterProtectionWindow()
    {
        using var fixture = new Fixture();
        fixture.Service.RegisterBrowser("browser-a:tab-1", new object());
        fixture.Service.RegisterBrowser("browser-a:tab-2", new object());
        await fixture.Service.RequestControlAsync("session-1", "browser-a:tab-1", true);
        fixture.Time.Advance(TerminalSizeControlService.ConnectedOwnerProtectionDelay);

        var result = await fixture.Service.RecordInputAsync(
            "session-1",
            "browser-a:tab-2");

        Assert.False(result.OwnershipChanged);
        Assert.False(result.Status.IsOwner);
        Assert.True(result.Status.OwnerOnline);
        Assert.True(result.Status.OwnerInSameBrowserProfile);
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
        var early = await fixture.Service.RecordInputAsync("session-1", "browser-b:tab-2");
        Assert.False(early.OwnershipChanged);

        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var eligible = await fixture.Service.RecordInputAsync("session-1", "browser-b:tab-2");

        Assert.True(eligible.OwnershipChanged);
        Assert.True(eligible.Status.IsOwner);
    }

    [Fact]
    public async Task IdleOwnerStillGetsFullOfflineGracePeriodFromDisconnect()
    {
        using var fixture = new Fixture();
        var ownerConnection = new object();
        fixture.Service.RegisterBrowser("browser-a:tab-1", ownerConnection);
        await fixture.Service.RequestControlAsync("session-1", "browser-a:tab-1", true);
        fixture.Time.Advance(TimeSpan.FromHours(1));
        fixture.Service.UnregisterBrowser("browser-a:tab-1", ownerConnection);

        var immediate = await fixture.Service.RecordInputAsync(
            "session-1",
            "browser-b:tab-2");
        fixture.Time.Advance(TerminalSizeControlService.OfflineTakeoverDelay);
        var afterGrace = await fixture.Service.RecordInputAsync(
            "session-1",
            "browser-b:tab-2");

        Assert.False(immediate.OwnershipChanged);
        Assert.True(afterGrace.OwnershipChanged);
        Assert.True(afterGrace.Status.IsOwner);
    }

    [Fact]
    public async Task RestartedServiceGivesPersistedOwnerFreshOfflineGracePeriod()
    {
        using var fixture = new Fixture();
        fixture.Service.RegisterBrowser("browser-a:tab-1", new object());
        await fixture.Service.RequestControlAsync("session-1", "browser-a:tab-1", true);
        fixture.Time.Advance(TimeSpan.FromHours(1));
        fixture.RestartService();

        var immediate = await fixture.Service.RecordInputAsync(
            "session-1",
            "browser-b:tab-2");
        fixture.Time.Advance(TerminalSizeControlService.OfflineTakeoverDelay);
        var afterGrace = await fixture.Service.RecordInputAsync(
            "session-1",
            "browser-b:tab-2");

        Assert.False(immediate.OwnershipChanged);
        Assert.True(afterGrace.OwnershipChanged);
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

        var result = await fixture.Service.RecordInputAsync(
            "session-1",
            "browser-a:tab-2",
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
    public async Task ConcurrentExplicitClaimsWithOneObservedEpochHaveOneWinner()
    {
        using var fixture = new Fixture();
        var owner = await fixture.Service.RequestControlAsync(
            "session-1",
            "browser-a:tab-1",
            true);

        var claims = await Task.WhenAll(
            fixture.Service.RequestControlAsync(
                "session-1",
                "browser-b:tab-2",
                true,
                expectedEpoch: owner.Status.Epoch),
            fixture.Service.RequestControlAsync(
                "session-1",
                "browser-c:tab-3",
                true,
                expectedEpoch: owner.Status.Epoch));

        Assert.Single(claims, result => result.OwnershipChanged);
        Assert.All(claims, result => Assert.Equal(owner.Status.Epoch + 1, result.Status.Epoch));
        var browserB = fixture.Service.GetStatus("session-1", "browser-b:tab-2");
        var browserC = fixture.Service.GetStatus("session-1", "browser-c:tab-3");
        Assert.NotEqual(browserB.IsOwner, browserC.IsOwner);
    }

    [Fact]
    public async Task ExplicitHandoff_DoesNotImmediatelyPingPongBack()
    {
        using var fixture = new Fixture();
        fixture.Service.RegisterBrowser("browser-a:tab-1", new object());
        fixture.Service.RegisterBrowser("browser-b:tab-2", new object());
        await fixture.Service.RequestControlAsync("session-1", "browser-a:tab-1", true);
        var handoff = await fixture.Service.RequestControlAsync(
            "session-1",
            "browser-b:tab-2",
            true);
        var oldOwnerInput = await fixture.Service.RecordInputAsync(
            "session-1",
            "browser-a:tab-1");

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
        await fixture.Service.RecordInputAsync("session-1", "browser-a:tab-1");
        fixture.Time.Advance(TimeSpan.FromMinutes(2));

        var result = await fixture.Service.RecordInputAsync(
            "session-1",
            "browser-b:tab-2");

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

    [Fact]
    public async Task TypingDoesNotWaitForResizeAcknowledgement()
    {
        using var fixture = new Fixture();
        fixture.Service.RegisterBrowser("desktop:tab", new object());
        var owner = await fixture.Service.RequestControlAsync("session-1", "desktop:tab", true);
        var acknowledge = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable IDISP013 // Intentionally held pending, then awaited in finally before fixture disposal.
        var resizing = fixture.Service.ResizeAsync("session-1", "desktop:tab", owner.Status.Epoch,
            100, 30, _ => acknowledge.Task);
#pragma warning restore IDISP013
        try
        {
            Assert.False(resizing.IsCompleted);
            var ownerInput = fixture.Service.RecordInputAsync("session-1", "desktop:tab");
            Assert.True(ownerInput.IsCompletedSuccessfully);
            Assert.True(ownerInput.Result.Status.IsOwner);
            var followerInput = fixture.Service.RecordInputAsync("session-1", "ipad:tab");
            Assert.True(followerInput.IsCompletedSuccessfully);
            Assert.False(followerInput.Result.Status.IsOwner);
        }
        finally
        {
            acknowledge.SetResult(true);
            await resizing;
        }
    }

    [Fact]
    public async Task PassiveReplacementTabInheritsOnlyItsOwnOfflineProfile()
    {
        using var fixture = new Fixture();
        var connection = new object();
        fixture.Service.RegisterBrowser("desktop:old", connection);
        await fixture.Service.RequestControlAsync("session-1", "desktop:old", true);
        Assert.False((await fixture.Service.RequestControlAsync("session-1", "desktop:new", false)).Status.IsOwner);
        fixture.Service.UnregisterBrowser("desktop:old", connection);
        Assert.False((await fixture.Service.RequestControlAsync("session-1", "ipad:new", false)).Status.IsOwner);
        Assert.True((await fixture.Service.RequestControlAsync("session-1", "desktop:new", false)).Status.IsOwner);
    }

    [Fact]
    public async Task PassiveIpadNeverTakesDesktopAfterExpiryOrReconnect()
    {
        using var fixture = new Fixture();
        var desktop = new object();
        fixture.Service.RegisterBrowser("desktop:tab", desktop);
        fixture.Service.RegisterBrowser("ipad:tab", new object());
        var owner = await fixture.Service.RequestControlAsync("session-1", "desktop:tab", true);
        fixture.Time.Advance(TimeSpan.FromHours(1));
        var online = await fixture.Service.RequestControlAsync("session-1", "ipad:tab", false);
        Assert.False(online.Status.IsOwner);
        fixture.Service.UnregisterBrowser("desktop:tab", desktop);
        fixture.Time.Advance(TimeSpan.FromHours(1));
        var offline = await fixture.Service.RequestControlAsync("session-1", "ipad:tab", false);
        Assert.False(offline.Status.IsOwner);
        fixture.Service.RegisterBrowser("desktop:tab", desktop);
        Assert.True(fixture.Service.GetStatus("session-1", "desktop:tab").IsOwner);
        Assert.Equal(owner.Status.Epoch, offline.Status.Epoch);
        var input = await fixture.Service.RecordInputAsync("session-1", "ipad:tab");
        Assert.True(input.Status.IsOwner);
    }

    [Fact]
    public async Task PassiveOwnerRequestDoesNotRenewProtection()
    {
        using var fixture = new Fixture();
        fixture.Service.RegisterBrowser("desktop:tab", new object());
        await fixture.Service.RequestControlAsync("session-1", "desktop:tab", true);
        fixture.Time.Advance(TimeSpan.FromMinutes(4));
        await fixture.Service.RequestControlAsync("session-1", "desktop:tab", false);
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await fixture.Service.RecordInputAsync("session-1", "ipad:tab")).Status.IsOwner);
    }

    [Fact]
    public async Task HeadlessResizeCannotChangeBrowserOwnedSize()
    {
        using var fixture = new Fixture();
        var calls = 0;
        Task<bool> Resize(CancellationToken _) { calls++; return Task.FromResult(true); }
        Assert.True(await fixture.Service.ResizeUnownedAsync("session-1", 100, 30, Resize));
        await fixture.Service.RequestControlAsync("session-1", "desktop:tab", true);
        Assert.False(await fixture.Service.ResizeUnownedAsync("session-1", 80, 24, Resize));
        Assert.Equal(1, calls);
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
        public TerminalSizeControlService Service { get; private set; }

        public void RestartService()
        {
            Service.Dispose();
            Service = new TerminalSizeControlService(_directory, Time);
        }

        public void Dispose()
        {
            Service.Dispose();
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
