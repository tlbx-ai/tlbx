using Ai.Tlbx.MidTerm.Models.Sessions;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed class SessionSupervisorService
{
    public const string UnknownState = "unknown";
    public const string ShellState = "shell";
    public const string IdlePromptState = "idle-prompt";
    public const string BusyTurnState = "busy-turn";
    public const string BlockedState = "blocked";
    public const string DeadState = "dead";

    private readonly SessionHeatService _heatService;
    private readonly AiCliProfileService _profileService;
    private readonly SessionAppServerControlHostRuntimeService? _appServerControlHostRuntime;
    private readonly AcpAgentCatalogService? _acpAgentCatalog;

    public SessionSupervisorService(
        SessionHeatService heatService,
        AiCliProfileService profileService,
        SessionAppServerControlHostRuntimeService? appServerControlHostRuntime = null,
        AcpAgentCatalogService? acpAgentCatalog = null)
    {
        _heatService = heatService;
        _profileService = profileService;
        _appServerControlHostRuntime = appServerControlHostRuntime;
        _acpAgentCatalog = acpAgentCatalog;
    }

    public SessionSupervisorInfoDto Describe(SessionInfoDto session)
    {
        var telemetry = _heatService.GetSnapshot(session.Id);
        var profile = _profileService.NormalizeProfile(null, session);
        var now = DateTimeOffset.UtcNow;

        var (state, customReason) = ResolveStateAndReason(session, profile, telemetry, now);
        var needsAttention = session.AgentControlled &&
            (state is DeadState or BlockedState or ShellState);

        var attentionReason = needsAttention
            ? customReason ?? state switch
            {
                DeadState => "session-exited",
                BlockedState => "prompt-not-acknowledged",
                ShellState => "ai-cli-not-running",
                _ => null
            }
            : null;

        return new SessionSupervisorInfoDto
        {
            State = state,
            Profile = profile,
            NeedsAttention = needsAttention,
            AttentionReason = attentionReason,
            AttentionScore = GetAttentionScore(session, state, telemetry),
            LastInputAt = telemetry.LastInputAt,
            LastOutputAt = telemetry.LastOutputAt,
            LastBellAt = telemetry.LastBellAt,
            CurrentHeat = telemetry.CurrentHeat,
            LastTextOutputAt = telemetry.LastTextOutputAt,
            TextActivityAgeMs = telemetry.LastTextOutputAt is { } textAt ? Math.Max(0, (now - textAt).TotalMilliseconds) : null
        };
    }

    public SessionAttentionResponse DescribeFleet(IEnumerable<SessionInfoDto> sessions, bool agentOnly)
    {
        var filtered = agentOnly
            ? sessions.Where(static session => session.AgentControlled)
            : sessions;

        var items = filtered
            .Select(session =>
            {
                session.Supervisor = Describe(session);
                return new SessionAttentionItem
                {
                    Session = session,
                    AttentionScore = session.Supervisor.AttentionScore
                };
            })
            .OrderByDescending(static item => item.AttentionScore)
            .ThenBy(static item => item.Session.Order)
            .ToList();

        return new SessionAttentionResponse
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            AgentOnly = agentOnly,
            AttentionCount = items.Count(static item => item.Session.Supervisor?.NeedsAttention == true),
            Sessions = items
        };
    }

    private (string State, string? AttentionReason) ResolveStateAndReason(
        SessionInfoDto session,
        string profile,
        SessionHeatSnapshot telemetry,
        DateTimeOffset now)
    {
        if (!session.IsRunning)
        {
            return (DeadState, "session-exited");
        }

        if (session.AppServerControlOnly || _appServerControlHostRuntime?.OwnsSession(session.Id) == true)
        {
            return ResolveAppServerControlState(session, profile);
        }

        if (!IsInteractiveAiProfile(profile))
        {
            return (ShellState, "ai-cli-not-running");
        }

        if (telemetry.LastInputAt is { } lastInput &&
            lastInput >= now.AddMinutes(-5) &&
            (telemetry.LastOutputAt is null || lastInput > telemetry.LastOutputAt.Value.AddSeconds(15)))
        {
            return (BlockedState, "prompt-not-acknowledged");
        }

        if (telemetry.LastBellAt is { } lastBell &&
            lastBell >= now.AddSeconds(-20) &&
            telemetry.CurrentHeat < 0.12)
        {
            return (BlockedState, "prompt-not-acknowledged");
        }

        if (telemetry.LastOutputAt is { } lastOutput &&
            lastOutput >= now.AddSeconds(-4) &&
            telemetry.CurrentHeat > 0.02)
        {
            return (BusyTurnState, null);
        }

        return (IdlePromptState, null);
    }

    private (string State, string? AttentionReason) ResolveAppServerControlState(
        SessionInfoDto session,
        string profile)
    {
        if (_appServerControlHostRuntime is not null)
        {
            if (_appServerControlHostRuntime.TryGetCachedHistoryWindow(session.Id, out var history))
            {
                if (history.Requests.Any(static request =>
                        string.Equals(request.State, "open", StringComparison.OrdinalIgnoreCase)))
                {
                    return (BlockedState, "approval-required");
                }

                if (string.Equals(history.CurrentTurn.State, "running", StringComparison.OrdinalIgnoreCase) ||
                    history.Session.State is "running" or "starting" or "connecting")
                {
                    return (BusyTurnState, null);
                }

                if (history.Session.State is "error" or "failed" ||
                    !string.IsNullOrWhiteSpace(history.Session.LastError))
                {
                    return (BlockedState, "agent-runtime-error");
                }
            }

            if (_appServerControlHostRuntime.TryGetRuntimeSummary(session.Id, out var summary))
            {
                if (string.Equals(summary.Status, "error", StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrWhiteSpace(summary.LastError))
                {
                    return (BlockedState, "agent-runtime-error");
                }

                if (summary.Status is "starting" or "connecting")
                {
                    return (BusyTurnState, null);
                }
            }

            if (_appServerControlHostRuntime.OwnsSession(session.Id) ||
                _appServerControlHostRuntime.IsEnabledFor(profile))
            {
                return (IdlePromptState, null);
            }
        }

        return IsInteractiveAiProfile(profile)
            ? (IdlePromptState, null)
            : (ShellState, "ai-cli-not-running");
    }

    private bool IsInteractiveAiProfile(string profile)
    {
        return _profileService.IsInteractiveAi(profile) ||
            _acpAgentCatalog?.ContainsProfile(profile) == true;
    }

    private static int GetAttentionScore(
        SessionInfoDto session,
        string state,
        SessionHeatSnapshot telemetry)
    {
        var score = state switch
        {
            DeadState => 100,
            BlockedState => 90,
            ShellState when session.AgentControlled => 80,
            IdlePromptState => 35,
            BusyTurnState => 10,
            _ => 0
        };

        if (session.AgentControlled)
        {
            score += 5;
        }

        if (telemetry.LastBellAt is { } lastBell &&
            lastBell >= DateTimeOffset.UtcNow.AddMinutes(-2))
        {
            score += 5;
        }

        return score;
    }
}
