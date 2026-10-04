using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class CodexResumeLaunchTests
{
    [Theory]
    [InlineData("codex --yolo", "codex --yolo resume thread123")]
    [InlineData("codex --no-daemon --yolo", "codex --no-daemon --yolo resume thread123")]
    [InlineData("node \"C:\\Program Files\\codex.js\" --model custom", "node \"C:\\Program Files\\codex.js\" --model custom resume thread123")]
    [InlineData("  & 'C:\\Tools\\codex.exe' --remote ws://localhost:9000  ", "& 'C:\\Tools\\codex.exe' --remote ws://localhost:9000 resume thread123")]
    [InlineData(null, "codex --yolo resume thread123")]
    [InlineData("   ", "codex --yolo resume thread123")]
    public void ResumeRetainsSuppliedFlagsAndQuoting(string? launchCommand, string expected)
    {
        Assert.Equal(expected, SessionCodexHandoffService.BuildResumeLaunchCommand(launchCommand, "thread123"));
    }
}
