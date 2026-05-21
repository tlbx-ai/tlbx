using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Models.History;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Models.Spaces;
using Ai.Tlbx.MidTerm.Services.Git;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.Spaces;

public sealed class SpaceService
{
    private readonly string _spacesPath;
    private readonly Lock _lock = new();
    private readonly SettingsService _settingsService;
    private readonly HistoryService _historyService;
    private SpaceStore _store = new();

    public SpaceService(SettingsService settingsService, HistoryService historyService)
    {
        _settingsService = settingsService;
        _spacesPath = Path.Combine(settingsService.SettingsDirectory, "spaces.json");
        _historyService = historyService;
        Load();
        EnsureMigratedFromHistory();
    }

    public async Task<List<SpaceSummaryDto>> GetSpacesAsync(
        TtyHostSessionManager sessionManager,
        bool includeWorkspaces = true,
        bool pinnedOnly = false,
        CancellationToken ct = default)
    {
        if (includeWorkspaces)
        {
            await NormalizeStoreAsync(
                pinnedOnly
                    ? static space => space.IsPinned
                    : static _ => true,
                ct).ConfigureAwait(false);
        }

        List<SpaceRecord> snapshot;
        lock (_lock)
        {
            snapshot = _store.Spaces
                .Where(space => !pinnedOnly || space.IsPinned)
                .Select(Clone)
                .OrderBy(space => DeriveVisibleSpaceName(space), StringComparer.OrdinalIgnoreCase)
                .ThenBy(space => space.RootPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var spaces = new List<SpaceSummaryDto>(snapshot.Count);
        foreach (var record in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            var workspaces = includeWorkspaces
                ? (await BuildWorkspacesAsync(record, sessionManager, ct).ConfigureAwait(false)).ToArray()
                : [];
            spaces.Add(BuildSpaceSummary(record, workspaces));
        }

        return spaces;
    }

    public async Task<SpaceSummaryDto?> ImportAsync(string path, string? label, TtyHostSessionManager sessionManager, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalizedPath = NormalizePath(path);
        if (!Directory.Exists(normalizedPath))
        {
            return null;
        }

        var repoInfo = await GitCommandRunner.GetRepositoryInfoAsync(normalizedPath).ConfigureAwait(false);

        SpaceRecord record;
        lock (_lock)
        {
            record = repoInfo is null
                ? ImportPlainLocked(normalizedPath, label)
                : ImportGitLocked(normalizedPath, label, repoInfo);
            PersistLocked();
        }

        return await BuildSpaceSummaryAsync(record, sessionManager, ct).ConfigureAwait(false);
    }

    public async Task<SpaceSummaryDto?> GetSpaceAsync(
        string spaceId,
        TtyHostSessionManager sessionManager,
        CancellationToken ct = default)
    {
        var record = await GetNormalizedSpaceRecordAsync(spaceId, ct).ConfigureAwait(false);

        return record is null
            ? null
            : await BuildSpaceSummaryAsync(record, sessionManager, ct).ConfigureAwait(false);
    }

    public async Task ReconcileSessionBindingsAsync(
        TtyHostSessionManager sessionManager,
        CancellationToken ct = default)
    {
        await NormalizeStoreAsync(static _ => true, ct).ConfigureAwait(false);

        List<SpaceRecord> snapshot;
        lock (_lock)
        {
            snapshot = _store.Spaces.Select(Clone).ToList();
        }

        if (snapshot.Count == 0)
        {
            return;
        }

        var workspaceBindings = await BuildWorkspaceBindingIndexAsync(snapshot, ct).ConfigureAwait(false);
        if (workspaceBindings.PathLookup.Count == 0)
        {
            return;
        }

        foreach (var session in sessionManager.GetSessionList().Sessions)
        {
            ct.ThrowIfCancellationRequested();

            var launchOrigin = sessionManager.GetLaunchOrigin(session.Id);
            if (string.IsNullOrWhiteSpace(launchOrigin))
            {
                launchOrigin = string.IsNullOrWhiteSpace(session.SpaceId)
                    ? SessionLaunchOrigins.AdHoc
                    : SessionLaunchOrigins.Space;
                sessionManager.SetLaunchOrigin(session.Id, launchOrigin);
            }

            if (!string.Equals(
                    SessionLaunchOrigins.Normalize(launchOrigin),
                    SessionLaunchOrigins.Space,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var normalizedWorkspacePath = NormalizeOptionalPath(session.WorkspacePath)
                ?? NormalizeOptionalPath(session.CurrentDirectory);
            if (string.IsNullOrWhiteSpace(normalizedWorkspacePath))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(session.SpaceId) &&
                workspaceBindings.SpaceWorkspacePaths.TryGetValue(session.SpaceId, out var boundPaths) &&
                boundPaths.Contains(normalizedWorkspacePath))
            {
                if (!string.Equals(session.WorkspacePath, normalizedWorkspacePath, StringComparison.OrdinalIgnoreCase))
                {
                    sessionManager.SetWorkspacePath(session.Id, normalizedWorkspacePath);
                }

                continue;
            }

            if (!workspaceBindings.PathLookup.TryGetValue(normalizedWorkspacePath, out var matches) ||
                matches.Count != 1)
            {
                continue;
            }

            var match = matches[0];
            sessionManager.SetSpaceId(session.Id, match.SpaceId);
            sessionManager.SetWorkspacePath(session.Id, match.WorkspacePath);
        }
    }

    public async Task<string?> ResolveWorkspacePathAsync(string spaceId, string workspaceKey, CancellationToken ct = default)
    {
        var record = await GetNormalizedSpaceRecordAsync(spaceId, ct).ConfigureAwait(false);

        if (record is null || string.IsNullOrWhiteSpace(workspaceKey))
        {
            return null;
        }

        if (!string.Equals(record.Kind, SpaceKinds.Git, StringComparison.Ordinal))
        {
            var plainKey = BuildWorkspaceKey(record.RootPath);
            return string.Equals(plainKey, workspaceKey, StringComparison.OrdinalIgnoreCase)
                ? NormalizePath(record.RootPath)
                : null;
        }

        var configuredWorktreeRoot = GetConfiguredWorktreeRootDirectory();
        var worktrees = await GitCommandRunner.ListWorktreesAsync(record.RootPath).ConfigureAwait(false);
        return worktrees
            .Select(worktree => NormalizePath(worktree.Path))
            .Where(path => ShouldDisplayWorktree(record.RootPath, path, configuredWorktreeRoot))
            .FirstOrDefault(path => string.Equals(BuildWorkspaceKey(path), workspaceKey, StringComparison.OrdinalIgnoreCase));
    }

    public bool UpdateSpace(string spaceId, string? label, bool? isPinned)
    {
        if (string.IsNullOrWhiteSpace(spaceId))
        {
            return false;
        }

        lock (_lock)
        {
            var record = _store.Spaces.FirstOrDefault(space => string.Equals(space.Id, spaceId, StringComparison.Ordinal));
            if (record is null)
            {
                return false;
            }

            var normalizedLabel = label is null
                ? record.Label
                : string.IsNullOrWhiteSpace(label)
                    ? DeriveDefaultLabel(record.ImportedPath)
                    : label.Trim();
            var nextPinned = isPinned ?? record.IsPinned;

            if (string.Equals(record.Label, normalizedLabel, StringComparison.Ordinal) &&
                record.IsPinned == nextPinned)
            {
                return true;
            }

            record.Label = normalizedLabel;
            record.IsPinned = nextPinned;
            record.UpdatedAtUtc = DateTime.UtcNow;
            PersistLocked();
            return true;
        }
    }

    public bool Delete(string spaceId)
    {
        if (string.IsNullOrWhiteSpace(spaceId))
        {
            return false;
        }

        lock (_lock)
        {
            var removed = _store.Spaces.RemoveAll(space => string.Equals(space.Id, spaceId, StringComparison.Ordinal)) > 0;
            if (removed)
            {
                PersistLocked();
            }

            return removed;
        }
    }

    public async Task<SpaceSummaryDto?> InitGitAsync(
        string spaceId,
        TtyHostSessionManager sessionManager,
        CancellationToken ct = default)
    {
        SpaceRecord? record;
        lock (_lock)
        {
            record = _store.Spaces.FirstOrDefault(space => string.Equals(space.Id, spaceId, StringComparison.Ordinal));
            record = record is null ? null : Clone(record);
        }

        if (record is null || !Directory.Exists(record.RootPath))
        {
            return null;
        }

        var (exitCode, _, stderr) = await GitCommandRunner.RunGitInDirectoryAsync(record.RootPath, "init").ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "git init failed." : stderr.Trim());
        }

        var repoInfo = await GitCommandRunner.GetRepositoryInfoAsync(record.RootPath).ConfigureAwait(false);
        if (repoInfo is null)
        {
            throw new InvalidOperationException("Git repository initialization completed, but MidTerm could not resolve the repository metadata.");
        }

        lock (_lock)
        {
            var stored = _store.Spaces.FirstOrDefault(space => string.Equals(space.Id, spaceId, StringComparison.Ordinal));
            if (stored is null)
            {
                return null;
            }

            stored.Kind = SpaceKinds.Git;
            stored.RootPath = repoInfo.RepoRoot;
            stored.ImportedPath = repoInfo.RepoRoot;
            stored.CommonRepoId = NormalizePath(repoInfo.CommonGitDir);
            stored.IsPinned = true;
            stored.UpdatedAtUtc = DateTime.UtcNow;
            PersistLocked();
            record = Clone(stored);
        }

        return await BuildSpaceSummaryAsync(record!, sessionManager, ct).ConfigureAwait(false);
    }

    public async Task<SpaceSummaryDto?> CreateWorktreeAsync(
        string spaceId,
        string targetPath,
        string branchName,
        string? worktreeLabel,
        TtyHostSessionManager sessionManager,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(branchName))
        {
            return null;
        }

        SpaceRecord? record;
        lock (_lock)
        {
            record = _store.Spaces.FirstOrDefault(space => string.Equals(space.Id, spaceId, StringComparison.Ordinal));
            record = record is null ? null : Clone(record);
        }

        if (record is null || !string.Equals(record.Kind, SpaceKinds.Git, StringComparison.Ordinal))
        {
            return null;
        }

        var normalizedTargetPath = NormalizePath(targetPath);
        var configuredWorktreeRoot = GetConfiguredWorktreeRootDirectory();
        if (!string.IsNullOrWhiteSpace(configuredWorktreeRoot) &&
            !IsPathWithinRoot(normalizedTargetPath, configuredWorktreeRoot))
        {
            throw new InvalidOperationException(
                $"Worktrees must be created under the configured worktree root: {configuredWorktreeRoot}");
        }

        if (Directory.Exists(normalizedTargetPath) && Directory.EnumerateFileSystemEntries(normalizedTargetPath).Any())
        {
            throw new InvalidOperationException("The target worktree path already exists and is not empty.");
        }

        var branch = branchName.Trim();
        var (exitCode, _, stderr) = await GitCommandRunner.RunGitInDirectoryAsync(
            record.RootPath,
            "worktree",
            "add",
            "-b",
            branch,
            normalizedTargetPath).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "git worktree add failed." : stderr.Trim());
        }

        lock (_lock)
        {
            var stored = _store.Spaces.FirstOrDefault(space => string.Equals(space.Id, spaceId, StringComparison.Ordinal));
            if (stored is not null)
            {
                SetWorktreeLabelLocked(stored, normalizedTargetPath, worktreeLabel);
                stored.UpdatedAtUtc = DateTime.UtcNow;
                PersistLocked();
            }
        }

        return await GetSpaceAsync(spaceId, sessionManager, ct).ConfigureAwait(false);
    }

    public async Task<SpaceSummaryDto?> UpdateWorkspaceLabelAsync(
        string spaceId,
        string workspaceKey,
        string? label,
        TtyHostSessionManager sessionManager,
        CancellationToken ct = default)
    {
        var workspacePath = await ResolveWorkspacePathAsync(spaceId, workspaceKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return null;
        }

        lock (_lock)
        {
            var stored = _store.Spaces.FirstOrDefault(space => string.Equals(space.Id, spaceId, StringComparison.Ordinal));
            if (stored is null || !string.Equals(stored.Kind, SpaceKinds.Git, StringComparison.Ordinal))
            {
                return null;
            }

            if (string.Equals(NormalizePath(stored.RootPath), NormalizePath(workspacePath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The main workspace cannot be renamed.");
            }

            SetWorktreeLabelLocked(stored, workspacePath, label);
            stored.UpdatedAtUtc = DateTime.UtcNow;
            PersistLocked();
        }

        return await GetSpaceAsync(spaceId, sessionManager, ct).ConfigureAwait(false);
    }

    public async Task<SpaceSummaryDto?> RemoveWorktreeAsync(
        string spaceId,
        string workspaceKey,
        bool force,
        TtyHostSessionManager sessionManager,
        CancellationToken ct = default)
    {
        var workspacePath = await ResolveWorkspacePathAsync(spaceId, workspaceKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return null;
        }

        SpaceRecord? record;
        lock (_lock)
        {
            record = _store.Spaces.FirstOrDefault(space => string.Equals(space.Id, spaceId, StringComparison.Ordinal));
            record = record is null ? null : Clone(record);
        }

        if (record is null || !string.Equals(record.Kind, SpaceKinds.Git, StringComparison.Ordinal))
        {
            return null;
        }

        var normalizedWorkspacePath = NormalizePath(workspacePath);
        if (string.Equals(normalizedWorkspacePath, NormalizePath(record.RootPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The main workspace cannot be removed.");
        }

        var activeSessions = sessionManager.GetSessionList().Sessions
            .Any(session => string.Equals(
                NormalizeOptionalPath(session.WorkspacePath) ?? NormalizeOptionalPath(session.CurrentDirectory),
                normalizedWorkspacePath,
                StringComparison.OrdinalIgnoreCase));
        if (activeSessions)
        {
            throw new InvalidOperationException("Close active sessions in this worktree before deleting it.");
        }

        var changeCount = await GitCommandRunner.GetWorktreeChangeCountAsync(normalizedWorkspacePath).ConfigureAwait(false);
        if (changeCount > 0 && !force)
        {
            throw new InvalidOperationException("This worktree has uncommitted changes. Confirm deletion again to remove it.");
        }

        var args = force
            ? new[] { "worktree", "remove", "--force", normalizedWorkspacePath }
            : new[] { "worktree", "remove", normalizedWorkspacePath };
        var (exitCode, _, stderr) = await GitCommandRunner.RunGitInDirectoryAsync(record.RootPath, args).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "git worktree remove failed." : stderr.Trim());
        }

        lock (_lock)
        {
            var stored = _store.Spaces.FirstOrDefault(space => string.Equals(space.Id, spaceId, StringComparison.Ordinal));
            if (stored is not null)
            {
                RemoveWorktreeLabelLocked(stored, normalizedWorkspacePath);
                stored.UpdatedAtUtc = DateTime.UtcNow;
                PersistLocked();
            }
        }

        return await GetSpaceAsync(spaceId, sessionManager, ct).ConfigureAwait(false);
    }

    public IReadOnlyList<LaunchEntry> GetRecentEntries(int count = 6)
    {
        return _historyService
            .GetEntries()
            .Where(entry => !entry.IsStarred)
            .OrderByDescending(entry => entry.LastUsed)
            .Take(Math.Max(1, count))
            .ToList();
    }

    public bool RemoveRecentEntry(string entryId)
    {
        return _historyService.RemoveEntry(entryId);
    }

    private async Task<SpaceSummaryDto> BuildSpaceSummaryAsync(
        SpaceRecord record,
        TtyHostSessionManager sessionManager,
        CancellationToken ct)
    {
        var workspaces = (await BuildWorkspacesAsync(record, sessionManager, ct).ConfigureAwait(false)).ToArray();
        return BuildSpaceSummary(record, workspaces);
    }

    private static SpaceSummaryDto BuildSpaceSummary(
        SpaceRecord record,
        IReadOnlyCollection<SpaceWorkspaceDto>? workspaces)
    {
        var workspaceArray = workspaces?.ToArray() ?? [];
        return new SpaceSummaryDto
        {
            Id = record.Id,
            DisplayName = DeriveVisibleSpaceName(record),
            Label = record.Label,
            Kind = record.Kind,
            RootPath = record.RootPath,
            ImportedPath = record.ImportedPath,
            CommonRepoId = record.CommonRepoId,
            IsPinned = record.IsPinned,
            CanInitGit = CanInitGit(record),
            CanCreateWorktree = CanCreateWorktree(record),
            PrimaryWorkspaceKey = workspaceArray.FirstOrDefault(workspace => workspace.IsMain)?.Key
                ?? workspaceArray.FirstOrDefault()?.Key
                ?? BuildWorkspaceKey(record.RootPath),
            CreatedAtUtc = record.CreatedAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc,
            Workspaces = workspaceArray
        };
    }

    private async Task<List<SpaceWorkspaceDto>> BuildWorkspacesAsync(
        SpaceRecord record,
        TtyHostSessionManager sessionManager,
        CancellationToken ct)
    {
        if (!string.Equals(record.Kind, SpaceKinds.Git, StringComparison.Ordinal))
        {
            return [BuildPlainWorkspace(record, sessionManager)];
        }

        var configuredWorktreeRoot = GetConfiguredWorktreeRootDirectory();
        var worktrees = (await GitCommandRunner.ListWorktreesAsync(record.RootPath).ConfigureAwait(false))
            .Where(worktree => ShouldDisplayWorktree(record.RootPath, worktree.Path, configuredWorktreeRoot))
            .ToList();
        if (worktrees.Count == 0)
        {
            return [BuildPlainWorkspace(record, sessionManager)];
        }

        var localSessions = sessionManager.GetSessionList().Sessions;
        var sessionByWorkspace = localSessions
            .Select(session => new
            {
                Session = session,
                WorkspacePath = NormalizeOptionalPath(session.WorkspacePath) ?? NormalizeOptionalPath(session.CurrentDirectory)
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.WorkspacePath))
            .GroupBy(entry => entry.WorkspacePath!, entry => entry.Session, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var worktreeMetadata = (record.Worktrees ?? [])
            .Where(worktree => !string.IsNullOrWhiteSpace(worktree.Path))
            .GroupBy(worktree => NormalizePath(worktree.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last(),
                StringComparer.OrdinalIgnoreCase);

        var result = new List<SpaceWorkspaceDto>(worktrees.Count);
        foreach (var worktree in worktrees)
        {
            ct.ThrowIfCancellationRequested();
            var normalizedPath = NormalizePath(worktree.Path);
            sessionByWorkspace.TryGetValue(normalizedPath, out var workspaceSessions);
            workspaceSessions ??= [];
            worktreeMetadata.TryGetValue(normalizedPath, out var metadata);

            var changeCount = await GitCommandRunner.GetWorktreeChangeCountAsync(worktree.Path).ConfigureAwait(false);
            var activeSessions = workspaceSessions
                .Select(session => new SpaceWorkspaceSessionDto
                {
                    SessionId = session.Id,
                    Title = string.IsNullOrWhiteSpace(session.Name)
                        ? session.ShellType
                        : session.Name!,
                    Surface = string.IsNullOrWhiteSpace(session.Surface)
                        ? SpaceSurfaceKinds.Terminal
                        : session.Surface!,
                    AppServerControlOnly = session.AppServerControlOnly,
                    ProfileHint = session.ProfileHint
                })
                .OrderBy(session => session.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            result.Add(new SpaceWorkspaceDto
            {
                Key = BuildWorkspaceKey(worktree.Path),
                DisplayName = ResolveWorkspaceDisplayName(
                    record,
                    normalizedPath,
                    metadata?.Label),
                Path = normalizedPath,
                Kind = SpaceWorkspaceKinds.Worktree,
                Branch = worktree.Branch,
                Head = worktree.Head,
                IsMain = string.Equals(normalizedPath, NormalizePath(record.RootPath), StringComparison.OrdinalIgnoreCase),
                IsDetached = worktree.IsDetached,
                Locked = worktree.IsLocked,
                Prunable = worktree.IsPrunable,
                ChangeCount = changeCount,
                HasChanges = changeCount > 0,
                HasActiveAiSession = activeSessions.Any(IsAgentSession),
                ActiveSessions = activeSessions
            });
        }

        return result
            .OrderByDescending(workspace => workspace.IsMain)
            .ThenBy(workspace => workspace.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<WorkspaceBindingIndex> BuildWorkspaceBindingIndexAsync(
        IReadOnlyCollection<SpaceRecord> spaces,
        CancellationToken ct)
    {
        var configuredWorktreeRoot = GetConfiguredWorktreeRootDirectory();
        var pathLookup = new Dictionary<string, List<SpaceWorkspaceBinding>>(StringComparer.OrdinalIgnoreCase);
        var spaceWorkspacePaths = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var record in spaces)
        {
            ct.ThrowIfCancellationRequested();

            var bindings = new List<SpaceWorkspaceBinding>();
            if (!string.Equals(record.Kind, SpaceKinds.Git, StringComparison.Ordinal))
            {
                bindings.Add(new SpaceWorkspaceBinding(record.Id, NormalizePath(record.RootPath)));
            }
            else
            {
                var worktrees = await GitCommandRunner.ListWorktreesAsync(record.RootPath).ConfigureAwait(false);
                foreach (var worktreePath in worktrees
                    .Select(worktree => NormalizePath(worktree.Path))
                    .Where(path => ShouldDisplayWorktree(record.RootPath, path, configuredWorktreeRoot)))
                {
                    bindings.Add(new SpaceWorkspaceBinding(record.Id, worktreePath));
                }
            }

            if (bindings.Count == 0)
            {
                continue;
            }

            var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var binding in bindings)
            {
                uniquePaths.Add(binding.WorkspacePath);
                if (!pathLookup.TryGetValue(binding.WorkspacePath, out var existingBindings))
                {
                    existingBindings = [];
                    pathLookup[binding.WorkspacePath] = existingBindings;
                }

                existingBindings.Add(binding);
            }

            spaceWorkspacePaths[record.Id] = uniquePaths;
        }

        return new WorkspaceBindingIndex(pathLookup, spaceWorkspacePaths);
    }

    private static bool IsAgentSession(SpaceWorkspaceSessionDto session)
    {
        return session.AppServerControlOnly ||
               string.Equals(session.Surface, SpaceSurfaceKinds.Codex, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(session.Surface, SpaceSurfaceKinds.Claude, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(session.Surface, SpaceSurfaceKinds.Grok, StringComparison.OrdinalIgnoreCase);
    }

    private static SpaceWorkspaceDto BuildPlainWorkspace(SpaceRecord record, TtyHostSessionManager sessionManager)
    {
        var normalizedRoot = NormalizePath(record.RootPath);
        var sessions = sessionManager.GetSessionList().Sessions
            .Where(session => string.Equals(
                NormalizeOptionalPath(session.WorkspacePath) ?? NormalizeOptionalPath(session.CurrentDirectory),
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase))
            .Select(session => new SpaceWorkspaceSessionDto
            {
                SessionId = session.Id,
                Title = string.IsNullOrWhiteSpace(session.Name) ? session.ShellType : session.Name!,
                Surface = string.IsNullOrWhiteSpace(session.Surface) ? SpaceSurfaceKinds.Terminal : session.Surface!,
                AppServerControlOnly = session.AppServerControlOnly,
                ProfileHint = session.ProfileHint
            })
            .OrderBy(session => session.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SpaceWorkspaceDto
        {
            Key = BuildWorkspaceKey(record.RootPath),
            DisplayName = "Main",
            Path = normalizedRoot,
            Kind = SpaceWorkspaceKinds.Plain,
            IsMain = true,
            ActiveSessions = sessions,
            HasActiveAiSession = sessions.Any(IsAgentSession)
        };
    }

    private static string BuildWorkspaceKey(string path)
    {
        var normalized = NormalizePath(path)
            .Replace('\\', '/')
            .ToLowerInvariant();
        var bytes = Encoding.UTF8.GetBytes(normalized);
        return "ws_" + Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private string GetConfiguredWorktreeRootDirectory()
    {
        return NormalizePath(_settingsService.GetEffectiveWorktreeRootDirectory());
    }

    private static bool ShouldDisplayWorktree(string rootPath, string worktreePath, string configuredWorktreeRoot)
    {
        var normalizedRoot = NormalizePath(rootPath);
        var normalizedWorktree = NormalizePath(worktreePath);
        if (string.Equals(normalizedRoot, normalizedWorktree, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(configuredWorktreeRoot))
        {
            return IsPathWithinRoot(normalizedWorktree, configuredWorktreeRoot);
        }

        return false;
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        var normalizedPath = NormalizePath(path);
        var normalizedRoot = NormalizePath(root);
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedPath.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeOptionalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return NormalizePath(path);
    }

    private void EnsureMigratedFromHistory()
    {
        lock (_lock)
        {
            if (_store.MigratedFromHistory)
            {
                return;
            }

            foreach (var entry in _historyService.GetEntries().Where(entry => entry.IsStarred))
            {
                if (string.IsNullOrWhiteSpace(entry.WorkingDirectory))
                {
                    continue;
                }

                var normalizedPath = NormalizePath(entry.WorkingDirectory);
                if (!Directory.Exists(normalizedPath))
                {
                    continue;
                }

                try
                {
                    var repoInfo = GitCommandRunner.GetRepositoryInfoAsync(normalizedPath).GetAwaiter().GetResult();
                    if (repoInfo is null)
                    {
                        ImportPlainLocked(normalizedPath, entry.Label);
                    }
                    else
                    {
                        ImportGitLocked(normalizedPath, entry.Label, repoInfo);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(() => $"Space migration skipped '{normalizedPath}': {ex.Message}");
                    ImportPlainLocked(normalizedPath, entry.Label);
                }
            }

            _store.MigratedFromHistory = true;
            PersistLocked();
        }
    }

    private async Task NormalizeStoreAsync(Func<SpaceRecord, bool> shouldNormalize, CancellationToken ct)
    {
        List<SpaceRecord> snapshot;
        lock (_lock)
        {
            snapshot = _store.Spaces.Select(Clone).ToList();
        }

        if (snapshot.Count == 0)
        {
            return;
        }

        var normalizedStore = new SpaceStore
        {
            MigratedFromHistory = true,
            Spaces = []
        };

        foreach (var candidate in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            var normalized = shouldNormalize(candidate)
                ? await NormalizeRecordAsync(candidate).ConfigureAwait(false)
                : Clone(candidate);
            MergeResolvedRecord(normalizedStore.Spaces, normalized);
        }

        lock (_lock)
        {
            var currentJson = JsonSerializer.Serialize(_store, SpaceJsonContext.Default.SpaceStore);
            var normalizedJson = JsonSerializer.Serialize(normalizedStore, SpaceJsonContext.Default.SpaceStore);
            if (string.Equals(currentJson, normalizedJson, StringComparison.Ordinal))
            {
                return;
            }

            _store = normalizedStore;
            PersistLocked();
        }
    }

    private static async Task<SpaceRecord> NormalizeRecordAsync(SpaceRecord record)
    {
        var importedPath = !string.IsNullOrWhiteSpace(record.ImportedPath)
            ? NormalizePath(record.ImportedPath)
            : NormalizePath(record.RootPath);
        var probePath = Directory.Exists(importedPath)
            ? importedPath
            : NormalizePath(record.RootPath);
        var repoInfo = Directory.Exists(probePath)
            ? await GitCommandRunner.GetRepositoryInfoAsync(probePath).ConfigureAwait(false)
            : null;

        var normalized = Clone(record);
        normalized.ImportedPath = importedPath;
        normalized.Label = string.IsNullOrWhiteSpace(normalized.Label)
            ? DeriveDefaultLabel(importedPath)
            : normalized.Label.Trim();

        if (repoInfo is null)
        {
            normalized.Kind = SpaceKinds.Plain;
            normalized.RootPath = probePath;
            normalized.CommonRepoId = null;
            return normalized;
        }

        normalized.Kind = SpaceKinds.Git;
        normalized.RootPath = NormalizePath(repoInfo.RepoRoot);
        normalized.CommonRepoId = NormalizePath(repoInfo.CommonGitDir);
        normalized.Label = DeriveDefaultLabel(normalized.RootPath);
        return normalized;
    }

    private async Task<SpaceRecord?> GetNormalizedSpaceRecordAsync(string spaceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(spaceId))
        {
            return null;
        }

        SpaceRecord? snapshot;
        lock (_lock)
        {
            snapshot = _store.Spaces.FirstOrDefault(space => string.Equals(space.Id, spaceId, StringComparison.Ordinal));
            snapshot = snapshot is null ? null : Clone(snapshot);
        }

        if (snapshot is null)
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();
        var normalized = await NormalizeRecordAsync(snapshot).ConfigureAwait(false);
        normalized.Id = snapshot.Id;
        normalized.IsPinned = snapshot.IsPinned;
        normalized.CreatedAtUtc = snapshot.CreatedAtUtc;
        normalized.UpdatedAtUtc = snapshot.UpdatedAtUtc;
        normalized.Worktrees = MergeWorktreeMetadata(snapshot.Worktrees, normalized.Worktrees);

        lock (_lock)
        {
            var stored = _store.Spaces.FirstOrDefault(space => string.Equals(space.Id, spaceId, StringComparison.Ordinal));
            if (stored is null)
            {
                return normalized;
            }

            if (string.Equals(
                    JsonSerializer.Serialize(stored, SpaceJsonContext.Default.SpaceRecord),
                    JsonSerializer.Serialize(normalized, SpaceJsonContext.Default.SpaceRecord),
                    StringComparison.Ordinal))
            {
                return Clone(stored);
            }

            stored.Label = normalized.Label;
            stored.Kind = normalized.Kind;
            stored.RootPath = normalized.RootPath;
            stored.ImportedPath = normalized.ImportedPath;
            stored.CommonRepoId = normalized.CommonRepoId;
            stored.IsPinned = normalized.IsPinned;
            stored.Worktrees = MergeWorktreeMetadata(stored.Worktrees, normalized.Worktrees);
            stored.CreatedAtUtc = normalized.CreatedAtUtc;
            stored.UpdatedAtUtc = DateTime.UtcNow;
            PersistLocked();
            return Clone(stored);
        }
    }

    private static void MergeResolvedRecord(List<SpaceRecord> spaces, SpaceRecord candidate)
    {
        var match = spaces.FirstOrDefault(existing => SpaceIdentityComparer(existing, candidate));
        if (match is null)
        {
            if (string.IsNullOrWhiteSpace(candidate.Label))
            {
                candidate.Label = DeriveDefaultLabel(candidate.RootPath);
            }

            spaces.Add(candidate);
            return;
        }

        match.IsPinned |= candidate.IsPinned;
        match.RootPath = candidate.RootPath;
        match.ImportedPath = ChooseImportedPath(match.ImportedPath, candidate.ImportedPath);
        match.Kind = candidate.Kind;
        match.CommonRepoId = candidate.CommonRepoId;
        match.Label = DeriveDefaultLabel(candidate.RootPath);
        match.CreatedAtUtc = MinTimestamp(match.CreatedAtUtc, candidate.CreatedAtUtc);
        match.UpdatedAtUtc = MaxTimestamp(match.UpdatedAtUtc, candidate.UpdatedAtUtc);
        match.Worktrees = MergeWorktreeMetadata(match.Worktrees, candidate.Worktrees);
    }

    private static bool SpaceIdentityComparer(SpaceRecord left, SpaceRecord right)
    {
        if (string.Equals(left.Kind, SpaceKinds.Git, StringComparison.Ordinal) &&
            string.Equals(right.Kind, SpaceKinds.Git, StringComparison.Ordinal))
        {
            return string.Equals(left.CommonRepoId, right.CommonRepoId, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(left.RootPath, right.RootPath, StringComparison.OrdinalIgnoreCase);
    }

    private static List<SpaceWorktreeRecord> MergeWorktreeMetadata(
        List<SpaceWorktreeRecord>? left,
        List<SpaceWorktreeRecord>? right)
    {
        var merged = new Dictionary<string, SpaceWorktreeRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var worktree in left ?? [])
        {
            var normalizedPath = NormalizePath(worktree.Path);
            merged[normalizedPath] = new SpaceWorktreeRecord
            {
                Path = normalizedPath,
                Label = worktree.Label,
                UpdatedAtUtc = worktree.UpdatedAtUtc
            };
        }

        foreach (var worktree in right ?? [])
        {
            var normalizedPath = NormalizePath(worktree.Path);
            if (!merged.TryGetValue(normalizedPath, out var existing) ||
                worktree.UpdatedAtUtc >= existing.UpdatedAtUtc)
            {
                merged[normalizedPath] = new SpaceWorktreeRecord
                {
                    Path = normalizedPath,
                    Label = worktree.Label,
                    UpdatedAtUtc = worktree.UpdatedAtUtc
                };
            }
        }

        return merged.Values
            .OrderBy(worktree => worktree.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ChooseImportedPath(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return right;
        }

        if (string.IsNullOrWhiteSpace(right))
        {
            return left;
        }

        return left.Length <= right.Length ? left : right;
    }

    private static DateTime MinTimestamp(DateTime left, DateTime right)
    {
        if (left == default) return right;
        if (right == default) return left;
        return left <= right ? left : right;
    }

    private static DateTime MaxTimestamp(DateTime left, DateTime right)
    {
        return left >= right ? left : right;
    }

    private static string DeriveVisibleSpaceName(SpaceRecord record)
    {
        var path = string.IsNullOrWhiteSpace(record.RootPath) ? record.ImportedPath : record.RootPath;
        return DeriveDefaultLabel(path);
    }

    private static bool CanInitGit(SpaceRecord record)
    {
        return string.Equals(record.Kind, SpaceKinds.Plain, StringComparison.Ordinal) &&
               Directory.Exists(record.RootPath);
    }

    private static bool CanCreateWorktree(SpaceRecord record)
    {
        return string.Equals(record.Kind, SpaceKinds.Git, StringComparison.Ordinal) &&
               Directory.Exists(record.RootPath);
    }

    private SpaceRecord ImportPlainLocked(string normalizedPath, string? label)
    {
        var existing = _store.Spaces.FirstOrDefault(space =>
            string.Equals(space.RootPath, normalizedPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(space.Kind, SpaceKinds.Plain, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(label))
            {
                existing.Label = label.Trim();
            }

            existing.UpdatedAtUtc = DateTime.UtcNow;
            return Clone(existing);
        }

        var record = new SpaceRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Label = string.IsNullOrWhiteSpace(label) ? DeriveDefaultLabel(normalizedPath) : label.Trim(),
            Kind = SpaceKinds.Plain,
            RootPath = normalizedPath,
            ImportedPath = normalizedPath,
            IsPinned = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        _store.Spaces.Add(record);
        return Clone(record);
    }

    private SpaceRecord ImportGitLocked(string normalizedPath, string? label, GitCommandRunner.GitRepositoryInfo repoInfo)
    {
        var commonRepoId = NormalizePath(repoInfo.CommonGitDir);
        var existing = _store.Spaces.FirstOrDefault(space =>
            string.Equals(space.CommonRepoId, commonRepoId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(label))
            {
                existing.Label = label.Trim();
            }
            else if (string.IsNullOrWhiteSpace(existing.Label))
            {
                existing.Label = DeriveDefaultLabel(repoInfo.RepoRoot);
            }

            existing.RootPath = NormalizePath(repoInfo.RepoRoot);
            existing.ImportedPath = normalizedPath;
            existing.Kind = SpaceKinds.Git;
            existing.CommonRepoId = commonRepoId;
            existing.IsPinned = true;
            existing.UpdatedAtUtc = DateTime.UtcNow;
            return Clone(existing);
        }

        var record = new SpaceRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Label = string.IsNullOrWhiteSpace(label) ? DeriveDefaultLabel(repoInfo.RepoRoot) : label.Trim(),
            Kind = SpaceKinds.Git,
            RootPath = NormalizePath(repoInfo.RepoRoot),
            ImportedPath = normalizedPath,
            CommonRepoId = commonRepoId,
            IsPinned = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        _store.Spaces.Add(record);
        return Clone(record);
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_spacesPath))
            {
                _store = new SpaceStore();
                return;
            }

            try
            {
                var json = File.ReadAllText(_spacesPath);
                _store = JsonSerializer.Deserialize(json, SpaceJsonContext.Default.SpaceStore) ?? new SpaceStore();
            }
            catch (Exception ex)
            {
                Log.Warn(() => $"Failed to load spaces: {ex.Message}");
                _store = new SpaceStore();
            }
        }
    }

    private void PersistLocked()
    {
        var dir = Path.GetDirectoryName(_spacesPath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(_store, SpaceJsonContext.Default.SpaceStore);
        File.WriteAllText(_spacesPath, json);
    }

    private static SpaceRecord Clone(SpaceRecord record)
    {
        return new SpaceRecord
        {
            Id = record.Id,
            Label = record.Label,
            Kind = record.Kind,
            RootPath = record.RootPath,
            ImportedPath = record.ImportedPath,
            CommonRepoId = record.CommonRepoId,
            IsPinned = record.IsPinned,
            Worktrees = (record.Worktrees ?? [])
                .Select(worktree => new SpaceWorktreeRecord
                {
                    Path = worktree.Path,
                    Label = worktree.Label,
                    UpdatedAtUtc = worktree.UpdatedAtUtc
                })
                .ToList(),
            CreatedAtUtc = record.CreatedAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc
        };
    }

    private static string DeriveDefaultLabel(string path)
    {
        var trimmed = NormalizePath(path);
        return Path.GetFileName(trimmed) is { Length: > 0 } name ? name : trimmed;
    }

    private static string ResolveWorkspaceDisplayName(
        SpaceRecord record,
        string normalizedPath,
        string? customLabel)
    {
        var normalizedRoot = NormalizePath(record.RootPath);
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return "Main";
        }

        var trimmedLabel = customLabel?.Trim();
        return string.IsNullOrWhiteSpace(trimmedLabel)
            ? DeriveDefaultWorkspaceDisplayName(normalizedPath)
            : trimmedLabel;
    }

    private static string DeriveDefaultWorkspaceDisplayName(string path)
    {
        var trimmed = NormalizePath(path);
        return Path.GetFileName(trimmed) is { Length: > 0 } name ? name : trimmed;
    }

    private static void SetWorktreeLabelLocked(SpaceRecord record, string worktreePath, string? label)
    {
        record.Worktrees ??= [];
        var normalizedPath = NormalizePath(worktreePath);
        var entry = record.Worktrees.FirstOrDefault(worktree =>
            string.Equals(NormalizePath(worktree.Path), normalizedPath, StringComparison.OrdinalIgnoreCase));

        var trimmedLabel = label?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedLabel))
        {
            if (entry is not null)
            {
                record.Worktrees.Remove(entry);
            }

            return;
        }

        if (entry is null)
        {
            record.Worktrees.Add(new SpaceWorktreeRecord
            {
                Path = normalizedPath,
                Label = trimmedLabel,
                UpdatedAtUtc = DateTime.UtcNow
            });
            return;
        }

        entry.Path = normalizedPath;
        entry.Label = trimmedLabel;
        entry.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static void RemoveWorktreeLabelLocked(SpaceRecord record, string worktreePath)
    {
        record.Worktrees ??= [];
        var normalizedPath = NormalizePath(worktreePath);
        record.Worktrees.RemoveAll(worktree =>
            string.Equals(NormalizePath(worktree.Path), normalizedPath, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim());
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private sealed record SpaceWorkspaceBinding(string SpaceId, string WorkspacePath);

    private sealed record WorkspaceBindingIndex(
        Dictionary<string, List<SpaceWorkspaceBinding>> PathLookup,
        Dictionary<string, HashSet<string>> SpaceWorkspacePaths);
}
