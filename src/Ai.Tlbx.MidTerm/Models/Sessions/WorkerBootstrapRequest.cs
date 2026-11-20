namespace Ai.Tlbx.MidTerm.Models.Sessions;

public sealed class WorkerBootstrapRequest
{
    public string? Name { get; set; }
    public string? Shell { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? ResumeThreadId { get; set; }
    public string? HandoffSourceSessionId { get; set; }
    public int Cols { get; set; } = 120;
    public int Rows { get; set; } = 30;
    public bool AgentControlled { get; set; } = true;
    public bool InjectGuidance { get; set; } = true;
    public string? Profile { get; set; }
    public bool AppServerControlOnly { get; set; }
    public string? LaunchCommand { get; set; }
    public string? SpaceId { get; set; }
    public string? WorkspacePath { get; set; }
    public string? Surface { get; set; }
    public int LaunchDelayMs { get; set; } = 1200;
    public List<string> SlashCommands { get; set; } = [];
    public int SlashCommandDelayMs { get; set; } = 350;
}
