using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models.Git;
using Ai.Tlbx.MidTerm.Services.Git;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class SessionUpdateStateServiceTests
{
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void SessionRegistry_GetSessionList_CanIncludeHiddenSessionsForUpdateStateCapture()
    {
        var registry = new SessionRegistry(isServiceMode: false);
        registry.SessionCache["visible"] = new SessionInfo
        {
            Id = "visible",
            ShellType = "Pwsh",
            CreatedAt = DateTime.UtcNow,
            IsRunning = true
        };
        registry.SessionCache["hidden"] = new SessionInfo
        {
            Id = "hidden",
            ShellType = "Pwsh",
            CreatedAt = DateTime.UtcNow,
            IsRunning = true
        };
        registry.MarkHidden("hidden");

        Assert.Equal(["visible"], registry.GetSessionList().Sessions.Select(static session => session.Id));
        Assert.Equal(
            ["hidden", "visible"],
            registry.GetSessionList(includeHidden: true).Sessions
                .Select(static session => session.Id)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TryBuildResumeCommand_PreservesCodexSafetyFlagsAroundResumeHint()
    {
        var command = SessionUpdateStateService.TryBuildResumeCommand(
            "resume a session with 'codex resume 984792347guid2342384798'",
            "codex --yolo --sandbox danger-full-access --approval-policy never app-server --listen ws://127.0.0.1",
            "codex",
            tryResumeNonAiAgentProcesses: false);

        Assert.Equal(
            "codex --yolo --sandbox danger-full-access --approval-policy never resume 984792347guid2342384798",
            command);
    }

    [Fact]
    public void TryBuildResumeCommand_UsesNewestResumeHintFromScrollback()
    {
        var command = SessionUpdateStateService.TryBuildResumeCommand(
            """
            old resume hint: codex resume old-thread
            later resume hint: codex resume new-thread
            """,
            "codex --model gpt-5.5-codex",
            "codex",
            tryResumeNonAiAgentProcesses: false);

        Assert.Equal("codex --model gpt-5.5-codex resume new-thread", command);
    }

    [Fact]
    public void TryBuildResumeCommand_PrefersClosureResumeHintAtTail()
    {
        var earlierNoise = string.Join(
            Environment.NewLine,
            Enumerable.Range(0, 200).Select(static index => string.Create(
                CultureInfo.InvariantCulture,
                $"old line {index}: codex resume stale-{index}")));
        var command = SessionUpdateStateService.TryBuildResumeCommand(
            $"""
            {earlierNoise}
            Token usage: total=3,869,408 input=3,663,094 (+ 153,286,400 cached) output=206,314 (reasoning 47,275)
            To continue this session, run codex resume 019e74e5-d492-7253-a21c-eacc7d24e10f
            """,
            "codex --model gpt-5.5-codex",
            "codex",
            tryResumeNonAiAgentProcesses: false);

        Assert.Equal(
            "codex --model gpt-5.5-codex resume 019e74e5-d492-7253-a21c-eacc7d24e10f",
            command);
    }

    [Fact]
    public void TryBuildResumeCommand_PreservesEqualsStyleFlags()
    {
        var command = SessionUpdateStateService.TryBuildResumeCommand(
            "resume with grok resume grok-thread-42",
            "grok --profile=unsafe --config Q:/repo/grok.toml --remote ws://localhost",
            "grok",
            tryResumeNonAiAgentProcesses: false);

        Assert.Equal("grok --profile=unsafe --config Q:/repo/grok.toml resume grok-thread-42", command);
    }

    [Fact]
    public void TryBuildResumeCommand_PreservesClaudeDangerousPermissionFlag()
    {
        var command = SessionUpdateStateService.TryBuildResumeCommand(
            "To continue, run: claude --resume abc-123",
            "claude --dangerously-skip-permissions",
            "claude",
            tryResumeNonAiAgentProcesses: false);

        Assert.Equal("claude --dangerously-skip-permissions --resume abc-123", command);
    }

    [Fact]
    public void TryBuildResumeCommand_DoesNotRelaunchNonAiProcessWithoutOptIn()
    {
        var command = SessionUpdateStateService.TryBuildResumeCommand(
            terminalText: "",
            foregroundCommandLine: "btop",
            foregroundName: "btop",
            tryResumeNonAiAgentProcesses: false);

        Assert.Null(command);
    }

    [Fact]
    public void TryBuildResumeCommand_RelaunchesNonAiProcessWhenOptedIn()
    {
        var command = SessionUpdateStateService.TryBuildResumeCommand(
            terminalText: "",
            foregroundCommandLine: "btop --utf-force",
            foregroundName: "btop",
            tryResumeNonAiAgentProcesses: true);

        Assert.Equal("btop --utf-force", command);
    }

    [Fact]
    public void TryBuildResumeCommand_DoesNotRelaunchShellWhenOptedIn()
    {
        var command = SessionUpdateStateService.TryBuildResumeCommand(
            terminalText: "",
            foregroundCommandLine: "pwsh",
            foregroundName: "pwsh",
            tryResumeNonAiAgentProcesses: true);

        Assert.Null(command);
    }

    [Fact]
    public async Task RestoreAsync_AppliesDecorationStateAndConsumesStateFile()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), "midterm-update-state-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDir);
        var statePath = Path.Combine(stateDir, "state.json");

        await using var manager = new TtyHostSessionManager(expectedVersion: "1.0.0", minCompatibleVersion: "1.0.0");
        using var gitWatcher = new GitWatcherService();
        AddCachedSession(manager, "s1");
        await File.WriteAllTextAsync(
            statePath,
            JsonSerializer.Serialize(new SessionUpdateState
            {
                Kind = "web",
                SavedAt = DateTimeOffset.UtcNow,
                Sessions =
                [
                    new SessionDecorationState
                    {
                        SessionId = "s1",
                        TerminalTitle = "Codex worker",
                        Topic = "update state",
                        Notes = "restore decoration"
                    }
                ]
            }, StateJsonOptions));

        await new SessionUpdateStateService(stateDir).RestoreAsync(manager, gitWatcher);

        var dto = manager.GetSessionList().Sessions.Single(session => session.Id == "s1");
        Assert.Equal("Codex worker", dto.TerminalTitle);
        Assert.Equal("update state", dto.Topic);
        Assert.Equal("restore decoration", dto.Notes);
        Assert.False(File.Exists(statePath));
    }

    [Fact]
    public void BuildFullUpdateRestoreDecorations_IncludesSavedSessionsWithoutResumeIntent()
    {
        var state = new SessionUpdateState
        {
            Kind = "full",
            SavedAt = DateTimeOffset.UtcNow,
            Sessions =
            [
                new SessionDecorationState
                {
                    SessionId = "shell-only",
                    ShellType = "Pwsh",
                    Cols = 161,
                    Rows = 42,
                    CurrentDirectory = "Q:\\repos\\MidTerm",
                    Topic = "must come back"
                }
            ]
        };

        var decorations = SessionUpdateStateService.BuildFullUpdateRestoreDecorations(
            state,
            new Dictionary<string, SessionResumeIntent>(StringComparer.Ordinal));

        var decoration = Assert.Single(decorations);
        Assert.Equal("shell-only", decoration.SessionId);
        Assert.Equal("Pwsh", decoration.ShellType);
        Assert.Equal(161, decoration.Cols);
        Assert.Equal(42, decoration.Rows);
        Assert.Equal("Q:\\repos\\MidTerm", decoration.CurrentDirectory);
        Assert.Equal("must come back", decoration.Topic);
    }

    [Fact]
    public void BuildFullUpdateRestoreDecorations_KeepsPendingIntentWithoutDecoration()
    {
        var pending = new SessionResumeIntent
        {
            OriginalSessionId = "pending-only",
            ShellType = "Pwsh",
            WorkingDirectory = "Q:\\repos\\Jpa",
            Cols = 120,
            Rows = 30,
            Command = "codex resume thread-123"
        };

        var decorations = SessionUpdateStateService.BuildFullUpdateRestoreDecorations(
            new SessionUpdateState { Kind = "full", SavedAt = DateTimeOffset.UtcNow },
            new Dictionary<string, SessionResumeIntent>(StringComparer.Ordinal)
            {
                ["pending-only"] = pending
            });

        var decoration = Assert.Single(decorations);
        Assert.Equal("pending-only", decoration.SessionId);
        Assert.Equal("Pwsh", decoration.ShellType);
        Assert.Equal("Q:\\repos\\Jpa", decoration.CurrentDirectory);
    }

    [Fact]
    public void MergeExtraGitRepos_KeepsPersistedRepoWhenLiveWatcherMissesIt()
    {
        var repos = SessionUpdateStateService.MergeExtraGitRepos(
            liveBindings:
            [
                new GitRepoBinding
                {
                    RepoRoot = @"Q:\repos\Jpa",
                    Label = "Jpa",
                    Role = "cwd",
                    Source = "auto",
                    IsPrimary = true
                }
            ],
            persistedRepos:
            [
                new TtyHostGitRepoMetadata
                {
                    RepoRoot = @"Q:\repos\MidTerm\",
                    Label = "MidTerm",
                    Role = "target",
                    Source = "manual"
                }
            ]);

        var repo = Assert.Single(repos);
        Assert.Equal(@"Q:\repos\MidTerm", repo.RepoRoot);
        Assert.Equal("MidTerm", repo.Label);
        Assert.Equal("target", repo.Role);
        Assert.Equal("manual", repo.Source);
    }

    [Fact]
    public void MergeExtraGitRepos_PrefersLiveBindingForDuplicateExtraRepo()
    {
        var repos = SessionUpdateStateService.MergeExtraGitRepos(
            liveBindings:
            [
                new GitRepoBinding
                {
                    RepoRoot = @"Q:\repos\MidTerm",
                    Label = "Live MidTerm",
                    Role = "target",
                    Source = "manual",
                    IsPrimary = false
                }
            ],
            persistedRepos:
            [
                new TtyHostGitRepoMetadata
                {
                    RepoRoot = @"Q:\repos\MidTerm\",
                    Label = "Old MidTerm",
                    Role = "old",
                    Source = "host"
                }
            ]);

        var repo = Assert.Single(repos);
        Assert.Equal(@"Q:\repos\MidTerm", repo.RepoRoot);
        Assert.Equal("Live MidTerm", repo.Label);
        Assert.Equal("target", repo.Role);
        Assert.Equal("manual", repo.Source);
    }

    private static void AddCachedSession(TtyHostSessionManager manager, string sessionId)
    {
        var cache = typeof(TtyHostSessionManager)
            .GetField("_sessionCache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(manager) as ConcurrentDictionary<string, SessionInfo>;
        Assert.NotNull(cache);
        cache[sessionId] = new SessionInfo
        {
            Id = sessionId,
            ShellType = "Pwsh",
            CreatedAt = DateTime.UtcNow,
            IsRunning = true
        };
    }
}
