using System.Net;
using Ai.Tlbx.MidTerm.Services.Browser;

namespace Ai.Tlbx.MidTerm.Services.Connect;

internal static class ConnectLocalAccessPolicy
{
    internal static bool IsSetupPath(string path) => path.Equals("/connect.html", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/connect-setup.", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/connect", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/connect/", StringComparison.OrdinalIgnoreCase);

    internal static bool Allows(HttpContext context, bool previewRequest)
    {
        var request = context.Request;
        if (previewRequest || !request.IsHttps
            || context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)
            || context.Connection.LocalIpAddress is not { } local || !IPAddress.IsLoopback(local)
            || (request.Host.Port ?? 443) != context.Connection.LocalPort)
            return false;
        var host = request.Host.Host;
        if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && (!IPAddress.TryParse(host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address))) return false;
        foreach (var header in request.Headers.Keys)
            if (header.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) || header.Equals("Via", StringComparison.OrdinalIgnoreCase)
                || header.Equals("X-Real-IP", StringComparison.OrdinalIgnoreCase) || header.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                || header.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
                || header.Equals("X-MidTerm-Internal-Proxy", StringComparison.OrdinalIgnoreCase)) return false;
        if (!request.Headers.TryGetValue("Sec-Fetch-Site", out var site) || site.ToString() is not ("same-origin" or "none")) return false;
        var origin = request.Headers.Origin;
        if (origin.Count > 1 || (origin.Count == 1 && !origin.ToString().Equals($"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase))) return false;
        return HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
            || (origin.Count == 1 && request.HasJsonContentType());
    }

    internal static void Configure(WebApplication app, BrowserPreviewOriginService previewOrigin)
    {
        app.Use(async (context, next) =>
        {
            if (IsSetupPath(context.Request.Path.Value ?? ""))
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.Pragma = "no-cache";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["X-Frame-Options"] = "DENY";
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.Headers["Referrer-Policy"] = "no-referrer";
                    context.Response.Headers["X-Frame-Options"] = "DENY";
                    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
                    return Task.CompletedTask;
                });
                if (!Allows(context, previewOrigin.IsPreviewRequest(context)))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }
            await next();
        });
    }
}
