using System.Text.Json;
using Ai.Tlbx.MidTerm.AgentHost;
using Xunit;

namespace Ai.Tlbx.MidTerm.AgentHost.UnitTests;

public sealed class CommandPresentationTests
{
    [Fact]
    public void MultilineCommandSurvivesProviderProjectionAndCanonicalCopy()
    {
        const string command = "pwsh -Command \"@'\r\n$p = 'Grüße 😀'; Get-Content $p\r\n'@\"";
        using var input = JsonDocument.Parse(JsonSerializer.Serialize(new { command }));
        var presentations = new[]
        {
            AppServerControlToolPresentationProjector.FromCodex("commandExecution", "completed", null, input.RootElement),
            AppServerControlToolPresentationProjector.FromClaude("Bash", "in_progress", input.RootElement),
            AppServerControlToolPresentationProjector.FromAcp("tool", "in_progress", "Run", "execute", input.RootElement)
        };
        foreach (var presentation in presentations)
        {
            Assert.Equal("command", presentation.Category);
            Assert.Equal(command, presentation.Subject);
            Assert.Equal(command, AppServerControlToolPresentationProjector.Clone(presentation).Subject);
        }
    }

    [Fact]
    public void LargeCommandsRemainBoundedWithoutTruncatingNormalMultilineScripts()
    {
        var command = "echo start\n" + new string('x', 40_000);
        using var input = JsonDocument.Parse(JsonSerializer.Serialize(new { command }));
        var presentation = AppServerControlToolPresentationProjector.FromClaude("Bash", "in_progress", input.RootElement);
        Assert.Equal(32_768, presentation.Subject!.Length);
        Assert.StartsWith("echo start\n", presentation.Subject, StringComparison.Ordinal);
        Assert.EndsWith("…", presentation.Subject, StringComparison.Ordinal);
    }
}
