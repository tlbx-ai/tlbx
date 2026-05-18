using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.Models.Sessions;

/// <summary>
/// Terminal session information returned by the API.
/// </summary>
public sealed class SessionInfoDto
{
    public string Id { get; set; } = string.Empty;
    public int Pid { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsRunning { get; set; }
    public int? ExitCode { get; set; }
    public int Cols { get; set; }
    public int Rows { get; set; }
    public string ShellType { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? TerminalTitle { get; set; }
    public string? Topic { get; set; }
    public string? Notes { get; set; }
    public bool ManuallyNamed { get; set; }
    public string? CurrentDirectory { get; set; }
    public int? ForegroundPid { get; set; }
    public string? ForegroundName { get; set; }
    public string? ForegroundCommandLine { get; set; }
    public string? ForegroundDisplayName { get; set; }
    public string? ForegroundProcessIdentity { get; set; }
    public SessionAgentAttachPoint? AgentAttachPoint { get; set; }
    public int Order { get; set; }
    public string? ParentSessionId { get; set; }
    public string? BookmarkId { get; set; }
    public string? SpaceId { get; set; }
    public string? WorkspacePath { get; set; }
    public string? Surface { get; set; }
    public bool IsAdHoc { get; set; }
    public bool AgentControlled { get; set; }
    public bool AppServerControlOnly { get; set; }
    public string? ProfileHint { get; set; }
    public string? AppServerControlResumeThreadId { get; set; }
    public bool HasAppServerControlHistory { get; set; }
    public SessionSupervisorInfoDto? Supervisor { get; set; }
}
