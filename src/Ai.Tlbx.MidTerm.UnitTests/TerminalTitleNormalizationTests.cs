using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TerminalTitleNormalizationTests
{
    [Theory]
    [InlineData(@"C:\WINDOWS\system32\cmd.exe")]
    [InlineData("cmd.exe")]
    [InlineData(@"C:\WINDOWS\system32\conhost.exe")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe")]
    [InlineData(@"C:\Program Files\Git\usr\bin\bash.exe")]
    [InlineData("/usr/bin/bash")]
    public void BareShellAndConsoleWrapperTitlesNormalizeToNull(string rawTitle)
    {
        var session = new SessionInfo { Id = "s1", ShellType = "Pwsh" };
        Assert.Null(TtyHostSessionManager.NormalizeTerminalTitle(session, rawTitle));
    }

    [Theory]
    [InlineData("Claude Code")]
    [InlineData("vim README.md")]
    public void MeaningfulApplicationTitlesArePreserved(string rawTitle)
    {
        var session = new SessionInfo { Id = "s1", ShellType = "Pwsh" };
        Assert.Equal(rawTitle, TtyHostSessionManager.NormalizeTerminalTitle(session, rawTitle));
    }
}
