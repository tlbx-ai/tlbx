namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>
/// Authoritative migration requirements for the public generated CLI surface.
/// Tool discovery is only a structural check: every acceptance scenario still
/// needs behavioral evidence before the CLI can be considered replaceable.
/// </summary>
internal static class TlbxMcpCliParity
{
    internal sealed record Requirement(
        string Command,
        string PowerShellCommand,
        string Category,
        string[] McpTools,
        string AcceptanceScenario);

    internal sealed record Subcommand(string Command, string McpTool, string AcceptanceScenario);

    internal static IReadOnlyList<Requirement> Commands { get; } =
    [
        E("context", "Context", "context", "context_get,context_repair,context_export", "Verified server/self/preview context, repair errors and child-context propagation replace all text/JSON/shell export forms without trusting inherited IDs."),
        E("run_isolated", "RunIsolated", "process", "process_run_isolated", "Exact executable/argument boundaries, caller identity/cwd, detached stdin and separate durable stdout/stderr; retain active runs during bounded cleanup."),
        E("query", "Query", "browser", "browser_query", "Selectors, depth and text-only results preserve Unicode and multiple-match reporting."),
        E("click", "Click", "browser", "browser_click", "Click the scoped owner's selector, preserving match errors and navigation behavior."),
        E("fill", "Fill", "browser", "browser_fill", "Literal multiline/quoted/Unicode values reach the scoped element without shell escaping."),
        E("session", "Session", "context", "context_get", "Return the verified calling session, never a cwd/title/inherited-ID guess."),
        E("preview", "Preview", "context", "context_get,browser_previews,browser_batch", "Select a named preview per client or pass explicit previewName through a scoped sequence; concurrent clients cannot change each other's scope."),
        E("exec", "Exec", "browser", "browser_evaluate", "Evaluate exact multiline JavaScript in the scoped owner and return structured values/errors."),
        E("wait", "Wait", "browser", "browser_wait", "Selector wait honors timeout, cancellation and absence without replaying a mutation."),
        E("screenshot", "Screenshot", "browser", "browser_screenshot", "Viewport/full-page capture returns an image and optional workspace artifact without stealing preview ownership."),
        E("batch", "Batch", "browser", "browser_batch", "Ordered typed commands preserve timeout, per-command results and named-preview scope."),
        E("snapshot", "Snapshot", "browser", "browser_snapshot", "Return the complete snapshot or explicit continuation/artifact; optional workspace saving preserves the CLI artifact use case."),
        E("outline", "Outline", "browser", "browser_outline", "Requested depth produces the scoped page structure without changing focus."),
        E("attrs", "Attrs", "browser", "browser_attributes", "Return attributes for the selected element and exact no/multiple-match diagnostics."),
        E("css", "Css", "browser", "browser_css", "Requested computed-property names and selector remain separate typed arguments."),
        E("log", "Log", "browser", "browser_console", "Console filters error/warn/all preserve the scoped browser log and ordering."),
        E("text", "Text", "browser", "browser_query", "Default body or explicit selector returns text-only content without HTML/file indirection."),
        E("scroll", "Scroll", "browser", "browser_scroll", "Window/container scroll supports signed X/Y deltas and absolute edge directions."),
        E("wheel", "Wheel", "browser", "browser_wheel", "Optional selector, signed direction/delta and steps return measured scroll progress."),
        E("agent_wheel", "AgentWheel", "browser", "browser_agent_wheel", "Wheel the explicit structured-agent history target and report actual position/progress."),
        E("submit", "Submit", "browser", "browser_submit", "Default first form or explicit selector submits once in the scoped browser."),
        E("url", "Url", "browser", "browser_url", "Return the upstream URL, not a tlbx proxy URL."),
        E("links", "Links", "browser", "browser_links", "Return scoped page links and targets without navigation."),
        E("forms", "Forms", "browser", "browser_forms", "Default/all or selected forms expose the existing structure and values."),
        E("navigate", "Navigate", "preview", "browser_navigate", "Navigate the named preview and verify controllability without changing the user's active session."),
        E("open", "Open", "preview", "browser_open", "Hidden open is default; explicit claim/activate retain owner selection and actual readiness checks."),
        E("close_preview", "ClosePreview", "preview", "browser_close", "Close default preview or remove the named preview with the existing lifecycle rules."),
        new("mt_with_preview", "Invoke-MtPreview", "preview", ["browser_with_preview", "workflow_run"], "Temporary named preview runs browser-only or cross-domain typed tool steps with guaranteed cleanup on success, failure and cancellation; original client scope survives."),
        E("reload", "Reload", "preview", "browser_reload", "Soft reload preserves the intended target and scope."),
        E("forcereload", "ForceReload", "preview", "browser_reload", "Force reload applies cache-busting while preserving session and named preview."),
        E("target", "Target", "preview", "browser_target", "Read the selected named-preview target and state."),
        E("cookies", "Cookies", "preview", "browser_cookies", "Read the scoped cookie jar without mixing another session or preview."),
        E("previews", "Previews", "preview", "browser_previews", "List only the selected session's previews with stable names and state."),
        E("claim_preview", "ClaimPreview", "preview", "browser_claim", "Explicit browserId ownership handoff is required; offline owners never move implicitly."),
        E("claim_main_browser", "ClaimMainBrowser", "preview", "browser_claim_main", "Explicit leading-browser selection preserves the distinct terminal-size ownership contract."),
        E("capabilities", "Capabilities", "browser", "browser_capabilities", "Discover actual supported commands and live readiness as structured data, without guessing from installed names."),
        E("topic", "Topic", "session", "session_set_topic", "Set arbitrary Unicode topic text or explicitly clear it on self/authorized target."),
        E("repo", "Repo", "repository", "repos_list,repo_bind,repo_remove,repo_refresh", "List/status, bind role/label, remove and refresh honor explicit target session and canonical repository paths."),
        E("inspect", "Inspect", "browser", "browser_inspect", "Return coherent page/status/proxy diagnostics, optionally with an inline screenshot."),
        E("clearcookies", "ClearCookies", "preview", "browser_clear_cookies", "Clear both browser-side and server-side jar state for this preview."),
        E("clearstate", "ClearState", "preview", "browser_clear_state", "Clear scoped cookies/storage/cache/service workers without affecting siblings."),
        E("hardreload", "HardReload", "preview", "browser_reload,browser_clear_cookies", "Hard reload performs the same cookie clearing and fresh-load sequence as the CLI."),
        E("preview_reset", "PreviewReset", "preview", "browser_preview_reset", "Optionally retarget, clear scoped state and hard-reload as one observable workflow."),
        E("proxylog", "ProxyLog", "browser", "browser_proxylog", "Return bounded request details for the selected preview, including requested limit."),
        E("proxylog_summary", "ProxyLogSummary", "browser", "browser_proxylog_summary", "Return the scoped compact request/status/error summary with requested limit."),
        E("apply_update", "ApplyUpdate", "update", "update_apply", "Explicit source selection, detached update and verified server/version recovery preserve a lost-response/uncertain-outcome distinction."),
        E("boards", "Boards", "board", "boards_list", "List boards through the existing board service."),
        E("board", "Board", "board", "boards_list,board_get,board_item_get,board_create,board_update,board_set_viewport,board_delete,board_item_add,board_item_write,board_item_remove,board_browser_command,board_launch,board_launches", "Every board action retains ID/revision checks, item typing and launch/browser semantics; stale writes fail explicitly."),
        E("sessions", "Sessions", "session", "sessions_list", "Return the same terminal/controller session projection and live supervisor information as the CLI."),
        E("buffer", "Buffer", "terminal", "terminal_buffer", "Read the explicit terminal buffer while preserving binary/text/ANSI distinctions."),
        E("redraw", "Redraw", "terminal", "terminal_redraw", "Ask the foreground console application to repaint without inventing provider turns."),
        E("tail", "Tail", "terminal", "terminal_tail", "Honor line count and ANSI stripping for self or an authorized explicit terminal."),
        E("sendtext", "SendText", "terminal", "terminal_send_text", "Send exact literal text without implicit Enter or shell re-quoting."),
        E("paste", "Paste", "terminal", "terminal_paste", "Multiline, bracketed and file-path paste use the existing paste/upload path and do not auto-submit."),
        E("prompt", "Prompt", "terminal", "terminal_prompt", "State-aware send-and-submit keeps delay/submit policy and sends the literal prompt once."),
        E("prompt_now", "PromptNow", "terminal", "terminal_prompt_now", "Interrupt then atomically send/submit with requested delay, preserving duplicate/unknown-outcome diagnostics."),
        E("slash", "Slash", "terminal", "terminal_slash", "Slash command normalization and one submission follow the existing prompt contract."),
        E("wake", "Wake", "queue", "queue_schedule", "One-shot delay preserves exact legacy unit equivalents, title/source and target session."),
        E("recur", "Recur", "queue", "queue_schedule", "Recurring interval persists and repeats until explicitly cancelled; units and target remain exact."),
        E("queue", "Queue", "queue", "queue_list", "List one-shot and recurring queue items for the requested session."),
        E("wake_cancel", "WakeCancel", "queue", "queue_cancel", "Cancel only the explicitly identified queue item."),
        E("sendkeys", "SendKeys", "terminal", "terminal_send_keys", "Ordered named keys, modifiers and multiple-key sequences preserve their transport bytes."),
        E("enter", "Enter", "terminal", "terminal_send_keys", "Send the Enter key to the intended terminal only."),
        E("ctrlc", "Ctrlc", "terminal", "terminal_send_keys", "Send C-c to the intended terminal, distinct from a structured-agent interrupt."),
        E("escape", "Escape", "terminal", "terminal_send_keys", "Send Escape without an implicit prompt or submit."),
        E("up", "Up", "terminal", "terminal_send_keys", "Send Up to the intended terminal only."),
        E("down", "Down", "terminal", "terminal_send_keys", "Send Down to the intended terminal only."),
        E("left", "Left", "terminal", "terminal_send_keys", "Send Left to the intended terminal only."),
        E("right", "Right", "terminal", "terminal_send_keys", "Send Right to the intended terminal only."),
        E("inject", "Inject", "session", "session_inject_guidance", "Ensure guidance/helpers in the target cwd while keeping the CLI supported."),
        E("notify", "Notify", "notification", "notification_send", "Target, title, priority and literal notification body preserve existing notification behavior."),
        E("activity", "Activity", "session", "session_activity", "Honor time window and bell-history limit for the explicit session."),
        E("attention", "Attention", "session", "sessions_attention", "Preserve ranked fleet attention and the agentOnly filter."),
        E("control_plane", "ControlPlane", "control-plane", "control_plane_get", "Read the local or explicitly selected remote machine's deterministic control plane."),
        E("agent_capabilities", "AgentCapabilities", "control-plane", "agent_capabilities", "Preserve local/remote-machine and per-session runtime capability distinctions."),
        E("events", "Events", "control-plane", "control_events", "Read exact mutation events with sequence, limit and optional remote machine filters."),
        E("dispatch", "Dispatch", "control-plane", "agent_dispatch", "Explicit ordered target fan-out sends structured turn text once per selected session."),
        E("work_list", "WorkList", "control-plane", "work_list", "Preserve state, kind, session and limit filters."),
        E("work_add", "WorkAdd", "control-plane", "work_add", "Retain kind/title/summary/nextAction/priority/project/dedupe/session/URL/repositoryPath and attributable source."),
        E("work_update", "WorkUpdate", "control-plane", "work_update", "Retain state and absent-versus-explicit-empty field updates, including URL and repositoryPath."),
        E("work_delete", "WorkDelete", "control-plane", "work_delete", "Delete only the explicitly identified work item through the existing service."),
        E("publish_status", "PublishStatus", "control-plane", "status_publish", "Publish explicit working/waiting/needsInput/blocked/done state with all task/project/path fields and source."),
        E("status_list", "StatusList", "control-plane", "status_list", "List all or explicitly filtered published session status."),
        E("status_clear", "StatusClear", "control-plane", "status_clear", "Clear only the selected session's published status."),
        E("checkpoint", "Checkpoint", "control-plane", "checkpoint_add", "Preserve kind, summary, details, project, session, repositoryPath and source."),
        E("checkpoints", "Checkpoints", "control-plane", "checkpoints_list", "Preserve session/kind/limit filters and checkpoint ordering."),
        E("input_history", "InputHistory", "history", "input_history_list", "Preserve session/kind/limit filtering and canonical prompt/paste/upload entries."),
        E("input_history_show", "InputHistoryShow", "history", "input_history_get", "Return the exact identified history entry, including attachment metadata."),
        E("input_history_replay", "InputHistoryReplay", "history", "input_history_replay", "Replay the identified entry into the explicitly authorized target through the existing replay contract."),
        E("input_history_delete", "InputHistoryDelete", "history", "input_history_delete", "Delete only the explicitly identified history entry."),
        E("input_history_clear", "InputHistoryClear", "history", "input_history_clear", "Clear only the selected session's input history."),
        E("bootstrap", "Bootstrap", "session", "session_bootstrap", "Keep name/cwd/profile/guidance, launch and slash delays, ordered slash commands and terminal worker identity."),
        E("acp_new", "AcpNew", "agent-controller", "agent_session_create", "Create an explicit controller session using the requested profile; never reclassify an ordinary terminal."),
        E("acp_history", "AcpHistory", "agent-controller", "agent_history", "Preserve session, startIndex/count/viewportWidth and canonical structured history."),
        E("acp_turn", "AcpTurn", "agent-controller", "agent_turn", "Retain literal text and explicit model/effort/planMode/permissionMode provider options."),
        E("acp_interrupt", "AcpInterrupt", "agent-controller", "agent_interrupt", "Interrupt the explicit active turn using its optional turn ID; never synthesize terminal Ctrl-C."),
        E("acp_steer", "AcpSteer", "agent-controller", "agent_steer", "expectedTurnId is authoritative and stale steering is rejected rather than retargeted."),
        E("acp_compact", "AcpCompact", "agent-controller", "agent_compact", "Request canonical provider compaction for the selected controller session."),
        E("new_session", "NewSession", "session", "session_create", "Create the requested shell/cwd without injecting provider flags or stealing unrelated browser ownership."),
        E("split", "Split", "layout", "tmux_split", "Preserve vertical/horizontal adjacency, target pane, cwd, optional initial command, inherited dimensions and tmux parent/layout identity."),
        E("detach", "Detach", "preview", "browser_detach", "Detach the selected preview to its popup without transferring ownership implicitly."),
        E("dock", "Dock", "preview", "browser_dock", "Dock the selected popup preview back into the existing workspace."),
        E("viewport", "Viewport", "preview", "browser_viewport", "Preserve width/height and the 0/0 reset behavior on the selected preview."),
        E("mobile", "Mobile", "preview", "browser_mobile", "Preserve action/profile and owning-tab-bound local Chrome device control."),
        E("status", "Status", "preview", "browser_status,browser_target", "Report real bridge readiness and target fallback without guessing that an ACK means controllable.")
    ];

    // The browser text endpoint has public operations beyond generated helper names.
    internal static IReadOnlyList<Subcommand> BrowserCommands { get; } =
    [
        B("query", "browser_query"), B("click", "browser_click"), B("scroll", "browser_scroll"),
        B("wheel", "browser_wheel"), B("fill", "browser_fill"), B("exec", "browser_evaluate"),
        B("screenshot", "browser_screenshot"), B("snapshot", "browser_snapshot"), B("wait", "browser_wait"),
        B("navigate", "browser_navigate"), B("reload", "browser_reload"), B("outline", "browser_outline"),
        B("attrs", "browser_attributes"), B("css", "browser_css"), B("log", "browser_console"),
        B("links", "browser_links"), B("submit", "browser_submit"), B("forms", "browser_forms"),
        B("url", "browser_url"), B("clearcookies", "browser_clear_cookies"), B("clearstate", "browser_clear_state"),
        B("mobile", "browser_mobile"), B("status", "browser_status"), B("claim", "browser_claim"),
        B("claim-main", "browser_claim_main"), B("capabilities", "browser_capabilities"),
        B("proxylog-summary", "browser_proxylog_summary"), B("inspect", "browser_inspect")
    ];

    internal static IReadOnlyList<Subcommand> BoardCommands { get; } =
    [
        new("list", "boards_list", "List boards."),
        new("get", "board_get", "Read an existing board by ID."),
        new("item", "board_item_get", "Read an existing item by board/item ID."),
        new("create", "board_create", "Create from the complete typed board payload."),
        new("update", "board_update", "Preserve revision-based board update semantics."),
        new("viewport", "board_set_viewport", "Preserve typed viewport mutation semantics."),
        new("delete", "board_delete", "Require expectedRevision; stale deletion is rejected."),
        new("add", "board_item_add", "Add every existing item type from its complete payload."),
        new("write", "board_item_write", "Write item payload with existing revision validation."),
        new("remove", "board_item_remove", "Require expectedRevision for explicit item removal."),
        new("browser", "board_browser_command", "Preserve board-item browser-command dispatch."),
        new("launch", "board_launch", "Preserve launch payload, launch identity and dispatch result."),
        new("launches", "board_launches", "Read actual board launch records.")
    ];

    internal static IReadOnlyList<Subcommand> RepoCommands { get; } =
    [
        new("list", "repos_list", "Read the selected session's repository watch set."),
        new("status", "repos_list", "Read the same complete status projection as list."),
        new("add", "repo_bind", "Bind canonical path with explicit role/label/session."),
        new("remove", "repo_remove", "Unbind only the exact repository root from this session."),
        new("rm", "repo_remove", "Preserve the remove alias use case."),
        new("refresh", "repo_refresh", "Refresh all or one exact root in the selected session.")
    ];

    // These are CLI parameter names, not an instruction to retain shell syntax
    // in MCP. The inventory test detects newly added flags/options so their
    // meaning must be covered explicitly in the corresponding typed tool.
    internal static IReadOnlyDictionary<string, string> PowerShellParameters { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Mt-Context"] = "Format", ["Mt-RunIsolated"] = "Executable,ArgumentList",
            ["Mt-Query"] = "Selector,Text", ["Mt-Click"] = "Selector", ["Mt-Fill"] = "Selector,Value",
            ["Mt-Session"] = "", ["Mt-Preview"] = "Name", ["Mt-Exec"] = "Code",
            ["Mt-Wait"] = "Selector,Timeout", ["Mt-Screenshot"] = "FullPage",
            ["Mt-Batch"] = "Commands,Timeout", ["Mt-Snapshot"] = "", ["Mt-Outline"] = "Depth",
            ["Mt-Attrs"] = "Selector", ["Mt-Css"] = "Selector,Props", ["Mt-Log"] = "Filter",
            ["Mt-Text"] = "Selector", ["Mt-Scroll"] = "Selector,DeltaY,DeltaX,To",
            ["Mt-Wheel"] = "Selector,Direction,Steps", ["Mt-AgentWheel"] = "Direction,Steps,SessionId",
            ["Mt-Submit"] = "Selector", ["Mt-Url"] = "", ["Mt-Links"] = "", ["Mt-Forms"] = "Selector",
            ["Mt-Navigate"] = "Url", ["Mt-Open"] = "Url,Claim,Activate", ["Mt-ClosePreview"] = "",
            ["Invoke-MtPreview"] = "Name,Url,ScriptBlock,ArgumentList",
            ["Mt-Reload"] = "", ["Mt-ForceReload"] = "", ["Mt-Target"] = "", ["Mt-Cookies"] = "",
            ["Mt-Previews"] = "", ["Mt-ClaimPreview"] = "", ["Mt-ClaimMainBrowser"] = "BrowserId",
            ["Mt-Capabilities"] = "Json", ["Mt-Topic"] = "InputArgs,Clear",
            ["Mt-Repo"] = "Action,PathOrRoot,Role,Label,SessionId", ["Mt-Inspect"] = "Screenshot",
            ["Mt-ClearCookies"] = "", ["Mt-ClearState"] = "", ["Mt-HardReload"] = "",
            ["Mt-PreviewReset"] = "Url", ["Mt-ProxyLog"] = "Limit", ["Mt-ProxyLogSummary"] = "Limit",
            ["Mt-ApplyUpdate"] = "Source", ["Mt-Boards"] = "", ["Mt-Board"] = "InputArgs",
            ["Mt-Sessions"] = "", ["Mt-Buffer"] = "InputArgs", ["Mt-Redraw"] = "InputArgs",
            ["Mt-Tail"] = "InputArgs", ["Mt-SendText"] = "InputArgs",
            ["Mt-Paste"] = "Bracketed,File,InputArgs", ["Mt-Prompt"] = "InputArgs,DelayMs",
            ["Mt-PromptNow"] = "InputArgs,DelayMs", ["Mt-Slash"] = "InputArgs,DelayMs",
            ["Mt-Wake"] = "InputArgs", ["Mt-Recur"] = "InputArgs", ["Mt-Queue"] = "InputArgs",
            ["Mt-WakeCancel"] = "QueueId", ["Mt-SendKeys"] = "InputArgs", ["Mt-Enter"] = "InputArgs",
            ["Mt-Ctrlc"] = "InputArgs", ["Mt-Escape"] = "InputArgs", ["Mt-Up"] = "InputArgs",
            ["Mt-Down"] = "InputArgs", ["Mt-Left"] = "InputArgs", ["Mt-Right"] = "InputArgs",
            ["Mt-Inject"] = "InputArgs", ["Mt-Notify"] = "Message,Title,Priority,SessionId",
            ["Mt-Activity"] = "InputArgs", ["Mt-Attention"] = "AgentOnly", ["Mt-ControlPlane"] = "MachineId",
            ["Mt-AgentCapabilities"] = "MachineId", ["Mt-Events"] = "After,Limit,MachineId",
            ["Mt-Dispatch"] = "SessionId,Text", ["Mt-WorkList"] = "State,Kind,SessionId,Limit",
            ["Mt-WorkAdd"] = "Kind,Title,Summary,NextAction,Priority,Project,DedupeKey,SessionId,Url,RepositoryPath",
            ["Mt-WorkUpdate"] = "Id,State,Summary,NextAction,Url,RepositoryPath", ["Mt-WorkDelete"] = "Id",
            ["Mt-PublishStatus"] = "State,Summary,CurrentTask,NextAction,Project,SessionId,RepositoryPath",
            ["Mt-StatusList"] = "SessionId", ["Mt-StatusClear"] = "SessionId",
            ["Mt-Checkpoint"] = "Kind,Summary,Details,Project,SessionId,RepositoryPath",
            ["Mt-Checkpoints"] = "SessionId,Kind,Limit", ["Mt-InputHistory"] = "SessionId,Kind,Limit",
            ["Mt-InputHistoryShow"] = "Id", ["Mt-InputHistoryReplay"] = "Id,TargetSessionId",
            ["Mt-InputHistoryDelete"] = "Id", ["Mt-InputHistoryClear"] = "SessionId",
            ["Mt-Bootstrap"] = "Name,Cwd,Profile,SlashCommands", ["Mt-AcpNew"] = "Name,Cwd,Profile",
            ["Mt-AcpHistory"] = "SessionId,StartIndex,Count,ViewportWidth",
            ["Mt-AcpTurn"] = "Text,SessionId,Model,Effort,PlanMode,PermissionMode",
            ["Mt-AcpInterrupt"] = "SessionId,TurnId", ["Mt-AcpSteer"] = "ExpectedTurnId,Text,SessionId",
            ["Mt-AcpCompact"] = "SessionId", ["Mt-NewSession"] = "Shell,Cwd", ["Mt-Split"] = "Horizontal",
            ["Mt-Detach"] = "", ["Mt-Dock"] = "", ["Mt-Viewport"] = "Width,Height",
            ["Mt-Mobile"] = "Action,Profile", ["Mt-Status"] = ""
        };

    /// <summary>Missing discovery names only. An empty result is NOT functional parity evidence.</summary>
    internal static string[] MissingTools(IEnumerable<string> registeredNames)
    {
        var available = new HashSet<string>(registeredNames, StringComparer.Ordinal);
        return Commands.SelectMany(requirement => requirement.McpTools)
            .Concat(BrowserCommands.Select(requirement => requirement.McpTool))
            .Concat(BoardCommands.Select(requirement => requirement.McpTool))
            .Concat(RepoCommands.Select(requirement => requirement.McpTool))
            .Distinct(StringComparer.Ordinal)
            .Where(name => !available.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static Requirement E(string command, string powershell, string category, string tools, string acceptance) =>
        new("mt_" + command, "Mt-" + powershell, category,
            tools.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), acceptance);

    private static Subcommand B(string command, string tool) =>
        new(command, tool, "Preserve typed CLI parameters, scoped ownership, error reporting and cancellation; behavioral verification is required.");
}
