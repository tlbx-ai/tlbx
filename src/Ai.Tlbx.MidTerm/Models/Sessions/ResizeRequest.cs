namespace Ai.Tlbx.MidTerm.Models.Sessions;

/// <summary>
/// Request payload for resizing a terminal session.
/// </summary>
public sealed class ResizeRequest
{
    public int Cols { get; set; }
    public int Rows { get; set; }
}
