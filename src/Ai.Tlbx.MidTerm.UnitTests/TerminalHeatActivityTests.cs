using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;
namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TerminalHeatActivityTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Fact]
    public void TextClockAndTransportClockAreSeparateAndNotificationsAreBounded()
    {
        var clock = new Clock();
        var service = new SessionTelemetryService(clock);
        var events = new List<string>();
        service.TextActivity += events.Add;
        service.RecordOutput("a", "text"u8);
        var first = service.GetSnapshot("a").LastTextOutputAt;
        for (var i = 0; i < 100; i++)
        {
            clock.Now += TimeSpan.FromMilliseconds(10);
            service.RecordOutput("a", "\u001b[31m⠁ ⠂\u001b[0m"u8);
        }
        Assert.Single(events);
        Assert.Equal(first, service.GetSnapshot("a").LastTextOutputAt);
        Assert.Equal(clock.Now, service.GetSnapshot("a").LastOutputAt);
        for (var i = 0; i < 100; i++)
        {
            service.RecordOutput("a", "x"u8);
            clock.Now += TimeSpan.FromMilliseconds(10);
        }
        Assert.Equal(5, events.Count);
        Assert.Equal(clock.Now - TimeSpan.FromMilliseconds(10), service.GetSnapshot("a").LastTextOutputAt);
        Assert.Null(service.GetSnapshot("b").LastTextOutputAt);
        service.RecordOutput("b", "short"u8);
        Assert.Equal("b", events[^1]);
        service.ClearSession("a");
        Assert.Null(service.GetSnapshot("a").LastTextOutputAt);
    }
}
