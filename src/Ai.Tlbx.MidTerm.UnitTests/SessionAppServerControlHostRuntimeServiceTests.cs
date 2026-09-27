using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Services.Hosting;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Settings;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

[Collection(PathSensitiveEnvironmentCollection.Name)]
public sealed class SessionAppServerControlHostRuntimeServiceTests
{
    private static SettingsService CreateSettingsService() => new();
    private static SettingsService CreateSettingsService(string directory) => new(directory);

    [Fact]
    public void ResolveConfiguredHostPath_UsesExplicitShadowBuildAndRejectsMissingPath()
    {
        var previous = Environment.GetEnvironmentVariable(
            SessionAppServerControlHostRuntimeService.AgentHostPathEnvironmentVariable);
        var root = Path.Combine(Path.GetTempPath(), "midterm-agenthost-override-tests", Guid.NewGuid().ToString("N"));
        var hostPath = Path.Combine(root, "mtagenthost.dll");
        Directory.CreateDirectory(root);
        File.WriteAllText(hostPath, "shadow host");

        try
        {
            Environment.SetEnvironmentVariable(
                SessionAppServerControlHostRuntimeService.AgentHostPathEnvironmentVariable,
                hostPath);
            Assert.Equal(
                Path.GetFullPath(hostPath),
                SessionAppServerControlHostRuntimeService.ResolveConfiguredHostPath());

            Environment.SetEnvironmentVariable(
                SessionAppServerControlHostRuntimeService.AgentHostPathEnvironmentVariable,
                Path.Combine(root, "missing.dll"));
            Assert.Throws<FileNotFoundException>(
                SessionAppServerControlHostRuntimeService.ResolveConfiguredHostPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                SessionAppServerControlHostRuntimeService.AgentHostPathEnvironmentVariable,
                previous);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SessionAppServerControlRuntimeService_CanDelegateToMtAgentHostSyntheticMode()
    {
        await using var hostRuntime = new SessionAppServerControlHostRuntimeService(CreateSettingsService(), mode: "synthetic");
        await using var sessionManager = new TtyHostSessionManager();
        var profileService = new AiCliProfileService();
        await using var runtime = new SessionAppServerControlRuntimeService(sessionManager, profileService, hostRuntime);

        var session = new SessionInfoDto
        {
            Id = "session-runtime-1",
            CurrentDirectory = AppContext.BaseDirectory,
            ForegroundName = "codex"
        };

        var attached = await runtime.EnsureAttachedAsync(session.Id, session);
        Assert.True(attached);
        Assert.True(runtime.IsAttached(session.Id));

        var turn = await runtime.StartTurnAsync(
            session.Id,
            new AppServerControlTurnRequest
            {
                Text = "Inspect the workspace.",
                Attachments = []
            });

        Assert.Equal("codex", turn.Provider);
        Assert.Equal("accepted", turn.Status);

        var resolved = await runtime.ResolveRequestAsync(
            session.Id,
            "req-approval-1",
            new AppServerControlRequestDecisionRequest
            {
                Decision = "accept"
            });

        Assert.Equal("accepted", resolved.Status);

        var snapshot = await WaitForSnapshotAsync(
            runtime,
            session.Id,
            current => current.Requests.Any(request => request.Kind == "approval" && request.Decision == "accept"));
        Assert.NotNull(snapshot);
        Assert.Equal("codex", snapshot!.Provider);
        Assert.Equal("Synthetic codex reply for: Inspect the workspace.", snapshot.Streams.AssistantText);
        Assert.Contains(snapshot.Requests, request => request.Kind == "approval" && request.Decision == "accept");

        Assert.True(runtime.TryGetRuntimeSummary(session.Id, out var runtimeSnapshot));
        Assert.Equal("mtagenthost-ipc", runtimeSnapshot.TransportKey);
        Assert.Equal("running", runtimeSnapshot.Status);
        Assert.Equal("Synthetic codex reply for: Inspect the workspace.", runtimeSnapshot.AssistantText);
    }

    [Fact]
    public async Task SessionAppServerControlRuntimeService_CanDelegateToMtAgentHostCodexMode()
    {
        using var fakeCodex = FakeCodexPathScope.Create();
        await using var hostRuntime = new SessionAppServerControlHostRuntimeService(CreateSettingsService(), mode: "codex");
        await using var sessionManager = new TtyHostSessionManager();
        var profileService = new AiCliProfileService();
        await using var runtime = new SessionAppServerControlRuntimeService(sessionManager, profileService, hostRuntime);

        var imagePath = Path.Combine(fakeCodex.Root, "sample.png");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3, 4]);

        var session = new SessionInfoDto
        {
            Id = "session-runtime-codex-1",
            CurrentDirectory = fakeCodex.Root,
            ForegroundName = "codex"
        };

        var attached = await runtime.EnsureAttachedAsync(session.Id, session);
        Assert.True(attached);
        Assert.True(runtime.IsAttached(session.Id));

        var turn = await runtime.StartTurnAsync(
            session.Id,
            new AppServerControlTurnRequest
            {
                Text = "Inspect attachments and ask approval.",
                Attachments =
                [
                    new AppServerControlAttachmentReference
                    {
                        Kind = "image",
                        Path = imagePath,
                        MimeType = "image/png"
                    }
                ]
            });

        Assert.Equal("codex", turn.Provider);
        Assert.Equal("accepted", turn.Status);

        var requestSnapshot = await WaitForSnapshotAsync(
            runtime,
            session.Id,
            current => current.Requests.Any(request => request.Kind == "command_execution_approval" && request.State == "open"));
        Assert.NotNull(requestSnapshot);
        var pendingRequest = Assert.Single(requestSnapshot!.Requests, request => request.Kind == "command_execution_approval" && request.State == "open");

        var resolved = await runtime.ResolveRequestAsync(
            session.Id,
            pendingRequest.RequestId,
            new AppServerControlRequestDecisionRequest
            {
                Decision = "accept"
            });

        Assert.Equal("accepted", resolved.Status);

        var snapshot = await WaitForSnapshotAsync(
            runtime,
            session.Id,
            current => current.CurrentTurn.State == "completed" &&
                       current.Requests.Any(request => request.Kind == "command_execution_approval" && request.Decision == "accept"));
        Assert.NotNull(snapshot);
        Assert.Contains("images=1", snapshot!.Streams.AssistantText, StringComparison.Ordinal);
        Assert.Contains(snapshot.Requests, request => request.Kind == "command_execution_approval" && request.Decision == "accept");
        Assert.Contains(
            snapshot.Items,
            item => item.ItemType == "user_message" &&
                    item.Attachments.Count == 1 &&
                    string.Equals(item.Attachments[0].DisplayName, "sample.png", StringComparison.Ordinal));

        Assert.True(runtime.TryGetRuntimeSummary(session.Id, out var runtimeSnapshot));
        Assert.Equal("mtagenthost-ipc", runtimeSnapshot.TransportKey);
        Assert.Equal("ready", runtimeSnapshot.Status);
        Assert.Contains("images=1", runtimeSnapshot.AssistantText ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionAppServerControlRuntimeService_KeepsExistingCodexAppServerControlAttachAfterTerminalReturnsToShell()
    {
        using var fakeCodex = FakeCodexPathScope.Create();
        await using var hostRuntime = new SessionAppServerControlHostRuntimeService(CreateSettingsService(), mode: "codex");
        await using var sessionManager = new TtyHostSessionManager();
        var profileService = new AiCliProfileService();
        await using var runtime = new SessionAppServerControlRuntimeService(sessionManager, profileService, hostRuntime);

        var sessionId = "session-runtime-codex-shell-return-1";
        var codexSession = new SessionInfoDto
        {
            Id = sessionId,
            CurrentDirectory = fakeCodex.Root,
            ForegroundName = "codex"
        };

        var attached = await runtime.EnsureAttachedAsync(sessionId, codexSession);
        Assert.True(attached);
        Assert.True(runtime.IsAttached(sessionId));

        var shellSession = new SessionInfoDto
        {
            Id = sessionId,
            CurrentDirectory = fakeCodex.Root,
            ForegroundName = "shell",
            ForegroundProcessIdentity = "shell"
        };

        var reusedAttach = await runtime.EnsureAttachedAsync(sessionId, shellSession);
        Assert.True(reusedAttach);

        var turn = await runtime.StartTurnAsync(
            sessionId,
            new AppServerControlTurnRequest
            {
                Text = "Confirm the reused attach still accepts turns.",
                Attachments = []
            });

        Assert.Equal("accepted", turn.Status);

        var snapshot = await WaitForSnapshotAsync(
            runtime,
            sessionId,
            current => string.Equals(current.CurrentTurn.State, "completed", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(snapshot);
        Assert.Contains("reused attach", snapshot!.Streams.AssistantText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SessionAppServerControlRuntimeService_CanResolveMtAgentHostCodexUserInput()
    {
        using var fakeCodex = FakeCodexPathScope.Create();
        await using var hostRuntime = new SessionAppServerControlHostRuntimeService(CreateSettingsService(), mode: "codex");
        await using var sessionManager = new TtyHostSessionManager();
        var profileService = new AiCliProfileService();
        await using var runtime = new SessionAppServerControlRuntimeService(sessionManager, profileService, hostRuntime);

        var session = new SessionInfoDto
        {
            Id = "session-runtime-codex-user-1",
            CurrentDirectory = fakeCodex.Root,
            ForegroundName = "codex"
        };

        var attached = await runtime.EnsureAttachedAsync(session.Id, session);
        Assert.True(attached);
        _ = await runtime.StartTurnAsync(
            session.Id,
            new AppServerControlTurnRequest
            {
                Text = "Inspect the repo and ask user for the mode.",
                Attachments = []
            });

        var requestSnapshot = await WaitForSnapshotAsync(
            runtime,
            session.Id,
            current => current.Requests.Any(request => request.Kind == "interview" && request.State == "open"));
        Assert.NotNull(requestSnapshot);
        var pendingRequest = Assert.Single(requestSnapshot!.Requests, request => request.Kind == "interview" && request.State == "open");

        var resolved = await runtime.ResolveUserInputAsync(
            session.Id,
            pendingRequest.RequestId,
            new AppServerControlUserInputAnswerRequest
            {
                Answers =
                [
                    new AppServerControlAnsweredQuestion
                    {
                        QuestionId = "choice",
                        Answers = ["Safe"]
                    }
                ]
            });

        Assert.Equal("accepted", resolved.Status);

        var snapshot = await WaitForSnapshotAsync(
            runtime,
            session.Id,
            current => current.CurrentTurn.State == "completed" &&
                       current.Requests.Any(request => request.Kind == "interview" &&
                                                       request.Answers.Any(answer => answer.QuestionId == "choice" &&
                                                                                    answer.Answers.Contains("Safe", StringComparer.Ordinal))));
        Assert.NotNull(snapshot);
        Assert.Contains(
            snapshot!.Requests,
            request => request.Kind == "interview" &&
                       request.Answers.Any(answer => answer.QuestionId == "choice" &&
                                                    answer.Answers.Contains("Safe", StringComparer.Ordinal)));
    }

    [Fact]
    public async Task SessionAppServerControlRuntimeService_CanInterruptMtAgentHostCodexTurn()
    {
        using var fakeCodex = FakeCodexPathScope.Create();
        await using var hostRuntime = new SessionAppServerControlHostRuntimeService(CreateSettingsService(), mode: "codex");
        await using var sessionManager = new TtyHostSessionManager();
        var profileService = new AiCliProfileService();
        await using var runtime = new SessionAppServerControlRuntimeService(sessionManager, profileService, hostRuntime);

        var session = new SessionInfoDto
        {
            Id = "session-runtime-codex-interrupt-1",
            CurrentDirectory = fakeCodex.Root,
            ForegroundName = "codex"
        };

        var attached = await runtime.EnsureAttachedAsync(session.Id, session);
        Assert.True(attached);
        _ = await runtime.StartTurnAsync(
            session.Id,
            new AppServerControlTurnRequest
            {
                Text = "Run a turn that will be interrupted.",
                Attachments = []
            });

        var startedSnapshot = await WaitForSnapshotAsync(
            runtime,
            session.Id,
            current => !string.IsNullOrWhiteSpace(current.CurrentTurn.TurnId) &&
                       string.Equals(current.CurrentTurn.State, "running", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(startedSnapshot);

        var interrupted = await runtime.InterruptTurnAsync(
            session.Id,
            new AppServerControlInterruptRequest
            {
                TurnId = startedSnapshot!.CurrentTurn.TurnId
            });

        Assert.Equal("accepted", interrupted.Status);

        var finalSnapshot = await WaitForSnapshotAsync(
            runtime,
            session.Id,
            current => string.Equals(current.CurrentTurn.State, "interrupted", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(finalSnapshot);
        Assert.Equal("Interrupted", finalSnapshot!.CurrentTurn.StateLabel);
    }

    [Fact]
    public async Task SessionAppServerControlRuntimeService_CanAttachMtAgentHostCodexWithoutServicePath()
    {
        using var fakeCodex = FakeCodexPathScope.Create();
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", string.Empty);
        try
        {
            await using var hostRuntime = new SessionAppServerControlHostRuntimeService(CreateSettingsService(), mode: "codex");
            await using var sessionManager = new TtyHostSessionManager();
            var profileService = new AiCliProfileService();
            await using var runtime = new SessionAppServerControlRuntimeService(sessionManager, profileService, hostRuntime);

            var session = new SessionInfoDto
            {
                Id = "session-runtime-codex-no-path-1",
                CurrentDirectory = fakeCodex.Root,
                ForegroundName = "codex",
                ForegroundCommandLine = $"\"{fakeCodex.ExecutablePath}\" --yolo"
            };

            var attached = await runtime.EnsureAttachedAsync(session.Id, session);

            Assert.True(attached);
            Assert.True(runtime.IsAttached(session.Id));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public async Task SessionAppServerControlRuntimeService_CanAttachToExistingCodexWebSocketWithoutSpawningCodex()
    {
        await using var fakeServer = FakeCodexWebSocketServer.Start(
            loadedThreadId: "thread-remote-1",
            assistantReply: "Remote Codex shared-runtime reply.");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", string.Empty);
        try
        {
            await using var hostRuntime = new SessionAppServerControlHostRuntimeService(CreateSettingsService(), mode: "codex");
            await using var sessionManager = new TtyHostSessionManager();
            var profileService = new AiCliProfileService();
            await using var runtime = new SessionAppServerControlRuntimeService(sessionManager, profileService, hostRuntime);

            var session = new SessionInfoDto
            {
                Id = "session-runtime-codex-remote-1",
                CurrentDirectory = AppContext.BaseDirectory,
                ForegroundName = "codex",
                ForegroundCommandLine = $"codex --remote {fakeServer.Endpoint} resume thread-remote-1",
                AgentAttachPoint = new SessionAgentAttachPoint
                {
                    Provider = SessionAgentAttachPoint.CodexProvider,
                    TransportKind = SessionAgentAttachPoint.CodexAppServerWebSocketTransport,
                    Endpoint = fakeServer.Endpoint,
                    SharedRuntime = true,
                    Source = "test",
                    PreferredThreadId = "thread-remote-1"
                }
            };

            var attached = await runtime.EnsureAttachedAsync(session.Id, session);

            Assert.True(attached);
            Assert.True(runtime.IsAttached(session.Id));

            var turn = await runtime.StartTurnAsync(
                session.Id,
                new AppServerControlTurnRequest
                {
                    Text = "Continue from the existing thread.",
                    Attachments = []
                });

            Assert.Equal("accepted", turn.Status);
            Assert.Equal("thread-remote-1", turn.ThreadId);

            var snapshot = await WaitForSnapshotAsync(
                runtime,
                session.Id,
                current => string.Equals(current.CurrentTurn.State, "completed", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(snapshot);
            Assert.Equal("Remote Codex shared-runtime reply.", snapshot!.Streams.AssistantText);

            Assert.True(runtime.TryGetRuntimeSummary(session.Id, out var runtimeSnapshot));
            Assert.Equal(SessionAgentAttachPoint.CodexAppServerWebSocketTransport, runtimeSnapshot.TransportKey);
            Assert.Equal("Codex app-server websocket", runtimeSnapshot.TransportLabel);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public async Task SessionAppServerControlRuntimeService_DoesNotFallbackWhenMtAgentHostColdAttachThrows()
    {
        using var fakeCodex = FakeCodexPathScope.Create();
        await using var hostRuntime = new SessionAppServerControlHostRuntimeService(
            CreateSettingsService(),
            instanceIdentity: null,
            mode: "codex",
            launcher: static (
                string _fileName,
                IReadOnlyList<string> _args,
                string _workingDirectory,
                IReadOnlyDictionary<string, string?>? _environmentOverrides,
                IReadOnlyList<string>? _pathPrependEntries,
                string? _runAsUser,
                string? _runAsUserSid,
                out TtyHostSpawner.RedirectedProcessHandle? launchedProcess,
                out string? failure) =>
            {
                launchedProcess = null;
                failure = null;
                throw new InvalidOperationException("Injected mtagenthost cold-start failure.");
            });
        await using var sessionManager = new TtyHostSessionManager();
        var profileService = new AiCliProfileService();
        await using var runtime = new SessionAppServerControlRuntimeService(sessionManager, profileService, hostRuntime);

        var session = new SessionInfoDto
        {
            Id = "session-runtime-codex-host-attach-failure-1",
            CurrentDirectory = fakeCodex.Root,
            ForegroundName = "codex"
        };

        var attached = await runtime.EnsureAttachedAsync(session.Id, session);

        Assert.False(attached);
        Assert.False(runtime.IsAttached(session.Id));
        Assert.False(runtime.TryGetRuntimeSummary(session.Id, out _));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.StartTurnAsync(
                session.Id,
                new AppServerControlTurnRequest
                {
                    Text = "Host attach failed, so App Server Controller must not invent a second Codex runtime.",
                    Attachments = []
                }));

        Assert.Equal("App Server Controller runtime is not attached.", error.Message);
    }

    [Fact]
    public async Task SessionAppServerControlHostRuntimeService_TerminateAllOwnedHostsAsync_KillsRecoveredHosts()
    {
        var settingsRoot = Path.Combine(Path.GetTempPath(), "midterm-appServerControl-terminate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsRoot);
        var instanceIdentity = MidTermInstanceIdentity.Load(settingsRoot, 5050);
        var session = new SessionInfoDto
        {
            Id = "session-runtime-terminate-host-1",
            CurrentDirectory = AppContext.BaseDirectory,
            ForegroundName = "codex",
            AppServerControlOnly = true,
            ProfileHint = "codex"
        };

        try
        {
            await using var initialRuntime = new SessionAppServerControlHostRuntimeService(
                CreateSettingsService(settingsRoot),
                instanceIdentity,
                mode: "synthetic");

            Assert.True(await initialRuntime.EnsureAttachedAsync(session.Id, "codex", session));
            var hostPid = await WaitForHostPidAsync(instanceIdentity, session.Id);

            await using var restartedRuntime = new SessionAppServerControlHostRuntimeService(
                CreateSettingsService(settingsRoot),
                instanceIdentity,
                mode: "synthetic");

            Assert.True(await restartedRuntime.RecoverExistingHostAsync(session.Id, "codex", session));

            var terminated = await restartedRuntime.TerminateAllOwnedHostsAsync();

            Assert.Equal(1, terminated);
            Assert.True(await WaitForConditionAsync(() => !ProcessExists(hostPid)));
            Assert.False(restartedRuntime.MayHaveRecoverableHost(session.Id));
        }
        finally
        {
            try
            {
                Directory.Delete(settingsRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task SessionAppServerControlHostRuntimeService_CanRecoverFullAppServerControlHistoryAfterMtRestart()
    {
        var settingsRoot = Path.Combine(Path.GetTempPath(), "midterm-appServerControl-restart-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsRoot);
        var instanceIdentity = MidTermInstanceIdentity.Load(settingsRoot, 4040);
        var session = new SessionInfoDto
        {
            Id = "session-runtime-restart-host-1",
            CurrentDirectory = AppContext.BaseDirectory,
            ForegroundName = "codex",
            AppServerControlOnly = true,
            ProfileHint = "codex"
        };

        try
        {
            await using var initialRuntime = new SessionAppServerControlHostRuntimeService(
                CreateSettingsService(settingsRoot),
                instanceIdentity,
                mode: "synthetic");

            var attached = await initialRuntime.EnsureAttachedAsync(session.Id, "codex", session);
            Assert.True(attached);

            var turn = await initialRuntime.StartTurnAsync(
                session.Id,
                new AppServerControlTurnRequest
                {
                    Text = "Persist this App Server Controller conversation across restart.",
                    Attachments = []
                });

            Assert.Equal("accepted", turn.Status);
            var initialSnapshot = await WaitForSnapshotAsync(
                initialRuntime,
                session.Id,
                current => current.CurrentTurn.State == "completed" &&
                           current.Streams.AssistantText.Contains("Persist this App Server Controller conversation across restart.", StringComparison.Ordinal));
            Assert.NotNull(initialSnapshot);

            await using var restartedRuntime = new SessionAppServerControlHostRuntimeService(
                CreateSettingsService(settingsRoot),
                instanceIdentity,
                mode: "synthetic");

            var recovered = await restartedRuntime.RecoverExistingHostAsync(session.Id, "codex", session);
            Assert.True(recovered);

            var recoveredSnapshot = await WaitForSnapshotAsync(
                restartedRuntime,
                session.Id,
                current => current.CurrentTurn.State == "completed" &&
                           current.Streams.AssistantText.Contains("Persist this App Server Controller conversation across restart.", StringComparison.Ordinal));
            Assert.NotNull(recoveredSnapshot);
            var fullHistorySnapshot = await restartedRuntime.GetHistoryWindowAsync(session.Id, 0, 200);
            Assert.NotNull(fullHistorySnapshot);
            Assert.Contains(
                fullHistorySnapshot!.History,
                entry => entry.ItemType == "user_message" &&
                         entry.Body.Contains("Persist this App Server Controller conversation across restart.", StringComparison.Ordinal));
            Assert.Equal(initialSnapshot!.Streams.AssistantText, recoveredSnapshot.Streams.AssistantText);
            await restartedRuntime.DetachAsync(session.Id);
        }
        finally
        {
            try
            {
                Directory.Delete(settingsRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task SessionAppServerControlRuntimeService_CanRecoverCodexAppServerControlHistoryWhenRestartedSessionLooksLikeShell()
    {
        using var fakeCodex = FakeCodexPathScope.Create();
        var settingsRoot = Path.Combine(Path.GetTempPath(), "midterm-appServerControl-codex-shell-recovery-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsRoot);
        var instanceIdentity = MidTermInstanceIdentity.Load(settingsRoot, 4041);
        const string prompt = "Persist Codex App Server Controller history while the terminal looks like shell after restart.";
        var initialSession = new SessionInfoDto
        {
            Id = "session-runtime-codex-shell-recovery-1",
            CurrentDirectory = fakeCodex.Root,
            ForegroundName = "codex"
        };

        try
        {
            await using var initialHostRuntime = new SessionAppServerControlHostRuntimeService(
                CreateSettingsService(settingsRoot),
                instanceIdentity,
                mode: "codex");
            await using var initialSessionManager = new TtyHostSessionManager();
            var profileService = new AiCliProfileService();
            await using var initialRuntime = new SessionAppServerControlRuntimeService(
                initialSessionManager,
                profileService,
                initialHostRuntime);

            Assert.True(await initialRuntime.EnsureAttachedAsync(initialSession.Id, initialSession));
            var turn = await initialRuntime.StartTurnAsync(
                initialSession.Id,
                new AppServerControlTurnRequest
                {
                    Text = prompt,
                    Attachments = []
                });

            Assert.Equal("accepted", turn.Status);
            var initialSnapshot = await WaitForSnapshotAsync(
                initialRuntime,
                initialSession.Id,
                current => current.CurrentTurn.State == "completed" &&
                           current.Streams.AssistantText.Contains(prompt, StringComparison.Ordinal));
            Assert.NotNull(initialSnapshot);

            await using var restartedHostRuntime = new SessionAppServerControlHostRuntimeService(
                CreateSettingsService(settingsRoot),
                instanceIdentity,
                mode: "codex");
            await using var restartedSessionManager = new TtyHostSessionManager();
            await using var restartedRuntime = new SessionAppServerControlRuntimeService(
                restartedSessionManager,
                profileService,
                restartedHostRuntime);

            var shellLikeSession = new SessionInfoDto
            {
                Id = initialSession.Id,
                CurrentDirectory = fakeCodex.Root,
                ShellType = "Pwsh",
                ForegroundName = "pwsh",
                ForegroundCommandLine = "pwsh",
                ForegroundProcessIdentity = "shell"
            };

            var attached = await restartedRuntime.EnsureAttachedAsync(shellLikeSession.Id, shellLikeSession);

            Assert.True(attached);
            var recoveredSnapshot = await WaitForSnapshotAsync(
                restartedRuntime,
                shellLikeSession.Id,
                current => current.CurrentTurn.State == "completed" &&
                           current.Streams.AssistantText.Contains(prompt, StringComparison.Ordinal));
            Assert.NotNull(recoveredSnapshot);
            Assert.Equal(initialSnapshot!.Thread.ThreadId, recoveredSnapshot!.Thread.ThreadId);

            var fullHistorySnapshot = await restartedRuntime.GetHistoryWindowAsync(shellLikeSession.Id, 0, 200);
            Assert.NotNull(fullHistorySnapshot);
            Assert.Contains(
                fullHistorySnapshot!.History,
                entry => entry.ItemType == "user_message" &&
                         entry.Body.Contains(prompt, StringComparison.Ordinal));

            await restartedRuntime.DetachAsync(shellLikeSession.Id);
        }
        finally
        {
            try
            {
                Directory.Delete(settingsRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task SessionAppServerControlHostRuntimeService_Forget_KillsRecoveredMtAgentHostProcess()
    {
        var settingsRoot = Path.Combine(Path.GetTempPath(), "midterm-appServerControl-kill-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsRoot);
        var instanceIdentity = MidTermInstanceIdentity.Load(settingsRoot, 4040);
        var session = new SessionInfoDto
        {
            Id = "session-runtime-kill-host-1",
            CurrentDirectory = AppContext.BaseDirectory,
            ForegroundName = "codex",
            AppServerControlOnly = true,
            ProfileHint = "codex"
        };

        try
        {
            await using var initialRuntime = new SessionAppServerControlHostRuntimeService(
                CreateSettingsService(settingsRoot),
                instanceIdentity,
                mode: "synthetic");

            Assert.True(await initialRuntime.EnsureAttachedAsync(session.Id, "codex", session));

            var hostPid = await WaitForTrackedHostPidAsync(initialRuntime, session.Id);
            Assert.True(hostPid > 0);
            Assert.True(ProcessExists(hostPid));

            await using var recoveredRuntime = new SessionAppServerControlHostRuntimeService(
                CreateSettingsService(settingsRoot),
                instanceIdentity,
                mode: "synthetic");

            Assert.True(await recoveredRuntime.RecoverExistingHostAsync(session.Id, "codex", session));
            Assert.False(HasTrackedHostProcess(recoveredRuntime, session.Id));

            recoveredRuntime.Forget(session.Id);

            Assert.True(await WaitForConditionAsync(() => !ProcessExists(hostPid)));
        }
        finally
        {
            try
            {
                Directory.Delete(settingsRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task<AppServerControlHistoryWindowResponse?> WaitForSnapshotAsync(
        SessionAppServerControlRuntimeService runtime,
        string sessionId,
        Func<AppServerControlHistoryWindowResponse, bool> predicate)
    {
        for (var i = 0; i < 80; i++)
        {
            runtime.TryGetCachedHistoryWindow(sessionId, out var snapshot);
            snapshot ??= await runtime.GetHistoryWindowAsync(sessionId);
            if (snapshot is not null && predicate(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(25);
        }

        return await runtime.GetHistoryWindowAsync(sessionId).ConfigureAwait(false) ??
               (runtime.TryGetCachedHistoryWindow(sessionId, out var finalSnapshot) ? finalSnapshot : null);
    }

    private static async Task<AppServerControlHistoryWindowResponse?> WaitForSnapshotAsync(
        SessionAppServerControlHostRuntimeService hostRuntime,
        string sessionId,
        Func<AppServerControlHistoryWindowResponse, bool> predicate)
    {
        for (var i = 0; i < 80; i++)
        {
            hostRuntime.TryGetCachedHistoryWindow(sessionId, out var snapshot);
            snapshot ??= await hostRuntime.GetHistoryWindowAsync(sessionId);
            if (snapshot is not null && predicate(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(25);
        }

        return await hostRuntime.GetHistoryWindowAsync(sessionId).ConfigureAwait(false) ??
               (hostRuntime.TryGetCachedHistoryWindow(sessionId, out var finalSnapshot) ? finalSnapshot : null);
    }

    private static async Task<int> WaitForHostPidAsync(MidTermInstanceIdentity instanceIdentity, string sessionId)
    {
        var registryPath = Path.Combine(
            Path.GetDirectoryName(instanceIdentity.SessionRegistryPath) ?? Path.GetTempPath(),
            $"appServerControl-host-sessions-{instanceIdentity.InstanceId}.json");

        for (var i = 0; i < 80; i++)
        {
            if (File.Exists(registryPath))
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(registryPath));
                if (document.RootElement.TryGetProperty("Sessions", out var sessions))
                {
                    using var entries = sessions.EnumerateArray();
                    while (entries.MoveNext())
                    {
                        var entry = entries.Current;
                        if (!entry.TryGetProperty("SessionId", out var sessionIdProperty) ||
                            !string.Equals(sessionIdProperty.GetString(), sessionId, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (entry.TryGetProperty("HostPid", out var hostPidProperty) &&
                            hostPidProperty.TryGetInt32(out var hostPid) &&
                            hostPid > 0)
                        {
                            return hostPid;
                        }
                    }
                }
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException($"Timed out waiting for host pid for session '{sessionId}'.");
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 80; i++)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return predicate();
    }

    private static bool ProcessExists(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int> WaitForTrackedHostPidAsync(SessionAppServerControlHostRuntimeService runtime, string sessionId)
    {
        for (var i = 0; i < 80; i++)
        {
            var hostPid = GetTrackedHostPid(runtime, sessionId);
            if (hostPid > 0)
            {
                return hostPid;
            }

            await Task.Delay(25);
        }

        return GetTrackedHostPid(runtime, sessionId);
    }

    private static int GetTrackedHostPid(SessionAppServerControlHostRuntimeService runtime, string sessionId)
    {
        var state = GetTrackedHostState(runtime, sessionId);
        if (state is null)
        {
            return 0;
        }

        return state.GetType().GetProperty("HostPid", BindingFlags.Instance | BindingFlags.Public)?.GetValue(state) as int? ?? 0;
    }

    private static bool HasTrackedHostProcess(SessionAppServerControlHostRuntimeService runtime, string sessionId)
    {
        var state = GetTrackedHostState(runtime, sessionId);
        if (state is null)
        {
            return false;
        }

        return state.GetType().GetProperty("Process", BindingFlags.Instance | BindingFlags.Public)?.GetValue(state) is Process;
    }

    private static object? GetTrackedHostState(SessionAppServerControlHostRuntimeService runtime, string sessionId)
    {
        var statesField = typeof(SessionAppServerControlHostRuntimeService).GetField("_states", BindingFlags.Instance | BindingFlags.NonPublic);
        var states = statesField?.GetValue(runtime);
        var tryGetValue = states?.GetType().GetMethod("TryGetValue");
        if (states is null || tryGetValue is null)
        {
            return null;
        }

        var args = new object?[] { sessionId, null };
        var found = tryGetValue.Invoke(states, args) as bool? == true;
        return found ? args[1] : null;
    }
}
