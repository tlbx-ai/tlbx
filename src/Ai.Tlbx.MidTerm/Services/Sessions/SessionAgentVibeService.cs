using System.Globalization;
using Ai.Tlbx.MidTerm.Models.Sessions;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed class SessionAgentVibeService
{
    private readonly TtyHostSessionManager _sessionManager;
    private readonly SessionTelemetryService _sessionTelemetry;
    private readonly SessionSupervisorService _sessionSupervisor;
    private readonly AiCliProfileService _profileService;
    private readonly AiCliCapabilityService _capabilityService;
    private readonly SessionAgentFeedService _feedService;
    private readonly SessionAppServerControlRuntimeService _appServerControlRuntime;

    public SessionAgentVibeService(
        TtyHostSessionManager sessionManager,
        SessionTelemetryService sessionTelemetry,
        SessionSupervisorService sessionSupervisor,
        AiCliProfileService profileService,
        AiCliCapabilityService capabilityService,
        SessionAgentFeedService feedService,
        SessionAppServerControlRuntimeService appServerControlRuntime)
    {
        _sessionManager = sessionManager;
        _sessionTelemetry = sessionTelemetry;
        _sessionSupervisor = sessionSupervisor;
        _profileService = profileService;
        _capabilityService = capabilityService;
        _feedService = feedService;
        _appServerControlRuntime = appServerControlRuntime;
    }

    public async Task<AgentSessionVibeResponse?> BuildVibeAsync(
        string sessionId,
        int tailLines = 80,
        int activitySeconds = 90,
        int bellLimit = 8,
        CancellationToken ct = default)
    {
        var session = _sessionManager.GetSessionList().Sessions.FirstOrDefault(s => s.Id == sessionId);

        if (session is null)
        {
            return null;
        }

        session.Supervisor = _sessionSupervisor.Describe(session);
        var supervisor = session.Supervisor ?? new SessionSupervisorInfoDto();
        var profile = _profileService.NormalizeProfile(supervisor.Profile, session);
        var capability = await _capabilityService.DescribeAsync(profile, session.AppServerControlOnly, ct).ConfigureAwait(false);
        var providerLabel = PrettifyProfile(profile);
        var stateLabel = PrettifySupervisorState(supervisor.State);
        var now = DateTimeOffset.UtcNow;

        var activity = _sessionTelemetry.GetActivity(sessionId, activitySeconds, bellLimit);
        var tailText = string.Empty;
        if (!session.AppServerControlOnly)
        {
            var snapshot = await _sessionManager.GetBufferAsync(sessionId, ct: ct).ConfigureAwait(false);
            tailText = BuildTailText(snapshot?.Data, tailLines);
        }

        if (_appServerControlRuntime.TryGetRuntimeSummary(sessionId, out var runtimeSummary))
        {
            return BuildNativeVibe(
                sessionId,
                profile,
                providerLabel,
                capability,
                supervisor,
                activity,
                tailLines,
                tailText,
                runtimeSummary,
                now);
        }

        return new AgentSessionVibeResponse
        {
            SessionId = sessionId,
            Source = session.AppServerControlOnly ? "appServerControl-pending" : "fallback",
            GeneratedAt = now,
            Header = new AgentSessionVibeHeader
            {
                Title = BuildTitle(session.Name, providerLabel),
                Subtitle = $"{providerLabel} • {stateLabel}",
                Provider = profile,
                ProviderLabel = providerLabel,
                State = supervisor.State,
                StateLabel = stateLabel,
                NeedsAttention = supervisor.NeedsAttention,
                AttentionReason = supervisor.AttentionReason,
                TransportSummary = BuildTransportSummary(capability.Lane, capability.Capabilities),
                Chips = BuildChips(providerLabel, supervisor.State, supervisor.AttentionReason, capability.Lane)
            },
            Lane = capability.Lane,
            Capabilities = capability.Capabilities,
                Overview = new AgentSessionVibeOverview
            {
                StateValue = stateLabel,
                StateMeta = session.AppServerControlOnly
                    ? capability.Lane.Label
                    : supervisor.NeedsAttention ? "Needs attention" : "Stable",
                ActivityValue = $"{FormatBytes(activity.CurrentBytesPerSecond)}/s",
                ActivityMeta = $"Heat {FormatHeat(activity.CurrentHeat)}",
                LastOutputValue = FormatRelativeTime(activity.LastOutputAt, now),
                LastOutputMeta = FormatAbsoluteTime(activity.LastOutputAt),
                BellsValue = activity.TotalBellCount.ToString(CultureInfo.InvariantCulture),
                BellsMeta = activity.LastBellAt is null
                    ? "No recent bell"
                    : $"Last bell {FormatRelativeTime(activity.LastBellAt, now)}"
            },
            Activities = _feedService.RefreshAndGet(
                sessionId,
                profile,
                capability.Lane,
                supervisor,
                activity,
                tailText).ToList(),
            Heatmap = activity.Heatmap,
            Terminal = new AgentSessionVibeTerminal
            {
                TailLineCount = Math.Max(1, tailLines),
                TailText = tailText,
                EmptyMessage = session.AppServerControlOnly
                    ? "Explicit App Server Controller sessions do not include a terminal surface."
                    : "No recent output in the terminal buffer."
            }
        };
    }

    private static AgentSessionVibeResponse BuildNativeVibe(
        string sessionId,
        string profile,
        string providerLabel,
        AiCliCapabilitySnapshot capability,
        SessionSupervisorInfoDto supervisor,
        SessionActivityResponse activity,
        int tailLines,
        string terminalTailText,
        AppServerControlRuntimeSummary runtimeSummary,
        DateTimeOffset now)
    {
        var transportSummary = $"{runtimeSummary.TransportLabel} is attached as the native App Server Controller runtime. App Server Controller remains provider-owned and separate from any terminal surface.";
        var chips = new List<AgentSessionVibeChip>
        {
            new()
            {
                Text = providerLabel,
                Tone = "profile"
            },
            new()
            {
                Text = runtimeSummary.StatusLabel,
                Tone = runtimeSummary.Status == "error" ? "attention" : runtimeSummary.Status == "running" ? "positive" : "info"
            },
            new()
            {
                Text = "Native runtime",
                Tone = "positive"
            }
        };

        if (!string.IsNullOrWhiteSpace(runtimeSummary.PendingQuestion))
        {
            chips.Add(new AgentSessionVibeChip
            {
                Text = "Waiting for input",
                Tone = "attention"
            });
        }

        var capabilities = capability.Capabilities
            .Select(capabilityItem => capabilityItem.Key == "native"
                ? new AgentSessionVibeCapability
                {
                    Key = capabilityItem.Key,
                    Label = capabilityItem.Label,
                    Status = "live",
                    StatusLabel = "Live",
                    Detail = runtimeSummary.TransportLabel + " is actively feeding App Server Controller for this session."
                }
                : capabilityItem.Key == "terminal"
                    ? new AgentSessionVibeCapability
                    {
                        Key = capabilityItem.Key,
                        Label = capabilityItem.Label,
                        Status = capabilityItem.Status,
                        StatusLabel = capabilityItem.StatusLabel,
                        Detail = "xterm still owns the visible terminal surface and remains available beside App Server Controller."
                    }
                    : capabilityItem)
            .ToList();

        var nativeTail = !string.IsNullOrWhiteSpace(runtimeSummary.AssistantText)
            ? runtimeSummary.AssistantText
            : !string.IsNullOrWhiteSpace(runtimeSummary.UnifiedDiff)
                ? runtimeSummary.UnifiedDiff
                : terminalTailText;

        return new AgentSessionVibeResponse
        {
            SessionId = sessionId,
            Source = "native",
            GeneratedAt = now,
            Header = new AgentSessionVibeHeader
            {
                Title = providerLabel,
                Subtitle = $"{providerLabel} • {runtimeSummary.StatusLabel}",
                Provider = profile,
                ProviderLabel = providerLabel,
                State = runtimeSummary.Status,
                StateLabel = runtimeSummary.StatusLabel,
                NeedsAttention = runtimeSummary.Status == "error" || supervisor.NeedsAttention,
                AttentionReason = runtimeSummary.LastError ?? supervisor.AttentionReason,
                TransportSummary = transportSummary,
                Chips = chips
            },
            Lane = new AgentSessionVibeLane
            {
                Mode = "native-live",
                Tone = runtimeSummary.Status == "error" ? "attention" : runtimeSummary.Status == "running" ? "positive" : "info",
                Label = "Native App Server Controller",
                Detail = transportSummary
            },
            Capabilities = capabilities,
            Overview = new AgentSessionVibeOverview
            {
                StateValue = runtimeSummary.StatusLabel,
                StateMeta = runtimeSummary.PendingQuestion is null ? "Native runtime active" : "Waiting for input",
                ActivityValue = $"{runtimeSummary.Activities.Count.ToString(CultureInfo.InvariantCulture)} events",
                ActivityMeta = runtimeSummary.TransportLabel,
                LastOutputValue = FormatRelativeTime(runtimeSummary.LastEventAt, now),
                LastOutputMeta = FormatAbsoluteTime(runtimeSummary.LastEventAt),
                BellsValue = activity.TotalBellCount.ToString(CultureInfo.InvariantCulture),
                BellsMeta = activity.LastBellAt is null
                    ? "No recent terminal bell"
                    : $"Last bell {FormatRelativeTime(activity.LastBellAt, now)}"
            },
            Activities = runtimeSummary.Activities,
            Heatmap = activity.Heatmap,
            Terminal = new AgentSessionVibeTerminal
            {
                TailLineCount = Math.Max(1, tailLines),
                TailText = nativeTail?.TrimEnd() ?? string.Empty,
                EmptyMessage = "No native App Server Controller output has arrived yet."
            }
        };
    }

    private static string BuildTailText(byte[]? buffer, int tailLines)
    {
        if (buffer is null || buffer.Length == 0)
        {
            return string.Empty;
        }

        var text = TerminalOutputSanitizer.Decode(buffer);
        text = TerminalOutputSanitizer.StripEscapeSequences(text);
        text = TerminalOutputSanitizer.TailLines(text, Math.Max(1, tailLines), out _, out _);
        return text.TrimEnd();
    }

    private static string BuildTitle(string? sessionName, string providerLabel)
    {
        return string.IsNullOrWhiteSpace(sessionName)
            ? providerLabel
            : sessionName.Trim();
    }

    private static List<AgentSessionVibeChip> BuildChips(
        string providerLabel,
        string state,
        string? attentionReason,
        AgentSessionVibeLane lane)
    {
        var chips = new List<AgentSessionVibeChip>
        {
            new()
            {
                Text = providerLabel,
                Tone = "profile"
            },
            new()
            {
                Text = PrettifySupervisorState(state),
                Tone = state
            },
            new()
            {
                Text = lane.Label,
                Tone = lane.Tone
            }
        };

        if (!string.IsNullOrWhiteSpace(attentionReason))
        {
            chips.Add(new AgentSessionVibeChip
            {
                Text = PrettifyAttentionReason(attentionReason),
                Tone = "attention"
            });
        }

        return chips;
    }

    private static string PrettifyProfile(string profile)
    {
        return profile switch
        {
            AiCliProfileService.CodexProfile => "Codex",
            AiCliProfileService.ClaudeProfile => "Claude",
            AiCliProfileService.GrokProfile => "Grok",
            AiCliProfileService.OpenCodeProfile => "OpenCode",
            AiCliProfileService.GenericAiProfile => "Generic Agent",
            AiCliProfileService.ShellProfile => "Shell",
            _ => profile
        };
    }

    private static string PrettifySupervisorState(string state)
    {
        return state switch
        {
            SessionSupervisorService.BusyTurnState => "Busy turn",
            SessionSupervisorService.IdlePromptState => "Idle prompt",
            SessionSupervisorService.BlockedState => "Blocked",
            SessionSupervisorService.ShellState => "Shell only",
            SessionSupervisorService.DeadState => "Exited",
            _ => "Unknown"
        };
    }

    private static string PrettifyAttentionReason(string? attentionReason)
    {
        return attentionReason switch
        {
            "session-exited" => "Session exited",
            "prompt-not-acknowledged" => "Prompt not acknowledged",
            "ai-cli-not-running" => "AI CLI not running",
            null or "" => "Needs attention",
            _ => attentionReason
        };
    }

    private static string BuildTransportSummary(
        AgentSessionVibeLane lane,
        IReadOnlyList<AgentSessionVibeCapability> capabilities)
    {
        var capabilitySummary = capabilities
            .Where(static capability => !string.Equals(capability.Key, "terminal", StringComparison.Ordinal))
            .Select(static capability => $"{capability.Label}: {capability.StatusLabel}")
            .ToArray();
        return capabilitySummary.Length == 0
            ? lane.Detail
            : $"{lane.Detail} {string.Join(" | ", capabilitySummary)}";
    }

    private static string FormatBytes(long value)
    {
        string[] suffixes = ["B", "KB", "MB", "GB"];
        double size = Math.Max(value, 0);
        var suffixIndex = 0;
        while (size >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            size /= 1024;
            suffixIndex++;
        }

        var format = suffixIndex == 0 ? "0" : "0.#";
        return size.ToString(format, CultureInfo.InvariantCulture) + " " + suffixes[suffixIndex];
    }

    private static string FormatHeat(double heat)
    {
        return Math.Round(Math.Clamp(heat, 0, 1) * 100, MidpointRounding.AwayFromZero)
            .ToString(CultureInfo.InvariantCulture) + "%";
    }

    private static string FormatRelativeTime(DateTimeOffset? timestamp, DateTimeOffset now)
    {
        if (timestamp is null)
        {
            return "Never";
        }

        var delta = now - timestamp.Value;
        if (delta < TimeSpan.FromSeconds(5))
        {
            return "Just now";
        }

        if (delta < TimeSpan.FromMinutes(1))
        {
            return $"{Math.Max(1, (int)Math.Floor(delta.TotalSeconds)).ToString(CultureInfo.InvariantCulture)}s ago";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)Math.Floor(delta.TotalMinutes)).ToString(CultureInfo.InvariantCulture)}m ago";
        }

        if (delta < TimeSpan.FromDays(1))
        {
            return $"{Math.Max(1, (int)Math.Floor(delta.TotalHours)).ToString(CultureInfo.InvariantCulture)}h ago";
        }

        return $"{Math.Max(1, (int)Math.Floor(delta.TotalDays)).ToString(CultureInfo.InvariantCulture)}d ago";
    }

    private static string FormatAbsoluteTime(DateTimeOffset? timestamp)
    {
        return timestamp is null
            ? "Unknown"
            : timestamp.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
