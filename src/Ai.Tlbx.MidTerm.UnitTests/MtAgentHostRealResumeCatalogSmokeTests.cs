using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed partial class MtAgentHostRealResumeCatalogSmokeTests
{
    private const string ResumeProbeEnvVar = "MIDTERM_RUN_REAL_RESUME_PROBES";
    private static readonly TimeSpan ActiveCodexSessionCooldown = TimeSpan.FromMinutes(10);
    private readonly ITestOutputHelper _output;

    public MtAgentHostRealResumeCatalogSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Category", "RealCodex")]
    [Trait("Category", "ResumeProbe")]
    public async Task MtAgentHost_CanResumeRealCodexConversationFromLiveLocalHistory()
    {
        if (!IsRealCodexResumeProbeEnabled())
        {
            return;
        }

        var workingDirectory = ResolveRepoRoot();
        var candidate = TryFindCodexCandidate(workingDirectory);
        if (candidate is null)
        {
            return;
        }

        _output.WriteLine($"Codex resume candidate: {candidate.SessionId} ({candidate.SourcePath})");
        _output.WriteLine($"Codex probe expects prior user message: {candidate.RecentUserMessage}");

        var hostDll = ResolveAgentHostDll();
        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();

        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Contains("codex", hello.Providers, StringComparer.Ordinal);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach-real-codex-live-resume",
                SessionId = "session-real-codex-live-resume",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "session-real-codex-live-resume",
                    Provider = "codex",
                    WorkingDirectory = candidate.WorkingDirectory,
                    ResumeThreadId = candidate.SessionId
                }
            });

            var attachResult = await AppServerControlHostTestClient.ReadResultAsync(
                process.StandardOutput,
                pendingPatches,
                "cmd-attach-real-codex-live-resume");
            Assert.Equal("accepted", attachResult.Status);

            var attachWindow = await WaitForReadyWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-real-codex-live-resume");
            LogWindow("codex attach", attachWindow);
            Assert.Equal(candidate.SessionId, attachWindow.Thread.ThreadId);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn-real-codex-live-resume",
                SessionId = "session-real-codex-live-resume",
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text =
                        """
                        Reply with exactly the most recent direct user message in this conversation before this turn.
                        Collapse any internal whitespace to single spaces.
                        Reply with that message only.
                        """,
                    Attachments = []
                }
            });

            var turnResult = await AppServerControlHostTestClient.ReadResultAsync(
                process.StandardOutput,
                pendingPatches,
                "cmd-turn-real-codex-live-resume");
            Assert.Equal("accepted", turnResult.Status);

            var turnWindow = await WaitForTurnStateWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                "session-real-codex-live-resume",
                "completed");
            LogWindow("codex turn", turnWindow);

            var assistantText = AppServerControlHostTestClient.CollectAssistantText(turnWindow);
            _output.WriteLine($"Codex resumed assistant text: {NormalizeMessage(assistantText)}");
            AssertNormalizedMessageMatch(candidate.RecentUserMessage, assistantText);
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

    private static ResumeProbeCandidate? TryFindCodexCandidate(string workingDirectory)
    {
        var targetDirectory = NormalizePath(workingDirectory);
        var cutoff = DateTime.UtcNow - ActiveCodexSessionCooldown;
        var sessionsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
        if (!Directory.Exists(sessionsRoot))
        {
            return null;
        }

        foreach (var path in Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories)
                     .Select(static path => new FileInfo(path))
                     .Where(file => file.LastWriteTimeUtc <= cutoff)
                     .OrderByDescending(static file => file.LastWriteTimeUtc)
                     .Take(400)
                     .Select(static file => file.FullName))
        {
            if (!TryReadCodexSessionMeta(path, out var sessionId, out var cwd) ||
                string.IsNullOrWhiteSpace(cwd) ||
                !string.Equals(NormalizePath(cwd), targetDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var recentUserMessage = TryReadLastCodexDirectUserMessage(path);
            if (IsUsableResumeProbeMessage(recentUserMessage))
            {
                return new ResumeProbeCandidate(sessionId!, cwd, NormalizeMessage(recentUserMessage!), path);
            }
        }

        return null;
    }

    private static bool TryReadCodexSessionMeta(string path, out string? sessionId, out string? cwd)
    {
        sessionId = null;
        cwd = null;

        using var reader = OpenSharedReaderOrNull(path);
        if (reader is null)
        {
            return false;
        }

        try
        {
            var firstLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(firstLine))
            {
                return false;
            }

            using var json = JsonDocument.Parse(firstLine);
            var root = json.RootElement;
            if (!string.Equals(root.GetProperty("type").GetString(), "session_meta", StringComparison.Ordinal))
            {
                return false;
            }

            var payload = root.GetProperty("payload");
            sessionId = payload.GetProperty("id").GetString();
            cwd = payload.GetProperty("cwd").GetString();
            return !string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(cwd);
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadLastCodexDirectUserMessage(string path)
    {
        using var reader = OpenSharedReaderOrNull(path);
        if (reader is null)
        {
            return null;
        }

        string? lastDirectMessage = null;

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (!TryGetString(root, "type", out var rootType) ||
                    !string.Equals(rootType, "response_item", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!root.TryGetProperty("payload", out var payload) ||
                    payload.ValueKind != JsonValueKind.Object ||
                    !TryGetString(payload, "type", out var payloadType) ||
                    !string.Equals(payloadType, "message", StringComparison.Ordinal) ||
                    !TryGetString(payload, "role", out var role) ||
                    !string.Equals(role, "user", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                for (var index = 0; index < content.GetArrayLength(); index++)
                {
                    var item = content[index];
                    if (item.ValueKind != JsonValueKind.Object ||
                        !TryGetString(item, "type", out var itemType) ||
                        !string.Equals(itemType, "input_text", StringComparison.Ordinal) ||
                        !TryGetString(item, "text", out var text))
                    {
                        continue;
                    }

                    if (IsUsableResumeProbeMessage(text))
                    {
                        lastDirectMessage = text;
                    }
                }
            }
            catch
            {
            }
        }

        return lastDirectMessage;
    }

    private static bool IsUsableResumeProbeMessage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = NormalizeMessage(text);
        if (normalized.Length < 8 || normalized.Length > 180)
        {
            return false;
        }

        if (normalized.Contains("AGENTS.md instructions", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("<environment_context>", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("[Request interrupted", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Reply with exactly the most recent direct user message", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var words = Tokenize(normalized);
        return words.Count is >= 2 and <= 24;
    }

    private static void AssertNormalizedMessageMatch(string expected, string actual)
    {
        var normalizedExpected = NormalizeMessage(expected);
        var normalizedActual = NormalizeMessage(actual)
            .Trim('"')
            .Trim('\'')
            .Trim('`');

        Assert.Equal(normalizedExpected, normalizedActual);
    }

    private static List<string> Tokenize(string text)
    {
        return NonWhitespaceRegex().Matches(text)
            .Select(static match => match.Value)
            .ToList();
    }

    private static string NormalizeMessage(string text)
    {
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static StreamReader? OpenSharedReaderOrNull(string path)
    {
        try
        {
            var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(stream);
        }
        catch
        {
            return null;
        }
    }

    private async Task<AppServerControlHistoryWindowResponse> WaitForReadyWindowAsync(
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
            TimeSpan.FromSeconds(20),
            count: 240);
    }

    private async Task<AppServerControlHistoryWindowResponse> WaitForTurnStateWindowAsync(
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
            TimeSpan.FromSeconds(90),
            count: 320);
    }

    private void LogWindow(string label, AppServerControlHistoryWindowResponse window)
    {
        _output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0}: session={1} thread={2} turn={3} history={4}/{5} requests={6} notices={7}",
            label,
            window.Session.State,
            window.Thread.ThreadId,
            window.CurrentTurn.State,
            window.History.Count,
            window.HistoryCount,
            window.Requests.Count,
            window.Notices.Count));
    }

    private static bool IsRealCodexResumeProbeEnabled()
    {
        return IsProbeEnabled() && ResolveCodexOnPath() is not null;
    }

    private static bool IsProbeEnabled()
    {
        return string.Equals(Environment.GetEnvironmentVariable(ResumeProbeEnvVar), "1", StringComparison.Ordinal);
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

    private static string ResolveAgentHostDll()
    {
        return MtAgentHostTestPathResolver.ResolveAgentHostDll(AppContext.BaseDirectory);
    }

    private static string ResolveRepoRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    [GeneratedRegex(@"\S+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex NonWhitespaceRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex WhitespaceRegex();

    private sealed record ResumeProbeCandidate(
        string SessionId,
        string WorkingDirectory,
        string RecentUserMessage,
        string SourcePath);
}
