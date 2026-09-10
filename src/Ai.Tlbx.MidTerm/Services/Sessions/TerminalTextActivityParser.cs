using System.Globalization;
using System.Text;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

/// <summary>Counts text, excluding terminal commands, whitespace and graphical symbols.
/// Retains parsing state across PTY chunks; does not modify the output stream.</summary>
public sealed class TerminalTextActivityParser
{
    private enum State { Ground, Escape, EscapeIntermediate, Csi, String, StringEscape }
    private State _state;
    private readonly byte[] _utf8 = new byte[4];
    private int _utf8Count;
    private int _utf8Length;

    public int CountTextUnits(ReadOnlySpan<byte> data)
    {
        var count = 0;
        foreach (var value in data)
        {
            if (_state == State.StringEscape)
            {
                _state = value == (byte)'\\' || value == 0x07 ? State.Ground
                    : value == 0x1b ? State.StringEscape : State.String;
                continue;
            }
            if (_state == State.String)
            {
                if (value == 0x07) _state = State.Ground;
                else if (value == 0x1b) _state = State.StringEscape;
                continue;
            }
            if (value == 0x1b)
            {
                _utf8Count = 0;
                _state = State.Escape;
                continue;
            }
            if (value is 0x18 or 0x1a)
            {
                _state = State.Ground;
                _utf8Count = 0;
                continue;
            }
            if (_state == State.Escape)
            {
                _state = value switch
                {
                    (byte)'[' => State.Csi,
                    (byte)']' or (byte)'P' or (byte)'X' or (byte)'^' or (byte)'_' => State.String,
                    >= 0x20 and <= 0x2f => State.EscapeIntermediate,
                    _ => State.Ground
                };
                continue;
            }
            if (_state == State.Csi || _state == State.EscapeIntermediate)
            {
                if (value >= (_state == State.Csi ? 0x40 : 0x30) && value <= 0x7e)
                    _state = State.Ground;
                continue;
            }
            if (_utf8Count > 0)
            {
                if ((value & 0xc0) == 0x80)
                {
                    _utf8[_utf8Count++] = value;
                    if (_utf8Count == _utf8Length)
                    {
                        if (Rune.DecodeFromUtf8(_utf8.AsSpan(0, _utf8Count), out var rune, out _) == System.Buffers.OperationStatus.Done
                            && IsText(rune)) count++;
                        _utf8Count = 0;
                    }
                    continue;
                }
                _utf8Count = 0;
            }
            if (value < 0x80)
            {
                if (IsText(new Rune(value))) count++;
            }
            else if (value is >= 0xc2 and <= 0xf4)
            {
                _utf8[0] = value;
                _utf8Count = 1;
                _utf8Length = value < 0xe0 ? 2 : value < 0xf0 ? 3 : 4;
            }
        }
        return count;
    }

    private static bool IsText(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
        UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or
        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or
        UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber or
        UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or
        UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or
        UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;
}
