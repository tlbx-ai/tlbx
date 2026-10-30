using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Ai.Tlbx.MidTerm.Services.Boards;
using ModelContextProtocol.Server;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>Semantic CLI counterparts. Schemas come from the exact REST DTO metadata.</summary>
internal static partial class TlbxMcpApiTools
{
    internal static IEnumerable<McpServerTool> CreateTools() => Operations().Select(operation => new TlbxMcpApiTool(operation));

    internal static IEnumerable<McpApiOperation> Operations()
    {
        yield return Body("session_create", "POST", "/api/sessions", "Create a real terminal session. Exact launchCommand/arguments are preserved; no provider flags are injected.", AppJsonContext.Default.CreateSessionRequest);
        yield return Target("session_close", "DELETE", "/api/sessions/{id}", "Close an explicit session or verified self. This stops its running terminal/runtime.");
        yield return Target("session_set_topic", "PUT", "/api/sessions/{id}/topic", "Set the sidebar task topic. Pass topic:null to clear.", AppJsonContext.Default.SetSessionTopicRequest);
        yield return Target("session_rename", "PUT", "/api/sessions/{id}/name", "Rename the session.", AppJsonContext.Default.RenameSessionRequest);
        yield return Target("session_inject_guidance", "POST", "/api/sessions/{id}/inject-guidance", "Ensure the maintained CLI helpers and agent guidance exist in the session working directory.");
        yield return Target("session_activity", "GET", "/api/sessions/{id}/activity", "Read output activity and bell history.")
            .With(Q("seconds", "integer", "120"), Q("bellLimit", "integer", "25"));
        yield return D("sessions_attention", "GET", "/api/sessions/attention", "Read ranked fleet attention without inferring assistant meaning from terminal text.")
            .With(Q("agentOnly", "boolean", "true"));
        yield return Target("session_state", "GET", "/api/sessions/{id}/state", "Read authoritative terminal/runtime state, optionally with its buffer.")
            .With(Q("includeBuffer", "boolean", "true"), Q("includeBufferBase64", "boolean", "false"));
        yield return Target("terminal_buffer", "GET", "/api/sessions/{id}/buffer", "Read exact terminal bytes as base64 without ANSI or Unicode loss.");
        yield return Target("terminal_tail", "GET", "/api/sessions/{id}/buffer/tail", "Read terminal tail; ANSI stripping is explicit and defaults on.")
            .With(Q("lines", "integer", "120"), Q("stripAnsi", "boolean", "true"));
        yield return Target("terminal_redraw", "POST", "/api/sessions/{id}/redraw", "Ask the foreground console application to repaint its current screen.");
        yield return Target("terminal_send_text", "POST", "/api/sessions/{id}/input/text", "Send literal text without submitting. Text/newlines/base64 follow the existing terminal API exactly.", AppJsonContext.Default.SessionInputRequest);
        yield return Target("terminal_paste", "POST", "/api/sessions/{id}/input/paste", "Paste literal text/file paths with explicit bracketed-paste semantics; no shell quoting or command-length limit.", AppJsonContext.Default.SessionPasteRequest);
        yield return Target("terminal_send_keys", "POST", "/api/sessions/{id}/input/keys", "Send named keys such as Enter, C-c, Escape and arrows, preserving order.", AppJsonContext.Default.SessionKeyInputRequest);
        yield return Target("terminal_prompt", "POST", "/api/sessions/{id}/input/prompt", "State-aware send and submit using the existing atomic prompt path. Supports exact delays, key sequences, profile, text or base64.", AppJsonContext.Default.SessionPromptRequest);
        var promptNow = Target("terminal_prompt_now", "POST", "/api/sessions/{id}/input/prompt", "Interrupt first, then atomically send and submit. Delay/key/profile options remain available.", AppJsonContext.Default.SessionPromptRequest);
        promptNow.Forced["interruptFirst"] = true;
        promptNow.Forced["mode"] = "interrupt-first";
        yield return promptNow;
        yield return new McpApiOperation("terminal_slash", "POST", "/api/sessions/{id}/input/prompt", "Send a slash command through the same state-aware prompt API. A leading slash is added if missing.")
        {
            BodyType = AppJsonContext.Default.SessionPromptRequest, SessionRoute = "id",
            Transform = binding =>
            {
                var text = binding.Body["text"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(text)) throw new McpApiArgumentException("text must contain a slash command.");
                if (!text.StartsWith('/')) binding.Body["text"] = "/" + text;
            }
        };

        var schedule = new McpApiOperation("queue_schedule", "POST", "/api/command-bay/queue", "Schedule a one-shot or recurring structured prompt/action. delay and interval accept 30s, 5m, 2h, 1d (bare numbers mean minutes); delayMs/runAt/repeatEveryMs remain available.")
        {
            BodyType = AppJsonContext.Default.ManagerBarQueueEnqueueRequest, SessionBody = "sessionId",
            Transform = binding =>
            {
                if (binding.Arguments.TryGetValue("delay", out var delay)) binding.Body["delayMs"] = ParseDelay(delay);
                if (binding.Arguments.TryGetValue("interval", out var interval))
                {
                    var ms = ParseDelay(interval);
                    if (ms < 1000) throw new McpApiArgumentException("Recurring interval must be at least one second.");
                    binding.Body["repeatEveryMs"] = ms;
                    if (!binding.Body.ContainsKey("delayMs") && !binding.Body.ContainsKey("runAt")) binding.Body["delayMs"] = ms;
                }
            }
        };
        schedule.Parameters.Add(P("delay", null, DelaySchema()));
        schedule.Parameters.Add(P("interval", null, DelaySchema()));
        yield return schedule;
        yield return D("queue_list", "GET", "/api/command-bay/queue", "List pending one-shot/recurring prompts and actions for the target session.").With(SessionQuery());
        yield return D("queue_cancel", "DELETE", "/api/command-bay/queue/{queueId}", "Cancel an explicit queued item; running work is not silently retried.").Path("queueId", "queueId");
        var notification = ScopedBody("notification_send", "POST", "/api/notifications", "Send a notification associated with verified self or an explicit target session.", AppJsonContext.Default.TerminalNotificationRequest);
        notification.Defaults["title"] = "tlbx";
        yield return notification;

        yield return Remote("control_plane_get", "/api/control-plane", "/api/hub/machines/{id}/control-plane", "Read explicitly published work, status and checkpoints locally or on a registered Hub machine.")
            .With(Q("workLimit", "integer", "100"), Q("checkpointLimit", "integer", "30"));
        yield return Remote("agent_capabilities", "/api/control-plane/capabilities", "/api/hub/machines/{id}/control-plane/capabilities", "Read exact product and per-session agent capabilities locally or on a registered Hub machine.");
        yield return Remote("control_events", "/api/control-plane/events", "/api/hub/machines/{id}/control-plane/events", "Read exact mutation events after a sequence, locally or on a registered Hub machine.")
            .With(Q("after", "integer", "0"), Q("limit", "integer", "100"));
        yield return Body("agent_dispatch", "POST", "/api/control-plane/dispatch", "Explicit deterministic fan-out to one to 32 sessionIds. The complete structured turn supports attachments and provider options; no PTY meaning is inferred.", AppJsonContext.Default.ControlPlaneDispatchRequest);
        yield return D("work_list", "GET", "/api/control-plane/work-items", "List published work with optional state/kind/session/project filters.")
            .With(Q("state"), Q("kind"), SessionQuery(defaultSelf: false), Q("project"), Q("limit", "integer", "100"));
        yield return new McpApiOperation("work_add", "POST", "/api/control-plane/work-items", "Create or deduplicate a published work item. Associate with verified self by default; an unbound operator may omit the association or specify targetSessionId.")
        {
            BodyType = AppJsonContext.Default.CreateControlPlaneWorkItemRequest, SessionBody = "sessionId", AllowUnassociatedBody = true
        };
        yield return Body("work_update", "PATCH", "/api/control-plane/work-items/{id}", "Update work state and optional fields. Omitted fields remain unchanged; explicit empty values are preserved.", AppJsonContext.Default.UpdateControlPlaneWorkItemRequest).Path("id", "workId");
        yield return D("work_delete", "DELETE", "/api/control-plane/work-items/{id}", "Delete an explicitly identified work item.").Path("id", "workId");
        yield return new McpApiOperation("status_publish", "PUT", "/api/control-plane/session-status/{sessionId}", "Publish explicit working/waiting/needsInput/blocked/done status for a session.")
        {
            SessionRoute = "sessionId", BodyType = AppJsonContext.Default.PublishControlPlaneSessionStatusRequest
        };
        yield return D("status_list", "GET", "/api/control-plane/session-status", "List all published statuses, or filter to an explicit targetSessionId.").With(SessionQuery(defaultSelf: false));
        yield return new McpApiOperation("status_clear", "DELETE", "/api/control-plane/session-status/{sessionId}", "Clear the target session's published status.") { SessionRoute = "sessionId" };
        yield return ScopedBody("checkpoint_add", "POST", "/api/control-plane/checkpoints", "Record an explicit checkpoint with kind, summary, details and project/repository context.", AppJsonContext.Default.CreateControlPlaneCheckpointRequest);
        yield return D("checkpoints_list", "GET", "/api/control-plane/checkpoints", "Read checkpoints, optionally filtered by session and kind.")
            .With(SessionQuery(defaultSelf: false), Q("kind"), Q("limit", "integer", "100"));

        yield return D("input_history_list", "GET", "/api/input-history", "Read deterministic prompt/paste/upload history for the target session.")
            .With(SessionQuery(), Q("kind"), Q("limit", "integer", "100"));
        yield return D("input_history_get", "GET", "/api/input-history/{id}", "Read one exact recorded input payload.").Path("id", "entryId");
        yield return new McpApiOperation("input_history_replay", "POST", "/api/input-history/{id}/replay", "Replay one recorded prompt, paste or upload into verified self or an explicit target, preserving its structured payload and terminal/agent surface semantics.")
        {
            BodyType = AppJsonContext.Default.InputHistoryReplayRequest, SessionBody = "targetSessionId"
        }.Path("id", "entryId");
        yield return D("input_history_delete", "DELETE", "/api/input-history/{id}", "Remove one explicitly identified input history entry.").Path("id", "entryId");
        yield return D("input_history_clear", "DELETE", "/api/input-history", "Clear recorded input history for the target session.").With(SessionQuery());

        var bootstrap = Body("session_bootstrap", "POST", "/api/workers/bootstrap", "Create and bootstrap a terminal agent worker with exact profile/workingDirectory, guidance and initial slash commands. Supports launchDelayMs and slashCommandDelayMs.", AppJsonContext.Default.WorkerBootstrapRequest);
        bootstrap.Defaults["agentControlled"] = true;
        bootstrap.Defaults["injectGuidance"] = true;
        bootstrap.Defaults["launchDelayMs"] = 1200;
        bootstrap.Defaults["slashCommandDelayMs"] = 350;
        yield return bootstrap;
        var agentNew = Body("agent_session_create", "POST", "/api/workers/bootstrap", "Create a dedicated structured Agent Controller runtime, separate from ordinary terminal sessions. Existing user-controlled launch arguments remain intact.", AppJsonContext.Default.WorkerBootstrapRequest);
        agentNew.Forced["appServerControlOnly"] = true;
        agentNew.Defaults["agentControlled"] = true;
        agentNew.Defaults["injectGuidance"] = true;
        agentNew.Defaults["profile"] = "codex";
        yield return agentNew;
        yield return Target("agent_history", "GET", "/api/sessions/{id}/agent-control/history", "Read canonical structured agent history, not terminal transcript scraping.")
            .With(Q("startIndex", "integer"), Q("count", "integer"), Q("viewportWidth", "integer"));
        yield return Target("agent_turn", "POST", "/api/sessions/{id}/agent-control/turn", "Submit a structured agent turn with model/effort/plan/permission options and attachments.", AppJsonContext.Default.AppServerControlTurnRequest);
        yield return Target("agent_interrupt", "POST", "/api/sessions/{id}/agent-control/interrupt", "Interrupt the structured runtime, optionally targeting a specific turnId.", AppJsonContext.Default.AppServerControlInterruptRequest);
        yield return Target("agent_steer", "POST", "/api/sessions/{id}/agent-control/steer", "Steer the exact expected active turn with text/attachments, retaining concurrency checks.", AppJsonContext.Default.AppServerControlSteerRequest);
        yield return Target("agent_compact", "POST", "/api/sessions/{id}/agent-control/compact", "Compact the dedicated structured agent runtime.");

        yield return D("repos_list", "GET", "/api/git/repos", "List the target session's watched repositories and current Git status.").With(SessionQuery());
        yield return ScopedBody("repo_bind", "POST", "/api/git/repos", "Bind a repository path to the target session with the existing role/label contract.", AppJsonContext.Default.GitRepoBindRequest);
        yield return D("repo_remove", "DELETE", "/api/git/repos", "Remove an explicit watched repository binding from the target session.").With(SessionQuery(), Q("repoRoot", required: true));
        yield return ScopedBody("repo_refresh", "POST", "/api/git/repos/refresh", "Refresh repository state for the target session; optionally select one repoRoot.", AppJsonContext.Default.GitRepoRefreshRequest);

        yield return D("boards_list", "GET", "/api/boards", "List boards and current revision tokens.");
        yield return Board("board_get", "GET", "/api/boards/{id}", "Read a board and its current revision.");
        yield return Body("board_create", "POST", "/api/boards", "Create a board with the full existing BoardPatch schema.", BoardJsonContext.Default.BoardPatch);
        yield return Board("board_update", "PATCH", "/api/boards/{id}", "Update a board with expectedRevision conflict protection.", BoardJsonContext.Default.BoardPatch);
        yield return Board("board_set_viewport", "PATCH", "/api/boards/{id}/viewport", "Set board canvas viewport using its native viewport schema.", BoardJsonContext.Default.BoardViewport);
        yield return Board("board_delete", "DELETE", "/api/boards/{id}", "Delete a board using the current expectedRevision; read the board before deleting.").With(Q("expectedRevision", "integer", required: true));
        yield return Board("board_item_add", "POST", "/api/boards/{id}/items", "Add an item with the full structured item and revision contract.", BoardJsonContext.Default.BoardItemWrite);
        yield return BoardItem("board_item_get", "GET", "/api/boards/{id}/items/{itemId}", "Read an exact board item and revision.");
        yield return BoardItem("board_item_write", "PUT", "/api/boards/{id}/items/{itemId}", "Write a board item using expectedRevision and the complete structured item payload.", BoardJsonContext.Default.BoardItemWrite);
        yield return BoardItem("board_item_remove", "DELETE", "/api/boards/{id}/items/{itemId}", "Remove an exact board item with expectedRevision conflict protection.").With(Q("expectedRevision", "integer", required: true));
        yield return BoardItem("board_browser_command", "POST", "/api/boards/{id}/items/{itemId}/browser-command", "Execute the existing typed board-browser command, retaining board scope and browser ownership.", AppJsonContext.Default.BrowserCommandRequest);
        yield return BoardItem("board_launch", "POST", "/api/boards/{id}/items/{itemId}/launch", "Launch the item using exact requestId, expectedRevision, entry, workingDirectory, launchCommand and profile.", BoardJsonContext.Default.BoardLaunchRequest);
        yield return Board("board_launches", "GET", "/api/boards/{id}/launches", "List board launches with itemId, limit and offset filters.").With(Q("itemId"), Q("limit", "integer"), Q("offset", "integer"));

        yield return D("update_status", "GET", "/api/update/check", "Check the existing update channel without applying an update.");
        yield return D("update_apply", "POST", "/api/update/apply", "Start the existing update workflow. The connection may restart; never replay an unobserved apply automatically. Inspect update_status after reconnection.")
            .With(Q("source"), Q("detached", "boolean", "true"), Q("forceFull", "boolean", "false"));
    }

    private static McpApiOperation D(string name, string method, string path, string description) => new(name, method, path, description);
    private static McpApiOperation Body(string name, string method, string path, string description, JsonTypeInfo type) =>
        new(name, method, path, description) { BodyType = type };
    private static McpApiOperation Target(string name, string method, string path, string description, JsonTypeInfo? type = null) =>
        new(name, method, path, description) { SessionRoute = "id", BodyType = type };
    private static McpApiOperation ScopedBody(string name, string method, string path, string description, JsonTypeInfo type) =>
        new(name, method, path, description) { SessionBody = "sessionId", BodyType = type };
    private static McpApiOperation Remote(string name, string path, string remote, string description) =>
        new(name, "GET", path, description) { RemotePattern = remote };
    private static McpApiOperation Board(string name, string method, string path, string description, JsonTypeInfo? type = null) =>
        new McpApiOperation(name, method, path, description) { BodyType = type }.Path("id", "boardId");
    private static McpApiOperation BoardItem(string name, string method, string path, string description, JsonTypeInfo? type = null) =>
        Board(name, method, path, description, type).Path("itemId", "itemId");
    private static McpApiParameter Q(string name, string type = "string", string? defaultValue = null, bool required = false) =>
        new(name, name, new JsonObject { ["type"] = type }, required, defaultValue);
    private static McpApiParameter P(string name, string? query, JsonObject schema) => new(name, query, schema);
    private static McpApiParameter SessionQuery(bool defaultSelf = true) =>
        new("targetSessionId", "sessionId", new JsonObject { ["type"] = "string", ["minLength"] = 1 }, SelfDefault: defaultSelf);
    private static JsonObject DelaySchema() => new() { ["type"] = new JsonArray("string", "number"),
        ["description"] = "Duration: 30s, 5m, 2h, 1d. Bare numeric values mean minutes, matching the CLI." };
    internal static int ParseDelay(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        var match = DurationRegex().Match(text.Trim());
        if (!match.Success || !decimal.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
            throw new McpApiArgumentException("Invalid duration. Use values such as 30s, 5m, 2h, or 1d.");
        var multiplier = match.Groups[2].Value.ToLowerInvariant() switch { "ms" => 1L, "s" => 1000L, "h" => 3_600_000L, "d" => 86_400_000L, _ => 60_000L };
        if (amount <= 0 || amount > int.MaxValue / (decimal)multiplier)
            throw new McpApiArgumentException("Duration must be positive and at most 2147483647 milliseconds.");
        var milliseconds = decimal.Round(amount * multiplier, 0, MidpointRounding.ToEven);
        if (milliseconds < 1 || milliseconds > int.MaxValue)
            throw new McpApiArgumentException("Duration rounds outside the supported millisecond range.");
        return (int)milliseconds;
    }

    [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?)(ms|s|m|h|d)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex DurationRegex();

    private static McpApiOperation With(this McpApiOperation operation, params McpApiParameter[] parameters)
    {
        operation.Parameters.AddRange(parameters);
        return operation;
    }
    private static McpApiOperation Path(this McpApiOperation operation, string route, string argument)
    {
        operation.Routes.Add(route, argument);
        return operation;
    }
}
