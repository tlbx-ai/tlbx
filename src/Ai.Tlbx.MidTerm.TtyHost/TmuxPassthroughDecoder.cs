using System.Buffers;

namespace Ai.Tlbx.MidTerm.TtyHost;

/// <summary>Unwrap tmux DCS frames before capability detection, replay and live output.
/// Only the exact tmux prefix is consumed; other terminal sequences remain byte-exact.
/// Payloads stream without accumulating image data, including across PTY read boundaries.</summary>
internal sealed class TmuxPassthroughDecoder
{
    private static ReadOnlySpan<byte> Prefix => "\x1bPtmux;"u8;
    private int _prefix;
    private bool _inside;
    private bool _escape;

    public ReadOnlyMemory<byte> Decode(ReadOnlyMemory<byte> input)
    {
        if (!_inside && _prefix == 0 && input.Span.IndexOf(Prefix) < 0)
        {
            var pendingPrefix = false;
            for (var length = 1; length < Prefix.Length && length <= input.Length; length++)
                if (input.Span.EndsWith(Prefix[..length])) { pendingPrefix = true; break; }
            if (!pendingPrefix) return input;
        }
        var output = new ArrayBufferWriter<byte>(input.Length + Prefix.Length);
        foreach (var value in input.Span)
        {
            if (_inside)
            {
                if (_escape)
                {
                    _escape = false;
                    if (value == (byte)'\\') { _inside = false; continue; }
                    Emit(output, 0x1b);
                    if (value != 0x1b) Emit(output, value);
                }
                else if (value == 0x1b) _escape = true;
                else Emit(output, value);
                continue;
            }
            if (value == Prefix[_prefix])
            {
                if (++_prefix == Prefix.Length) { _inside = true; _prefix = 0; }
                continue;
            }
            if (_prefix != 0) output.Write(Prefix[.._prefix]);
            _prefix = 0;
            if (value == Prefix[0]) _prefix = 1;
            else Emit(output, value);
        }
        return output.WrittenMemory;
    }

    private static void Emit(ArrayBufferWriter<byte> output, byte value)
    {
        output.GetSpan(1)[0] = value;
        output.Advance(1);
    }
}
