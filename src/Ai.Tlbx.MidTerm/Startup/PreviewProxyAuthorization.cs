using System.Security.Cryptography;
using System.Text;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.WebPreview;

namespace Ai.Tlbx.MidTerm.Startup;

internal static class PreviewProxyAuthorization
{
    internal const string OpaqueSandboxPolicy = "sandbox allow-scripts allow-forms allow-popups allow-modals allow-downloads";
    internal const string RouteItemKey = "tlbx.authorized-preview-route";
    internal const string PathPrefixItemKey = "tlbx.authorized-preview-path-prefix";
    internal const string ExplicitItemKey = "tlbx.explicit-preview-capability";

    internal static void ApplyResponsePolicy(HttpContext context)
    {
        // Apply before authentication too: error documents, SVG/XHTML and
        // top-level capability navigation must never regain the tlbx origin.
        context.Response.Headers.ContentSecurityPolicy = OpaqueSandboxPolicy;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.ContentSecurityPolicy = OpaqueSandboxPolicy;
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (context.Request.Headers.Origin.ToString().Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Items.ContainsKey(RouteItemKey) && context.Items.ContainsKey(ExplicitItemKey))
                {
                    context.Response.Headers.AccessControlAllowOrigin = "null";
                    context.Response.Headers.AccessControlAllowCredentials = "true";
                    context.Response.Headers.AccessControlAllowMethods = "GET, HEAD, POST, PUT, PATCH, DELETE, OPTIONS";
                    context.Response.Headers.AccessControlAllowHeaders = context.Request.Headers.AccessControlRequestHeaders;
                    context.Response.Headers.AccessControlExposeHeaders = WebPreviewProxyMiddleware.UpstreamUrlHeaderName;
                    context.Response.Headers.Vary = "Origin, Access-Control-Request-Method, Access-Control-Request-Headers";
                }
                else
                {
                    context.Response.Headers.Remove("Access-Control-Allow-Origin");
                    context.Response.Headers.Remove("Access-Control-Allow-Credentials");
                    context.Response.Headers.Remove("Access-Control-Expose-Headers");
                }
            }
            return Task.CompletedTask;
        });
    }

    internal static string GetProxyPrefix(HttpContext context, string routeKey) =>
        context.Items.TryGetValue(PathPrefixItemKey, out var prefix) && prefix is string path
            ? path : $"/webpreview/{routeKey}";

    internal static bool TryAuthorize(HttpContext context, BrowserPreviewRegistry registry)
    {
        var request = context.Request;
        if (!TryGetRoute(request, out var routeKey)) return false;
        var cookieName = "mt-preview-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(routeKey)))[..16];
        var pathAccess = TryParseAccessPath(request.Path, out _, out var pathId, out var pathToken, out var prefix, out _);
        var previewId = pathAccess ? pathId : request.Query["__mtPreviewId"].ToString();
        var token = pathAccess ? pathToken : request.Query["__mtPreviewToken"].ToString();
        var bootstrap = pathAccess || !string.IsNullOrEmpty(previewId) || !string.IsNullOrEmpty(token);
        if (!bootstrap)
        {
            // Opaque/cross-site callers must prove their own route capability,
            // not borrow another preview's browser-supplied HttpOnly cookie.
            if (request.Headers.Origin.ToString().Equals("null", StringComparison.OrdinalIgnoreCase)
                || request.Headers["Sec-Fetch-Site"].ToString().Equals("cross-site", StringComparison.OrdinalIgnoreCase)) return false;
            var cookie = request.Cookies[cookieName]?.Split(':', 2);
            if (cookie is not { Length: 2 }) return false;
            previewId = cookie[0];
            token = cookie[1];
        }

        if (previewId.Length > 128 || token.Length > 256) return false;
        if (!registry.TryValidate(previewId, token, out var registration)
            || !string.Equals(registration.RouteKey, routeKey, StringComparison.Ordinal)) return false;

        context.Items[RouteItemKey] = routeKey;
        if (bootstrap) context.Items[ExplicitItemKey] = true;
        if (pathAccess) context.Items[PathPrefixItemKey] = prefix;
        if (bootstrap && !pathAccess)
        {
            context.Response.Cookies.Append(cookieName, $"{previewId}:{token}", new CookieOptions
            {
                Secure = true,
                HttpOnly = true,
                SameSite = SameSiteMode.None,
                Path = "/",
                MaxAge = TimeSpan.FromHours(8),
                IsEssential = true
            });
        }
        return true;
    }

    internal static bool TryParseAccessPath(PathString path, out string routeKey, out string previewId,
        out string token, out string prefix, out PathString logicalPath)
    {
        routeKey = previewId = token = prefix = "";
        logicalPath = path;
        if (!WebPreviewProxyMiddleware.TryParseProxyRoute(path, out routeKey, out var remaining)
            || !remaining.StartsWith("/_access/", StringComparison.Ordinal)) return false;
        var parts = remaining[9..].Split('/', 3);
        if (parts.Length < 2 || parts[0].Length is < 1 or > 128 || parts[1].Length is < 1 or > 256
            || !parts[0].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            || !parts[1].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        previewId = parts[0]; token = parts[1];
        prefix = $"/webpreview/{Uri.EscapeDataString(routeKey)}/_access/{previewId}/{token}";
        logicalPath = $"/webpreview/{Uri.EscapeDataString(routeKey)}/" + (parts.Length == 3 ? parts[2] : "");
        return true;
    }

    internal static string RedactAccessPath(string value)
    {
        // URI referers retain percent-escaped marker spellings, whereas Kestrel
        // has already decoded request paths. Decode only marker segments, never
        // the upstream remainder (notably %2F).
        var parts = value.Split('/', 7);
        if (parts.Length < 4
            || !Uri.UnescapeDataString(parts[1]).Equals("webpreview", StringComparison.OrdinalIgnoreCase)
            || !Uri.UnescapeDataString(parts[3]).Equals("_access", StringComparison.Ordinal)) return value;
        return $"/webpreview/{parts[2]}/" + (parts.Length == 7 ? parts[6] : "");
    }

    internal static bool AllowsRoute(HttpContext context, string routeKey) =>
        !context.Items.TryGetValue(RouteItemKey, out var authorized)
        || string.Equals(authorized as string, routeKey, StringComparison.Ordinal);

    private static bool TryGetRoute(HttpRequest request, out string routeKey)
    {
        if (WebPreviewProxyMiddleware.TryParseProxyRoute(request.Path, out routeKey, out _)) return true;
        return Uri.TryCreate(request.Headers.Referer.ToString(), UriKind.Absolute, out var referer)
            && WebPreviewProxyMiddleware.TryParseProxyRoute(referer.AbsolutePath, out routeKey, out _);
    }
}
