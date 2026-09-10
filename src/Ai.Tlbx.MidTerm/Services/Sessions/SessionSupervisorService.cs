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

    public SessionSupervisorService(
        SessionHeatService heatService,
        AiCliProfileService profileService)
    {
        _heatService = heatService;
        _profileService = profileService;
    }

    public SessionSupervisorInfoDto Describe(SessionInfoDto session)
    {
        var telemetry = _heatService.GetSnapshot(session.Id);
        var profile = _profileService.NormalizeProfile(null, session);
        var now = DateTimeOffset.UtcNow;

        var state = ResolveState(session, profile, telemetry, now);
        var needsAttention = session.AgentControlled &&
            (state is DeadState or BlockedState or ShellState);

        var attentionReason = needsAttention
            ? state switch
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

    private string ResolveState(
        SessionInfoDto session,
        string profile,
        SessionHeatSnapshot telemetry,
        DateTimeOffset now)
    {
        if (!session.IsRunning)
        {
            return DeadState;
        }

        if (!_profileService.IsInteractiveAi(profile))
        {
            return ShellState;
        }

        if (telemetry.LastInputAt is { } lastInput &&
            lastInput >= now.AddMinutes(-5) &&
            (telemetry.LastOutputAt is null || lastInput > telemetry.LastOutputAt.Value.AddSeconds(15)))
        {
            return BlockedState;
        }

        if (telemetry.LastBellAt is { } lastBell &&
            lastBell >= now.AddSeconds(-20) &&
            telemetry.CurrentHeat < 0.12)
        {
            return BlockedState;
        }

        if (telemetry.LastOutputAt is { } lastOutput &&
            lastOutput >= now.AddSeconds(-4) &&
            telemetry.CurrentHeat > 0.02)
        {
            return BusyTurnState;
        }

        return IdlePromptState;
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
