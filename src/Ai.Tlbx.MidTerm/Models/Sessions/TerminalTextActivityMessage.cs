namespace Ai.Tlbx.MidTerm.Models.Sessions;

public sealed class TerminalTextActivityMessage
{
    public string Type => "terminal-text-activity";
    public required string SessionId { get; init; }
    public DateTimeOffset? LastTextOutputAt { get; init; }
    public double? TextActivityAgeMs { get; init; }
}
