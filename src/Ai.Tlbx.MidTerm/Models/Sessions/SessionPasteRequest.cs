namespace Ai.Tlbx.MidTerm.Models.Sessions;

public sealed class SessionPasteRequest
{
    public string? Text { get; set; }
    public string? Base64 { get; set; }
    public bool BracketedPaste { get; set; }
    public bool IsFilePath { get; set; }
    public string? HistorySource { get; set; }
}
