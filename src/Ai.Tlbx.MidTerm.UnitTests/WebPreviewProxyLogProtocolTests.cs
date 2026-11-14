using System.Net;
using Ai.Tlbx.MidTerm.Models.WebPreview;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class WebPreviewProxyLogProtocolTests
{
    [Fact]
    public void TransformFailureDoesNotReportUpstream200AsSuccessOrExposeExceptionText()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        var entry = new WebPreviewProxyLogEntry { Type = "http", StatusCode = 200, DurationMs = 7 };
        var log = new WebPreviewProxyRequestLog("route", new());
        log.Record(entry, response);
        log.Select(response);
        log.Stage = "JavaScript transform/body";

        var result = Assert.Single(log.Complete(200, false, false,
            new InvalidOperationException("private-token-and-source"), 123));

        Assert.Equal(500, result.StatusCode);
        Assert.Equal(200, result.UpstreamStatusCode);
        Assert.Equal(7L, result.UpstreamDurationMs);
        Assert.Equal(123L, result.DurationMs);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal(false, result.ResponseStarted);
        Assert.Contains("JavaScript transform/body", result.Error);
        Assert.Contains("InvalidOperationException", result.Error);
        Assert.DoesNotContain("private-token-and-source", result.Error);
        var summary = WebPreviewEndpoints.BuildProxyLogSummaryText([result]);
        Assert.Contains("errors: 1", summary);
        Assert.Contains("500 http upstream=200 outcome=failed", summary);
    }

    [Theory]
    [InlineData(false, false, 500, "failed")]
    [InlineData(true, false, 200, "incomplete")]
    [InlineData(false, true, 499, "cancelled")]
    [InlineData(true, true, 200, "cancelled")]
    public void StreamingFailureAndCancellationRespectAlreadySentHeaders(
        bool started, bool cancelled, int status, string outcome)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        var log = new WebPreviewProxyRequestLog("route", new());
        log.Record(new() { StatusCode = 200, DurationMs = 2 }, response);
        log.Select(response);
        log.Stage = "stream body";

        var result = Assert.Single(log.Complete(200, started, cancelled, new IOException(), 999));

        Assert.Equal(status, result.StatusCode);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(started, result.ResponseStarted);
        Assert.Equal(999L, result.DurationMs);
        Assert.NotNull(result.Error);
        Assert.Contains(started ? "headers already sent" : "response not started", result.Error);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(304)]
    public void NormalAndNotModifiedResponsesCompleteWithFullDuration(int status)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status);
        var log = new WebPreviewProxyRequestLog("route", new());
        log.Record(new() { StatusCode = status, DurationMs = 3 }, response);
        log.Select(response);

        var result = Assert.Single(log.Complete(status, true, false, null, 12));

        Assert.Equal(status, result.StatusCode);
        Assert.Equal(status, result.UpstreamStatusCode);
        Assert.Equal("completed", result.Outcome);
        Assert.Null(result.Error);
        Assert.Equal(12L, result.DurationMs);
        Assert.Contains("errors: 0", WebPreviewEndpoints.BuildProxyLogSummaryText([result]));
        Assert.Contains("redirects: 0", WebPreviewEndpoints.BuildProxyLogSummaryText([result]));
    }

    [Fact]
    public void DiscardedFallbackIsAnAttemptAndDoesNotReplaceSelectedResponse()
    {
        using var primary = new HttpResponseMessage(HttpStatusCode.NotFound);
        using var fallback = new HttpResponseMessage(HttpStatusCode.NotFound);
        var log = new WebPreviewProxyRequestLog("route", new());
        var selected = new WebPreviewProxyLogEntry { StatusCode = 404, DurationMs = 2 };
        var discarded = new WebPreviewProxyLogEntry { StatusCode = 404, DurationMs = 4 };
        log.Record(selected, primary);
        log.Record(discarded, fallback);
        log.Select(primary);

        var results = log.Complete(404, true, false, null, 15);

        Assert.Equal(2, results.Count);
        Assert.Equal("failed", selected.Outcome);
        Assert.Equal(15L, selected.DurationMs);
        Assert.Equal("upstream-attempt", discarded.Outcome);
        Assert.Equal(4L, discarded.DurationMs);
        var summary = WebPreviewEndpoints.BuildProxyLogSummaryText(results);
        Assert.Contains("errors: 1", summary);
        Assert.Contains("upstream-only attempts: 1", summary);
    }

    [Fact]
    public void SuccessfulFallbackDoesNotCountTheOriginal404AsADownstreamError()
    {
        using var primary = new HttpResponseMessage(HttpStatusCode.NotFound);
        using var fallback = new HttpResponseMessage(HttpStatusCode.OK);
        var log = new WebPreviewProxyRequestLog("route", new());
        log.Record(new() { StatusCode = 404, DurationMs = 2 }, primary);
        log.Record(new() { StatusCode = 200, DurationMs = 4 }, fallback);
        log.Select(fallback);

        var results = log.Complete(200, true, false, null, 15);

        Assert.Equal("upstream-attempt", results[0].Outcome);
        Assert.Equal("completed", results[1].Outcome);
        Assert.Equal(15L, results[1].DurationMs);
        Assert.Contains("errors: 0", WebPreviewEndpoints.BuildProxyLogSummaryText(results));
    }

    [Fact]
    public void TimeoutKeepsGatewayStatusWithoutInventingAnUpstreamResponse()
    {
        var log = new WebPreviewProxyRequestLog("route", new());
        log.Record(new() { StatusCode = 504, Error = "timeout", DurationMs = 30 }, null);
        log.Select(null);

        var result = Assert.Single(log.Complete(504, true, false, null, 32));

        Assert.Equal(504, result.StatusCode);
        Assert.Null(result.UpstreamStatusCode);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal("timeout", result.Error);
        Assert.Equal(32L, result.DurationMs);
    }

    [Fact]
    public void FailureBeforeUpstreamHeadersStillProducesOneCompletedDiagnostic()
    {
        var log = new WebPreviewProxyRequestLog("route", new() { Method = "POST" });

        var result = Assert.Single(log.Complete(200, false, false, new IOException(), 9));

        Assert.Equal(500, result.StatusCode);
        Assert.Null(result.UpstreamStatusCode);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal(9L, result.DurationMs);
    }

    [Fact]
    public void SummaryCountsAllFailuresWhileBoundingFailureDetails()
    {
        var entries = Enumerable.Range(0, 10)
            .Select(_ => new WebPreviewProxyLogEntry { StatusCode = 500 }).ToArray();

        var summary = WebPreviewEndpoints.BuildProxyLogSummaryText(entries);

        Assert.Contains("errors: 10", summary);
        Assert.Equal(8, summary.Split('\n').Count(line => line.StartsWith("  500 ", StringComparison.Ordinal)));
    }
}
