using System.Globalization;
using System.Text;
using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Services.Tmux;
using Ai.Tlbx.MidTerm.Services.WebPreview;

namespace Ai.Tlbx.MidTerm.Services.Browser;

public static class BrowserEndpoints
{
    public static void MapBrowserEndpoints(
        WebApplication app,
        BrowserCommandService commandService,
        BrowserPreviewRegistry previewRegistry,
        BrowserPreviewOwnerService previewOwnerService,
        BrowserPreviewOriginService previewOriginService,
        TtyHostSessionManager sessionManager,
        WebPreviewService webPreviewService,
        BrowserUiBridge? uiBridge = null)
    {
        MapPreviewClientEndpoint(app, previewRegistry, previewOwnerService, previewOriginService, webPreviewService);
        MapStatusEndpoint(app, commandService, webPreviewService, uiBridge);
        MapCliEndpoint(app, commandService, sessionManager, webPreviewService, uiBridge);
        MapJsonEndpoints(app, commandService, sessionManager, webPreviewService, uiBridge);

        if (uiBridge is not null)
        {
            MapUiEndpoints(app, commandService, uiBridge, webPreviewService);
        }
    }

    private static void MapStatusEndpoint(
        WebApplication app,
        BrowserCommandService commandService,
        WebPreviewService webPreviewService,
        BrowserUiBridge? uiBridge)
    {
        app.MapGet("/api/browser/status", (string? sessionId, string? previewName, string? previewId) =>
        {
            var targetUrl = !string.IsNullOrWhiteSpace(sessionId)
                ? webPreviewService.GetTargetUrl(sessionId, previewName)
                : null;
            var status = commandService.GetStatus(
                targetUrl,
                sessionId,
                previewName,
                previewId,
                uiBridge?.ConnectedBrowserCount ?? 0);
            return Results.Json(status, AppJsonContext.Default.BrowserStatusResponse);
        });

        app.MapGet("/api/browser/status-text", (string? sessionId, string? previewName, string? previewId) =>
        {
            var targetUrl = !string.IsNullOrWhiteSpace(sessionId)
                ? webPreviewService.GetTargetUrl(sessionId, previewName)
                : null;
            var status = commandService.GetStatusText(
                targetUrl,
                sessionId,
                previewName,
                previewId,
                uiBridge?.ConnectedBrowserCount ?? 0);
            return Results.Text(status);
        });
    }

    private static void MapPreviewClientEndpoint(
        WebApplication app,
        BrowserPreviewRegistry previewRegistry,
        BrowserPreviewOwnerService previewOwnerService,
        BrowserPreviewOriginService previewOriginService,
        WebPreviewService webPreviewService)
    {
        app.MapPost("/api/browser/preview-client", (BrowserPreviewClientRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.SessionId))
            {
                return Results.BadRequest("sessionId required");
            }

            var preview = webPreviewService.EnsurePreviewSession(request.SessionId, request.PreviewName);
            var browserId = BrowserIdentity.Build(
                ctx.Request.Cookies["mt-client-id"],
                request.TabId ?? ctx.Request.Query["tabId"].FirstOrDefault());
            var created = previewRegistry.Create(
                preview.SessionId,
                preview.PreviewName,
                preview.RouteKey,
                browserId);
            previewOwnerService.ClaimIfMissing(
                preview.SessionId,
                preview.PreviewName,
                browserId);
            var response = new BrowserPreviewClientResponse
            {
                SessionId = created.SessionId,
                PreviewName = created.PreviewName,
                RouteKey = created.RouteKey,
                PreviewId = created.PreviewId,
                PreviewToken = created.PreviewToken,
                Origin = previewOriginService.GetOrigin(ctx.Request)
            };
            return Results.Json(response, AppJsonContext.Default.BrowserPreviewClientResponse);
        });
    }

    private static void MapUiEndpoints(
        WebApplication app,
        BrowserCommandService commandService,
        BrowserUiBridge uiBridge,
        WebPreviewService webPreviewService)
    {
        app.MapPost("/api/browser/detach", (Models.WebPreview.WebPreviewSessionRequest request) =>
        {
            return uiBridge.RequestDetach(
                NormalizeOptional(request.SessionId),
                NormalizeOptional(request.PreviewName),
                out var error)
                ? Results.Ok()
                : Results.Text(error + "\n", statusCode: 409);
        });

        app.MapPost("/api/browser/dock", (Models.WebPreview.WebPreviewSessionRequest request) =>
        {
            return uiBridge.RequestDock(
                NormalizeOptional(request.SessionId),
                NormalizeOptional(request.PreviewName),
                out var error)
                ? Results.Ok()
                : Results.Text(error + "\n", statusCode: 409);
        });

        app.MapPost("/api/browser/viewport", (Models.Browser.ViewportRequest request) =>
        {
            return uiBridge.RequestViewport(
                NormalizeOptional(request.SessionId),
                NormalizeOptional(request.PreviewName),
                request.Width,
                request.Height,
                out var error)
                ? Results.Ok()
                : Results.Text(error + "\n", statusCode: 409);
        });

        app.MapPost("/api/browser/open", async (Models.WebPreview.WebPreviewTargetRequest request, CancellationToken cancellationToken) =>
        {
            var sessionId = NormalizeOptional(request.SessionId);
            var previewName = NormalizeOptional(request.PreviewName);
            var url = request.Url ?? "";
            var activateSession = request.ActivateSession ?? true;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Results.BadRequest("sessionId required");
            }

            if (!webPreviewService.SetTarget(sessionId, previewName, url))
            {
                return Results.BadRequest("Invalid URL. Must be http://, https://, or a local file:/// URL, and cannot point to this server.");
            }

            if (!uiBridge.RequestOpen(
                sessionId,
                previewName,
                url,
                activateSession,
                out var error))
            {
                return Results.Text(error + "\n", statusCode: 409);
            }

            var status = await commandService.WaitForControllableAsync(
                url,
                sessionId,
                previewName,
                requireClientConnectedAfterUtc: DateTimeOffset.UtcNow,
                requireVisibleClient: true,
                connectedUiClientCountProvider: () => uiBridge.ConnectedBrowserCount,
                cancellationToken: cancellationToken);

            var statusText = commandService.GetStatusText(
                url,
                sessionId,
                previewName,
                connectedUiClientCount: uiBridge.ConnectedBrowserCount);

            var ready = status.Controllable && status.DefaultClient?.IsVisible == true;
            return ready
                ? Results.Text(statusText)
                : Results.Text(statusText, statusCode: 409);
        });
    }

    private static void MapCliEndpoint(
        WebApplication app,
        BrowserCommandService commandService,
        TtyHostSessionManager sessionManager,
        WebPreviewService webPreviewService,
        BrowserUiBridge? uiBridge)
    {
        app.MapPost("/api/browser", async (HttpContext ctx) =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            var body = ms.ToArray();

            var args = TmuxCommandParser.ParseNullDelimitedArgs(body);
            if (args.Count == 0)
            {
                BrowserLog.Error($"Empty request ({body.Length} bytes)");
                return Results.Text("usage: mtbrowser <command> [args...]\n\nCommands:\n  query <selector> [--depth N] [--text]\n  click <selector>\n  scroll [selector] [deltaY|top|bottom|left|right] [deltaX]\n  fill <selector> <value>\n  exec <js-code>\n  screenshot [--session <id>]\n  snapshot --session <id>\n  wait <selector> [--timeout N]\n  navigate <url>\n  reload [--force|--hard]\n  outline [depth]     Page structure (tag+id+class tree)\n  attrs <selector>    Element attributes (no children)\n  css <selector> <props>  Computed CSS (comma-separated)\n  log [error|warn|all]    Console log buffer\n  links               All links on page\n  submit [selector]   Submit form (default: first form)\n  forms [selector]    Form structure and values\n  url                 Current upstream page URL\n  clearcookies        Clear browser-side cookies in iframe\n  clearstate          Clear browser-side cookies and storage in iframe\n  status              Preview bridge status\n  claim               Explicitly claim preview ownership for this browser UI\n  capabilities [--json]  Compact command/capability discovery\n  inspect [--screenshot] Compact page/status/proxy diagnostic bundle\n  proxylog-summary [--limit N] Compact proxy request summary\n", statusCode: 400);
            }

            var command = args[0].ToLowerInvariant();

            if (command == "status")
            {
                var sessionId = GetFlagValue(args, "--session");
                var previewName = GetFlagValue(args, "--preview");
                var previewId = GetFlagValue(args, "--preview-id");
                var targetUrl = sessionId is not null
                    ? webPreviewService.GetTargetUrl(sessionId, previewName)
                    : null;
                var status = commandService.GetStatusText(
                    targetUrl,
                    sessionId,
                    previewName,
                    previewId,
                    uiBridge?.ConnectedBrowserCount ?? 0).TrimEnd('\n', '\r');
                return Results.Text(status);
            }

            if (command == "claim")
            {
                var sessionId = GetFlagValue(args, "--session");
                var previewName = GetFlagValue(args, "--preview");
                if (uiBridge is null)
                {
                    return Results.Text("Browser UI bridge is not available.\n", statusCode: 409);
                }

                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    return Results.Text("sessionId required\n", statusCode: 400);
                }

                return uiBridge.RequestClaim(sessionId, previewName, out var error)
                    ? Results.Text($"claimed preview '{previewName ?? WebPreviewService.DefaultPreviewName}' in session '{sessionId}'\n")
                    : Results.Text(error + "\n", statusCode: 409);
            }

            if (command == "capabilities")
            {
                var sessionId = GetFlagValue(args, "--session");
                var previewName = GetFlagValue(args, "--preview");
                var targetUrl = sessionId is not null
                    ? webPreviewService.GetTargetUrl(sessionId, previewName)
                    : null;
                var capabilities = BuildCapabilitiesResponse(
                    commandService,
                    webPreviewService,
                    uiBridge,
                    sessionId,
                    previewName,
                    targetUrl);

                if (HasFlag(args, "--json"))
                {
                    return Results.Json(capabilities, AppJsonContext.Default.BrowserCapabilitiesResponse);
                }

                return Results.Text(BuildCapabilitiesText(capabilities));
            }

            if (command == "proxylog-summary")
            {
                var sessionId = GetFlagValue(args, "--session");
                var previewName = GetFlagValue(args, "--preview");
                var limit = GetIntFlag(args, "--limit") ?? 100;
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    return Results.Text("sessionId required\n", statusCode: 400);
                }

                var entries = webPreviewService.GetLogEntries(sessionId, previewName, limit);
                return Results.Text(WebPreviewEndpoints.BuildProxyLogSummaryText(entries));
            }

            if (command == "inspect")
            {
                var sessionId = GetFlagValue(args, "--session");
                var previewName = GetFlagValue(args, "--preview");
                var includeScreenshot = HasFlag(args, "--screenshot");
                var text = await BuildInspectTextAsync(
                    commandService,
                    sessionManager,
                    webPreviewService,
                    uiBridge,
                    sessionId,
                    previewName,
                    includeScreenshot,
                    ctx.RequestAborted);
                return Results.Text(text);
            }

            var request = ParseCliArgs(command, args);
            if (request is null)
            {
                return Results.Text($"unknown command: {command}\n", statusCode: 400);
            }

            var result = await commandService.ExecuteCommandAsync(request, ctx.RequestAborted);

            if (command is "snapshot" or "screenshot" && result.Success && result.Result is not null)
            {
                var saved = await SaveResultToDiskAsync(command, result, request, sessionManager, webPreviewService);
                if (saved is not null)
                {
                    return Results.Text(saved + "\n");
                }
            }

            if (!result.Success)
            {
                return Results.Text(result.Error ?? "command failed\n", statusCode: 400);
            }

            var output = result.Result ?? "";
            if (!output.EndsWith('\n'))
                output += "\n";
            return Results.Text(output);
        });
    }

    private static void MapJsonEndpoints(
        WebApplication app,
        BrowserCommandService commandService,
        TtyHostSessionManager sessionManager,
        WebPreviewService webPreviewService,
        BrowserUiBridge? uiBridge)
    {
        app.MapGet("/api/browser/capabilities", (string? sessionId, string? previewName) =>
        {
            var targetUrl = sessionId is not null
                ? webPreviewService.GetTargetUrl(sessionId, previewName)
                : null;
            var response = BuildCapabilitiesResponse(
                commandService,
                webPreviewService,
                uiBridge,
                sessionId,
                previewName,
                targetUrl);
            return Results.Json(response, AppJsonContext.Default.BrowserCapabilitiesResponse);
        });

        app.MapPost("/api/browser/claim", (Models.WebPreview.WebPreviewSessionRequest request) =>
        {
            if (uiBridge is null)
            {
                return Results.Text("Browser UI bridge is not available.\n", statusCode: 409);
            }

            if (string.IsNullOrWhiteSpace(request.SessionId))
            {
                return Results.BadRequest("sessionId required");
            }

            return uiBridge.RequestClaim(
                NormalizeOptional(request.SessionId),
                NormalizeOptional(request.PreviewName),
                out var error)
                ? Results.Ok()
                : Results.Text(error + "\n", statusCode: 409);
        });

        app.MapPost("/api/browser/command", async (BrowserCommandRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.Command))
            {
                return Results.BadRequest("command required");
            }

            var result = await commandService.ExecuteCommandAsync(request, ctx.RequestAborted);
            return ToJsonResult(result);
        });

        app.MapPost("/api/browser/query", async (BrowserCommandRequest request, HttpContext ctx) =>
        {
            var cmd = WithCommand(request, "query");
            var result = await commandService.ExecuteCommandAsync(cmd, ctx.RequestAborted);
            return ToJsonResult(result);
        });

        app.MapPost("/api/browser/click", async (BrowserCommandRequest request, HttpContext ctx) =>
        {
            var cmd = WithCommand(request, "click");
            var result = await commandService.ExecuteCommandAsync(cmd, ctx.RequestAborted);
            return ToJsonResult(result);
        });

        app.MapPost("/api/browser/fill", async (BrowserCommandRequest request, HttpContext ctx) =>
        {
            var cmd = WithCommand(request, "fill");
            var result = await commandService.ExecuteCommandAsync(cmd, ctx.RequestAborted);
            return ToJsonResult(result);
        });

        app.MapPost("/api/browser/scroll", async (BrowserCommandRequest request, HttpContext ctx) =>
        {
            var cmd = WithCommand(request, "scroll");
            var result = await commandService.ExecuteCommandAsync(cmd, ctx.RequestAborted);
            return ToJsonResult(result);
        });

        app.MapPost("/api/browser/exec", async (BrowserCommandRequest request, HttpContext ctx) =>
        {
            var cmd = WithCommand(request, "exec");
            var result = await commandService.ExecuteCommandAsync(cmd, ctx.RequestAborted);
            return ToJsonResult(result);
        });

        app.MapPost("/api/browser/wait", async (BrowserCommandRequest request, HttpContext ctx) =>
        {
            var cmd = WithCommand(request, "wait");
            var result = await commandService.ExecuteCommandAsync(cmd, ctx.RequestAborted);
            return ToJsonResult(result);
        });

        app.MapPost("/api/browser/screenshot", async (BrowserCommandRequest request, HttpContext ctx) =>
        {
            var cmd = WithCommand(request, "screenshot");
            var result = await commandService.ExecuteCommandAsync(cmd, ctx.RequestAborted);

            if (result.Success && result.Result is not null)
            {
                var path = await SaveResultToDiskAsync("screenshot", result, cmd, sessionManager, webPreviewService);
                if (path is not null)
                {
                    return ToJsonResult(new BrowserWsResult
                    {
                        Id = result.Id,
                        Success = true,
                        Result = path
                    });
                }
            }

            return ToJsonResult(result);
        });

        app.MapPost("/api/browser/screenshot-raw", async (BrowserCommandRequest request, HttpContext ctx) =>
        {
            var cmd = WithCommand(request, "screenshot");
            var result = await commandService.ExecuteCommandAsync(cmd, ctx.RequestAborted);
            return ToJsonResult(result);
        });

        app.MapPost("/api/browser/snapshot", async (BrowserCommandRequest request, HttpContext ctx) =>
        {
            var cmd = WithCommand(request, "snapshot");
            var result = await commandService.ExecuteCommandAsync(cmd, ctx.RequestAborted);

            if (result.Success && result.Result is not null)
            {
                var path = await SaveResultToDiskAsync("snapshot", result, cmd, sessionManager, webPreviewService);
                if (path is not null)
                {
                    return ToJsonResult(new BrowserWsResult
                    {
                        Id = result.Id,
                        Success = true,
                        Result = path
                    });
                }
            }

            return ToJsonResult(result);
        });

        app.MapPost("/api/browser/outline", async (BrowserCommandRequest request, HttpContext ctx) =>
        {
            var cmd = WithCommand(request, "outline");
            var result = await commandService.ExecuteCommandAsync(cmd, ctx.RequestAborted);
            return ToJsonResult(result);
        });

        app.MapPost("/api/browser/attrs", async (BrowserCommandRequest request, HttpContext ctx) =>
        {
            var cmd = WithCommand(request, "attrs");
            var result = await commandService.ExecuteCommandAsync(cmd, ctx.RequestAborted);
            return ToJsonResult(result);
        });

        app.MapPost("/api/browser/css", async (BrowserCommandRequest request, HttpContext ctx) =>
        {
            var cmd = WithCommand(request, "css");
            var result = await commandService.ExecuteCommandAsync(cmd, ctx.RequestAborted);
            return ToJsonResult(result);
        });

        app.MapPost("/api/browser/log", async (BrowserCommandRequest request, HttpContext ctx) =>
        {
            var cmd = WithCommand(request, "log");
            var result = await commandService.ExecuteCommandAsync(cmd, ctx.RequestAborted);
            return ToJsonResult(result);
        });

        app.MapPost("/api/browser/links", async (BrowserCommandRequest request, HttpContext ctx) =>
        {
            var cmd = WithCommand(request, "links");
            var result = await commandService.ExecuteCommandAsync(cmd, ctx.RequestAborted);
            return ToJsonResult(result);
        });

        app.MapPost("/api/browser/submit", async (BrowserCommandRequest request, HttpContext ctx) =>
        {
            var cmd = WithCommand(request, "submit");
            var result = await commandService.ExecuteCommandAsync(cmd, ctx.RequestAborted);
            return ToJsonResult(result);
        });

        app.MapPost("/api/browser/forms", async (BrowserCommandRequest request, HttpContext ctx) =>
        {
            var cmd = WithCommand(request, "forms");
            var result = await commandService.ExecuteCommandAsync(cmd, ctx.RequestAborted);
            return ToJsonResult(result);
        });
    }

    private static BrowserCapabilitiesResponse BuildCapabilitiesResponse(
        BrowserCommandService commandService,
        WebPreviewService webPreviewService,
        BrowserUiBridge? uiBridge,
        string? sessionId,
        string? previewName,
        string? targetUrl)
    {
        var normalizedPreview = WebPreviewService.NormalizePreviewName(previewName);
        var status = commandService.GetStatus(
            targetUrl,
            sessionId,
            normalizedPreview,
            connectedUiClientCount: uiBridge?.ConnectedBrowserCount ?? 0);
        return new BrowserCapabilitiesResponse
        {
            SessionId = sessionId ?? "",
            PreviewName = normalizedPreview,
            Status = status,
            FastCommands =
            [
                "mt_status",
                "mt_inspect",
                "mt_outline [depth]",
                "mt_text [selector]",
                "mt_scroll [selector] [deltaY|top|bottom]",
                "mt_query <selector> --text",
                "mt_exec <js>",
                "mt_topic <text>",
                "mt_repo list|add|remove|refresh"
            ],
            DiagnosticCommands =
            [
                "mt_proxylog_summary [limit]",
                "mt_proxylog [limit]",
                "mt_log error",
                "mt_forms",
                "mt_links",
                "mt_screenshot"
            ],
            RecoveryCommands =
            [
                "mt_claim_preview",
                "mt_open --claim <url>",
                "mt_reload",
                "mt_preview_reset [url]",
                "mt_clearstate"
            ],
            Notes =
            [
                "Browser commands require a configured preview target, an attached MidTerm UI on /ws/state, and an injected /ws/browser bridge from the preview frame.",
                "mt_inspect is the lowest-token first diagnostic command; mt_proxylog_summary is the lowest-token proxy diagnostic command.",
                "Use mt_topic with a 3-6 word high-level work topic, updating it when the user's work area shifts.",
                "Use mt_repo to bind every additional repository you use that is not the current working directory so MidTerm shows it in the IDE bar and sidebar.",
                "Screenshots use in-page html2canvas and can differ from native browser screenshots for canvas, video, and cross-origin frame content."
            ]
        };
    }

    private static string BuildCapabilitiesText(BrowserCapabilitiesResponse capabilities)
    {
        var status = capabilities.Status;
        return string.Join('\n',
            [
                "dev browser capabilities",
                $"session: {capabilities.SessionId}",
                $"preview: {capabilities.PreviewName}",
                $"state: {status.State}",
                $"bridge phase: {status.BridgePhase}",
                $"controllable: {(status.Controllable ? "yes" : "no")}",
                $"target: {status.TargetUrl ?? "(none)"}",
                $"recovery: {status.RecoveryHint ?? "(none)"}",
                "fast: " + string.Join(", ", capabilities.FastCommands),
                "diagnostics: " + string.Join(", ", capabilities.DiagnosticCommands),
                "recovery commands: " + string.Join(", ", capabilities.RecoveryCommands),
                "notes: " + string.Join(" ", capabilities.Notes)
            ]) + "\n";
    }

    private static async Task<string> BuildInspectTextAsync(
        BrowserCommandService commandService,
        TtyHostSessionManager sessionManager,
        WebPreviewService webPreviewService,
        BrowserUiBridge? uiBridge,
        string? sessionId,
        string? previewName,
        bool includeScreenshot,
        CancellationToken cancellationToken)
    {
        var targetUrl = sessionId is not null
            ? webPreviewService.GetTargetUrl(sessionId, previewName)
            : null;
        var statusText = commandService.GetStatusText(
            targetUrl,
            sessionId,
            previewName,
            connectedUiClientCount: uiBridge?.ConnectedBrowserCount ?? 0).TrimEnd();
        var status = commandService.GetStatus(
            targetUrl,
            sessionId,
            previewName,
            connectedUiClientCount: uiBridge?.ConnectedBrowserCount ?? 0);
        var lines = new List<string>
        {
            "dev browser inspect",
            statusText,
            "proxy:",
            sessionId is null
                ? "sessionId required for proxy summary"
                : WebPreviewEndpoints.BuildProxyLogSummaryText(webPreviewService.GetLogEntries(sessionId, previewName, 50)).TrimEnd()
        };

        if (!status.Controllable)
        {
            lines.Add("browser: unavailable");
            return string.Join('\n', lines) + "\n";
        }

        var scope = new BrowserCommandRequest
        {
            SessionId = sessionId,
            PreviewName = previewName
        };

        var pageInfo = await RunBrowserTextCommandAsync(commandService, scope, "exec", value:
            """
            (function(){var t=(document.body&&document.body.innerText||"").replace(/\s+/g," ").trim();return JSON.stringify({url:location.href,title:document.title,readyState:document.readyState,text:t.slice(0,1200),links:document.links.length,forms:document.forms.length,images:document.images.length,frames:document.querySelectorAll("iframe,frame").length});})()
            """,
            timeout: 3,
            cancellationToken: cancellationToken);
        lines.Add("page:");
        lines.Add(pageInfo);

        var outline = await RunBrowserTextCommandAsync(commandService, scope, "outline", maxDepth: 3, timeout: 3, cancellationToken: cancellationToken);
        lines.Add("outline:");
        lines.Add(TrimMultiline(outline, 3000));

        var forms = await RunBrowserTextCommandAsync(commandService, scope, "forms", selector: "form", timeout: 3, cancellationToken: cancellationToken);
        lines.Add("forms:");
        lines.Add(TrimMultiline(forms, 2000));

        var errors = await RunBrowserTextCommandAsync(commandService, scope, "log", value: "error", timeout: 3, cancellationToken: cancellationToken);
        lines.Add("console errors:");
        lines.Add(TrimMultiline(errors, 2000));

        if (includeScreenshot)
        {
            var screenshotResult = await commandService.ExecuteCommandAsync(
                new BrowserCommandRequest
                {
                    Command = "screenshot",
                    SessionId = sessionId,
                    PreviewName = previewName,
                    Timeout = 30
                },
                cancellationToken);
            if (screenshotResult.Success && screenshotResult.Result is not null)
            {
                var screenshotPath = await SaveResultToDiskAsync(
                    "screenshot",
                    screenshotResult,
                    new BrowserCommandRequest { SessionId = sessionId, PreviewName = previewName },
                    sessionManager,
                    webPreviewService);
                lines.Add("screenshot:");
                lines.Add(screenshotPath ?? "(capture succeeded, save failed)");
            }
            else
            {
                lines.Add("screenshot:");
                lines.Add(screenshotResult.Error ?? "screenshot failed");
            }
        }

        return string.Join('\n', lines) + "\n";
    }

    private static async Task<string> RunBrowserTextCommandAsync(
        BrowserCommandService commandService,
        BrowserCommandRequest scope,
        string command,
        string? selector = null,
        string? value = null,
        int? maxDepth = null,
        int timeout = 5,
        CancellationToken cancellationToken = default)
    {
        var result = await commandService.ExecuteCommandAsync(
            new BrowserCommandRequest
            {
                Command = command,
                Selector = selector,
                Value = value,
                MaxDepth = maxDepth,
                Timeout = timeout,
                SessionId = scope.SessionId,
                PreviewName = scope.PreviewName,
                PreviewId = scope.PreviewId
            },
            cancellationToken);
        return result.Success ? result.Result ?? "" : $"error: {result.Error ?? "command failed"}";
    }

    private static string TrimMultiline(string value, int maxLength)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..Math.Max(0, maxLength - 1)] + "...";
    }

    private static BrowserCommandRequest? ParseCliArgs(string command, List<string> args)
    {
        var request = command switch
        {
            "query" => new BrowserCommandRequest
            {
                Command = "query",
                Selector = GetPositional(args, 1),
                MaxDepth = GetIntFlag(args, "--depth"),
                TextOnly = HasFlag(args, "--text"),
                Timeout = GetIntFlag(args, "--timeout")
            },
            "click" => new BrowserCommandRequest
            {
                Command = "click",
                Selector = GetPositional(args, 1)
            },
            "scroll" => new BrowserCommandRequest
            {
                Command = "scroll",
                Selector = GetScrollSelector(args),
                Value = BuildScrollValue(args)
            },
            "fill" => new BrowserCommandRequest
            {
                Command = "fill",
                Selector = GetPositional(args, 1),
                Value = GetPositional(args, 2)
            },
            "exec" => new BrowserCommandRequest
            {
                Command = "exec",
                Value = GetPositional(args, 1),
                Timeout = GetIntFlag(args, "--timeout")
            },
            "screenshot" => new BrowserCommandRequest
            {
                Command = "screenshot",
                SessionId = GetFlagValue(args, "--session")
            },
            "snapshot" => new BrowserCommandRequest
            {
                Command = "snapshot",
                SessionId = GetFlagValue(args, "--session")
            },
            "wait" => new BrowserCommandRequest
            {
                Command = "wait",
                Selector = GetPositional(args, 1),
                Timeout = GetIntFlag(args, "--timeout") ?? 5
            },
            "navigate" => new BrowserCommandRequest
            {
                Command = "navigate",
                Value = GetPositional(args, 1)
            },
            "reload" => new BrowserCommandRequest
            {
                Command = "reload",
                Value = HasFlag(args, "--hard") ? "hard" : HasFlag(args, "--force") ? "force" : "soft"
            },
            "outline" => new BrowserCommandRequest
            {
                Command = "outline",
                MaxDepth = GetIntFlag(args, "--depth") ??
                    (args.Count > 1 && int.TryParse(args[1], CultureInfo.InvariantCulture, out var od) ? od : 4)
            },
            "attrs" => new BrowserCommandRequest
            {
                Command = "attrs",
                Selector = GetPositional(args, 1)
            },
            "css" => new BrowserCommandRequest
            {
                Command = "css",
                Selector = GetPositional(args, 1),
                Value = GetPositional(args, 2)
            },
            "log" => new BrowserCommandRequest
            {
                Command = "log",
                Value = GetPositional(args, 1) ?? "all"
            },
            "links" => new BrowserCommandRequest
            {
                Command = "links"
            },
            "submit" => new BrowserCommandRequest
            {
                Command = "submit",
                Selector = GetPositional(args, 1)
            },
            "forms" => new BrowserCommandRequest
            {
                Command = "forms",
                Selector = GetPositional(args, 1)
            },
            "url" => new BrowserCommandRequest { Command = "url" },
            "clearcookies" => new BrowserCommandRequest { Command = "clearcookies" },
            "clearstate" => new BrowserCommandRequest { Command = "clearstate" },
            _ => null
        };

        return request is null
            ? null
            : new BrowserCommandRequest
            {
                Command = request.Command,
                Selector = request.Selector,
                Value = request.Value,
                MaxDepth = request.MaxDepth,
                TextOnly = request.TextOnly,
                Timeout = request.Timeout,
                SessionId = GetFlagValue(args, "--session"),
                PreviewName = GetFlagValue(args, "--preview"),
                PreviewId = request.PreviewId
            };
    }

    private static async Task<string?> SaveResultToDiskAsync(
        string command,
        BrowserWsResult result,
        BrowserCommandRequest request,
        TtyHostSessionManager sessionManager,
        WebPreviewService webPreviewService)
    {
        string? cwd = null;
        if (request.SessionId is not null)
        {
            var session = sessionManager.GetSession(request.SessionId);
            cwd = session?.CurrentDirectory;
        }

        if (string.IsNullOrEmpty(cwd))
        {
            var sessions = sessionManager.GetAllSessions();
            cwd = sessions.FirstOrDefault(s => !string.IsNullOrEmpty(s.CurrentDirectory))?.CurrentDirectory;
        }

        if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd))
            return null;

        if (command == "screenshot" && result.Result is not null)
        {
            var screenshotsDir = MidtermDirectory.EnsureSubdirectory(cwd, "screenshots");

            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var filePath = Path.Combine(screenshotsDir, $"screenshot_{ts}.png");

            try
            {
                var base64 = result.Result;
                if (base64.Contains(',', StringComparison.Ordinal))
                    base64 = base64[(base64.IndexOf(',', StringComparison.Ordinal) + 1)..];

                var bytes = Convert.FromBase64String(base64);
                await File.WriteAllBytesAsync(filePath, bytes);
                return filePath;
            }
            catch (Exception ex)
            {
                BrowserLog.Error($"Failed to save screenshot: {ex.Message}");
                return null;
            }
        }

        if (command == "snapshot" && result.Result is not null)
        {
            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var snapshotDir = MidtermDirectory.EnsureSubdirectory(cwd, $"snapshot_{ts}");

            try
            {
                var html = result.Result;
                await File.WriteAllTextAsync(Path.Combine(snapshotDir, "index.html"), html);

                return snapshotDir;
            }
            catch (Exception ex)
            {
                BrowserLog.Error($"Failed to save snapshot: {ex.Message}");
                return null;
            }
        }

        return null;
    }

    private static BrowserCommandRequest WithCommand(BrowserCommandRequest request, string command) =>
        new()
        {
            Command = command,
            Selector = request.Selector,
            Value = request.Value,
            MaxDepth = request.MaxDepth,
            TextOnly = request.TextOnly,
            Timeout = request.Timeout,
            SessionId = request.SessionId,
            PreviewName = request.PreviewName,
            PreviewId = request.PreviewId
        };

    private static IResult ToJsonResult(BrowserWsResult result)
    {
        var response = new BrowserCommandResponse
        {
            Success = result.Success,
            Result = result.Result,
            Error = result.Error,
            MatchCount = result.MatchCount
        };
        return Results.Json(response, AppJsonContext.Default.BrowserCommandResponse);
    }

    private static string? GetPositional(List<string> args, int index)
    {
        for (int i = 1, pos = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                i++;
                continue;
            }
            if (pos == index - 1)
                return args[i];
            pos++;
        }
        return null;
    }

    private static string? GetScrollSelector(List<string> args)
    {
        var first = GetPositional(args, 1);
        var second = GetPositional(args, 2);
        if (first is null)
        {
            return null;
        }

        return second is null && IsScrollValue(first) ? null : first;
    }

    private static string? BuildScrollValue(List<string> args)
    {
        var first = GetPositional(args, 1);
        var second = GetPositional(args, 2);
        var third = GetPositional(args, 3);
        if (first is null)
        {
            return null;
        }

        if (second is null && IsScrollValue(first))
        {
            return first;
        }

        if (second is null)
        {
            return null;
        }

        return third is null ? second : second + " " + third;
    }

    private static bool IsScrollValue(string value)
    {
        return value.Equals("top", StringComparison.OrdinalIgnoreCase)
            || value.Equals("bottom", StringComparison.OrdinalIgnoreCase)
            || value.Equals("left", StringComparison.OrdinalIgnoreCase)
            || value.Equals("right", StringComparison.OrdinalIgnoreCase)
            || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    private static string? GetFlagValue(List<string> args, string flag)
    {
        for (var i = 1; i < args.Count - 1; i++)
        {
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static int? GetIntFlag(List<string> args, string flag)
    {
        var value = GetFlagValue(args, flag);
        return value is not null && int.TryParse(value, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static bool HasFlag(List<string> args, string flag)
    {
        return args.Any(a => a.Equals(flag, StringComparison.Ordinal));
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
