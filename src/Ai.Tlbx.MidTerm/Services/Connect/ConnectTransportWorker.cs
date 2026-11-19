using System.Diagnostics;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading.Channels;

namespace Ai.Tlbx.MidTerm.Services.Connect;

/// <summary>The only production entry point that creates native WebRTC peers.</summary>
internal static class ConnectTransportWorker
{
    internal static bool IsWorkerProcess { get; private set; }
    internal static int Run(string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[0], CultureInfo.InvariantCulture, out var parentId)
            || !long.TryParse(args[1], CultureInfo.InvariantCulture, out var parentStarted)) return 2;
        try { return RunAsync(parentId, parentStarted).GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.Error.WriteLine("Link worker stopped: " + ex); return 1; }
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Native peer is disposed with a bounded asynchronous teardown in finally.")]
    private static async Task<int> RunAsync(int parentId, long parentStarted)
    {
        using var parent = Process.GetProcessById(parentId);
        if (parent.StartTime.ToUniversalTime().Ticks != parentStarted || parent.HasExited) return 2;
        IsWorkerProcess = true;
        using var lifetime = new CancellationTokenSource();
        // A native operation may block the command loop. Parent death still kills
        // this worker through an independent process-handle wait.
        var parentWatch = WatchParentAsync(parent, lifetime.Token);
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        Console.SetOut(Console.Error); // stdout is exclusively the binary IPC stream.
        using var writer = new SemaphoreSlim(1, 1);
        var first = await ConnectWorkerProtocol.ReadAsync(input, lifetime.Token).ConfigureAwait(false);
        if (first is not { Kind: ConnectWorkerMessage.Initialize }) throw new InvalidDataException("Missing Link worker initialization.");
        var init = JsonSerializer.Deserialize(first.Value.Payload, ConnectJsonContext.Default.ConnectWorkerInitialization)
            ?? throw new InvalidDataException("Empty Link worker initialization.");

        async Task SendAsync(ConnectWorkerMessage kind, ReadOnlyMemory<byte> payload)
        {
            await writer.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try { await ConnectWorkerProtocol.WriteAsync(output, kind, payload, lifetime.Token).ConfigureAwait(false); }
            finally { writer.Release(); }
        }

        // The parent keeps one bulk fragment in flight. Its native backpressure
        // must never block the worker's primary pipe reader.
        var bulkFrames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true });
        Task bulkWriter = Task.CompletedTask;
        async Task SendBulkAsync(ConnectNativePeer active)
        {
            try
            {
                await foreach (var frame in bulkFrames.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
                {
                    await active.SendAsync(frame, lifetime.Token, bulk: true).ConfigureAwait(false);
                    await SendAsync(ConnectWorkerMessage.BulkFrameSent, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception)
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }

        ConnectNativePeer? transport = null;
        try
        {
            transport = await ConnectNativePeer.CreateAsync(init.SettingsDirectory, init.Configuration,
                signal => SendAsync(ConnectWorkerMessage.Signal, JsonSerializer.SerializeToUtf8Bytes(signal, ConnectJsonContext.Default.ConnectSignal)),
                frame => SendAsync(ConnectWorkerMessage.Frame, frame),
                () => SendAsync(ConnectWorkerMessage.Open, ReadOnlyMemory<byte>.Empty),
                () => SendAsync(ConnectWorkerMessage.BulkOpen, ReadOnlyMemory<byte>.Empty),
                lifetime.Cancel, lifetime.Token).ConfigureAwait(false);
            bulkWriter = SendBulkAsync(transport);
            await SendAsync(ConnectWorkerMessage.Ready, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
            while (await ConnectWorkerProtocol.ReadAsync(input, lifetime.Token).ConfigureAwait(false) is { } message)
            {
                switch (message.Kind)
                {
                    case ConnectWorkerMessage.Signal:
                        var signal = JsonSerializer.Deserialize(message.Payload, ConnectJsonContext.Default.ConnectSignal)
                            ?? throw new InvalidDataException("Empty Link worker signal.");
                        await transport.ApplySignalAsync(signal, lifetime.Token).ConfigureAwait(false);
                        break;
                    case ConnectWorkerMessage.Frame:
                        await transport.SendAsync(message.Payload, lifetime.Token).ConfigureAwait(false);
                        break;
                    case ConnectWorkerMessage.BulkFrame:
                        if (!bulkFrames.Writer.TryWrite(message.Payload))
                            throw new InvalidDataException("Link worker bulk send exceeded its queue limit.");
                        break;
                    default: throw new InvalidDataException("Unexpected Link worker command.");
                }
            }
            return 0;
        }
        finally
        {
            lifetime.Cancel();
            // Native teardown is allowed to fail or hang only in this worker.
            // The parent also enforces a three-second exit deadline.
            try { if (transport is not null) await transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { KillSelf(); }
            try { await bulkWriter.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false); }
            catch (TimeoutException) { KillSelf(); }
            try { await parentWatch.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private static void KillSelf()
    {
        using var self = Process.GetCurrentProcess();
        self.Kill();
    }

    private static async Task WatchParentAsync(Process parent, CancellationToken ct)
    {
        try
        {
            await parent.WaitForExitAsync(ct).ConfigureAwait(false);
            KillSelf();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
