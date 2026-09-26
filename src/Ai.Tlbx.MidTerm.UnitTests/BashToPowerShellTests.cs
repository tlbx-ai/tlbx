using Ai.Tlbx.MidTerm.Services.Tmux.Commands;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public class BashTranslationTests
{
    [Fact]
    public void PowerShell_ClaudeCodeAgentCommand()
    {
        var bash = "cd 'Q:\\repos\\MidTerm' && CLAUDECODE=1 CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1 'C:\\Users\\johan\\.local\\bin\\claude.exe' --agent-id uncle-bob@code-review --model claude-opus-4-6";
        var result = IoCommands.TranslateForPowerShell(bash);

        Assert.Equal(
            "cd 'Q:\\repos\\MidTerm'; $env:CLAUDECODE='1'; $env:CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS='1'; & 'C:\\Users\\johan\\.local\\bin\\claude.exe' --agent-id uncle-bob@code-review --model claude-opus-4-6",
            result);
    }

    [Fact]
    public void PowerShell_QuotedCommand_AddsCallOperator()
    {
        var result = IoCommands.TranslateForPowerShell("MY_VAR=123 '/path/to/exe' --flag");
        Assert.Equal("$env:MY_VAR='123'; & '/path/to/exe' --flag", result);
    }

    [Fact]
    public void PowerShell_MultipleChainedSegments()
    {
        var result = IoCommands.TranslateForPowerShell("cd /tmp && FOO=1 BAR=2 cmd && echo done");
        Assert.Equal("cd /tmp; $env:FOO='1'; $env:BAR='2'; cmd; echo done", result);
    }

    [Fact]
    public void Cmd_ClaudeCodeAgentCommand()
    {
        var bash = "cd 'Q:\\repos\\MidTerm' && CLAUDECODE=1 CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1 'C:\\Users\\johan\\.local\\bin\\claude.exe' --agent-id uncle-bob@code-review";
        var result = IoCommands.TranslateForCmd(bash);

        Assert.Equal(
            "cd 'Q:\\repos\\MidTerm'&&set \"CLAUDECODE=1\"&& set \"CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1\"&& 'C:\\Users\\johan\\.local\\bin\\claude.exe' --agent-id uncle-bob@code-review",
            result);
    }

}
