using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.AspNetCore.Http;

namespace Ai.Tlbx.MidTerm.Services.WebPreview;

/// <summary>Useful remote diagnostics without URLs containing credentials or arbitrary exception text.</summary>
internal static class PreviewProxyFailure
{
    public static string Describe(Exception error, Uri target, string stage = "tlbx host to application")
    {
        var origin = new UriBuilder(target.Scheme, target.Host, target.Port).Uri.GetLeftPart(UriPartial.Authority);
        var cause = error is OperationCanceledException ? "Request timed out" : error.GetType().Name;
        if (error is HttpRequestException request) cause = request.HttpRequestError.ToString();
        for (Exception? inner = error; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException socket) { cause = "Socket: " + socket.SocketErrorCode; break; }
            if (inner is AuthenticationException) cause = "TLS handshake failed";
        }
        return $"Preview request failed.\nStage: {stage}\nTarget: {origin}\nCause: {cause}\nCheck that the target is running and the HTTP/HTTPS scheme and port are correct. The target is resolved on the tlbx host.";
    }

    public static async Task WriteAsync(HttpContext context, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Tlbx-Failure-Stage"] = "host-to-application";
        var message = context.Items[typeof(PreviewProxyFailure)] as string
            ?? "Preview request failed. No upstream response was received. Check the target on the tlbx host.";
        await context.Response.WriteAsync(message, context.RequestAborted);
    }
}
