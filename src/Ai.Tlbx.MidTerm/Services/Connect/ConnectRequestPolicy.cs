using Ai.Tlbx.MidTerm.Services.Browser;
using Microsoft.AspNetCore.WebUtilities;

namespace Ai.Tlbx.MidTerm.Services.Connect;

internal static class ConnectRequestPolicy
{
    private static readonly HashSet<string> BlockedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Host", "Origin", "Referer", "Connection",
        "Upgrade", "Keep-Alive", "Transfer-Encoding", "TE", "Trailer", "Content-Length",
        "Sec-WebSocket-Key", "Sec-WebSocket-Version", "Sec-WebSocket-Extensions", "Sec-WebSocket-Protocol",
        "Forwarded", "X-Forwarded-Host", "X-Forwarded-For", "X-Forwarded-Proto", "X-MidTerm-Internal-Proxy"
    };

    public static Uri ResolvePath(Uri origin, string? path) => ResolveLocalPath(origin, path, allowPreview: false);

    public static Uri ResolvePreviewPath(Uri origin, string? path, string? routeKey)
    {
        if (routeKey is null || routeKey.Length != 20 || !routeKey.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("A native preview route is required.");
        var uri = ResolveLocalPath(origin, path, allowPreview: true);
        var decoded = DecodePath(uri.AbsolutePath);
        if (decoded == "/ws/browser") return uri;
        if (decoded.Equals("/webpreview", StringComparison.OrdinalIgnoreCase)
            || decoded.StartsWith("/webpreview/", StringComparison.OrdinalIgnoreCase))
        {
            var pieces = decoded.Split('/', 4);
            if (pieces.Length < 3 || pieces[1] != "webpreview" || pieces[2] != routeKey)
                throw new InvalidDataException("The request does not belong to this preview route.");
            return uri;
        }
        // A leaked upstream root URL must stay inside the bound preview, even when it looks like a tlbx API.
        return new Uri(origin, "/webpreview/" + routeKey + uri.PathAndQuery);
    }

    public static bool AuthorizePreviewSocket(BrowserPreviewRegistry registry, Uri destination, string routeKey)
    {
        var query = QueryHelpers.ParseQuery(destination.Query);
        return registry.TryValidate(query["previewId"].ToString(), query["token"].ToString(), out var registration)
            && registration.RouteKey == routeKey
            && (!query.TryGetValue("routeKey", out var queryRoute) || queryRoute.ToString() == routeKey);
    }

    private static Uri ResolveLocalPath(Uri origin, string? path, bool allowPreview)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 16384 || path[0] != '/' || path.StartsWith("//", StringComparison.Ordinal)
            || path.Contains('\\', StringComparison.Ordinal) || path.Contains('#', StringComparison.Ordinal) || path.Any(char.IsControl))
            throw new InvalidDataException("Only local tlbx paths are allowed.");
        var queryIndex = path.IndexOf('?', StringComparison.Ordinal);
        var pathname = queryIndex < 0 ? path : path[..queryIndex];
        pathname = DecodePath(pathname);
        if (!allowPreview && (pathname.Equals("/api/connect", StringComparison.OrdinalIgnoreCase)
            || pathname.StartsWith("/api/connect/", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Connect configuration requires a local login.");
        if (!allowPreview && (pathname.Equals("/webpreview", StringComparison.OrdinalIgnoreCase)
            || pathname.StartsWith("/webpreview/", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Web previews require their scoped Connect surface.");
        var uri = new Uri(origin, path);
        if (!string.Equals(uri.Authority, origin.Authority, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme != origin.Scheme)
            throw new InvalidDataException("Connect request escaped the local origin.");
        return uri;
    }

    private static string DecodePath(string pathname)
    {
        for (var i = 0; i < 3; i++)
        {
            pathname = Uri.UnescapeDataString(pathname);
            if (pathname.Contains('\\', StringComparison.Ordinal) || pathname.Contains('?', StringComparison.Ordinal)
                || pathname.Contains('#', StringComparison.Ordinal) || pathname.Any(char.IsControl)
                || pathname.StartsWith("//", StringComparison.Ordinal)
                || pathname.Split('/').Any(segment => segment is "." or ".."))
                throw new InvalidDataException("Invalid Connect request path.");
        }
        return pathname;
    }

    public static bool AllowHeader(string name) => !BlockedHeaders.Contains(name)
        && !name.StartsWith("Sec-Fetch-", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    // The upstream application may use Basic/Bearer auth. Its requests remain on the scoped preview port/path.
    public static bool AllowPreviewHeader(string name) => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || AllowHeader(name);

    public static Uri NormalizeServerUrl(string? value)
    {
        if (!Uri.TryCreate(string.IsNullOrWhiteSpace(value) ? "https://connect.tlbx.ai" : value, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new ArgumentException("Connect requires an HTTPS origin (HTTP is allowed only on loopback).");
        return uri;
    }
}
