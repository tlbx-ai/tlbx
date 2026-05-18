using System.Diagnostics;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

[Collection(PathSensitiveEnvironmentCollection.Name)]
public sealed class MtAgentHostCodexIntegrationTests
{
    [Fact]
    public async Task MtAgentHost_CanDriveFakeCodexAttachTurnApprovalAndAttachments()
    {
        using var fakeCodex = FakeCodexPathScope.Create();
        var hostDll = ResolveAgentHostDll();
        var imagePath = Path.Combine(fakeCodex.Root, "sample.png");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3, 4]);
        var filePath = Path.Combine(fakeCodex.Root, "notes.txt");
        await File.WriteAllTextAsync(filePath, "attached text file");

        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();
        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Equal(AppServerControlHostProtocol.CurrentVersion, hello.ProtocolVersion);
            Assert.Contains("codex", hello.Providers);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach",
                SessionId = "session-1",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-1",
                    Provider = "codex",
                    WorkingDirectory = fakeCodex.Root
                }
            });

            var attachResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach");
            Assert.Equal("accepted", attachResult.Status);
            var attachWindow = await WaitForReadyWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-1");
            Assert.Equal("ready", attachWindow.Session.State);
            Assert.False(string.IsNullOrWhiteSpace(attachWindow.Thread.ThreadId));

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn",
                SessionId = "session-1",
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text = "Inspect attachments and ask approval.",
                    Attachments =
                    [
                        new AppServerControlAttachmentReference { Kind = "file", Path = filePath },
                        new AppServerControlAttachmentReference { Kind = "image", Path = imagePath, MimeType = "image/png" }
                    ]
                }
            });

            var turnResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn");
            Assert.Equal("accepted", turnResult.Status);
            Assert.NotNull(turnResult.TurnStarted);
            Assert.Equal("codex", turnResult.TurnStarted!.Provider);

            _ = await AppServerControlHostTestClient.ReadUntilMatchAsync(
                process.StandardOutput,
                pendingPatches,
                patch => patch.Patch.RequestUpserts.Any(static request => request.Kind == "command_execution_approval" && request.State == "open"),
                maxPatches: 40,
                timeout: TimeSpan.FromSeconds(10));

            var turnWindow = await AppServerControlHostTestClient.GetHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-1",
                count: 96);
            Assert.Contains("images=1", turnWindow.Streams.AssistantText, StringComparison.Ordinal);
            Assert.Contains("filerefs=true", turnWindow.Streams.AssistantText, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(turnWindow.Streams.UnifiedDiff));
            var request = Assert.Single(turnWindow.Requests, request => request.Kind == "command_execution_approval" && request.State == "open");

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-resolve",
                SessionId = "session-1",
                Type = "request.resolve",
                ResolveRequest = new AppServerControlRequestResolutionCommand
                {
                    RequestId = request.RequestId,
                    Decision = "accept"
                }
            });

            var resolveResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-resolve");
            Assert.Equal("accepted", resolveResult.Status);

            var resolveWindow = await WaitForTurnStateWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-1",
                "completed");
            Assert.Contains(resolveWindow.Requests, entry => entry.RequestId == request.RequestId && entry.Decision == "accept");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task MtAgentHost_CanDriveFakeCodexUserInputFlow()
    {
        using var fakeCodex = FakeCodexPathScope.Create();
        var hostDll = ResolveAgentHostDll();
        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();

        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Contains("codex", hello.Providers);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach",
                SessionId = "session-user-input",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-user-input",
                    Provider = "codex",
                    WorkingDirectory = fakeCodex.Root
                }
            });

            _ = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach");
            _ = await WaitForReadyWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-user-input");

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn-user",
                SessionId = "session-user-input",
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text = "Inspect the repo and ask user for the mode.",
                    Attachments = []
                }
            });

            _ = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn-user");
            _ = await AppServerControlHostTestClient.ReadUntilMatchAsync(
                process.StandardOutput,
                pendingPatches,
                patch => patch.Patch.RequestUpserts.Any(static request => request.Kind == "interview" && request.State == "open"),
                maxPatches: 40,
                timeout: TimeSpan.FromSeconds(10));

            var questionWindow = await AppServerControlHostTestClient.GetHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-user-input",
                count: 96);
            var userInputRequest = Assert.Single(questionWindow.Requests, request => request.Kind == "interview" && request.State == "open");
            Assert.Equal("choice", Assert.Single(userInputRequest.Questions).Id);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-user-answer",
                SessionId = "session-user-input",
                Type = "user-input.resolve",
                ResolveUserInput = new AppServerControlUserInputResolutionCommand
                {
                    RequestId = userInputRequest.RequestId,
                    Answers =
                    [
                        new AppServerControlAnsweredQuestion
                        {
                            QuestionId = "choice",
                            Answers = ["Safe"]
                        }
                    ]
                }
            });

            var resolveResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-user-answer");
            Assert.Equal("accepted", resolveResult.Status);

            var resolveWindow = await WaitForTurnStateWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-user-input",
                "completed");
            Assert.Contains(resolveWindow.Requests, request => request.RequestId == userInputRequest.RequestId && request.State == "resolved");
            Assert.Contains(resolveWindow.History, item => item.RequestId == userInputRequest.RequestId && item.ItemType == "interview");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task MtAgentHost_SpawnsFakeCodexAppServerWithExpectedColdAttachParameters()
    {
        var originalYoloDefault = Environment.GetEnvironmentVariable("MIDTERM_APP_SERVER_CONTROL_CODEX_YOLO_DEFAULT");
        var originalRemoteCompactionDisabled = Environment.GetEnvironmentVariable("MIDTERM_APP_SERVER_CONTROL_CODEX_REMOTE_COMPACTION_V2_DISABLED");
        Environment.SetEnvironmentVariable("MIDTERM_APP_SERVER_CONTROL_CODEX_YOLO_DEFAULT", "false");
        Environment.SetEnvironmentVariable("MIDTERM_APP_SERVER_CONTROL_CODEX_REMOTE_COMPACTION_V2_DISABLED", null);

        using var fakeCodex = FakeCodexPathScope.Create();
        var hostDll = ResolveAgentHostDll();
        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();

        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Contains("codex", hello.Providers);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach-cold-launch",
                SessionId = "session-cold-launch",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-cold-launch",
                    Provider = "codex",
                    WorkingDirectory = fakeCodex.Root
                }
            });

            var attachResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach-cold-launch");
            Assert.Equal("accepted", attachResult.Status);

            _ = await WaitForReadyWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-cold-launch");

            var capture = await WaitForFakeCodexLaunchCaptureAsync(
                fakeCodex.CapturePath,
                static launch => launch.Arguments.Length > 0 &&
                                 !string.IsNullOrWhiteSpace(launch.ThreadStartCwd));

            Assert.Equal(["-c", "fast_default_opt_out=false", "--enable", "remote_compaction_v2", "app-server"], capture.Arguments);
            Assert.Equal(fakeCodex.Root, capture.ProcessWorkingDirectory);
            Assert.Contains("initialize", capture.Methods);
            Assert.Contains("initialized", capture.Methods);
            Assert.Contains("thread/start", capture.Methods);
            Assert.DoesNotContain("thread/resume", capture.Methods);
            Assert.Equal("midterm", capture.InitializeClientName);
            Assert.Equal("MidTerm App Server Controller", capture.InitializeClientTitle);
            Assert.False(string.IsNullOrWhiteSpace(capture.InitializeClientVersion));
            Assert.True(capture.InitializeExperimentalApi);
            Assert.Equal(fakeCodex.Root, capture.ThreadStartCwd);
            Assert.Equal("on-request", capture.ThreadStartApprovalPolicy);
            Assert.Equal("workspace-write", capture.ThreadStartSandbox);
            Assert.False(capture.ThreadStartExperimentalRawEvents);
            Assert.False(capture.ThreadStartPersistExtendedHistory);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Environment.SetEnvironmentVariable("MIDTERM_APP_SERVER_CONTROL_CODEX_YOLO_DEFAULT", originalYoloDefault);
            Environment.SetEnvironmentVariable("MIDTERM_APP_SERVER_CONTROL_CODEX_REMOTE_COMPACTION_V2_DISABLED", originalRemoteCompactionDisabled);
        }
    }

    [Fact]
    public async Task MtAgentHost_AllowsExplicitRemoteCompactionFeatureDisable()
    {
        var originalYoloDefault = Environment.GetEnvironmentVariable("MIDTERM_APP_SERVER_CONTROL_CODEX_YOLO_DEFAULT");
        var originalRemoteCompactionDisabled = Environment.GetEnvironmentVariable("MIDTERM_APP_SERVER_CONTROL_CODEX_REMOTE_COMPACTION_V2_DISABLED");
        Environment.SetEnvironmentVariable("MIDTERM_APP_SERVER_CONTROL_CODEX_YOLO_DEFAULT", "false");
        Environment.SetEnvironmentVariable("MIDTERM_APP_SERVER_CONTROL_CODEX_REMOTE_COMPACTION_V2_DISABLED", "true");

        using var fakeCodex = FakeCodexPathScope.Create();
        var hostDll = ResolveAgentHostDll();
        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();

        try
        {
            _ = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach-remote-compaction-disabled",
                SessionId = "session-remote-compaction-disabled",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-remote-compaction-disabled",
                    Provider = "codex",
                    WorkingDirectory = fakeCodex.Root
                }
            });

            var attachResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach-remote-compaction-disabled");
            Assert.Equal("accepted", attachResult.Status);

            _ = await WaitForReadyWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-remote-compaction-disabled");

            var capture = await WaitForFakeCodexLaunchCaptureAsync(
                fakeCodex.CapturePath,
                static launch => launch.Arguments.Length > 0 &&
                                 !string.IsNullOrWhiteSpace(launch.ThreadStartCwd));

            Assert.Equal(["-c", "fast_default_opt_out=false", "app-server"], capture.Arguments);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Environment.SetEnvironmentVariable("MIDTERM_APP_SERVER_CONTROL_CODEX_YOLO_DEFAULT", originalYoloDefault);
            Environment.SetEnvironmentVariable("MIDTERM_APP_SERVER_CONTROL_CODEX_REMOTE_COMPACTION_V2_DISABLED", originalRemoteCompactionDisabled);
        }
    }

    [Fact]
    public async Task MtAgentHost_CanResolveFakeCodexPermissionApprovalRequest()
    {
        using var fakeCodex = FakeCodexPathScope.Create();
        var hostDll = ResolveAgentHostDll();
        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();

        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Contains("codex", hello.Providers);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach-permissions",
                SessionId = "session-permissions",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-permissions",
                    Provider = "codex",
                    WorkingDirectory = fakeCodex.Root
                }
            });

            _ = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach-permissions");
            _ = await WaitForReadyWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-permissions");

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn-permissions",
                SessionId = "session-permissions",
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text = "Inspect repo and ask permission.",
                    Attachments = []
                }
            });

            _ = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn-permissions");
            _ = await AppServerControlHostTestClient.ReadUntilMatchAsync(
                process.StandardOutput,
                pendingPatches,
                patch => patch.Patch.RequestUpserts.Any(static request => request.Kind == "permissions_approval" && request.State == "open"),
                maxPatches: 40,
                timeout: TimeSpan.FromSeconds(10));

            var permissionWindow = await AppServerControlHostTestClient.GetHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-permissions",
                count: 96);
            Assert.Contains("protocol-v2.txt", permissionWindow.Streams.UnifiedDiff, StringComparison.Ordinal);
            Assert.Contains("from patch updated", permissionWindow.Streams.UnifiedDiff, StringComparison.Ordinal);
            var request = Assert.Single(permissionWindow.Requests, request => request.Kind == "permissions_approval" && request.State == "open");
            Assert.Contains("Permissions approval", request.KindLabel, StringComparison.Ordinal);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-resolve-permissions",
                SessionId = "session-permissions",
                Type = "request.resolve",
                ResolveRequest = new AppServerControlRequestResolutionCommand
                {
                    RequestId = request.RequestId,
                    Decision = "accept"
                }
            });

            var resolveResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-resolve-permissions");
            Assert.Equal("accepted", resolveResult.Status);

            var resolveWindow = await WaitForTurnStateWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-permissions",
                "completed");
            Assert.Contains(resolveWindow.Requests, entry => entry.RequestId == request.RequestId && entry.Decision == "accept");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task MtAgentHost_AppliesExplicitUserProfileEnvironmentToSpawnedCodexProcess()
    {
        using var fakeCodex = FakeCodexPathScope.Create();
        var hostDll = ResolveAgentHostDll();
        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();
        var profileDirectory = Path.Combine(fakeCodex.Root, "Users", "johan");
        Directory.CreateDirectory(Path.Combine(profileDirectory, "AppData", "Roaming", "npm"));
        Directory.CreateDirectory(Path.Combine(profileDirectory, "AppData", "Local", "Programs", "nodejs"));
        Directory.CreateDirectory(Path.Combine(profileDirectory, ".local", "bin"));

        try
        {
            _ = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach-profile-env",
                SessionId = "session-profile-env",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-profile-env",
                    Provider = "codex",
                    WorkingDirectory = fakeCodex.Root,
                    UserProfileDirectory = profileDirectory
                }
            });

            var attachResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach-profile-env");
            Assert.Equal("accepted", attachResult.Status);

            var capture = await WaitForFakeCodexLaunchCaptureAsync(
                fakeCodex.CapturePath,
                static launch => !string.IsNullOrWhiteSpace(launch.UserProfile));

            Assert.Equal(profileDirectory, capture.UserProfile);
            Assert.Equal(profileDirectory, capture.Home);
            Assert.Equal(Path.Combine(profileDirectory, ".codex"), capture.CodexHome);
            Assert.Equal(Path.Combine(profileDirectory, "AppData", "Roaming"), capture.AppData);
            Assert.Equal(Path.Combine(profileDirectory, "AppData", "Local"), capture.LocalAppData);
            Assert.StartsWith(Path.Combine(profileDirectory, "AppData", "Roaming", "npm"), capture.Path, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task MtAgentHost_CanAttachToExistingCodexWebSocketRuntime()
    {
        await using var fakeServer = FakeCodexWebSocketServer.Start(
            loadedThreadId: "thread-remote-1",
            assistantReply: "Remote Codex shared-runtime reply.");
        var hostDll = ResolveAgentHostDll();
        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();

        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Contains("codex", hello.Providers);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach-remote",
                SessionId = "session-remote",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-remote",
                    Provider = "codex",
                    WorkingDirectory = AppContext.BaseDirectory,
                    AttachPoint = new SessionAgentAttachPoint
                    {
                        Provider = SessionAgentAttachPoint.CodexProvider,
                        TransportKind = SessionAgentAttachPoint.CodexAppServerWebSocketTransport,
                        Endpoint = fakeServer.Endpoint,
                        SharedRuntime = true,
                        Source = "test",
                        PreferredThreadId = "thread-remote-1"
                    },
                    ResumeThreadId = "thread-remote-1"
                }
            });

            var attachResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach-remote");
            Assert.Equal("accepted", attachResult.Status);

            var attachWindow = await WaitForReadyWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-remote");
            Assert.Equal("thread-remote-1", attachWindow.Thread.ThreadId);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn-remote",
                SessionId = "session-remote",
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text = "Continue from the shared thread.",
                    Attachments = []
                }
            });

            var turnResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn-remote");
            Assert.Equal("accepted", turnResult.Status);
            Assert.Equal("thread-remote-1", turnResult.TurnStarted!.ThreadId);

            var turnWindow = await WaitForTurnStateWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-remote",
                "completed");
            Assert.Contains("Remote Codex shared-runtime reply.", AppServerControlHostTestClient.CollectAssistantText(turnWindow), StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task MtAgentHost_MapsCodexProtocolV2RemoteControlAndPatchNotifications()
    {
        await using var fakeServer = FakeCodexWebSocketServer.Start(
            loadedThreadId: "thread-remote-protocol-v2-1",
            assistantReply: "Remote Codex protocol v2 reply.",
            emitTurnIds: true,
            emitProtocolV2Surface: true);
        var hostDll = ResolveAgentHostDll();
        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();

        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Contains("codex", hello.Providers);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach-protocol-v2",
                SessionId = "session-protocol-v2",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-protocol-v2",
                    Provider = "codex",
                    WorkingDirectory = AppContext.BaseDirectory,
                    AttachPoint = new SessionAgentAttachPoint
                    {
                        Provider = SessionAgentAttachPoint.CodexProvider,
                        TransportKind = SessionAgentAttachPoint.CodexAppServerWebSocketTransport,
                        Endpoint = fakeServer.Endpoint,
                        SharedRuntime = true,
                        Source = "test",
                        PreferredThreadId = "thread-remote-protocol-v2-1"
                    },
                    ResumeThreadId = "thread-remote-protocol-v2-1"
                }
            });

            _ = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach-protocol-v2");
            _ = await WaitForReadyWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-protocol-v2");

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn-protocol-v2",
                SessionId = "session-protocol-v2",
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text = "Continue and emit protocol v2 notifications.",
                    Attachments = []
                }
            });

            _ = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn-protocol-v2");
            var turnWindow = await WaitForTurnStateWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-protocol-v2",
                "completed");

            Assert.Contains(turnWindow.Notices, notice => notice.Message.Contains("remote-control status: connected", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(turnWindow.Notices, notice => notice.Type == "runtime.warning" && notice.Message.Contains("Fake Codex protocol warning", StringComparison.Ordinal));
            Assert.Contains("protocol-v2.txt", turnWindow.Streams.UnifiedDiff, StringComparison.Ordinal);
            Assert.Contains("from patch updated", turnWindow.Streams.UnifiedDiff, StringComparison.Ordinal);
            Assert.Contains("protocol command output", turnWindow.Streams.CommandOutput, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task MtAgentHost_NormalizesCamelCaseCodexItemsFromWebSocketRuntime()
    {
        await using var fakeServer = FakeCodexWebSocketServer.Start(
            loadedThreadId: "thread-remote-rich-1",
            assistantReply: "HELLO_FROM_CODEX",
            emitRichHistoryItems: true);
        var hostDll = ResolveAgentHostDll();
        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();

        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Contains("codex", hello.Providers);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach-rich",
                SessionId = "session-remote-rich",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-remote-rich",
                    Provider = "codex",
                    WorkingDirectory = AppContext.BaseDirectory,
                    AttachPoint = new SessionAgentAttachPoint
                    {
                        Provider = SessionAgentAttachPoint.CodexProvider,
                        TransportKind = SessionAgentAttachPoint.CodexAppServerWebSocketTransport,
                        Endpoint = fakeServer.Endpoint,
                        SharedRuntime = true,
                        Source = "test",
                        PreferredThreadId = "thread-remote-rich-1"
                    },
                    ResumeThreadId = "thread-remote-rich-1"
                }
            });

            _ = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach-rich");
            _ = await WaitForReadyWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-remote-rich");

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn-rich",
                SessionId = "session-remote-rich",
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text = "Reply with exactly HELLO_FROM_CODEX",
                    Attachments = []
                }
            });

            _ = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn-rich");
            var turnWindow = await WaitForTurnStateWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-remote-rich",
                "completed");

            Assert.Contains(
                turnWindow.History,
                item => string.IsNullOrWhiteSpace(item.CommandText) &&
                        item.Body.Contains("Reply with exactly HELLO_FROM_CODEX", StringComparison.Ordinal));
            Assert.Contains(
                turnWindow.History,
                item => string.IsNullOrWhiteSpace(item.CommandText) &&
                        item.Body.Contains("HELLO_FROM_CODEX", StringComparison.Ordinal));
            Assert.Contains(
                turnWindow.History,
                item => !string.IsNullOrWhiteSpace(item.CommandText) &&
                        item.Body.Contains("pwd", StringComparison.Ordinal));
            Assert.Contains("HELLO_FROM_CODEX", AppServerControlHostTestClient.CollectAssistantText(turnWindow), StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task MtAgentHost_MapsCodexMcpToolProgressIntoCanonicalItemUpdates()
    {
        await using var fakeServer = FakeCodexWebSocketServer.Start(
            loadedThreadId: "thread-remote-mcp-1",
            assistantReply: "MCP progress handled.",
            emitRichHistoryItems: true,
            emitTurnIds: true,
            emitMcpToolProgress: true);
        var hostDll = ResolveAgentHostDll();
        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();

        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Contains("codex", hello.Providers);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach-mcp",
                SessionId = "session-remote-mcp",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-remote-mcp",
                    Provider = "codex",
                    WorkingDirectory = AppContext.BaseDirectory,
                    AttachPoint = new SessionAgentAttachPoint
                    {
                        Provider = SessionAgentAttachPoint.CodexProvider,
                        TransportKind = SessionAgentAttachPoint.CodexAppServerWebSocketTransport,
                        Endpoint = fakeServer.Endpoint,
                        SharedRuntime = true,
                        Source = "test",
                        PreferredThreadId = "thread-remote-mcp-1"
                    },
                    ResumeThreadId = "thread-remote-mcp-1"
                }
            });

            _ = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach-mcp");
            _ = await WaitForReadyWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-remote-mcp");

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn-mcp",
                SessionId = "session-remote-mcp",
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text = "Show MCP tool progress.",
                    Attachments = []
                }
            });

            _ = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn-mcp");
            var turnWindow = await WaitForTurnStateWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-remote-mcp",
                "completed");

            Assert.Contains(
                turnWindow.History,
                item => item.ItemId == "item-mcp-1" &&
                        item.TurnId == "turn-remote-1" &&
                        item.Body.Contains("Searching src for AppServerControl runtime events", StringComparison.Ordinal));
            Assert.Contains(
                turnWindow.Items,
                item => item.ItemId == "item-mcp-1" &&
                        item.ItemType == "mcp_tool_call" &&
                        item.Status == "completed");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task MtAgentHost_EmitsFallbackAppServerControlItemForUnknownCodexNotifications()
    {
        await using var fakeServer = FakeCodexWebSocketServer.Start(
            loadedThreadId: "thread-remote-unknown-1",
            assistantReply: "Unknown event handled.",
            emitRichHistoryItems: true,
            emitTurnIds: true,
            emitUnknownAgentNotification: true);
        var hostDll = ResolveAgentHostDll();
        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();

        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Contains("codex", hello.Providers);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach-unknown",
                SessionId = "session-remote-unknown",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-remote-unknown",
                    Provider = "codex",
                    WorkingDirectory = AppContext.BaseDirectory,
                    AttachPoint = new SessionAgentAttachPoint
                    {
                        Provider = SessionAgentAttachPoint.CodexProvider,
                        TransportKind = SessionAgentAttachPoint.CodexAppServerWebSocketTransport,
                        Endpoint = fakeServer.Endpoint,
                        SharedRuntime = true,
                        Source = "test",
                        PreferredThreadId = "thread-remote-unknown-1"
                    },
                    ResumeThreadId = "thread-remote-unknown-1"
                }
            });

            _ = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach-unknown");
            _ = await WaitForReadyWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-remote-unknown");

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn-unknown",
                SessionId = "session-remote-unknown",
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text = "Show an unknown Codex event.",
                    Attachments = []
                }
            });

            _ = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn-unknown");
            _ = await AppServerControlHostTestClient.ReadUntilMatchAsync(
                process.StandardOutput,
                pendingPatches,
                patch => patch.Patch.HistoryUpserts.Any(item =>
                    item.TurnId == "turn-remote-1" &&
                    item.ItemType == "unknown_agent_message"),
                maxPatches: 20,
                timeout: TimeSpan.FromSeconds(10));

            var turnWindow = await AppServerControlHostTestClient.GetHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-remote-unknown",
                count: 128);
            Assert.Contains(
                turnWindow.History,
                item => item.TurnId == "turn-remote-1" &&
                        item.ItemType == "unknown_agent_message" &&
                        item.Title == "Unknown agent message" &&
                        item.Body.Contains("codex/event/unhandled_notification", StringComparison.Ordinal) &&
                        item.Body.Contains("Unhandled codex event for fallback coverage", StringComparison.Ordinal));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task MtAgentHost_MapsCodexBackgroundTerminalWaitIntoCanonicalTaskProgress()
    {
        await using var fakeServer = FakeCodexWebSocketServer.Start(
            loadedThreadId: "thread-remote-background-wait-1",
            assistantReply: "Background wait handled.",
            emitRichHistoryItems: true,
            emitTurnIds: true,
            emitBackgroundTerminalWaitNotification: true);
        var hostDll = ResolveAgentHostDll();
        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();

        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Contains("codex", hello.Providers);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach-background-wait",
                SessionId = "session-background-wait",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-background-wait",
                    Provider = "codex",
                    WorkingDirectory = AppContext.BaseDirectory,
                    AttachPoint = new SessionAgentAttachPoint
                    {
                        Provider = SessionAgentAttachPoint.CodexProvider,
                        TransportKind = SessionAgentAttachPoint.CodexAppServerWebSocketTransport,
                        Endpoint = fakeServer.Endpoint,
                        SharedRuntime = true,
                        Source = "test",
                        PreferredThreadId = "thread-remote-background-wait-1"
                    },
                    ResumeThreadId = "thread-remote-background-wait-1"
                }
            });

            _ = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach-background-wait");
            _ = await WaitForReadyWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-background-wait");

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn-background-wait",
                SessionId = "session-background-wait",
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text = "Show background wait.",
                    Attachments = []
                }
            });

            _ = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn-background-wait");
            _ = await AppServerControlHostTestClient.ReadUntilMatchAsync(
                process.StandardOutput,
                pendingPatches,
                patch => patch.Patch.HistoryUpserts.Any(item =>
                    item.TurnId == "turn-remote-1" &&
                    item.Kind == "reasoning" &&
                    item.Body.Contains("Waited for background terminal", StringComparison.Ordinal)),
                maxPatches: 20,
                timeout: TimeSpan.FromSeconds(10));

            var turnWindow = await AppServerControlHostTestClient.GetHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-background-wait",
                count: 128);
            Assert.Contains(
                turnWindow.History,
                item => item.TurnId == "turn-remote-1" &&
                        item.Kind == "reasoning" &&
                        item.Title == "Waiting for background terminal" &&
                        item.Body.Contains("Waited for background terminal  npm run lint", StringComparison.Ordinal));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task MtAgentHost_MapsCodexMcpStartupStatusIntoCanonicalAgentStateRuntimeEvents()
    {
        await using var fakeServer = FakeCodexWebSocketServer.Start(
            loadedThreadId: "thread-remote-agent-state-1",
            assistantReply: "Agent state handled.",
            emitMcpStartupStatus: true);
        var hostDll = ResolveAgentHostDll();
        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();

        try
        {
            _ = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach-agent-state",
                SessionId = "session-agent-state",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-agent-state",
                    Provider = "codex",
                    WorkingDirectory = AppContext.BaseDirectory,
                    AttachPoint = new SessionAgentAttachPoint
                    {
                        Provider = SessionAgentAttachPoint.CodexProvider,
                        TransportKind = SessionAgentAttachPoint.CodexAppServerWebSocketTransport,
                        Endpoint = fakeServer.Endpoint,
                        SharedRuntime = true,
                        Source = "test",
                        PreferredThreadId = "thread-remote-agent-state-1"
                    },
                    ResumeThreadId = "thread-remote-agent-state-1"
                }
            });

            _ = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach-agent-state");
            _ = await AppServerControlHostTestClient.ReadUntilMatchAsync(
                process.StandardOutput,
                pendingPatches,
                patch => patch.Patch.NoticeUpserts.Any(static notice => notice.Type == "agent.state"),
                maxPatches: 8,
                timeout: TimeSpan.FromSeconds(10));

            var attachWindow = await AppServerControlHostTestClient.WaitForHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-agent-state",
                window => window.Notices.Any(static notice => notice.Type == "agent.state"),
                TimeSpan.FromSeconds(10),
                count: 96);
            Assert.Contains(
                attachWindow.Notices,
                notice => notice.Type == "agent.state" &&
                          notice.Message == "codex_apps starting.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task MtAgentHost_GroupsStartupStderrBlocksIntoCanonicalAgentErrorRuntimeEvents()
    {
        using var fakeCodex = FakeCodexPathScope.Create();
        var previousBlock = Environment.GetEnvironmentVariable("MIDTERM_FAKE_CODEX_STARTUP_STDERR");
        Environment.SetEnvironmentVariable(
            "MIDTERM_FAKE_CODEX_STARTUP_STDERR",
            "ERROR\n[features].collab is deprecated. Use [features].multi_agent instead.\n\nEnable it with `--enable multi_agent` or `[features].multi_agent` in config.toml.");
        var hostDll = ResolveAgentHostDll();
        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();

        try
        {
            _ = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach-agent-error",
                SessionId = "session-agent-error",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-agent-error",
                    Provider = "codex",
                    WorkingDirectory = fakeCodex.Root
                }
            });

            _ = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach-agent-error");
            _ = await AppServerControlHostTestClient.ReadUntilMatchAsync(
                process.StandardOutput,
                pendingPatches,
                patch => patch.Patch.NoticeUpserts.Any(static notice => notice.Type == "agent.error"),
                maxPatches: 8,
                timeout: TimeSpan.FromSeconds(10));

            var attachWindow = await AppServerControlHostTestClient.WaitForHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-agent-error",
                window => window.Notices.Any(static notice => notice.Type == "agent.error"),
                TimeSpan.FromSeconds(10),
                count: 96);
            Assert.Contains(
                attachWindow.Notices,
                notice => notice.Type == "agent.error" &&
                          notice.Message.Contains(
                              "[features].collab is deprecated. Use [features].multi_agent instead.",
                              StringComparison.Ordinal) &&
                          notice.Message.Contains(
                              "Enable it with `--enable multi_agent`",
                              StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MIDTERM_FAKE_CODEX_STARTUP_STDERR", previousBlock);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task MtAgentHost_PreservesPayloadTurnIdForLateCodexDiffNotifications()
    {
        await using var fakeServer = FakeCodexWebSocketServer.Start(
            loadedThreadId: "thread-remote-late-diff-1",
            assistantReply: "Remote Codex reply with late diff.",
            emitTurnIds: true,
            emitLateDiffAfterCompletion: true);
        var hostDll = ResolveAgentHostDll();
        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();

        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Contains("codex", hello.Providers);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach-late-diff",
                SessionId = "session-remote-late-diff",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-remote-late-diff",
                    Provider = "codex",
                    WorkingDirectory = AppContext.BaseDirectory,
                    AttachPoint = new SessionAgentAttachPoint
                    {
                        Provider = SessionAgentAttachPoint.CodexProvider,
                        TransportKind = SessionAgentAttachPoint.CodexAppServerWebSocketTransport,
                        Endpoint = fakeServer.Endpoint,
                        SharedRuntime = true,
                        Source = "test",
                        PreferredThreadId = "thread-remote-late-diff-1"
                    },
                    ResumeThreadId = "thread-remote-late-diff-1"
                }
            });

            _ = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach-late-diff");
            _ = await WaitForReadyWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-remote-late-diff");

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn-late-diff",
                SessionId = "session-remote-late-diff",
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text = "Show a late diff update.",
                    Attachments = []
                }
            });

            _ = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn-late-diff");
            var turnWindow = await AppServerControlHostTestClient.WaitForHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-remote-late-diff",
                window => window.History.Any(item => item.Body.Contains("--- a/remote.txt", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(10),
                count: 96);
            var diffEntry = Assert.Single(
                turnWindow.History,
                item => item.Body.Contains("--- a/remote.txt", StringComparison.Ordinal));
            Assert.Equal("turn-remote-1", diffEntry.TurnId);
            Assert.Equal("--- a/remote.txt\n+++ b/remote.txt\n@@ -1 +1 @@\n-old\n+new", diffEntry.Body);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task MtAgentHost_CanInterruptFakeCodexTurn()
    {
        using var fakeCodex = FakeCodexPathScope.Create();
        var hostDll = ResolveAgentHostDll();
        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();

        try
        {
            _ = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach",
                SessionId = "session-interrupt",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-interrupt",
                    Provider = "codex",
                    WorkingDirectory = fakeCodex.Root
                }
            });

            _ = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach");
            _ = await WaitForReadyWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-interrupt");

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn-interrupt",
                SessionId = "session-interrupt",
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text = "Run a turn that will be interrupted.",
                    Attachments = []
                }
            });

            var turnResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn-interrupt");
            Assert.Equal("accepted", turnResult.Status);
            Assert.NotNull(turnResult.TurnStarted);

            var startedWindow = await AppServerControlHostTestClient.WaitForHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-interrupt",
                window => string.Equals(window.CurrentTurn.State, "running", StringComparison.Ordinal) &&
                          !string.IsNullOrWhiteSpace(window.CurrentTurn.TurnId),
                TimeSpan.FromSeconds(10),
                count: 32);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn-stop",
                SessionId = "session-interrupt",
                Type = "turn.interrupt",
                InterruptTurn = new AppServerControlInterruptRequest
                {
                    TurnId = startedWindow.CurrentTurn.TurnId
                }
            });

            var interruptResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn-stop");
            Assert.Equal("accepted", interruptResult.Status);

            var interruptedWindow = await AppServerControlHostTestClient.WaitForHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-interrupt",
                window => string.Equals(window.CurrentTurn.State, "aborted", StringComparison.Ordinal) ||
                          string.Equals(window.CurrentTurn.State, "interrupted", StringComparison.Ordinal),
                TimeSpan.FromSeconds(10),
                count: 160);
            Assert.True(
                string.Equals(interruptedWindow.CurrentTurn.State, "aborted", StringComparison.Ordinal) ||
                string.Equals(interruptedWindow.CurrentTurn.State, "interrupted", StringComparison.Ordinal),
                $"Expected interrupted Codex turn state, got '{interruptedWindow.CurrentTurn.State}'.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
        }
    }

    private static Process StartAgentHost(string hostDll)
    {
        var dotnetHost = ResolveDotNetHostPath();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = dotnetHost,
                Arguments = $"\"{hostDll}\" --stdio",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        return process;
    }

    private static string ResolveDotNetHostPath()
    {
        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(dotnetHost) && File.Exists(dotnetHost))
        {
            return dotnetHost;
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        return "dotnet";
    }

    private static string ResolveAgentHostDll()
    {
        return MtAgentHostTestPathResolver.ResolveAgentHostDll(AppContext.BaseDirectory);
    }

    private static async Task<FakeCodexLaunchCapture> WaitForFakeCodexLaunchCaptureAsync(
        string capturePath,
        Func<FakeCodexLaunchCapture, bool> predicate)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(capturePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(capturePath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var capture = JsonSerializer.Deserialize<FakeCodexLaunchCapture>(json);
                        if (capture is not null && predicate(capture))
                        {
                            return capture;
                        }
                    }
                }
                catch (JsonException)
                {
                }
                catch (IOException)
                {
                }
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out waiting for fake Codex launch capture at '{capturePath}'.");
    }

    private static async Task<AppServerControlHistoryWindowResponse> WaitForReadyWindowAsync(
        StreamReader reader,
        StreamWriter writer,
        Queue<AppServerControlHostHistoryPatchEnvelope> pendingPatches,
        string sessionId)
    {
        return await AppServerControlHostTestClient.WaitForHistoryWindowAsync(
            reader,
            writer,
            pendingPatches,
            sessionId,
            window => string.Equals(window.Session.State, "ready", StringComparison.Ordinal) &&
                      !string.IsNullOrWhiteSpace(window.Thread.ThreadId),
            TimeSpan.FromSeconds(10),
            count: 96);
    }

    private static async Task<AppServerControlHistoryWindowResponse> WaitForTurnStateWindowAsync(
        StreamReader reader,
        StreamWriter writer,
        Queue<AppServerControlHostHistoryPatchEnvelope> pendingPatches,
        string sessionId,
        string state)
    {
        return await AppServerControlHostTestClient.WaitForHistoryWindowAsync(
            reader,
            writer,
            pendingPatches,
            sessionId,
            window => string.Equals(window.CurrentTurn.State, state, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10),
            count: 160);
    }

    private sealed class FakeCodexLaunchCapture
    {
        public string[] Arguments { get; set; } = [];

        public string? ProcessWorkingDirectory { get; set; }

        public string? UserProfile { get; set; }

        public string? Home { get; set; }

        public string? CodexHome { get; set; }

        public string? AppData { get; set; }

        public string? LocalAppData { get; set; }

        public string? Path { get; set; }

        public List<string> Methods { get; set; } = [];

        public string? InitializeClientName { get; set; }

        public string? InitializeClientTitle { get; set; }

        public string? InitializeClientVersion { get; set; }

        public bool? InitializeExperimentalApi { get; set; }

        public string? ThreadStartCwd { get; set; }

        public string? ThreadStartApprovalPolicy { get; set; }

        public string? ThreadStartSandbox { get; set; }

        public bool? ThreadStartExperimentalRawEvents { get; set; }

        public bool? ThreadStartPersistExtendedHistory { get; set; }
    }
}
