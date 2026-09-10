using System.Diagnostics;
using System.Globalization;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

[Collection(PathSensitiveEnvironmentCollection.Name)]
public sealed class MtAgentHostClaudeIntegrationTests
{
    [Fact]
    public async Task MtAgentHost_DrivesClaudeAgentSdkAcrossApprovedMultiTurnSession()
    {
        using var fakeClaude = FakeClaudePathScope.Create();
        using var process = StartAgentHost(ResolveAgentHostPath());
        var sessionId = "session-claude-sdk-" + Guid.NewGuid().ToString("N");
        var resumeThreadId = Guid.NewGuid().ToString();
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();
        var imagePath = Path.Combine(fakeClaude.Root, "pasted-image.png");
        var imageBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        await File.WriteAllBytesAsync(imagePath, imageBytes);
        string? lastApprovalRequestId = null;

        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Contains("claude-agent-sdk", hello.Providers, StringComparer.Ordinal);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "attach",
                SessionId = sessionId,
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = sessionId,
                    Provider = "claude",
                    RuntimeKind = "claude-agent-sdk",
                    ExecutablePath = fakeClaude.ExecutablePath,
                    WorkingDirectory = fakeClaude.Root,
                    ResumeThreadId = resumeThreadId
                }
            });
            var attach = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "attach");
            Assert.Equal("accepted", attach.Status);

            var providerThreadId = string.Empty;
            for (var turnNumber = 1; turnNumber <= 2; turnNumber++)
            {
                var turnNumberText = turnNumber.ToString(CultureInfo.InvariantCulture);
                var commandId = $"turn-{turnNumberText}";
                await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
                {
                    CommandId = commandId,
                    SessionId = sessionId,
                    Type = "turn.start",
                    StartTurn = new AppServerControlTurnRequest
                    {
                        Text = $"Inspect turn {turnNumberText}.",
                        Attachments = turnNumber == 1
                            ?
                            [
                                new AppServerControlAttachmentReference
                                {
                                    Kind = "image",
                                    Path = imagePath,
                                    MimeType = "image/png",
                                    DisplayName = "pasted-image.png"
                                }
                            ]
                            : [],
                        PermissionMode = AppServerControlQuickSettings.PermissionModeManual
                    }
                });
                var started = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, commandId);
                Assert.Equal("accepted", started.Status);

                var approvalWindow = await AppServerControlHostTestClient.WaitForHistoryWindowAsync(
                    process.StandardOutput,
                    process.StandardInput,
                    pendingPatches,
                    sessionId,
                    window => window.Requests.Any(request => request.State == "open"),
                    TimeSpan.FromSeconds(20),
                    count: 128);
                var approval = Assert.Single(approvalWindow.Requests, request => request.State == "open");
                Assert.Equal("tool_approval", approval.Kind);
                lastApprovalRequestId = approval.RequestId;

                await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
                {
                    CommandId = $"approve-{turnNumberText}",
                    SessionId = sessionId,
                    Type = "request.resolve",
                    ResolveRequest = new AppServerControlRequestResolutionCommand
                    {
                        RequestId = approval.RequestId,
                        Decision = "accept"
                    }
                });
                var resolved = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, $"approve-{turnNumberText}");
                Assert.Equal("accepted", resolved.Status);

                var completed = await AppServerControlHostTestClient.WaitForHistoryWindowAsync(
                    process.StandardOutput,
                    process.StandardInput,
                    pendingPatches,
                    sessionId,
                    window => window.CurrentTurn.State == "completed",
                    TimeSpan.FromSeconds(20),
                    count: 192);
                Assert.Contains($"Fake Claude SDK reply {turnNumberText}", AppServerControlHostTestClient.CollectAssistantText(completed), StringComparison.Ordinal);
                if (turnNumber == 1)
                {
                    Assert.Contains(
                        $"[image image/png {imageBytes.Length.ToString(CultureInfo.InvariantCulture)} bytes]",
                        AppServerControlHostTestClient.CollectAssistantText(completed),
                        StringComparison.Ordinal);
                }
                Assert.Contains(completed.Requests, request => request.RequestId == approval.RequestId && request.Decision == "accept");
                if (turnNumber == 1)
                {
                    providerThreadId = completed.Thread.ThreadId;
                    Assert.Equal(resumeThreadId, providerThreadId);
                }
                else Assert.Equal(providerThreadId, completed.Thread.ThreadId);
            }

            Assert.False(string.IsNullOrWhiteSpace(lastApprovalRequestId));
            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "approve-already-resolved",
                SessionId = sessionId,
                Type = "request.resolve",
                ResolveRequest = new AppServerControlRequestResolutionCommand
                {
                    RequestId = lastApprovalRequestId,
                    Decision = "accept"
                }
            });
            var duplicateResolution = await AppServerControlHostTestClient.ReadResultAsync(
                process.StandardOutput,
                pendingPatches,
                "approve-already-resolved");
            Assert.Equal("rejected", duplicateResolution.Status);
            Assert.Contains("was not found", duplicateResolution.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
        }
    }

    private static Process StartAgentHost(string hostDll)
    {
        var isNativeExecutable = string.Equals(Path.GetExtension(hostDll), ".exe", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = isNativeExecutable ? hostDll : "dotnet",
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
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start mtagenthost.");
    }

    private static string ResolveAgentHostPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("MIDTERM_TEST_AGENTHOST_PATH");
        return !string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)
            ? overridePath
            : MtAgentHostTestPathResolver.ResolveAgentHostDll(AppContext.BaseDirectory);
    }
}
