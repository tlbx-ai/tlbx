using System.Globalization;

using Ai.Tlbx.MidTerm.Services.Sessions;
namespace Ai.Tlbx.MidTerm.Services.Tmux;

/// <summary>
/// Maps between stable tmux pane IDs (%N) and MidTerm session IDs.
/// Pane indices are assigned at session creation and never change.
/// </summary>
public sealed class TmuxPaneMapper
{
    private readonly TtyHostSessionManager _sessionManager;
    private readonly Dictionary<int, string> _paneToSession = new();
    private readonly Dictionary<string, int> _sessionToPane = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public TmuxPaneMapper(TtyHostSessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    /// <summary>
    /// Register a stable pane index for a session. Called on session creation.
    /// </summary>
    public void RegisterSession(string sessionId, int paneIndex)
    {
        lock (_lock)
        {
            _paneToSession[paneIndex] = sessionId;
            _sessionToPane[sessionId] = paneIndex;
        }
    }

    /// <summary>
    /// Remove the pane mapping for a closed session.
    /// </summary>
    public void UnregisterSession(string sessionId)
    {
        lock (_lock)
        {
            if (_sessionToPane.Remove(sessionId, out var paneIndex))
            {
                _paneToSession.Remove(paneIndex);
            }
        }
    }

    /// <summary>
    /// Resolve a tmux pane ID (e.g. "%3") to a MidTerm session ID.
    /// </summary>
    public string? PaneIdToSessionId(string paneId)
    {
        var stripped = paneId.StartsWith('%', StringComparison.Ordinal) ? paneId[1..] : paneId;
        if (!int.TryParse(stripped, CultureInfo.InvariantCulture, out var index))
        {
            return null;
        }

        lock (_lock)
        {
            return _paneToSession.GetValueOrDefault(index);
        }
    }

    /// <summary>
    /// Get the tmux pane ID (e.g. "%3") for a MidTerm session ID.
    /// </summary>
    public string? SessionIdToPaneId(string sessionId)
    {
        lock (_lock)
        {
            return _sessionToPane.TryGetValue(sessionId, out var index)
                ? string.Create(CultureInfo.InvariantCulture, $"%{index}")
                : null;
        }
    }

    /// <summary>
    /// Get the numeric pane index for a MidTerm session ID.
    /// </summary>
    public int? SessionIdToPaneIndex(string sessionId)
    {
        lock (_lock)
        {
            return _sessionToPane.TryGetValue(sessionId, out var index)
                ? index
                : null;
        }
    }

    /// <summary>
    /// Fallback when no caller pane ID is available. Returns the first session
    /// by order. Server has no concept of focused session (that's per-client
    /// in the browser), so first-by-order is the best available approximation.
    /// </summary>
    public string? GetActiveSessionId()
    {
        var sessions = _sessionManager.GetSessionList().Sessions;
        return sessions.Count > 0 ? sessions[0].Id : null;
    }
}
