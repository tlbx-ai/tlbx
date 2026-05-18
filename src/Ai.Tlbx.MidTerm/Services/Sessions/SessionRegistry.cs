using System.Collections.Concurrent;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models.Sessions;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

internal sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, Action> _stateListeners = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _tempDirectories = new(StringComparer.Ordinal);
    private readonly string _dropsBasePath;
    private readonly SessionControlStateService? _sessionControlStateService;
    private readonly SessionLayoutStateService? _sessionLayoutStateService;
    private int _nextOrder;

    public SessionRegistry(
        bool isServiceMode,
        SessionControlStateService? sessionControlStateService = null,
        SessionLayoutStateService? sessionLayoutStateService = null)
    {
        _dropsBasePath = GetDropsBasePath(isServiceMode);
        _sessionControlStateService = sessionControlStateService;
        _sessionLayoutStateService = sessionLayoutStateService;
    }

    public ConcurrentDictionary<string, TtyHostClient> Clients { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, SessionInfo> SessionCache { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, int> SessionOrder { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, byte> TmuxCreatedSessions { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, byte> TmuxCommandStarted { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, byte> HiddenSessions { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, string> TmuxParentSessions { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, string> BookmarkLinks { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, string> SessionTopics { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, string> SessionNotes { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, byte> AgentControlledSessions { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, byte> AppServerControlOnlySessions { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, string> LaunchOrigins { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, string> ProfileHints { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, string> AppServerControlResumeThreadIds { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, string> SpaceIds { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, string> WorkspacePaths { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, string> Surfaces { get; } = new(StringComparer.Ordinal);

    public int ClientCount => Clients.Count;

    public int SessionCount => SessionCache.Count;

    public int NextOrder => Volatile.Read(ref _nextOrder);

    public void SetNextOrder(int nextOrder)
    {
        Interlocked.Exchange(ref _nextOrder, nextOrder);
    }

    public int ReserveNextOrder()
    {
        return Interlocked.Increment(ref _nextOrder);
    }

    public SessionInfo? GetSession(string sessionId)
    {
        return SessionCache.TryGetValue(sessionId, out var info) ? info : null;
    }

    public string GetTempDirectory(string sessionId)
    {
        return _tempDirectories.GetOrAdd(sessionId, id =>
        {
            var tempPath = Path.Combine(_dropsBasePath, id);
            Directory.CreateDirectory(tempPath);
            return tempPath;
        });
    }

    public void CleanupTempDirectory(string sessionId)
    {
        if (_tempDirectories.TryRemove(sessionId, out var tempPath))
        {
            try
            {
                if (Directory.Exists(tempPath))
                {
                    Directory.Delete(tempPath, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    public void MarkHidden(string sessionId)
    {
        HiddenSessions.TryAdd(sessionId, 0);
    }

    public bool IsHidden(string sessionId)
    {
        return HiddenSessions.ContainsKey(sessionId);
    }

    public void MarkTmuxCreated(string sessionId)
    {
        TmuxCreatedSessions.TryAdd(sessionId, 0);
    }

    public void SetTmuxParent(string childSessionId, string parentSessionId)
    {
        while (TmuxParentSessions.TryGetValue(parentSessionId, out var grandparent))
        {
            parentSessionId = grandparent;
        }

        TmuxParentSessions[childSessionId] = parentSessionId;

        if (IsAgentControlled(parentSessionId))
        {
            AgentControlledSessions[childSessionId] = 0;
            _sessionControlStateService?.SetAgentControlled(childSessionId, agentControlled: true);
        }
    }

    public IReadOnlyList<SessionInfo> GetAllSessions()
    {
        return SessionCache.Values.ToList();
    }

    public SessionListDto GetSessionList()
    {
        return new SessionListDto
        {
            Sessions = SessionCache.Values
                .Where(s => !HiddenSessions.ContainsKey(s.Id))
                .Select(s =>
                {
                    var spaceId = GetSpaceId(s.Id);
                    var launchOrigin = ResolveLaunchOrigin(s.Id, spaceId);
                    return new SessionInfoDto
                    {
                        Id = s.Id,
                        Pid = s.Pid,
                        CreatedAt = s.CreatedAt,
                        IsRunning = s.IsRunning,
                        ExitCode = s.ExitCode,
                        Cols = s.Cols,
                        Rows = s.Rows,
                        ShellType = s.ShellType,
                        Name = s.Name,
                        TerminalTitle = s.TerminalTitle,
                        Topic = SessionTopics.TryGetValue(s.Id, out var topic) ? topic : null,
                        Notes = SessionNotes.TryGetValue(s.Id, out var notes) ? notes : null,
                        ManuallyNamed = s.ManuallyNamed,
                        CurrentDirectory = s.CurrentDirectory,
                        ForegroundPid = s.ForegroundPid,
                        ForegroundName = s.ForegroundName,
                        ForegroundCommandLine = s.ForegroundCommandLine,
                        AgentAttachPoint = s.AgentAttachPoint,
                        Order = SessionOrder.TryGetValue(s.Id, out var order) ? order : int.MaxValue,
                        ParentSessionId = TmuxParentSessions.TryGetValue(s.Id, out var parentId) ? parentId : null,
                        BookmarkId = BookmarkLinks.TryGetValue(s.Id, out var bookmarkId) ? bookmarkId : null,
                        SpaceId = spaceId,
                        WorkspacePath = GetWorkspacePath(s.Id),
                        Surface = GetSurface(s.Id),
                        IsAdHoc = IsAdHoc(launchOrigin),
                        AgentControlled = IsAgentControlled(s.Id),
                        AppServerControlOnly = IsAppServerControlOnly(s.Id),
                        ProfileHint = GetProfileHint(s.Id),
                        AppServerControlResumeThreadId = GetAppServerControlResumeThreadId(s.Id),
                        ForegroundDisplayName = s.ForegroundDisplayName,
                        ForegroundProcessIdentity = s.ForegroundProcessIdentity
                    };
                })
                .OrderBy(s => s.Order)
                .ToList()
        };
    }

    public void RemoveSessionState(string sessionId)
    {
        SessionCache.TryRemove(sessionId, out _);
        SessionOrder.TryRemove(sessionId, out _);
        TmuxCreatedSessions.TryRemove(sessionId, out _);
        TmuxCommandStarted.TryRemove(sessionId, out _);
        HiddenSessions.TryRemove(sessionId, out _);
        TmuxParentSessions.TryRemove(sessionId, out _);
        BookmarkLinks.TryRemove(sessionId, out _);
        SessionTopics.TryRemove(sessionId, out _);
        SessionNotes.TryRemove(sessionId, out _);
        AgentControlledSessions.TryRemove(sessionId, out _);
        AppServerControlOnlySessions.TryRemove(sessionId, out _);
        LaunchOrigins.TryRemove(sessionId, out _);
        ProfileHints.TryRemove(sessionId, out _);
        AppServerControlResumeThreadIds.TryRemove(sessionId, out _);
        SpaceIds.TryRemove(sessionId, out _);
        WorkspacePaths.TryRemove(sessionId, out _);
        Surfaces.TryRemove(sessionId, out _);
        _sessionControlStateService?.RemoveSession(sessionId);
        _sessionLayoutStateService?.RemoveSession(sessionId);

        foreach (var kvp in TmuxParentSessions.ToArray())
        {
            if (kvp.Value == sessionId)
            {
                TmuxParentSessions.TryRemove(kvp.Key, out _);
            }
        }

        CleanupTempDirectory(sessionId);
    }

    public bool SetBookmarkId(string sessionId, string bookmarkId)
    {
        if (!SessionCache.ContainsKey(sessionId))
        {
            return false;
        }

        BookmarkLinks[sessionId] = bookmarkId;
        NotifyStateChange();
        return true;
    }

    public bool SetSessionNotes(string sessionId, string? notes)
    {
        if (!SessionCache.ContainsKey(sessionId))
        {
            return false;
        }

        var normalized = NormalizeSessionNotes(notes);
        if (normalized is null)
        {
            SessionNotes.TryRemove(sessionId, out _);
        }
        else
        {
            SessionNotes[sessionId] = normalized;
        }

        NotifyStateChange();
        return true;
    }

    public bool SetSessionTopic(string sessionId, string? topic)
    {
        if (!SessionCache.ContainsKey(sessionId))
        {
            return false;
        }

        var normalized = NormalizeSessionTopic(topic);
        if (normalized is null)
        {
            SessionTopics.TryRemove(sessionId, out _);
        }
        else
        {
            SessionTopics[sessionId] = normalized;
        }

        NotifyStateChange();
        return true;
    }

    internal static string? NormalizeSessionTopic(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return null;
        }

        var normalized = topic.ReplaceLineEndings(" ").Trim();
        while (normalized.Contains("  ", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
        }

        return normalized.Length <= 120 ? normalized : normalized[..120];
    }

    internal static string? NormalizeSessionNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        var lines = notes.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.None)
            .Take(5)
            .Select(static line => line.TrimEnd())
            .ToArray();
        var normalized = string.Join('\n', lines).Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        return normalized.Length <= 600 ? normalized : normalized[..600];
    }

    public bool SetAgentControlled(string sessionId, bool agentControlled)
    {
        if (!SessionCache.ContainsKey(sessionId))
        {
            return false;
        }

        foreach (var relatedSessionId in GetTmuxFamilySessionIds(sessionId))
        {
            if (agentControlled)
            {
                AgentControlledSessions[relatedSessionId] = 0;
            }
            else
            {
                AgentControlledSessions.TryRemove(relatedSessionId, out _);
            }

            _sessionControlStateService?.SetAgentControlled(relatedSessionId, agentControlled);
        }

        NotifyStateChange();
        return true;
    }

    public bool SetAppServerControlOnly(string sessionId, bool appServerControlOnly)
    {
        if (!SessionCache.ContainsKey(sessionId))
        {
            return false;
        }

        if (appServerControlOnly)
        {
            AppServerControlOnlySessions[sessionId] = 0;
        }
        else
        {
            AppServerControlOnlySessions.TryRemove(sessionId, out _);
        }

        _sessionControlStateService?.SetAppServerControlOnly(sessionId, appServerControlOnly);
        NotifyStateChange();
        return true;
    }

    public bool IsAppServerControlOnly(string sessionId)
    {
        return AppServerControlOnlySessions.ContainsKey(sessionId)
            || _sessionControlStateService?.IsAppServerControlOnly(sessionId) == true;
    }

    public bool SetProfileHint(string sessionId, string? profile)
    {
        if (!SessionCache.ContainsKey(sessionId))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(profile))
        {
            ProfileHints.TryRemove(sessionId, out _);
        }
        else
        {
            ProfileHints[sessionId] = profile.Trim();
        }

        _sessionControlStateService?.SetProfileHint(sessionId, profile);
        NotifyStateChange();
        return true;
    }

    public bool SetLaunchOrigin(string sessionId, string? launchOrigin)
    {
        if (!SessionCache.ContainsKey(sessionId))
        {
            return false;
        }

        var normalized = SessionLaunchOrigins.Normalize(launchOrigin);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            LaunchOrigins.TryRemove(sessionId, out _);
        }
        else
        {
            LaunchOrigins[sessionId] = normalized;
        }

        _sessionControlStateService?.SetLaunchOrigin(sessionId, normalized);
        NotifyStateChange();
        return true;
    }

    public string? GetLaunchOrigin(string sessionId)
    {
        var spaceId = GetSpaceId(sessionId);
        return ResolveLaunchOrigin(sessionId, spaceId);
    }

    public bool SetAppServerControlResumeThreadId(string sessionId, string? resumeThreadId)
    {
        if (!SessionCache.ContainsKey(sessionId))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(resumeThreadId))
        {
            AppServerControlResumeThreadIds.TryRemove(sessionId, out _);
        }
        else
        {
            AppServerControlResumeThreadIds[sessionId] = resumeThreadId.Trim();
        }

        _sessionControlStateService?.SetAppServerControlResumeThreadId(sessionId, resumeThreadId);
        NotifyStateChange();
        return true;
    }

    public bool SetSpaceId(string sessionId, string? spaceId)
    {
        if (!SessionCache.ContainsKey(sessionId))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(spaceId))
        {
            SpaceIds.TryRemove(sessionId, out _);
        }
        else
        {
            SpaceIds[sessionId] = spaceId.Trim();
        }

        _sessionControlStateService?.SetSpaceId(sessionId, spaceId);
        NotifyStateChange();
        return true;
    }

    public bool SetWorkspacePath(string sessionId, string? workspacePath)
    {
        if (!SessionCache.ContainsKey(sessionId))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            WorkspacePaths.TryRemove(sessionId, out _);
        }
        else
        {
            WorkspacePaths[sessionId] = workspacePath.Trim();
        }

        _sessionControlStateService?.SetWorkspacePath(sessionId, workspacePath);
        NotifyStateChange();
        return true;
    }

    public bool SetSurface(string sessionId, string? surface)
    {
        if (!SessionCache.ContainsKey(sessionId))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(surface))
        {
            Surfaces.TryRemove(sessionId, out _);
        }
        else
        {
            Surfaces[sessionId] = surface.Trim();
        }

        _sessionControlStateService?.SetSurface(sessionId, surface);
        NotifyStateChange();
        return true;
    }

    public int ClearBookmarksByHistoryId(string bookmarkId)
    {
        if (string.IsNullOrWhiteSpace(bookmarkId))
        {
            return 0;
        }

        var removed = 0;
        foreach (var link in BookmarkLinks.ToArray())
        {
            if (!string.Equals(link.Value, bookmarkId, StringComparison.Ordinal))
            {
                continue;
            }

            if (BookmarkLinks.TryRemove(link.Key, out _))
            {
                removed++;
            }
        }

        if (removed > 0)
        {
            NotifyStateChange();
        }

        return removed;
    }

    public bool ReorderSessions(IList<string> sessionIds)
    {
        foreach (var id in sessionIds)
        {
            if (!SessionCache.ContainsKey(id))
            {
                return false;
            }
        }

        for (var i = 0; i < sessionIds.Count; i++)
        {
            SessionOrder[sessionIds[i]] = i;
        }

        NotifyStateChange();
        return true;
    }

    public string AddStateListener(Action callback)
    {
        var id = Guid.NewGuid().ToString("N");
        _stateListeners[id] = callback;
        return id;
    }

    public void RemoveStateListener(string id)
    {
        _stateListeners.TryRemove(id, out _);
    }

    public void NotifyStateChange()
    {
        foreach (var listener in _stateListeners.Values)
        {
            try
            {
                listener();
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "TtyHostSessionManager.NotifyStateChange");
            }
        }
    }

    public IReadOnlyList<string> TempDirectorySessionIds => _tempDirectories.Keys.ToList();

    public void ClearAll()
    {
        Clients.Clear();
        SessionCache.Clear();
        SessionOrder.Clear();
        TmuxCreatedSessions.Clear();
        TmuxCommandStarted.Clear();
        HiddenSessions.Clear();
        TmuxParentSessions.Clear();
        BookmarkLinks.Clear();
        AgentControlledSessions.Clear();
        LaunchOrigins.Clear();
        AppServerControlResumeThreadIds.Clear();
        SpaceIds.Clear();
        WorkspacePaths.Clear();
        Surfaces.Clear();
        _stateListeners.Clear();
        _tempDirectories.Clear();
    }

    private bool IsAgentControlled(string sessionId)
    {
        return AgentControlledSessions.ContainsKey(sessionId)
            || _sessionControlStateService?.IsAgentControlled(sessionId) == true;
    }

    private string? GetProfileHint(string sessionId)
    {
        return ProfileHints.TryGetValue(sessionId, out var profileHint)
            ? profileHint
            : _sessionControlStateService?.GetProfileHint(sessionId);
    }

    private string? GetAppServerControlResumeThreadId(string sessionId)
    {
        return AppServerControlResumeThreadIds.TryGetValue(sessionId, out var resumeThreadId)
            ? resumeThreadId
            : _sessionControlStateService?.GetAppServerControlResumeThreadId(sessionId);
    }

    private string? GetStoredLaunchOrigin(string sessionId)
    {
        return LaunchOrigins.TryGetValue(sessionId, out var launchOrigin)
            ? launchOrigin
            : _sessionControlStateService?.GetLaunchOrigin(sessionId);
    }

    private string? GetSpaceId(string sessionId)
    {
        return SpaceIds.TryGetValue(sessionId, out var spaceId)
            ? spaceId
            : _sessionControlStateService?.GetSpaceId(sessionId);
    }

    private string? GetWorkspacePath(string sessionId)
    {
        return WorkspacePaths.TryGetValue(sessionId, out var workspacePath)
            ? workspacePath
            : _sessionControlStateService?.GetWorkspacePath(sessionId);
    }

    private string? GetSurface(string sessionId)
    {
        return Surfaces.TryGetValue(sessionId, out var surface)
            ? surface
            : _sessionControlStateService?.GetSurface(sessionId);
    }

    private string? ResolveLaunchOrigin(string sessionId, string? spaceId)
    {
        return SessionLaunchOrigins.Normalize(GetStoredLaunchOrigin(sessionId)) ??
               (string.IsNullOrWhiteSpace(spaceId)
                   ? SessionLaunchOrigins.AdHoc
                   : SessionLaunchOrigins.Space);
    }

    private static bool IsAdHoc(string? launchOrigin)
    {
        return string.Equals(
            SessionLaunchOrigins.Normalize(launchOrigin),
            SessionLaunchOrigins.AdHoc,
            StringComparison.Ordinal);
    }

    private IReadOnlyList<string> GetTmuxFamilySessionIds(string sessionId)
    {
        var rootSessionId = TmuxParentSessions.TryGetValue(sessionId, out var parentSessionId)
            ? parentSessionId
            : sessionId;

        var sessionIds = new HashSet<string>(StringComparer.Ordinal)
        {
            rootSessionId
        };

        foreach (var kvp in TmuxParentSessions)
        {
            if (string.Equals(kvp.Value, rootSessionId, StringComparison.Ordinal))
            {
                sessionIds.Add(kvp.Key);
            }
        }

        if (SessionCache.ContainsKey(sessionId))
        {
            sessionIds.Add(sessionId);
        }

        return sessionIds
            .Where(id => SessionCache.ContainsKey(id))
            .ToList();
    }

    private static string GetDropsBasePath(bool isServiceMode)
    {
        if (isServiceMode && OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "MidTerm", "drops");
        }

        return Path.Combine(Path.GetTempPath(), "mt-drops");
    }
}
