using System.Text;
using Ai.Tlbx.MidTerm.TtyHost;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class CircularByteBufferProtocolTests
{
    [Fact]
    public void WrappedAsciiReplayPreservesBytesAndAbsoluteSequences()
    {
        using var ring = new CircularByteBuffer(16);
        ring.Write("0123456789abcdef"u8);
        ring.Write("ghij"u8);
        Assert.Equal("456789abcdefghij"u8.ToArray(), ring.ToArray());
        var tail = new byte[5];
        Assert.Equal(5, ring.CopyTailTo(tail, out var sequence));
        Assert.Equal(15UL, sequence);
        Assert.Equal("fghij"u8.ToArray(), tail);
    }

    [Fact]
    public void ReplayStartsAfterUtf8ScalarRatherThanAtContinuationByte()
    {
        using var ring = new CircularByteBuffer(9);
        ring.Write(Encoding.UTF8.GetBytes("AAAAA😀"));
        ring.Write("z"u8);
        var tail = new byte[4];
        var copied = ring.CopyTailTo(tail, out var sequence);
        Assert.Equal(1, copied);
        Assert.Equal(9UL, sequence);
        Assert.Equal("z"u8.ToArray(), tail[..copied]);
    }

    [Fact]
    public void WrappedReplaySkipsPartialCsiSequence()
    {
        using var ring = new CircularByteBuffer(12);
        ring.Write("abc\u001b[31mXYZ"u8);
        ring.Write("1234"u8);
        var tail = new byte[9];
        var copied = ring.CopyTailTo(tail, out var sequence);
        Assert.Equal(8UL, sequence);
        Assert.Equal("XYZ1234"u8.ToArray(), tail[..copied]);
    }

    [Fact]
    public void OversizedFragmentedOscIsDiscardedUntilItsTerminator()
    {
        using var ring = new CircularByteBuffer(8);
        ring.Write("\u001b]0;title"u8);
        Assert.Equal(0, ring.Count);
        ring.Write("!\u0007OK"u8);
        Assert.Equal("OK"u8.ToArray(), ring.ToArray());
        Assert.Equal(11UL, ring.TailPosition);
    }

    [Theory]
    [InlineData(13)]
    [InlineData(31)]
    [InlineData(97)]
    public void EveryFragmentSizePreservesTheSameFinalReplay(int capacity)
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("plain\u001b[31mGrüße Ω 😀\u001b[0m\r\n\u001b]0;title\u0007end", 8)));
        using var complete = new CircularByteBuffer(capacity);
        complete.Write(data);
        for (var fragmentSize = 1; fragmentSize <= 33; fragmentSize++)
        {
            using var fragmented = new CircularByteBuffer(capacity);
            for (var offset = 0; offset < data.Length; offset += fragmentSize)
                fragmented.Write(data.AsSpan(offset, Math.Min(fragmentSize, data.Length - offset)));
            Assert.Equal(complete.ToArray(), fragmented.ToArray());
            Assert.Equal(complete.TailPosition, fragmented.TailPosition);
            Assert.Equal(complete.TotalBytesWritten, fragmented.TotalBytesWritten);
        }
    }
}
