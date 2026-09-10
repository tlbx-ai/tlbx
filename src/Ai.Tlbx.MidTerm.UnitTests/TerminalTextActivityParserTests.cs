using System.Text;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TerminalTextActivityParserTests
{
    [Theory]
    [InlineData("\u001b[?2026h\u001b[38;2;120;130;140m⠁ ⠂ ⠄ ⠈ ⠐ ⠠ ⡀ ⢀\u001b[?2026l", 0)]
    [InlineData("\r\n\t ─│╭╯█ ◐◓◑◒ 😀", 0)]
    [InlineData("\u001b[31mhello\u001b[0m\r\n世界", 7)]
    [InlineData("Grüße 42! e\u0301 𐐀", 11)]
    [InlineData("\u001b]0;window title\u0007\u001bPignored payload\u001b\\\u001b(B", 0)]
    [InlineData("⠁\u001b[10;20HFehler: 42\u001b[0m⠂", 9)]
    public void TextCount_IsIndependentOfChunkBoundaries(string output, int expected)
    {
        var bytes = Encoding.UTF8.GetBytes(output);
        for (var split = 0; split <= bytes.Length; split++)
        {
            var parser = new TerminalTextActivityParser();
            Assert.Equal(expected, parser.CountTextUnits(bytes.AsSpan(0, split))
                + parser.CountTextUnits(bytes.AsSpan(split)));
        }
        var bytewise = new TerminalTextActivityParser();
        Assert.Equal(expected, bytes.Sum(value => bytewise.CountTextUnits(new[] { value })));
    }

    [Fact]
    public void Telemetry_KeepsRawBytesWhileGraphicsStayCold()
    {
        var service = new SessionTelemetryService();
        var output = Encoding.UTF8.GetBytes("\u001b[?2026h\u001b[38;2;123;45;67m⠁ ⠂\u001b[?2026l");
        foreach (var value in output) service.RecordOutput("sparkle", new[] { value });
        var snapshot = service.GetSnapshot("sparkle");
        Assert.Equal(output.Length, snapshot.TotalOutputBytes);
        Assert.NotNull(snapshot.LastOutputAt);
        Assert.Equal(0, snapshot.CurrentHeat);
        Assert.All(service.GetActivity("sparkle", 30, 10).Heatmap, sample => Assert.Equal(0, sample.Heat));
        service.RecordOutput("sparkle", "Antwort."u8);
        Assert.Contains(service.GetActivity("sparkle", 30, 10).Heatmap, sample => sample.Heat == 1);
        service.RecordOutput("other", "\u001b["u8);
        service.RecordOutput("sparkle", "Text"u8);
        Assert.Equal(0, service.GetSnapshot("other").CurrentHeat);
    }
}
