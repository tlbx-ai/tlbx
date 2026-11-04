using System.Buffers.Binary;
using Ai.Tlbx.MidTerm.Services.Connect;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class ConnectWorkerProtocolTests
{
    [Fact]
    public async Task BinaryFrameRoundTripsWithoutChangingPayload()
    {
        using var stream = new MemoryStream();
        var payload = Enumerable.Range(0, 65536).Select(i => (byte)i).ToArray();
        await ConnectWorkerProtocol.WriteAsync(stream, ConnectWorkerMessage.Frame, payload, CancellationToken.None);
        stream.Position = 0;
        var frame = await ConnectWorkerProtocol.ReadAsync(stream, CancellationToken.None);
        Assert.NotNull(frame);
        Assert.Equal(ConnectWorkerMessage.Frame, frame.Value.Kind);
        Assert.Equal(payload, frame.Value.Payload);
        Assert.Null(await ConnectWorkerProtocol.ReadAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(255, 0)]
    [InlineData(1, -1)]
    [InlineData(1, int.MaxValue)]
    [InlineData(3, 0)]
    [InlineData(3, 65537)]
    [InlineData(4, 1)]
    [InlineData(5, 1)]
    public async Task RejectsInvalidHeadersBeforeReadingPayload(byte kind, int length)
    {
        byte[] header = new byte[5];
        header[0] = kind;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => ConnectWorkerProtocol.ReadAsync(stream, CancellationToken.None));
        Assert.Equal(5, stream.Position);
    }

    [Fact]
    public async Task RejectsTruncatedPayload()
    {
        using var stream = new MemoryStream(new byte[] { 3, 2, 0, 0, 0, 7 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => ConnectWorkerProtocol.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void MainProcessCannotLoadNativeWebRtc()
    {
        Assert.Throws<InvalidOperationException>(() => ConnectNativeLoader.GetApi(Path.GetTempPath()));
    }
}
