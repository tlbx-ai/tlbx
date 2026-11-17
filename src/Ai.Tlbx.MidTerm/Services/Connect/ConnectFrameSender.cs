namespace Ai.Tlbx.MidTerm.Services.Connect;

/// <summary>Lets small replies pass bulk transfers without changing the peer's framing protocol.</summary>
internal sealed class ConnectFrameSender(
    Func<ReadOnlyMemory<byte>, CancellationToken, Task> send,
    CancellationToken lifetime) : IDisposable
{
    internal const int FragmentPayloadSize = 8 * 1024;
    private const int InteractiveMessageSize = 16 * 1024;
    private readonly ConnectFrames _frames = new();
    private readonly SemaphoreSlim _frameGate = new(1, 1);
    private readonly SemaphoreSlim _interactiveGate = new(1, 1);
    private readonly SemaphoreSlim _bulkGate = new(1, 1);

    public async Task SendAsync(byte[] message, CancellationToken ct)
    {
        // Only one message per lane may be partially delivered. Together they stay
        // below the existing receiver's eight-assembly / 64 MiB limits, even when
        // the bulk message reaches the full 32 MiB protocol limit.
        var lane = message.Length <= InteractiveMessageSize ? _interactiveGate : _bulkGate;
        await lane.WaitAsync(ct);
        try
        {
            // IPC pipe writes can complete synchronously. Yield so a bulk reply
            // cannot monopolize the shared incoming-frame dispatcher.
            await Task.Yield();
            var started = false;
            foreach (var frame in _frames.Encode(message, FragmentPayloadSize))
            {
                // Once the first fragment is sent, finish its assembly unless the
                // entire peer ends. A caller timeout must not strand half a message.
                var token = started ? lifetime : ct;
                await _frameGate.WaitAsync(token);
                try
                {
                    await send(frame, token);
                    started = true;
                }
                finally { _frameGate.Release(); }
            }
        }
        finally { lane.Release(); }
    }

    public void Dispose()
    {
        _frameGate.Dispose();
        _interactiveGate.Dispose();
        _bulkGate.Dispose();
    }
}
