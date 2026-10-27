using System.Globalization;
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
    private const int ResumeHintTailLineCount = 8;
    private const int GracefulExitOutputLimit = 64 * 1024;
    private const int CodexExitResumeSearchLimit = 1024;
    private static readonly TimeSpan GracefulExitInterruptDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan GracefulExitOutputDrainDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RestoredShellReadyTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RestoredAgentStartTimeout = TimeSpan.FromSeconds(6);
    private readonly string _statePath;

    public SessionUpdateStateService(SettingsService settingsService)
        : this(settingsService.SettingsDirectory)
    {
    }

    public SessionUpdateStateService(string settingsDirectory)
        : this(settingsDirectory, "state.json")
    {
    }

    internal SessionUpdateStateService(string settingsDirectory, string fileName)
    {
        _statePath = Path.Combine(settingsDirectory, fileName);
    }

    internal async Task<SessionUpdateState?> ReadSavedStateAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_statePath)) return null;
        await using var stream = File.OpenRead(_statePath);
        return await JsonSerializer.DeserializeAsync(stream, SessionUpdateStateJsonContext.Default.SessionUpdateState, ct)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("The saved session state is empty.");
    }

    internal static bool CanSaveWorkspaceSession(SessionDecorationState session) =>
        !session.AppServerControlOnly &&
        (IsCodexForeground(session.ForegroundName, session.ForegroundCommandLine, session.ForegroundProcessIdentity) ||
         (string.IsNullOrWhiteSpace(session.ForegroundCommandLine) && string.IsNullOrWhiteSpace(session.ForegroundName)) ||
         IsShellProcess(session.ForegroundName, session.ForegroundCommandLine ?? ""));

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
            var stoppedSessions = new List<(string SessionId, string Command)>();
            try
            {
                foreach (var decoration in decorations.OrderBy(static item => item.Order))
                {
                    var command = await BuildResumeCommandAfterGracefulExitAsync(
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
                    if (IsCodexForeground(
                            decoration.ForegroundName,
                            decoration.ForegroundCommandLine,
                            decoration.ForegroundProcessIdentity))
                    {
                        stoppedSessions.Add((decoration.SessionId, command));
                    }
                }

                await PersistAsync(state, ct).ConfigureAwait(false);
            }
            catch
            {
                await RelaunchStoppedSessionsAsync(sessionManager, stoppedSessions).ConfigureAwait(false);
                throw;
            }
        }

        else
        {
            await PersistAsync(state, ct).ConfigureAwait(false);
        }

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
            return BuildResumeCommand(resumeHint, foregroundCommandLine);
        }

        if (!tryResumeNonAiAgentProcesses ||
            string.IsNullOrWhiteSpace(foregroundCommandLine) ||
            IsShellProcess(foregroundName, foregroundCommandLine))
        {
            return null;
        }

        return foregroundCommandLine.Trim();
    }

    internal static List<SessionDecorationState> CaptureSessionDecorations(
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

    private static async Task<string?> BuildResumeCommandAfterGracefulExitAsync(
        TtyHostSessionManager sessionManager,
        SessionDecorationState decoration,
        bool tryResumeNonAiAgentProcesses,
        CancellationToken ct)
    {
        if (IsCodexForeground(
                decoration.ForegroundName,
                decoration.ForegroundCommandLine,
                decoration.ForegroundProcessIdentity))
        {
            try
            {
                var resumeId = await CaptureCodexResumeIdAfterExitAsync(
                    sessionManager,
                    decoration.SessionId,
                    ct).ConfigureAwait(false);
                return BuildResumeCommand(
                    new AiResumeHint(AiCliProfileService.CodexProfile, "resume", resumeId),
                    decoration.ForegroundCommandLine);
            }
            catch
            {
                await RelaunchCodexIfStoppedAsync(
                    sessionManager,
                    decoration.SessionId,
                    decoration.ForegroundCommandLine).ConfigureAwait(false);
                throw;
            }
        }

        if (IsSupportedAiForeground(
                decoration.ForegroundName,
                decoration.ForegroundCommandLine,
                decoration.ForegroundProcessIdentity))
        {
            throw new InvalidOperationException(
                $"Full-update recovery cannot yet stop {decoration.ForegroundProcessIdentity ?? decoration.ForegroundName} authoritatively in session {decoration.SessionId}.");
        }

        if (!tryResumeNonAiAgentProcesses ||
            string.IsNullOrWhiteSpace(decoration.ForegroundCommandLine) ||
            IsShellProcess(decoration.ForegroundName, decoration.ForegroundCommandLine))
        {
            return null;
        }

        return decoration.ForegroundCommandLine.Trim();
    }

    private static async Task<string> CaptureCodexResumeIdAfterExitAsync(
        TtyHostSessionManager sessionManager,
        string sessionId,
        CancellationToken ct)
    {
        var output = new List<byte>(4096);
        var outputGate = new object();
        void OnOutput(string observedSessionId, ulong _, int __, int ___, ReadOnlyMemory<byte> data)
        {
            if (!string.Equals(observedSessionId, sessionId, StringComparison.Ordinal) || data.IsEmpty)
            {
                return;
            }

            lock (outputGate)
            {
                var overflow = output.Count + data.Length - GracefulExitOutputLimit;
                if (overflow > 0)
                {
                    output.RemoveRange(0, Math.Min(overflow, output.Count));
                }

                output.AddRange(data.ToArray());
            }
        }

        sessionManager.OnOutput += OnOutput;
        try
        {
            // The update may have been requested by a tool running inside this
            // exact Codex turn. Interrupt that tool first so Codex can return to
            // its prompt and process the explicit /quit command.
            await sessionManager.SendInputAsync(sessionId, new byte[] { 0x03 }, ct).ConfigureAwait(false);
            await Task.Delay(GracefulExitInterruptDelay, ct).ConfigureAwait(false);

            var interruptDeadline = DateTimeOffset.UtcNow + GracefulExitOutputDrainDelay;
            while (DateTimeOffset.UtcNow < interruptDeadline)
            {
                string interruptOutput;
                lock (outputGate)
                {
                    interruptOutput = Encoding.UTF8.GetString(output.ToArray());
                }

                var interruptResumeId = TryExtractCodexExitResumeId(interruptOutput);
                var afterInterrupt = await sessionManager.GetSessionFreshAsync(sessionId, ct).ConfigureAwait(false);
                var codexStillRunning = afterInterrupt is not null &&
                    IsCodexForeground(
                        afterInterrupt.ForegroundName,
                        afterInterrupt.ForegroundCommandLine,
                        afterInterrupt.ForegroundProcessIdentity);
                if (!codexStillRunning && interruptResumeId is not null)
                {
                    return interruptResumeId;
                }

                await Task.Delay(100, ct).ConfigureAwait(false);
            }

            var finalAfterInterrupt = await sessionManager.GetSessionFreshAsync(sessionId, ct).ConfigureAwait(false);
            var codexStillRunningAfterDrain = finalAfterInterrupt is not null &&
                IsCodexForeground(
                    finalAfterInterrupt.ForegroundName,
                    finalAfterInterrupt.ForegroundCommandLine,
                    finalAfterInterrupt.ForegroundProcessIdentity);
            if (!codexStillRunningAfterDrain)
            {
                string interruptOutput;
                lock (outputGate)
                {
                    interruptOutput = Encoding.UTF8.GetString(output.ToArray());
                }

                return TryExtractCodexExitResumeId(interruptOutput)
                    ?? throw new InvalidOperationException(
                        $"Codex exited from session {sessionId} after interruption without emitting its authoritative resume command.");
            }

            // Discard every repaint produced while interrupting the active turn.
            // Only bytes emitted after the explicit /quit are authoritative for
            // recovery and may contain the resume id.
            lock (outputGate)
            {
                output.Clear();
            }

            await sessionManager.SendInputAsync(sessionId, "/quit"u8.ToArray(), ct).ConfigureAwait(false);
            await Task.Delay(50, ct).ConfigureAwait(false);
            await sessionManager.SendInputAsync(sessionId, new byte[] { 0x0d }, ct).ConfigureAwait(false);

            var deadline = DateTimeOffset.UtcNow + GracefulExitTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                string capturedText;
                lock (outputGate)
                {
                    capturedText = Encoding.UTF8.GetString(output.ToArray());
                }

                var resumeId = TryExtractCodexExitResumeId(capturedText);
                var session = await sessionManager.GetSessionFreshAsync(sessionId, ct).ConfigureAwait(false);
                var codexExited = session is not null &&
                    !IsCodexForeground(
                        session.ForegroundName,
                        session.ForegroundCommandLine,
                        session.ForegroundProcessIdentity);
                if (codexExited && resumeId is not null)
                {
                    return resumeId;
                }

                if (codexExited && resumeId is null)
                {
                    throw new InvalidOperationException(
                        $"Codex exited from session {sessionId} without emitting its authoritative resume command.");
                }

                await Task.Delay(100, ct).ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Codex in session {sessionId} did not exit with an authoritative resume command within {GracefulExitTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds.");
        }
        finally
        {
            sessionManager.OnOutput -= OnOutput;
        }
    }

    internal static string? TryExtractCodexExitResumeId(string? capturedAfterExitStarted)
    {
        if (string.IsNullOrWhiteSpace(capturedAfterExitStarted))
        {
            return null;
        }

        var clean = TerminalOutputSanitizer.StripEscapeSequences(capturedAfterExitStarted);
        var markers = CodexExitResumeMarkerRegex().Matches(clean);
        for (var markerIndex = markers.Count - 1; markerIndex >= 0; markerIndex--)
        {
            var marker = markers[markerIndex];
            var searchStart = marker.Index + marker.Length;
            var searchLength = Math.Min(CodexExitResumeSearchLimit, clean.Length - searchStart);
            if (markerIndex + 1 < markers.Count)
            {
                searchLength = Math.Min(searchLength, markers[markerIndex + 1].Index - searchStart);
            }

            if (searchLength <= 0)
            {
                continue;
            }

            var searchWindow = clean.Substring(searchStart, searchLength);
            var candidates = CodexThreadIdRegex().Matches(searchWindow);
            if (candidates.Count != 1)
            {
                continue;
            }

            var candidate = candidates[0].Value;
            if (Guid.TryParse(candidate, out _))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsCodexForeground(
        string? foregroundName,
        string? foregroundCommandLine,
        string? foregroundProcessIdentity)
    {
        return IsToken(foregroundProcessIdentity, AiCliProfileService.CodexProfile)
            || IsToken(foregroundName, AiCliProfileService.CodexProfile)
            || TokenizeCommandLine(foregroundCommandLine)
                .Any(static token => IsToken(token, AiCliProfileService.CodexProfile));
    }

    private static bool IsToken(string? value, string expected)
    {
        return string.Equals(
            Path.GetFileNameWithoutExtension(value?.Trim()),
            expected,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task RelaunchStoppedSessionsAsync(
        TtyHostSessionManager sessionManager,
        IEnumerable<(string SessionId, string Command)> stoppedSessions)
    {
        foreach (var (sessionId, command) in stoppedSessions)
        {
            try
            {
                await sessionManager.SendInputAsync(
                    sessionId,
                    Encoding.UTF8.GetBytes(command + "\r"),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn(() => $"Failed to relaunch session {sessionId} after update capture was aborted: {ex.Message}");
            }
        }
    }

    private static async Task RelaunchCodexIfStoppedAsync(
        TtyHostSessionManager sessionManager,
        string sessionId,
        string? originalCommandLine)
    {
        try
        {
            var session = await sessionManager.GetSessionFreshAsync(
                sessionId,
                CancellationToken.None).ConfigureAwait(false);
            if (session is null ||
                IsCodexForeground(
                    session.ForegroundName,
                    session.ForegroundCommandLine,
                    session.ForegroundProcessIdentity))
            {
                return;
            }

            var command = BuildCodexInteractiveResumeCommand(originalCommandLine);
            await sessionManager.SendInputAsync(
                sessionId,
                Encoding.UTF8.GetBytes(command + "\r"),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Failed to relaunch Codex in session {sessionId} after authoritative resume capture failed: {ex.Message}");
        }
    }

    internal static string BuildCodexInteractiveResumeCommand(string? originalCommandLine)
    {
        return BuildCommand([
            AiCliProfileService.CodexProfile,
            ..PreserveResumeFlags(originalCommandLine),
            "resume"
        ]);
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
                    await WaitForRestoredShellReadyAsync(sessionManager, created.Session.Id, ct)
                        .ConfigureAwait(false);
                    await sessionManager.SendInputAsync(
                        created.Session.Id,
                        Encoding.UTF8.GetBytes(intent.Command + "\r"),
                        ct).ConfigureAwait(false);

                    var expectedProvider = TryGetResumeProvider(intent.Command);
                    if (expectedProvider is not null &&
                        !await WaitForRestoredAgentAsync(
                            sessionManager,
                            created.Session.Id,
                            expectedProvider,
                            ct).ConfigureAwait(false))
                    {
                        Log.Warn(() => $"Restored session {decoration.SessionId} stayed at the shell; retrying its {expectedProvider} resume command once");
                        await sessionManager.SendInputAsync(
                            created.Session.Id,
                            Encoding.UTF8.GetBytes(intent.Command + "\r"),
                            ct).ConfigureAwait(false);

                        if (!await WaitForRestoredAgentAsync(
                                sessionManager,
                                created.Session.Id,
                                expectedProvider,
                                ct).ConfigureAwait(false))
                        {
                            throw new InvalidOperationException(
                                $"{expectedProvider} did not become the foreground process after two resume attempts.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(() => $"Failed to send resume command for restored session {decoration.SessionId}: {ex.Message}");
                    failedOriginalSessionIds.Add(decoration.SessionId);
                    recreatedOrderBySessionId.Remove(created.Session.Id);
                    try
                    {
                        await sessionManager.CloseSessionAsync(created.Session.Id, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception closeEx)
                    {
                        Log.Warn(() => $"Failed to close incomplete restored session {created.Session.Id}: {closeEx.Message}");
                    }
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

    private static async Task WaitForRestoredShellReadyAsync(
        TtyHostSessionManager sessionManager,
        string sessionId,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + RestoredShellReadyTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var buffer = await sessionManager.GetBufferAsync(
                sessionId,
                maxBytes: 4096,
                reason: TerminalReplayReason.Manual,
                ct: ct).ConfigureAwait(false);
            if (buffer is { Data.Length: > 0 })
            {
                return;
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        Log.Warn(() => $"Restored shell {sessionId} produced no startup output before its resume command was sent");
    }

    private static async Task<bool> WaitForRestoredAgentAsync(
        TtyHostSessionManager sessionManager,
        string sessionId,
        string expectedProvider,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + RestoredAgentStartTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var session = await sessionManager.GetSessionFreshAsync(sessionId, ct).ConfigureAwait(false);
            if (session is null)
            {
                return false;
            }

            if (IsToken(session.ForegroundProcessIdentity, expectedProvider)
                || IsToken(session.ForegroundName, expectedProvider)
                || TokenizeCommandLine(session.ForegroundCommandLine)
                    .Any(token => IsToken(token, expectedProvider)))
            {
                return true;
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return false;
    }

    internal static string? TryGetResumeProvider(string? command)
    {
        foreach (var token in TokenizeCommandLine(command))
        {
            var fileName = Path.GetFileNameWithoutExtension(token.Trim('"', '\''));
            if (fileName is not null &&
                (fileName.Equals("codex", StringComparison.OrdinalIgnoreCase)
                 || fileName.Equals("claude", StringComparison.OrdinalIgnoreCase)
                 || fileName.Equals("grok", StringComparison.OrdinalIgnoreCase)))
            {
                return fileName.ToLowerInvariant();
            }
        }

        return null;
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
            if (!decoration.AppServerControlOnly)
            {
                await sessionManager.SetSessionExtraGitReposMetadataAsync(
                    targetSessionId,
                    decoration.ExtraGitRepos,
                    ct).ConfigureAwait(false);
            }
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

        var temporaryPath = statePath + ".tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, state,
                    SessionUpdateStateJsonContext.Default.SessionUpdateState, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
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

    private static string BuildResumeCommand(AiResumeHint hint, string? foregroundCommandLine)
    {
        var preservedFlags = PreserveResumeFlags(foregroundCommandLine);
        return BuildCommand([hint.Provider, ..preservedFlags, hint.ResumeArgument, hint.ThreadId]);
    }

    private static bool IsSupportedAiForeground(
        string? foregroundName,
        string? foregroundCommandLine,
        string? foregroundProcessIdentity)
    {
        return IsSupportedAiToken(foregroundProcessIdentity)
            || IsSupportedAiToken(foregroundName)
            || TokenizeCommandLine(foregroundCommandLine).Any(IsSupportedAiToken);
    }

    private static bool IsSupportedAiToken(string? value)
    {
        var token = Path.GetFileNameWithoutExtension(value?.Trim());
        return token is not null &&
            (token.Equals("codex", StringComparison.OrdinalIgnoreCase)
             || token.Equals("claude", StringComparison.OrdinalIgnoreCase)
             || token.Equals("grok", StringComparison.OrdinalIgnoreCase));
    }

    private static int LastIndexOfAsciiIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty || haystack.Length < needle.Length)
        {
            return -1;
        }

        for (var start = haystack.Length - needle.Length; start >= 0; start--)
        {
            var matches = true;
            for (var offset = 0; offset < needle.Length; offset++)
            {
                if (ToAsciiLower(haystack[start + offset]) != ToAsciiLower(needle[offset]))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return start;
            }
        }

        return -1;
    }

    private static bool ContainsResumeAcrossBoundary(byte[] tail, int tailLength, ReadOnlySpan<byte> current)
    {
        var prefixLength = Math.Min(current.Length, 8);
        var suffixLength = Math.Min(tailLength, 8);
        Span<byte> boundary = stackalloc byte[suffixLength + prefixLength];
        tail.AsSpan(tailLength - suffixLength, suffixLength).CopyTo(boundary);
        current[..prefixLength].CopyTo(boundary[suffixLength..]);
        return LastIndexOfAsciiIgnoreCase(boundary, "resume"u8) >= 0;
    }

    private static byte ToAsciiLower(byte value)
    {
        return value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;
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

    [GeneratedRegex(@"\bTo\s+(?:continue|resume)\s+this\s+session\s*,?\s*run\s+codex\s+resume\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CodexExitResumeMarkerRegex();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CodexThreadIdRegex();

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
