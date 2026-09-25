using System.Buffers.Text;
using System.Runtime.InteropServices;

namespace Ai.Tlbx.MidTerm.TtyHost;

/// <summary>
/// Prevents delayed or replayed browser answers to terminal color queries from
/// reaching an application after it has already stopped waiting for them.
/// </summary>
internal sealed class TerminalColorQueryGuard(TimeProvider? timeProvider = null)
{
    // Windows TUIs can stop reading OSC replies after a 100 ms startup probe
    // (Codex 0.154.0). Leave time for the ConPTY input hop: a reply arriving
    // later must not become editable text after the application's probe ends.
    private static readonly TimeSpan MaximumResponseAge = OperatingSystem.IsWindows()
        ? TimeSpan.FromMilliseconds(50)
        : TimeSpan.FromSeconds(1);
    private const int MaximumOscBytes = 1024;
    private const byte Escape = 0x1b;
    private const byte Bell = 0x07;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly Dictionary<int, PendingQuery> _queries = [];
    private readonly List<byte> _osc = new(64);
    private bool _capturingOsc;
    private bool _oscEscapePending;
    private bool _escapePending;

    public void ObservePtyOutput(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            if (!_capturingOsc)
            {
                if (_escapePending)
                {
                    _escapePending = false;
                    if (value == (byte)']')
                    {
                        _capturingOsc = true;
                        _osc.Clear();
                        continue;
                    }
                }

                _escapePending = value == Escape;
                continue;
            }

            if (_oscEscapePending)
            {
                _oscEscapePending = false;
                if (value == (byte)'\\')
                {
                    RecordQuery(CollectionsMarshal.AsSpan(_osc));
                    ResetOscCapture();
                    continue;
                }

                _osc.Add(Escape);
            }

            if (value == Bell)
            {
                RecordQuery(CollectionsMarshal.AsSpan(_osc));
                ResetOscCapture();
                continue;
            }

            if (value == Escape)
            {
                _oscEscapePending = true;
                continue;
            }

            _osc.Add(value);
            if (_osc.Count > MaximumOscBytes)
            {
                ResetOscCapture();
            }
        }
    }

    /// <summary>
    /// Removes terminal-generated color responses which no longer have a fresh,
    /// unanswered application query. A browser may concatenate several OSC
    /// replies with ordinary input in one transport frame, so filtering must be
    /// sequence-aware rather than deciding on the frame as a whole.
    /// </summary>
    public byte[]? FilterClientInput(ReadOnlySpan<byte> data)
    {
        List<byte>? filtered = null;
        var copyStart = 0;
        for (var offset = 0; offset < data.Length;)
        {
            if (!TryParseColorResponse(data[offset..], out var colorIndex, out var responseLength))
            {
                offset++;
                continue;
            }

            if (ShouldSuppressColorResponse(colorIndex))
            {
                filtered ??= new List<byte>(data.Length);
                filtered.AddRange(data[copyStart..offset].ToArray());
                copyStart = offset + responseLength;
            }

            offset += responseLength;
        }

        if (filtered is null)
        {
            return null;
        }

        filtered.AddRange(data[copyStart..].ToArray());
        return filtered.ToArray();
    }

    private bool ShouldSuppressColorResponse(int colorIndex)
    {
        lock (_lock)
        {
            if (!_queries.TryGetValue(colorIndex, out var query))
            {
                return true;
            }

            if (query.Answered)
            {
                return true;
            }

            var stale = _timeProvider.GetElapsedTime(query.Timestamp) > MaximumResponseAge;
            _queries[colorIndex] = query with { Answered = true };
            return stale;
        }
    }

    private void RecordQuery(ReadOnlySpan<byte> payload)
    {
        var separator = payload.IndexOf((byte)';');
        if (separator <= 0)
        {
            return;
        }

        if (!Utf8Parser.TryParse(payload[..separator], out int colorIndex, out var consumed) ||
            consumed != separator ||
            colorIndex is < 10 or > 12)
        {
            return;
        }

        var values = payload[(separator + 1)..];
        var timestamp = _timeProvider.GetTimestamp();
        lock (_lock)
        {
            while (colorIndex <= 12)
            {
                var nextSeparator = values.IndexOf((byte)';');
                var value = nextSeparator < 0 ? values : values[..nextSeparator];
                if (value.SequenceEqual("?"u8))
                {
                    _queries[colorIndex] = new PendingQuery(timestamp, Answered: false);
                }

                if (nextSeparator < 0)
                {
                    break;
                }

                colorIndex++;
                values = values[(nextSeparator + 1)..];
            }
        }
    }

    private static bool TryParseColorResponse(
        ReadOnlySpan<byte> data,
        out int colorIndex,
        out int responseLength)
    {
        colorIndex = 0;
        responseLength = 0;
        if (data.Length < 2 || data[0] != Escape || data[1] != (byte)']')
        {
            return false;
        }

        var terminatorIndex = -1;
        var terminatorLength = 0;
        var limit = Math.Min(data.Length, MaximumOscBytes + 2);
        for (var i = 2; i < limit; i++)
        {
            if (data[i] == Bell)
            {
                terminatorIndex = i;
                terminatorLength = 1;
                break;
            }

            if (data[i] == Escape && i + 1 < limit && data[i + 1] == (byte)'\\')
            {
                terminatorIndex = i;
                terminatorLength = 2;
                break;
            }
        }

        if (terminatorIndex < 0)
        {
            return false;
        }

        var payload = data[2..terminatorIndex];
        var separator = payload.IndexOf((byte)';');
        if (separator <= 0 ||
            !Utf8Parser.TryParse(payload[..separator], out colorIndex, out var consumed) ||
            consumed != separator ||
            colorIndex is < 10 or > 12)
        {
            return false;
        }

        var color = payload[(separator + 1)..];
        if (!color.StartsWith("rgb:"u8))
        {
            return false;
        }

        var components = color[4..];
        var firstSlash = components.IndexOf((byte)'/');
        var secondSlash = firstSlash < 0 ? -1 : components[(firstSlash + 1)..].IndexOf((byte)'/');
        if (firstSlash <= 0 || secondSlash <= 0)
        {
            return false;
        }

        secondSlash += firstSlash + 1;
        var valid = IsHexComponent(components[..firstSlash]) &&
            IsHexComponent(components[(firstSlash + 1)..secondSlash]) &&
            IsHexComponent(components[(secondSlash + 1)..]);
        if (valid)
        {
            responseLength = terminatorIndex + terminatorLength;
        }

        return valid;
    }

    private static bool IsHexComponent(ReadOnlySpan<byte> component)
    {
        return component.Length is >= 1 and <= 4 && component.IndexOfAnyExcept(
            "0123456789abcdefABCDEF"u8) < 0;
    }

    private void ResetOscCapture()
    {
        _capturingOsc = false;
        _oscEscapePending = false;
        _escapePending = false;
        _osc.Clear();
    }

    private readonly record struct PendingQuery(long Timestamp, bool Answered);
}
