using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Models.Sessions;

public sealed class ManagerBarQueueEnqueueRequest
{
    public string SessionId { get; set; } = string.Empty;
    public ManagerBarButton? Action { get; set; }
    public AppServerControlTurnRequest? Turn { get; set; }
    public int? DelayMs { get; set; }
    public DateTimeOffset? RunAt { get; set; }
}
