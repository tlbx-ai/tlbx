using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Share;
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

            // Preview ports are a separate capability surface. A route name or
            // Referer alone is never proof of access to a private preview.
            if (path.StartsWith("/webpreview/", StringComparison.OrdinalIgnoreCase)
                || (previewOriginService.IsPreviewRequest(context)
                    && WebPreviewProxyMiddleware.ShouldProxyPreviewLeak(context.Request, path)))
            {
                var scopedAccess = !string.IsNullOrEmpty(authSettings.PasswordHash)
                    && PreviewProxyAuthorization.TryAuthorize(context, previewRegistry);
                var method = authService.AuthenticateRequest(context.Request);
                RequestAccessContext.SetApiKeyAuthenticated(context, method == RequestAuthMethod.ApiKey);
                var fullAccess = method != RequestAuthMethod.None && RequestOriginPolicy.Allows(context.Request);
                if (!scopedAccess && !fullAccess)
                {
                    AuthService.MarkAuthenticationRequired(context.Response);
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                RequestAccessContext.SetFullUser(context, fullAccess && !scopedAccess);
                await next();
                return;
            }

            if (!RequestOriginPolicy.Allows(context.Request)
                && !AllowsBrowserPreviewWebSocket(context.Request, previewRegistry))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (IsPublicRequest(context.Request))
            {
                await next();
                return;
            }

            if (string.IsNullOrEmpty(authSettings.PasswordHash))
            {
                AuthService.MarkAuthenticationRequired(context.Response);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("Set a password locally with mt --set-password before using tlbx.", context.RequestAborted);
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
                RequestAccessContext.SetApiKeyAuthenticated(context, authentication.Method == RequestAuthMethod.ApiKey);
                if (authentication.Method == RequestAuthMethod.SessionCookie && !context.WebSockets.IsWebSocketRequest)
                {
                    var freshToken = authService.RenewSessionToken(authentication.SessionTokenId!);
                    context.Response.Cookies.Append(
                        AuthService.SessionCookieName,
                        freshToken,
                        GetSessionCookieOptions());
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

    private static CookieOptions GetSessionCookieOptions() => new()
    {
        HttpOnly = true,
        // Preview access uses a separate route-scoped credential.
        SameSite = SameSiteMode.Lax,
        Secure = true,
        Path = "/",
        MaxAge = AuthService.SessionTokenValidity
    };

    internal static bool IsPublicRequest(HttpRequest request)
    {
        var path = request.Path.Value ?? "";
        return (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            ? IsPublicPath(path)
            : HttpMethods.IsPost(request.Method)
              && (path == "/api/auth/login" || path == "/api/share/claim");
    }

    internal static bool IsPublicPath(string path)
    {
        return path == "/login" ||
               path == "/login.html" ||
               path == "/shared" ||
               path.StartsWith("/shared/", StringComparison.Ordinal) ||
               path == "/trust" ||
               path == "/trust.html" ||
               path == "/api/bootstrap/login" ||
               path == "/api/certificate/info" ||
               path == "/api/certificate/download/pem" ||
               path == "/api/certificate/download/crt" ||
               path == "/api/certificate/download/mobileconfig" ||
               path.StartsWith("/css/", StringComparison.Ordinal) ||
               path.StartsWith("/js/", StringComparison.Ordinal) ||
               path.StartsWith("/fonts/", StringComparison.Ordinal) ||
               path.StartsWith("/locales/", StringComparison.Ordinal) ||
               path.StartsWith("/img/", StringComparison.Ordinal) ||
               path == "/favicon.svg" ||
               path == "/favicon.ico" ||
               path == "/site.webmanifest" ||
               path == "/sw.js" ||
               path == "/android-chrome-192x192.png" ||
               path == "/android-chrome-512x512.png" ||
               path == "/apple-touch-icon.png" ||
               path == "/favicon-16x16.png" ||
               path == "/favicon-32x32.png";
    }

    private static bool IsShareProtectedPath(string path)
    {
        return path == "/api/share/bootstrap" ||
               path == "/ws/share/state" ||
               path == "/ws/share/mux";
    }

}
