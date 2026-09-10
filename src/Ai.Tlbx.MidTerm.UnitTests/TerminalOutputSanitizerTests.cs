using System.Text;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TerminalOutputSanitizerTests
{
    [Fact]
    public void StripEscapeSequences_RemovesAnsiCodesAndControlBytes()
    {
        var text = "\u001b[31merror\u001b[0m\u0007\r\nok";

        var result = TerminalOutputSanitizer.StripEscapeSequences(text);

        Assert.Equal("error\nok", result);
    }

    [Fact]
    public void StripEscapeSequences_CarriageReturnKeepsLatestRenderedText()
    {
        var text = "Workin\rWorking\nDone";

        var result = TerminalOutputSanitizer.StripEscapeSequences(text);

        Assert.Equal("Working\nDone", result);
    }

    [Fact]
    public void StripEscapeSequences_RemovesWholeOscPayloads()
    {
        var text = "\u001b]7;file://BRAIN5700/Q:/repos/MidtermJpa\u0007PS Q:\\repos\\MidtermJpa>\u001b]0;C:\\Program Files\\PowerShell\\7\\pwsh.exe\u0007codex --yolo";

        var result = TerminalOutputSanitizer.StripEscapeSequences(text);

        Assert.Equal("PS Q:\\repos\\MidtermJpa>codex --yolo", result);
    }

    [Fact]
    public void TailLines_ReturnsLastRequestedLines()
    {
        var result = TerminalOutputSanitizer.TailLines("a\nb\nc\nd", 2, out var totalLines, out var returnedLines);

        Assert.Equal("c\nd", result);
        Assert.Equal(4, totalLines);
        Assert.Equal(2, returnedLines);
    }

    [Fact]
    public void TailLines_CollapsesLargeBlankRuns()
    {
        var result = TerminalOutputSanitizer.TailLines("a\n\n\n\nb\n\n\nc", 10, out var totalLines, out var returnedLines);

        Assert.Equal("a\n\nb\n\nc", result);
        Assert.Equal(5, totalLines);
        Assert.Equal(5, returnedLines);
    }

    [Fact]
    public void CountBellEvents_IgnoresOscTerminators()
    {
        ReadOnlySpan<byte> data =
        [
            0x1B, 0x5D, (byte)'0', (byte)';', (byte)'t', (byte)'i', (byte)'t', (byte)'l', (byte)'e', 0x07,
            0x07,
            0x1B, 0x5D, (byte)'7', (byte)';', (byte)'/', (byte)'t', (byte)'m', (byte)'p', 0x1B, 0x5C
        ];

        var result = TerminalOutputSanitizer.CountBellEvents(data);

        Assert.Equal(1, result);
    }

}
