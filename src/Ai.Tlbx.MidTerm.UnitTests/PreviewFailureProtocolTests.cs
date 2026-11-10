using System.Net;
using System.Net.Sockets;
using System.Text;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Ai.Tlbx.MidTerm.Startup;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class PreviewFailureProtocolTests
{
    [Fact]
    public void ConnectionFailureIdentifiesHostTargetAndSocketCauseWithoutSecrets()
    {
        var error = new HttpRequestException("private-token", new SocketException((int)SocketError.ConnectionRefused));
        var text = PreviewProxyFailure.Describe(error, new Uri("http://user:password@localhost:5043/private-path?token=secret#fragment"));
        Assert.Contains("http://localhost:5043", text, StringComparison.Ordinal);
        Assert.Contains("ConnectionRefused", text, StringComparison.Ordinal);
        Assert.Contains("tlbx host to application", text, StringComparison.Ordinal);
        foreach (var secret in new[] { "private-token", "user", "password", "private-path", "secret", "fragment" }) Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProxyFailureIsReadableAndUncachedOverHttp()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        context.Items[typeof(PreviewProxyFailure)] = PreviewProxyFailure.Describe(new TaskCanceledException(), new Uri("https://localhost:5043/"));
        await PreviewProxyFailure.WriteAsync(context, 504);
        Assert.Equal(504, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal("host-to-application", context.Response.Headers["X-Tlbx-Failure-Stage"]);
        Assert.Contains("Request timed out", Encoding.UTF8.GetString(body.ToArray()), StringComparison.Ordinal);
    }
    [Fact]
    public async Task UnreachableApplicationReturnsDetailed502ThroughTheActualPreviewMiddleware()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        listener.Stop();
        var service = new WebPreviewService(2000);
        Assert.True(service.SetTarget("failure-fixture", "diagnostic", $"http://127.0.0.1:{port}/"));
        var route = service.GetPreviewSession("failure-fixture", "diagnostic")!.RouteKey;
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = $"/webpreview/{route}/js/app.js";
        context.Request.QueryString = new QueryString("?token=must-not-appear");
        context.Items[PreviewProxyAuthorization.RouteItemKey] = route;
        await using var body = new MemoryStream();
        context.Response.Body = body;
        var middleware = new WebPreviewProxyMiddleware(_ => throw new InvalidOperationException("Must use proxy"), service);
        try { await middleware.InvokeAsync(context); }
        finally { service.DeletePreviewSession("failure-fixture", "diagnostic"); }
        Assert.Equal(502, context.Response.StatusCode);
        var text = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains($"http://127.0.0.1:{port}", text, StringComparison.Ordinal);
        Assert.Contains("ConnectionRefused", text, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-appear", text, StringComparison.Ordinal);
        Assert.Equal("host-to-application", context.Response.Headers["X-Tlbx-Failure-Stage"]);
    }

}
