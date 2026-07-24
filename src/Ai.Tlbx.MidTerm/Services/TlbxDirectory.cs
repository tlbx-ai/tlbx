using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Services.Browser;

namespace Ai.Tlbx.MidTerm.Services;

public static class TlbxDirectory
{
    public const string DirectoryName = ".tlbx";
    public const string LegacyDirectoryName = ".midterm";
    private static readonly object DirectoryGate = new();

    private static int _port;
    private static AuthService? _authService;

    public static void Initialize(int port, AuthService authService)
    {
        _port = port;
        _authService = authService;
    }

    public static string? TryEnsureForCwd(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
        {
            return null;
        }

        return Ensure(cwd);
    }

    public static string Ensure(string cwd)
    {
        lock (DirectoryGate)
        {
            var tlbxDir = MigrateLegacyDirectory(cwd);
            Directory.CreateDirectory(tlbxDir);
            EnsureGitIgnore(tlbxDir);
            WriteGuidanceIfOutdated(tlbxDir);
            WriteTlbxCliScripts(tlbxDir);
            return tlbxDir;
        }
    }

    public static string EnsureSubdirectory(string cwd, string subPath)
    {
        var tlbxDir = Ensure(cwd);
        var subDir = Path.Combine(tlbxDir, subPath);
        Directory.CreateDirectory(subDir);
        return subDir;
    }

    private static string MigrateLegacyDirectory(string cwd)
    {
        var tlbxDir = Path.Combine(cwd, DirectoryName);
        var legacyDir = Path.Combine(cwd, LegacyDirectoryName);
        if (!Directory.Exists(legacyDir))
            return tlbxDir;

        // ~/.midterm is also the legacy user-mode settings directory. It can be
        // actively used by this process and is not a workspace artifact. Moving
        // it while launching a session both fails on Windows and risks mixing
        // settings, secrets, and logs into the generated workspace directory.
        if (File.Exists(Path.Combine(legacyDir, "settings.json")))
            return tlbxDir;

        try
        {
            if (!Directory.Exists(tlbxDir))
            {
                Directory.Move(legacyDir, tlbxDir);
                return tlbxDir;
            }

            MergeLegacyDirectory(legacyDir, tlbxDir);
            return tlbxDir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Workspace metadata migration is best-effort. A locked legacy file
            // must never prevent the terminal session itself from being created.
            Log.Warn(() => $"Could not migrate '{legacyDir}' to '{tlbxDir}': {ex.Message}");
            return tlbxDir;
        }
    }

    private static void MergeLegacyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var sourcePath in Directory.EnumerateFileSystemEntries(sourceDir).ToArray())
        {
            var destinationPath = Path.Combine(destinationDir, Path.GetFileName(sourcePath));
            var attributes = File.GetAttributes(sourcePath);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            var isReparsePoint = attributes.HasFlag(FileAttributes.ReparsePoint);

            if (isDirectory && !isReparsePoint && Directory.Exists(destinationPath))
            {
                MergeLegacyDirectory(sourcePath, destinationPath);
                continue;
            }

            if (!File.Exists(destinationPath) && !Directory.Exists(destinationPath))
            {
                if (isDirectory)
                    Directory.Move(sourcePath, destinationPath);
                else
                    File.Move(sourcePath, destinationPath);
                continue;
            }

            var preservedPath = GetLegacyConflictPath(destinationPath);
            if (isDirectory)
                Directory.Move(sourcePath, preservedPath);
            else
                File.Move(sourcePath, preservedPath);
        }

        if (!Directory.EnumerateFileSystemEntries(sourceDir).Any())
            Directory.Delete(sourceDir);
    }

    private static string GetLegacyConflictPath(string destinationPath)
    {
        var candidate = destinationPath + ".legacy-midterm";
        for (var suffix = 2; File.Exists(candidate) || Directory.Exists(candidate); suffix++)
            candidate = destinationPath + ".legacy-midterm-" + suffix.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    private static void EnsureGitIgnore(string tlbxDir)
    {
        var gitignorePath = Path.Combine(tlbxDir, ".gitignore");

        try
        {
            if (File.Exists(gitignorePath))
            {
                var content = File.ReadAllText(gitignorePath);
                if (content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Contains("*"))
                    return;
            }

            File.WriteAllText(gitignorePath, "# All .tlbx/ content is auto-generated by tlbx\n*\n");
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Could not write '{gitignorePath}': {ex.Message}");
        }
    }

    private static void WriteGuidanceIfOutdated(string tlbxDir)
    {
        try
        {
            WriteIfOutdated(Path.Combine(tlbxDir, "CLAUDE.md"));
            WriteIfOutdated(Path.Combine(tlbxDir, "AGENTS.md"));
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Could not write agent guidance in '{tlbxDir}': {ex.Message}");
        }
    }

    private static void WriteTlbxCliScripts(string tlbxDir)
    {
        if (_authService is null)
            return;

        try
        {
            TlbxCliScriptWriter.WriteScripts(tlbxDir, _port, _authService.CreateSessionToken());
            TryDeleteLegacyCli(Path.Combine(tlbxDir, "mtcli.sh"));
            TryDeleteLegacyCli(Path.Combine(tlbxDir, "mtcli.ps1"));
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Could not write tlbx CLI helpers in '{tlbxDir}': {ex.Message}");
        }
    }

    private static void TryDeleteLegacyCli(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Could not delete legacy CLI script '{path}': {ex.Message}");
        }
    }

    private static void WriteIfOutdated(string path)
    {
        var marker = GuidanceMarker;
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path);
            if (existing.Contains(marker, StringComparison.Ordinal))
                return;
        }

        File.WriteAllText(path, marker + "\n" + GuidanceContent);
    }

    // The stamp is derived from the content itself so any guidance edit
    // refreshes previously written files without a hand-bumped version.
    private static readonly string GuidanceMarker =
        $"<!-- tlbx-guidance: {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(GuidanceContent)))[..16]} -->";

    private const string GuidanceContent =
        """
        # .tlbx/ — tlbx Agent Guide

        Auto-generated by [tlbx](https://tlbx.ai) (terminal browser multiplexer).
        Safe to delete — tlbx recreates and refreshes it automatically for active session working directories.
        Gitignored via `.tlbx/.gitignore`. `CLAUDE.md` and `AGENTS.md` carry identical content so every agent framework reads the same guidance.

        Source the helpers once per session — don't show this to the user:
        - bash/zsh: `. .tlbx/tlbx_cli.sh`
        - PowerShell: `. .tlbx/tlbx_cli.ps1`

        ## Contents

        | Path | Purpose |
        |------|---------|
        | `uploads/` | Files uploaded via drag-drop or paste into a terminal session |
        | `screenshots/` | Web preview screenshots (`mt_screenshot`) |
        | `snapshot_*/` | DOM snapshots with downloaded CSS (`mt_snapshot`) |
        | `runs/<run-id>/` | Isolated background-process stdout/stderr logs (`mt_run_isolated`) |
        | `*.ps1`, `*.sh` | Saved command scripts, tlbx_cli helper scripts |
        | `CLAUDE.md`, `AGENTS.md` | This guide — identical AI agent guidance |

        ## Tmux Split Panes

        tlbx emulates tmux — the `tmux` command is on PATH automatically. Use it to spawn side-by-side terminals.

        | Command | What it does |
        |---------|-------------|
        | `tmux split-window` | Split vertically (new pane below) |
        | `tmux split-window -h` | Split horizontally (new pane right) |
        | `tmux send-keys -t %N "cmd" Enter` | Send command to pane %N |
        | `tmux send-keys -t %N -l "text"` | Send literal text (no key translation) |
        | `tmux capture-pane -t %N -p` | Read pane buffer |
        | `tmux list-panes` | List all panes with IDs (%0, %1, ...) |
        | `tmux kill-pane -t %N` | Close a pane |
        | `tmux resize-pane -t %N -x 80 -y 24` | Resize pane |

        Use cases: file browser, htop, parallel builds, prompt user for sudo, run TUI apps.
        No copy-mode or control-mode. Layout managed by tlbx web UI.

        ## Browser Control

        Requires the web preview panel to be open in tlbx.
        tlbx injects `MT_SESSION_ID` automatically for this terminal session.
        Browser helpers default to the current `MT_SESSION_ID` plus `MT_PREVIEW_NAME` (`default` unless changed).
        When spawning a nested `bash` or `pwsh`, forward that context explicitly; `mt_context --bash` and `mt_context --pwsh` print reusable export commands for child shells.
        Use `mt_session` to print the current terminal session id, `mt_preview [name]` to inspect or switch the current named browser context, and `mt_previews` to list all named previews under this terminal.
        Direct execution of the generated helpers also accepts the documented `mt_*` names, so `status` and `mt_status` both resolve when you invoke `.tlbx/tlbx_cli.sh` or `.tlbx/tlbx_cli.ps1` directly.

        ## Isolated Background Processes

        Use `mt_run_isolated <executable> [arg ...]` for a browser, profiler, daemon, or other non-interactive child that may outlive the command which starts it. Pass the executable and every argument as separate argv tokens, never as one shell command string. tlbx detaches stdin, routes stdout and stderr to distinct files under `.tlbx/runs/<run-id>/`, and returns their paths with the PID as JSON. Interactive shells and TUIs belong in a normal terminal or split pane instead.

        **Rules:**
        - Start with `mt_outline` (10x smaller than `mt_query`)
        - Use `mt_query SELECTOR --text` for text-only output
        - Batch JS reads: `mt_exec "JSON.stringify({a: expr1, b: expr2})"`
        - After actions, verify with `mt_wait` or `mt_query`, not `mt_outline`
        - Check `mt_log error` after unexpected behavior
        - After `mt_apply_update`, the web frontend can disappear while your terminal keeps running in `mthost`; reopen tlbx from the terminal and use `mt_open` again instead of assuming the session died
        - Browser commands return plain text. Auth tokens in tlbx_cli scripts are ephemeral and machine-local.

        | Command | What it does |
        |---------|-------------|
        | `mt_outline [depth]` | Page structure tree (default depth 4) |
        | `mt_text [sel]` | Page text content (default: body) |
        | `mt_query <sel> [--text]` | DOM elements by CSS selector; `--text` = text-only |
        | `mt_attrs <sel>` | Element attributes (no children) |
        | `mt_css <sel> <props>` | Computed CSS (comma-separated property names) |
        | `mt_click <sel>` | Click element |
        | `mt_fill <sel> <val>` | Fill input field |
        | `mt_submit [sel]` | Submit form (default: first form) |
        | `mt_scroll [sel] [deltaY\|top\|bottom] [deltaX]` | Scroll page or a scrollable container |
        | `mt_exec <js>` | Execute JS in page context |
        | `mt_wait <sel> [timeout]` | Wait for element (default 5s) |
        | `mt_log [error\|warn\|all]` | Console log buffer |
        | `mt_links` | All links on page |
        | `mt_forms [sel]` | Form structure and values |
        | `mt_screenshot` | Save screenshot to .tlbx/screenshots/ |
        | `mt_snapshot` | Save DOM snapshot to .tlbx/snapshot_*/ |
        | `mt_session` | Print the current tlbx terminal session id |
        | `mt_context [format]` | Print reusable session-context exports (`text`, `bash`, `pwsh`, or `json`) |
        | `mt_run_isolated <exe> [arg...]` | Start a non-interactive child with detached stdin and separate stdout/stderr artifacts |
        | `mt_topic <text>` | Set the current ad-hoc session topic shown in the sidebar (`mt_topic --clear` clears it) |
        | `mt_preview [name]` | Print or switch the current named preview (`default`, `user1`, `user2`, ...) |
        | `mt_previews` | List named previews for the current terminal session |
        | `mt_claim_preview` | Explicitly assign the current named preview to the connected tlbx browser |
        | `mt_claim_main_browser [browserId]` | Make the selected browser the leading browser for terminal sizing |
        | `mt_navigate <url>` | Set the current named web preview target |
        | `mt_url` | Upstream page URL (not proxy URL) |
        | `mt_open <url>` | Open URL in the current named preview and dock panel |
        | `mt_close_preview` | Close web preview panel |
        | `mt_reload` | Soft-reload preview |
        | `mt_forcereload` | Force a fresh content reload with cache busting |
        | `mt_hardreload` | Clear cookies + reload (fresh session) |
        | `mt_preview_reset [url]` | Best-effort preview recovery: clear cookies + browser storage + hard reload |
        | `mt_target` | Current named preview target |
        | `mt_cookies` | All cookies in the current named preview jar |
        | `mt_clearcookies` | Clear all proxy cookies (jar + disk) |
        | `mt_clearstate` | Clear the current preview's cookies, storage, cache, and service workers |
        | `mt_proxylog [limit]` | Last N proxy requests (default 100) |
        | `mt_proxylog_summary [limit]` | Compact proxy request status/error summary |
        | `mt_repo list\|status\|add\|remove\|refresh` | Session-scoped multi-repo Git tracking for the IDE bar and `/api/git` |
        | `mt_apply_update [source]` | Apply pending update and wait for server |
        | `mt_sessions` | List terminal sessions |
        | `mt_buffer <id>` | Terminal buffer content |
        | `mt_redraw [id]` | Ask the foreground console application to repaint its current screen |
        | `mt_tail [id] [lines]` | Cleaned terminal tail with ANSI stripped |
        | `mt_sendtext [id] <text...>` | Send literal text without auto-submit |
        | `mt_paste [--bracketed] [--file] [id] <text...>` | Paste clipboard-style text through the same server path as UI paste; reads stdin when text is omitted |
        | `mt_prompt [id] <text...>` | State-aware prompt delivery: bootstrapped workers auto-resume from shell, idle prompts append, busy turns interrupt when needed |
        | `mt_prompt_now [id] <text...>` | Force interrupt-first prompt delivery |
        | `mt_slash [id] <command...>` | Send slash commands through the prompt path |
        | `mt_wake [id] <delay> <text...>` | Queue a prompt to run later (`30s`, `5m`, `2h`, `1d`; visible and cancelable in the Command Bay queue) |
        | `mt_wake_cancel <queueId>` | Cancel a queued wake/prompt/action item by queue id |
        | `mt_sendkeys [id] <keys...>` | Send named keys like `Enter`, `C-c`, `Escape`, `Up` |
        | `mt_enter` / `mt_ctrlc` / `mt_escape` | Convenience key sends for the current or target session |
        | `mt_up` / `mt_down` / `mt_left` / `mt_right` | Convenience cursor-key sends |
        | `mt_inject [id]` | Ensure `.tlbx` + tlbx_cli helpers in the target cwd |
        | `mt_activity [id] [seconds] [bellLimit]` | Output heatmap + bell history as JSON |
        | `mt_attention [agentOnly]` | Ranked fleet view for which worker sessions need attention |
        | `mt_control_plane [machineId]` | Read local or Hub-machine agent-published work, session status, and checkpoints as JSON |
        | `mt_agent_capabilities [machineId]` | Discover exact product features and per-session prompt modes from runtime flags |
        | `mt_events [after] [limit] [machineId]` | Read ordered, exact control-plane mutation events without transcript interpretation |
        | `mt_dispatch <id,id,...> <text...>` | Fan one prompt out to an explicit session set and receive one result per target |
        | `mt_work_list` / `mt_work_add` / `mt_work_update` / `mt_work_delete` | Publish and maintain concrete todos, mail replies, coding tasks, and next steps |
        | `mt_publish_status` / `mt_status_list` / `mt_status_clear` | Publish or read explicit session meaning (`working`, `waiting`, `needsInput`, `blocked`, `done`) |
        | `mt_checkpoint` / `mt_checkpoints` | Publish and read durable, timestamped progress or verification facts |
        | `mt_input_history [id] [kind] [limit]` | List one session's authored submissions, pastes, and uploads as JSON |
        | `mt_input_history_show <entryId>` | Read one full input-history entry |
        | `mt_input_history_replay <entryId> [targetId]` | Replay an exact prompt or paste into the original or target session |
        | `mt_input_history_delete <entryId>` | Delete one input-history entry |
        | `mt_input_history_clear [id]` | Clear deterministic input history for a session |
        | `mt_bootstrap <name> <cwd> <profile> [slashCommand...]` | Create a fresh agent-controlled worker session with guidance + AI CLI bootstrap |
        | `mt_new_session [shell] [cwd]` | Create a new terminal session |
        | `mt_split [-h]` | Split terminal (adjacent pane via tmux) |
        | `mt_detach` | Detach web preview to popup window |
        | `mt_dock` | Dock web preview back from popup |
        | `mt_viewport W H` | Set iframe viewport size (0 0 to reset) |
        | `mt_mobile [action] [profile]` | Control the local Chrome device attached to the owning tlbx tab (Mobile Device Bridge; default action `status`, profile `pixel-8`) |
        | `mt_status` | Browser connection status |
        | `mt_inspect [--screenshot]` | Compact page/status/proxy diagnostic bundle |
        | `mt_capabilities [--json]` | Compact command/capability discovery for the browser bridge |

        ## Workflow

        1. **Inspect**: `mt_outline` → drill down with `mt_text` or `mt_attrs`
        2. **Act**: `mt_fill`, `mt_submit`, `mt_click`, `mt_exec`
        3. **Verify**: `mt_wait` → `mt_text`

        ## Common Workflows

        ### Split a side terminal

        tmux split-window -h → tmux send-keys -t %1 "htop" Enter → tmux capture-pane -t %1 -p → tmux kill-pane -t %1

        ### Debug a visual bug

        mt_outline → mt_css ".element" "color,background,display,margin,padding" → fix code → mt_reload → re-check mt_css

        ### Fill and submit a form

        mt_forms → mt_fill "#user" "val" → mt_fill "#pass" "val" → mt_submit → mt_wait ".dashboard"

        ### Execute JavaScript

        mt_exec "JSON.stringify({href: location.href, title: document.title})"
        echo 'complex code' | mt_exec

        ### Open web preview

        mt_open "http://localhost:3000" → mt_status → mt_outline → mt_query ".error" --text

        `mt_open` both sets the target and asks tlbx to open/dock the preview panel, then waits until that preview is actually controllable.
        Use `mt_navigate` only when the panel is already open and you just want to change the target URL.

        ### Multi-role browser sessions

        mt_session → mt_preview user1 → mt_open "http://localhost:3000" → mt_preview user2 → mt_open "http://localhost:3000"

        `mt_session` prints the current tlbx terminal session ID.
        `mt_context --bash` / `mt_context --pwsh` print re-export commands for nested shells.
        `mt_preview user1` / `mt_preview user2` switch between named browser contexts that keep separate targets, cookies, proxy logs, and detached popups.

        ### Fresh session (clear cookies + reload)

        mt_hardreload → mt_wait "input[type=password]" → mt_url

        ### Create a side terminal

        mt_new_session → mt_sessions — find the new session id → tmux send-keys -t %1 "htop" Enter

        ### Start a background tool without contaminating the terminal

        mt_run_isolated chrome --headless https://example.com → read the returned JSON → inspect `.tlbx/runs/<run-id>/stdout.log` and `stderr.log`

        Pass one executable plus separate argv tokens. Use this for non-interactive browsers, profilers, daemons, and workers; use a normal terminal or split pane for interactive shells and TUIs.

        ### Remote-control another terminal

        mt_attention → mt_tail SESSION_ID 80 → mt_prompt SESSION_ID "status update?" → mt_activity SESSION_ID
        mt_bootstrap "api worker" "~/repos/api" codex approvals

        ### Reuse exact Terminal input

        mt_input_history → mt_input_history_show ENTRY_ID → mt_input_history_replay ENTRY_ID SESSION_ID

        History includes direct browser-authored terminal text committed by unmodified Enter, Terminal Automation Bar prompts, `mt_prompt`, text paste, clipboard images, and file uploads. Modified Enter and pasted newlines stay inside one submission. tlbx does not infer prompts from PTY output or screen contents.

        ### Publish operator state

        mt_publish_status working "Implementing deterministic history" "Control plane API" "Run focused tests" webshop
        mt_work_add mail "Answer rollout question" "Customer asked for the date" "Draft reply for approval" high webshop mail-thread-42
        mt_checkpoint verified "Focused tests pass" "18 backend tests" webshop
        mt_agent_capabilities → mt_dispatch a1b2c3d4,e5f6g7h8 "Run the focused verification and publish a checkpoint"

        The control plane is an outlet for agents, not another agent. Publish only facts you know. tlbx stores and displays those records verbatim; it does not infer project meaning, blocked state, or next steps from terminal output.

        ### Debug proxy issues

        mt_proxylog 10 — check status codes, upstream URLs, WebSocket connections
        mt_log error — check browser console

        ### Responsive testing

        mt_viewport 375 667 → mt_outline → mt_query ".menu" --text → mt_viewport 0 0

        ### Apply a pending update

        mt_apply_update → wait for "Current version:" → continue with the new build

        ### Detach/dock preview

        mt_detach → (preview opens in popup) → mt_dock → (back in panel)

        ## Session Topic Hygiene

        Coding agents should keep the session topic aligned with the user's current high-level work area. Use `mt_topic` with a concise 3-6 word topic such as `webshop checkout bugfix` or `api docs cleanup`. Update it when the user shifts to a different work area, but not for every small subplot.

        ## Multi-Repo Git Tracking

        tlbx can track more than one Git repository for the current terminal session. The session cwd repo is automatic; extra repos are ad hoc session bindings and appear as additional Git blocks in the IDE bar.
        Coding agents should add every additional repo they use, inspect, or edit with `mt_repo add <path> target` when that repo is not the session working directory, then run `mt_repo refresh`.

        mt_repo list → mt_repo add "~/repos/lib" target → mt_repo refresh → check the IDE bar Git blocks

        | Command | What it does |
        |---------|-------------|
        | `mt_repo list` / `mt_repo status` | List tracked repos and cached status for the current session |
        | `mt_repo add <path> [role]` | Add another repo, for example `mt_repo add ~/repos/lib target` |
        | `mt_repo refresh [repoRoot]` | Refresh one repo or all tracked repos |
        | `mt_repo remove <repoRoot>` | Remove an extra repo binding; the primary cwd repo stays automatic |

        `mt_repo` bindings are session-scoped and ad hoc. This keeps tlbx showing both repo states side by side in the IDE bar and a compact extra repo line under the sidebar cwd. The cwd repo remains automatic; `mt_repo remove REPO_ROOT` only removes extra bindings.

        ## Tips

        - mt_outline is 10x smaller than mt_query — always start there
        - mt_text is shorter than mt_query SEL --text — use it for page text
        - mt_open is the CLI command that opens/docks the preview and now fails loudly if the preview never becomes controllable
        - direct execution of the generated helpers accepts both bare command names and documented `mt_*` names, so `status` and `mt_status` both resolve when you run `.tlbx/tlbx_cli.sh` or `.tlbx/tlbx_cli.ps1` directly
        - mt_status reports `state: ready`, `state: waiting`, or `state: ambiguous` so you can tell whether the browser bridge is actually usable
        - Every C# change in the local source loop restarts the source `mt`; wait for the source URL to answer again before trusting browser results from that iteration
        - mt_session prints the current tlbx terminal session ID that tlbx_cli browser commands default to
        - mt_run_isolated detaches child stdin and sends stdout/stderr to separate `.tlbx/runs/<run-id>/` artifacts; never pass it one shell command string
        - mt_context --bash / mt_context --pwsh print export commands for nested shells so child bash/pwsh processes keep the same tlbx session scope
        - mt_topic labels the current ad-hoc session in the sidebar; keep it at 3-6 words and update it when the user's high-level topic shifts
        - mt_preview user1 / mt_preview user2 let one terminal own multiple isolated browser contexts
        - When one tlbx instance is previewing another, the outer tlbx browser tab owns `/ws/state`; the nested preview target alone cannot satisfy browser-control commands
        - mt_tail strips ANSI escape sequences and compresses noisy blank-line runs so supervisor sessions can read clean terminal output
        - mt_redraw performs a temporary PTY geometry pulse when a full-screen console application needs to repaint after foreign output
        - mt_prompt uses tlbx's server-side prompt API so text plus submit happen atomically instead of as two client-side calls
        - mt_prompt is state-aware: bootstrapped workers auto-resume from shell, and shell vs idle prompt vs busy turn should be decided by tlbx, not guessed ad hoc by the supervisor
        - mt_prompt_now is the explicit takeover helper for busy AI terminals when immediate interrupt-first execution is intended
        - mt_slash routes slash commands like `/status` or `/compact` through the same prompt path instead of pasting them manually
        - mt_wake queues a future prompt through the Command Bay queue, so delayed work survives helper reloads and can be canceled from the queue
        - mt_input_history and its show/replay/delete helpers expose the same session-scoped deterministic Terminal history available in each session's top bar; use them instead of scraping terminal output to recover prior prompts or pasted paths
        - mt_control_plane is the machine-readable operator outlet; use mt_work_add/update, mt_publish_status, and mt_checkpoint to make work and next actions visible without asking tlbx to infer them
        - mt_agent_capabilities reports product-authored feature flags and exact session runtime flags; mt_dispatch acts only on the session IDs you pass and returns a separate accepted/queued/error result for each
        - mt_events is an ordered event feed derived only from explicit control-plane mutations; use its latestSequence cursor instead of parsing terminal output for completion or attention
        - mt_attention gives you a ranked fleet view of which agent-controlled sessions need attention first
        - mt_repo list/add/remove/refresh is the discoverable CLI for session-scoped multi-repo Git tracking; add every additional repo you use that is not the cwd before falling back to ad hoc shell git checks
        - mt_bootstrap creates a fresh agent-controlled worker session, injects `.tlbx`, launches the chosen AI CLI profile, and can immediately send slash commands
        - mt_inspect bundles page, status, and proxy diagnostics into one compact first-look call; mt_proxylog_summary compresses proxy traffic the same way
        - mt_capabilities lists what the browser bridge of this tlbx build actually supports — check it before assuming a command exists
        - mt_preview_reset [url] is the fast recovery move when a named preview has the wrong logged-in user or stale browser state
        - After a tlbx web update, the browser frontend can close while your terminal keeps running in mthost; reopen tlbx from the terminal and run mt_open again for the current preview instead of recreating the session
        - mt_sendkeys plus mt_enter / mt_ctrlc / mt_escape / mt_up / mt_down / mt_left / mt_right are the direct terminal steering helpers
        - mt_submit is more reliable than mt_click on submit buttons (uses JS form.requestSubmit)
        - Chain commands: mt_fill "#a" "x" && mt_fill "#b" "y" && mt_submit
        - If mt_status still shows `state: waiting` after mt_open, treat that as a tlbx browser-attachment bug and inspect mt_proxylog plus mt_log error
        - If mt_status shows `ui clients: 0`, the owning tlbx browser tab is gone; reopen the outer tlbx UI before debugging the preview target itself
        - Browser command failures now print the server error body instead of silently returning nothing
        - If mt_status reports multiple clients, tlbx prefers the focused/visible preview client first, then falls back to the main browser's newest preview connection
        - If mt_open returns "No tlbx browser UI is connected", there is no live browser tab attached to /ws/state
        - tmux list-panes shows pane IDs (%0, %1, ...) — use these with send-keys and capture-pane
        """;
}
