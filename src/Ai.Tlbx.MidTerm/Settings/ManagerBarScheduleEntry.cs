using System.Text.RegularExpressions;

namespace Ai.Tlbx.MidTerm.Settings;

public sealed partial class ManagerBarScheduleEntry
{
    private static readonly HashSet<string> ValidRepeats = new(StringComparer.Ordinal) { "daily", "weekdays", "weekends" };

    public string TimeOfDay { get; set; } = "09:00";
    public string Repeat { get; set; } = "daily";

    public ManagerBarScheduleEntry? Normalize()
    {
        var normalizedTimeOfDay = TimeOfDay ?? "09:00";
        if (!TimePattern().IsMatch(normalizedTimeOfDay))
        {
            return null;
        }

        var normalizedRepeat = ValidRepeats.Contains(Repeat ?? string.Empty) ? Repeat! : "daily";

        return new ManagerBarScheduleEntry
        {
            TimeOfDay = normalizedTimeOfDay,
            Repeat = normalizedRepeat
        };
    }

    [GeneratedRegex("^(?:[01]\\d|2[0-3]):[0-5]\\d$", RegexOptions.None, 1000)]
    private static partial Regex TimePattern();
}
