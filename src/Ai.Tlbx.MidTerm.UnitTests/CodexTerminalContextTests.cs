using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class CodexTerminalContextTests
{
    [Theory]
    [InlineData("codex --yolo", "codex --no-daemon --yolo")]
    [InlineData("codex resume abc", "codex --no-daemon resume abc")]
    [InlineData("node C:\\npm\\codex.js --yolo", "node C:\\npm\\codex.js --no-daemon --yolo")]
    [InlineData("& 'C:\\Program Files\\codex.exe' --yolo", "& 'C:\\Program Files\\codex.exe' --no-daemon --yolo")]
    [InlineData("node.exe \"C:\\Program Files\\codex.js\" --yolo \"keep this prompt\"", "node.exe \"C:\\Program Files\\codex.js\" --no-daemon --yolo \"keep this prompt\"")]
    [InlineData("codex --no-daemon --yolo", "codex --no-daemon --yolo")]
    [InlineData("codex --yolo \"prompt mentions --no-daemon\"", "codex --no-daemon --yolo \"prompt mentions --no-daemon\"")]
    [InlineData("C:\\Tools\\codex.cmd --yolo", "C:\\Tools\\codex.cmd --no-daemon --yolo")]
    [InlineData("claude --dangerously-skip-permissions", "claude --dangerously-skip-permissions")]
    [InlineData("node other.js codex", "node other.js codex")]
    [InlineData("codex app-server", "codex app-server")]
    [InlineData("echo codex --yolo", "echo codex --yolo")]
    public void BookmarkLaunchPreservesArgumentsAndUsesTerminalContext(string original, string expected)
    {
        Assert.Equal(expected, AiCliProfileService.PreserveTerminalContext(original));
        Assert.Equal(expected, AiCliProfileService.PreserveTerminalContext(expected));
    }

    [Fact]
    public void ResumeOfRegisteredLegacyLaunchUsesSameContextBoundary()
    {
        Assert.Equal("codex --no-daemon --yolo resume thread123",
            SessionCodexHandoffService.BuildResumeLaunchCommand("codex --yolo", "thread123"));
        Assert.Equal("codex --no-daemon --yolo", new AiCliProfileService().GetDefaultLaunchCommand("codex"));
    }
}
