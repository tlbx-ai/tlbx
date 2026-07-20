using Ai.Tlbx.MidTerm.Models.Update;

namespace Ai.Tlbx.MidTerm.Models.Sessions;

/// <summary>
/// WebSocket state update message sent to clients.
/// </summary>
public sealed class StateUpdate
{
    public SessionListDto? Sessions { get; init; }
    public UpdateInfo? Update { get; init; }
    public SessionLayoutState? Layout { get; init; }
    public List<ManagerBarQueueEntryDto>? ManagerBarQueue { get; init; }
    public List<TerminalSizeControlStatus>? TerminalSizeControls { get; init; }
}
