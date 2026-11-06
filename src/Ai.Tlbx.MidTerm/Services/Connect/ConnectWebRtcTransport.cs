using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Logging;

namespace Ai.Tlbx.MidTerm.Services.Connect;

/// <summary>One peer's private IPC proxy. The web server never loads WebRTC native code.</summary>
internal sealed class ConnectWebRtcTransport : IAsyncDisposable
{
    [SuppressMessage("Usage", "IDISP002:Dispose member", Justification = "ReleaseAsync disposes the process after its pipes drain.")]
    [SuppressMessage("Usage", "IDISP006:Implement IDisposable", Justification = "Worker shutdown and pipe draining are asynchronous.")]
    private readonly Process _process;
    [SuppressMessage("Usage", "IDISP002:Dispose member", Justification = "ReleaseAsync disposes this member after asynchronous users drain.")]
    [SuppressMessage("Usage", "IDISP006:Implement IDisposable", Justification = "Worker disposal must asynchronously drain pipes and process exit.")]
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "ReleaseAsync disposes the lifetime after readers and writers finish.")]
    private readonly CancellationTokenSource _lifetime;
    [SuppressMessage("Usage", "IDISP002:Dispose member", Justification = "ReleaseAsync disposes this gate after asynchronous writers drain.")]
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "ReleaseAsync disposes the gate after pending writers finish.")]
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<ConnectSignal, Task> _onSignal;
    private readonly Func<ReadOnlyMemory<byte>, Task> _onFrame;
    private readonly Func<Task> _onOpen;
    private readonly Action _onFailure;
    private readonly string _logPath;
    private readonly Task _reader, _stderr;
    private readonly Lock _disposeGate = new();
    private Task? _disposeTask;
    private int _closed, _failed;

    internal int WorkerProcessId => _process.Id;
    internal static string[] NativeServerUrls(ConnectIceServer[] servers, bool expanded = false) => ConnectNativePeer.NativeServerUrls(servers, expanded);

    private ConnectWebRtcTransport(Process process, string logPath, Func<ConnectSignal, Task> onSignal,
        Func<ReadOnlyMemory<byte>, Task> onFrame, Func<Task> onOpen, Action onFailure, CancellationToken ct)
    {
        _process = process; _logPath = logPath;
        _onSignal = onSignal; _onFrame = onFrame; _onOpen = onOpen; _onFailure = onFailure;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _stderr = CaptureErrorsAsync();
        _reader = ReadAsync();
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Successful initialization transfers ownership to caller; finally disposes unsuccessful instances.")]
    [SuppressMessage("Usage", "IDISP001:Dispose created", Justification = "Successful initialization transfers process ownership to the transport and caller.")]
    [SuppressMessage("Usage", "IDISP003:Dispose previous before re-assigning", Justification = "Null marks ownership transfer to the returned instance.")]
    public static async Task<ConnectWebRtcTransport> CreateAsync(string settingsDirectory, ConnectSignal configuration,
        Func<ConnectSignal, Task> onSignal, Func<ReadOnlyMemory<byte>, Task> onFrame, Func<Task> onOpen,
        Action onFailure, CancellationToken ct)
    {
        _ = NativeServerUrls(configuration.IceServers ?? []);
        var logs = Path.Combine(settingsDirectory, "logs", "link-workers");
        FileRecoveryStorage.EnsurePrivate(logs);
        var logPath = Path.Combine(logs, "worker-" + Guid.NewGuid().ToString("N") + ".log");
        ConnectWebRtcTransport? transport = null;
        Process? process = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            process = Process.Start(StartInfo()) ?? throw new IOException("Could not start Link worker.");
            transport = new ConnectWebRtcTransport(process, logPath, onSignal, onFrame, onOpen, onFailure, ct);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var init = new ConnectWorkerInitialization(settingsDirectory, configuration);
            await transport.WriteAsync(ConnectWorkerMessage.Initialize,
                JsonSerializer.SerializeToUtf8Bytes(init, ConnectJsonContext.Default.ConnectWorkerInitialization), deadline.Token).ConfigureAwait(false);
            await transport._ready.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            var result = transport;
            transport = null; process = null;
            return result;
        }
        catch (Exception ex)
        {
            throw new IOException("Link worker could not be initialized.", ex);
        }
        finally
        {
            if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
            else if (process is not null)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                process.Dispose();
            }
        }
    }

    internal static ProcessStartInfo StartInfo()
    {
        var isOwnEntry = Assembly.GetEntryAssembly() == typeof(Program).Assembly;
        var host = isOwnEntry ? Environment.ProcessPath! : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var info = new ProcessStartInfo(host)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (!isOwnEntry) info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "mt.dll"));
        else if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        using var parent = Process.GetCurrentProcess();
        info.ArgumentList.Add("connect-worker");
        info.ArgumentList.Add(parent.Id.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        return info;
    }

    public Task ApplySignalAsync(ConnectSignal signal, CancellationToken ct) => WriteAsync(ConnectWorkerMessage.Signal,
        JsonSerializer.SerializeToUtf8Bytes(signal, ConnectJsonContext.Default.ConnectSignal), ct);
    public Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct) => WriteAsync(ConnectWorkerMessage.Frame, frame, ct);

    private async Task WriteAsync(ConnectWorkerMessage kind, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var entered = false;
        try
        {
            await _writer.WaitAsync(deadline.Token).ConfigureAwait(false); entered = true;
            await ConnectWorkerProtocol.WriteAsync(_process.StandardInput.BaseStream, kind, payload, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!ct.IsCancellationRequested && Volatile.Read(ref _closed) == 0) Fail();
            throw;
        }
        finally { if (entered) _writer.Release(); }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await ConnectWorkerProtocol.ReadAsync(_process.StandardOutput.BaseStream, _lifetime.Token).ConfigureAwait(false) is { } message)
            {
                switch (message.Kind)
                {
                    case ConnectWorkerMessage.Ready: _ready.TrySetResult(); break;
                    case ConnectWorkerMessage.Open: await _onOpen().ConfigureAwait(false); break;
                    case ConnectWorkerMessage.Signal:
                        var signal = JsonSerializer.Deserialize(message.Payload, ConnectJsonContext.Default.ConnectSignal)
                            ?? throw new InvalidDataException("Empty Link worker signal.");
                        await _onSignal(signal).ConfigureAwait(false);
                        break;
                    case ConnectWorkerMessage.Frame: await _onFrame(message.Payload).ConfigureAwait(false); break;
                    default: throw new InvalidDataException("Unexpected Link worker response.");
                }
            }
        }
        catch (Exception) { /* A corrupt or closed worker stream is a peer failure. */ }
        finally { if (Volatile.Read(ref _closed) == 0) Fail(); }
    }

    private async Task CaptureErrorsAsync()
    {
        try
        {
            await using var file = new FileStream(_logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
            var buffer = new byte[4096];
            var saved = 0;
            int count;
            while ((count = await _process.StandardError.BaseStream.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) != 0)
            {
                var keep = Math.Min(count, 65536 - saved);
                if (keep > 0) await file.WriteAsync(buffer.AsMemory(0, keep), CancellationToken.None).ConfigureAwait(false);
                saved += keep; // Continue draining even after the private log reaches its cap.
            }
        }
        catch (Exception) { Fail(); }
    }

    private void Fail()
    {
        _ready.TrySetException(new IOException("Link worker exited or its IPC stream failed."));
        if (Volatile.Read(ref _closed) != 0 || Interlocked.Exchange(ref _failed, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try { _onFailure(); } catch (Exception) { }
            try { await DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
        }, CancellationToken.None);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= Task.Run(DisposeCoreAsync, CancellationToken.None));
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        _lifetime.Cancel();
        try { _process.StandardInput.Close(); } catch (Exception) { }
        try { await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            try { _process.Kill(entireProcessTree: true); } catch (Exception) { }
            try { await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { /* Cleanup must not hold the control plane indefinitely. */ }
        }
        catch (Exception) { /* A peer cleanup failure cannot stop mt. */ }
        Log.Info(() => string.Create(CultureInfo.InvariantCulture, $"[Link] Worker {_process.Id} closed; main server remains running."));
        var workers = Task.WhenAll(_reader, _stderr);
        try { await workers.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false); }
        catch (TimeoutException) { _ = ReleaseAfterWorkersAsync(workers); return; }
        var release = ReleaseAsync();
        try { await release.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false); }
        catch (TimeoutException) { _ = ObserveReleaseAsync(release); }
    }

    private static async Task ObserveReleaseAsync(Task release)
    {
        try { await release.ConfigureAwait(false); } catch (Exception) { }
    }

    private async Task ReleaseAfterWorkersAsync(Task workers)
    {
        try { await workers.ConfigureAwait(false); await ReleaseAsync().ConfigureAwait(false); } catch (Exception) { }
    }
    private async Task ReleaseAsync()
    {
        await _writer.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _writer.Dispose(); _lifetime.Dispose(); _process.Dispose();
        try { if (new FileInfo(_logPath).Length == 0) File.Delete(_logPath); } catch (Exception) { }
    }
}
