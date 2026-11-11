using System.Text;
using Ai.Tlbx.MidTerm.Services.Tmux;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TmuxWireFramingTests
{
    [Theory]
    [InlineData("")]
    [InlineData("\0")]
    [InlineData("\0\0")]
    public void EmptyWireFieldsDoNotBecomeArguments(string wire) =>
        Assert.Empty(TmuxCommandParser.ParseNullDelimitedArgs(Encoding.UTF8.GetBytes(wire)));

    [Fact]
    public void SlicedUtf8WireKeepsUnicodeAndUnterminatedFinalArgument()
    {
        var wire = Encoding.UTF8.GetBytes("Xsend-keys\0-l\0Grüße 😀Y");
        var args = TmuxCommandParser.ParseNullDelimitedArgs(wire.AsSpan(1, wire.Length - 2));
        Assert.Equal(new[] { "send-keys", "-l", "Grüße 😀" }, args, StringComparer.Ordinal);
        wire.AsSpan().Clear();
        Assert.Equal("Grüße 😀", args[2]);
    }

    [Fact]
    public void RepeatedAndTrailingDelimitersAreIgnored() =>
        Assert.Equal(new[] { "send-keys", "-l" }, TmuxCommandParser.ParseNullDelimitedArgs("send-keys\0\0-l\0\0"u8), StringComparer.Ordinal);

    [Fact]
    public void MalformedUtf8RetainsReplacementDecoding() =>
        Assert.Equal(new[] { "\uFFFD", "\uFFFD" }, TmuxCommandParser.ParseNullDelimitedArgs([0xff, 0, 0xc3]), StringComparer.Ordinal);
}
