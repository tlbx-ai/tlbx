using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Common.Ipc;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Services.Hosting;
using Ai.Tlbx.MidTerm.Services.Updates;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed class SessionAppServerControlHostRuntimeService : IAsyncDisposable
{
    internal delegate bool RedirectedProcessLauncher(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environmentOverrides,
        IReadOnlyList<string>? pathPrependEntries,
        string? runAsUser,
        string? runAsUserSid,
        out TtyHostSpawner.RedirectedProcessHandle? launchedProcess,
        out string? failure);

    private const string CodexMode = "codex";
    private const string OffMode = "off";
    private const string SyntheticMode = "synthetic";
    private const string HostModeEnvironmentVariable = "MIDTERM_APP_SERVER_CONTROL_HOST_MODE";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly ConcurrentDictionary<string, HostRuntimeState> _states = new(StringComparer.Ordinal);
    private readonly SettingsService _settingsService;
    private readonly MidTermInstanceIdentity _instanceIdentity;
    private readonly AppServerControlHostOwnershipRegistry _ownershipRegistry;
    private readonly bool _preserveHostsOnDispose;
    private readonly string _mode;
    private readonly RedirectedProcessLauncher _launcher;

    public SessionAppServerControlHostRuntimeService(
        SettingsService settingsService,
        MidTermInstanceIdentity? instanceIdentity = null,
        string? mode = null)
        : this(settingsService, instanceIdentity, mode, null)
    {
    }

    internal SessionAppServerControlHostRuntimeService(
        SettingsService settingsService,
        MidTermInstanceIdentity? instanceIdentity,
        string? mode,
        RedirectedProcessLauncher? launcher)
    {
        _settingsService = settingsService;
        _instanceIdentity = instanceIdentity ?? MidTermInstanceIdentity.Load(
            Path.Combine(Path.GetTempPath(), "midterm-test-agenthost", Guid.NewGuid().ToString("N")),
            0);
        _ownershipRegistry = new AppServerControlHostOwnershipRegistry(
            Path.Combine(
                Path.GetDirectoryName(_instanceIdentity.SessionRegistryPath) ?? Path.GetTempPath(),
                $"appServerControl-host-sessions-{_instanceIdentity.InstanceId}.json"));
        _preserveHostsOnDispose = !IsTestBinaryBaseDirectory(AppContext.BaseDirectory);
        _mode = NormalizeMode(mode ?? Environment.GetEnvironmentVariable(HostModeEnvironmentVariable));
        _launcher = launcher ?? TtyHostSpawner.TryStartRedirectedProcess;
    }

    public bool IsEnabledFor(string? profile)
    {
        return (_mode, profile) switch
        {
            (SyntheticMode, AiCliProfileService.CodexProfile or AiCliProfileService.ClaudeProfile or AiCliProfileService.GrokProfile) => true,
            (CodexMode, AiCliProfileService.CodexProfile or AiCliProfileService.ClaudeProfile or AiCliProfileService.GrokProfile) => true,
            _ => false
        };
    }

    internal bool TryResolveRecoverableProfile(string sessionId, [NotNullWhen(true)] out string? profile)
    {
        profile = null;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        if (_states.TryGetValue(sessionId, out var state))
        {
            var connectedProfile = NormalizeRecoverableProfile(state.Profile);
            if (connectedProfile is not null)
            {
                profile = connectedProfile;
                return true;
            }

            var cachedHistoryProfile = NormalizeRecoverableProfile(state.CachedHistoryWindow?.Provider);
            if (cachedHistoryProfile is not null)
            {
                profile = cachedHistoryProfile;
                return true;
            }
        }

        var recordedProfile = _ownershipRegistry.GetSessions()
            .Where(record => string.Equals(record.SessionId, sessionId, StringComparison.Ordinal))
            .Select(record => NormalizeRecoverableProfile(record.Profile))
            .FirstOrDefault(candidate => candidate is not null);
        if (recordedProfile is not null)
        {
            profile = recordedProfile;
            return true;
        }

        return false;
    }

    public bool OwnsSession(string sessionId)
    {
        return _states.TryGetValue(sessionId, out var state) &&
               state.Input is not null &&
               state.Output is not null &&
               state.Status is not HostRuntimeStatus.None and not HostRuntimeStatus.Stopped;
    }

    public async Task<bool> EnsureAttachedAsync(
        string sessionId,
        string profile,
        SessionInfoDto session,
        string? resumeThreadIdOverride = null,
        CancellationToken ct = default)
    {
        return await EnsureAttachedAsync(
            sessionId,
            profile,
            session,
            resumeThreadIdOverride,
            allowSpawn: true,
            ct).ConfigureAwait(false);
    }

    public bool MayHaveRecoverableHost(string sessionId)
    {
        if (OwnsSession(sessionId))
        {
            return true;
        }

        if (_ownershipRegistry.GetSessions().Any(record => string.Equals(record.SessionId, sessionId, StringComparison.Ordinal)))
        {
            return true;
        }

        return AppServerControlHostEndpointDiscovery.FindEndpointPid(_instanceIdentity.InstanceId, sessionId).HasValue;
    }

    public async Task<bool> RecoverExistingHostAsync(
        string sessionId,
        string profile,
        SessionInfoDto session,
        string? resumeThreadIdOverride = null,
        CancellationToken ct = default)
    {
        return await EnsureAttachedAsync(
            sessionId,
            profile,
            session,
            resumeThreadIdOverride,
            allowSpawn: false,
            ct).ConfigureAwait(false);
    }

    private async Task<bool> EnsureAttachedAsync(
        string sessionId,
        string profile,
        SessionInfoDto session,
        string? resumeThreadIdOverride,
        bool allowSpawn,
        CancellationToken ct)
    {
        var workingDirectory = session.CurrentDirectory;
        if (!IsEnabledFor(profile) ||
            string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(workingDirectory) ||
            !Directory.Exists(workingDirectory))
        {
            return false;
        }

        var state = _states.GetOrAdd(sessionId, static id => new HostRuntimeState(id));
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            state.Profile = profile;
            state.WorkingDirectory = workingDirectory;
            var attachPoint = SelectAttachPoint(profile, session);

            if (state.Input is not null && state.Output is not null)
            {
                return true;
            }

            await DisposeStateAsync(state, terminateHost: false).ConfigureAwait(false);
            var settings = _settingsService.Load();
            var userProfileDirectory = ResolveConfiguredUserProfileDirectory(settings);
            var executablePath = attachPoint is null
                ? AiCliCommandLocator.ResolveExecutablePath(profile, session, userProfileDirectory)
                : null;
            var preferredProfileDirectory = ResolvePreferredProfileDirectory(settings, executablePath);
            BuildLaunchEnvironment(
                settings,
                executablePath,
                preferredProfileDirectory,
                _instanceIdentity,
                out var environmentOverrides,
                out var pathPrependEntries);

            if (!await TryConnectExistingHostAsync(state, profile, workingDirectory, ct).ConfigureAwait(false))
            {
                if (!allowSpawn)
                {
                    state.Status = HostRuntimeStatus.None;
                    state.LastError = null;
                    return false;
                }

                if (!TryResolveLaunch(profile, _mode, _settingsService.SettingsDirectory, out var launch))
                {
                    state.Status = HostRuntimeStatus.Error;
                    state.LastError = "mtagenthost executable could not be resolved.";
                    return false;
                }

                TtyHostSpawner.RedirectedProcessHandle? launchedProcess = null;
                try
                {
                    if (!_launcher(
                            launch.FileName,
                            BuildIpcLaunchArguments(launch.Arguments, sessionId, _instanceIdentity.InstanceId, _instanceIdentity.OwnerToken),
                            workingDirectory,
                            environmentOverrides,
                            pathPrependEntries,
                            settings.RunAsUser,
                            settings.RunAsUserSid,
                            out launchedProcess,
                            out var launchFailure))
                    {
                        state.Status = HostRuntimeStatus.Error;
                        state.LastError = string.IsNullOrWhiteSpace(launchFailure)
                            ? "mtagenthost process failed to start."
                            : launchFailure;
                        return false;
                    }

                    if (launchedProcess is null)
                    {
                        state.Status = HostRuntimeStatus.Error;
                        state.LastError = "mtagenthost process launcher returned no handle.";
                        return false;
                    }

                    var launchedResources = launchedProcess.DetachForIpc();
                    state.AttachOwnedLaunch(launchedResources.Process, launchedResources.Error);
                    launchedProcess.Dispose();
                    launchedProcess = null;

                    if (!await ConnectToSpawnedHostAsync(state, ct).ConfigureAwait(false))
                    {
                        state.LastError = "mtagenthost IPC endpoint did not become available.";
                        await DisposeStateAsync(state, terminateHost: true).ConfigureAwait(false);
                        state.Status = HostRuntimeStatus.Error;
                        return false;
                    }
                }
                finally
                {
                    launchedProcess?.Dispose();
                }
            }

            var attachResult = await SendCommandAsync(
                state,
                commandId => new AppServerControlHostCommandEnvelope
                {
                    CommandId = commandId,
                    SessionId = sessionId,
                    Type = "runtime.attach",
                    AttachRuntime = new AppServerControlAttachRuntimeRequest
                    {
                        SessionId = sessionId,
                        Provider = profile,
                        WorkingDirectory = workingDirectory,
                        InstanceId = _instanceIdentity.InstanceId,
                        OwnerToken = _instanceIdentity.OwnerToken,
                        AttachPoint = attachPoint,
                        ExecutablePath = executablePath,
                        UserProfileDirectory = preferredProfileDirectory,
                        ResumeThreadId = resumeThreadIdOverride ?? attachPoint?.PreferredThreadId
                    }
                },
                ct).ConfigureAwait(false);

            state.Status = attachResult.Status == "accepted" ? HostRuntimeStatus.Ready : HostRuntimeStatus.Error;
            state.LastError = attachResult.Status == "accepted" ? null : attachResult.Message;
            if (attachResult.Status == "accepted")
            {
                state.TransportKey = attachPoint?.TransportKind ?? "mtagenthost-ipc";
                state.TransportLabel = DescribeTransportLabel(_mode, profile, attachPoint);
                _ownershipRegistry.Upsert(sessionId, state.HostPid, profile, workingDirectory);
                await RefreshCachedHistoryWindowAsync(state, ct).ConfigureAwait(false);
            }
            return attachResult.Status == "accepted";
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static SessionAgentAttachPoint? SelectAttachPoint(string profile, SessionInfoDto session)
    {
        if (session.AgentAttachPoint is null)
        {
            return null;
        }

        return string.Equals(session.AgentAttachPoint.Provider, profile, StringComparison.OrdinalIgnoreCase)
            ? session.AgentAttachPoint
            : null;
    }

    private string? NormalizeRecoverableProfile(string? profile)
    {
        var normalized = string.IsNullOrWhiteSpace(profile)
            ? null
            : profile.Trim().ToLowerInvariant();
        return IsEnabledFor(normalized) ? normalized : null;
    }

    public async Task<bool> TrySendPromptAsync(
        string sessionId,
        SessionPromptRequest request,
        CancellationToken ct = default)
    {
        if (!_states.TryGetValue(sessionId, out var state))
        {
            return false;
        }

        if (!SessionApiEndpoints.TryGetInputBytes(
                new SessionInputRequest
                {
                    Text = request.Text,
                    Base64 = request.Base64,
                    AppendNewline = false
                },
                out var promptBytes,
                out _))
        {
            return false;
        }

        var promptText = Encoding.UTF8.GetString(promptBytes);
        if (string.IsNullOrWhiteSpace(promptText))
        {
            return false;
        }

        await StartTurnAsync(
            sessionId,
            new AppServerControlTurnRequest
            {
                Text = promptText,
                Attachments = []
            },
            ct).ConfigureAwait(false);
        return true;
    }

    public async Task<AppServerControlTurnStartResponse> StartTurnAsync(
        string sessionId,
        AppServerControlTurnRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = GetRequiredState(sessionId);

        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await SendCommandAsync(
                state,
                commandId => new AppServerControlHostCommandEnvelope
                {
                    CommandId = commandId,
                    SessionId = sessionId,
                    Type = "turn.start",
                    StartTurn = request
                },
                ct).ConfigureAwait(false);

            state.Status = HostRuntimeStatus.Running;
            var turnStarted = result.TurnStarted
                              ?? throw new InvalidOperationException("App Server Controller host did not return turn-start metadata.");
            return turnStarted;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<AppServerControlCommandAcceptedResponse> InterruptTurnAsync(
        string sessionId,
        AppServerControlInterruptRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = GetRequiredState(sessionId);

        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await SendCommandAsync(
                state,
                commandId => new AppServerControlHostCommandEnvelope
                {
                    CommandId = commandId,
                    SessionId = sessionId,
                    Type = "turn.interrupt",
                    InterruptTurn = request
                },
                ct).ConfigureAwait(false);

            return result.Accepted ?? new AppServerControlCommandAcceptedResponse
            {
                SessionId = sessionId,
                Status = result.Status
            };
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<AppServerControlCommandAcceptedResponse> ResolveRequestAsync(
        string sessionId,
        string requestId,
        AppServerControlRequestDecisionRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(request);
        var state = GetRequiredState(sessionId);

        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await SendCommandAsync(
                state,
                commandId => new AppServerControlHostCommandEnvelope
                {
                    CommandId = commandId,
                    SessionId = sessionId,
                    Type = "request.resolve",
                    ResolveRequest = new AppServerControlRequestResolutionCommand
                    {
                        RequestId = requestId,
                        Decision = request.Decision
                    }
                },
                ct).ConfigureAwait(false);

            return result.Accepted ?? new AppServerControlCommandAcceptedResponse
            {
                SessionId = sessionId,
                Status = result.Status,
                RequestId = requestId
            };
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<AppServerControlCommandAcceptedResponse> SetGoalAsync(
        string sessionId,
        AppServerControlGoalSetRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = GetRequiredState(sessionId);

        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await SendCommandAsync(
                state,
                commandId => new AppServerControlHostCommandEnvelope
                {
                    CommandId = commandId,
                    SessionId = sessionId,
                    Type = "thread.goal.set",
                    SetGoal = request
                },
                ct).ConfigureAwait(false);

            return result.Accepted ?? new AppServerControlCommandAcceptedResponse
            {
                SessionId = sessionId,
                Status = result.Status
            };
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<AppServerControlCommandAcceptedResponse> ResolveUserInputAsync(
        string sessionId,
        string requestId,
        AppServerControlUserInputAnswerRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(request);
        var state = GetRequiredState(sessionId);

        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await SendCommandAsync(
                state,
                commandId => new AppServerControlHostCommandEnvelope
                {
                    CommandId = commandId,
                    SessionId = sessionId,
                    Type = "user-input.resolve",
                    ResolveUserInput = new AppServerControlUserInputResolutionCommand
                    {
                        RequestId = requestId,
                        Answers = request.Answers
                    }
                },
                ct).ConfigureAwait(false);

            return result.Accepted ?? new AppServerControlCommandAcceptedResponse
            {
                SessionId = sessionId,
                Status = result.Status,
                RequestId = requestId
            };
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public bool TryGetRuntimeSummary(string sessionId, out AppServerControlRuntimeSummary summary)
    {
        summary = default!;
        if (!_states.TryGetValue(sessionId, out var state) ||
            state.Status == HostRuntimeStatus.None ||
            state.Status == HostRuntimeStatus.Stopped ||
            state.Input is null ||
            state.Output is null)
        {
            return false;
        }

        var cachedHistory = state.CachedHistoryWindow;
        summary = new AppServerControlRuntimeSummary
        {
            SessionId = sessionId,
            Profile = state.Profile ?? AiCliProfileService.UnknownProfile,
            TransportKey = state.TransportKey,
            TransportLabel = state.TransportLabel,
            Status = ToStatusValue(state.Status),
            StatusLabel = ToStatusLabel(state.Status),
            LastError = state.LastError ?? cachedHistory?.Session.LastError,
            LastEventAt = cachedHistory?.Session.LastEventAt,
            AssistantText = cachedHistory?.Streams.AssistantText,
            UnifiedDiff = cachedHistory?.Streams.UnifiedDiff,
            PendingQuestion = cachedHistory?.Requests.FirstOrDefault(static request => request.Kind == "interview" && request.State == "open")?.Detail,
            Activities = []
        };
        return true;
    }

    public bool HasHistory(string sessionId)
    {
        if (_states.TryGetValue(sessionId, out var state))
        {
            return state.CachedHistoryWindow?.HistoryCount > 0 || OwnsSession(sessionId);
        }

        return _ownershipRegistry.GetSessions().Any(record => string.Equals(record.SessionId, sessionId, StringComparison.Ordinal)) ||
               AppServerControlHostEndpointDiscovery.FindEndpointPid(_instanceIdentity.InstanceId, sessionId).HasValue;
    }

    public bool TryGetCachedHistoryWindow(string sessionId, out AppServerControlHistoryWindowResponse historyWindow)
    {
        historyWindow = default!;
        if (!_states.TryGetValue(sessionId, out var state) || state.CachedHistoryWindow is null)
        {
            return false;
        }

        historyWindow = CloneHistoryWindow(state.CachedHistoryWindow);
        return true;
    }

    public async Task<AppServerControlHistoryWindowResponse?> GetHistoryWindowAsync(
        string sessionId,
        int? startIndex = null,
        int? count = null,
        int? viewportWidth = null,
        CancellationToken ct = default)
    {
        var state = GetRequiredState(sessionId);
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await SendCommandAsync(
                state,
                commandId => new AppServerControlHostCommandEnvelope
                {
                    CommandId = commandId,
                    SessionId = sessionId,
                    Type = "history.window.get",
                    HistoryWindow = new AppServerControlHostHistoryWindowRequest
                    {
                        StartIndex = startIndex,
                        Count = count,
                        ViewportWidth = viewportWidth
                    }
                },
                ct).ConfigureAwait(false);

            if (result.HistoryWindow is null)
            {
                return null;
            }

            UpdateCachedHistoryWindow(state, result.HistoryWindow, replaceHistory: true);
            return CloneHistoryWindow(result.HistoryWindow);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public AppServerControlHistoryPatchSubscription SubscribeHistoryPatches(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var state = GetRequiredState(sessionId);
        var channel = Channel.CreateUnbounded<AppServerControlHistoryPatch>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var subscriber = new AppServerControlHistoryPatchSubscriber(channel.Writer);

        lock (state.HistorySubscribersSync)
        {
            state.HistoryPatchSubscribers.Add(subscriber);
        }

        var subscriptionState = new SubscriptionState(
            () =>
            {
                lock (state.HistorySubscribersSync)
                {
                    state.HistoryPatchSubscribers.Remove(subscriber);
                }

                channel.Writer.TryComplete();
            });
        var subscription = new AppServerControlHistoryPatchSubscription(channel.Reader, subscriptionState);

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(
                static stateObject =>
                {
                    if (stateObject is SubscriptionState stateToClose)
                    {
                        stateToClose.Close();
                    }
                },
                subscriptionState);
        }

        return subscription;
    }

    public void Forget(string sessionId)
    {
        if (_states.TryRemove(sessionId, out var state))
        {
            _ = DisposeOwnedStateAsync(state, terminateHost: true);
        }

        _ownershipRegistry.Remove(sessionId);
    }

    public async Task DetachAsync(string sessionId, CancellationToken ct = default)
    {
        if (!_states.TryRemove(sessionId, out var state))
        {
            return;
        }

        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DisposeOwnedStateAsync(state, terminateHost: true).ConfigureAwait(false);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<int> TerminateAllOwnedHostsAsync(CancellationToken ct = default)
    {
        var terminated = 0;
        var sessionIds = _states.Keys
            .Concat(_ownershipRegistry.GetSessions().Select(static record => record.SessionId))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var sessionId in sessionIds)
        {
            ct.ThrowIfCancellationRequested();
            if (_states.TryRemove(sessionId, out var state))
            {
                await state.Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await DisposeOwnedStateAsync(state, terminateHost: true).ConfigureAwait(false);
                    terminated++;
                }
                finally
                {
                    state.Gate.Release();
                }

                continue;
            }

            var record = _ownershipRegistry.GetSessions()
                .FirstOrDefault(candidate => string.Equals(candidate.SessionId, sessionId, StringComparison.Ordinal));
            if (record is null)
            {
                continue;
            }

            await TerminateOwnedHostAsync(sessionId, record.HostPid).ConfigureAwait(false);
            terminated++;
        }

        return terminated;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var state in _states.Values)
        {
            await DisposeOwnedStateAsync(state, terminateHost: !_preserveHostsOnDispose).ConfigureAwait(false);
        }

        _states.Clear();
    }

    private async Task ReadLoopAsync(HostRuntimeState state)
    {
        try
        {
            while (state.Output is not null)
            {
                var line = await state.Output.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (root.TryGetProperty("patch", out _))
                {
                    var patchEnvelope = JsonSerializer.Deserialize(line, AppServerControlHostJsonContext.Default.AppServerControlHostHistoryPatchEnvelope);
                    if (patchEnvelope is null)
                    {
                        continue;
                    }

                    ApplyHistoryPatchToState(state, patchEnvelope.Patch);
                    continue;
                }

                if (root.TryGetProperty("commandId", out var commandIdProperty))
                {
                    var commandId = commandIdProperty.GetString();
                    var result = JsonSerializer.Deserialize(line, AppServerControlHostJsonContext.Default.AppServerControlHostCommandResultEnvelope);
                    if (result is null || string.IsNullOrWhiteSpace(commandId))
                    {
                        continue;
                    }

                    if (state.PendingCommands.TryRemove(commandId, out var pending))
                    {
                        pending.TrySetResult(result);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            state.LastError = ex.Message;
            state.Status = HostRuntimeStatus.Error;
            Log.Warn(() => $"SessionAppServerControlHostRuntimeService read loop failed for {state.SessionId}: {ex.Message}");
        }
    }

    private async Task ReadErrorLoopAsync(HostRuntimeState state)
    {
        try
        {
            while (state.Error is not null)
            {
                var line = await state.Error.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    var sanitized = AppServerControlHistoryTextSanitizer.Sanitize(line);
                    if (string.IsNullOrWhiteSpace(sanitized))
                    {
                        continue;
                    }

                    state.LastError = sanitized;
                    Log.Info(() => $"mtagenthost[{state.SessionId}] {sanitized}");
                }
            }
        }
        catch (Exception ex)
        {
            state.LastError = ex.Message;
            Log.Warn(() => $"SessionAppServerControlHostRuntimeService stderr loop failed for {state.SessionId}: {ex.Message}");
        }
    }

    private async Task<AppServerControlHostCommandResultEnvelope> SendCommandAsync(
        HostRuntimeState state,
        Func<string, AppServerControlHostCommandEnvelope> createCommand,
        CancellationToken ct)
    {
        var commandId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var pending = new TaskCompletionSource<AppServerControlHostCommandResultEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!state.PendingCommands.TryAdd(commandId, pending))
        {
            throw new InvalidOperationException("Failed to track App Server Controller host command.");
        }

        try
        {
            var command = createCommand(commandId);
            var payload = JsonSerializer.Serialize(command, AppServerControlHostJsonContext.Default.AppServerControlHostCommandEnvelope);
            await state.Input!.WriteLineAsync(payload).ConfigureAwait(false);
            await state.Input.FlushAsync(ct).ConfigureAwait(false);

            var result = await pending.Task.WaitAsync(CommandTimeout, ct).ConfigureAwait(false);
            if (!string.Equals(result.Status, "accepted", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(result.Message ?? $"App Server Controller host rejected command '{command.Type}'.");
            }

            return result;
        }
        finally
        {
            state.PendingCommands.TryRemove(commandId, out _);
        }
    }

    private static async Task<string> ReadLineWithTimeoutAsync(StreamReader reader, CancellationToken ct)
    {
        var line = await reader.ReadLineAsync(ct).AsTask().WaitAsync(CommandTimeout, ct).ConfigureAwait(false);
        return line ?? throw new EndOfStreamException("mtagenthost closed stdout before sending hello.");
    }

    private async Task<bool> TryConnectExistingHostAsync(
        HostRuntimeState state,
        string profile,
        string workingDirectory,
        CancellationToken ct)
    {
        var recorded = _ownershipRegistry.GetSessions()
            .FirstOrDefault(record => string.Equals(record.SessionId, state.SessionId, StringComparison.Ordinal));
        if (recorded is not null)
        {
            if (await TryConnectToHostAsync(state, recorded.HostPid, ct).ConfigureAwait(false))
            {
                state.Profile = string.IsNullOrWhiteSpace(recorded.Profile) ? profile : recorded.Profile;
                state.WorkingDirectory = string.IsNullOrWhiteSpace(recorded.WorkingDirectory) ? workingDirectory : recorded.WorkingDirectory;
                return true;
            }

            _ownershipRegistry.Remove(state.SessionId);
            AppServerControlHostEndpointDiscovery.CleanupEndpoint(_instanceIdentity.InstanceId, state.SessionId, recorded.HostPid);
        }

        var discoveredPid = AppServerControlHostEndpointDiscovery.FindEndpointPid(_instanceIdentity.InstanceId, state.SessionId);
        if (discoveredPid is int hostPid && await TryConnectToHostAsync(state, hostPid, ct).ConfigureAwait(false))
        {
            state.Profile = profile;
            state.WorkingDirectory = workingDirectory;
            return true;
        }

        return false;
    }

    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP001:Dispose created", Justification = "The connected host transport is transferred into HostRuntimeState on success and disposed explicitly on failure paths.")]
    private async Task<bool> ConnectToSpawnedHostAsync(HostRuntimeState state, CancellationToken ct)
    {
        if (state.Process is null)
        {
            return false;
        }

        await Task.Delay(50, ct).ConfigureAwait(false);
        var launchedPid = state.Process.Id;
        for (var wait = 50; wait <= 800; wait *= 2)
        {
            if (state.Process.HasExited)
            {
                return false;
            }

            if (await TryConnectToHostAsync(state, launchedPid, ct, connectTimeoutMs: 125).ConfigureAwait(false))
            {
                return true;
            }

            var discoveredPid = AppServerControlHostEndpointDiscovery.FindEndpointPid(_instanceIdentity.InstanceId, state.SessionId);
            if (discoveredPid is int hostPid &&
                hostPid != launchedPid &&
                await TryConnectToHostAsync(state, hostPid, ct, connectTimeoutMs: 125).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(wait, ct).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> TryConnectToHostAsync(
        HostRuntimeState state,
        int hostPid,
        CancellationToken ct,
        int connectTimeoutMs = 1000)
    {
        await DisposeStreamsAsync(state).ConfigureAwait(false);

        var endpoint = AppServerControlHostEndpoint.GetSessionEndpoint(_instanceIdentity.InstanceId, state.SessionId, hostPid);
        HostTransportConnection? connection = null;
        var attached = false;
        try
        {
#pragma warning disable IDISP001
            connection = await HostTransportConnection.ConnectAsync(endpoint, connectTimeoutMs, ct).ConfigureAwait(false);
#pragma warning restore IDISP001
            var helloLine = await ReadLineWithTimeoutAsync(connection.Reader, ct).ConfigureAwait(false);
            var hello = JsonSerializer.Deserialize(helloLine, AppServerControlHostJsonContext.Default.AppServerControlHostHello)
                        ?? throw new InvalidOperationException("App Server Controller host hello payload was empty.");
            ValidateHello(hello);

            state.AttachOwnedConnection(connection);
            attached = true;
            state.HostPid = hostPid;
            state.TransportKey = "mtagenthost-ipc";
            state.TransportLabel = "mtagenthost owned IPC";
            state.Status = HostRuntimeStatus.Starting;
            state.ReaderTask = Task.Run(() => ReadLoopAsync(state), CancellationToken.None);
            if (state.Error is not null)
            {
                state.ErrorTask = Task.Run(() => ReadErrorLoopAsync(state), CancellationToken.None);
            }

            return true;
        }
        catch
        {
            if (attached)
            {
                await DisposeStreamsAsync(state).ConfigureAwait(false);
            }
            else
            {
                connection?.Dispose();
            }

            return false;
        }
    }

    private static void ValidateHello(AppServerControlHostHello hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        EnsureProtocolVersion(hello.ProtocolVersion);
    }

    private async Task RefreshCachedHistoryWindowAsync(HostRuntimeState state, CancellationToken ct)
    {
        var result = await SendCommandAsync(
            state,
            commandId => new AppServerControlHostCommandEnvelope
            {
                CommandId = commandId,
                SessionId = state.SessionId,
                Type = "history.window.get",
                HistoryWindow = new AppServerControlHostHistoryWindowRequest()
            },
            ct).ConfigureAwait(false);

        if (result.HistoryWindow is not null)
        {
            UpdateCachedHistoryWindow(state, result.HistoryWindow, replaceHistory: true);
            return;
        }

        if (state.CachedHistoryWindow is null)
        {
            var placeholder = BuildPlaceholderHistoryWindow(state);
            state.CachedHistoryWindow = placeholder;
        }

        RefreshRuntimeStatusFromHistory(state);
    }

    private static void ApplyHistoryPatchToState(HostRuntimeState state, AppServerControlHistoryPatch patch)
    {
        state.LastError = patch.Session.LastError ?? state.LastError;

        if (state.CachedHistoryWindow is not null &&
            patch.LatestSequence < state.CachedHistoryWindow.LatestSequence)
        {
            return;
        }

        if (state.CachedHistoryWindow is null)
        {
            state.CachedHistoryWindow = new AppServerControlHistoryWindowResponse
            {
                SessionId = patch.SessionId,
                Provider = patch.Provider,
                GeneratedAt = patch.GeneratedAt,
                LatestSequence = patch.LatestSequence,
                HistoryCount = patch.HistoryCount,
                Session = CloneSessionSummary(patch.Session),
                Thread = CloneThreadSummary(patch.Thread),
                CurrentTurn = CloneTurnSummary(patch.CurrentTurn),
                QuickSettings = CloneQuickSettings(patch.QuickSettings),
                Streams = CloneStreams(patch.Streams),
                Requests = patch.RequestUpserts.Select(CloneRequestSummary).ToList(),
                Items = patch.ItemUpserts.Select(CloneItemSummary).ToList(),
                Notices = patch.NoticeUpserts.Select(CloneNotice).ToList(),
            };
        }
        else
        {
            var cached = state.CachedHistoryWindow;
            cached.Provider = string.IsNullOrWhiteSpace(patch.Provider) ? cached.Provider : patch.Provider;
            cached.GeneratedAt = patch.GeneratedAt;
            cached.LatestSequence = Math.Max(cached.LatestSequence, patch.LatestSequence);
            cached.HistoryCount = Math.Max(cached.HistoryCount, patch.HistoryCount);
            cached.Session = CloneSessionSummary(patch.Session);
            cached.Thread = CloneThreadSummary(patch.Thread);
            cached.CurrentTurn = CloneTurnSummary(patch.CurrentTurn);
            cached.QuickSettings = CloneQuickSettings(patch.QuickSettings);
            cached.Streams = CloneStreams(patch.Streams);
            cached.Items = MergeItemSummaries(cached.Items, patch.ItemUpserts, patch.ItemRemovals);
            cached.Requests = MergeRequestSummaries(cached.Requests, patch.RequestUpserts, patch.RequestRemovals);
            cached.Notices = MergeNotices(cached.Notices, patch.NoticeUpserts);
        }

        RefreshRuntimeStatusFromHistory(state);
        FanOutHistoryPatch(state, patch);
    }

    private static void UpdateCachedHistoryWindow(HostRuntimeState state, AppServerControlHistoryWindowResponse historyWindow, bool replaceHistory)
    {
        var next = CloneHistoryWindow(historyWindow);
        if (!replaceHistory && state.CachedHistoryWindow is not null)
        {
            next.History = state.CachedHistoryWindow.History;
        }

        state.CachedHistoryWindow = next;
        RefreshRuntimeStatusFromHistory(state);
    }

    private static AppServerControlHistoryWindowResponse BuildPlaceholderHistoryWindow(HostRuntimeState state)
    {
        return new AppServerControlHistoryWindowResponse
        {
            SessionId = state.SessionId,
            Provider = state.Profile ?? AiCliProfileService.UnknownProfile,
            GeneratedAt = DateTimeOffset.UtcNow,
            LatestSequence = 1,
            HistoryCount = 0,
            HistoryWindowStart = 0,
            HistoryWindowEnd = 0,
            HasOlderHistory = false,
            HasNewerHistory = false,
            Session = new AppServerControlSessionSummary
            {
                State = ToStatusValue(state.Status),
                StateLabel = ToStatusLabel(state.Status),
                LastError = state.LastError,
            },
            Thread = new AppServerControlThreadSummary(),
            CurrentTurn = new AppServerControlTurnSummary(),
            QuickSettings = new AppServerControlQuickSettingsSummary(),
            Streams = new AppServerControlStreamsSummary(),
        };
    }

    private static void RefreshRuntimeStatusFromHistory(HostRuntimeState state)
    {
        var cachedHistory = state.CachedHistoryWindow;
        if (cachedHistory is null)
        {
            return;
        }

        state.LastError = cachedHistory.Session.LastError ?? state.LastError;
        state.Status = ResolveRuntimeStatus(cachedHistory);
    }

    private static HostRuntimeStatus ResolveRuntimeStatus(AppServerControlHistoryWindowResponse historyWindow)
    {
        if (!string.IsNullOrWhiteSpace(historyWindow.Session.LastError))
        {
            return HostRuntimeStatus.Error;
        }

        var turnState = historyWindow.CurrentTurn.State?.Trim().ToLowerInvariant();
        if (turnState is "running" or "in_progress" or "started" or "submitted")
        {
            return HostRuntimeStatus.Running;
        }

        var sessionState = historyWindow.Session.State?.Trim().ToLowerInvariant();
        return sessionState switch
        {
            "starting" => HostRuntimeStatus.Starting,
            "ready" or "completed" or "idle" => HostRuntimeStatus.Ready,
            "error" => HostRuntimeStatus.Error,
            "stopped" => HostRuntimeStatus.Stopped,
            _ => stateFromThreadOrFallback(sessionState)
        };

        static HostRuntimeStatus stateFromThreadOrFallback(string? value)
        {
            return value switch
            {
                "running" => HostRuntimeStatus.Running,
                _ => HostRuntimeStatus.Ready
            };
        }
    }

    private static void EnsureProtocolVersion(string? protocolVersion)
    {
        if (!string.Equals(protocolVersion, AppServerControlHostProtocol.CurrentVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported App Server Controller host protocol version '{protocolVersion ?? "(null)"}'. Expected '{AppServerControlHostProtocol.CurrentVersion}'.");
        }
    }

    private static void FanOutHistoryPatch(HostRuntimeState state, AppServerControlHistoryPatch patch)
    {
        List<AppServerControlHistoryPatchSubscriber>? stale = null;
        lock (state.HistorySubscribersSync)
        {
            foreach (var subscriber in state.HistoryPatchSubscribers)
            {
                if (subscriber.Writer.TryWrite(CloneHistoryPatch(patch)))
                {
                    continue;
                }

                stale ??= [];
                stale.Add(subscriber);
            }

            if (stale is not null)
            {
                foreach (var subscriber in stale)
                {
                    state.HistoryPatchSubscribers.Remove(subscriber);
                }
            }
        }
    }

    private static AppServerControlHistoryWindowResponse CloneHistoryWindow(AppServerControlHistoryWindowResponse source)
    {
        return new AppServerControlHistoryWindowResponse
        {
            SessionId = source.SessionId,
            Provider = source.Provider,
            GeneratedAt = source.GeneratedAt,
            LatestSequence = source.LatestSequence,
            HistoryCount = source.HistoryCount,
            HistoryWindowStart = source.HistoryWindowStart,
            HistoryWindowEnd = source.HistoryWindowEnd,
            HasOlderHistory = source.HasOlderHistory,
            HasNewerHistory = source.HasNewerHistory,
            Session = CloneSessionSummary(source.Session),
            Thread = CloneThreadSummary(source.Thread),
            CurrentTurn = CloneTurnSummary(source.CurrentTurn),
            QuickSettings = CloneQuickSettings(source.QuickSettings),
            Streams = CloneStreams(source.Streams),
            History = source.History.Select(CloneHistoryEntry).ToList(),
            Items = source.Items.Select(CloneItemSummary).ToList(),
            Requests = source.Requests.Select(CloneRequestSummary).ToList(),
            Notices = source.Notices.Select(CloneNotice).ToList()
        };
    }

    private static AppServerControlHistoryPatch CloneHistoryPatch(AppServerControlHistoryPatch source)
    {
        return new AppServerControlHistoryPatch
        {
            SessionId = source.SessionId,
            Provider = source.Provider,
            GeneratedAt = source.GeneratedAt,
            LatestSequence = source.LatestSequence,
            HistoryCount = source.HistoryCount,
            Session = CloneSessionSummary(source.Session),
            Thread = CloneThreadSummary(source.Thread),
            CurrentTurn = CloneTurnSummary(source.CurrentTurn),
            QuickSettings = CloneQuickSettings(source.QuickSettings),
            Streams = CloneStreams(source.Streams),
            HistoryUpserts = source.HistoryUpserts.Select(CloneHistoryEntry).ToList(),
            HistoryRemovals = [.. source.HistoryRemovals],
            ItemUpserts = source.ItemUpserts.Select(CloneItemSummary).ToList(),
            ItemRemovals = [.. source.ItemRemovals],
            RequestUpserts = source.RequestUpserts.Select(CloneRequestSummary).ToList(),
            RequestRemovals = [.. source.RequestRemovals],
            NoticeUpserts = source.NoticeUpserts.Select(CloneNotice).ToList()
        };
    }

    private static AppServerControlHistoryItem CloneHistoryEntry(AppServerControlHistoryItem source)
    {
        return new AppServerControlHistoryItem
        {
            EntryId = source.EntryId,
            Order = source.Order,
            EstimatedHeightPx = source.EstimatedHeightPx,
            Kind = source.Kind,
            TurnId = source.TurnId,
            ItemId = source.ItemId,
            RequestId = source.RequestId,
            Status = source.Status,
            ItemType = source.ItemType,
            Title = source.Title,
            CommandText = source.CommandText,
            Body = source.Body,
            Attachments = source.Attachments.Select(CloneAttachment).ToList(),
            FileMentions = source.FileMentions.Select(CloneInlineFileReference).ToList(),
            ImagePreviews = source.ImagePreviews.Select(CloneInlineImagePreview).ToList(),
            Streaming = source.Streaming,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt
        };
    }

    private static AppServerControlItemSummary CloneItemSummary(AppServerControlItemSummary source)
    {
        return new AppServerControlItemSummary
        {
            ItemId = source.ItemId,
            TurnId = source.TurnId,
            ItemType = source.ItemType,
            Status = source.Status,
            Title = source.Title,
            Detail = source.Detail,
            Attachments = source.Attachments.Select(CloneAttachment).ToList(),
            UpdatedAt = source.UpdatedAt
        };
    }

    private static AppServerControlRequestSummary CloneRequestSummary(AppServerControlRequestSummary source)
    {
        return new AppServerControlRequestSummary
        {
            RequestId = source.RequestId,
            TurnId = source.TurnId,
            Kind = source.Kind,
            KindLabel = source.KindLabel,
            State = source.State,
            Detail = source.Detail,
            Decision = source.Decision,
            Questions = source.Questions.Select(CloneQuestion).ToList(),
            Answers = source.Answers.Select(CloneAnsweredQuestion).ToList(),
            UpdatedAt = source.UpdatedAt
        };
    }

    private static AppServerControlRuntimeNotice CloneNotice(AppServerControlRuntimeNotice source)
    {
        return new AppServerControlRuntimeNotice
        {
            EventId = source.EventId,
            Type = source.Type,
            Message = source.Message,
            Detail = source.Detail,
            CreatedAt = source.CreatedAt
        };
    }

    private static AppServerControlSessionSummary CloneSessionSummary(AppServerControlSessionSummary source)
    {
        return new AppServerControlSessionSummary
        {
            State = source.State,
            StateLabel = source.StateLabel,
            Reason = source.Reason,
            LastError = source.LastError,
            LastEventAt = source.LastEventAt
        };
    }

    private static AppServerControlThreadSummary CloneThreadSummary(AppServerControlThreadSummary source)
    {
        return new AppServerControlThreadSummary
        {
            ThreadId = source.ThreadId,
            State = source.State,
            StateLabel = source.StateLabel
        };
    }

    private static AppServerControlTurnSummary CloneTurnSummary(AppServerControlTurnSummary source)
    {
        return new AppServerControlTurnSummary
        {
            TurnId = source.TurnId,
            State = source.State,
            StateLabel = source.StateLabel,
            Model = source.Model,
            Effort = source.Effort,
            StartedAt = source.StartedAt,
            CompletedAt = source.CompletedAt
        };
    }

    private static AppServerControlQuickSettingsSummary CloneQuickSettings(AppServerControlQuickSettingsSummary source)
    {
        return new AppServerControlQuickSettingsSummary
        {
            Model = source.Model,
            Effort = source.Effort,
            PlanMode = source.PlanMode,
            PermissionMode = source.PermissionMode,
            ModelOptions = AppServerControlQuickSettings.CloneOptions(source.ModelOptions),
            EffortOptions = AppServerControlQuickSettings.CloneOptions(source.EffortOptions)
        };
    }

    private static AppServerControlStreamsSummary CloneStreams(AppServerControlStreamsSummary source)
    {
        return new AppServerControlStreamsSummary
        {
            AssistantText = source.AssistantText,
            ReasoningText = source.ReasoningText,
            ReasoningSummaryText = source.ReasoningSummaryText,
            PlanText = source.PlanText,
            CommandOutput = source.CommandOutput,
            FileChangeOutput = source.FileChangeOutput,
            UnifiedDiff = source.UnifiedDiff
        };
    }

    private static AppServerControlAttachmentReference CloneAttachment(AppServerControlAttachmentReference source)
    {
        return new AppServerControlAttachmentReference
        {
            Kind = source.Kind,
            Path = source.Path,
            MimeType = source.MimeType,
            DisplayName = source.DisplayName
        };
    }

    private static AppServerControlInlineFileReference CloneInlineFileReference(AppServerControlInlineFileReference source)
    {
        return new AppServerControlInlineFileReference
        {
            Field = source.Field,
            DisplayText = source.DisplayText,
            Path = source.Path,
            PathKind = source.PathKind,
            ResolvedPath = source.ResolvedPath,
            Exists = source.Exists,
            IsDirectory = source.IsDirectory,
            MimeType = source.MimeType,
            Line = source.Line,
            Column = source.Column
        };
    }

    private static AppServerControlInlineImagePreview CloneInlineImagePreview(AppServerControlInlineImagePreview source)
    {
        return new AppServerControlInlineImagePreview
        {
            DisplayPath = source.DisplayPath,
            ResolvedPath = source.ResolvedPath,
            MimeType = source.MimeType
        };
    }

    private static AppServerControlQuestion CloneQuestion(AppServerControlQuestion source)
    {
        return new AppServerControlQuestion
        {
            Id = source.Id,
            Header = source.Header,
            Question = source.Question,
            MultiSelect = source.MultiSelect,
            Options = source.Options.Select(option => new AppServerControlQuestionOption
            {
                Label = option.Label,
                Description = option.Description
            }).ToList()
        };
    }

    private static AppServerControlAnsweredQuestion CloneAnsweredQuestion(AppServerControlAnsweredQuestion source)
    {
        return new AppServerControlAnsweredQuestion
        {
            QuestionId = source.QuestionId,
            Answers = [.. source.Answers]
        };
    }

    private static List<AppServerControlItemSummary> MergeItemSummaries(
        IReadOnlyList<AppServerControlItemSummary> current,
        IReadOnlyList<AppServerControlItemSummary> upserts,
        IReadOnlyList<string> removals)
    {
        var next = current.ToDictionary(item => item.ItemId, CloneItemSummary, StringComparer.Ordinal);
        foreach (var removal in removals)
        {
            next.Remove(removal);
        }

        foreach (var upsert in upserts)
        {
            next[upsert.ItemId] = CloneItemSummary(upsert);
        }

        return next.Values.OrderByDescending(item => item.UpdatedAt).ToList();
    }

    private static List<AppServerControlRequestSummary> MergeRequestSummaries(
        IReadOnlyList<AppServerControlRequestSummary> current,
        IReadOnlyList<AppServerControlRequestSummary> upserts,
        IReadOnlyList<string> removals)
    {
        var next = current.ToDictionary(request => request.RequestId, CloneRequestSummary, StringComparer.Ordinal);
        foreach (var removal in removals)
        {
            next.Remove(removal);
        }

        foreach (var upsert in upserts)
        {
            next[upsert.RequestId] = CloneRequestSummary(upsert);
        }

        return next.Values.OrderByDescending(request => request.UpdatedAt).ToList();
    }

    private static List<AppServerControlRuntimeNotice> MergeNotices(
        IReadOnlyList<AppServerControlRuntimeNotice> current,
        IReadOnlyList<AppServerControlRuntimeNotice> upserts)
    {
        var next = current.ToDictionary(notice => notice.EventId, CloneNotice, StringComparer.Ordinal);
        foreach (var upsert in upserts)
        {
            next[upsert.EventId] = CloneNotice(upsert);
        }

        return next.Values.OrderByDescending(notice => notice.CreatedAt).ToList();
    }

    private static string NormalizeMode(string? mode)
    {
        return (mode ?? CodexMode).Trim().ToLowerInvariant() switch
        {
            SyntheticMode => SyntheticMode,
            CodexMode => CodexMode,
            _ => OffMode
        };
    }

    private static string ToStatusValue(HostRuntimeStatus status)
    {
        return status switch
        {
            HostRuntimeStatus.Starting => "starting",
            HostRuntimeStatus.Running => "running",
            HostRuntimeStatus.Error => "error",
            HostRuntimeStatus.Stopped => "stopped",
            HostRuntimeStatus.Ready => "ready",
            _ => "ready"
        };
    }

    private static string ToStatusLabel(HostRuntimeStatus status)
    {
        return status switch
        {
            HostRuntimeStatus.Starting => "Starting",
            HostRuntimeStatus.Running => "Running",
            HostRuntimeStatus.Error => "Error",
            HostRuntimeStatus.Stopped => "Stopped",
            HostRuntimeStatus.Ready => "Ready",
            _ => "Ready"
        };
    }

    private static string DescribeTransportLabel(string mode, string profile, SessionAgentAttachPoint? attachPoint)
    {
        if (attachPoint is null && string.Equals(mode, CodexMode, StringComparison.Ordinal))
        {
            return "mtagenthost owned IPC";
        }

        if (attachPoint is not null)
        {
            return attachPoint.TransportKind switch
            {
                SessionAgentAttachPoint.CodexAppServerWebSocketTransport => "Codex app-server websocket",
                _ => attachPoint.TransportKind
            };
        }

        return mode switch
        {
            SyntheticMode => "mtagenthost synthetic stdio",
            CodexMode => $"mtagenthost {profile} stdio",
            _ => "mtagenthost stdio"
        };
    }

    private static void BuildLaunchEnvironment(
        MidTermSettings settings,
        string? executablePath,
        string? profileDirectory,
        MidTermInstanceIdentity instanceIdentity,
        out Dictionary<string, string?> environmentOverrides,
        out List<string> pathPrependEntries)
    {
        environmentOverrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        pathPrependEntries = [];

        static void AddPathEntry(List<string> entries, string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            if (!entries.Exists(existing => string.Equals(existing, directory, StringComparison.OrdinalIgnoreCase)))
            {
                entries.Add(directory);
            }
        }

        var executableDirectory = Path.GetDirectoryName(executablePath);
        if (!string.IsNullOrWhiteSpace(executableDirectory))
        {
            AddPathEntry(pathPrependEntries, executableDirectory);
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var directory in AiCliCommandLocator.GetWellKnownWindowsCommandDirectories(
                         Environment.GetEnvironmentVariable("APPDATA"),
                         Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                         Environment.GetEnvironmentVariable("USERPROFILE")))
            {
                AddPathEntry(pathPrependEntries, directory);
            }
        }

        if (!string.IsNullOrWhiteSpace(profileDirectory))
        {
            AppServerControlHostEnvironmentResolver.ApplyProfileEnvironment(
                environmentOverrides,
                profileDirectory,
                pathPrependEntries);
        }

        ApplyProviderSettings(environmentOverrides, settings);
        environmentOverrides["MIDTERM_INSTANCE_ID"] = instanceIdentity.InstanceId;
        environmentOverrides["MIDTERM_OWNER_TOKEN"] = instanceIdentity.OwnerToken;
    }

    private static string? ResolveConfiguredUserProfileDirectory(MidTermSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(settings.RunAsUser))
        {
            return null;
        }

        return AppServerControlHostEnvironmentResolver.ResolveWindowsProfileDirectory(settings.RunAsUser, settings.RunAsUserSid);
    }

    private static string? ResolvePreferredProfileDirectory(MidTermSettings settings, string? executablePath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return ResolveConfiguredUserProfileDirectory(settings)
               ?? AppServerControlHostEnvironmentResolver.ResolveWindowsProfileDirectoryFromExecutablePath(executablePath)
               ?? AppServerControlHostEnvironmentResolver.ResolveCurrentWindowsProfileDirectory();
    }

    private static void ApplyProviderSettings(IDictionary<string, string?> environment, MidTermSettings settings)
    {
        environment["MIDTERM_APP_SERVER_CONTROL_CODEX_YOLO_DEFAULT"] = settings.CodexYoloDefault ? "true" : "false";
        environment["MIDTERM_APP_SERVER_CONTROL_CODEX_DEFAULT_MODEL"] = NormalizeOptionalValue(settings.CodexDefaultAppServerControlModel) ?? string.Empty;
        environment["MIDTERM_APP_SERVER_CONTROL_CODEX_ENVIRONMENT_VARIABLES"] = settings.CodexEnvironmentVariables ?? string.Empty;
        environment["MIDTERM_APP_SERVER_CONTROL_CLAUDE_DEFAULT_MODEL"] = NormalizeOptionalValue(settings.ClaudeDefaultAppServerControlModel) ?? string.Empty;
        environment["MIDTERM_APP_SERVER_CONTROL_CLAUDE_ENVIRONMENT_VARIABLES"] = settings.ClaudeEnvironmentVariables ?? string.Empty;
        environment["MIDTERM_APP_SERVER_CONTROL_CLAUDE_DANGEROUSLY_SKIP_PERMISSIONS"] =
            settings.ClaudeDangerouslySkipPermissionsDefault ? "true" : "false";
        environment["MIDTERM_APP_SERVER_CONTROL_GROK_DEFAULT_MODEL"] = "grok-build-0.1";
        environment["MIDTERM_APP_SERVER_CONTROL_GROK_ENVIRONMENT_VARIABLES"] = string.Empty;
        environment["MIDTERM_APP_SERVER_CONTROL_GROK_ALWAYS_APPROVE_DEFAULT"] = "false";
    }

    private static string? NormalizeOptionalValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    internal static string? ResolveInstalledHostExecutablePath(string settingsDirectory, string? baseDirectory = null)
    {
        var executableName = OperatingSystem.IsWindows() ? "mtagenthost.exe" : "mtagenthost";
        var baseDir = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
        foreach (var candidate in EnumerateInstalledHostExecutableCandidates(baseDir, settingsDirectory, executableName))
        {
            var installedDll = Path.ChangeExtension(candidate, ".dll");
            var installedRuntimeConfig = Path.ChangeExtension(candidate, ".runtimeconfig.json");
            var hasFrameworkPayload = File.Exists(installedDll);
            var looksLikeBrokenAppHost = OperatingSystem.IsWindows() && File.Exists(installedRuntimeConfig) && !hasFrameworkPayload;
            if (File.Exists(candidate) && !looksLikeBrokenAppHost)
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool TryResolveLaunch(string profile, string mode, string settingsDirectory, out HostLaunch launch)
    {
        var baseDir = AppContext.BaseDirectory;
        var installedExecutable = ResolveInstalledHostExecutablePath(settingsDirectory, baseDir);
        if (!string.IsNullOrWhiteSpace(installedExecutable))
        {
            launch = new HostLaunch(
                installedExecutable,
                string.Equals(mode, SyntheticMode, StringComparison.Ordinal)
                    ? ["--stdio", "--synthetic", profile]
                    : ["--stdio"]);
            return true;
        }

        return TryResolveDevLaunch(profile, mode, baseDir, out launch);
    }

    private static IEnumerable<string> EnumerateInstalledHostExecutableCandidates(string baseDir, string settingsDirectory, string executableName)
    {
        var primaryPath = Path.Combine(baseDir, executableName);
        yield return primaryPath;

        if (!string.IsNullOrWhiteSpace(settingsDirectory))
        {
            var fallbackPath = UpdateService.GetAgentHostFallbackPath(settingsDirectory);
            if (!string.Equals(fallbackPath, primaryPath, StringComparison.Ordinal))
            {
                yield return fallbackPath;
            }
        }
    }

    internal static string? ResolveDevHostDllPath(string baseDir)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        var preferredConfiguration = baseDir.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : baseDir.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                ? "Debug"
                : null;
        var configurations = preferredConfiguration is null
            ? new[] { "Debug", "Release" }
            : new[] { preferredConfiguration, string.Equals(preferredConfiguration, "Release", StringComparison.Ordinal) ? "Debug" : "Release" };
        foreach (var configuration in configurations)
        {
            var devDllCandidates = new[]
            {
                Path.Combine(repoRoot, "Ai.Tlbx.MidTerm.AgentHost", "bin", configuration, "net10.0", "win-x64", "mtagenthost.dll"),
                Path.Combine(repoRoot, "Ai.Tlbx.MidTerm.AgentHost", "bin", configuration, "net10.0", "win-x64", "Ai.Tlbx.MidTerm.AgentHost.dll"),
                Path.Combine(repoRoot, "Ai.Tlbx.MidTerm.AgentHost", "bin", configuration, "net10.0", "mtagenthost.dll"),
                Path.Combine(repoRoot, "Ai.Tlbx.MidTerm.AgentHost", "bin", configuration, "net10.0", "Ai.Tlbx.MidTerm.AgentHost.dll")
            };
            var devDll = devDllCandidates.FirstOrDefault(File.Exists);
            if (!string.IsNullOrWhiteSpace(devDll))
            {
                return devDll;
            }
        }

        return null;
    }

    private static bool TryResolveDevLaunch(string profile, string mode, string baseDir, out HostLaunch launch)
    {
        var devDll = ResolveDevHostDllPath(baseDir);
        if (!string.IsNullOrWhiteSpace(devDll))
        {
            var dotnetHost = ResolveDotNetHostPath();
            if (string.IsNullOrWhiteSpace(dotnetHost))
            {
                launch = default;
                return false;
            }

            launch = new HostLaunch(
                dotnetHost,
                string.Equals(mode, SyntheticMode, StringComparison.Ordinal)
                    ? [devDll, "--stdio", "--synthetic", profile]
                    : [devDll, "--stdio"]);
            return true;
        }

        launch = default;
        return false;
    }

    private static bool IsTestBinaryBaseDirectory(string baseDir)
    {
        return baseDir.Contains("Ai.Tlbx.MidTerm.UnitTests", StringComparison.OrdinalIgnoreCase) ||
               baseDir.Contains("Ai.Tlbx.MidTerm.Tests", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveDotNetHostPath()
    {
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(hostPath) && File.Exists(hostPath))
        {
            return hostPath;
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        return AiCliCommandLocator.FindExecutableInPath("dotnet");
    }

    private HostRuntimeState GetRequiredState(string sessionId)
    {
        if (!_states.TryGetValue(sessionId, out var state))
        {
            throw new InvalidOperationException($"App Server Controller host runtime is not attached: missing state for {sessionId}.");
        }

        if (state.Input is null)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"App Server Controller host runtime is not attached: state exists for {sessionId} but input is null (status={state.Status}, hostPid={state.HostPid}, hasConnection={(state.Connection is not null).ToString().ToLowerInvariant()}, hasProcess={(state.Process is not null).ToString().ToLowerInvariant()})."));
        }

        return state;
    }

    private static async Task DisposeStreamsAsync(HostRuntimeState state)
    {
        foreach (var pending in state.PendingCommands.Values)
        {
            pending.TrySetException(new InvalidOperationException("App Server Controller host runtime connection is closing."));
        }

        state.PendingCommands.Clear();
        state.DisposeConnection();
        CompleteHistorySubscribers(state);

        if (state.ReaderTask is not null)
        {
            await Task.WhenAny(state.ReaderTask, Task.Delay(250)).ConfigureAwait(false);
            state.ReaderTask = null;
        }
    }

    private async Task DisposeOwnedStateAsync(HostRuntimeState state, bool terminateHost)
    {
        var hostPid = state.HostPid;
        try
        {
            await DisposeStateAsync(state, terminateHost).ConfigureAwait(false);
        }
        finally
        {
            CleanupOwnedHostRegistration(state.SessionId, hostPid);
        }
    }

    private void CleanupOwnedHostRegistration(string sessionId, int hostPid)
    {
        _ownershipRegistry.Remove(sessionId);
        if (hostPid > 0)
        {
            AppServerControlHostEndpointDiscovery.CleanupEndpoint(_instanceIdentity.InstanceId, sessionId, hostPid);
        }
    }

    private async Task TerminateOwnedHostAsync(string sessionId, int hostPid)
    {
        try
        {
            await TerminateHostProcessAsync(null, hostPid).ConfigureAwait(false);
        }
        finally
        {
            CleanupOwnedHostRegistration(sessionId, hostPid);
        }
    }

    private static async Task DisposeStateAsync(HostRuntimeState state, bool terminateHost)
    {
        foreach (var pending in state.PendingCommands.Values)
        {
            pending.TrySetException(new InvalidOperationException("App Server Controller host runtime is shutting down."));
        }

        state.PendingCommands.Clear();
        CompleteHistorySubscribers(state);
        state.CachedHistoryWindow = null;

        try
        {
            if (terminateHost)
            {
                await TerminateHostProcessAsync(state.Process, state.HostPid).ConfigureAwait(false);
            }
        }
        catch
        {
        }

        state.DisposeConnection();
        state.DisposeOwnedLaunch();

        if (state.ReaderTask is not null)
        {
            await Task.WhenAny(state.ReaderTask, Task.Delay(250)).ConfigureAwait(false);
        }

        if (state.ErrorTask is not null)
        {
            await Task.WhenAny(state.ErrorTask, Task.Delay(250)).ConfigureAwait(false);
        }

        state.Status = HostRuntimeStatus.Stopped;
        state.HostPid = 0;
    }

    private static void CompleteHistorySubscribers(HostRuntimeState state)
    {
        lock (state.HistorySubscribersSync)
        {
            foreach (var subscriber in state.HistoryPatchSubscribers)
            {
                subscriber.Writer.TryComplete();
            }

            state.HistoryPatchSubscribers.Clear();
        }
    }

    private static async Task TerminateHostProcessAsync(Process? launchedProcess, int hostPid)
    {
        if (launchedProcess is { HasExited: false } process)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            return;
        }

        if (hostPid <= 0)
        {
            return;
        }

        try
        {
            using var externalProcess = Process.GetProcessById(hostPid);
            if (externalProcess.HasExited)
            {
                return;
            }

            externalProcess.Kill(entireProcessTree: true);
            await externalProcess.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed class HostRuntimeState
    {
        public HostRuntimeState(string sessionId)
        {
            SessionId = sessionId;
        }

        public string SessionId { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ConcurrentDictionary<string, TaskCompletionSource<AppServerControlHostCommandResultEnvelope>> PendingCommands { get; } = new(StringComparer.Ordinal);
        public string? Profile { get; set; }
        public string? WorkingDirectory { get; set; }
        public string TransportKey { get; set; } = string.Empty;
        public string TransportLabel { get; set; } = string.Empty;
        public string? LastError { get; set; }
        public HostRuntimeStatus Status { get; set; }
        public int HostPid { get; set; }
        public HostTransportConnection? Connection { get; private set; }
        public Process? Process { get; private set; }
        public StreamWriter? Input { get; private set; }
        public StreamReader? Output { get; private set; }
        public StreamReader? Error { get; private set; }
        public Task? ReaderTask { get; set; }
        public Task? ErrorTask { get; set; }
        public AppServerControlHistoryWindowResponse? CachedHistoryWindow { get; set; }
        public Lock HistorySubscribersSync { get; } = new();
        public List<AppServerControlHistoryPatchSubscriber> HistoryPatchSubscribers { get; } = [];

        public void AttachOwnedConnection(HostTransportConnection connection)
        {
            DisposeConnection();
            Connection = connection;
            Input = connection.Writer;
            Output = connection.Reader;
        }

        public void AttachOwnedLaunch(Process process, StreamReader error)
        {
            DisposeOwnedLaunch();
            Process = process;
            Error = error;
        }

        public void DisposeConnection()
        {
            var connection = Connection;
            Connection = null;
            Input = null;
            Output = null;
            try { connection?.Dispose(); } catch { }
        }

        public void DisposeOwnedLaunch()
        {
            var error = Error;
            var process = Process;
            Error = null;
            Process = null;
            try { error?.Dispose(); } catch { }
            try { process?.Dispose(); } catch { }
        }
    }

    private sealed class AppServerControlHistoryPatchSubscriber(ChannelWriter<AppServerControlHistoryPatch> writer)
    {
        public ChannelWriter<AppServerControlHistoryPatch> Writer { get; } = writer;
    }

    private sealed class HostTransportConnection : IDisposable
    {
        private readonly IDisposable _handle;

        private HostTransportConnection(IDisposable handle, Stream stream)
        {
            _handle = handle;
            Reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            Writer = new StreamWriter(stream, Utf8NoBom, bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
        }

        public StreamReader Reader { get; }
        public StreamWriter Writer { get; }

        [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP001:Dispose created", Justification = "Ownership of the created pipe or socket transport is transferred into HostTransportConnection.")]
        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Socket ownership is transferred to NetworkStream with ownsSocket: true and disposed on failure.")]
        public static async Task<HostTransportConnection> ConnectAsync(string endpoint, int timeoutMs, CancellationToken ct)
        {
            if (OperatingSystem.IsWindows())
            {
                var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
                return new HostTransportConnection(pipe, pipe);
            }

            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeoutMs);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), timeoutCts.Token).ConfigureAwait(false);
                var stream = new NetworkStream(socket, ownsSocket: true);
                return new HostTransportConnection(stream, stream);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            try { Writer.Dispose(); } catch { }
            try { Reader.Dispose(); } catch { }
            try { _handle.Dispose(); } catch { }
        }
    }

    private static IReadOnlyList<string> BuildIpcLaunchArguments(
        IReadOnlyList<string> args,
        string sessionId,
        string instanceId,
        string ownerToken)
    {
        var updated = new List<string>(args.Count + 7);
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--stdio", StringComparison.Ordinal))
            {
                updated.Add("--ipc");
            }
            else
            {
                updated.Add(arg);
            }
        }

        updated.Add("--session-id");
        updated.Add(sessionId);
        updated.Add("--instance-id");
        updated.Add(instanceId);
        updated.Add("--owner-token");
        updated.Add(ownerToken);
        return updated;
    }

    private readonly record struct HostLaunch(string FileName, IReadOnlyList<string> Arguments);

    private enum HostRuntimeStatus
    {
        None,
        Starting,
        Ready,
        Running,
        Error,
        Stopped
    }
}

public sealed class AppServerControlHistoryPatchSubscription : IDisposable
{
    private readonly SubscriptionState _state;
    private int _disposed;

    internal AppServerControlHistoryPatchSubscription(ChannelReader<AppServerControlHistoryPatch> reader, SubscriptionState state)
    {
        Reader = reader;
        _state = state;
    }

    public ChannelReader<AppServerControlHistoryPatch> Reader { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _state.Close();
    }
}

internal sealed class SubscriptionState
{
    private readonly Action _dispose;
    private int _disposed;

    public SubscriptionState(Action dispose)
    {
        _dispose = dispose;
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _dispose();
        }
    }
}



