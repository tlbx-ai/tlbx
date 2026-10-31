using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>Native browser operations with explicit schemas; no shell argument parser or ambient preview state.</summary>
internal sealed class TlbxMcpBrowserTools : McpServerTool
{
    private readonly Tool _tool;
    private readonly string? _command;

    private TlbxMcpBrowserTools(string name, string description, JsonObject properties,
        string[]? required = null, bool readOnly = false, string? command = null, bool destructive = false)
    {
        _command = command;
        properties["targetSessionId"] = Property("string", "Optional explicit target session. Omit to use the verified calling session.");
        properties["previewName"] = Property("string", "Named preview, default: default. Explicit per call; never shared between MCP clients.");
        properties["previewId"] = Property("string", "Optional exact preview client identity for DOM commands and status.");
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        if (required is { Length: > 0 }) schema["required"] = Strings(required);
        _tool = new Tool
        {
            Name = name, Description = description,
            InputSchema = JsonSerializer.SerializeToElement(schema, McpJsonContext.Default.JsonObject),
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = readOnly, DestructiveHint = destructive,
                IdempotentHint = readOnly, OpenWorldHint = true
            }
        };
    }

    public override Tool ProtocolTool => _tool;
    internal string? BrowserCommand => _command;
    public override IReadOnlyList<object> Metadata => [];

    internal static IEnumerable<McpServerTool> CreateTools() =>
    [
        Command("query", "Query DOM elements, with optional textOnly and maxDepth.", Fields(("selector", Property("string")), ("textOnly", Property("boolean")), ("maxDepth", Property("integer")), ("timeout", Timeout())), ["selector"], true),
        Command("click", "Click a DOM element once. A timeout does not authorize replaying the click.", Fields(("selector", Property("string"))), ["selector"]),
        Command("fill", "Fill an input with literal Unicode text, without shell quoting or command-length limitations.", Fields(("selector", Property("string")), ("value", Property("string"))), ["selector", "value"]),
        Command("evaluate", "Execute JavaScript in the existing preview. code is passed literally, not through a shell.", Fields(("code", Property("string")), ("timeout", Timeout())), ["code"], command: "exec"),
        Command("wait", "Wait for an element in the existing preview.", Fields(("selector", Property("string")), ("timeout", Timeout())), ["selector"], true),
        Command("screenshot", "Capture viewport or fullPage as inline MCP PNG. Optionally save the same artifact as the CLI in the target workspace.", Fields(("fullPage", Property("boolean")), ("saveToWorkspace", Property("boolean")))),
        Command("snapshot", "Return the existing preview HTML directly, optionally also saving the CLI-compatible workspace artifact.", Fields(("saveToWorkspace", Property("boolean")))),
        Command("outline", "Return the page structure tree, with explicit maxDepth.", Fields(("maxDepth", Property("integer"))), readOnly: true),
        Command("attributes", "Read a selected element's attributes.", Fields(("selector", Property("string"))), ["selector"], true, "attrs"),
        Command("css", "Read computed CSS. properties is an array; props also accepts the CLI's comma-separated form.", Fields(("selector", Property("string")), ("properties", new JsonObject { ["type"] = "array", ["items"] = Property("string") }), ("props", Property("string"))), ["selector"], true),
        Command("console", "Read captured console messages by filter: error, warn or all.", Fields(("filter", Enum("error", "warn", "all"))), readOnly: true, command: "log"),
        Command("scroll", "Scroll window or a selector by fractional deltaX/deltaY, or to an edge.", Fields(("selector", Property("string")), ("deltaY", Property("number")), ("deltaX", Property("number")), ("to", Enum("top", "bottom", "left", "right")))),
        Command("wheel", "Send wheel events and return measured scroll progress.", Fields(("selector", Property("string")), ("direction", Enum("up", "down")), ("deltaY", Property("number")), ("steps", PositiveInteger()))),
        Command("submit", "Submit a form once. Defaults to the first form; never replay automatically.", Fields(("selector", Property("string")))),
        Command("url", "Read the upstream page URL rather than the tlbx proxy URL.", new(), readOnly: true),
        Command("links", "Read all page links.", new(), readOnly: true),
        Command("forms", "Read form structure and values; optional selector.", Fields(("selector", Property("string"))), readOnly: true),
        new TlbxMcpBrowserTools("browser_open", "Open a preview using the shared CLI/REST ownership and readiness workflow. Hidden by default; activate and claim must be explicit.",
            Fields(("url", Property("string")), ("activate", Property("boolean")), ("claim", Property("boolean")), ("browserId", Property("string"))), ["url"]),
        new TlbxMcpBrowserTools("browser_navigate", "Navigate this preview and await a verified controllable document without activating the user's session.", Fields(("url", Property("string"))), ["url"]),
        new TlbxMcpBrowserTools("browser_close", "Close this preview. Default preview keeps its identity with a cleared target; named previews are removed idempotently.", new()),
        new TlbxMcpBrowserTools("browser_previews", "List all named previews of the target session. Use explicit previewName per call rather than mutable global client state.", new(), readOnly: true),
        new TlbxMcpBrowserTools("browser_target", "Read preview target URL, route and revision.", new(), readOnly: true),
        new TlbxMcpBrowserTools("browser_status", "Read structured bridge, owner and client status for this preview.", new(), readOnly: true),
        new TlbxMcpBrowserTools("browser_cookies", "Read the target preview's cookie jar, as supported by the full-user CLI.", new(), readOnly: true),
        new TlbxMcpBrowserTools("browser_clear_cookies", "Clear browser-side and server-side preview cookies using the CLI ordering.", new(), destructive: true),
        new TlbxMcpBrowserTools("browser_clear_state", "Clear browser cookies/storage/service workers and server preview state using the CLI ordering.", new(), destructive: true),
        new TlbxMcpBrowserTools("browser_reload", "Reload in soft, force or hard mode. Hard also clears browser/server cookies before dispatch, as the CLI does.", Fields(("mode", Enum("soft", "force", "hard")))),
        new TlbxMcpBrowserTools("browser_preview_reset", "Optionally navigate, clear preview state, then perform a hard reload; report partial cleanup warnings instead of replaying actions.", Fields(("url", Property("string"))), destructive: true),
        new TlbxMcpBrowserTools("browser_proxylog", "Read bounded proxy request evidence including failures.", Fields(("limit", PositiveInteger())), readOnly: true),
        new TlbxMcpBrowserTools("browser_proxylog_summary", "Read the same compact proxy summary as the CLI.", Fields(("limit", PositiveInteger())), readOnly: true),
        new TlbxMcpBrowserTools("browser_claim", "Explicitly hand this preview to a selected connected tlbx browser. browserId is recommended when there are several UIs.", Fields(("browserId", Property("string")))),
        new TlbxMcpBrowserTools("browser_claim_main", "Explicitly make a selected browser the leading browser; this is separate from preview ownership.", Fields(("browserId", Property("string")))),
        new TlbxMcpBrowserTools("browser_ui_clients", "List connected browser identities available for an explicit ownership handoff.", new(), readOnly: true),
        new TlbxMcpBrowserTools("browser_capabilities", "Read live browser capabilities and recovery hints from the shared CLI capability projection.", new(), readOnly: true),
        new TlbxMcpBrowserTools("browser_inspect", "Gather the same compact page/status/proxy diagnostic bundle as the CLI, with optional inline screenshot or workspace artifact.", Fields(("screenshot", Property("boolean")), ("saveToWorkspace", Property("boolean")))),
        new TlbxMcpBrowserTools("browser_detach", "Detach this preview into its owning browser's popup and wait for acknowledgment.", new()),
        new TlbxMcpBrowserTools("browser_dock", "Dock this preview back in its owning browser and wait for acknowledgment.", new()),
        new TlbxMcpBrowserTools("browser_viewport", "Set iframe width/height in the owning browser. Both zero reset the viewport.", Fields(("width", Property("integer")), ("height", Property("integer")))),
        new TlbxMcpBrowserTools("browser_mobile", "Control Chrome mobile emulation in the owning browser's optional extension. Actions: open/status/rotate/keyboard/background/foreground/reload/screenshot/close.", Fields(("action", Property("string")), ("profile", Property("string")))),
        new TlbxMcpBrowserTools("browser_agent_wheel", "Wheel the visible structured-agent history and return measured progress.", Fields(("direction", Enum("up", "down")), ("deltaY", Property("number")), ("steps", PositiveInteger()))),
        new TlbxMcpBrowserTools("browser_batch", "Run 1-32 typed browser commands with shared owner/revision checks and partial results. No step is automatically replayed.", BatchFields(), ["commands"]),
        new TlbxMcpBrowserTools("browser_with_preview", "Open a fresh ephemeral named preview, execute a typed browser batch, and remove the preview in finally even on failure/cancellation.", WithPreviewFields(), ["url", "commands"]),
        new TlbxMcpBrowserTools("browser_scope_open", "Reserve a fresh ephemeral preview for a native cross-tool callback. Hidden open, actor-bound opaque lease; automatically closes after eleven minutes if the caller disappears. Omit previewName for a cryptographically unique name.", Fields(("url", Property("string"))), ["url"]),
        new TlbxMcpBrowserTools("browser_scope_close", "Close only the ephemeral preview associated with this caller's opaque scopeToken. Cleanup has an independent bounded lifetime.", Fields(("scopeToken", Property("string"))), ["scopeToken"])
    ];

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var services = request.Services!;
        var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
        if (http is null || TlbxMcpEndpoints.Authorize(http, services.GetRequiredService<SessionCliContextService>()) is not null)
            return Error("TLBX_MCP_CONTEXT_EXPIRED", "No tool was executed. Obtain a fresh verified context.");
        try
        {
            var args = request.Params?.Arguments?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            ValidateArguments(args);
            if (_tool.Name == "browser_scope_close")
            {
                await services.GetRequiredService<BrowserPreviewOperations>().CloseScopeAsync(
                    (string)http.Items[TlbxMcpEndpoints.CallerIdentityItem]!, RequiredString(args, "scopeToken")).ConfigureAwait(false);
                return Reply(new JsonObject { ["success"] = true });
            }
            var caller = http.Items[TlbxMcpEndpoints.SessionItem] as string;
            var target = OptionalString(args, "targetSessionId") ?? caller;
            if (target is null && _tool.Name is not ("browser_ui_clients" or "browser_claim_main"))
                return Error("TLBX_MCP_TARGET_REQUIRED", "This caller has no session binding. Supply targetSessionId explicitly.");
            if (!string.Equals(target, caller, StringComparison.Ordinal))
            {
                if (!RequestAccessContext.HasFullUserAccess(http))
                    return Error("TLBX_MCP_TARGET_FORBIDDEN", "Controlling another session requires full-user supervisor access.");
                if (target is not null && services.GetRequiredService<TtyHostSessionManager>().GetSession(target) is null)
                    return Error("TLBX_MCP_TARGET_NOT_FOUND", "The explicit target session does not exist.");
            }
            var preview = OptionalString(args, "previewName");
            var previewId = OptionalString(args, "previewId");
            if (_command is not null)
            {
                var command = BuildCommand(_command, args, target!, preview, previewId);
                var result = await services.GetRequiredService<BrowserCommandService>().ExecuteCommandAsync(command, cancellationToken).ConfigureAwait(false);
                return await CommandResultAsync(services, command, result, Bool(args, "saveToWorkspace")).ConfigureAwait(false);
            }
            return await ExecuteOperationAsync(services, args, target ?? "", preview, previewId, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex) { return Error("TLBX_MCP_INVALID_ARGUMENT", ex.Message); }
        catch (FormatException ex) { return Error("TLBX_MCP_INVALID_IMAGE", ex.Message); }
        catch (InvalidOperationException ex) { return Error("TLBX_MCP_BROWSER_FAILED", ex.Message); }
    }

    private async Task<CallToolResult> ExecuteOperationAsync(IServiceProvider services, IReadOnlyDictionary<string, JsonElement> args,
        string target, string? preview, string? previewId, CancellationToken ct)
    {
        var commands = services.GetRequiredService<BrowserCommandService>();
        var previews = services.GetRequiredService<WebPreviewService>();
        var ui = services.GetRequiredService<BrowserUiBridge>();
        var operations = services.GetRequiredService<BrowserPreviewOperations>();
        switch (_tool.Name)
        {
            case "browser_open":
            case "browser_navigate":
                // Validate every typed option before a requested ownership change.
                var openUrl = RequiredString(args, "url");
                var shouldClaim = Bool(args, "claim");
                var shouldActivate = Bool(args, "activate");
                var claimBrowser = OptionalString(args, "browserId");
                if (!shouldClaim && claimBrowser is not null)
                    throw new ArgumentException("browserId on browser_open requires claim=true.");
                if (shouldClaim && !ui.RequestClaim(target, preview, out var claimError, claimBrowser))
                    return Error("TLBX_MCP_BROWSER_OWNER_UNAVAILABLE", claimError);
                var opened = await operations.OpenAsync(target, preview, openUrl,
                    _tool.Name == "browser_open" && shouldActivate, ct).ConfigureAwait(false);
                return OperationResult(opened, target, preview);
            case "browser_scope_open":
                var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext!;
                var scope = await operations.OpenScopeAsync((string)http.Items[TlbxMcpEndpoints.CallerIdentityItem]!, target,
                    preview, RequiredString(args, "url"), ct).ConfigureAwait(false);
                return Reply(new JsonObject
                {
                    ["success"] = true, ["scopeToken"] = scope.Token, ["sessionId"] = scope.SessionId,
                    ["previewName"] = scope.PreviewName, ["expiresAfterSeconds"] = 660
                });
            case "browser_close":
                return OperationResult(await operations.CloseAsync(target, preview, ct).ConfigureAwait(false), target, preview);
            case "browser_previews":
                return Reply(previews.ListPreviewSessions(target), AppJsonContext.Default.WebPreviewSessionListResponse);
            case "browser_target":
                var targetPreview = previews.GetPreviewSession(target, preview);
                return targetPreview is not null ? Reply(targetPreview, AppJsonContext.Default.WebPreviewSessionInfo)
                    : Reply(new JsonObject { ["sessionId"] = target, ["previewName"] = WebPreviewService.NormalizePreviewName(preview), ["active"] = false, ["url"] = null });
            case "browser_status":
                return Reply(commands.GetStatus(previews.GetTargetUrl(target, preview), target, preview, previewId, ui.ConnectedBrowserCount), AppJsonContext.Default.BrowserStatusResponse);
            case "browser_cookies":
                return Reply(previews.GetCookies(target, preview), AppJsonContext.Default.WebPreviewCookiesResponse);
            case "browser_clear_cookies":
            case "browser_clear_state":
                return await ClearAsync(commands, previews, target, preview, _tool.Name == "browser_clear_state", ct).ConfigureAwait(false);
            case "browser_reload":
                return await ReloadAsync(commands, previews, target, preview, OptionalString(args, "mode") ?? "soft", ct).ConfigureAwait(false);
            case "browser_preview_reset":
                if (OptionalString(args, "url") is { } newUrl)
                {
                    var navigation = await operations.OpenAsync(target, preview, newUrl, false, ct).ConfigureAwait(false);
                    if (!navigation.Success) return OperationResult(navigation, target, preview);
                }
                var cleared = await ClearAsync(commands, previews, target, preview, true, ct).ConfigureAwait(false);
                var reloaded = await ReloadAsync(commands, previews, target, preview, "hard", ct).ConfigureAwait(false);
                if (cleared.IsError == true)
                    reloaded.Content.Add(new TextContentBlock { Text = "State cleanup warning: " + ((TextContentBlock)cleared.Content[0]).Text });
                return reloaded;
            case "browser_proxylog":
                return Reply(previews.GetLogEntries(target, preview, Integer(args, "limit", 100)), AppJsonContext.Default.ListWebPreviewProxyLogEntry);
            case "browser_proxylog_summary":
                return Reply(new JsonObject { ["sessionId"] = target, ["previewName"] = WebPreviewService.NormalizePreviewName(preview),
                    ["summary"] = WebPreviewEndpoints.BuildProxyLogSummaryText(previews.GetLogEntries(target, preview, Integer(args, "limit", 100))) });
            case "browser_claim":
                return ui.RequestClaim(target, preview, out var error, OptionalString(args, "browserId"))
                    ? Success(target, preview) : Error("TLBX_MCP_BROWSER_OWNER_UNAVAILABLE", error);
            case "browser_claim_main":
                return ui.RequestClaimMain(OptionalString(args, "browserId"), out var browser, out var mainError)
                    ? Reply(new JsonObject { ["success"] = true, ["browserId"] = browser }) : Error("TLBX_MCP_BROWSER_OWNER_UNAVAILABLE", mainError);
            case "browser_ui_clients":
                return Reply(new JsonObject { ["browserIds"] = Strings(ui.GetConnectedBrowserIds()) });
            case "browser_capabilities":
                return Reply(BrowserEndpoints.BuildCapabilitiesResponse(commands, previews, ui, target, preview,
                    previews.GetTargetUrl(target, preview)), AppJsonContext.Default.BrowserCapabilitiesResponse);
            case "browser_inspect":
                var diagnostics = await BrowserEndpoints.BuildInspectTextAsync(commands, services.GetRequiredService<TtyHostSessionManager>(),
                    previews, ui, target, preview, false, ct).ConfigureAwait(false);
                var inspection = new JsonObject { ["sessionId"] = target, ["previewName"] = WebPreviewService.NormalizePreviewName(preview),
                    ["diagnostics"] = diagnostics };
                CallToolResult? capturedInspection = null;
                if (Bool(args, "screenshot") || Bool(args, "saveToWorkspace"))
                {
                    var shot = new BrowserCommandRequest { Command = "screenshot", SessionId = target, PreviewName = preview, PreviewId = previewId, Timeout = 30 };
                    capturedInspection = await CommandResultAsync(services, shot, await commands.ExecuteCommandAsync(shot, ct).ConfigureAwait(false), Bool(args, "saveToWorkspace")).ConfigureAwait(false);
                    inspection["screenshot"] = capturedInspection.StructuredContent is { } captureMetadata
                        ? JsonNode.Parse(captureMetadata.GetRawText()) : new JsonObject { ["success"] = false };
                }
                var inspected = Reply(inspection);
                if (capturedInspection is not null)
                {
                    foreach (var content in capturedInspection.Content) inspected.Content.Add(content);
                    inspected.IsError = capturedInspection.IsError;
                }
                return inspected;
            case "browser_detach":
            case "browser_dock":
            case "browser_viewport":
                var result = await ui.SerializeTargetOperationAsync(target, preview, () => _tool.Name switch
                {
                    "browser_detach" => ui.RequestDetachAsync(target, preview, ct),
                    "browser_dock" => ui.RequestDockAsync(target, preview, ct),
                    _ => ui.RequestViewportAsync(target, preview, Integer(args, "width", 0), Integer(args, "height", 0), ct)
                }, ct).ConfigureAwait(false);
                return result.Success ? Success(target, preview) : Error("TLBX_MCP_BROWSER_UI_FAILED", result.Error ?? "Browser UI did not acknowledge the operation.");
            case "browser_mobile":
                return ui.RequestMobileDevice(target, preview, OptionalString(args, "action") ?? "status", OptionalString(args, "profile") ?? "pixel-8", out var mobileError)
                    ? Reply(new JsonObject { ["accepted"] = true, ["sessionId"] = target, ["previewName"] = WebPreviewService.NormalizePreviewName(preview) })
                    : Error("TLBX_MCP_BROWSER_MOBILE_FAILED", mobileError);
            case "browser_agent_wheel":
                var wheel = await ui.RequestAgentWheelAsync(target, WheelDelta(args), Integer(args, "steps", 1), ct).ConfigureAwait(false);
                var wheelReply = Reply(wheel, AppJsonContext.Default.AgentHistoryWheelResult);
                wheelReply.IsError = !wheel.Success;
                return wheelReply;
            case "browser_batch":
                return await BatchAsync(services, args, target, preview, ct).ConfigureAwait(false);
            case "browser_with_preview":
                var name = preview ?? "mcp-" + Guid.NewGuid().ToString("N");
                if (string.Equals(name, WebPreviewService.DefaultPreviewName, StringComparison.OrdinalIgnoreCase)
                    || previews.GetPreviewSession(target, name) is not null)
                    throw new ArgumentException("An ephemeral preview must have a fresh non-default name; existing previews are never overwritten.");
                // Validate the complete batch before opening a browser or performing any mutation.
                _ = ParseBatch(args, target, name);
                return await operations.WithPreviewAsync(target, name, RequiredString(args, "url"),
                    bodyCt => BatchAsync(services, args, target, name, bodyCt), ct).ConfigureAwait(false);
            default: throw new InvalidOperationException("Unknown browser MCP operation.");
        }
    }

    private static async Task<CallToolResult> ClearAsync(BrowserCommandService commands, WebPreviewService previews,
        string target, string? preview, bool state, CancellationToken ct)
    {
        var browser = await commands.ExecuteCommandAsync(new BrowserCommandRequest
        {
            Command = state ? "clearstate" : "clearcookies", SessionId = target, PreviewName = preview
        }, ct).ConfigureAwait(false);
        if (!browser.Success) return Error("TLBX_MCP_BROWSER_FAILED", browser.Error ?? "Browser state could not be cleared; server state was not changed.");
        var changed = state ? previews.ClearState(target, preview) : previews.ClearAllCookies(target, preview);
        return changed ? Success(target, preview) : Error("TLBX_MCP_BROWSER_NO_TARGET", "Browser cleanup completed but no active server target could be cleared.");
    }

    private static async Task<CallToolResult> ReloadAsync(BrowserCommandService commands, WebPreviewService previews,
        string target, string? preview, string mode, CancellationToken ct)
    {
        if (mode is not ("soft" or "force" or "hard")) throw new ArgumentException("mode must be soft, force or hard.");
        if (mode == "hard")
        {
            var cleared = await ClearAsync(commands, previews, target, preview, false, ct).ConfigureAwait(false);
            if (cleared.IsError == true) return cleared;
            if (!previews.HardReload(target, preview)) return Error("TLBX_MCP_BROWSER_NO_TARGET", "No active target.");
        }
        var result = await commands.ExecuteCommandAsync(new BrowserCommandRequest
        {
            Command = "reload", Value = mode, SessionId = target, PreviewName = preview
        }, ct).ConfigureAwait(false);
        return result.Success ? Success(target, preview) : Error("TLBX_MCP_BROWSER_RELOAD_FAILED",
            (result.Error ?? "Reload was not acknowledged.") + " Inspect state before retrying; the reload may already have executed.");
    }

    private static async Task<CallToolResult> BatchAsync(IServiceProvider services, IReadOnlyDictionary<string, JsonElement> args,
        string target, string? preview, CancellationToken ct)
    {
        var request = ParseBatch(args, target, preview);
        var batch = await services.GetRequiredService<BrowserCommandService>().ExecuteBatchAsync(request, ct).ConfigureAwait(false);
        var images = new List<ContentBlock>();
        var captures = new JsonArray();
        foreach (var step in batch.Results.Where(s => s.Command == "screenshot" && s.Success))
        {
            var shot = new BrowserCommandRequest { Command = "screenshot", SessionId = target, PreviewName = preview };
            var captured = await CommandResultAsync(services, shot, new BrowserWsResult { Success = true, Result = step.Result }, Bool(args, "saveToWorkspace")).ConfigureAwait(false);
            // The structured-result text is content block 0.
            var imageIndex = images.Count + 1;
            images.AddRange(captured.Content);
            step.Result = "MCP content block " + imageIndex.ToString(CultureInfo.InvariantCulture);
            captures.Add((JsonNode)new JsonObject
            {
                ["stepIndex"] = step.Index, ["contentIndex"] = imageIndex,
                ["capture"] = captured.StructuredContent is { } metadata ? JsonNode.Parse(metadata.GetRawText()) : null
            });
            if (captured.IsError == true)
            {
                batch.Success = false;
                batch.Error ??= "A browser step completed but its screenshot/artifact conversion failed. Do not replay the batch.";
                batch.FailedIndex ??= step.Index;
            }
        }
        var batchData = JsonNode.Parse(JsonSerializer.Serialize(batch, AppJsonContext.Default.BrowserBatchResponse))!.AsObject();
        batchData["captures"] = captures;
        var response = Reply(batchData);
        foreach (var image in images) response.Content.Add(image);
        response.IsError = !batch.Success;
        return response;
    }

    internal static BrowserBatchRequest ParseBatch(IReadOnlyDictionary<string, JsonElement> args, string target, string? preview)
    {
        if (!args.TryGetValue("commands", out var commands) || commands.ValueKind != JsonValueKind.Array
            || commands.GetArrayLength() is < 1 or > 32)
            throw new ArgumentException("commands must contain 1-32 typed browser commands.");
        var items = new List<BrowserCommandRequest>();
        using var commandItems = commands.EnumerateArray();
        foreach (var item in commandItems)
        {
            if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each browser command must be an object.");
            using var itemProperties = item.EnumerateObject();
            var values = itemProperties.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
            var command = RequiredString(values, "command");
            if (!BatchCommands.Contains(command, StringComparer.Ordinal)) throw new ArgumentException("Unsupported batch command: " + command);
            if (values.Keys.Any(key => !BatchPropertyNames.Contains(key, StringComparer.Ordinal)))
                throw new ArgumentException("Unknown batch argument. Commands cannot override their target session or preview.");
            items.Add(BuildCommand(command, values, target, preview, null));
        }
        var timeout = Integer(args, "timeout", 60);
        if (timeout is < 1 or > 120) throw new ArgumentException("Batch timeout must be 1-120 seconds.");
        return new BrowserBatchRequest { SessionId = target, PreviewName = preview, Commands = items, Timeout = timeout };
    }

    internal static BrowserCommandRequest BuildCommand(string command, IReadOnlyDictionary<string, JsonElement> args,
        string target, string? preview, string? previewId)
    {
        command = command == "evaluate" ? "exec" : command;
        var selector = OptionalString(args, "selector");
        var value = OptionalString(args, "value", allowEmpty: command is "fill" or "exec");
        int? timeout = args.ContainsKey("timeout") ? Integer(args, "timeout", 0) : command == "wait" ? 15 : null;
        if (timeout is < 1 or > MaximumTimeoutSeconds) throw new ArgumentException("timeout exceeds the shared browser timer range.");
        if (command is "query" or "click" or "fill" or "wait" or "attrs" or "css")
            selector = RequiredString(args, "selector");
        if (command == "fill") value = RequiredString(args, "value", allowEmpty: true);
        if (command == "exec") value = OptionalString(args, "code", allowEmpty: true) ?? RequiredString(args, "value", allowEmpty: true);
        if (command == "navigate") value = RequiredString(args, "value");
        if (command == "reload")
        {
            value ??= "soft";
            if (value is not ("soft" or "force" or "hard")) throw new ArgumentException("Reload value must be soft, force or hard.");
        }
        if (command == "scroll")
        {
            selector ??= "window";
            value = OptionalString(args, "to") ?? value
                ?? string.Create(CultureInfo.InvariantCulture, $"{Number(args, "deltaY", 600)} {Number(args, "deltaX", 0)}");
            if (args.ContainsKey("to") && value is not ("top" or "bottom" or "left" or "right")) throw new ArgumentException("Invalid scroll edge.");
        }
        if (command == "wheel") selector ??= "window";
        if (command == "log")
        {
            value = OptionalString(args, "filter") ?? value ?? "all";
            if (value is not ("all" or "warn" or "error")) throw new ArgumentException("Console filter must be all, warn or error.");
        }
        if (command == "css")
        {
            if (args.TryGetValue("properties", out var properties))
            {
                if (properties.ValueKind != JsonValueKind.Array) throw new ArgumentException("properties must be an array.");
                using var cssItems = properties.EnumerateArray();
                var cssProperties = cssItems.ToArray();
                if (properties.ValueKind != JsonValueKind.Array || properties.GetArrayLength() == 0
                    || cssProperties.Any(p => p.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(p.GetString())))
                    throw new ArgumentException("properties must contain CSS property names.");
                value = string.Join(',', cssProperties.Select(p => p.GetString()));
            }
            value ??= OptionalString(args, "props");
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("CSS properties or props is required.");
        }
        if (command == "outline" && !args.ContainsKey("maxDepth")) value = null;
        var steps = Integer(args, "steps", 1);
        if (steps < 1) throw new ArgumentException("steps must be positive.");
        return new BrowserCommandRequest
        {
            Command = command, SessionId = target, PreviewName = preview, PreviewId = previewId,
            Selector = selector, Value = value, Timeout = timeout,
            MaxDepth = args.ContainsKey("maxDepth") ? Integer(args, "maxDepth", 4) : command == "outline" ? 4 : null,
            TextOnly = Bool(args, "textOnly"), FullPage = Bool(args, "fullPage"),
            DeltaX = args.ContainsKey("deltaX") ? Number(args, "deltaX", 0) : null,
            DeltaY = command == "wheel" ? WheelDelta(args) : args.ContainsKey("deltaY") ? Number(args, "deltaY", 0) : null,
            Steps = command == "wheel" ? steps : null,
            WaitForNavigation = Bool(args, "waitForNavigation")
        };
    }

    internal static async Task<CallToolResult> CommandResultAsync(IServiceProvider services, BrowserCommandRequest request,
        BrowserWsResult result, bool save)
    {
        if (!result.Success) return Error("TLBX_MCP_BROWSER_FAILED", result.Error ?? "Browser command failed.");
        var oversizedImage = request.Command == "screenshot" && result.Result is { Length: > 16_000_000 } payload
            && payload.StartsWith("data:image/png;base64,", StringComparison.Ordinal);
        var oversizedSnapshot = request.Command == "snapshot" && result.Result is { Length: > 65536 };
        var store = services.GetService<TlbxMcpArtifactStore>();
        var saveArtifact = save || (oversizedImage || oversizedSnapshot) && store is null;
        string? artifact = null;
        if (saveArtifact && request.Command is "screenshot" or "snapshot")
        {
            artifact = await BrowserEndpoints.SaveResultToDiskAsync(request.Command, result, request,
                services.GetRequiredService<TtyHostSessionManager>(), services.GetRequiredService<WebPreviewService>()).ConfigureAwait(false);
        }
        var data = new JsonObject
        {
            ["success"] = !saveArtifact || artifact is not null,
            ["sessionId"] = request.SessionId, ["previewName"] = WebPreviewService.NormalizePreviewName(request.PreviewName),
            ["matchCount"] = result.MatchCount, ["artifactPath"] = artifact
        };
        ResourceLinkBlock? resource = null;
        if (store is not null && result.Result is { } original)
        {
            if (request.Command == "screenshot" && original.StartsWith("data:image/png;base64,", StringComparison.Ordinal))
                resource = store.Register(Convert.FromBase64String(original[22..]), "screenshot.png", "image/png");
            else if (request.Command == "snapshot")
                resource = store.Register(System.Text.Encoding.UTF8.GetBytes(original), "snapshot.html", "text/html; charset=utf-8");
            if (resource is not null) data["resourceUri"] = resource.Uri;
        }
        if (request.Command == "screenshot")
        {
            if (oversizedImage)
            {
                var workspaceFailed = save && artifact is null;
                data["success"] = !workspaceFailed && (resource is not null || artifact is not null);
                var previewImage = result.Result is { } fullPng
                    ? TlbxMcpArtifactStore.PreviewPng(Convert.FromBase64String(fullPng[22..])) : null;
                data["inlineImageAvailable"] = previewImage is not null;
                data["inlineImageIsPreview"] = previewImage is not null;
                data["detail"] = resource is not null
                    ? "The full PNG exceeds the inline image budget. resourceUri preserves the complete original; use artifact_save for a caller-local file."
                    : "The full PNG exceeds the inline image budget. The workspace artifact preserves the complete original.";
                var oversized = Reply(data);
                if (resource is not null) oversized.Content.Add(resource);
                if (previewImage is not null) oversized.Content.Add(previewImage);
                oversized.IsError = workspaceFailed || resource is null && artifact is null;
                if (workspaceFailed) oversized.Content.Add(new TextContentBlock
                    { Text = "Screenshot captured, but the requested workspace copy could not be saved. Do not replay the browser operation." });
                return oversized;
            }
            var image = TlbxMcpTool.Screenshot(result.Result);
            if (image.IsError == true) return image;
            image.StructuredContent = JsonSerializer.SerializeToElement(data, McpJsonContext.Default.JsonObject);
            if (resource is not null) image.Content.Add(resource);
            if (save && artifact is null)
            {
                image.IsError = true;
                image.Content.Add(new TextContentBlock { Text = "Screenshot captured, but workspace artifact could not be saved. Do not replay the browser operation." });
            }
            return image;
        }
        if (request.Command == "snapshot") data["inlineSnapshotAvailable"] = !oversizedSnapshot;
        if (oversizedSnapshot)
        {
            var preserved = resource is not null || artifact is not null;
            var failedCopy = save && artifact is null;
            data["success"] = preserved && !failedCopy;
            data["detail"] = resource is not null
                ? "The complete HTML exceeds the 65536-character inline budget. resourceUri preserves the original; use artifact_save for a caller-local file."
                : artifact is not null ? "The complete HTML exceeds the inline budget. The workspace artifact preserves the original."
                : "The HTML exceeds the inline budget and no complete artifact could be retained.";
            var snapshot = Reply(data);
            if (resource is not null) snapshot.Content.Add(resource);
            snapshot.IsError = !preserved || failedCopy;
            if (failedCopy) snapshot.Content.Add(new TextContentBlock
                { Text = "Snapshot captured, but the requested workspace copy could not be saved. Do not replay the browser operation." });
            return snapshot;
        }
        data["result"] = ParseResult(result.Result);
        var response = Reply(data);
        if (resource is not null) response.Content.Add(resource);
        if (save && artifact is null) response.IsError = true;
        return response;
    }

    private void ValidateArguments(IReadOnlyDictionary<string, JsonElement> args)
    {
        var properties = _tool.InputSchema.GetProperty("properties");
        if (args.Keys.Any(key => !properties.TryGetProperty(key, out _))) throw new ArgumentException("Unexpected browser tool argument.");
        if (_tool.InputSchema.TryGetProperty("required", out var required))
        {
            using var requiredItems = required.EnumerateArray();
            foreach (var property in requiredItems)
                if (!args.ContainsKey(property.GetString()!)) throw new ArgumentException(property.GetString() + " is required.");
        }
    }

    private static JsonNode? ParseResult(string? result)
    {
        if (result is null) return null;
        try { return JsonNode.Parse(result); }
        catch (JsonException) { return JsonValue.Create(result); }
    }
    private static string RequiredString(IReadOnlyDictionary<string, JsonElement> args, string key, bool allowEmpty = false)
        => OptionalString(args, key, allowEmpty) ?? throw new ArgumentException(key + " is required.");
    private static string? OptionalString(IReadOnlyDictionary<string, JsonElement> args, string key, bool allowEmpty = false)
    {
        if (!args.TryGetValue(key, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || (!allowEmpty && string.IsNullOrWhiteSpace(value.GetString())))
            throw new ArgumentException(key + " must be a string" + (allowEmpty ? "." : " containing non-whitespace text."));
        return value.GetString();
    }
    private static bool Bool(IReadOnlyDictionary<string, JsonElement> args, string key)
    {
        if (!args.TryGetValue(key, out var value)) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException(key + " must be a boolean.");
        return value.GetBoolean();
    }
    private static int Integer(IReadOnlyDictionary<string, JsonElement> args, string key, int fallback)
    {
        if (!args.TryGetValue(key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) throw new ArgumentException(key + " must be an integer.");
        return number;
    }
    private static double Number(IReadOnlyDictionary<string, JsonElement> args, string key, double fallback)
    {
        if (!args.TryGetValue(key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new ArgumentException(key + " must be a finite number.");
        return number;
    }
    private static double WheelDelta(IReadOnlyDictionary<string, JsonElement> args)
    {
        if (args.ContainsKey("deltaY") && args.ContainsKey("direction")) throw new ArgumentException("Use direction or deltaY, not both.");
        var direction = OptionalString(args, "direction");
        if (direction is not (null or "up" or "down")) throw new ArgumentException("direction must be up or down.");
        return args.ContainsKey("deltaY") ? Number(args, "deltaY", 120) : direction == "up" ? -120 : 120;
    }
    private static CallToolResult OperationResult(BrowserPreviewOperationResult result, string target, string? preview)
    {
        var response = Reply(new JsonObject { ["success"] = result.Success, ["statusCode"] = result.StatusCode,
            ["sessionId"] = target, ["previewName"] = WebPreviewService.NormalizePreviewName(preview), ["detail"] = result.Text });
        response.IsError = !result.Success;
        return response;
    }
    private static CallToolResult Success(string target, string? preview) => Reply(new JsonObject { ["success"] = true,
        ["sessionId"] = target, ["previewName"] = WebPreviewService.NormalizePreviewName(preview) });
    private static CallToolResult Reply<T>(T value, JsonTypeInfo<T> typeInfo) => Reply(JsonSerializer.SerializeToElement(value, typeInfo));
    private static CallToolResult Reply(JsonObject value) => Reply(JsonSerializer.SerializeToElement(value, McpJsonContext.Default.JsonObject));
    private static CallToolResult Reply(JsonElement value) => new() { StructuredContent = value, Content = [new TextContentBlock { Text = value.GetRawText() }] };
    private static CallToolResult Error(string code, string message) => new() { IsError = true, Content = [new TextContentBlock { Text = code + ": " + message }] };
    private static TlbxMcpBrowserTools Command(string name, string description, JsonObject properties,
        string[]? required = null, bool readOnly = false, string? command = null)
        => new("browser_" + name, description, properties, required, readOnly, command ?? name);
    private static JsonObject Fields(params (string Name, JsonObject Schema)[] fields)
    {
        var result = new JsonObject();
        foreach (var (name, schema) in fields) result[name] = schema;
        return result;
    }
    private static JsonObject Property(string type, string? description = null)
    {
        var result = new JsonObject { ["type"] = type };
        if (description is not null) result["description"] = description;
        return result;
    }
    private static JsonObject PositiveInteger() => new() { ["type"] = "integer", ["minimum"] = 1 };
    // CancellationTokenSource's timer accepts at most uint.MaxValue - 1 ms.
    private const int MaximumTimeoutSeconds = (int)((uint.MaxValue - 1L) / 1000L);
    private static JsonObject Timeout(int maximum = MaximumTimeoutSeconds) => new() { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = maximum };
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    private static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = Strings(values) };
    private static readonly string[] BatchCommands = ["query", "click", "fill", "exec", "evaluate", "wait", "screenshot", "scroll", "wheel", "navigate", "reload", "outline", "attrs", "css", "log", "links", "submit", "forms", "url"];
    private static readonly string[] BatchPropertyNames = ["command", "selector", "value", "code", "timeout", "maxDepth", "textOnly", "fullPage", "deltaX", "deltaY", "steps", "direction", "to", "properties", "props", "filter", "waitForNavigation"];
    private static JsonObject BatchFields() => Fields(("commands", new JsonObject
    {
        ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 32,
        ["items"] = new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false, ["required"] = Strings(["command"]),
            ["properties"] = Fields(("command", Enum(BatchCommands)), ("selector", Property("string")), ("value", Property("string")),
                ("code", Property("string")), ("timeout", Timeout()), ("maxDepth", Property("integer")), ("textOnly", Property("boolean")),
                ("fullPage", Property("boolean")), ("deltaX", Property("number")), ("deltaY", Property("number")), ("steps", PositiveInteger()),
                ("direction", Enum("up", "down")), ("to", Enum("top", "bottom", "left", "right")),
                ("properties", new JsonObject { ["type"] = "array", ["items"] = Property("string") }), ("props", Property("string")),
                ("filter", Enum("error", "warn", "all")), ("waitForNavigation", Property("boolean")))
        }
    }), ("timeout", Timeout(120)), ("saveToWorkspace", Property("boolean")));
    private static JsonObject WithPreviewFields()
    {
        var fields = BatchFields();
        fields["url"] = Property("string");
        return fields;
    }
}
