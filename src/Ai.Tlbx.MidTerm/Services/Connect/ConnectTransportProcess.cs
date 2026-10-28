using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Ai.Tlbx.MidTerm.Services.Connect;

/// <summary>Owns one transport-only Pion child. Authentication and proxy work remain in this process.</summary>
internal sealed class ConnectTransportProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _lifetime;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _stdout;
    private readonly Task _stderr;
    private readonly FileStream _errorLog;
    private bool _disposed;
    internal int ProcessId => _process.Id;

    private ConnectTransportProcess(Process process, string errorLog, Func<ConnectSignal, Task> onMessage, CancellationToken ct)
    {
        _process = process;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _errorLog = new FileStream(errorLog, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        _stdout = ReadOutputAsync(onMessage);
        _stderr = process.StandardError.BaseStream.CopyToAsync(_errorLog, _lifetime.Token);
    }

    public static async Task<ConnectTransportProcess> StartAsync(string connectionId, string settingsDirectory, string logDirectory,
        ConnectSignal connect, Func<ConnectSignal, Task> onMessage, CancellationToken ct)
    {
        var executable = ConnectTransportLocator.Resolve(settingsDirectory);
        Directory.CreateDirectory(logDirectory);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            }
        };
        if (!process.Start())
        {
            process.Dispose();
            throw new IOException("Could not start the tlbx Connect transport.");
        }
        ConnectTransportProcess owner;
        try
        {
            owner = new ConnectTransportProcess(process, Path.Combine(logDirectory,
                DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture) + "-" + connectionId + ".stderr.log"), onMessage, ct);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.Dispose();
            throw;
        }
        try
        {
            await owner.SendAsync(new ConnectSignal
            {
                Type = "init", Version = 1, IceServers = connect.IceServers ?? [], RelayOnly = connect.RelayOnly
            }, ct);
            await owner._ready.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            return owner;
        }
        catch
        {
            await owner.DisposeAsync();
            throw;
        }
    }

    public async Task SendAsync(ConnectSignal message, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var line = JsonSerializer.Serialize(message, ConnectJsonContext.Default.ConnectSignal);
        if (Encoding.UTF8.GetByteCount(line) > 131072) throw new InvalidDataException("Connect transport message is too large.");
        await _writer.WaitAsync(ct);
        try
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct);
            await _process.StandardInput.FlushAsync(ct);
        }
        finally { _writer.Release(); }
    }

    private async Task ReadOutputAsync(Func<ConnectSignal, Task> onMessage)
    {
        try
        {
            await foreach (var line in ReadLinesAsync(_process.StandardOutput, _lifetime.Token))
            {
                var message = JsonSerializer.Deserialize(line, ConnectJsonContext.Default.ConnectSignal)
                    ?? throw new InvalidDataException("Empty Connect transport message.");
                if (message.Type == "ready" && message.Version == 1) _ready.TrySetResult();
                else await onMessage(message);
            }
            if (!_lifetime.IsCancellationRequested)
                await onMessage(new ConnectSignal { Type = "error", Code = "transport_closed", Fatal = true });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ObjectDisposedException
            or FormatException or OverflowException)
        {
            _ready.TrySetException(new IOException("Connect transport ended before it was ready."));
            if (!_lifetime.IsCancellationRequested)
                await onMessage(new ConnectSignal { Type = "error", Code = "transport_failed", Fatal = true });
        }
        finally { _ready.TrySetException(new IOException("Connect transport is no longer running.")); }
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(StreamReader reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new char[8192];
        var line = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var character = buffer[i];
                if (character == '\n')
                {
                    yield return line.ToString().TrimEnd('\r');
                    line.Clear();
                }
                else
                {
                    if (line.Length >= 131072) throw new InvalidDataException("Connect transport line exceeds its limit.");
                    line.Append(character);
                }
            }
        }
        if (line.Length != 0) throw new InvalidDataException("Incomplete Connect transport message.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        try { _process.StandardInput.Close(); } catch (IOException) { }
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await _process.WaitForExitAsync(CancellationToken.None);
        }
        try
        {
            try { await Task.WhenAll(_stdout, _stderr); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException
                or System.Net.WebSockets.WebSocketException) { }
        }
        finally
        {
            await _errorLog.DisposeAsync();
            _process.Dispose();
            _lifetime.Dispose();
            _writer.Dispose();
        }
    }
}
