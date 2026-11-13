using System.Text;
using Ai.Tlbx.MidTerm.TtyHost;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TmuxPassthroughDecoderTests
{
    [Fact]
    public void WrappedGraphicsRemainExactAcrossEveryReadBoundary()
    {
        const string image = "\x1b_Ga=T,U=1,i=8388608,f=100;UE5H\x1b\\";
        var wire = Encoding.UTF8.GetBytes("before\x1bPtmux;" + image.Replace("\x1b", "\x1b\x1b", StringComparison.Ordinal) + "\x1b\\after Ω");
        var expected = Encoding.UTF8.GetBytes("before" + image + "after Ω");
        for (var split = 0; split <= wire.Length; split++)
        {
            var decoder = new TmuxPassthroughDecoder();
            var first = decoder.Decode(wire.AsMemory(0, split)).ToArray();
            var second = decoder.Decode(wire.AsMemory(split)).ToArray();
            Assert.Equal(expected, first.Concat(second));
        }
    }

    [Fact]
    public void BytewiseFramesAndOtherDcsPreserveBytes()
    {
        var decoder = new TmuxPassthroughDecoder();
        var wire = Encoding.ASCII.GetBytes("\x1bPqNOT-TMUX\x1b\\\x1bPtmux;\x1b\x1b[31mred\x1b\x1b[0m\x1b\\");
        var output = wire.SelectMany(value => decoder.Decode(new byte[] { value }).ToArray()).ToArray();
        Assert.Equal("\x1bPqNOT-TMUX\x1b\\\x1b[31mred\x1b[0m", Encoding.ASCII.GetString(output));
    }
}
