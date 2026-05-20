using Ai.Tlbx.MidTerm.Services.Tmux;
using Ai.Tlbx.MidTerm.Services.Tmux.Commands;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public class TmuxCompatibilityTests
{
    [Fact]
    public void WindowsPowerShellShim_EmbedsEndpointUrl()
    {
        var script = TmuxScriptWriter.BuildWindowsPowerShellScript("https://localhost:2100/api/tmux");

        Assert.Contains("https://localhost:2100/api/tmux", script, StringComparison.Ordinal);
        Assert.DoesNotContain("{endpointUrl}", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("extended-keys", "on")]
    [InlineData("extended-keys-format", "csi-u")]
    [InlineData("xterm-keys", "on")]
    [InlineData("allow-passthrough", "on")]
    [InlineData("set-clipboard", "external")]
    [InlineData("focus-events", "on")]
    public void ShowOptions_ReportsCodexDoctorTmuxFeatureOptions(string option, string expected)
    {
        var commands = TmuxCommandParser.Parse(["show-options", "-gqv", option]);
        var result = new ConfigCommands().ShowOptions(commands[0]);

        Assert.True(result.Success);
        Assert.Equal(expected + "\n", result.Output);
    }
}
