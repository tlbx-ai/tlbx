namespace Ai.Tlbx.MidTerm.Models.Sessions;

public sealed class TerminalSizeControlStatus
{
    public string SessionId { get; set; } = string.Empty;
    public bool IsOwner { get; set; }
    public bool HasOwner { get; set; }
    public bool OwnerOnline { get; set; }
    public bool OwnerInSameBrowserProfile { get; set; }
    public bool CanTakeOverAutomatically { get; set; }
    public string? OwnerLabel { get; set; }
    public long Epoch { get; set; }
}

public sealed class TerminalSizeControlCommandResult
{
    public TerminalSizeControlStatus Status { get; set; } = new();
    public bool OwnershipChanged { get; set; }
    public bool ResizeApplied { get; set; }
    public int Cols { get; set; }
    public int Rows { get; set; }
}
