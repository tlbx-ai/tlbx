using System.Globalization;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed class SessionAppServerControlRuntimeService : IAsyncDisposable, ISessionAppServerControlHeatSource
{
    private readonly AiCliProfileService _profileService;
    private readonly SessionAppServerControlHostRuntimeService _hostRuntime;

    public SessionAppServerControlRuntimeService(
        TtyHostSessionManager sessionManager,
        AiCliProfileService profileService,
        SessionAppServerControlHostRuntimeService hostRuntime,
        SettingsService? settingsService = null)
    {
        _profileService = profileService;
        _hostRuntime = hostRuntime;
        sessionManager.OnSessionClosed += Forget;
    }

    public async Task<bool> EnsureAttachedAsync(
        string sessionId,
        SessionInfoDto session,
        string? resumeThreadIdOverride = null,
        CancellationToken ct = default)
    {
        if (IsAttached(sessionId))
        {
            return true;
        }

        var profile = ResolveAttachProfile(session);
        if (!IsAttachableProfile(profile))
        {
            return false;
        }

        var cwd = session.CurrentDirectory;
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
        {
            return false;
        }

        if (!_hostRuntime.IsEnabledFor(profile))
        {
            Log.Warn(() => $"App Server Controller runtime attach refused for {sessionId}: mtagenthost is disabled for profile '{profile}'.");
            return false;
        }

        try
        {
            return await _hostRuntime.EnsureAttachedAsync(sessionId, profile, session, resumeThreadIdOverride, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"App Server Controller host attach failed for {sessionId}. {ex.Message}");
            await _hostRuntime.DetachAsync(sessionId, ct).ConfigureAwait(false);
            return false;
        }
    }

    public async Task DiscoverExistingSessionsAsync(
        TtyHostSessionManager sessionManager,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);

        var recovered = 0;
        foreach (var session in sessionManager.GetSessionList().Sessions)
        {
            ct.ThrowIfCancellationRequested();

            var profile = ResolveAttachProfile(session);
            if (!_hostRuntime.IsEnabledFor(profile) || !_hostRuntime.MayHaveRecoverableHost(session.Id))
            {
                continue;
            }

            try
            {
                if (await _hostRuntime.RecoverExistingHostAsync(session.Id, profile, session, ct: ct).ConfigureAwait(false))
                {
                    recovered++;
                }
            }
            catch (Exception ex)
            {
                Log.Warn(() => $"App Server Controller runtime recovery failed for {session.Id}: {ex.Message}");
                await _hostRuntime.DetachAsync(session.Id, ct).ConfigureAwait(false);
            }
        }

        Log.Info(() => string.Create(CultureInfo.InvariantCulture, $"SessionAppServerControlRuntimeService: Recovered {recovered} owned App Server Controller runtimes on startup."));
    }

    public bool IsAttached(string sessionId)
    {
        return _hostRuntime.OwnsSession(sessionId);
    }

    public Task DetachAsync(string sessionId, CancellationToken ct = default)
    {
        return _hostRuntime.DetachAsync(sessionId, ct);
    }

    public bool TryGetRuntimeSummary(string sessionId, out AppServerControlRuntimeSummary summary)
    {
        return _hostRuntime.TryGetRuntimeSummary(sessionId, out summary);
    }

    public bool HasHistory(string sessionId)
    {
        return _hostRuntime.HasHistory(sessionId);
    }

    public bool TryGetCachedHistoryWindow(string sessionId, out AppServerControlHistoryWindowResponse historyWindow)
    {
        return _hostRuntime.TryGetCachedHistoryWindow(sessionId, out historyWindow);
    }

    public SessionAppServerControlHeatSnapshot GetHeatSnapshot(string sessionId)
    {
        if (!_hostRuntime.TryGetCachedHistoryWindow(sessionId, out var historyWindow))
        {
            return SessionAppServerControlHeatSnapshot.Cold;
        }

        if (!ShouldSurfaceWorkingHeat(historyWindow))
        {
            return SessionAppServerControlHeatSnapshot.Cold;
        }

        return new SessionAppServerControlHeatSnapshot
        {
            CurrentHeat = 1,
            LastActivityAt = historyWindow.Session.LastEventAt ?? historyWindow.CurrentTurn.StartedAt
        };
    }

    public Task<AppServerControlHistoryWindowResponse?> GetHistoryWindowAsync(
        string sessionId,
        int? startIndex = null,
        int? count = null,
        int? viewportWidth = null,
        CancellationToken ct = default)
    {
        return _hostRuntime.GetHistoryWindowAsync(
            sessionId,
            startIndex,
            count,
            viewportWidth,
            ct);
    }

    public AppServerControlHistoryPatchSubscription SubscribeHistoryPatches(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        return _hostRuntime.SubscribeHistoryPatches(sessionId, cancellationToken);
    }

    public async Task<bool> TrySendPromptAsync(
        string sessionId,
        SessionPromptRequest request,
        CancellationToken ct = default)
    {
        if (!_hostRuntime.OwnsSession(sessionId))
        {
            return false;
        }

        return await _hostRuntime.TrySendPromptAsync(sessionId, request, ct).ConfigureAwait(false);
    }

    public async Task<AppServerControlTurnStartResponse> StartTurnAsync(
        string sessionId,
        AppServerControlTurnRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_hostRuntime.OwnsSession(sessionId))
        {
            throw new InvalidOperationException("App Server Controller runtime is not attached.");
        }

        return await _hostRuntime.StartTurnAsync(sessionId, request, ct).ConfigureAwait(false);
    }

    public async Task<AppServerControlCommandAcceptedResponse> InterruptTurnAsync(
        string sessionId,
        AppServerControlInterruptRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_hostRuntime.OwnsSession(sessionId))
        {
            throw new InvalidOperationException("App Server Controller runtime is not attached.");
        }

        return await _hostRuntime.InterruptTurnAsync(sessionId, request, ct).ConfigureAwait(false);
    }

    public async Task<AppServerControlCommandAcceptedResponse> SetGoalAsync(
        string sessionId,
        AppServerControlGoalSetRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_hostRuntime.OwnsSession(sessionId))
        {
            throw new InvalidOperationException("App Server Controller runtime is not attached.");
        }

        return await _hostRuntime.SetGoalAsync(sessionId, request, ct).ConfigureAwait(false);
    }

    public async Task<AppServerControlCommandAcceptedResponse> ResolveRequestAsync(
        string sessionId,
        string requestId,
        AppServerControlRequestDecisionRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(request);

        if (!_hostRuntime.OwnsSession(sessionId))
        {
            throw new InvalidOperationException("App Server Controller runtime is not attached.");
        }

        return await _hostRuntime.ResolveRequestAsync(sessionId, requestId, request, ct).ConfigureAwait(false);
    }

    public async Task<AppServerControlCommandAcceptedResponse> ResolveUserInputAsync(
        string sessionId,
        string requestId,
        AppServerControlUserInputAnswerRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(request);

        if (!_hostRuntime.OwnsSession(sessionId))
        {
            throw new InvalidOperationException("App Server Controller runtime is not attached.");
        }

        return await _hostRuntime.ResolveUserInputAsync(sessionId, requestId, request, ct).ConfigureAwait(false);
    }

    public void Forget(string sessionId)
    {
        _hostRuntime.Forget(sessionId);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private static bool ShouldSurfaceWorkingHeat(AppServerControlHistoryWindowResponse historyWindow)
    {
        if (historyWindow.Requests.Any(static request => string.Equals(request.State, "open", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (IsWorkingTurnState(historyWindow.CurrentTurn.State))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(historyWindow.CurrentTurn.State) &&
               IsWorkingSessionState(historyWindow.Session.State);
    }

    private string ResolveAttachProfile(SessionInfoDto session)
    {
        var detectedProfile = _profileService.NormalizeProfile(null, session);
        if (IsAttachableProfile(detectedProfile))
        {
            return detectedProfile;
        }

        var attachPointProfile = _profileService.NormalizeProfile(session.AgentAttachPoint?.Provider);
        if (IsAttachableProfile(attachPointProfile))
        {
            return attachPointProfile;
        }

        return _hostRuntime.TryResolveRecoverableProfile(session.Id, out var recoverableProfile)
            ? recoverableProfile
            : detectedProfile;
    }

    private static bool IsAttachableProfile(string? profile)
    {
        return profile is AiCliProfileService.CodexProfile or AiCliProfileService.ClaudeProfile or AiCliProfileService.GrokProfile;
    }

    private static bool IsWorkingTurnState(string? state)
    {
        return state?.Trim().ToLowerInvariant() switch
        {
            "running" => true,
            "in_progress" => true,
            "started" => true,
            "submitted" => true,
            _ => false
        };
    }

    private static bool IsWorkingSessionState(string? state)
    {
        return state?.Trim().ToLowerInvariant() switch
        {
            "starting" => true,
            "running" => true,
            _ => false
        };
    }
}

public sealed class AppServerControlRuntimeSummary
{
    public string SessionId { get; init; } = string.Empty;
    public string Profile { get; init; } = string.Empty;
    public string TransportKey { get; init; } = string.Empty;
    public string TransportLabel { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public string? LastError { get; init; }
    public DateTimeOffset? LastEventAt { get; init; }
    public string? AssistantText { get; init; }
    public string? UnifiedDiff { get; init; }
    public string? PendingQuestion { get; init; }
    public List<AgentSessionVibeActivity> Activities { get; init; } = [];
}
