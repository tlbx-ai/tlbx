using Ai.Tlbx.MidTerm.Models.Git;
using Ai.Tlbx.MidTerm.Settings;
using Ai.Tlbx.MidTerm.Startup;

using Ai.Tlbx.MidTerm.Services.Sessions;
namespace Ai.Tlbx.MidTerm.Services.Git;

public sealed class GitDebugResponse
{
    public string? MidTermVersion { get; set; }
    public string? RequestedSessionId { get; set; }
    public bool SessionFound { get; set; }
    public string? CurrentDirectory { get; set; }
    public string? GitVersion { get; set; }
    public string? RepoRootFromCwd { get; set; }
    public string? CachedRepoRoot { get; set; }
    public bool HasCachedStatus { get; set; }
    public string? CachedBranch { get; set; }
    public GitDebugSessionInfo[] Sessions { get; set; } = [];
    public GitCommandLog? LastGitCommand { get; set; }
}

public sealed class GitCommandLog
{
    public string Args { get; set; } = "";
    public string WorkingDir { get; set; } = "";
    public int ExitCode { get; set; }
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
    public string Timestamp { get; set; } = "";
}

public sealed class GitDebugSessionInfo
{
    public string Id { get; set; } = "";
    public string? CurrentDirectory { get; set; }
    public string? RegisteredRepo { get; set; }
    public GitRepoBinding[] RegisteredRepos { get; set; } = [];
    public string? RepoRootProbe { get; set; }
    public string? ProbeError { get; set; }
}

public static class GitEndpoints
{
    public static void MapGitEndpoints(WebApplication app, GitWatcherService gitWatcher, TtyHostSessionManager sessionManager)
    {
        app.MapGet("/api/git/debug", async (string? sessionId) =>
        {
            var session = string.IsNullOrEmpty(sessionId) ? null : sessionManager.GetSession(sessionId);
            var cwd = session?.CurrentDirectory;
            string? repoRoot = null;

            var gitVersionOutput = await GitCommandRunner.GetGitVersionAsync();

            if (!string.IsNullOrEmpty(cwd))
            {
                repoRoot = await GitCommandRunner.GetRepoRootAsync(cwd);
            }

            var cachedRepoRoot = string.IsNullOrEmpty(sessionId) ? null : gitWatcher.GetRepoRoot(sessionId!);
            var cachedStatus = string.IsNullOrEmpty(sessionId) ? null : gitWatcher.GetCachedStatus(sessionId!);

            var debug = new GitDebugResponse
            {
                MidTermVersion = CliCommands.GetVersion(),
                RequestedSessionId = sessionId,
                SessionFound = session is not null,
                CurrentDirectory = cwd,
                GitVersion = gitVersionOutput,
                RepoRootFromCwd = repoRoot,
                CachedRepoRoot = cachedRepoRoot,
                HasCachedStatus = cachedStatus is not null,
                CachedBranch = cachedStatus?.Branch,
                Sessions = await Task.WhenAll(sessionManager.GetAllSessions().Select(async s =>
                {
                    string? probeRoot = null;
                    string? probeError = null;
                    if (!string.IsNullOrEmpty(s.CurrentDirectory))
                    {
                        try
                        {
                            probeRoot = await GitCommandRunner.GetRepoRootAsync(s.CurrentDirectory);
                        }
                        catch (Exception ex)
                        {
                            probeError = ex.Message;
                        }
                    }
                    return new GitDebugSessionInfo
                    {
                        Id = s.Id,
                        CurrentDirectory = s.CurrentDirectory,
                RegisteredRepo = gitWatcher.GetRepoRoot(s.Id),
                RegisteredRepos = gitWatcher.GetRepoBindings(s.Id),
                        RepoRootProbe = probeRoot,
                        ProbeError = probeError
                    };
                })),
                LastGitCommand = GitCommandRunner.GetLastCommandLog()
            };

            return Results.Json(debug, GitJsonContext.Default.GitDebugResponse);
        });

        app.MapGet("/api/git/repos", async (string? sessionId) =>
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return Results.BadRequest("sessionId required");
            }

            var session = sessionManager.GetSession(sessionId);
            if (session is not null && !string.IsNullOrEmpty(session.CurrentDirectory) && gitWatcher.GetRepoRoot(sessionId) is null)
            {
                await gitWatcher.RegisterSessionAsync(sessionId, session.CurrentDirectory);
            }

            return Results.Json(
                new GitRepoListResponse { Repos = gitWatcher.GetRepoBindings(sessionId) },
                GitJsonContext.Default.GitRepoListResponse);
        });

        app.MapPost("/api/git/repos", async (GitRepoBindRequest request) =>
        {
            if (string.IsNullOrEmpty(request.SessionId) || string.IsNullOrEmpty(request.Path))
            {
                return Results.BadRequest("sessionId and path required");
            }

            var repos = await gitWatcher.AddSessionRepoAsync(
                request.SessionId,
                request.Path,
                request.Label,
                request.Role,
                "manual");

            if (repos is null)
            {
                return Results.BadRequest("Path is not in a git repository");
            }

            sessionManager.SetSessionExtraGitReposMetadata(request.SessionId, repos);
            return Results.Json(new GitRepoListResponse { Repos = repos }, GitJsonContext.Default.GitRepoListResponse);
        });

        app.MapDelete("/api/git/repos", (string? sessionId, string? repoRoot) =>
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(repoRoot))
            {
                return Results.BadRequest("sessionId and repoRoot required");
            }

            gitWatcher.RemoveSessionRepo(sessionId, repoRoot);
            sessionManager.SetSessionExtraGitReposMetadata(sessionId, gitWatcher.GetRepoBindings(sessionId));
            return Results.Json(
                new GitRepoListResponse { Repos = gitWatcher.GetRepoBindings(sessionId) },
                GitJsonContext.Default.GitRepoListResponse);
        });

        app.MapPost("/api/git/repos/refresh", async (GitRepoRefreshRequest request) =>
        {
            if (string.IsNullOrEmpty(request.SessionId))
            {
                return Results.BadRequest("sessionId required");
            }

            var repoRoot = gitWatcher.ResolveRepoRoot(request.SessionId, request.RepoRoot);
            if (repoRoot is not null)
            {
                await gitWatcher.RefreshStatusAsync(repoRoot);
            }
            else
            {
                foreach (var repo in gitWatcher.GetRepoBindings(request.SessionId))
                {
                    await gitWatcher.RefreshStatusAsync(repo.RepoRoot);
                }
            }

            return Results.Json(
                new GitRepoListResponse { Repos = gitWatcher.GetRepoBindings(request.SessionId) },
                GitJsonContext.Default.GitRepoListResponse);
        });

        app.MapGet("/api/git/status", async (string? sessionId, string? repoRoot) =>
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return Results.BadRequest("sessionId required");
            }

            var session = sessionManager.GetSession(sessionId);
            if (session is null)
            {
                return Results.NotFound("Session not found");
            }

            var resolvedRepoRoot = gitWatcher.ResolveRepoRoot(sessionId, repoRoot);
            if (resolvedRepoRoot is null)
            {
                var workingDir = session.CurrentDirectory;
                if (string.IsNullOrEmpty(workingDir))
                {
                    return Results.Json(new GitStatusResponse(), GitJsonContext.Default.GitStatusResponse);
                }

                await gitWatcher.RegisterSessionAsync(sessionId, workingDir);
                resolvedRepoRoot = gitWatcher.ResolveRepoRoot(sessionId, repoRoot);
            }

            if (resolvedRepoRoot is null)
            {
                return Results.Json(new GitStatusResponse(), GitJsonContext.Default.GitStatusResponse);
            }

            var cached = gitWatcher.GetCachedStatus(sessionId, resolvedRepoRoot);
            if (cached is not null)
            {
                return Results.Json(cached, GitJsonContext.Default.GitStatusResponse);
            }

            await gitWatcher.RefreshStatusAsync(resolvedRepoRoot);
            var status = gitWatcher.GetCachedStatus(sessionId, resolvedRepoRoot) ?? new GitStatusResponse { RepoRoot = resolvedRepoRoot };
            return Results.Json(status, GitJsonContext.Default.GitStatusResponse);
        });

        app.MapGet("/api/git/diff", async (string? sessionId, string? repoRoot, string? path, bool? staged) =>
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(path))
            {
                return Results.BadRequest("sessionId and path required");
            }

            var (resolvedRepoRoot, error) = ResolveRepo(sessionId, repoRoot, gitWatcher, sessionManager);
            if (error is not null) return error;

            var diff = await GitCommandRunner.GetDiffAsync(resolvedRepoRoot!, path, staged ?? false);
            return Results.Text(diff, "text/plain");
        });

        app.MapGet("/api/git/diff-view", async (string? sessionId, string? repoRoot, string? path, string? scope) =>
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(path))
            {
                return Results.BadRequest("sessionId and path required");
            }

            var normalizedScope = string.Equals(scope, "staged", StringComparison.OrdinalIgnoreCase)
                ? "staged"
                : "worktree";

            var (resolvedRepoRoot, error) = ResolveRepo(sessionId, repoRoot, gitWatcher, sessionManager);
            if (error is not null) return error;

            var (patch, isTruncated) = await GitCommandRunner.GetDiffPatchAsync(
                resolvedRepoRoot!,
                path,
                normalizedScope == "staged");

            var response = GitPatchParser.ParseDiff(normalizedScope, patch, isTruncated);
            return Results.Json(response, GitJsonContext.Default.GitDiffViewResponse);
        });

        app.MapGet("/api/git/log", async (string? sessionId, string? repoRoot, int? count) =>
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return Results.BadRequest("sessionId required");
            }

            var (resolvedRepoRoot, error) = ResolveRepo(sessionId, repoRoot, gitWatcher, sessionManager);
            if (error is not null) return error;

            var entries = await GitCommandRunner.GetLogAsync(resolvedRepoRoot!, count ?? 20);
            return Results.Json(entries, GitJsonContext.Default.GitLogEntryArray);
        });

        app.MapGet("/api/git/commit", async (string? sessionId, string? repoRoot, string? hash) =>
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(hash))
            {
                return Results.BadRequest("sessionId and hash required");
            }

            var (resolvedRepoRoot, error) = ResolveRepo(sessionId, repoRoot, gitWatcher, sessionManager);
            if (error is not null) return error;

            var metadata = await GitCommandRunner.GetCommitMetadataAsync(resolvedRepoRoot!, hash);
            if (metadata is null)
            {
                return Results.NotFound("Commit not found");
            }

            var (patch, isTruncated) = await GitCommandRunner.GetCommitPatchAsync(resolvedRepoRoot!, hash);
            var response = GitPatchParser.ParseCommitDetails(metadata, patch, isTruncated);
            return Results.Json(response, GitJsonContext.Default.GitCommitDetailsResponse);
        });
    }

    private static (string? RepoRoot, IResult? Error) ResolveRepo(
        string? sessionId,
        string? repoRoot,
        GitWatcherService gitWatcher,
        TtyHostSessionManager sessionManager)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return (null, Results.BadRequest("sessionId required"));
        }

        var session = sessionManager.GetSession(sessionId);
        if (session is null)
        {
            return (null, Results.NotFound("Session not found"));
        }

        var resolvedRepoRoot = gitWatcher.ResolveRepoRoot(sessionId, repoRoot);
        if (resolvedRepoRoot is null)
        {
            return (null, Results.BadRequest("Session not in a git repository"));
        }

        return (resolvedRepoRoot, null);
    }
}
