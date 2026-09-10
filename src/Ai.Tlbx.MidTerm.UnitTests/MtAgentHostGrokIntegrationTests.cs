using System.Diagnostics;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

[Collection(PathSensitiveEnvironmentCollection.Name)]
public sealed class MtAgentHostGrokIntegrationTests
{
    [Theory]
    [InlineData("grok", "agent", "stdio")]
    [InlineData("opencode", "acp")]
    [InlineData("gemini", "--acp")]
    [InlineData("copilot", "--acp")]
    [InlineData("custom-acp", "--stdio-acp")]
    public async Task MtAgentHost_CanDriveStandardAcpAgent(string provider, params string[] expectedArguments)
    {
        using var fakeGrok = FakeGrokPathScope.Create();
        var hostDll = ResolveAgentHostDll();
        var sessionId = $"session-{provider}-" + Guid.NewGuid().ToString("N");
        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();
        var imagePath = Path.Combine(fakeGrok.Root, "pasted-image.png");
        var imageBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        await File.WriteAllBytesAsync(imagePath, imageBytes);

        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Contains("acp-v1", hello.Providers, StringComparer.Ordinal);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach",
                SessionId = sessionId,
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = sessionId,
                    Provider = provider,
                    RuntimeKind = "acp-v1",
                    ExecutablePath = fakeGrok.ExecutablePath,
                    AgentName = "Fake ACP agent",
                    ExecutableArguments = expectedArguments.ToList(),
                    WorkingDirectory = fakeGrok.Root
                }
            });

            var attachResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach");
            Assert.True(
                string.Equals("accepted", attachResult.Status, StringComparison.Ordinal),
                attachResult.Message ?? $"Unexpected attach status '{attachResult.Status}'.");
            _ = await AppServerControlHostTestClient.ReadUntilMatchAsync(
                process.StandardOutput,
                pendingPatches,
                patch => string.Equals(patch.Patch.Session.State, "ready", StringComparison.Ordinal),
                maxPatches: 8,
                timeout: TimeSpan.FromSeconds(10));

            var attachWindow = await AppServerControlHostTestClient.GetHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                sessionId,
                count: 32);
            Assert.Equal(provider, attachWindow.Provider);
            Assert.Equal("grok-build-0.1", attachWindow.QuickSettings.Model);
            Assert.Contains(attachWindow.QuickSettings.ModelOptions, option => option.Value == "grok-4.3");
            var capture = await WaitForFakeGrokLaunchCaptureAsync(
                fakeGrok.CapturePath,
                static launch => launch.Arguments.Length > 0);
            Assert.Equal(expectedArguments, capture.Arguments);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn",
                SessionId = sessionId,
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text = "Inspect the workspace.",
                    Attachments =
                    [
                        new AppServerControlAttachmentReference
                        {
                            Kind = "image",
                            Path = imagePath,
                            MimeType = "image/png",
                            DisplayName = "pasted-image.png"
                        }
                    ]
                }
            });

            var turnResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn");
            Assert.Equal("accepted", turnResult.Status);
            Assert.Equal(provider, turnResult.TurnStarted!.Provider);

            _ = await AppServerControlHostTestClient.ReadUntilMatchAsync(
                process.StandardOutput,
                pendingPatches,
                patch => string.Equals(patch.Patch.CurrentTurn.State, "completed", StringComparison.Ordinal),
                maxPatches: 40,
                timeout: TimeSpan.FromSeconds(10));

            var turnWindow = await AppServerControlHostTestClient.GetHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                sessionId,
                count: 96);
            Assert.Contains("Fake Grok reply.", turnWindow.Streams.AssistantText, StringComparison.Ordinal);
            Assert.Contains($"[image image/png {imageBytes.Length} bytes]", turnWindow.Streams.AssistantText, StringComparison.Ordinal);
            Assert.Contains("Fake Grok is thinking.", turnWindow.Streams.ReasoningText, StringComparison.Ordinal);
            Assert.Contains(turnWindow.Items, item => item.ItemType == "dynamic_tool_call" && item.Status == "completed");
            Assert.Contains(
                turnWindow.Notices,
                notice => notice.Type == "agent.state" &&
                          notice.Message.Contains("commands available: compact, always-approve.", StringComparison.Ordinal));
            Assert.Contains(
                turnWindow.Notices,
                notice => notice.Type == "agent.state" &&
                          notice.Message.Contains("Fake Grok notification: Session notification handled.", StringComparison.Ordinal));
            Assert.Contains(
                turnWindow.Notices,
                notice => notice.Type == "thread.token-usage.updated" &&
                          notice.Detail?.Contains("1,234 / 128,000 context tokens, 0.0123 USD cumulative", StringComparison.Ordinal) == true);
            Assert.DoesNotContain(
                turnWindow.Notices,
                notice => notice.Message.Contains("ignored", StringComparison.OrdinalIgnoreCase));

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn-2",
                SessionId = sessionId,
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text = "Second turn in the same ACP session.",
                    Attachments = []
                }
            });
            var secondTurnResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn-2");
            Assert.Equal("accepted", secondTurnResult.Status);
            _ = await AppServerControlHostTestClient.ReadUntilMatchAsync(
                process.StandardOutput,
                pendingPatches,
                patch => string.Equals(patch.Patch.CurrentTurn.TurnId, secondTurnResult.TurnStarted!.TurnId, StringComparison.Ordinal) &&
                         string.Equals(patch.Patch.CurrentTurn.State, "completed", StringComparison.Ordinal),
                maxPatches: 40,
                timeout: TimeSpan.FromSeconds(10));
            var secondWindow = await AppServerControlHostTestClient.GetHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                sessionId,
                count: 128);
            Assert.Equal(turnWindow.Thread.ThreadId, secondWindow.Thread.ThreadId);
            Assert.Contains("Second turn in the same ACP session.", secondWindow.Streams.AssistantText, StringComparison.Ordinal);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn-permission",
                SessionId = sessionId,
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text = "Inspect permission handling.",
                    PermissionMode = AppServerControlQuickSettings.PermissionModeManual
                }
            });
            var permissionTurn = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn-permission");
            Assert.Equal("accepted", permissionTurn.Status);
            var approvalWindow = await AppServerControlHostTestClient.WaitForHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                sessionId,
                window => window.Requests.Any(request => request.State == "open"),
                TimeSpan.FromSeconds(10),
                count: 160);
            var approval = Assert.Single(approvalWindow.Requests, request => request.State == "open");
            Assert.Equal("command_execution_approval", approval.Kind);
            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-resolve-permission",
                SessionId = sessionId,
                Type = "request.resolve",
                ResolveRequest = new AppServerControlRequestResolutionCommand
                {
                    RequestId = approval.RequestId,
                    Decision = "accept"
                }
            });
            var permissionResolution = await AppServerControlHostTestClient.ReadResultAsync(
                process.StandardOutput,
                pendingPatches,
                "cmd-resolve-permission");
            Assert.Equal("accepted", permissionResolution.Status);
            var resolvedWindow = await AppServerControlHostTestClient.GetHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                sessionId,
                count: 160);
            Assert.Contains(
                resolvedWindow.Requests,
                request => request.RequestId == approval.RequestId && request.Decision == "accept" && request.State == "resolved");

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn-interrupt",
                SessionId = sessionId,
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest { Text = "Keep running until interrupt." }
            });
            var interruptTurn = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn-interrupt");
            Assert.Equal("accepted", interruptTurn.Status);
            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-interrupt",
                SessionId = sessionId,
                Type = "turn.interrupt",
                InterruptTurn = new AppServerControlInterruptRequest { TurnId = interruptTurn.TurnStarted!.TurnId }
            });
            var interrupted = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-interrupt");
            Assert.Equal("accepted", interrupted.Status);
            var interruptedWindow = await AppServerControlHostTestClient.WaitForHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                sessionId,
                window => string.Equals(window.CurrentTurn.TurnId, interruptTurn.TurnStarted.TurnId, StringComparison.Ordinal) &&
                          string.Equals(window.CurrentTurn.State, "interrupted", StringComparison.Ordinal),
                TimeSpan.FromSeconds(10),
                count: 192);
            Assert.Equal("ready", interruptedWindow.Session.State);
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
    public async Task MtAgentHost_RejectsUnsupportedAcpProtocolVersion()
    {
        var previousVersion = Environment.GetEnvironmentVariable("MIDTERM_FAKE_GROK_PROTOCOL_VERSION");
        Environment.SetEnvironmentVariable("MIDTERM_FAKE_GROK_PROTOCOL_VERSION", "2");
        using var fakeGrok = FakeGrokPathScope.Create();
        using var process = StartAgentHost(ResolveAgentHostDll());
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();
        try
        {
            _ = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach-invalid-protocol",
                SessionId = "session-invalid-acp-protocol",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-invalid-acp-protocol",
                    Provider = "custom-acp",
                    RuntimeKind = "acp-v1",
                    ExecutablePath = fakeGrok.ExecutablePath,
                    AgentName = "Fake ACP agent",
                    WorkingDirectory = fakeGrok.Root
                }
            });

            var result = await AppServerControlHostTestClient.ReadResultAsync(
                process.StandardOutput,
                pendingPatches,
                "cmd-attach-invalid-protocol");

            Assert.Equal("rejected", result.Status);
            Assert.Contains("requires ACP v1", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MIDTERM_FAKE_GROK_PROTOCOL_VERSION", previousVersion);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
        }
    }

    private static string ResolveAgentHostDll()
    {
        var overridePath = Environment.GetEnvironmentVariable("MIDTERM_TEST_AGENTHOST_PATH");
        return !string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)
            ? overridePath
            : MtAgentHostTestPathResolver.ResolveAgentHostDll(AppContext.BaseDirectory);
    }

    private static async Task<FakeGrokLaunchCapture> WaitForFakeGrokLaunchCaptureAsync(
        string capturePath,
        Func<FakeGrokLaunchCapture, bool> predicate)
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
                        var capture = JsonSerializer.Deserialize<FakeGrokLaunchCapture>(json);
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

        throw new TimeoutException($"Timed out waiting for fake Grok launch capture at '{capturePath}'.");
    }

    private static Process StartAgentHost(string hostDll)
    {
        var isNativeExecutable = string.Equals(Path.GetExtension(hostDll), ".exe", StringComparison.OrdinalIgnoreCase);
        var dotnetHost = ResolveDotNetHostPath();
        var startInfo = new ProcessStartInfo
        {
            FileName = isNativeExecutable ? hostDll : dotnetHost,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!isNativeExecutable)
        {
            startInfo.ArgumentList.Add(hostDll);
        }
        startInfo.ArgumentList.Add("--stdio");
        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("Failed to start mtagenthost.");
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

    private sealed class FakeGrokLaunchCapture
    {
        public string[] Arguments { get; set; } = [];
    }
}
