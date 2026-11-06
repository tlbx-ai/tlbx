using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

// Owner-requested policy: current human input wins; passive browsers never reclaim.
public sealed class ActiveTerminalInputOwnershipTests
{
    [Theory]
    [InlineData("vpn:tab", "link:tab")]
    [InlineData("desktop:one", "desktop:two")]
    public async Task HumanInputImmediatelyMovesSizingToTheTypingBrowser(string previous, string typing)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tlbx-active-input-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var service = new TerminalSizeControlService(directory);
            service.RegisterBrowser(previous, new object());
            service.RegisterBrowser(typing, new object());
            var initial = await service.RequestControlAsync("session", previous, true,
                ct: TestContext.Current.CancellationToken);
            var active = await service.RecordInputAsync("session", typing,
                ct: TestContext.Current.CancellationToken);
            Assert.True(active.Status.IsOwner);
            Assert.Equal(initial.Status.Epoch + 1, active.Status.Epoch);
            var passive = await service.RequestControlAsync("session", previous, false,
                ct: TestContext.Current.CancellationToken);
            Assert.False(passive.Status.IsOwner);
            Assert.Equal(active.Status.Epoch, passive.Status.Epoch);
            var calls = 0;
            var staleResize = await service.ResizeAsync("session", previous, initial.Status.Epoch,
                80, 24, _ => { calls++; return Task.FromResult(true); }, TestContext.Current.CancellationToken);
            Assert.False(staleResize.ResizeApplied);
            Assert.Equal(0, calls);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task PassiveSiblingCannotInheritDuringAnOwnersConnectionGap()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tlbx-active-input-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var service = new TerminalSizeControlService(directory);
            var connection = new object();
            service.RegisterBrowser("desktop:active", connection);
            var initial = await service.RequestControlAsync("session", "desktop:active", true,
                ct: TestContext.Current.CancellationToken);
            service.UnregisterBrowser("desktop:active", connection);
            var passive = await service.RequestControlAsync("session", "desktop:idle", false,
                ct: TestContext.Current.CancellationToken);
            Assert.False(passive.Status.IsOwner);
            Assert.Equal(initial.Status.Epoch, passive.Status.Epoch);
            service.RegisterBrowser("desktop:active", connection);
            Assert.True(service.GetStatus("session", "desktop:active").IsOwner);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
