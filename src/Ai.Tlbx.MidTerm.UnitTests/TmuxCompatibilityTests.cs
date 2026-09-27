using Ai.Tlbx.MidTerm.Services.Tmux;
using Ai.Tlbx.MidTerm.Services.Tmux.Commands;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public class TmuxCompatibilityTests
{

    [Fact]
    public void Parse_ConsumesGlobalSocketOptionBeforeCommand()
    {
        var commands = TmuxCommandParser.Parse(["-S", @"\\.\pipe\midterm-tmux-14444", "display-message", "-t", "%6", "-p", "#{window_id}"]);

        Assert.Single(commands);
        Assert.Equal("display-message", commands[0].Name);
        Assert.Equal("%6", commands[0].GetFlag("-t"));
        Assert.True(commands[0].HasFlag("-p"));
        Assert.Equal(["#{window_id}"], commands[0].Positional);
    }

    [Fact]
    public void Parse_ConsumesStackedBooleanGlobalOptions()
    {
        var commands = TmuxCommandParser.Parse(["-2", "-u", "-vv", "-L", "default", "list-panes", "-F", "#{pane_id}"]);

        Assert.Single(commands);
        Assert.Equal("list-panes", commands[0].Name);
        Assert.Equal("#{pane_id}", commands[0].GetFlag("-F"));
    }

    [Fact]
    public void Parse_KeepsVersionRequestAsCommand()
    {
        var commands = TmuxCommandParser.Parse(["-V"]);

        Assert.Single(commands);
        Assert.Equal("-V", commands[0].Name);
    }

}
