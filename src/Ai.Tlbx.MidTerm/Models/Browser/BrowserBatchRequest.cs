namespace Ai.Tlbx.MidTerm.Models.Browser;

public sealed class BrowserBatchRequest
{
    public string? SessionId { get; init; }
    public string? PreviewName { get; init; }
    public List<BrowserCommandRequest> Commands { get; init; } = [];
    public int? Timeout { get; init; }
}

public sealed class BrowserBatchResponse
{
    public bool Success { get; set; }
    public int? FailedIndex { get; set; }
    public string? Error { get; set; }
    public double DurationMs { get; set; }
    public List<BrowserBatchStepResult> Results { get; } = [];
}

public sealed class BrowserBatchStepResult
{
    public int Index { get; init; }
    public string Command { get; init; } = "";
    public bool Success { get; init; }
    public string? Result { get; set; }
    public string? Error { get; init; }
    public int? MatchCount { get; init; }
    public double DurationMs { get; init; }
}
