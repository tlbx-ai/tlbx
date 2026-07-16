using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ai.Tlbx.MidTerm.Models;

using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Models.Auth;
using Ai.Tlbx.MidTerm.Models.Certificates;
using Ai.Tlbx.MidTerm.Models.Files;
using Ai.Tlbx.MidTerm.Models.History;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Models.System;
namespace Ai.Tlbx.MidTerm.Services.Tmux;

/// <summary>
/// Evaluates tmux format strings like #{pane_id}, #{pane_width}, etc.
/// </summary>
public sealed partial class TmuxFormatter
{
    private readonly TmuxPaneMapper _paneMapper;
    private readonly TtyHostSessionManager _sessionManager;

    public TmuxFormatter(TmuxPaneMapper paneMapper, TtyHostSessionManager sessionManager)
    {
        _paneMapper = paneMapper;
        _sessionManager = sessionManager;
    }

    [GeneratedRegex(@"#\{([^}]+)\}", RegexOptions.None, 1000)]
    private static partial Regex FormatVariableRegex();

    /// <summary>
    /// Evaluate a tmux format string, replacing #{variable} tokens with session values.
    /// </summary>
    public string Evaluate(string format, SessionInfoDto session, bool isActive)
    {
        var paneIndex = _paneMapper.SessionIdToPaneIndex(session.Id) ?? 0;
        var totalPanes = _sessionManager.GetSessionList().Sessions.Count;

        return FormatVariableRegex().Replace(format, match =>
        {
            var variable = match.Groups[1].Value;
            return ResolveVariable(variable, session, paneIndex, isActive, totalPanes);
        });
    }

    private static string ResolveVariable(
        string variable,
        SessionInfoDto session,
        int paneIndex,
        bool isActive,
        int totalPanes)
    {
        return variable switch
        {
            "pane_id" => string.Create(CultureInfo.InvariantCulture, $"%{paneIndex}"),
            "pane_index" => paneIndex.ToString(CultureInfo.InvariantCulture),
            "pane_pid" => session.Pid.ToString(CultureInfo.InvariantCulture),
            "pane_width" => session.Cols.ToString(CultureInfo.InvariantCulture),
            "pane_height" => session.Rows.ToString(CultureInfo.InvariantCulture),
            "pane_current_path" => session.CurrentDirectory ?? "",
            "pane_current_command" => session.ForegroundName ?? "",
            "pane_title" => session.Name ?? session.TerminalTitle ?? "",
            "pane_active" => isActive ? "1" : "0",
            "pane_dead" => session.IsRunning ? "0" : "1",
            "pane_tty" => "",
            "extended-keys-format" => "csi-u",
            "client_termname" => "xterm-256color",
            "client_termtype" => "xterm-256color",
            "client_termfeatures" => "RGB,clipboard,extkeys,focus,title",

            "session_id" => "$0",
            "session_name" => "tlbx",
            "session_windows" => "1",
            "session_attached" => "1",
            "session_group" => "",
            "session_created" => new DateTimeOffset(session.CreatedAt).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),

            "window_id" => "@0",
            "window_index" => "0",
            "window_name" => "tlbx",
            "window_width" => session.Cols.ToString(CultureInfo.InvariantCulture),
            "window_height" => session.Rows.ToString(CultureInfo.InvariantCulture),
            "window_panes" => totalPanes.ToString(CultureInfo.InvariantCulture),
            "window_active" => "1",
            "window_flags" => "*",

            "cursor_x" => "0",
            "cursor_y" => "0",
            "scroll_position" => "0",
            "alternate_on" => "0",

            _ => ""
        };
    }

    /// <summary>
    /// Formats a default pane line (when no -F format is specified).
    /// Matches tmux default: "0: [80x24] [history 0/2000, 0 bytes] %0 (active)"
    /// </summary>
    public string FormatDefaultPaneLine(SessionInfoDto session, bool isActive)
    {
        var paneIndex = _paneMapper.SessionIdToPaneIndex(session.Id) ?? 0;
        var active = isActive ? " (active)" : "";
        return string.Create(CultureInfo.InvariantCulture, $"{paneIndex}: [{session.Cols}x{session.Rows}] [history 0/2000, 0 bytes] %{paneIndex}{active}");
    }

    /// <summary>
    /// Format the default list-sessions line (when no -F format is specified).
    /// </summary>
    public string FormatDefaultSessionLine()
    {
        var sessions = _sessionManager.GetSessionList().Sessions;
        var paneCount = sessions.Count;
        var created = sessions.Count > 0 ? sessions[0].CreatedAt : DateTime.UtcNow;
        return string.Create(CultureInfo.InvariantCulture, $"tlbx: {paneCount} windows (created {created:ddd MMM dd HH:mm:ss yyyy}) (attached)");
    }

    /// <summary>
    /// Format the default list-windows line (when no -F format is specified).
    /// </summary>
    public string FormatDefaultWindowLine()
    {
        var sessions = _sessionManager.GetSessionList().Sessions;
        var paneCount = sessions.Count;
        return string.Create(CultureInfo.InvariantCulture, $"0: tlbx* ({paneCount} panes) [active]");
    }
}
