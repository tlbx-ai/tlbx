using System.Text.Json.Serialization;

namespace Ai.Tlbx.MidTerm.Models.Sessions;

public sealed class SessionResumeRequest
{
    [JsonRequired]
    public string ResumeThreadId { get; set; } = string.Empty;
    public string? WorkingDirectory { get; set; }
}
