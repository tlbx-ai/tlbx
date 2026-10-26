namespace Ai.Tlbx.MidTerm.Services.Sessions;

/// <summary>
/// A replayed DSR query must not inject a cursor report into a running TUI:
/// Windows interprets an unsolicited CPR as F3 (Codex's transcript search).
/// Only terminal responses enter this filter; real keyboard/paste input bypasses it.
/// </summary>
internal sealed class TerminalCursorQueryGuard(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly List<byte> _query = new(5);
    private long? _pending;
    private bool _privateQuery;

    public void ObservePtyOutput(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            foreach (var value in data)
            {
                if (value == 0x1b) _query.Clear();
                if (_query.Count == 0 && value != 0x1b) continue;
                _query.Add(value);
                var bytes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_query);
                if (bytes.SequenceEqual("\u001b[6n"u8) || bytes.SequenceEqual("\u001b[?6n"u8))
                {
                    _pending = _time.GetTimestamp();
                    _privateQuery = bytes.Length == 5;
                    _query.Clear();
                }
                else if (!("\u001b[6n"u8.StartsWith(bytes) || "\u001b[?6n"u8.StartsWith(bytes)))
                {
                    _query.Clear();
                }
            }
        }
    }

    public byte[]? FilterResponse(ReadOnlySpan<byte> data)
    {
        List<byte>? filtered = null;
        var copied = 0;
        lock (_gate)
        {
            for (var offset = 0; offset < data.Length; offset++)
            {
                var length = CursorReportLength(data[offset..], out var privateReport);
                if (length == 0) continue;
                var fresh = _pending is { } timestamp && privateReport == _privateQuery &&
                    _time.GetElapsedTime(timestamp) <= TimeSpan.FromSeconds(1);
                if (privateReport == _privateQuery) _pending = null;
                if (!fresh)
                {
                    filtered ??= new List<byte>(data.Length);
                    filtered.AddRange(data[copied..offset].ToArray());
                    copied = offset + length;
                }
                offset += length - 1;
            }
        }
        if (filtered is null) return null;
        filtered.AddRange(data[copied..].ToArray());
        return filtered.ToArray();
    }

    private static int CursorReportLength(ReadOnlySpan<byte> data, out bool privateReport)
    {
        privateReport = false;
        if (!data.StartsWith("\u001b["u8)) return 0;
        var offset = 2;
        if (offset < data.Length && data[offset] == '?')
        {
            privateReport = true;
            offset++;
        }
        var row = offset;
        while (offset < data.Length && data[offset] is >= (byte)'0' and <= (byte)'9') offset++;
        if (offset == row || offset >= data.Length || data[offset++] != ';') return 0;
        var col = offset;
        while (offset < data.Length && data[offset] is >= (byte)'0' and <= (byte)'9') offset++;
        return offset > col && offset < data.Length && data[offset] == 'R' ? offset + 1 : 0;
    }
}
