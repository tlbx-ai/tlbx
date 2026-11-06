using System.Buffers.Binary;

namespace Ai.Tlbx.MidTerm.Services.Connect;

internal enum ConnectWorkerMessage : byte { Initialize = 1, Signal, Frame, Ready, Open }
internal sealed record ConnectWorkerInitialization(string SettingsDirectory, ConnectSignal Configuration);

/// <summary>Private inherited pipes only; lengths are checked before allocation.</summary>
internal static class ConnectWorkerProtocol
{
    internal const int MaximumPayload = 256 * 1024;

    internal static async Task WriteAsync(Stream output, ConnectWorkerMessage kind,
        ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        Validate(kind, payload.Length);
        var header = new byte[5];
        header[0] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);
        await output.WriteAsync(header, ct).ConfigureAwait(false);
        await output.WriteAsync(payload, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    internal static async Task<(ConnectWorkerMessage Kind, byte[] Payload)?> ReadAsync(Stream input, CancellationToken ct)
    {
        var header = new byte[5];
        if (await input.ReadAsync(header.AsMemory(0, 1), ct).ConfigureAwait(false) == 0) return null;
        await input.ReadExactlyAsync(header.AsMemory(1), ct).ConfigureAwait(false);
        var kind = (ConnectWorkerMessage)header[0];
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        Validate(kind, length);
        var payload = new byte[length];
        await input.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return (kind, payload);
    }

    private static void Validate(ConnectWorkerMessage kind, int length)
    {
        if (length < 0 || length > MaximumPayload || kind is < ConnectWorkerMessage.Initialize or > ConnectWorkerMessage.Open
            || (kind == ConnectWorkerMessage.Frame && length is <= 0 or > ConnectNativePeer.MaximumFrameSize)
            || (kind is ConnectWorkerMessage.Ready or ConnectWorkerMessage.Open && length != 0))
            throw new InvalidDataException("Invalid Link worker frame.");
    }
}
