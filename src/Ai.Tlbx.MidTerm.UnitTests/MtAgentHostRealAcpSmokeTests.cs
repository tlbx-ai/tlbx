using System.Diagnostics;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

[Collection(PathSensitiveEnvironmentCollection.Name)]
public sealed class MtAgentHostRealAcpSmokeTests
{
    [Theory]
    [Trait("Category", "RealAcp")]
    [InlineData("grok", "Grok Build", "grok", true, "agent", "stdio")]
    [InlineData("opencode", "OpenCode", "opencode", false, "acp")]
    [InlineData("gemini", "Gemini CLI", "gemini", true, "--acp")]
    [InlineData("copilot", "GitHub Copilot CLI", "copilot", true, "--acp")]
    public async Task MtAgentHost_CompletesRealAcpMultiTurnImageSession(
        string provider,
        string agentName,
        string executableName,
        bool expectsImageUnderstanding,
        params string[] executableArguments)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MIDTERM_RUN_REAL_ACP_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var executablePath = FindExecutable(executableName)
                             ?? throw new InvalidOperationException($"{agentName} was not found on PATH.");
        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        var imagePath = Path.Combine(repositoryRoot, "src", "Ai.Tlbx.MidTerm", "src", "static", "img", "logo.png");
        Assert.True(File.Exists(imagePath), $"ACP image fixture was not found: {imagePath}");
        var workdir = Path.Combine(Path.GetTempPath(), $"tlbx-real-acp-{provider}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workdir);
        using var process = StartAgentHost(MtAgentHostTestPathResolver.ResolveAgentHostDll(AppContext.BaseDirectory));
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();
        var sessionId = $"session-real-{provider}-" + Guid.NewGuid().ToString("N");
        var marker = $"TLBX_{provider.ToUpperInvariant()}_MEMORY_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Contains("acp-v1", hello.Providers, StringComparer.Ordinal);
            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "attach-real-acp",
                SessionId = sessionId,
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = sessionId,
                    Provider = provider,
                    RuntimeKind = "acp-v1",
                    ExecutablePath = executablePath,
                    ExecutableArguments = executableArguments.ToList(),
                    AgentName = agentName,
                    WorkingDirectory = workdir
                }
            });
            var attach = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "attach-real-acp");
            Assert.True(
                string.Equals("accepted", attach.Status, StringComparison.Ordinal),
                $"{agentName} attach failed: {attach.Message}");

            var first = await RunTurnAsync(
                process,
                pendingPatches,
                sessionId,
                "turn-memory",
                $"Remember this token for the next turn and reply with exactly {marker}. Do not use tools.");
            Assert.Contains(marker, CollectAssistantText(first, first.CurrentTurn.TurnId), StringComparison.Ordinal);
            var providerThreadId = first.Thread.ThreadId;
            Assert.False(string.IsNullOrWhiteSpace(providerThreadId));

            var second = await RunTurnAsync(
                process,
                pendingPatches,
                sessionId,
                "turn-image",
                $"Inspect the attached image. Choose A for a red bicycle, B for a white outlined toolbox on black with a blue eye and a G-like symbol, or C for a green tree. Reply exactly {marker}| followed by the single correct letter.",
                [
                    new AppServerControlAttachmentReference
                    {
                        Kind = "image",
                        Path = imagePath,
                        MimeType = "image/png",
                        DisplayName = "tlbx-logo.png"
                    }
                ]);
            var imageResponse = CollectAssistantText(second, second.CurrentTurn.TurnId);
            Assert.False(string.IsNullOrWhiteSpace(imageResponse));
            if (expectsImageUnderstanding)
            {
                Assert.Contains($"{marker}|B", imageResponse, StringComparison.Ordinal);
            }
            Assert.Equal(providerThreadId, second.Thread.ThreadId);
            Assert.DoesNotContain(second.Notices, notice => notice.Type == "runtime.error");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            try { Directory.Delete(workdir, recursive: true); } catch { }
        }
    }

    private static async Task<AppServerControlHistoryWindowResponse> RunTurnAsync(
        Process process,
        Queue<AppServerControlHostHistoryPatchEnvelope> pendingPatches,
        string sessionId,
        string commandId,
        string prompt,
        List<AppServerControlAttachmentReference>? attachments = null)
    {
        await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
        {
            CommandId = commandId,
            SessionId = sessionId,
            Type = "turn.start",
            StartTurn = new AppServerControlTurnRequest
            {
                Text = prompt,
                Attachments = attachments ?? [],
                PermissionMode = AppServerControlQuickSettings.PermissionModeManual
            }
        });
        var started = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, commandId);
        Assert.Equal("accepted", started.Status);
        var turnId = started.TurnStarted?.TurnId;
        Assert.False(string.IsNullOrWhiteSpace(turnId));

        var resolvedRequests = new HashSet<string>(StringComparer.Ordinal);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var window = await AppServerControlHostTestClient.GetHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                sessionId,
                count: 192);
            foreach (var request in window.Requests.Where(request =>
                         request.State == "open" &&
                         resolvedRequests.Add(request.RequestId)))
            {
                var resolveCommandId = $"resolve-{commandId}-{resolvedRequests.Count}";
                await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
                {
                    CommandId = resolveCommandId,
                    SessionId = sessionId,
                    Type = "request.resolve",
                    ResolveRequest = new AppServerControlRequestResolutionCommand
                    {
                        RequestId = request.RequestId,
                        Decision = "accept"
                    }
                });
                var resolved = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, resolveCommandId);
                Assert.Equal("accepted", resolved.Status);
            }

            if (string.Equals(window.CurrentTurn.TurnId, turnId, StringComparison.Ordinal) &&
                window.CurrentTurn.State is "completed" or "failed" or "interrupted")
            {
                Assert.True(
                    string.Equals(window.CurrentTurn.State, "completed", StringComparison.Ordinal),
                    $"ACP turn ended as '{window.CurrentTurn.State}'. Session={window.Session.State}; Error={window.Session.LastError}; Notices={string.Join(" | ", window.Notices.Select(notice => $"{notice.Type}: {notice.Message} {notice.Detail}"))}");
                return window;
            }

            await Task.Delay(250);
        }

        var diagnostic = await AppServerControlHostTestClient.GetHistoryWindowAsync(
            process.StandardOutput,
            process.StandardInput,
            pendingPatches,
            sessionId,
            count: 192);
        throw new TimeoutException(
            $"ACP turn '{commandId}' timed out. Session={diagnostic.Session.State}; Turn={diagnostic.CurrentTurn.State}; Assistant={CollectAssistantText(diagnostic, turnId)}; Notices={string.Join(" | ", diagnostic.Notices.Select(notice => $"{notice.Type}: {notice.Message} {notice.Detail}"))}");
    }

    private static string CollectAssistantText(AppServerControlHistoryWindowResponse window, string? turnId)
    {
        return string.Join(
            "\n",
            window.History
                .Where(entry => string.Equals(entry.Kind, "assistant", StringComparison.Ordinal) &&
                                string.Equals(entry.TurnId, turnId, StringComparison.Ordinal))
                .Select(entry => entry.Body));
    }

    private static Process StartAgentHost(string hostDll)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(hostDll);
        startInfo.ArgumentList.Add("--stdio");
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start mtagenthost.");
    }

    private static string? FindExecutable(string name)
    {
        var names = OperatingSystem.IsWindows()
            ? new[] { name + ".exe", name + ".cmd", name + ".bat", name }
            : [name];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidateName in names)
            {
                var candidate = Path.Combine(directory.Trim('"'), candidateName);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static string FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Could not find repository root above '{startDirectory}'.");
    }
}
