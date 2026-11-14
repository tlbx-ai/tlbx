namespace Ai.Tlbx.MidTerm.Models.WebPreview;

public sealed class WebPreviewProxyLogEntry
{
    public int Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string Type { get; set; } = "";
    public string Method { get; set; } = "";
    public string RequestUrl { get; set; } = "";
    public string UpstreamUrl { get; set; } = "";
    public int StatusCode { get; set; }
    public string? Error { get; set; }
    public long DurationMs { get; set; }
    public int? UpstreamStatusCode { get; set; }
    public long? UpstreamDurationMs { get; set; }
    public string? Outcome { get; set; }
    public bool? ResponseStarted { get; set; }
    public Dictionary<string, string> RequestHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ResponseHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? RequestCookies { get; set; }
    public string? ResponseCookies { get; set; }
    public string? ContentType { get; set; }
    public long? ContentLength { get; set; }
    public string? SubProtocols { get; set; }
    public string? NegotiatedProtocol { get; set; }
}
