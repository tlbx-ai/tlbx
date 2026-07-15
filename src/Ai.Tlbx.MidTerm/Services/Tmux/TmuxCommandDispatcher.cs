using System.Text;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Services.Tmux.Commands;

namespace Ai.Tlbx.MidTerm.Services.Tmux;

/// <summary>
/// Routes parsed tmux commands to their handlers and collects output.
/// </summary>
public sealed class TmuxCommandDispatcher
{
    private readonly SessionCommands _sessionCommands;
    private readonly IoCommands _ioCommands;
    private readonly PaneCommands _paneCommands;
    private readonly WindowCommands _windowCommands;
    private readonly ConfigCommands _configCommands;
    private readonly MiscCommands _miscCommands;

    public TmuxCommandDispatcher(
        SessionCommands sessionCommands,
        IoCommands ioCommands,
        PaneCommands paneCommands,
        WindowCommands windowCommands,
        ConfigCommands configCommands,
        MiscCommands miscCommands)
    {
        _sessionCommands = sessionCommands;
        _ioCommands = ioCommands;
        _paneCommands = paneCommands;
        _windowCommands = windowCommands;
        _configCommands = configCommands;
        _miscCommands = miscCommands;
    }

    /// <summary>
    /// Dispatch a list of parsed commands (may be chained via ;).
    /// Returns combined text output and overall success status.
    /// </summary>
    public async Task<TmuxResult> DispatchAsync(
        List<TmuxCommandParser.ParsedCommand> commands,
        string? callerPaneId,
        CancellationToken ct)
    {
        var output = new StringBuilder();
        var success = true;

        foreach (var cmd in commands)
        {
            TmuxLog.Command(cmd.Name, callerPaneId, cmd.Flags, cmd.Positional);
            var result = await DispatchSingleAsync(cmd, callerPaneId, ct).ConfigureAwait(false);
            TmuxLog.Result(cmd.Name, result.Success, result.Output);
            if (!string.IsNullOrEmpty(result.Output))
            {
                output.Append(result.Output);
            }
            if (!result.Success)
            {
                success = false;
                break;
            }
        }

        return new TmuxResult(success, output.ToString());
    }

    private async Task<TmuxResult> DispatchSingleAsync(
        TmuxCommandParser.ParsedCommand cmd,
        string? callerPaneId,
        CancellationToken ct)
    {
        try
        {
            return cmd.Name switch
            {
                // Session commands
                "list-panes" or "lsp" => _sessionCommands.ListPanes(cmd, callerPaneId),
                "list-sessions" or "ls" => _sessionCommands.ListSessions(cmd),
                "list-windows" or "lsw" => _sessionCommands.ListWindows(cmd),
                "has-session" or "has" => _sessionCommands.HasSession(cmd),

                // IO commands
                "send-keys" or "send" => await _ioCommands.SendKeysAsync(cmd, callerPaneId, ct),
                "display-message" or "display" => _ioCommands.DisplayMessage(cmd, callerPaneId),
                "capture-pane" or "capturep" => await _ioCommands.CapturePaneAsync(cmd, callerPaneId, ct),

                // Pane commands
                "split-window" or "splitw" => await _paneCommands.SplitWindowAsync(cmd, callerPaneId, ct),
                "select-pane" or "selectp" => _paneCommands.SelectPane(cmd, callerPaneId),
                "kill-pane" or "killp" => await _paneCommands.KillPaneAsync(cmd, callerPaneId, ct),
                "resize-pane" or "resizep" => await _paneCommands.ResizePaneAsync(cmd, callerPaneId, ct),
                "swap-pane" or "swapp" => _paneCommands.SwapPane(cmd, callerPaneId),

                // Window commands
                "new-window" or "neww" => await _windowCommands.NewWindowAsync(cmd, callerPaneId, ct),
                "select-window" or "selectw" => _windowCommands.SelectWindow(cmd, callerPaneId),
                "kill-window" or "killw" => await _windowCommands.KillWindowAsync(cmd, callerPaneId, ct),
                "select-layout" or "selectl" => TmuxResult.Ok(),

                // Config commands (stubs)
                "show-options" or "show-option" or "show" => _configCommands.ShowOptions(cmd),
                "set-option" or "set" => _configCommands.SetOption(cmd),
                "bind-key" or "bind" => TmuxResult.Ok(),
                "unbind-key" or "unbind" => TmuxResult.Ok(),
                "source-file" or "source" => TmuxResult.Ok(),

                // Misc commands
                "run-shell" or "run" => await _miscCommands.RunShellAsync(cmd, ct),
                "display-popup" or "popup" => await _miscCommands.DisplayPopupAsync(cmd, callerPaneId, ct),
                "wait-for" or "wait" => await _miscCommands.WaitForAsync(cmd, ct),

                // Environment stubs
                "set-environment" or "setenv" => TmuxResult.Ok(),
                "show-environment" or "showenv" => TmuxResult.Ok(),

                // Buffer stubs
                "list-buffers" or "lsb" => TmuxResult.Ok(),
                "show-buffer" or "showb" => TmuxResult.Ok(),
                "set-buffer" or "setb" => TmuxResult.Ok(),
                "delete-buffer" or "deleteb" => TmuxResult.Ok(),

                // Pane/window operation stubs
                "pipe-pane" or "pipep" => TmuxResult.Ok(),
                "respawn-pane" or "respawnp" => TmuxResult.Ok(),
                "last-pane" or "lastp" => TmuxResult.Ok(),
                "last-window" or "last" => TmuxResult.Ok(),
                "move-pane" or "movep" => TmuxResult.Ok(),
                "move-window" or "movew" => TmuxResult.Ok(),

                // Rename stubs
                "rename-session" or "rename" => TmuxResult.Ok(),
                "rename-window" or "renamew" => TmuxResult.Ok(),

                // Informational
                "server-info" or "info" => TmuxResult.Ok("tlbx tmux compatibility layer\n"),
                "start-server" or "start" => TmuxResult.Ok(),
                "kill-server" => TmuxResult.Fail("kill-server is not supported in tlbx\n"),

                // Not implemented
                "-CC" or "-C" => TmuxResult.Fail("control mode is not supported by tlbx\n"),
                "attach-session" or "attach" or "a" => TmuxResult.Fail("attach-session is not applicable in tlbx (use the web UI)\n"),
                "detach-client" or "detach" => TmuxResult.Fail("detach-client is not applicable in tlbx\n"),
                "copy-mode" => TmuxResult.Fail("copy-mode is not supported by tlbx\n"),
                "choose-tree" => TmuxResult.Fail("choose-tree is not supported by tlbx\n"),

                _ => TmuxResult.Fail($"unknown command: {cmd.Name}\n")
            };
        }
        catch (Exception ex)
        {
            Log.Exception(ex, $"TmuxCommandDispatcher.Dispatch({cmd.Name})");
            TmuxLog.Error($"{cmd.Name}: {ex.GetType().Name}: {ex.Message}");
            return TmuxResult.Fail($"error: {ex.Message}\n");
        }
    }
}

/// <summary>
/// Result of a tmux command: success/failure flag plus text output.
/// </summary>
public readonly record struct TmuxResult(bool Success, string Output)
{
    public static TmuxResult Ok(string output = "") => new(true, output);
    public static TmuxResult Fail(string output) => new(false, output);
}
