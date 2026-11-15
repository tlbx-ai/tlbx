namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed class SessionAppServerControlHeatSnapshot
{
    public static SessionAppServerControlHeatSnapshot Cold { get; } = new();

    public bool IsDedicatedSession { get; init; }
    public double CurrentHeat { get; init; }
    public DateTimeOffset? LastActivityAt { get; init; }
}
