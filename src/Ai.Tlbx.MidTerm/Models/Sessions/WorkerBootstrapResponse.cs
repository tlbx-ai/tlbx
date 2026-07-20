namespace Ai.Tlbx.MidTerm.Models.Sessions;

public sealed class WorkerBootstrapResponse
{
    public required SessionInfoDto Session { get; set; }
    public string Profile { get; set; } = "unknown";
    public string? LaunchCommand { get; set; }
    public List<string> SlashCommands { get; set; } = [];
    public bool GuidanceInjected { get; set; }
    public string? TlbxDir { get; set; }
}
