using System.Text;

namespace Ai.Tlbx.MidTerm.Common.Process;

/// <summary>Bounded stdio for short-lived helpers, including a concurrently drained error tail.</summary>
public sealed class BoundedProcessText(TextReader reader, int maxCharacters)
{
    private readonly char[] _buffer = new char[4096];
    private int _offset;
    private int _count;
    private int _remaining = maxCharacters;

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (_offset == _count)
            {
                // Some redirected anonymous-pipe readers do not interrupt an active
                // OS read on cancellation. The owner then kills its process tree,
                // which completes that read before disposing the reader.
                _count = await reader.ReadAsync(_buffer.AsMemory(), ct).AsTask().WaitAsync(ct).ConfigureAwait(false);
                _offset = 0;
                _remaining -= _count;
                if (_remaining < 0) throw new InvalidDataException("Helper stdout exceeded its character limit.");
                if (_count == 0) return line.Length == 0 ? null : line.ToString();
            }
            var end = Array.IndexOf(_buffer, '\n', _offset, _count - _offset);
            var length = (end < 0 ? _count : end) - _offset;
            line.Append(_buffer, _offset, length);
            _offset += length;
            if (end < 0) continue;
            _offset++;
            return line.ToString().TrimEnd('\r');
        }
    }

    public static async Task<string> DrainTailAsync(TextReader reader, CancellationToken ct)
    {
        var buffer = new char[4096];
        var tail = new StringBuilder();
        var remaining = 1024 * 1024;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).AsTask().WaitAsync(ct).ConfigureAwait(false)) > 0)
        {
            remaining -= count;
            if (remaining < 0) throw new InvalidDataException("Helper stderr exceeded its character limit.");
            tail.Append(buffer, 0, count);
            if (tail.Length > 4096) tail.Remove(0, tail.Length - 4096);
        }
        return tail.ToString();
    }

    public static async Task StopAsync(System.Diagnostics.Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { }
    }
}
