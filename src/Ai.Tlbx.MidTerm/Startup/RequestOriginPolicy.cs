namespace Ai.Tlbx.MidTerm.Startup;

internal static class RequestOriginPolicy
{
    public static bool Allows(HttpRequest request)
    {
        // An opaque preview may use only its explicitly scoped capability routes,
        // never installation cookies/API access, including otherwise read-only GETs.
        if (request.Headers.Origin.ToString().Equals("null", StringComparison.OrdinalIgnoreCase)) return false;
        var isWebSocket = request.Path.StartsWithSegments("/ws", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.Headers.Upgrade, "websocket", StringComparison.OrdinalIgnoreCase);
        if (!isWebSocket && (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
            || HttpMethods.IsOptions(request.Method))) return true;

        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            // Native API clients do not send Origin. Browser cross-site requests
            // must not gain that exemption by omitting the header.
            return !string.Equals(request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase);
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.PathAndQuery == "/"
            && string.IsNullOrEmpty(uri.Fragment)
            && string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.IdnHost, request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == (request.Host.Port ?? (request.IsHttps ? 443 : 80));
    }
}
