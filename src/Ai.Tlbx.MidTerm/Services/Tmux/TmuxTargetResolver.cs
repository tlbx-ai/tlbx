using System.Globalization;

namespace Ai.Tlbx.MidTerm.Services.Tmux;

/// <summary>
/// Parses tmux -t target syntax: session:window.pane
/// Since MidTerm maps everything to a single session/window,
/// we only really care about the pane part.
/// </summary>
public sealed class TmuxTargetResolver
{
    private readonly TmuxPaneMapper _paneMapper;

    public TmuxTargetResolver(TmuxPaneMapper paneMapper)
    {
        _paneMapper = paneMapper;
    }

    /// <summary>
    /// Resolve a tmux target string to a MidTerm session ID.
    /// Accepts: %N, N, session:window.pane, or bare pane index.
    /// </summary>
    public string? ResolveToSessionId(string? target, string? callerPaneId)
    {
        if (string.IsNullOrEmpty(target))
        {
            return ResolveCallerSessionId(callerPaneId);
        }

        // Handle full target syntax: session:window.pane
        var paneTarget = target;
        var dotIndex = target.LastIndexOf('.', StringComparison.Ordinal);
        if (dotIndex >= 0)
        {
            paneTarget = target[(dotIndex + 1)..];
        }
        else
        {
            var colonIndex = target.LastIndexOf(':', StringComparison.Ordinal);
            if (colonIndex >= 0)
            {
                paneTarget = target[(colonIndex + 1)..];
            }
        }

        // Try as %N pane ID
        if (paneTarget.StartsWith('%', StringComparison.Ordinal))
        {
            return _paneMapper.PaneIdToSessionId(paneTarget);
        }

        // Try as bare integer (pane index)
        if (int.TryParse(paneTarget, CultureInfo.InvariantCulture, out _))
        {
            return _paneMapper.PaneIdToSessionId($"%{paneTarget}");
        }

        // Try as session ID directly (MidTerm 8-char hex)
        var sessions = _paneMapper.SessionIdToPaneId(paneTarget);
        if (sessions is not null)
        {
            return paneTarget;
        }

        return null;
    }

    /// <summary>
    /// Resolve the caller's X-Tmux-Pane header to a session ID, falling back to the active session.
    /// </summary>
    public string? ResolveCallerSessionId(string? callerPaneId)
    {
        if (string.IsNullOrEmpty(callerPaneId))
        {
            return _paneMapper.GetActiveSessionId();
        }
        return _paneMapper.PaneIdToSessionId(callerPaneId);
    }
}
