namespace Ai.Tlbx.MidTerm.Services.Sessions;

internal static class TerminalHeat
{
    public const double CooldownSeconds = 30;

    public static double FromTextOutput(DateTimeOffset? lastTextOutputAt, DateTimeOffset now) =>
        lastTextOutputAt is { } at
            ? Math.Clamp(1 - (now - at).TotalSeconds / CooldownSeconds, 0, 1)
            : 0;
}
