namespace Ai.Tlbx.MidTerm.Services.Connect;

internal static class ConnectRequestPolicy
{
    private static readonly HashSet<string> BlockedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Host", "Origin", "Referer", "Connection",
        "Upgrade", "Keep-Alive", "Transfer-Encoding", "TE", "Trailer", "Content-Length",
        "Sec-WebSocket-Key", "Sec-WebSocket-Version", "Sec-WebSocket-Extensions", "Sec-WebSocket-Protocol",
        "Forwarded", "X-Forwarded-Host", "X-Forwarded-For", "X-Forwarded-Proto"
    };

    public static Uri ResolvePath(Uri origin, string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 16384 || path[0] != '/' || path.StartsWith("//", StringComparison.Ordinal)
            || path.Contains('\\', StringComparison.Ordinal) || path.Contains('#', StringComparison.Ordinal) || path.Any(char.IsControl))
            throw new InvalidDataException("Only local tlbx paths are allowed.");
        var queryIndex = path.IndexOf('?', StringComparison.Ordinal);
        var pathname = queryIndex < 0 ? path : path[..queryIndex];
        for (var i = 0; i < 3; i++)
        {
            pathname = Uri.UnescapeDataString(pathname);
            if (pathname.Contains('\\', StringComparison.Ordinal) || pathname.Contains('?', StringComparison.Ordinal)
                || pathname.Contains('#', StringComparison.Ordinal) || pathname.Any(char.IsControl)
                || pathname.StartsWith("//", StringComparison.Ordinal)
                || pathname.Split('/').Any(segment => segment is "." or ".."))
                throw new InvalidDataException("Invalid Connect request path.");
        }
        if (pathname.Equals("/api/connect", StringComparison.OrdinalIgnoreCase)
            || pathname.StartsWith("/api/connect/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Connect configuration requires a local login.");
        var uri = new Uri(origin, path);
        if (!string.Equals(uri.Authority, origin.Authority, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme != origin.Scheme)
            throw new InvalidDataException("Connect request escaped the local origin.");
        return uri;
    }

    public static bool AllowHeader(string name) => !BlockedHeaders.Contains(name)
        && !name.StartsWith("Sec-Fetch-", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    public static Uri NormalizeServerUrl(string? value)
    {
        if (!Uri.TryCreate(string.IsNullOrWhiteSpace(value) ? "https://connect.tlbx.ai" : value, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new ArgumentException("Connect requires an HTTPS origin (HTTP is allowed only on loopback).");
        return uri;
    }
}
