using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TerminalSizeControlServiceTests
{

    [Fact]
    public async Task ExplicitClaim_ImmediatelyOverridesFreshOwner()
    {
        using var fixture = new Fixture();
        fixture.Service.RegisterBrowser("browser-a:tab-1", new object());
        await fixture.Service.RequestControlAsync("session-1", "browser-a:tab-1", true, ct: Xunit.TestContext.Current.CancellationToken);

        var result = await fixture.Service.RequestControlAsync("session-1", "browser-b:tab-2", true, ct: Xunit.TestContext.Current.CancellationToken);

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
            true, ct: Xunit.TestContext.Current.CancellationToken);

        var claims = await Task.WhenAll(
            fixture.Service.RequestControlAsync(
                "session-1",
                "browser-b:tab-2",
                true,
                expectedEpoch: owner.Status.Epoch, ct: Xunit.TestContext.Current.CancellationToken),
            fixture.Service.RequestControlAsync(
                "session-1",
                "browser-c:tab-3",
                true,
                expectedEpoch: owner.Status.Epoch, ct: Xunit.TestContext.Current.CancellationToken));

        Assert.Single(claims, result => result.OwnershipChanged);
        Assert.All(claims, result => Assert.Equal(owner.Status.Epoch + 1, result.Status.Epoch));
        var browserB = fixture.Service.GetStatus("session-1", "browser-b:tab-2");
        var browserC = fixture.Service.GetStatus("session-1", "browser-c:tab-3");
        Assert.NotEqual(browserB.IsOwner, browserC.IsOwner);
    }

    [Fact]
    public async Task ExplicitHandoff_CannotBeUndoneByPassiveFormerOwner()
    {
        using var fixture = new Fixture();
        fixture.Service.RegisterBrowser("browser-a:tab-1", new object());
        fixture.Service.RegisterBrowser("browser-b:tab-2", new object());
        await fixture.Service.RequestControlAsync("session-1", "browser-a:tab-1", true, ct: Xunit.TestContext.Current.CancellationToken);
        var handoff = await fixture.Service.RequestControlAsync(
            "session-1",
            "browser-b:tab-2",
            true, ct: Xunit.TestContext.Current.CancellationToken);
        var oldOwnerInput = await fixture.Service.RequestControlAsync(
            "session-1",
            "browser-a:tab-1", false, ct: Xunit.TestContext.Current.CancellationToken);

        Assert.True(handoff.Status.IsOwner);
        Assert.False(oldOwnerInput.OwnershipChanged);
        Assert.False(oldOwnerInput.Status.IsOwner);
        Assert.Equal(handoff.Status.Epoch, oldOwnerInput.Status.Epoch);
    }

    [Fact]
    public async Task Resize_RequiresCurrentOwnerAndEpoch()
    {
        using var fixture = new Fixture();
        var owner = await fixture.Service.RequestControlAsync("session-1", "browser-a:tab-1", true, ct: Xunit.TestContext.Current.CancellationToken);
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
            }, Xunit.TestContext.Current.CancellationToken);
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
            }, Xunit.TestContext.Current.CancellationToken);
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
            }, Xunit.TestContext.Current.CancellationToken);

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
        var owner = await fixture.Service.RequestControlAsync("session-1", "browser-a:tab-1", true, ct: Xunit.TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Service.ResizeAsync(
            "session-1",
            "browser-a:tab-1",
            owner.Status.Epoch,
            cols,
            rows,
            _ => Task.FromResult(true), Xunit.TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CurrentOwnerTypingDoesNotWaitForResizeButHandoffSerializes()
    {
        using var fixture = new Fixture();
        fixture.Service.RegisterBrowser("desktop:tab", new object());
        var owner = await fixture.Service.RequestControlAsync("session-1", "desktop:tab", true, ct: Xunit.TestContext.Current.CancellationToken);
        var acknowledge = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable IDISP013 // Intentionally held pending, then awaited in finally before fixture disposal.
        var resizing = fixture.Service.ResizeAsync("session-1", "desktop:tab", owner.Status.Epoch,
            100, 30, _ => acknowledge.Task, Xunit.TestContext.Current.CancellationToken);
#pragma warning restore IDISP013
        try
        {
            Assert.False(resizing.IsCompleted);
            var ownerInput = fixture.Service.RecordInputAsync("session-1", "desktop:tab", ct: Xunit.TestContext.Current.CancellationToken);
            Assert.True(ownerInput.IsCompletedSuccessfully);
            Assert.True(ownerInput.Result.Status.IsOwner);
            var followerInput = fixture.Service.RecordInputAsync("session-1", "ipad:tab", ct: Xunit.TestContext.Current.CancellationToken);
            Assert.False(followerInput.IsCompleted);
            acknowledge.SetResult(true);
            var handoff = await followerInput;
            Assert.True(handoff.Status.IsOwner);
        }
        finally
        {
            acknowledge.TrySetResult(true);
            await resizing;
        }
    }

    [Fact]
    public async Task HeadlessResizeCannotChangeBrowserOwnedSize()
    {
        using var fixture = new Fixture();
        var calls = 0;
        Task<bool> Resize(CancellationToken _) { calls++; return Task.FromResult(true); }
        Assert.True(await fixture.Service.ResizeUnownedAsync("session-1", 100, 30, Resize, Xunit.TestContext.Current.CancellationToken));
        await fixture.Service.RequestControlAsync("session-1", "desktop:tab", true, ct: Xunit.TestContext.Current.CancellationToken);
        Assert.False(await fixture.Service.ResizeUnownedAsync("session-1", 80, 24, Resize, Xunit.TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NewOwnerInputSupersedesAnOlderHandoffWaitingForResize()
    {
        using var fixture = new Fixture();
        var original = await fixture.Service.RequestControlAsync("session-1", "desktop:one", true,
            ct: Xunit.TestContext.Current.CancellationToken);
        var acknowledge = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable IDISP013 // Held pending deliberately; both operations are awaited before fixture disposal.
        var resize = fixture.Service.ResizeAsync("session-1", "desktop:one", original.Status.Epoch,
            100, 30, _ => acknowledge.Task, Xunit.TestContext.Current.CancellationToken);
        var delayedInput = fixture.Service.RecordInputAsync("session-1", "link:one",
            ct: Xunit.TestContext.Current.CancellationToken);
#pragma warning restore IDISP013
        try
        {
            Assert.False(delayedInput.IsCompleted);
            var currentInput = await fixture.Service.RecordInputAsync("session-1", "desktop:one",
                ct: Xunit.TestContext.Current.CancellationToken);
            Assert.True(currentInput.Status.IsOwner);
        }
        finally { acknowledge.TrySetResult(true); await resize; }
        var delayed = await delayedInput;
        Assert.False(delayed.Status.IsOwner);
        Assert.False(delayed.OwnershipChanged);
        Assert.Equal(original.Status.Epoch, delayed.Status.Epoch);
    }

    [Fact]
    public async Task RestartPreservesOwnerAndEpochUntilNewHumanInput()
    {
        using var fixture = new Fixture();
        var original = await fixture.Service.RequestControlAsync("session-1", "desktop:one", true,
            ct: Xunit.TestContext.Current.CancellationToken);
        fixture.RestartService();
        var passive = await fixture.Service.RequestControlAsync("session-1", "desktop:two", false,
            ct: Xunit.TestContext.Current.CancellationToken);
        Assert.False(passive.Status.IsOwner);
        Assert.Equal(original.Status.Epoch, passive.Status.Epoch);
        Assert.Equal("desktop:one", fixture.Service.GetOwnerBrowserId("session-1"));
        var active = await fixture.Service.RecordInputAsync("session-1", "link:one",
            ct: Xunit.TestContext.Current.CancellationToken);
        Assert.True(active.Status.IsOwner);
        Assert.Equal(original.Status.Epoch + 1, active.Status.Epoch);
        fixture.RestartService();
        Assert.Equal("link:one", fixture.Service.GetOwnerBrowserId("session-1"));
        Assert.Equal(active.Status.Epoch, fixture.Service.GetStatus("session-1", "link:one").Epoch);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"midterm-size-control-{Guid.NewGuid():N}");

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Service = new TerminalSizeControlService(_directory);
        }

        public TerminalSizeControlService Service { get; private set; }

        public void RestartService()
        {
            Service.Dispose();
            Service = new TerminalSizeControlService(_directory);
        }

        public void Dispose()
        {
            Service.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

}
