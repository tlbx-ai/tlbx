namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed class SessionHeatSnapshot
{
    public long TotalOutputBytes { get; init; }
    public long TotalInputBytes { get; init; }
    public int TotalBellCount { get; init; }
    public DateTimeOffset? LastInputAt { get; init; }
    public DateTimeOffset? LastOutputAt { get; init; }
    public DateTimeOffset? LastTextOutputAt { get; init; }
    public DateTimeOffset? LastBellAt { get; init; }
    public int CurrentBytesPerSecond { get; init; }
    public double CurrentHeat { get; init; }
}
