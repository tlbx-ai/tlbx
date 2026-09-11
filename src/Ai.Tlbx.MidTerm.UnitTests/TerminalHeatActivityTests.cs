using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;
namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TerminalHeatActivityTests
{
    [Fact]
    public void NestedSynchronizedPaintKeepsTheOriginalTextComparison()
    {
        var parser = new TerminalTextActivityParser();
        parser.CountTextUnits("A"u8, 8, 3);
        Assert.Equal(0, parser.CountTextUnits("\u001b[?2026h\u001b[HB\u001b[?2026h"u8, 8, 3));
        Assert.Equal(1, parser.CountTextUnits("\u001b[?2026l"u8, 8, 3));
        Assert.Equal(0, parser.CountTextUnits("\u001b[?2026h\u001b[HX\u001b[HA\u001b[HB\u001b[?2026l"u8, 8, 3));
    }

    [Theory]
    [InlineData("\u001b[2;3r\u001b[S", "A", "C", "")]
    [InlineData("\u001b[2;3r\u001b[T", "A", "", "B")]
    [InlineData("\u001b[2;1H\u001b[L", "A", "", "B")]
    [InlineData("\u001b[2;1H\u001b[M", "A", "C", "")]
    public void ScrollAndLineEditsPreserveCellsOutsideTheirRegion(string command, string first, string second, string third)
    {
        var parser = new TerminalTextActivityParser();
        parser.CountTextUnits("A\r\nB\r\nC"u8, 8, 3);
        parser.CountTextUnits(System.Text.Encoding.UTF8.GetBytes(command), 8, 3);
        var repaint = $"\u001b[?2026h\u001b[1;1H{first}\u001b[2;1H{second}\u001b[3;1H{third}\u001b[?2026l";
        Assert.Equal(0, parser.CountTextUnits(System.Text.Encoding.UTF8.GetBytes(repaint), 8, 3));
        Assert.Equal(1, parser.CountTextUnits("\u001b[?2026h\u001b[1;1HZ\u001b[?2026l"u8, 8, 3));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Fact]
    public void RepeatedSynchronizedLogLinesStillHeatWhenViewportScrolls()
    {
        var parser = new TerminalTextActivityParser();
        Assert.True(parser.CountTextUnits("\u001b[?2026hA\r\nA\u001b[?2026l"u8, 80, 2) > 0);
        Assert.True(parser.CountTextUnits("\u001b[?2026h\r\nA\u001b[?2026l"u8, 80, 2) > 0);
    }

    [Fact]
    public void RepaintAfterExternalClearAndVerticalShiftIsColdAcrossChunks()
    {
        var parser = new TerminalTextActivityParser();
        Assert.True(parser.CountTextUnits("old shell\r\n\u001b[?2026hHello\r\nPrompt\u001b[?2026l"u8, 80, 24) > 0);
        Assert.Equal(0, parser.CountTextUnits("\u001b[H\u001b[2J"u8, 80, 24));
        Assert.Equal(0, parser.CountTextUnits("\u001b[?2026hHello\r\nPro"u8, 80, 24));
        Assert.Equal(0, parser.CountTextUnits("mpt\u001b[?2026l"u8, 80, 24));
        Assert.True(parser.CountTextUnits("\u001b[H\u001b[2J\u001b[?2026hHello\r\nNew result\u001b[?2026l"u8, 80, 24) > 0);
    }

    [Fact]
    public void HeatDecaysContinuouslyAndDecorationsDoNotRearmIt()
    {
        var clock = new Clock(); var service = new SessionTelemetryService(clock);
        service.RecordOutput("a", "hello"u8);
        foreach (var seconds in new[] { 0.0, 0.125, 5, 15, 22.5, 30, 60 })
        {
            clock.Now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero).AddSeconds(seconds);
            service.RecordOutput("a", "⠁"u8);
            Assert.Equal(Math.Max(0, 1 - seconds / 30), service.GetSnapshot("a").CurrentHeat, 6);
            Assert.Equal(service.GetSnapshot("a").CurrentHeat, service.GetActivity("a", 120, 10).CurrentHeat, 6);
        }
    }

    [Fact]
    public void SynchronizedRepaintIsColdButChangedTextAndRepeatedLogLinesAreHot()
    {
        var parser = new TerminalTextActivityParser();
        Assert.True(parser.CountTextUnits("\u001b[?2026h\u001b[HHello\u001b[?2026l"u8, 80, 24) > 0);
        Assert.Equal(0, parser.CountTextUnits("\u001b[?2026h\u001b[2J\u001b[HHello\u001b[?2026l"u8, 80, 24));
        Assert.True(parser.CountTextUnits("\u001b[?2026h\u001b[HWorld\u001b[?2026l"u8, 80, 24) > 0);
        Assert.True(parser.CountTextUnits("\r\nSame log line\r\n"u8, 80, 24) > 0);
        Assert.True(parser.CountTextUnits("Same log line\r\n"u8, 80, 24) > 0);
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
