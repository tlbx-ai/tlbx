using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class SessionTelemetryServiceTests
{
    [Fact]
    public void RecordOutput_TracksHeatmapAndBellHistory()
    {
        var service = new SessionTelemetryService(new Microsoft.Extensions.Time.Testing.FakeTimeProvider());

        service.RecordOutput("sess1234", "hello"u8.ToArray());
        service.RecordOutput("sess1234", [0x07]);

        var activity = service.GetActivity("sess1234", 30, 10);

        Assert.Equal("sess1234", activity.SessionId);
        Assert.True(activity.TotalOutputBytes >= 6);
        Assert.Equal(1, activity.TotalBellCount);
        Assert.NotEmpty(activity.Heatmap);
        Assert.Single(activity.BellHistory);
        Assert.True(activity.CurrentHeat >= 0);
    }

    [Fact]
    public void RecordOutput_SetsCurrentHeatToFullForAnyFreshOutput()
    {
        var service = new SessionTelemetryService(new Microsoft.Extensions.Time.Testing.FakeTimeProvider());

        service.RecordOutput("sess1234", "."u8.ToArray());

        var snapshot = service.GetSnapshot("sess1234");
        var activity = service.GetActivity("sess1234", 30, 10);

        Assert.Equal(1, snapshot.CurrentHeat);
        Assert.Equal(1, activity.CurrentHeat);
        Assert.Contains(activity.Heatmap, sample => sample.Bytes > 0 && sample.Heat == 1);
    }

    [Fact]
    public void RecordOutput_DoesNotCreateHeatFromControlOnlyTraffic()
    {
        var service = new SessionTelemetryService(new Microsoft.Extensions.Time.Testing.FakeTimeProvider());

        service.RecordOutput("sess1234", [0x1B, 0x5B, (byte)'?', (byte)'2', (byte)'5', (byte)'h', 0x0D]);

        var snapshot = service.GetSnapshot("sess1234");
        var activity = service.GetActivity("sess1234", 30, 10);

        Assert.True(snapshot.TotalOutputBytes > 0);
        Assert.Equal(0, snapshot.CurrentHeat);
        Assert.Equal(0, activity.CurrentHeat);
        Assert.DoesNotContain(activity.Heatmap, sample => sample.Heat > 0);
    }

    [Fact]
    public void ClearSession_RemovesStoredActivity()
    {
        var service = new SessionTelemetryService(new Microsoft.Extensions.Time.Testing.FakeTimeProvider());

        service.RecordOutput("sess1234", "hello"u8.ToArray());
        service.ClearSession("sess1234");

        var activity = service.GetActivity("sess1234", 30, 10);

        Assert.Equal(0, activity.TotalOutputBytes);
        Assert.Empty(activity.BellHistory);
        Assert.All(activity.Heatmap, sample => Assert.Equal(0, sample.Bytes));
    }
}
