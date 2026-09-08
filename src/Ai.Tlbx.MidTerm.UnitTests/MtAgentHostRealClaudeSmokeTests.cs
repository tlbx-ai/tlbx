using System.Diagnostics;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class MtAgentHostRealClaudeSmokeTests
{
    [Fact]
    [Trait("Category", "RealClaude")]
    public async Task MtAgentHost_CompletesRealClaudeAgentSdkTurn()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MIDTERM_RUN_REAL_CLAUDE_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var claudePath = FindExecutable("claude") ?? throw new InvalidOperationException("Claude Code was not found on PATH.");
        var workdir = Path.Combine(Path.GetTempPath(), "tlbx-real-claude-sdk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workdir);
        using var process = StartAgentHost(MtAgentHostTestPathResolver.ResolveAgentHostDll(AppContext.BaseDirectory));
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();
        var marker = "TLBX_CLAUDE_MEMORY_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var imagePath = Path.Combine(
            FindRepositoryRoot(AppContext.BaseDirectory),
            "src",
            "Ai.Tlbx.MidTerm",
            "src",
            "static",
            "img",
            "logo.png");
        Assert.True(File.Exists(imagePath), $"Claude image fixture was not found: {imagePath}");
        const string sessionId = "session-real-claude-sdk";

        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Contains("claude-agent-sdk", hello.Providers, StringComparer.Ordinal);
            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "attach-real-claude",
                SessionId = sessionId,
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = sessionId,
                    Provider = "claude",
                    RuntimeKind = "claude-agent-sdk",
                    ExecutablePath = claudePath,
                    WorkingDirectory = workdir
                }
            });
            var attach = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "attach-real-claude");
            Assert.Equal("accepted", attach.Status);

            var first = await RunTurnAsync(
                process,
                pendingPatches,
                sessionId,
                "turn-real-claude-memory",
                $"Remember this token for later turns and reply with exactly {marker}. Do not use tools.");
            Assert.Equal(marker, AppServerControlHostTestClient.CollectAssistantText(first).Trim());
            var providerThreadId = first.Thread.ThreadId;
            Assert.False(string.IsNullOrWhiteSpace(providerThreadId));

            var second = await RunTurnAsync(
                process,
                pendingPatches,
                sessionId,
                "turn-real-claude-image",
                $"Inspect the attached image. Choose A for a red bicycle, B for a white outlined toolbox on black with a blue eye and a G-like symbol, or C for a green tree. Reply exactly {marker}| followed by the single correct letter. Do not use tools.",
                [
                    new AppServerControlAttachmentReference
                    {
                        Kind = "image",
                        Path = imagePath,
                        MimeType = "image/png",
                        DisplayName = "tlbx-logo.png"
                    }
                ]);
            Assert.EndsWith($"{marker}|B", AppServerControlHostTestClient.CollectAssistantText(second).Trim(), StringComparison.Ordinal);
            Assert.Equal(providerThreadId, second.Thread.ThreadId);

            var third = await RunTurnAsync(
                process,
                pendingPatches,
                sessionId,
                "turn-real-claude-image-memory",
                "Without receiving the image again, recall the color of the eye detail in the previous image. Reply exactly BLUE. Do not use tools.");
            Assert.EndsWith("BLUE", AppServerControlHostTestClient.CollectAssistantText(third).Trim(), StringComparison.Ordinal);
            Assert.Equal(providerThreadId, third.Thread.ThreadId);
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
        var turn = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, commandId);
        Assert.Equal("accepted", turn.Status);
        var turnId = turn.TurnStarted?.TurnId;
        Assert.False(string.IsNullOrWhiteSpace(turnId));

        AppServerControlHistoryWindowResponse completed;
        try
        {
            completed = await AppServerControlHostTestClient.WaitForHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                sessionId,
                window => string.Equals(window.CurrentTurn.TurnId, turnId, StringComparison.Ordinal) &&
                          window.CurrentTurn.State is "completed" or "failed",
                TimeSpan.FromMinutes(2),
                count: 192);
        }
        catch (TimeoutException ex)
        {
            var diagnostic = await AppServerControlHostTestClient.GetHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                sessionId,
                count: 192);
            var notices = string.Join(" | ", diagnostic.Notices.Select(notice => $"{notice.Type}: {notice.Message} {notice.Detail}"));
            throw new TimeoutException(
                $"{ex.Message} Session={diagnostic.Session.State}; Turn={diagnostic.CurrentTurn.State}; Thread={diagnostic.Thread.ThreadId}; Assistant={AppServerControlHostTestClient.CollectAssistantText(diagnostic)}; Notices={notices}",
                ex);
        }
        Assert.True(
            string.Equals("completed", completed.CurrentTurn.State, StringComparison.Ordinal),
            $"Claude turn ended as '{completed.CurrentTurn.State}'. Session={completed.Session.State}; Error={completed.Session.LastError}; Notices={string.Join(" | ", completed.Notices.Select(notice => $"{notice.Type}: {notice.Message} {notice.Detail}"))}");
        Assert.DoesNotContain(completed.Notices, notice => notice.Type == "runtime.error");
        return completed;
    }

    private static string FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Could not find repository root above '{startDirectory}'.");
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
        var names = OperatingSystem.IsWindows() ? new[] { name + ".exe", name + ".cmd", name + ".bat" } : [name];
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
}
