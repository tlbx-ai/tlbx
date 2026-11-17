namespace Ai.Tlbx.MidTerm.Services.Connect;

/// <summary>Lets small replies pass bulk transfers without changing the peer's framing protocol.</summary>
internal sealed class ConnectFrameSender(
    Func<ReadOnlyMemory<byte>, CancellationToken, Task> send,
    CancellationToken lifetime) : IDisposable
{
    internal const int FragmentPayloadSize = 1024;
    private const int InteractiveMessageSize = 16 * 1024;
    private const int InteractiveBurst = 8;
    private readonly ConnectFrames _frames = new();
    private readonly SemaphoreSlim _frameGate = new(1, 1);
    private readonly SemaphoreSlim _interactiveGate = new(1, 1);
    private readonly SemaphoreSlim _bulkGate = new(1, 1);
    private readonly Lock _priorityGate = new();
    private TaskCompletionSource? _bulkTurn;
    private int _interactivePending, _interactiveAllowance;

    public async Task SendAsync(byte[] message, CancellationToken ct)
    {
        // Only one message per lane may be partially delivered. Together they stay
        // below the existing receiver's eight-assembly / 64 MiB limits, even when
        // the bulk message reaches the full 32 MiB protocol limit.
        var interactive = message.Length <= InteractiveMessageSize;
        var lane = interactive ? _interactiveGate : _bulkGate;
        if (interactive)
        {
            lock (_priorityGate) { _interactivePending++; }
        }
        var entered = false;
        try
        {
            await lane.WaitAsync(ct);
            entered = true;
            // IPC pipe writes can complete synchronously. Yield so a bulk reply
            // cannot monopolize the shared incoming-frame dispatcher.
            await Task.Yield();
            var started = false;
            foreach (var frame in _frames.Encode(message, FragmentPayloadSize))
            {
                // Once the first fragment is sent, finish its assembly unless the
                // entire peer ends. A caller timeout must not strand half a message.
                var token = started ? lifetime : ct;
                if (!interactive) await YieldToInteractiveAsync().WaitAsync(token);
                await _frameGate.WaitAsync(token);
                try
                {
                    await send(frame, token);
                    started = true;
                }
                finally { _frameGate.Release(); }
            }
        }
        finally
        {
            if (entered) lane.Release();
            if (interactive) FinishInteractive();
        }
    }

    private Task YieldToInteractiveAsync()
    {
        lock (_priorityGate)
        {
            if (_interactivePending == 0) return Task.CompletedTask;
            _interactiveAllowance = InteractiveBurst;
            _bulkTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _bulkTurn.Task;
        }
    }

    private void FinishInteractive()
    {
        lock (_priorityGate)
        {
            _interactivePending--;
            // Drain ready small replies, but let bulk advance after a bounded
            // burst so a continuous interactive stream cannot starve its assembly.
            if (_bulkTurn is not null && (--_interactiveAllowance == 0 || _interactivePending == 0))
            {
                _bulkTurn.TrySetResult();
                _bulkTurn = null;
            }
        }
    }

    public void Dispose()
    {
        _frameGate.Dispose();
        _interactiveGate.Dispose();
        _bulkGate.Dispose();
    }
}
