using System.Net;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Share;
using Ai.Tlbx.MidTerm.Services.Updates;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Startup;

public static class AuthMiddleware
{
    public static void ConfigureAuthMiddleware(
        WebApplication app,
        SettingsService settingsService,
        AuthService authService,
        ShareGrantService shareGrantService,
        BrowserPreviewOriginService previewOriginService,
        BrowserPreviewRegistry previewRegistry)
    {
        app.Use(async (context, next) =>
        {
            var authSettings = settingsService.Load();
            var path = context.Request.Path.Value ?? "";

            RequestAccessContext.SetFullUser(context, false);
            RequestAccessContext.SetShareAccess(context, null);

            var shareCookie = context.Request.Cookies[ShareGrantService.ShareCookieName];
            if (shareGrantService.TryResolveCookie(shareCookie, out var shareAccess))
            {
                RequestAccessContext.SetShareAccess(context, shareAccess);
            }

            if (!authSettings.AuthenticationEnabled || string.IsNullOrEmpty(authSettings.PasswordHash))
            {
                RequestAccessContext.SetFullUser(context, true);
                await next();
                return;
            }

            if (IsPublicPath(path))
            {
                await next();
                return;
            }

            if (path == "/api/shutdown" && IsLoopback(context))
            {
                await next();
                return;
            }

            if (AllowsPreviewOriginProxyRequest(context.Request, previewOriginService))
            {
                await next();
                return;
            }

            if (AllowsBrowserPreviewWebSocket(context.Request, previewRegistry))
            {
                await next();
                return;
            }

            if (IsShareProtectedPath(path))
            {
                if (shareAccess is not null)
                {
                    await next();
                    return;
                }

                AuthService.MarkAuthenticationRequired(context.Response);
                context.Response.StatusCode = 401;
                return;
            }

            var authentication = authService.AuthenticateRequestWithContext(context.Request);
            if (authentication.Method != RequestAuthMethod.None)
            {
                RequestAccessContext.SetFullUser(context, true);
                if (authentication.Method == RequestAuthMethod.SessionCookie && !context.WebSockets.IsWebSocketRequest)
                {
                    var freshToken = authService.RenewSessionToken(authentication.SessionTokenId!);
                    context.Response.Cookies.Append(
                        AuthService.SessionCookieName,
                        freshToken,
                        GetSessionCookieOptions(settingsService));
                }
                await next();
                return;
            }

            if (path.StartsWith("/api/", StringComparison.Ordinal) || path.StartsWith("/ws/", StringComparison.Ordinal))
            {
                AuthService.MarkAuthenticationRequired(context.Response);
                context.Response.StatusCode = 401;
                return;
            }

            context.Response.Redirect("/login.html");
        });
    }

    internal static bool AllowsBrowserPreviewWebSocket(
        HttpRequest request,
        BrowserPreviewRegistry? previewRegistry)
    {
        if (previewRegistry is null
            || !request.Path.Equals("/ws/browser", StringComparison.Ordinal)
            || !request.Query.TryGetValue("previewId", out var previewIds)
            || !request.Query.TryGetValue("token", out var tokens))
        {
            return false;
        }

        return previewRegistry.TryValidate(
            previewIds.FirstOrDefault(),
            tokens.FirstOrDefault(),
            out _);
    }

    internal static bool AllowsPreviewOriginProxyRequest(
        HttpRequest request,
        BrowserPreviewOriginService? previewOriginService)
    {
        if (previewOriginService is null
            || !previewOriginService.IsEnabled
            || request.Host.Port != previewOriginService.PreviewPort)
        {
            return false;
        }

        var path = request.Path.Value ?? "/";
        return path.StartsWith("/webpreview/", StringComparison.OrdinalIgnoreCase)
            || WebPreviewProxyMiddleware.ShouldProxyPreviewLeak(request, path);
    }

    private static CookieOptions GetSessionCookieOptions(SettingsService settingsService) => new()
    {
        HttpOnly = true,
        // Sandboxed previews use an opaque origin, so their subresource requests only
        // carry the auth cookie when dev mode intentionally relaxes SameSite.
        SameSite = UpdateService.IsDevEnvironment || settingsService.Load().DevMode
            ? SameSiteMode.None
            : SameSiteMode.Lax,
        Secure = true,
        Path = "/",
        MaxAge = AuthService.SessionTokenValidity
    };

    internal static bool IsPublicPath(string path)
    {
        return path == "/login" ||
               path == "/login.html" ||
               path == "/shared" ||
               path.StartsWith("/shared/", StringComparison.Ordinal) ||
               path == "/trust" ||
               path == "/trust.html" ||
               path == "/swagger" ||
               path.StartsWith("/swagger/", StringComparison.Ordinal) ||
               path.StartsWith("/openapi/", StringComparison.Ordinal) ||
               path == "/api/health" ||
               path == "/api/version" ||
               path == "/api/paths" ||
               path == "/api/security/status" ||
               path == "/api/share/claim" ||
               path.StartsWith("/api/certificate/", StringComparison.Ordinal) ||
               path.StartsWith("/api/auth/", StringComparison.Ordinal) ||
               path.StartsWith("/css/", StringComparison.Ordinal) ||
               path.StartsWith("/js/", StringComparison.Ordinal) ||
               path.StartsWith("/fonts/", StringComparison.Ordinal) ||
               path.StartsWith("/locales/", StringComparison.Ordinal) ||
               path.EndsWith(".ico", StringComparison.Ordinal) ||
               path.EndsWith(".png", StringComparison.Ordinal) ||
               path.EndsWith(".webmanifest", StringComparison.Ordinal) ||
               path.EndsWith(".woff", StringComparison.Ordinal) ||
               path.EndsWith(".woff2", StringComparison.Ordinal);
    }

    private static bool IsShareProtectedPath(string path)
    {
        return path == "/api/share/bootstrap" ||
               path == "/ws/share/state" ||
               path == "/ws/share/mux";
    }

    private static bool IsLoopback(HttpContext context)
    {
        var remoteIp = context.Connection.RemoteIpAddress;
        return remoteIp is not null && IPAddress.IsLoopback(remoteIp);
    }
}
