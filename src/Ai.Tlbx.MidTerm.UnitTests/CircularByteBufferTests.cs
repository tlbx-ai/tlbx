using System.Text;
using Ai.Tlbx.MidTerm.TtyHost;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class CircularByteBufferTests
{
    [Fact]
    public void TryCopySince_RejectsCursorsOutsideRetainedSequenceRange()
    {
        using var buffer = new CircularByteBuffer(4);
        buffer.Write(Encoding.ASCII.GetBytes("abcdef"));
        Span<byte> destination = stackalloc byte[4];

        Assert.False(buffer.TryCopySince(1, destination, out var beforeTailBytes));
        Assert.Equal(0, beforeTailBytes);
        Assert.False(buffer.TryCopySince(7, destination, out var afterHeadBytes));
        Assert.Equal(0, afterHeadBytes);
    }

    [Fact]
    public void TryCopySince_ReturnsTheContiguousSuffixAtAnExactCursor()
    {
        using var buffer = new CircularByteBuffer(8);
        buffer.Write(Encoding.ASCII.GetBytes("abcdef"));
        Span<byte> destination = stackalloc byte[4];

        Assert.True(buffer.TryCopySince(3, destination, out var bytesCopied));
        Assert.Equal(3, bytesCopied);
        Assert.Equal("def", Encoding.ASCII.GetString(destination[..bytesCopied]));
    }
}
