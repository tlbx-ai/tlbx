using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models.Git;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Services.Git;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed partial class SessionUpdateStateService
{
    private const int ResumeHintScrollbackBytes = 256 * 1024;
    private const int ResumeHintTailLineCount = 8;
    private readonly string _statePath;

    public SessionUpdateStateService(SettingsService settingsService)
        : this(settingsService.SettingsDirectory)
    {
    }

    public SessionUpdateStateService(string settingsDirectory)
    {
        _statePath = Path.Combine(settingsDirectory, "state.json");
    }

    public async Task CaptureAsync(
        TtyHostSessionManager sessionManager,
        GitWatcherService gitWatcher,
        bool fullUpdate,
        bool tryResumeNonAiAgentProcesses,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(gitWatcher);

        var decorations = CaptureSessionDecorations(sessionManager, gitWatcher);
        var state = new SessionUpdateState
        {
            SavedAt = DateTimeOffset.UtcNow,
            Kind = fullUpdate ? "full" : "web",
            Sessions = decorations
        };

        if (fullUpdate)
        {
            foreach (var decoration in decorations.OrderBy(static item => item.Order))
            {
                var command = await TryBuildResumeCommandAsync(
                    sessionManager,
                    decoration,
                    tryResumeNonAiAgentProcesses,
                    ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(command))
                {
                    continue;
                }

                state.PendingResumeSessions.Add(new SessionResumeIntent
                {
                    OriginalSessionId = decoration.SessionId,
                    Command = command,
                    ShellType = decoration.ShellType,
                    WorkingDirectory = decoration.CurrentDirectory,
                    Cols = decoration.Cols,
                    Rows = decoration.Rows,
                    Decoration = decoration
                });
            }
        }

        await PersistAsync(state, ct).ConfigureAwait(false);

        if (fullUpdate)
        {
            foreach (var sessionId in decorations.Select(static item => item.SessionId))
            {
                try
                {
                    await sessionManager.CloseSessionAsync(sessionId, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn(() => $"Failed to close session {sessionId} before full update: {ex.Message}");
                }
            }
        }
    }

    public async Task RestoreAsync(
        TtyHostSessionManager sessionManager,
        GitWatcherService gitWatcher,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(gitWatcher);

        var state = await LoadAsync(ct).ConfigureAwait(false);
        if (state is null)
        {
            return;
        }

        await RestoreDecorationsAsync(sessionManager, gitWatcher, state.Sessions, ct)
            .ConfigureAwait(false);
        var result = IsFullUpdateState(state)
            ? await RestoreFullUpdateSessionsAsync(sessionManager, gitWatcher, state, ct).ConfigureAwait(false)
            : RestoreUpdateStateResult.Success;
        if (result.FailedOriginalSessionIds.Count > 0)
        {
            PreserveFailedRestoreState(state, result.FailedOriginalSessionIds);
            await PersistAsync(state, ct).ConfigureAwait(false);
            return;
        }

        DeleteStateFile();
    }

    internal static string? TryBuildResumeCommand(
        string? terminalText,
        string? foregroundCommandLine,
        string? foregroundName,
        bool tryResumeNonAiAgentProcesses)
    {
        var resumeHint = TryFindAiResumeHint(terminalText);
        if (resumeHint is not null)
        {
            var preservedFlags = PreserveResumeFlags(foregroundCommandLine);
            return BuildCommand([resumeHint.Provider, ..preservedFlags, resumeHint.ResumeArgument, resumeHint.ThreadId]);
        }

        if (!tryResumeNonAiAgentProcesses ||
            string.IsNullOrWhiteSpace(foregroundCommandLine) ||
            IsShellProcess(foregroundName, foregroundCommandLine))
        {
            return null;
        }

        return foregroundCommandLine.Trim();
    }

    private static List<SessionDecorationState> CaptureSessionDecorations(
        TtyHostSessionManager sessionManager,
        GitWatcherService gitWatcher)
    {
        var visibleById = sessionManager.GetSessionList(includeHidden: true).Sessions
            .ToDictionary(static session => session.Id, StringComparer.Ordinal);
        var result = new List<SessionDecorationState>();

        foreach (var session in sessionManager.GetAllSessions())
        {
            visibleById.TryGetValue(session.Id, out var dto);
            var bindings = MergeExtraGitRepos(
                gitWatcher.GetRepoBindings(session.Id),
                sessionManager.GetPersistedSessionExtraGitRepos(session.Id));

            result.Add(new SessionDecorationState
            {
                SessionId = session.Id,
                ShellType = session.ShellType,
                Cols = session.Cols,
                Rows = session.Rows,
                CurrentDirectory = dto?.CurrentDirectory ?? session.CurrentDirectory,
                Name = dto?.Name ?? session.Name,
                TerminalTitle = dto?.TerminalTitle ?? session.TerminalTitle,
                Topic = dto?.Topic,
                Notes = dto?.Notes,
                ManuallyNamed = dto?.ManuallyNamed ?? session.ManuallyNamed,
                Order = dto?.Order ?? int.MaxValue,
                Hidden = sessionManager.IsHidden(session.Id),
                BookmarkId = dto?.BookmarkId,
                SpaceId = dto?.SpaceId,
                WorkspacePath = dto?.WorkspacePath,
                Surface = dto?.Surface,
                AgentControlled = dto?.AgentControlled ?? false,
                AppServerControlOnly = dto?.AppServerControlOnly ?? false,
                ProfileHint = dto?.ProfileHint,
                AppServerControlResumeThreadId = dto?.AppServerControlResumeThreadId,
                ForegroundName = dto?.ForegroundName ?? session.ForegroundName,
                ForegroundCommandLine = dto?.ForegroundCommandLine ?? session.ForegroundCommandLine,
                ForegroundDisplayName = dto?.ForegroundDisplayName ?? session.ForegroundDisplayName,
                ForegroundProcessIdentity = dto?.ForegroundProcessIdentity ?? session.ForegroundProcessIdentity,
                ExtraGitRepos = bindings
            });
        }

        return result;
    }

    internal static List<TtyHostGitRepoMetadata> MergeExtraGitRepos(
        IEnumerable<GitRepoBinding> liveBindings,
        IEnumerable<TtyHostGitRepoMetadata> persistedRepos)
    {
        var result = new List<TtyHostGitRepoMetadata>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var repo in liveBindings)
        {
            if (repo.IsPrimary || string.IsNullOrWhiteSpace(repo.RepoRoot))
            {
                continue;
            }

            Add(repo.RepoRoot, repo.Label, repo.Role, repo.Source);
        }

        foreach (var repo in persistedRepos)
        {
            if (string.IsNullOrWhiteSpace(repo.RepoRoot))
            {
                continue;
            }

            Add(repo.RepoRoot, repo.Label, repo.Role, repo.Source);
        }

        return result;

        void Add(string repoRoot, string? label, string? role, string? source)
        {
            var normalizedRoot = NormalizeRepoRoot(repoRoot);
            if (normalizedRoot is null || !seen.Add(normalizedRoot))
            {
                return;
            }

            result.Add(new TtyHostGitRepoMetadata
            {
                RepoRoot = normalizedRoot,
                Label = label,
                Role = role,
                Source = source
            });
        }
    }

    private static string? NormalizeRepoRoot(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(repoRoot.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return repoRoot.Trim();
        }
    }

    private static async Task<string?> TryBuildResumeCommandAsync(
        TtyHostSessionManager sessionManager,
        SessionDecorationState decoration,
        bool tryResumeNonAiAgentProcesses,
        CancellationToken ct)
    {
        var terminalText = string.Empty;
        try
        {
            var buffer = await sessionManager.GetBufferAsync(
                decoration.SessionId,
                ResumeHintScrollbackBytes,
                TerminalReplayReason.Manual,
                ct: ct).ConfigureAwait(false);
            if (buffer is not null)
            {
                terminalText = Encoding.UTF8.GetString(buffer.Data);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Failed to read resume scrollback for {decoration.SessionId}: {ex.Message}");
        }

        return TryBuildResumeCommand(
            terminalText,
            decoration.ForegroundCommandLine,
            decoration.ForegroundName,
            tryResumeNonAiAgentProcesses);
    }

    private static async Task RestoreDecorationsAsync(
        TtyHostSessionManager sessionManager,
        GitWatcherService gitWatcher,
        IEnumerable<SessionDecorationState> decorations,
        CancellationToken ct)
    {
        var byId = decorations.ToDictionary(static item => item.SessionId, StringComparer.Ordinal);
        foreach (var session in sessionManager.GetAllSessions())
        {
            if (!byId.TryGetValue(session.Id, out var decoration))
            {
                continue;
            }

            await ApplyDecorationAsync(sessionManager, gitWatcher, session.Id, decoration, ct)
                .ConfigureAwait(false);
        }
    }

    private async Task<RestoreUpdateStateResult> RestoreFullUpdateSessionsAsync(
        TtyHostSessionManager sessionManager,
        GitWatcherService gitWatcher,
        SessionUpdateState state,
        CancellationToken ct)
    {
        var liveSessionIds = sessionManager.GetAllSessions()
            .Select(static session => session.Id)
            .ToHashSet(StringComparer.Ordinal);
        var pendingByOriginalId = state.PendingResumeSessions
            .Where(static item => !string.IsNullOrWhiteSpace(item.OriginalSessionId))
            .GroupBy(static item => item.OriginalSessionId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var decorations = BuildFullUpdateRestoreDecorations(state, pendingByOriginalId);
        var recreatedOrderBySessionId = new Dictionary<string, int>(StringComparer.Ordinal);
        var failedOriginalSessionIds = new List<string>();

        foreach (var decoration in decorations.OrderBy(static item => item.Order))
        {
            if (liveSessionIds.Contains(decoration.SessionId))
            {
                continue;
            }

            pendingByOriginalId.TryGetValue(decoration.SessionId, out var intent);
            var created = await sessionManager.CreateSessionDetailedAsync(
                intent?.ShellType ?? decoration.ShellType,
                ResolveCols(intent, decoration),
                ResolveRows(intent, decoration),
                intent?.WorkingDirectory ?? decoration.CurrentDirectory,
                ct).ConfigureAwait(false);

            if (created.Session is null)
            {
                failedOriginalSessionIds.Add(decoration.SessionId);
                Log.Warn(() => $"Failed to recreate session {decoration.SessionId} after full update: {created.Failure?.Message}");
                continue;
            }

            await ApplyDecorationAsync(sessionManager, gitWatcher, created.Session.Id, decoration, ct)
                .ConfigureAwait(false);

            recreatedOrderBySessionId[created.Session.Id] = decoration.Order;

            if (!string.IsNullOrWhiteSpace(intent?.Command))
            {
                try
                {
                    await sessionManager.SendInputAsync(
                        created.Session.Id,
                        Encoding.UTF8.GetBytes(intent.Command + "\r"),
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn(() => $"Failed to send resume command for restored session {decoration.SessionId}: {ex.Message}");
                }
            }
        }

        if (recreatedOrderBySessionId.Count > 0)
        {
            sessionManager.ReorderSessions(
                sessionManager.GetSessionList(includeHidden: true).Sessions
                    .OrderBy(session => recreatedOrderBySessionId.TryGetValue(session.Id, out var order) ? order : session.Order)
                    .Select(static session => session.Id)
                    .ToList());
        }

        state.RestoredAt = DateTimeOffset.UtcNow;
        Log.Info(() => $"Restored full-update session state: recreated={recreatedOrderBySessionId.Count}, failed={failedOriginalSessionIds.Count}");
        return new RestoreUpdateStateResult(failedOriginalSessionIds);
    }

    private static bool IsFullUpdateState(SessionUpdateState state)
    {
        return string.Equals(state.Kind, "full", StringComparison.OrdinalIgnoreCase)
            || state.PendingResumeSessions.Count > 0;
    }

    internal static List<SessionDecorationState> BuildFullUpdateRestoreDecorations(
        SessionUpdateState state,
        IReadOnlyDictionary<string, SessionResumeIntent> pendingByOriginalId)
    {
        var result = state.Sessions
            .Where(item => !string.IsNullOrWhiteSpace(item.SessionId) && HasEnoughStateToRecreate(item, pendingByOriginalId))
            .GroupBy(static item => item.SessionId, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToList();
        var seen = result.Select(static item => item.SessionId).ToHashSet(StringComparer.Ordinal);

        foreach (var intent in pendingByOriginalId.Values)
        {
            if (seen.Contains(intent.OriginalSessionId))
            {
                continue;
            }

            result.Add(intent.Decoration ?? new SessionDecorationState
            {
                SessionId = intent.OriginalSessionId,
                ShellType = intent.ShellType ?? "",
                Cols = intent.Cols,
                Rows = intent.Rows,
                CurrentDirectory = intent.WorkingDirectory
            });
            seen.Add(intent.OriginalSessionId);
        }

        return result;
    }

    private static bool HasEnoughStateToRecreate(
        SessionDecorationState decoration,
        IReadOnlyDictionary<string, SessionResumeIntent> pendingByOriginalId)
    {
        if (pendingByOriginalId.ContainsKey(decoration.SessionId))
        {
            return true;
        }

        if (!decoration.AppServerControlOnly)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(decoration.ProfileHint)
            && !string.IsNullOrWhiteSpace(decoration.CurrentDirectory);
    }

    private static int ResolveCols(SessionResumeIntent? intent, SessionDecorationState decoration)
    {
        return FirstPositive(intent?.Cols, decoration.Cols, 120);
    }

    private static int ResolveRows(SessionResumeIntent? intent, SessionDecorationState decoration)
    {
        return FirstPositive(intent?.Rows, decoration.Rows, 30);
    }

    private static int FirstPositive(params int?[] values)
    {
        foreach (var value in values)
        {
            if (value.GetValueOrDefault() > 0)
            {
                return value.GetValueOrDefault();
            }
        }

        return 1;
    }

    private static void PreserveFailedRestoreState(
        SessionUpdateState state,
        IReadOnlyCollection<string> failedOriginalSessionIds)
    {
        var failed = failedOriginalSessionIds.ToHashSet(StringComparer.Ordinal);
        state.Sessions = state.Sessions
            .Where(session => failed.Contains(session.SessionId))
            .ToList();
        state.PendingResumeSessions = state.PendingResumeSessions
            .Where(intent => failed.Contains(intent.OriginalSessionId))
            .ToList();
        state.RestoredAt = DateTimeOffset.UtcNow;
        Log.Warn(() => $"Keeping update session state for retry; failedRestoreSessions={failed.Count}");
    }

    private static async Task ApplyDecorationAsync(
        TtyHostSessionManager sessionManager,
        GitWatcherService gitWatcher,
        string targetSessionId,
        SessionDecorationState decoration,
        CancellationToken ct)
    {
        if (decoration.ManuallyNamed || !string.IsNullOrWhiteSpace(decoration.Name))
        {
            await sessionManager.SetSessionNameAsync(targetSessionId, decoration.Name, decoration.ManuallyNamed, ct)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(decoration.Topic))
        {
            sessionManager.SetSessionTopic(targetSessionId, decoration.Topic);
        }

        if (!string.IsNullOrWhiteSpace(decoration.TerminalTitle))
        {
            await sessionManager.SetSessionNameAsync(targetSessionId, decoration.TerminalTitle, isManual: false, ct)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(decoration.Notes))
        {
            sessionManager.SetSessionNotes(targetSessionId, decoration.Notes);
        }

        if (!string.IsNullOrWhiteSpace(decoration.BookmarkId))
        {
            sessionManager.SetBookmarkId(targetSessionId, decoration.BookmarkId);
        }

        sessionManager.SetAgentControlled(targetSessionId, decoration.AgentControlled);
        sessionManager.SetAppServerControlOnly(targetSessionId, decoration.AppServerControlOnly);
        sessionManager.SetProfileHint(targetSessionId, decoration.ProfileHint);
        sessionManager.SetAppServerControlResumeThreadId(targetSessionId, decoration.AppServerControlResumeThreadId);
        sessionManager.SetSpaceId(targetSessionId, decoration.SpaceId);
        sessionManager.SetWorkspacePath(targetSessionId, decoration.WorkspacePath);
        sessionManager.SetSurface(targetSessionId, decoration.Surface);

        if (decoration.Hidden)
        {
            sessionManager.MarkHidden(targetSessionId);
        }

        if (decoration.ExtraGitRepos.Count > 0)
        {
            await gitWatcher.RestoreSessionExtraReposAsync(targetSessionId, decoration.ExtraGitRepos)
                .ConfigureAwait(false);
            sessionManager.SetSessionExtraGitReposMetadata(
                targetSessionId,
                decoration.ExtraGitRepos.Select(static repo => new GitRepoBinding
                {
                    RepoRoot = repo.RepoRoot,
                    Label = repo.Label ?? Path.GetFileName(repo.RepoRoot),
                    Role = repo.Role ?? "target",
                    Source = repo.Source ?? "manual",
                    IsPrimary = false
                }));
        }
    }

    private async Task<SessionUpdateState?> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_statePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_statePath);
            return await JsonSerializer.DeserializeAsync(
                stream,
                SessionUpdateStateJsonContext.Default.SessionUpdateState,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Failed to load update session state: {ex.Message}");
            return null;
        }
    }

    private Task PersistAsync(SessionUpdateState state, CancellationToken ct)
    {
        return PersistStateAsync(_statePath, state, ct);
    }

    private void DeleteStateFile()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                File.Delete(_statePath);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Failed to delete restored update session state: {ex.Message}");
        }
    }

    private static async Task PersistStateAsync(string? statePath, SessionUpdateState state, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(statePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(statePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(statePath);
        await JsonSerializer.SerializeAsync(
            stream,
            state,
            SessionUpdateStateJsonContext.Default.SessionUpdateState,
            ct).ConfigureAwait(false);
    }

    private static AiResumeHint? TryFindAiResumeHint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var tailHint = TryFindAiResumeHintInText(GetTailLines(text, ResumeHintTailLineCount));
        if (tailHint is not null)
        {
            return tailHint;
        }

        return TryFindAiResumeHintInText(text);
    }

    private static AiResumeHint? TryFindAiResumeHintInText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var matches = AiResumeHintRegex().Matches(text);
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var match = matches[i];
            var provider = match.Groups["provider"].Value.Trim();
            var resumeArgument = match.Groups["resumeArg"].Value.Trim();
            var threadId = match.Groups["threadId"].Value.Trim().Trim('\'', '"');
            if (!string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(threadId))
            {
                return new AiResumeHint(provider, resumeArgument, threadId);
            }
        }

        return null;
    }

    private static string GetTailLines(string text, int lineCount)
    {
        if (lineCount <= 0 || string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var remaining = lineCount;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            remaining--;
            if (remaining <= 0)
            {
                return text[(i + 1)..];
            }
        }

        return text;
    }

    private static string[] PreserveResumeFlags(string? commandLine)
    {
        var tokens = TokenizeCommandLine(commandLine);
        if (tokens.Count == 0)
        {
            return [];
        }

        var result = new List<string>();
        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (ShouldSkipResumeToken(token))
            {
                if (OptionConsumesNext(token) && i + 1 < tokens.Count && !tokens[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    i++;
                }
                continue;
            }

            if (!ShouldPreserveFlag(token))
            {
                continue;
            }

            result.Add(token);
            if (OptionConsumesNext(token) && i + 1 < tokens.Count)
            {
                result.Add(tokens[++i]);
            }
        }

        return result.ToArray();
    }

    private static bool ShouldSkipResumeToken(string token)
    {
        return string.Equals(token, "resume", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "--resume", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "app-server", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "--listen", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "--remote", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("--listen=", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("--remote=", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldPreserveFlag(string token)
    {
        if (!token.StartsWith("-", StringComparison.Ordinal))
        {
            return false;
        }

        var name = token.Split('=', 2)[0];
        return name is "--yolo"
            or "--dangerously-skip-permissions"
            or "--dangerously-bypass-approvals-and-sandbox"
            or "--model"
            or "-m"
            or "--sandbox"
            or "--approval-policy"
            or "--approval-mode"
            or "--config"
            or "--profile"
            or "--cwd"
            or "--cd";
    }

    private static bool OptionConsumesNext(string token)
    {
        if (token.Contains('=', StringComparison.Ordinal))
        {
            return false;
        }

        return token is "--model"
            or "-m"
            or "--sandbox"
            or "--approval-policy"
            or "--approval-mode"
            or "--config"
            or "--profile"
            or "--cwd"
            or "--cd"
            or "--listen"
            or "--remote";
    }

    private static bool IsShellProcess(string? foregroundName, string commandLine)
    {
        var firstToken = TokenizeCommandLine(commandLine).FirstOrDefault();
        var candidate = Path.GetFileNameWithoutExtension(
            string.IsNullOrWhiteSpace(foregroundName) ? firstToken : foregroundName);
        return candidate is not null &&
            (candidate.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
             || candidate.Equals("powershell", StringComparison.OrdinalIgnoreCase)
             || candidate.Equals("cmd", StringComparison.OrdinalIgnoreCase)
             || candidate.Equals("bash", StringComparison.OrdinalIgnoreCase)
             || candidate.Equals("zsh", StringComparison.OrdinalIgnoreCase)
             || candidate.Equals("sh", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildCommand(IEnumerable<string> tokens)
    {
        return string.Join(" ", tokens.Where(static token => !string.IsNullOrWhiteSpace(token)).Select(QuoteToken));
    }

    private static string QuoteToken(string token)
    {
        return token.Any(char.IsWhiteSpace)
            ? "\"" + token.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : token;
    }

    private static List<string> TokenizeCommandLine(string? commandLine)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return tokens;
        }

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

                if (ch == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] == quote.Value)
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
                FlushToken(tokens, current);
                continue;
            }

            current.Append(ch);
        }

        FlushToken(tokens, current);
        return tokens;
    }

    private static void FlushToken(List<string> tokens, StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        tokens.Add(current.ToString());
        current.Clear();
    }

    [GeneratedRegex(@"\b(?<provider>codex|claude|grok)(?:\.exe)?\s+(?<resumeArg>--?resume|resume)\s+(?<threadId>[A-Za-z0-9._:-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex AiResumeHintRegex();

    private sealed record AiResumeHint(string Provider, string ResumeArgument, string ThreadId);

    private sealed record RestoreUpdateStateResult(IReadOnlyList<string> FailedOriginalSessionIds)
    {
        public static RestoreUpdateStateResult Success { get; } = new([]);
    }
}

public sealed class SessionUpdateState
{
    public DateTimeOffset SavedAt { get; set; }
    public DateTimeOffset? RestoredAt { get; set; }
    public string Kind { get; set; } = "";
    public List<SessionDecorationState> Sessions { get; set; } = [];
    public List<SessionResumeIntent> PendingResumeSessions { get; set; } = [];
}

public sealed class SessionDecorationState
{
    public string SessionId { get; set; } = "";
    public string ShellType { get; set; } = "";
    public int Cols { get; set; }
    public int Rows { get; set; }
    public string? CurrentDirectory { get; set; }
    public string? Name { get; set; }
    public string? TerminalTitle { get; set; }
    public string? Topic { get; set; }
    public string? Notes { get; set; }
    public bool ManuallyNamed { get; set; }
    public int Order { get; set; } = int.MaxValue;
    public bool Hidden { get; set; }
    public string? BookmarkId { get; set; }
    public string? SpaceId { get; set; }
    public string? WorkspacePath { get; set; }
    public string? Surface { get; set; }
    public bool AgentControlled { get; set; }
    public bool AppServerControlOnly { get; set; }
    public string? ProfileHint { get; set; }
    public string? AppServerControlResumeThreadId { get; set; }
    public string? ForegroundName { get; set; }
    public string? ForegroundCommandLine { get; set; }
    public string? ForegroundDisplayName { get; set; }
    public string? ForegroundProcessIdentity { get; set; }
    public List<TtyHostGitRepoMetadata> ExtraGitRepos { get; set; } = [];
}

public sealed class SessionResumeIntent
{
    public string OriginalSessionId { get; set; } = "";
    public string Command { get; set; } = "";
    public string? ShellType { get; set; }
    public string? WorkingDirectory { get; set; }
    public int Cols { get; set; }
    public int Rows { get; set; }
    public SessionDecorationState? Decoration { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, WriteIndented = true)]
[JsonSerializable(typeof(SessionUpdateState))]
[JsonSerializable(typeof(SessionDecorationState))]
[JsonSerializable(typeof(SessionResumeIntent))]
[JsonSerializable(typeof(TtyHostGitRepoMetadata))]
internal sealed partial class SessionUpdateStateJsonContext : JsonSerializerContext
{
}
