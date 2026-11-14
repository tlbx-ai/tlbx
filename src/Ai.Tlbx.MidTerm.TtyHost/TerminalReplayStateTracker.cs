using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.TtyHost;

/// <summary>
/// Tracks persistent terminal modes whose activating control sequence may age out of the
/// raw scrollback ring. State is queried at the first byte of a replay so a fresh terminal
/// can restore the buffer and mouse/input modes before retained output is parsed.
/// </summary>
internal sealed class TerminalReplayStateTracker
{
    private enum ParseState : byte
    {
        Ground,
        Escape,
        Csi,
        CsiPrivate
    }

    private readonly List<Transition> _transitions = [];
    private ParseState _parseState;
    private int _parameter;
    private readonly List<int> _parameters = [];
    private static TerminalReplayState InitialState => new(0, 0x8000);
    private TerminalReplayState _baselineState = InitialState;
    private ulong _baselineSequence;

    public void Consume(ReadOnlySpan<byte> data, ulong sequenceStart)
    {
        for (var index = 0; index < data.Length; index++)
        {
            var value = data[index];
            var sequenceEndExclusive = sequenceStart + (ulong)index + 1;

            if (value == 0x1b)
            {
                _parseState = ParseState.Escape;
                ResetCsiParameters();
                continue;
            }

            if (value == 0x9b)
            {
                _parseState = ParseState.Csi;
                ResetCsiParameters();
                continue;
            }

            switch (_parseState)
            {
                case ParseState.Escape:
                    if (value == (byte)'[')
                    {
                        _parseState = ParseState.Csi;
                    }
                    else
                    {
                        if (value == (byte)'c')
                        {
                            RecordTransition(sequenceEndExclusive, InitialState);
                        }

                        _parseState = ParseState.Ground;
                    }
                    break;

                case ParseState.Csi:
                    if (value == (byte)'?')
                    {
                        _parseState = ParseState.CsiPrivate;
                    }
                    else
                    {
                        _parseState = IsCsiContinuation(value) ? ParseState.Csi : ParseState.Ground;
                    }
                    break;

                case ParseState.CsiPrivate:
                    if (value is >= (byte)'0' and <= (byte)'9')
                    {
                        _parameter = Math.Min(ushort.MaxValue, (_parameter * 10) + value - (byte)'0');
                    }
                    else if (value == (byte)';' && _parameters.Count < 64)
                    {
                        _parameters.Add(_parameter);
                        _parameter = 0;
                    }
                    else if (value is (byte)'h' or (byte)'l')
                    {
                        _parameters.Add(_parameter);
                        var state = _transitions.Count > 0 ? _transitions[^1].State : _baselineState;
                        foreach (var parameter in _parameters)
                        {
                            state = ApplyMode(state, parameter, value == (byte)'h');
                        }
                        RecordTransition(sequenceEndExclusive, state);

                        _parseState = ParseState.Ground;
                        ResetCsiParameters();
                    }
                    else
                    {
                        _parseState = IsCsiContinuation(value) ? ParseState.CsiPrivate : ParseState.Ground;
                    }
                    break;
            }
        }
    }

    public TerminalReplayState GetStateAt(ulong sequence)
    {
        if (sequence < _baselineSequence)
        {
            return InitialState;
        }

        var state = _baselineState;
        var low = 0;
        var high = _transitions.Count - 1;
        var lastMatch = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (_transitions[middle].SequenceEndExclusive <= sequence)
            {
                lastMatch = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (lastMatch >= 0)
        {
            state = _transitions[lastMatch].State;
        }

        return state;
    }

    public void TrimBefore(ulong sequence)
    {
        _baselineState = GetStateAt(sequence);
        _baselineSequence = sequence;

        var removeCount = 0;
        while (removeCount < _transitions.Count
            && _transitions[removeCount].SequenceEndExclusive <= sequence)
        {
            removeCount++;
        }

        if (removeCount > 0)
        {
            _transitions.RemoveRange(0, removeCount);
        }
    }

    private static bool IsCsiContinuation(byte value) => value is >= 0x20 and <= 0x3f;

    private static TerminalReplayState ApplyMode(TerminalReplayState state, int parameter, bool enabled)
    {
        if (parameter is 47 or 1047 or 1049)
        {
            return state with { AlternateScreenMode = enabled ? (ushort)parameter : (ushort)0 };
        }

        var tracking = parameter switch { 9 => 1, 1000 => 2, 1002 => 3, 1003 => 4, _ => 0 };
        var encoding = parameter switch { 1006 => 2, 1016 => 4, _ => 0 };
        var modes = (int)state.InputModes;
        if (tracking != 0)
        {
            modes = (modes & ~7) | (enabled ? tracking : 0);
        }
        else if (encoding != 0)
        {
            modes = (modes & ~(7 << 3)) | (enabled ? encoding << 3 : 0);
        }
        else
        {
            var flag = parameter switch { 1 => 1 << 6, 2004 => 1 << 7, 1004 => 1 << 8, _ => 0 };
            modes = enabled ? modes | flag : modes & ~flag;
        }
        return state with { InputModes = (ushort)modes };
    }

    private void ResetCsiParameters()
    {
        _parameter = 0;
        _parameters.Clear();
    }

    private void RecordTransition(ulong sequenceEndExclusive, TerminalReplayState state)
    {
        var priorState = _transitions.Count > 0 ? _transitions[^1].State : _baselineState;
        if (priorState == state)
        {
            return;
        }

        _transitions.Add(new Transition(sequenceEndExclusive, state));
    }

    private readonly record struct Transition(ulong SequenceEndExclusive, TerminalReplayState State);
}
