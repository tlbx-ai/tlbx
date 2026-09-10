namespace Ai.Tlbx.MidTerm.Models.Sessions;

public sealed class SessionSupervisorInfoDto
{
    public string State { get; set; } = "unknown";
    public string Profile { get; set; } = "unknown";
    public bool NeedsAttention { get; set; }
    public string? AttentionReason { get; set; }
    public int AttentionScore { get; set; }
    public DateTimeOffset? LastInputAt { get; set; }
    public DateTimeOffset? LastOutputAt { get; set; }
    public DateTimeOffset? LastBellAt { get; set; }
    public double CurrentHeat { get; set; }
    public DateTimeOffset? LastTextOutputAt { get; set; }
    public double? TextActivityAgeMs { get; set; }
}
