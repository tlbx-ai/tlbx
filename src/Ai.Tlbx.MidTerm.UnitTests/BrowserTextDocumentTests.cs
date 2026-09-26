using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class BrowserTextDocumentTests
{
    [Theory]
    [InlineData("application/json", "navigate", "iframe", true)]
    [InlineData("application/problem+json", "navigate", "document", true)]
    [InlineData("text/plain", "navigate", "iframe", true)]
    [InlineData("application/json", "cors", "empty", false)]
    [InlineData("application/json", "same-origin", "empty", false)]
    [InlineData("application/json", "", "", false)]
    [InlineData("text/plain", "no-cors", "script", false)]
    [InlineData("image/png", "navigate", "iframe", false)]
    [InlineData("text/event-stream", "navigate", "iframe", false)]
    public void OnlyTextualDocumentNavigationsBecomeControllablePages(
        string contentType, string mode, string destination, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Headers["Sec-Fetch-Mode"] = mode;
        context.Request.Headers["Sec-Fetch-Dest"] = destination;
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"posted\":true}", Encoding.UTF8, contentType)
        };
        Assert.Equal(expected, WebPreviewProxyMiddleware.ShouldRenderTextDocument(context.Request, response));
    }

    [Fact]
    public void ExplicitDownloadsKeepTheirOriginalRepresentation()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Sec-Fetch-Mode"] = "navigate";
        context.Request.Headers["Sec-Fetch-Dest"] = "iframe";
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment");
        Assert.False(WebPreviewProxyMiddleware.ShouldRenderTextDocument(context.Request, response));
    }

    [Fact]
    public void ViewerPreservesResponseTextWithoutExecutingEmbeddedMarkup()
    {
        const string text = "{\"value\":\"</pre><script>alert(1)</script>& 🧰\"}\nsecond line";
        var html = WebPreviewProxyMiddleware.RenderTextDocument(text);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        var start = html.IndexOf("<pre>", StringComparison.Ordinal) + "<pre>".Length;
        var end = html.IndexOf("</pre>", start, StringComparison.Ordinal);
        Assert.Equal(text, WebUtility.HtmlDecode(html[start..end]));
    }
}
