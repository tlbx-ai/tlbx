namespace Ai.Tlbx.MidTerm.Models.Sessions;

public sealed class InjectGuidanceResponse
{
    public string TlbxDir { get; set; } = "";
    public string TlbxCliShellPath { get; set; } = "";
    public string TlbxCliPowerShellPath { get; set; } = "";
    public bool ClaudeMdUpdated { get; set; }
    public bool AgentsMdUpdated { get; set; }
}
