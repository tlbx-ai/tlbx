namespace Ai.Tlbx.MidTerm.Settings;

public sealed class ManagerBarTrigger
{
    private static readonly HashSet<string> ValidKinds = new(StringComparer.Ordinal)
    {
        "fireAndForget",
        "onCooldown",
        "repeatCount",
        "repeatInterval",
        "schedule"
    };

    private static readonly HashSet<string> ValidIntervalUnits = new(StringComparer.Ordinal)
    {
        "seconds",
        "minutes",
        "hours",
        "days"
    };

    public string Kind { get; set; } = "fireAndForget";
    public int RepeatCount { get; set; } = 2;
    public int RepeatEveryValue { get; set; } = 5;
    public string RepeatEveryUnit { get; set; } = "minutes";
    public List<ManagerBarScheduleEntry> Schedule { get; set; } = [new()];

    public ManagerBarTrigger Normalize()
    {
        var normalizedKind = ValidKinds.Contains(Kind) ? Kind : "fireAndForget";
        var normalizedUnit = ValidIntervalUnits.Contains(RepeatEveryUnit) ? RepeatEveryUnit : "minutes";
        var normalizedSchedule = Schedule
            .Select(entry => entry.Normalize())
            .Where(entry => entry is not null)
            .Cast<ManagerBarScheduleEntry>()
            .ToList();

        if (normalizedKind == "schedule" && normalizedSchedule.Count == 0)
        {
            normalizedSchedule.Add(new ManagerBarScheduleEntry());
        }

        return new ManagerBarTrigger
        {
            Kind = normalizedKind,
            RepeatCount = Math.Max(1, RepeatCount),
            RepeatEveryValue = Math.Max(1, RepeatEveryValue),
            RepeatEveryUnit = normalizedUnit,
            Schedule = normalizedSchedule
        };
    }
}
