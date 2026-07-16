using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models.Sessions;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed class SessionCodexHandoffService
{
    private static readonly TimeSpan ForegroundTransitionTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ForegroundPollInterval = TimeSpan.FromMilliseconds(100);
    private readonly ConcurrentDictionary<string, string> _resumeThreadIds = new(StringComparer.Ordinal);
    private readonly TtyHostSessionManager _sessionManager;
    private readonly WorkerSessionRegistryService _workerRegistry;
    private readonly AiCliProfileService _profileService;
    private readonly SessionForegroundProcessService _foregroundProcessService;
    private readonly SessionAppServerControlRuntimeService _appServerControlRuntime;
    private readonly string _codexHome;

    public SessionCodexHandoffService(
        TtyHostSessionManager sessionManager,
        WorkerSessionRegistryService workerRegistry,
        AiCliProfileService profileService,
        SessionForegroundProcessService foregroundProcessService,
        SessionAppServerControlRuntimeService appServerControlRuntime,
        string? codexHome = null)
    {
        _sessionManager = sessionManager;
        _workerRegistry = workerRegistry;
        _profileService = profileService;
        _foregroundProcessService = foregroundProcessService;
        _appServerControlRuntime = appServerControlRuntime;
        _codexHome = string.IsNullOrWhiteSpace(codexHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : codexHome;
        _sessionManager.OnSessionClosed += Forget;
    }

    public async Task<string?> PrepareForAppServerControlAsync(SessionInfoDto session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!LooksLikeCodexForeground(session))
        {
            return TryGetKnownResumeThreadId(session.Id, out var knownResumeThreadId)
                ? knownResumeThreadId
                : null;
        }

        if (session.Supervisor?.State is SessionSupervisorService.BusyTurnState or SessionSupervisorService.BlockedState)
        {
            throw new InvalidOperationException("Finish or interrupt the terminal Codex turn before opening App Server Controller.");
        }

        var resumeThreadId = await ResolveResumeThreadIdAsync(session, ct).ConfigureAwait(false);
        RememberResumeThreadId(session.Id, resumeThreadId);

        if (session.ForegroundPid is not int foregroundPid || foregroundPid <= 0)
        {
            throw new InvalidOperationException("The terminal Codex process has no foreground pid to hand off.");
        }

        KillProcessTree(foregroundPid);
        await WaitForShellAsync(session.Id, session.ShellType, ct).ConfigureAwait(false);
        return resumeThreadId;
    }

    public async Task RestoreTerminalAsync(SessionInfoDto session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (LooksLikeCodexForeground(session))
        {
            var commandLineResumeThreadId = TryExtractResumeThreadId(session.ForegroundCommandLine);
            if (!string.IsNullOrWhiteSpace(commandLineResumeThreadId))
            {
                RememberResumeThreadId(session.Id, commandLineResumeThreadId);
            }

            await _appServerControlRuntime.DetachAsync(session.Id, ct).ConfigureAwait(false);
            return;
        }

        _appServerControlRuntime.TryGetCachedHistoryWindow(session.Id, out var historyWindow);
        if (historyWindow?.CurrentTurn?.State is "running")
        {
            throw new InvalidOperationException("Finish the App Server Controller turn before returning control to the terminal.");
        }

        if (historyWindow?.Requests.Any(static request => string.Equals(request.State, "open", StringComparison.OrdinalIgnoreCase)) == true)
        {
            throw new InvalidOperationException("Resolve the open App Server Controller request before returning control to the terminal.");
        }

        var resumeThreadId = historyWindow?.Thread.ThreadId;
        if (string.IsNullOrWhiteSpace(resumeThreadId) && !TryGetKnownResumeThreadId(session.Id, out resumeThreadId))
        {
            resumeThreadId = await ResolveResumeThreadIdAsync(session, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(resumeThreadId))
        {
            throw new InvalidOperationException("tlbx could not determine the Codex resume id for this session.");
        }

        RememberResumeThreadId(session.Id, resumeThreadId);
        await _appServerControlRuntime.DetachAsync(session.Id, ct).ConfigureAwait(false);
        await WaitForShellAsync(session.Id, session.ShellType, ct).ConfigureAwait(false);

        var launchCommand = BuildResumeLaunchCommand(
            _workerRegistry.TryGet(session.Id, out var registration)
                ? registration.LaunchCommand
                : _profileService.GetDefaultLaunchCommand(AiCliProfileService.CodexProfile),
            resumeThreadId);
        await _sessionManager.SendInputAsync(session.Id, Encoding.UTF8.GetBytes(launchCommand + "\r"), ct).ConfigureAwait(false);
        await WaitForCodexForegroundAsync(session.Id, ct).ConfigureAwait(false);
    }

    public bool TryGetKnownResumeThreadId(string sessionId, out string resumeThreadId)
    {
        if (_resumeThreadIds.TryGetValue(sessionId, out resumeThreadId!))
        {
            return true;
        }

        resumeThreadId = string.Empty;
        return false;
    }

    public void RememberResumeThreadId(string sessionId, string resumeThreadId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(resumeThreadId))
        {
            return;
        }

        _resumeThreadIds[sessionId] = resumeThreadId.Trim();
    }

    public void Forget(string sessionId)
    {
        _resumeThreadIds.TryRemove(sessionId, out _);
    }

    internal static string BuildResumeLaunchCommand(string? launchCommand, string resumeThreadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resumeThreadId);

        var baseCommand = string.IsNullOrWhiteSpace(launchCommand)
            ? "codex --yolo"
            : launchCommand.Trim();
        return $"{baseCommand} resume {resumeThreadId}";
    }

    internal static string? TryExtractResumeThreadId(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var tokens = Tokenize(commandLine);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!string.Equals(tokens[i], "resume", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            for (var j = i + 1; j < tokens.Count; j++)
            {
                var candidate = tokens[j];
                if (candidate.StartsWith("-", StringComparison.Ordinal))
                {
                    continue;
                }

                return candidate;
            }
        }

        return null;
    }

    internal async Task<string> ResolveResumeThreadIdAsync(SessionInfoDto session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!string.IsNullOrWhiteSpace(session.AgentAttachPoint?.PreferredThreadId))
        {
            return session.AgentAttachPoint.PreferredThreadId!;
        }

        _appServerControlRuntime.TryGetCachedHistoryWindow(session.Id, out var historyWindow);
        var appServerControlSnapshotThreadId = historyWindow?.Thread.ThreadId;
        if (!string.IsNullOrWhiteSpace(appServerControlSnapshotThreadId))
        {
            RememberResumeThreadId(session.Id, appServerControlSnapshotThreadId);
            return appServerControlSnapshotThreadId;
        }

        var commandLineResumeThreadId = TryExtractResumeThreadId(session.ForegroundCommandLine);
        if (!string.IsNullOrWhiteSpace(commandLineResumeThreadId))
        {
            return commandLineResumeThreadId;
        }

        if (TryGetKnownResumeThreadId(session.Id, out var knownResumeThreadId))
        {
            return knownResumeThreadId;
        }

        var resolvedFromDisk = await TryResolveResumeThreadIdFromDiskAsync(session, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(resolvedFromDisk))
        {
            return resolvedFromDisk;
        }

        throw new InvalidOperationException("tlbx could not determine the Codex resume id for this session.");
    }

    private async Task<string?> TryResolveResumeThreadIdFromDiskAsync(SessionInfoDto session, CancellationToken ct)
    {
        var sessionsRoot = Path.Combine(_codexHome, "sessions");
        if (string.IsNullOrWhiteSpace(session.CurrentDirectory) || !Directory.Exists(sessionsRoot))
        {
            return null;
        }

        var createdAtUtc = session.CreatedAt.Kind == DateTimeKind.Utc
            ? session.CreatedAt
            : session.CreatedAt.ToUniversalTime();
        var candidates = new List<(string SessionId, DateTimeOffset Timestamp)>();
        foreach (var file in Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories)
                     .Select(static path => new FileInfo(path))
                     .OrderByDescending(static file => file.LastWriteTimeUtc)
                     .Take(80))
        {
            ct.ThrowIfCancellationRequested();

            var candidate = await TryReadSessionMetaAsync(file.FullName, ct).ConfigureAwait(false);
            if (candidate is null)
            {
                continue;
            }

            if (!string.Equals(candidate.Cwd, session.CurrentDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var activityTimestamp = candidate.Timestamp > file.LastWriteTimeUtc
                ? candidate.Timestamp
                : new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            if (activityTimestamp.UtcDateTime < createdAtUtc.AddMinutes(-2))
            {
                continue;
            }

            candidates.Add((candidate.SessionId, activityTimestamp));
            if (candidates.Count >= 3)
            {
                break;
            }
        }

        return candidates.Count == 1
            ? candidates[0].SessionId
            : null;
    }

    private static async Task<CodexSessionMetaCandidate?> TryReadSessionMetaAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            var firstLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(firstLine))
            {
                return null;
            }

            using var json = JsonDocument.Parse(firstLine);
            var root = json.RootElement;
            if (!string.Equals(root.GetProperty("type").GetString(), "session_meta", StringComparison.Ordinal))
            {
                return null;
            }

            var payload = root.GetProperty("payload");
            var sessionId = payload.GetProperty("id").GetString();
            var cwd = payload.GetProperty("cwd").GetString();
            var timestampText = payload.GetProperty("timestamp").GetString();
            if (string.IsNullOrWhiteSpace(sessionId) ||
                string.IsNullOrWhiteSpace(cwd) ||
                !DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
            {
                return null;
            }

            return new CodexSessionMetaCandidate(sessionId!, cwd!, timestamp);
        }
        catch
        {
            return null;
        }
    }

    private async Task WaitForShellAsync(string sessionId, string? shellType, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + ForegroundTransitionTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var session = _sessionManager.GetSession(sessionId);
            if (session is null)
            {
                throw new InvalidOperationException("Terminal session disappeared during Codex handoff.");
            }

            if (IsShellForeground(session.ForegroundName, shellType))
            {
                return;
            }

            await Task.Delay(ForegroundPollInterval, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Terminal shell did not recover after stopping Codex.");
    }

    private async Task WaitForCodexForegroundAsync(string sessionId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + ForegroundTransitionTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var session = _sessionManager.GetSession(sessionId);
            if (session is null)
            {
                throw new InvalidOperationException("Terminal session disappeared while restoring Codex.");
            }

            if (_foregroundProcessService.HasIdentity(
                    session.ForegroundName,
                    session.ForegroundCommandLine,
                    session.AgentAttachPoint,
                    AiCliProfileService.CodexProfile))
            {
                return;
            }

            await Task.Delay(ForegroundPollInterval, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Codex did not return to the terminal after leaving App Server Controller.");
    }

    internal static bool LooksLikeCodexForeground(SessionInfoDto session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return string.Equals(session.ForegroundProcessIdentity, AiCliProfileService.CodexProfile, StringComparison.Ordinal);
    }

    internal static bool IsShellForeground(string? foregroundName, string? shellType)
    {
        var shell = NormalizeExecutableIdentity(shellType);
        if (string.IsNullOrWhiteSpace(shell))
        {
            return string.IsNullOrWhiteSpace(foregroundName) ||
                   string.Equals(NormalizeExecutableIdentity(foregroundName), "shell", StringComparison.Ordinal);
        }

        var normalizedForegroundName = NormalizeExecutableIdentity(foregroundName);
        return string.Equals(normalizedForegroundName, shell, StringComparison.Ordinal) ||
               string.Equals(normalizedForegroundName, "shell", StringComparison.Ordinal);
    }

    private static string NormalizeExecutableIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var candidate = value.Trim();
        var firstChar = candidate[0];
        if ((firstChar == '"' || firstChar == '\'') && candidate.Length > 1)
        {
            var closingQuote = candidate.IndexOf(firstChar, 1);
            if (closingQuote > 1)
            {
                candidate = candidate[1..closingQuote];
            }
        }

        candidate = candidate.Replace('\\', '/');
        var basename = candidate.Split('/').LastOrDefault() ?? candidate;
        var token = basename.Trim().Split(' ', '\t').FirstOrDefault() ?? basename.Trim();
        return token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? token[..^4].ToLowerInvariant()
            : token.ToLowerInvariant();
    }

    private static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char? quote = null;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var ch = commandLine[i];
            if (quote is not null)
            {
                if (ch == quote.Value)
                {
                    quote = null;
                    continue;
                }

                if (ch == '\\' &&
                    i + 1 < commandLine.Length &&
                    commandLine[i + 1] == quote.Value)
                {
                    current.Append(commandLine[i + 1]);
                    i++;
                    continue;
                }

                current.Append(ch);
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                FlushCurrent(tokens, current);
                continue;
            }

            current.Append(ch);
        }

        FlushCurrent(tokens, current);
        return tokens;
    }

    private static void FlushCurrent(List<string> tokens, StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        tokens.Add(current.ToString());
        current.Clear();
    }

    private static void KillProcessTree(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process may have already exited.
        }
    }

    private sealed record CodexSessionMetaCandidate(
        string SessionId,
        string Cwd,
        DateTimeOffset Timestamp);
}
