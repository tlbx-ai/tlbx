using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class MtAgentHostRealCodexSmokeTests
{
    private static readonly HttpClient ReadyClient = new();

    [Fact]
    [Trait("Category", "RealCodex")]
    public async Task MtAgentHost_CanAttachAndCompleteRealCodexTurn()
    {
        if (!IsRealCodexSmokeEnabled())
        {
            return;
        }

        var hostDll = ResolveAgentHostDll();
        var workdir = Path.Combine(Path.GetTempPath(), "midterm-real-codex-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workdir);

        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();
        var marker = "MIDTERM_REAL_CODEX_SMOKE_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Equal(AppServerControlHostProtocol.CurrentVersion, hello.ProtocolVersion);
            Assert.Contains("codex", hello.Providers, StringComparer.Ordinal);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach-real",
                SessionId = "session-real-codex",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-real-codex",
                    Provider = "codex",
                    WorkingDirectory = workdir
                }
            });

            var attachResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach-real");
            Assert.Equal("accepted", attachResult.Status);

            var readyWindow = await WaitForReadyWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-real-codex");
            var selectedModel = readyWindow.QuickSettings.Model;
            Assert.False(string.IsNullOrWhiteSpace(selectedModel));
            Assert.Contains(
                readyWindow.QuickSettings.ModelOptions,
                option => option.IsDefault && string.Equals(option.Value, selectedModel, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("medium", readyWindow.QuickSettings.Effort);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn-real",
                SessionId = "session-real-codex",
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text = $"Reply with exactly {marker}:<selected-model> and nothing else. Replace <selected-model> with the model selected for this turn in the authoritative tlbx runtime context.",
                    Attachments = []
                }
            });

            var turnResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn-real");
            Assert.Equal("accepted", turnResult.Status);
            Assert.NotNull(turnResult.TurnStarted);

            var turnWindow = await WaitForTurnStateWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-real-codex",
                "completed");
            Assert.Equal(selectedModel, turnWindow.CurrentTurn.Model);
            Assert.Equal("medium", turnWindow.CurrentTurn.Effort);
            Assert.Contains(
                $"{marker}:{selectedModel}",
                AppServerControlHostTestClient.CollectAssistantText(turnWindow),
                StringComparison.OrdinalIgnoreCase);
            Assert.True(
                turnWindow.History.Any(item => item.Streaming || item.ItemType is not null),
                "Expected canonical history items in the completed real Codex turn.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            try
            {
                Directory.Delete(workdir, recursive: true);
            }
            catch
            {
                // Ignore temp-dir cleanup failures in the smoke test.
            }
        }
    }

    [Fact]
    [Trait("Category", "RealCodex")]
    public async Task MtAgentHost_CanAttachToRealCodexWebSocketAppServer()
    {
        if (!IsRealCodexSmokeEnabled())
        {
            return;
        }

        var hostDll = ResolveAgentHostDll();
        var workdir = Path.Combine(Path.GetTempPath(), "midterm-real-codex-remote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workdir);
        var port = GetFreePort();
        var appServerEndpoint = string.Create(CultureInfo.InvariantCulture, $"ws://127.0.0.1:{port}");
        using var appServer = StartCodexAppServer(appServerEndpoint);
        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();
        var marker = "MIDTERM_REAL_CODEX_REMOTE_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        try
        {
            await WaitForCodexAppServerReadyAsync(port);

            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Equal(AppServerControlHostProtocol.CurrentVersion, hello.ProtocolVersion);
            Assert.Contains("codex", hello.Providers, StringComparer.Ordinal);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach-real-remote",
                SessionId = "session-real-codex-remote",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-real-codex-remote",
                    Provider = "codex",
                    WorkingDirectory = workdir,
                    AttachPoint = new SessionAgentAttachPoint
                    {
                        Provider = SessionAgentAttachPoint.CodexProvider,
                        TransportKind = SessionAgentAttachPoint.CodexAppServerWebSocketTransport,
                        Endpoint = appServerEndpoint,
                        SharedRuntime = true,
                        Source = "real-smoke"
                    }
                }
            });

            var attachResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach-real-remote");
            Assert.Equal("accepted", attachResult.Status);

            _ = await WaitForReadyWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-real-codex-remote");

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn-real-remote",
                SessionId = "session-real-codex-remote",
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text = $"Reply with exactly {marker} and nothing else.",
                    Attachments = []
                }
            });

            var turnResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn-real-remote");
            Assert.Equal("accepted", turnResult.Status);

            var turnWindow = await WaitForTurnStateWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-real-codex-remote",
                "completed");
            Assert.Contains(marker, AppServerControlHostTestClient.CollectAssistantText(turnWindow), StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            if (!appServer.HasExited)
            {
                appServer.Kill(entireProcessTree: true);
            }

            _ = await process.StandardError.ReadToEndAsync();
            _ = await appServer.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await appServer.WaitForExitAsync();

            try
            {
                Directory.Delete(workdir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    [Trait("Category", "RealCodex")]
    public async Task MtAgentHost_CanDriveRealCodexRichWorkspaceTurn()
    {
        if (!IsRealCodexSmokeEnabled())
        {
            return;
        }

        var hostDll = ResolveAgentHostDll();
        var workdir = Path.Combine(Path.GetTempPath(), "midterm-real-codex-rich-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workdir);
        var marker = "MIDTERM_REAL_CODEX_RICH_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        await File.WriteAllTextAsync(
            Path.Combine(workdir, "inventory.csv"),
            """
            name,count,owner
            alpha,3,Ada
            beta,5,Linus
            gamma,8,Grace
            """,
            Encoding.UTF8);
        await File.WriteAllTextAsync(
            Path.Combine(workdir, "report.md"),
            """
            # Workspace report

            status: TODO
            """,
            Encoding.UTF8);
        await InitializeGitWorkspaceAsync(workdir);

        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();

        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Equal(AppServerControlHostProtocol.CurrentVersion, hello.ProtocolVersion);
            Assert.Contains("codex", hello.Providers, StringComparer.Ordinal);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach-real-rich",
                SessionId = "session-real-codex-rich",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-real-codex-rich",
                    Provider = "codex",
                    WorkingDirectory = workdir
                }
            });

            var attachResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach-real-rich");
            Assert.Equal("accepted", attachResult.Status);

            _ = await WaitForReadyWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-real-codex-rich");

            var richPrompt = $$"""
            You are inside a temporary git repository.

            Complete this in one turn and use tools instead of only describing work:
            1. Inspect the workspace files.
            2. Update `report.md` by replacing `TODO` with `DONE` and append a new line `marker: {{marker}}`.
            3. Run a shell command that shows the diff for `report.md`.
            4. In your final assistant message include:
               - a short Plan section,
               - a markdown table summarizing the rows from `inventory.csv`,
               - the exact marker `{{marker}}`.

            Do not ask follow-up questions.
            """;

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn-real-rich",
                SessionId = "session-real-codex-rich",
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text = richPrompt,
                    Attachments = []
                }
            });

            var turnResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn-real-rich");
            Assert.Equal("accepted", turnResult.Status);
            Assert.NotNull(turnResult.TurnStarted);

            var turnWindow = await WaitForTurnStateWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-real-codex-rich",
                "completed");
            Assert.False(string.IsNullOrWhiteSpace(turnWindow.Streams.CommandOutput) &&
                         string.IsNullOrWhiteSpace(turnWindow.Streams.FileChangeOutput));
            Assert.False(string.IsNullOrWhiteSpace(turnWindow.Streams.UnifiedDiff));
            Assert.Contains(turnWindow.History, item => item.ItemType is "command_execution" or "file_change");

            var reportText = await File.ReadAllTextAsync(Path.Combine(workdir, "report.md"), Encoding.UTF8);
            Assert.Contains("DONE", reportText, StringComparison.Ordinal);
            Assert.Contains(marker, reportText, StringComparison.Ordinal);

            var assistantText = AppServerControlHostTestClient.CollectAssistantText(turnWindow);
            Assert.Contains(marker, assistantText, StringComparison.Ordinal);
            Assert.Contains("alpha", assistantText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("|", assistantText, StringComparison.Ordinal);
            Assert.True(
                assistantText.Contains("Plan", StringComparison.OrdinalIgnoreCase) ||
                (assistantText.Contains("1.", StringComparison.Ordinal) && assistantText.Contains("2.", StringComparison.Ordinal)),
                $"Expected a short plan in the assistant stream, but got:{Environment.NewLine}{assistantText}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            try
            {
                Directory.Delete(workdir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    [Trait("Category", "RealCodex")]
    public async Task MtAgentHost_CanHandleRealCodexQuestionThenFollowUpTurn()
    {
        if (!IsRealCodexSmokeEnabled())
        {
            return;
        }

        var hostDll = ResolveAgentHostDll();
        var workdir = Path.Combine(Path.GetTempPath(), "midterm-real-codex-question-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workdir);
        var marker = "MIDTERM_REAL_CODEX_QUESTION_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        await File.WriteAllTextAsync(
            Path.Combine(workdir, "mode.txt"),
            """
            selected-mode=pending
            """,
            Encoding.UTF8);
        await File.WriteAllTextAsync(
            Path.Combine(workdir, "instructions.txt"),
            """
            The operator must choose SAFE or FAST. Do not infer it.
            """,
            Encoding.UTF8);

        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();

        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Equal(AppServerControlHostProtocol.CurrentVersion, hello.ProtocolVersion);
            Assert.Contains("codex", hello.Providers, StringComparer.Ordinal);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach-real-question",
                SessionId = "session-real-codex-question",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-real-codex-question",
                    Provider = "codex",
                    WorkingDirectory = workdir
                }
            });

            var attachResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach-real-question");
            Assert.Equal("accepted", attachResult.Status);

            _ = await WaitForReadyWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-real-codex-question");

            var questionPrompt = """
            You are in a temporary workspace.

            Before touching any files, ask me exactly one question with the options SAFE and FAST.
            Do not infer the answer and do not continue without asking first.
            """;

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn-real-question",
                SessionId = "session-real-codex-question",
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text = questionPrompt,
                    Attachments = []
                }
            });

            var turnResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn-real-question");
            Assert.Equal("accepted", turnResult.Status);
            Assert.NotNull(turnResult.TurnStarted);

            var questionWindow = await WaitForTurnStateWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-real-codex-question",
                "completed");

            var questionAssistantText = AppServerControlHostTestClient.CollectAssistantText(questionWindow);
            Assert.Contains("SAFE", questionAssistantText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("FAST", questionAssistantText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("?", questionAssistantText, StringComparison.Ordinal);

            var unchangedModeText = await File.ReadAllTextAsync(Path.Combine(workdir, "mode.txt"), Encoding.UTF8);
            Assert.Contains("selected-mode=pending", unchangedModeText, StringComparison.OrdinalIgnoreCase);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn-real-follow-up",
                SessionId = "session-real-codex-question",
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text = $$"""
                    SAFE. Continue the task now.

                    Append `selected-mode=safe {{marker}}` to `mode.txt`.
                    In your final assistant response include a short two-step plan and the exact marker `{{marker}}`.
                    """,
                    Attachments = []
                }
            });

            var followUpResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn-real-follow-up");
            Assert.Equal("accepted", followUpResult.Status);
            Assert.NotNull(followUpResult.TurnStarted);

            var completionWindow = await WaitForTurnStateWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-real-codex-question",
                "completed");
            Assert.Contains(completionWindow.History, item => item.ItemType is "file_change" or "command_execution");

            var modeText = await File.ReadAllTextAsync(Path.Combine(workdir, "mode.txt"), Encoding.UTF8);
            Assert.Contains($"selected-mode=safe {marker}", modeText, StringComparison.OrdinalIgnoreCase);

            var assistantText = AppServerControlHostTestClient.CollectAssistantText(completionWindow);
            Assert.Contains(marker, assistantText, StringComparison.Ordinal);
            Assert.Contains("safe", assistantText, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                assistantText.Contains("Plan", StringComparison.OrdinalIgnoreCase) ||
                (assistantText.Contains("1.", StringComparison.Ordinal) && assistantText.Contains("2.", StringComparison.Ordinal)),
                $"Expected a short plan in the assistant stream, but got:{Environment.NewLine}{assistantText}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            try
            {
                Directory.Delete(workdir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static bool IsRealCodexSmokeEnabled()
    {
        var enabled = Environment.GetEnvironmentVariable("MIDTERM_RUN_REAL_CODEX_TESTS");
        if (!string.Equals(enabled, "1", StringComparison.Ordinal))
        {
            return false;
        }

        if (ResolveCodexOnPath() is null)
        {
            return false;
        }

        return true;
    }

    private static string? ResolveCodexOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cmd = Path.Combine(entry, "codex.cmd");
            if (File.Exists(cmd))
            {
                return cmd;
            }

            var exe = Path.Combine(entry, "codex.exe");
            if (File.Exists(exe))
            {
                return exe;
            }

            var bare = Path.Combine(entry, "codex");
            if (File.Exists(bare))
            {
                return bare;
            }
        }

        return null;
    }

    private static Process StartAgentHost(string hostDll)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
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

    private static Process StartCodexAppServer(string endpoint)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "pwsh" : ResolveCodexOnPath() ?? "codex",
                Arguments = OperatingSystem.IsWindows()
                    ? $"-NoProfile -Command \"codex app-server --listen {endpoint}\""
                    : $"app-server --listen {endpoint}",
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        return process;
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
            window => string.Equals(window.Session.State, "ready", StringComparison.Ordinal),
            TimeSpan.FromSeconds(20),
            count: 160);
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
            TimeSpan.FromSeconds(120),
            count: 320);
    }

    private static async Task WaitForCodexAppServerReadyAsync(int port)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            cts.Token.ThrowIfCancellationRequested();
            try
            {
                var readyUrl = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/readyz");
                using var response = await ReadyClient.GetAsync(readyUrl, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(200, cts.Token);
        }
    }

    private static async Task InitializeGitWorkspaceAsync(string workdir)
    {
        await RunProcessAsync("git", "init", workdir);
        await RunProcessAsync("git", "config user.name \"MidTerm Smoke\"", workdir);
        await RunProcessAsync("git", "config user.email \"midterm-smoke@example.invalid\"", workdir);
        await RunProcessAsync("git", "add .", workdir);
        await RunProcessAsync("git", "commit -m \"initial fixture\"", workdir);
    }

    private static async Task RunProcessAsync(string fileName, string arguments, string workingDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.True(
            process.ExitCode == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Command '{fileName} {arguments}' failed in '{workingDirectory}' with exit code {process.ExitCode}.{Environment.NewLine}STDOUT:{Environment.NewLine}{stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{stderr}"));
    }

    private static string ResolveAgentHostDll()
    {
        return MtAgentHostTestPathResolver.ResolveAgentHostDll(AppContext.BaseDirectory);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
