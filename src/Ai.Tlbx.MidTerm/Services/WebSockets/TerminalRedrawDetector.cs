namespace Ai.Tlbx.MidTerm.Services.WebSockets;

/// <summary>Recognizes a conservative full redraw inside a completed synchronized frame.</summary>
internal static class TerminalRedrawDetector
{
    internal static bool ContainsCompleteRedraw(ReadOnlySpan<byte> data, ulong sequenceStart, ulong afterSequence)
    {
        var synchronized = false;
        var reset = false;
        var clear = false;
        var home = false;
        for (var i = 0; i + 1 < data.Length; i++)
        {
            if (data[i] != 0x1b) continue;
            var escapeStart = i;
            var next = data[++i];
            if (next is (byte)']' or (byte)'P' or (byte)'X' or (byte)'^' or (byte)'_')
            {
                // Do not mistake control-string contents (including Kitty payloads) for CSI.
                var osc = next == (byte)']';
                while (++i < data.Length)
                {
                    if (osc && data[i] == 7) break;
                    if (data[i] == 0x1b && i + 1 < data.Length && data[i + 1] == (byte)'\\')
                    {
                        i++;
                        break;
                    }
                }
                continue;
            }
            if (next != (byte)'[') continue;
            var parameterStart = i + 1;
            while (++i < data.Length && data[i] is >= 0x20 and <= 0x3f) { }
            if (i >= data.Length) break;
            var parameters = data[parameterStart..i];
            var command = data[i];
            if (parameters.SequenceEqual("?2026"u8))
            {
                if (command == (byte)'h')
                {
                    synchronized = sequenceStart + (ulong)escapeStart >= afterSequence;
                    reset = clear = home = false;
                }
                else if (command == (byte)'l')
                {
                    if (synchronized && reset && clear && home) return true;
                    synchronized = false;
                }
                continue;
            }
            if (!synchronized) continue;
            if (command == (byte)'m' && (parameters.IsEmpty || parameters.SequenceEqual("0"u8))) reset = true;
            if (command == (byte)'J' && parameters.SequenceEqual("2"u8) && reset) clear = true;
            if (command is (byte)'H' or (byte)'f'
                && (parameters.IsEmpty || parameters.SequenceEqual("1;1"u8) || parameters.SequenceEqual("1"u8))) home = true;
        }
        return false;
    }
}
