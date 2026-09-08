using System.Diagnostics;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class MtAgentHostIntegrationTests
{
    private const string LegacyV1HostProtocolVersion = "app-server-control-host-v1";

    [Fact]
    public async Task SyntheticMtAgentHost_StreamsTypedAppServerControlProtocolOverStdio()
    {
        var hostDll = ResolveAgentHostDll();
        Assert.True(File.Exists(hostDll), $"Expected mtagenthost build output at '{hostDll}'.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{hostDll}\" --stdio --synthetic codex",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();

        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Equal(AppServerControlHostProtocol.CurrentVersion, hello.ProtocolVersion);
            Assert.Contains("codex", hello.Providers, StringComparer.Ordinal);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach",
                SessionId = "session-1",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-1",
                    Provider = "codex",
                    WorkingDirectory = "Q:\\repos\\MidtermJpa"
                }
            });

            var attachResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach");
            Assert.Equal("cmd-attach", attachResult.CommandId);
            Assert.Equal("accepted", attachResult.Status);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-git-set",
                SessionId = "session-1",
                Type = "session.git-metadata.set",
                GitMetadata = new AppServerControlHostGitMetadata
                {
                    Repos =
                    [
                        new TtyHostGitRepoMetadata
                        {
                            RepoRoot = @"Q:\repos\tlbx",
                            Label = "tlbx",
                            Role = "target",
                            Source = "manual"
                        }
                    ]
                }
            });
            var gitSetResult = await AppServerControlHostTestClient.ReadResultAsync(
                process.StandardOutput,
                pendingPatches,
                "cmd-git-set");
            Assert.Equal(@"Q:\repos\tlbx", Assert.Single(gitSetResult.GitMetadata!.Repos).RepoRoot);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-git-get",
                SessionId = "session-1",
                Type = "session.git-metadata.get"
            });
            var gitGetResult = await AppServerControlHostTestClient.ReadResultAsync(
                process.StandardOutput,
                pendingPatches,
                "cmd-git-get");
            Assert.Equal("tlbx", Assert.Single(gitGetResult.GitMetadata!.Repos).Label);

            _ = await AppServerControlHostTestClient.ReadUntilMatchAsync(
                process.StandardOutput,
                pendingPatches,
                patch => string.Equals(patch.Patch.Session.State, "ready", StringComparison.Ordinal) &&
                         !string.IsNullOrWhiteSpace(patch.Patch.Thread.ThreadId),
                maxPatches: 4,
                timeout: TimeSpan.FromSeconds(10));

            var attachWindow = await AppServerControlHostTestClient.GetHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-1",
                count: 16);
            Assert.Equal("ready", attachWindow.Session.State);
            Assert.Equal("active", attachWindow.Thread.State);
            Assert.False(string.IsNullOrWhiteSpace(attachWindow.Thread.ThreadId));

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn",
                SessionId = "session-1",
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text = "Inspect the repo and propose a patch.",
                    Attachments = []
                }
            });

            var turnResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn");
            Assert.NotNull(turnResult.TurnStarted);
            Assert.Equal("accepted", turnResult.TurnStarted!.Status);

            _ = await AppServerControlHostTestClient.ReadUntilMatchAsync(
                process.StandardOutput,
                pendingPatches,
                patch => patch.Patch.RequestUpserts.Count > 0,
                maxPatches: 12,
                timeout: TimeSpan.FromSeconds(10));

            var turnWindow = await AppServerControlHostTestClient.GetHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-1",
                count: 64);
            Assert.Contains(turnWindow.History, item => item.ItemType == "user_message");
            Assert.Contains("Synthetic codex reply", turnWindow.Streams.AssistantText, StringComparison.Ordinal);
            Assert.Contains("Inspecting workspace", turnWindow.Streams.ReasoningText, StringComparison.Ordinal);
            Assert.Contains("1. Read the repo.", turnWindow.Streams.PlanText, StringComparison.Ordinal);
            Assert.Contains("--- a/file.txt", turnWindow.Streams.UnifiedDiff, StringComparison.Ordinal);
            var request = Assert.Single(turnWindow.Requests);
            Assert.Equal("approval", request.Kind);
            Assert.Equal("open", request.State);

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

            var resolveWindow = await AppServerControlHostTestClient.GetHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-1",
                count: 64);
            Assert.Equal("running", resolveWindow.CurrentTurn.State);
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
    public async Task SyntheticMtAgentHost_RejectsLegacyV1HostProtocol()
    {
        var hostDll = ResolveAgentHostDll();
        Assert.True(File.Exists(hostDll), $"Expected mtagenthost build output at '{hostDll}'.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{hostDll}\" --stdio --synthetic codex",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();

        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Equal("app-server-control-host-v3", hello.ProtocolVersion);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                ProtocolVersion = LegacyV1HostProtocolVersion,
                CommandId = "cmd-legacy-v1",
                SessionId = "session-legacy-v1",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-legacy-v1",
                    Provider = "codex",
                    WorkingDirectory = "Q:\\repos\\MidtermJpa"
                }
            });

            var result = await AppServerControlHostTestClient.ReadResultAsync(
                process.StandardOutput,
                pendingPatches,
                "cmd-legacy-v1");

            Assert.Equal("cmd-legacy-v1", result.CommandId);
            Assert.Equal("session-legacy-v1", result.SessionId);
            Assert.Equal("rejected", result.Status);
            Assert.Contains("Unsupported protocol version", result.Message, StringComparison.Ordinal);
            Assert.Contains(LegacyV1HostProtocolVersion, result.Message, StringComparison.Ordinal);
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

    private static string ResolveAgentHostDll()
    {
        return MtAgentHostTestPathResolver.ResolveAgentHostDll(AppContext.BaseDirectory);
    }
}
