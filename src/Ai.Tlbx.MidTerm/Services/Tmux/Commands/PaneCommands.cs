using System.Globalization;
using System.Text;

using Ai.Tlbx.MidTerm.Services.Sessions;
namespace Ai.Tlbx.MidTerm.Services.Tmux.Commands;

/// <summary>
/// Handles: split-window, select-pane, kill-pane, resize-pane, swap-pane
/// </summary>
public sealed class PaneCommands
{
    private readonly TtyHostSessionManager _sessionManager;
    private readonly TmuxPaneMapper _paneMapper;
    private readonly TmuxTargetResolver _targetResolver;
    private readonly TmuxLayoutBridge _layoutBridge;
    private readonly TerminalSizeControlService _sizeControl;

    public PaneCommands(
        TtyHostSessionManager sessionManager,
        TmuxPaneMapper paneMapper,
        TmuxTargetResolver targetResolver,
        TmuxLayoutBridge layoutBridge, TerminalSizeControlService sizeControl)
    {
        _sessionManager = sessionManager;
        _paneMapper = paneMapper;
        _targetResolver = targetResolver;
        _layoutBridge = layoutBridge;
        _sizeControl = sizeControl;
    }

    /// <summary>
    /// Create a new session and dock it adjacent to the target pane.
    /// If positional args specify a command, send it to the new session via the PTY.
    /// </summary>
    public async Task<TmuxResult> SplitWindowAsync(
        TmuxCommandParser.ParsedCommand cmd,
        string? callerPaneId,
        CancellationToken ct)
    {
        var horizontal = cmd.HasFlag("-h");
        var target = cmd.GetFlag("-t");
        var workingDirectory = cmd.GetFlag("-c");

        var relativeToSessionId = _targetResolver.ResolveToSessionId(target, callerPaneId);
        if (relativeToSessionId is null)
        {
            return TmuxResult.Fail("can't find pane\n");
        }

        // Get the current session's dimensions to create the new one at similar size
        var existingSession = _sessionManager.GetSession(relativeToSessionId);
        var cols = existingSession?.Cols ?? 80;
        var rows = existingSession?.Rows ?? 24;

        var newSession = await _sessionManager.CreateSessionAsync(
            null, cols, rows, workingDirectory, ct).ConfigureAwait(false);

        if (newSession is null)
        {
            return TmuxResult.Fail("failed to create pane\n");
        }

        _sessionManager.MarkTmuxCreated(newSession.Id);
        _sessionManager.SetTmuxParent(newSession.Id, relativeToSessionId);
        await SendCommandIfPresentAsync(cmd.Positional, newSession.Id, ct).ConfigureAwait(false);

        // Broadcast dock instruction to frontend
        var position = horizontal ? "right" : "bottom";
        _layoutBridge.RequestDock(newSession.Id, relativeToSessionId, position);

        var paneId = _paneMapper.SessionIdToPaneId(newSession.Id);
        return TmuxResult.Ok(paneId + "\n");
    }

    /// <summary>
    /// Focus a pane by target (-t) or direction (-D/-U/-L/-R).
    /// </summary>
    public TmuxResult SelectPane(TmuxCommandParser.ParsedCommand cmd, string? callerPaneId)
    {
        var target = cmd.GetFlag("-t");

        // Directional selection: -D, -U, -L, -R
        if (cmd.HasFlag("-D") || cmd.HasFlag("-U") || cmd.HasFlag("-L") || cmd.HasFlag("-R"))
        {
            var direction = cmd.HasFlag("-L") ? "left"
                : cmd.HasFlag("-R") ? "right"
                : cmd.HasFlag("-U") ? "up"
                : "down";

            var callerSessionId = _targetResolver.ResolveCallerSessionId(callerPaneId);
            if (callerSessionId is null)
            {
                return TmuxResult.Fail("can't find pane\n");
            }

            var adjacentSessionId = _layoutBridge.GetAdjacentSession(callerSessionId, direction);
            if (adjacentSessionId is null)
            {
                return TmuxResult.Ok();
            }

            _layoutBridge.RequestFocus(adjacentSessionId);
            return TmuxResult.Ok();
        }

        // Target-based selection: -t %N
        if (target is not null)
        {
            var sessionId = _targetResolver.ResolveToSessionId(target, callerPaneId);
            if (sessionId is null)
            {
                return TmuxResult.Fail("can't find pane\n");
            }
            _layoutBridge.RequestFocus(sessionId);
            return TmuxResult.Ok();
        }

        // Last pane toggle (no flags)
        return TmuxResult.Ok();
    }

    /// <summary>
    /// Close a terminal session (pane).
    /// </summary>
    public async Task<TmuxResult> KillPaneAsync(
        TmuxCommandParser.ParsedCommand cmd,
        string? callerPaneId,
        CancellationToken ct)
    {
        var target = cmd.GetFlag("-t");
        var sessionId = _targetResolver.ResolveToSessionId(target, callerPaneId);

        if (sessionId is null)
        {
            return TmuxResult.Fail("can't find pane\n");
        }

        await _sessionManager.CloseSessionAsync(sessionId, ct).ConfigureAwait(false);
        return TmuxResult.Ok();
    }

    /// <summary>
    /// Resize a pane to absolute dimensions (-x/-y). Directional resize is a no-op.
    /// </summary>
    public async Task<TmuxResult> ResizePaneAsync(
        TmuxCommandParser.ParsedCommand cmd,
        string? callerPaneId,
        CancellationToken ct)
    {
        var target = cmd.GetFlag("-t");
        var sessionId = _targetResolver.ResolveToSessionId(target, callerPaneId);

        if (sessionId is null)
        {
            return TmuxResult.Fail("can't find pane\n");
        }

        var widthStr = cmd.GetFlag("-x");
        var heightStr = cmd.GetFlag("-y");

        if (widthStr is null && heightStr is null)
        {
            // Directional resize: -D/-U/-L/-R with optional adjustment amount
            return TmuxResult.Ok();
        }

        var session = _sessionManager.GetSession(sessionId);
        var cols = widthStr is not null && int.TryParse(widthStr, CultureInfo.InvariantCulture, out var w) ? w : session?.Cols ?? 80;
        var rows = heightStr is not null && int.TryParse(heightStr, CultureInfo.InvariantCulture, out var h) ? h : session?.Rows ?? 24;

        var resized = await _sizeControl.ResizeUnownedAsync(sessionId, cols, rows,
            resizeCt => _sessionManager.ResizeSessionAsync(sessionId, cols, rows, resizeCt), ct).ConfigureAwait(false);
        if (!resized) return TmuxResult.Fail("terminal size is controlled by its browser owner\n");
        return TmuxResult.Ok();
    }

    /// <summary>
    /// Swap the positions of two panes by reordering sessions.
    /// </summary>
    public TmuxResult SwapPane(TmuxCommandParser.ParsedCommand cmd, string? callerPaneId)
    {
        var src = cmd.GetFlag("-s");
        var dst = cmd.GetFlag("-t");

        var srcSessionId = _targetResolver.ResolveToSessionId(src, callerPaneId);
        var dstSessionId = _targetResolver.ResolveToSessionId(dst, callerPaneId);

        if (srcSessionId is null || dstSessionId is null)
        {
            return TmuxResult.Fail("can't find pane\n");
        }

        // Reorder sessions to swap their positions
        var sessions = _sessionManager.GetSessionList().Sessions;
        var ids = sessions.Select(s => s.Id).ToList();
        var srcIdx = ids.IndexOf(srcSessionId);
        var dstIdx = ids.IndexOf(dstSessionId);

        if (srcIdx >= 0 && dstIdx >= 0)
        {
            (ids[srcIdx], ids[dstIdx]) = (ids[dstIdx], ids[srcIdx]);
            _sessionManager.ReorderSessions(ids);
            _layoutBridge.RequestSwap(srcSessionId, dstSessionId);
        }

        return TmuxResult.Ok();
    }

    /// <summary>
    /// If positional args contain a command, send it to the session followed by Enter.
    /// The PTY kernel buffer holds the input until the shell reads from stdin.
    /// </summary>
    internal async Task SendCommandIfPresentAsync(List<string> positional, string sessionId, CancellationToken ct)
    {
        if (positional.Count == 0)
        {
            return;
        }

        var command = ShellQuote(positional);
        TmuxLog.Command("(exec)", null, new Dictionary<string, string?>(StringComparer.Ordinal), [command]);

        var data = Encoding.UTF8.GetBytes(command + "\r");
        await _sessionManager.SendInputAsync(sessionId, data, ct).ConfigureAwait(false);
    }

    private static string ShellQuote(List<string> args)
    {
        if (args.Count == 1)
        {
            return args[0];
        }

        var sb = new StringBuilder();
        foreach (var arg in args)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            if (arg.Length > 0 && !arg.AsSpan().ContainsAny(" \t|&;<>()$\\\"'`!#~"))
            {
                sb.Append(arg);
            }
            else
            {
                sb.Append('\'');
                sb.Append(arg.Replace("'", "'\\''", StringComparison.Ordinal));
                sb.Append('\'');
            }
        }
        return sb.ToString();
    }
}
