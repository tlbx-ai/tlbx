using System.Collections.Concurrent;
using System.Text;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models.Git;

namespace Ai.Tlbx.MidTerm.Services.Git;

public sealed class GitWatcherService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<string, RepoWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _sessionToRepo = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, GitRepoBinding>> _sessionExtraRepos = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _subscribedSessions = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim _globalRefreshThrottle = new(2, 2);

    private sealed class RepoWatcher : IDisposable
    {
        private sealed class OwnedCancellationSource : IDisposable
        {
            private CancellationTokenSource? _cts = new();

            public CancellationToken Token => _cts?.Token ?? CancellationToken.None;

            public CancellationToken Replace()
            {
                var previous = _cts;
                var next = new CancellationTokenSource();
                _cts = next;
                previous?.Cancel();
                previous?.Dispose();
                return next.Token;
            }

            public void Dispose()
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
            }
        }

        private readonly List<FileSystemWatcher> _indexWatchers = [];
        public int RefCount;
        private readonly OwnedCancellationSource _debounce = new();
        public GitStatusResponse? CachedStatus;
        public string? LastFingerprint;
        public volatile bool IsDisposed;
        public readonly SemaphoreSlim RefreshGate = new(1, 1);
        public volatile bool RefreshPending;
        public int SubscriberCount;
        private readonly OwnedCancellationSource _poll = new();

        public void StartIndexWatcher(IEnumerable<string> gitDirs, FileSystemEventHandler onIndexChange)
        {
            foreach (var existing in _indexWatchers)
            {
                existing.EnableRaisingEvents = false;
                existing.Dispose();
            }
            _indexWatchers.Clear();

            foreach (var gitDir in gitDirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(gitDir))
                {
                    continue;
                }

                var watcher = new FileSystemWatcher(gitDir)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                };

                watcher.Filters.Add("index");
                watcher.Filters.Add("HEAD");
                watcher.Filters.Add("FETCH_HEAD");
                watcher.Changed += onIndexChange;
                watcher.Created += onIndexChange;
                watcher.Renamed += (s, e) => onIndexChange(s, e);
                watcher.EnableRaisingEvents = true;
                _indexWatchers.Add(watcher);
            }
        }

        public CancellationToken DebounceToken => _debounce.Token;
        public CancellationToken PollToken => _poll.Token;

        public CancellationToken ReplaceDebounce()
        {
            return _debounce.Replace();
        }

        public CancellationToken ReplacePoll()
        {
            return _poll.Replace();
        }

        public void StopPolling()
        {
            _poll.Dispose();
        }

        public void Dispose()
        {
            IsDisposed = true;
            _poll.Dispose();
            _debounce.Dispose();
            foreach (var watcher in _indexWatchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _indexWatchers.Clear();
            RefreshGate.Dispose();
        }
    }

    public event Action<string, GitStatusResponse>? OnStatusChanged;
    public event Action<string>? OnReposChanged;

    public async Task RegisterSessionAsync(string sessionId, string? workingDir)
    {
        if (string.IsNullOrEmpty(workingDir))
        {
            Log.Verbose(() => $"[Git] RegisterSession({sessionId}): workingDir is null/empty");
            return;
        }

        Log.Verbose(() => $"[Git] RegisterSession({sessionId}): cwd={workingDir}");

        var repoRoot = await GitCommandRunner.GetRepoRootAsync(workingDir);
        if (repoRoot is null)
        {
            Log.Verbose(() => $"[Git] RegisterSession({sessionId}): not a git repo at {workingDir}");
            return;
        }

        repoRoot = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar);
        Log.Verbose(() => $"[Git] RegisterSession({sessionId}): repoRoot={repoRoot}");

        var changed = true;

        if (_sessionToRepo.TryGetValue(sessionId, out var existing))
        {
            if (string.Equals(existing, repoRoot, StringComparison.OrdinalIgnoreCase))
            {
                Log.Verbose(() => $"[Git] RegisterSession({sessionId}): already registered for {repoRoot}");
                changed = false;
            }
            else
            {
                ReleaseRepo(existing);
            }
        }

        if (changed)
        {
            _sessionToRepo[sessionId] = repoRoot;
            AddRef(repoRoot, workingDir);
        }

        await RefreshStatusAsync(repoRoot);
        if (changed)
        {
            OnReposChanged?.Invoke(sessionId);
        }

        Log.Verbose(() => $"[Git] RegisterSession({sessionId}): refresh complete, cached={_watchers.TryGetValue(repoRoot, out var w) && w.CachedStatus is not null}");
    }

    public void UnregisterSession(string sessionId)
    {
        _subscribedSessions.TryRemove(sessionId, out _);
        if (_sessionToRepo.TryRemove(sessionId, out var repoRoot))
        {
            ReleaseRepo(repoRoot);
        }

        if (_sessionExtraRepos.TryRemove(sessionId, out var repos))
        {
            foreach (var repo in repos.Keys)
            {
                ReleaseRepo(repo);
            }
        }
    }

    private void ReleaseRepo(string repoRoot)
    {
        if (!_watchers.TryGetValue(repoRoot, out var watcher)) return;

        if (Interlocked.Decrement(ref watcher.RefCount) <= 0)
        {
            if (_watchers.TryRemove(repoRoot, out var removed))
            {
                removed.Dispose();
            }
        }
    }

    private RepoWatcher AddRef(string repoRoot, string workingDir)
    {
        var watcher = _watchers.GetOrAdd(repoRoot, root => CreateWatcher(root, workingDir));
        Interlocked.Increment(ref watcher.RefCount);
        return watcher;
    }

    public GitStatusResponse? GetCachedStatus(string sessionId, string? repoRoot = null)
    {
        var resolved = ResolveRepoRoot(sessionId, repoRoot);
        if (resolved is null) return null;
        if (!_watchers.TryGetValue(resolved, out var watcher)) return null;
        return watcher.CachedStatus;
    }

    public string? GetRepoRoot(string sessionId)
    {
        return _sessionToRepo.TryGetValue(sessionId, out var root) ? root : null;
    }

    public string? ResolveRepoRoot(string sessionId, string? repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return GetRepoRoot(sessionId);
        }

        var full = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (SessionHasRepo(sessionId, full))
        {
            return full;
        }

        return null;
    }

    public bool SessionHasRepo(string sessionId, string repoRoot)
    {
        if (_sessionToRepo.TryGetValue(sessionId, out var primary)
            && string.Equals(primary, repoRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return _sessionExtraRepos.TryGetValue(sessionId, out var repos)
            && repos.ContainsKey(repoRoot);
    }

    public string[] GetSessionIdsForRepo(string repoRoot)
    {
        var ids = new List<string>();
        foreach (var pair in _sessionToRepo)
        {
            if (string.Equals(pair.Value, repoRoot, StringComparison.OrdinalIgnoreCase))
            {
                ids.Add(pair.Key);
            }
        }

        foreach (var pair in _sessionExtraRepos)
        {
            if (pair.Value.ContainsKey(repoRoot))
            {
                ids.Add(pair.Key);
            }
        }

        return ids.Distinct(StringComparer.Ordinal).ToArray();
    }

    public GitRepoBinding[] GetRepoBindings(string sessionId)
    {
        var result = new List<GitRepoBinding>();
        if (_sessionToRepo.TryGetValue(sessionId, out var primary))
        {
            result.Add(CreateBinding(primary, "cwd", "auto", true));
        }

        if (_sessionExtraRepos.TryGetValue(sessionId, out var extra))
        {
            result.AddRange(extra.Values.OrderBy(repo => repo.Label, StringComparer.OrdinalIgnoreCase)
                .Select(repo => CreateBinding(repo.RepoRoot, repo.Role, repo.Source, false, repo.Label)));
        }

        return result.ToArray();
    }

    public async Task<GitRepoBinding[]?> AddSessionRepoAsync(string sessionId, string path, string? label, string? role, string source)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var fullPath = Path.GetFullPath(path.Trim());
        var repoRoot = await GitCommandRunner.GetRepoRootAsync(fullPath);
        if (repoRoot is null) return null;

        repoRoot = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (_sessionToRepo.TryGetValue(sessionId, out var primary)
            && string.Equals(primary, repoRoot, StringComparison.OrdinalIgnoreCase))
        {
            return GetRepoBindings(sessionId);
        }

        var repos = _sessionExtraRepos.GetOrAdd(
            sessionId,
            _ => new ConcurrentDictionary<string, GitRepoBinding>(StringComparer.OrdinalIgnoreCase));

        var nextLabel = string.IsNullOrWhiteSpace(label) ? Path.GetFileName(repoRoot) : label.Trim();
        var nextRole = string.IsNullOrWhiteSpace(role) ? "target" : role.Trim();

        if (repos.TryGetValue(repoRoot, out var existing))
        {
            if (!string.Equals(existing.Label, nextLabel, StringComparison.Ordinal)
                || !string.Equals(existing.Role, nextRole, StringComparison.Ordinal)
                || !string.Equals(existing.Source, source, StringComparison.Ordinal))
            {
                existing.Label = nextLabel;
                existing.Role = nextRole;
                existing.Source = source;
                OnReposChanged?.Invoke(sessionId);
            }
        }
        else if (repos.TryAdd(repoRoot, new GitRepoBinding
        {
            RepoRoot = repoRoot,
            Label = nextLabel,
            Role = nextRole,
            Source = source,
            IsPrimary = false
        }))
        {
            AddRef(repoRoot, fullPath);
            if (_subscribedSessions.ContainsKey(sessionId) && _watchers.TryGetValue(repoRoot, out var watcher))
            {
                if (Interlocked.Increment(ref watcher.SubscriberCount) == 1)
                {
                    StartPolling(repoRoot, watcher);
                }
            }

            OnReposChanged?.Invoke(sessionId);
        }

        await RefreshStatusAsync(repoRoot);
        return GetRepoBindings(sessionId);
    }

    public async Task RestoreSessionExtraReposAsync(string sessionId, IEnumerable<TtyHostGitRepoMetadata> repos)
    {
        foreach (var repo in repos)
        {
            if (string.IsNullOrWhiteSpace(repo.RepoRoot))
            {
                continue;
            }

            await AddSessionRepoAsync(
                sessionId,
                repo.RepoRoot,
                repo.Label,
                repo.Role,
                string.IsNullOrWhiteSpace(repo.Source) ? "manual" : repo.Source).ConfigureAwait(false);
        }
    }

    public bool RemoveSessionRepo(string sessionId, string repoRoot)
    {
        var resolved = ResolveRepoRoot(sessionId, repoRoot);
        if (resolved is null || string.Equals(resolved, GetRepoRoot(sessionId), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!_sessionExtraRepos.TryGetValue(sessionId, out var repos) || !repos.TryRemove(resolved, out _))
        {
            return false;
        }

        if (_subscribedSessions.ContainsKey(sessionId) && _watchers.TryGetValue(resolved, out var watcher)
            && Interlocked.Decrement(ref watcher.SubscriberCount) <= 0)
        {
            watcher.StopPolling();
        }

        ReleaseRepo(resolved);
        OnReposChanged?.Invoke(sessionId);
        return true;
    }

    private GitRepoBinding CreateBinding(string repoRoot, string role, string source, bool isPrimary, string? label = null)
    {
        var status = _watchers.TryGetValue(repoRoot, out var watcher) ? watcher.CachedStatus : null;
        if (status is not null)
        {
            status.Label = label ?? Path.GetFileName(repoRoot);
            status.Role = role;
            status.Source = source;
            status.IsPrimary = isPrimary;
        }

        return new GitRepoBinding
        {
            RepoRoot = repoRoot,
            Label = label ?? Path.GetFileName(repoRoot),
            Role = role,
            Source = source,
            IsPrimary = isPrimary,
            Status = status
        };
    }

    public async Task RefreshStatusAsync(string repoRoot)
    {
        try
        {
            var statusTask = GitCommandRunner.GetStatusAsync(repoRoot);
            var recentCommitsTask = GitCommandRunner.GetLogAsync(repoRoot);
            var stashCountTask = GitCommandRunner.GetStashCountAsync(repoRoot);
            var numStatTask = GitCommandRunner.GetNumStatAsync(repoRoot);

            await Task.WhenAll(statusTask, recentCommitsTask, stashCountTask, numStatTask);

            var status = await statusTask;
            status.RecentCommits = await recentCommitsTask;
            status.StashCount = await stashCountTask;
            var numStat = await numStatTask;
            MergeNumStat(status, numStat);

            if (_watchers.TryGetValue(repoRoot, out var watcher))
            {
                var fingerprint = StatusFingerprint(status);
                if (watcher.LastFingerprint == fingerprint) return;
                watcher.CachedStatus = status;
                watcher.LastFingerprint = fingerprint;
            }

            OnStatusChanged?.Invoke(repoRoot, status);
        }
        catch (Exception ex)
        {
            Log.Error(() => $"[Git] RefreshStatus failed for {repoRoot}: {ex.Message}");
        }
    }

    private static void MergeNumStat(GitStatusResponse status, Dictionary<string, (int Additions, int Deletions)> numStat)
    {
        var totalAdd = 0;
        var totalDel = 0;

        void ApplyToEntries(GitFileEntry[] entries)
        {
            foreach (var entry in entries)
            {
                if (numStat.TryGetValue(entry.Path, out var stats))
                {
                    entry.Additions = stats.Additions;
                    entry.Deletions = stats.Deletions;
                    totalAdd += stats.Additions;
                    totalDel += stats.Deletions;
                }
            }
        }

        ApplyToEntries(status.Staged);
        ApplyToEntries(status.Modified);

        status.TotalAdditions = totalAdd;
        status.TotalDeletions = totalDel;
    }

    private RepoWatcher CreateWatcher(string repoRoot, string workingDir)
    {
        var watcher = new RepoWatcher();
        try
        {
            var repoInfo = GitCommandRunner.GetRepositoryInfoAsync(workingDir).GetAwaiter().GetResult();
            if (repoInfo is not null)
            {
                void OnIndexChange(object? s, FileSystemEventArgs e)
                {
                    DebouncedRefresh(repoRoot, watcher);
                }

                watcher.StartIndexWatcher(
                    [repoInfo.GitDir, repoInfo.CommonGitDir],
                    OnIndexChange);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"[Git] Watcher setup failed for {repoRoot}: {ex.Message}");
        }

        return watcher;
    }

    private void DebouncedRefresh(string repoRoot, RepoWatcher watcher)
    {
        if (watcher.IsDisposed) return;

        try
        {
            var token = watcher.ReplaceDebounce();
            _ = RunDebouncedRefreshAsync(repoRoot, watcher, token);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RunDebouncedRefreshAsync(string repoRoot, RepoWatcher watcher, CancellationToken token)
    {
        try
        {
            await Task.Delay(500, token).ConfigureAwait(false);
            if (!token.IsCancellationRequested)
            {
                await CoalescedRefreshAsync(repoRoot, watcher).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task CoalescedRefreshAsync(string repoRoot, RepoWatcher watcher)
    {
        var refreshToken = watcher.DebounceToken;
        if (refreshToken == CancellationToken.None)
        {
            refreshToken = watcher.PollToken;
        }

        if (!watcher.RefreshGate.Wait(0, refreshToken))
        {
            watcher.RefreshPending = true;
            return;
        }

        try
        {
            await _globalRefreshThrottle.WaitAsync(refreshToken);
            try
            {
                do
                {
                    watcher.RefreshPending = false;
                    await RefreshStatusAsync(repoRoot);
                } while (watcher.RefreshPending && !watcher.IsDisposed);
            }
            finally
            {
                _globalRefreshThrottle.Release();
            }
        }
        finally
        {
            watcher.RefreshGate.Release();
        }
    }

    public void Subscribe(string sessionId)
    {
        _subscribedSessions[sessionId] = 1;
        foreach (var repoRoot in GetRepoBindings(sessionId).Select(repo => repo.RepoRoot))
        {
            if (_watchers.TryGetValue(repoRoot, out var watcher)
                && Interlocked.Increment(ref watcher.SubscriberCount) == 1)
            {
                StartPolling(repoRoot, watcher);
            }
        }
    }

    public void Unsubscribe(string sessionId)
    {
        _subscribedSessions.TryRemove(sessionId, out _);
        foreach (var repoRoot in GetRepoBindings(sessionId).Select(repo => repo.RepoRoot))
        {
            if (_watchers.TryGetValue(repoRoot, out var watcher)
                && Interlocked.Decrement(ref watcher.SubscriberCount) <= 0)
            {
                watcher.StopPolling();
            }
        }
    }

    private void StartPolling(string repoRoot, RepoWatcher watcher)
    {
        var token = watcher.ReplacePoll();
        _ = PollLoopAsync(repoRoot, watcher, token);
    }

    private async Task PollLoopAsync(string repoRoot, RepoWatcher watcher, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !watcher.IsDisposed)
        {
            try
            {
                await Task.Delay(PollInterval, ct);
                await CoalescedRefreshAsync(repoRoot, watcher);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static string StatusFingerprint(GitStatusResponse s)
    {
        var sb = new StringBuilder(256);
        sb.Append(s.Branch).Append('|');
        sb.Append(s.Ahead).Append('|').Append(s.Behind).Append('|');
        sb.Append(s.TotalAdditions).Append('|').Append(s.TotalDeletions).Append('|');
        sb.Append(s.StashCount);
        if (s.RecentCommits.Length > 0)
            sb.Append('|').Append(s.RecentCommits[0].ShortHash);
        foreach (var f in s.Staged)
            sb.Append('|').Append(f.Status).Append(':').Append(f.Path).Append(':').Append(f.OriginalPath).Append(':').Append(f.Additions).Append(',').Append(f.Deletions);
        sb.Append('\x1F');
        foreach (var f in s.Modified)
            sb.Append('|').Append(f.Status).Append(':').Append(f.Path).Append(':').Append(f.Additions).Append(',').Append(f.Deletions);
        sb.Append('\x1F');
        foreach (var f in s.Conflicted)
            sb.Append('|').Append(f.Path);
        sb.Append('\x1F');
        foreach (var f in s.Untracked)
            sb.Append('|').Append(f.Path);
        return sb.ToString();
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers.Values)
        {
            watcher.Dispose();
        }
        _watchers.Clear();
        _sessionToRepo.Clear();
        _sessionExtraRepos.Clear();
        _subscribedSessions.Clear();
    }
}
