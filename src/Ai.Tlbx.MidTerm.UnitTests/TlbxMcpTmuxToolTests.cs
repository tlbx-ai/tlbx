using System.Text.Json;
using Ai.Tlbx.MidTerm.Services.Mcp;
using Ai.Tlbx.MidTerm.Services.Tmux;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TlbxMcpTmuxToolTests
{
    [Fact]
    public void SplitUsesSameTypedTargetCwdDirectionAndPositionalContractAsLegacyTmux()
    {
        const string path = "Q:\\a folder\\it's current";
        var values = Args("""
            {"targetPane":"session:window.12","horizontal":true,
             "workingDirectory":"Q:\\a folder\\it's current",
             "command":"pwsh","arguments":["-NoProfile","a b",";","$literal","quote'word",""]}
            """);
        var actual = TlbxMcpTmuxTool.Prepare(values, "verified-self");
        var cli = Assert.Single(TmuxCommandParser.Parse(
            ["split-window", "-h", "-t", "session:window.12", "-c", path,
             "pwsh", "-NoProfile", "a b", "$literal", "quote'word", ""]));
        Assert.Equal(cli.Name, actual.Name);
        Assert.Equal(cli.Flags.OrderBy(p => p.Key, StringComparer.Ordinal), actual.Flags.OrderBy(p => p.Key, StringComparer.Ordinal));
        // MCP doesn't parse command arguments as tmux chaining. Every supplied
        // value remains one positional argument for existing tmux ShellQuote.
        Assert.Equal(new[] { "pwsh", "-NoProfile", "a b", ";", "$literal", "quote'word", "" }, actual.Positional, StringComparer.Ordinal);
        Assert.True(actual.HasFlag("-h"));
        Assert.Equal(path, actual.GetFlag("-c"));
    }

    [Fact]
    public void LegacySplitParserStopsAtCommandAndHonorsExplicitBoundaryForAlias()
    {
        var command = Assert.Single(TmuxCommandParser.Parse(["splitw", "-h", "-c", "a folder", "--", "-executable", "-t", "literal-target"]));
        Assert.True(command.HasFlag("-h"));
        Assert.Equal("a folder", command.GetFlag("-c"));
        Assert.False(command.HasFlag("-t"));
        Assert.Equal(new[] { "-executable", "-t", "literal-target" }, command.Positional, StringComparer.Ordinal);
        var send = Assert.Single(TmuxCommandParser.Parse(["send-keys", "word", "-t", "%9", "C-c"]));
        Assert.Equal("%9", send.GetFlag("-t"));
        Assert.Equal(new[] { "word", "C-c" }, send.Positional, StringComparer.Ordinal);
    }

    [Fact]
    public void SelfIsExplicitTargetAndFocusFallbackIsNeverUsed()
    {
        var command = TlbxMcpTmuxTool.Prepare(Args("{}"), "verified-self");
        Assert.Equal("verified-self", command.GetFlag("-t"));
        Assert.False(command.HasFlag("-h"));
        Assert.False(command.HasFlag("-c"));
        Assert.Empty(command.Positional);
        Assert.Throws<ArgumentException>(() => TlbxMcpTmuxTool.Prepare(Args("{}"), null));
        Assert.Equal("explicit", TlbxMcpTmuxTool.Prepare(Args("""{"targetSessionId":"explicit"}"""), null).GetFlag("-t"));
    }

    [Fact]
    public void OneStringCommandIsPreservedByteForByteForExistingTerminalInput()
    {
        const string text = "echo \"literal; $x\" && next --arg='a b'";
        var values = Args("{}");
        values["command"] = JsonSerializer.SerializeToElement(text);
        var command = TlbxMcpTmuxTool.Prepare(values, "self");
        Assert.Equal(text, Assert.Single(command.Positional));
    }

    [Theory]
    [InlineData("{\"targetSessionId\":\"x\",\"targetPane\":\"%1\"}")]
    [InlineData("{\"horizontal\":\"true\"}")]
    [InlineData("{\"workingDirectory\":null}")]
    [InlineData("{\"arguments\":[\"arg\"]}")]
    [InlineData("{\"command\":\"exe\",\"arguments\":[123]}")]
    [InlineData("{\"sessionId\":\"unverified-self\"}")]
    public void InvalidOrAmbiguousInputCannotReachSessionCreation(string json) =>
        Assert.Throws<ArgumentException>(() => TlbxMcpTmuxTool.Prepare(Args(json), "self"));

    private static Dictionary<string, JsonElement> Args(string value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(value)!;
}
