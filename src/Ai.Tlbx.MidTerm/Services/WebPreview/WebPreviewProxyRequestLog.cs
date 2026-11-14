using Ai.Tlbx.MidTerm.Models.WebPreview;

namespace Ai.Tlbx.MidTerm.Services.WebPreview;

// Request-local completion of the existing proxy log. Entries are published only
// after transformation/body copy, so readers never see an unfinished 200 as success.
internal sealed class WebPreviewProxyRequestLog(string routeKey, WebPreviewProxyLogEntry initial)
{
    private readonly List<WebPreviewProxyLogEntry> _attempts = [];
    private readonly Dictionary<HttpResponseMessage, WebPreviewProxyLogEntry> _responses = new(ReferenceEqualityComparer.Instance);
    private WebPreviewProxyLogEntry? _selected;

    internal string RouteKey { get; } = routeKey;
    internal string Stage { get; set; } = "upstream";

    internal void Record(WebPreviewProxyLogEntry entry, HttpResponseMessage? response)
    {
        entry.UpstreamStatusCode = response is null ? null : (int)response.StatusCode;
        entry.UpstreamDurationMs = entry.DurationMs;
        entry.Outcome = "upstream-attempt";
        _attempts.Add(entry);
        if (response is not null) _responses[response] = entry;
    }

    internal void Select(HttpResponseMessage? response)
    {
        _selected = response is not null && _responses.TryGetValue(response, out var entry) ? entry : null;
        Stage = "response-headers";
    }

    internal IReadOnlyList<WebPreviewProxyLogEntry> Complete(int statusCode, bool responseStarted,
        bool requestAborted, Exception? failure, long elapsedMs)
    {
        var entry = _selected ?? _attempts.LastOrDefault() ?? initial;
        if (_attempts.Count == 0) _attempts.Add(entry);
        entry.DurationMs = elapsedMs;
        entry.ResponseStarted = responseStarted;
        entry.StatusCode = statusCode;
        if (requestAborted)
        {
            // 499 is a diagnostic classification before headers, not a claim
            // that a disconnected client received an HTTP response.
            if (!responseStarted) entry.StatusCode = 499;
            entry.Outcome = "cancelled";
            entry.Error = $"Downstream request cancelled during {Stage}; " +
                (responseStarted ? "headers already sent." : "response not started.");
        }
        else if (failure is not null)
        {
            if (!responseStarted) entry.StatusCode = StatusCodes.Status500InternalServerError;
            entry.Outcome = responseStarted ? "incomplete" : "failed";
            // Exception messages/stacks can contain upstream URLs, credentials
            // or source text. Add only the stage and exception type.
            entry.Error = $"Downstream {Stage} failed ({failure.GetType().Name}); " +
                (responseStarted ? "headers already sent; response incomplete." : "response not started.");
        }
        else
        {
            entry.Outcome = statusCode >= 400 ? "failed" : "completed";
        }
        return _attempts;
    }
}
