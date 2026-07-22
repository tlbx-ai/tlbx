using System.Globalization;
using System.Text;
using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Models.WebPreview;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Sessions;

namespace Ai.Tlbx.MidTerm.Services.WebPreview;

public static partial class WebPreviewEndpoints
{
    public static void MapWebPreviewEndpoints(
        WebApplication app,
        WebPreviewService webPreviewService,
        TtyHostSessionManager sessionManager,
        BrowserCommandService browserCommandService)
    {
        MapPreviewSessionEndpoints(app, webPreviewService);
        MapTargetEndpoints(app, webPreviewService, sessionManager);
        MapCookieEndpoints(app, webPreviewService);
        MapActionEndpoints(app, webPreviewService, sessionManager, browserCommandService);
        MapProxyLogEndpoints(app, webPreviewService);
    }

    private static void MapPreviewSessionEndpoints(WebApplication app, WebPreviewService service)
    {
        app.MapGet("/api/webpreview/previews", (string sessionId) =>
        {
            return Results.Json(
                service.ListPreviewSessions(sessionId),
                AppJsonContext.Default.WebPreviewSessionListResponse);
        });

        app.MapPost("/api/webpreview/previews", (WebPreviewSessionRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.SessionId))
            {
                return Results.BadRequest("sessionId required");
            }

            var response = service.EnsurePreviewSession(request.SessionId, request.PreviewName);
            return Results.Json(response, AppJsonContext.Default.WebPreviewSessionInfo);
        });

        app.MapDelete("/api/webpreview/previews", (string sessionId, string? previewName) =>
        {
            return service.DeletePreviewSession(sessionId, previewName)
                ? Results.Ok()
                : Results.BadRequest("Failed to delete preview.");
        });
    }

    private static void MapTargetEndpoints(WebApplication app, WebPreviewService service, TtyHostSessionManager sessionManager)
    {
        app.MapGet("/api/webpreview/target", (string sessionId, string? previewName) =>
        {
            var response = BuildTargetResponse(service, sessionId, previewName);
            return Results.Json(response, AppJsonContext.Default.WebPreviewTargetResponse);
        });

        app.MapPut("/api/webpreview/target", (WebPreviewTargetRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.SessionId))
            {
                return Results.BadRequest("sessionId required");
            }

            if (!service.SetTarget(request.SessionId, request.PreviewName, request.Url))
            {
                return Results.BadRequest("Invalid URL. Must be http://, https://, or a local file:/// URL, and cannot point to this server.");
            }

            WriteTlbxCliToActiveSessions(sessionManager);

            var response = BuildTargetResponse(service, request.SessionId, request.PreviewName);
            return Results.Json(response, AppJsonContext.Default.WebPreviewTargetResponse);
        });

        app.MapDelete("/api/webpreview/target", (string sessionId, string? previewName) =>
        {
            service.ClearTarget(sessionId, previewName);
            return Results.Ok();
        });
    }

    private static void MapCookieEndpoints(WebApplication app, WebPreviewService service)
    {
        app.MapGet("/api/webpreview/cookies", (string sessionId, string? previewName) =>
        {
            var response = service.GetCookies(sessionId, previewName);
            return Results.Json(response, AppJsonContext.Default.WebPreviewCookiesResponse);
        });

        app.MapPost("/api/webpreview/cookies", (string sessionId, string? previewName, WebPreviewCookieSetRequest request) =>
        {
            if (!service.TryGetPreviewRouteKey(sessionId, previewName, out var routeKey)
                || !service.SetCookieFromRaw(routeKey, request.Raw))
            {
                return Results.BadRequest("Invalid cookie format.");
            }

            var response = service.GetCookies(sessionId, previewName);
            return Results.Json(response, AppJsonContext.Default.WebPreviewCookiesResponse);
        });

        app.MapDelete("/api/webpreview/cookies", (string sessionId, string? previewName, string name, string? path, string? domain) =>
        {
            if (!service.DeleteCookie(sessionId, previewName, name, path, domain))
            {
                return Results.BadRequest("Failed to delete cookie.");
            }

            var response = service.GetCookies(sessionId, previewName);
            return Results.Json(response, AppJsonContext.Default.WebPreviewCookiesResponse);
        });

        app.MapPost("/api/webpreview/cookies/clear", (string sessionId, string? previewName) =>
        {
            if (!service.ClearAllCookies(sessionId, previewName))
                return Results.BadRequest("No active target.");
            return Results.Ok();
        });
    }

    private static void MapActionEndpoints(
        WebApplication app,
        WebPreviewService service,
        TtyHostSessionManager sessionManager,
        BrowserCommandService browserCommandService)
    {
        app.MapPost("/api/webpreview/state/clear", (string sessionId, string? previewName) =>
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Results.BadRequest("sessionId required");
            }

            if (!service.ClearState(sessionId, previewName))
            {
                return Results.BadRequest("Failed to clear preview state.");
            }

            var response = BuildTargetResponse(service, sessionId, previewName);
            return Results.Json(response, AppJsonContext.Default.WebPreviewTargetResponse);
        });

        app.MapPost("/api/webpreview/reload", async (WebPreviewReloadRequest request, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.SessionId))
            {
                return Results.BadRequest("sessionId required");
            }

            var mode = NormalizeReloadMode(request.Mode);
            if (mode == "hard")
            {
                if (!service.HardReload(request.SessionId, request.PreviewName))
                {
                    return Results.BadRequest("No active target.");
                }
            }

            if (mode is "soft" or "force" or "hard")
            {
                try
                {
                    await browserCommandService.ExecuteCommandAsync(
                        new BrowserCommandRequest
                        {
                            Command = "reload",
                            Value = mode,
                            SessionId = request.SessionId,
                            PreviewName = request.PreviewName
                        },
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }

            return Results.Ok();
        });

        app.MapPost("/api/webpreview/snapshot", async (
            WebPreviewSnapshotRequest request,
            CancellationToken cancellationToken) =>
        {
            var session = sessionManager.GetSession(request.SessionId);
            if (session is null)
                return Results.NotFound("Session not found");

            var cwd = session.CurrentDirectory;
            if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd))
                return Results.BadRequest("Session has no valid working directory");

            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var snapshotDir = TlbxDirectory.EnsureSubdirectory(cwd, $"snapshot_{ts}");
            var cssDir = Path.Combine(snapshotDir, "css");
            Directory.CreateDirectory(cssDir);

            // Process HTML — strip proxy artifacts, decode ext URLs
            var html = WebPreviewHtmlSnapshotSanitizer.StripProxyArtifacts(request.Html);
            html = WebPreviewHtmlSnapshotSanitizer.DecodeExtUrls(html);

            // Download CSS files and rewrite hrefs
            foreach (var cssUrl in request.CssUrls.Distinct(StringComparer.Ordinal))
            {
                if (!TryExtractProxyPath(cssUrl, out var routeKey, out var proxyPath))
                    continue;

                string upstreamUrl;
                if (proxyPath.StartsWith("/_ext", StringComparison.Ordinal))
                {
                    var qIdx = proxyPath.IndexOf("?u=", StringComparison.Ordinal);
                    if (qIdx < 0) continue;
                    var encoded = proxyPath[(qIdx + 3)..];
                    try { upstreamUrl = Uri.UnescapeDataString(encoded); }
                    catch { continue; }
                }
                else if (service.GetTargetUriByRouteKey(routeKey) is { } targetUri)
                {
                    upstreamUrl = BuildUpstreamUrl(targetUri, proxyPath);
                }
                else
                {
                    continue;
                }

                var rawName = proxyPath.Split('?')[0].Split('/').LastOrDefault() ?? "style";
                var baseName = Path.GetFileNameWithoutExtension(rawName);
                var fileName = SanitizeFileName(baseName) + ".css";

                var finalFileName = fileName;
                var counter = 1;
                while (File.Exists(Path.Combine(cssDir, finalFileName)))
                {
                    finalFileName = string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileNameWithoutExtension(fileName)}_{counter}.css");
                    counter++;
                }

                try
                {
                    var cssContent = await service.GetHttpClient(routeKey).GetStringAsync(upstreamUrl, cancellationToken);
                    await File.WriteAllTextAsync(Path.Combine(cssDir, finalFileName), cssContent, cancellationToken);

                    html = html.Replace(cssUrl, $"css/{finalFileName}", StringComparison.Ordinal);

                    var pathNoQuery = proxyPath.Split('?')[0];
                    html = html.Replace($"\"{pathNoQuery}\"", $"\"css/{finalFileName}\"", StringComparison.Ordinal);
                    html = html.Replace($"'{pathNoQuery}'", $"'css/{finalFileName}'", StringComparison.Ordinal);
                }
                catch
                {
                    // Skip failed assets — snapshot is still useful without them
                }
            }

            await File.WriteAllTextAsync(Path.Combine(snapshotDir, "index.html"), html, cancellationToken);

            return Results.Json(
                new WebPreviewSnapshotResponse { SnapshotPath = snapshotDir },
                AppJsonContext.Default.WebPreviewSnapshotResponse);
        });
    }

    private static void MapProxyLogEndpoints(WebApplication app, WebPreviewService service)
    {
        app.MapGet("/api/webpreview/proxylog", (string sessionId, string? previewName, int? limit) =>
        {
            var entries = service.GetLogEntries(sessionId, previewName, limit ?? 100);
            return Results.Json(entries, AppJsonContext.Default.ListWebPreviewProxyLogEntry);
        });

        app.MapGet("/api/webpreview/proxylog/summary", (string sessionId, string? previewName, int? limit) =>
        {
            var entries = service.GetLogEntries(sessionId, previewName, limit ?? 100);
            return Results.Text(BuildProxyLogSummaryText(entries));
        });

        app.MapDelete("/api/webpreview/proxylog", (string sessionId, string? previewName) =>
        {
            service.ClearLog(sessionId, previewName);
            return Results.Ok();
        });
    }

    internal static string BuildProxyLogSummaryText(IReadOnlyList<WebPreviewProxyLogEntry> entries)
    {
        if (entries.Count == 0)
        {
            return "proxylog summary\nentries: 0\nerrors: 0\n";
        }

        var statusBuckets = entries
            .GroupBy(entry => entry.StatusCode)
            .OrderBy(group => group.Key)
            .Select(group => string.Create(CultureInfo.InvariantCulture, $"{group.Key}:{group.Count()}"));
        var failures = entries
            .Where(entry => entry.StatusCode >= 400 || !string.IsNullOrWhiteSpace(entry.Error))
            .OrderByDescending(entry => entry.Timestamp)
            .Take(8)
            .ToArray();
        var websocketEntries = entries
            .Where(entry => string.Equals(entry.Method, "WS-UPGRADE", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var slowest = entries
            .OrderByDescending(entry => entry.DurationMs)
            .Take(5)
            .ToArray();
        var redirected = entries.Count(entry => entry.StatusCode is >= 300 and <= 399);
        var withCookies = entries.Count(entry => !string.IsNullOrWhiteSpace(entry.RequestCookies));

        var lines = new List<string>
        {
            "proxylog summary",
            string.Create(CultureInfo.InvariantCulture, $"entries: {entries.Count}"),
            string.Create(CultureInfo.InvariantCulture, $"errors: {failures.Length}"),
            $"status: {string.Join(" ", statusBuckets)}",
            string.Create(CultureInfo.InvariantCulture, $"websocket: {websocketEntries.Length} total, {websocketEntries.Count(entry => entry.StatusCode == 101)} connected, {websocketEntries.Count(entry => entry.StatusCode >= 400)} failed"),
            string.Create(CultureInfo.InvariantCulture, $"redirects: {redirected}"),
            string.Create(CultureInfo.InvariantCulture, $"requests with cookies: {withCookies}"),
            $"slowest: {string.Join(" | ", slowest.Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.StatusCode} {entry.DurationMs}ms {TrimForSummary(entry.UpstreamUrl, 96)}")))}"
        };

        if (failures.Length > 0)
        {
            lines.Add("failures:");
            foreach (var failure in failures)
            {
                var error = string.IsNullOrWhiteSpace(failure.Error) ? "" : $" error={TrimForSummary(failure.Error, 80)}";
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"  {failure.StatusCode} {failure.Type} {TrimForSummary(failure.UpstreamUrl, 120)}{error}"));
            }
        }

        return string.Join('\n', lines) + "\n";
    }

    private static string TrimForSummary(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(none)";
        }

        var compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= maxLength ? compact : compact[..Math.Max(0, maxLength - 1)] + "...";
    }

    /// <summary>
    /// Extracts the path+query portion from an absolute browser URL, returning
    /// true only if it passes through the /webpreview proxy prefix.
    /// </summary>
    private static bool TryExtractProxyPath(string absoluteUrl, out string routeKey, out string proxyPath)
    {
        routeKey = "";
        proxyPath = "";
        if (!Uri.TryCreate(absoluteUrl, UriKind.Absolute, out var uri))
            return false;

        if (!WebPreviewProxyMiddleware.TryParseProxyRoute(uri.AbsolutePath, out routeKey, out var remainingPath))
            return false;

        proxyPath = remainingPath + uri.Query;
        return true;
    }

    private static WebPreviewTargetResponse BuildTargetResponse(WebPreviewService service, string sessionId, string? previewName)
    {
        var preview = service.EnsurePreviewSession(sessionId, previewName);
        return new WebPreviewTargetResponse
        {
            SessionId = preview.SessionId,
            PreviewName = preview.PreviewName,
            RouteKey = preview.RouteKey,
            Url = preview.Url,
            Active = preview.Active,
            TargetRevision = preview.TargetRevision
        };
    }

    private static void WriteTlbxCliToActiveSessions(TtyHostSessionManager sessionManager)
    {
        var sessions = sessionManager.GetAllSessions();
        foreach (var session in sessions)
        {
            var cwd = session.CurrentDirectory;
            if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd))
            {
                TlbxDirectory.Ensure(cwd);
            }
        }
    }

    /// <summary>
    /// Strips the /webpreview prefix from a proxy path and prepends the upstream origin.
    /// e.g. /webpreview/typo3temp/style.css → http://upstream.host/typo3temp/style.css
    /// </summary>
    private static string BuildUpstreamUrl(Uri targetUri, string proxyPath)
    {
        return targetUri.GetLeftPart(UriPartial.Authority) + proxyPath;
    }

    /// <summary>
    /// Returns a filesystem-safe version of a filename, replacing invalid chars with underscores.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 || c == '?' ? '_' : c);
        }
        var result = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(result) ? "style" : result;
    }

    private static string NormalizeReloadMode(string? mode)
    {
        if (string.Equals(mode, "hard", StringComparison.OrdinalIgnoreCase))
        {
            return "hard";
        }

        if (string.Equals(mode, "force", StringComparison.OrdinalIgnoreCase))
        {
            return "force";
        }

        return "soft";
    }

}
