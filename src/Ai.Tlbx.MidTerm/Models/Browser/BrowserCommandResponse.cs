using System.Text.Json;

namespace Ai.Tlbx.MidTerm.Models.Browser;

public sealed class BrowserCommandResponse
{
    public bool Success { get; init; }
    public string? Result { get; init; }
    public string? Error { get; init; }
    public int? MatchCount { get; init; }
    public JsonElement? Capture { get; init; }
}
