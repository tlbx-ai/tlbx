using System.Collections.Concurrent;
using System.Globalization;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models.Sessions;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed class SessionAgentFeedService
{
    private const int MaxActivityCount = 120;

    private sealed class SessionFeedState
    {
        public Lock SyncRoot { get; } = new();
        public List<AgentSessionVibeActivity> Activities { get; } = [];
        public long NextSequence { get; set; }
        public string? LastProfile { get; set; }
        public string? LastState { get; set; }
        public bool LastNeedsAttention { get; set; }
        public string? LastAttentionReason { get; set; }
        public int LastBellCount { get; set; }
        public bool LastHadTailPreview { get; set; }
        public string? LastForegroundSignature { get; set; }
    }

    private readonly ConcurrentDictionary<string, SessionFeedState> _sessions = new(StringComparer.Ordinal);

    public void NoteWorkerBootstrap(
        string sessionId,
        string profile,
        string? launchCommand,
        IReadOnlyList<string> slashCommands,
        bool guidanceInjected)
    {
        var state = GetState(sessionId);
        lock (state.SyncRoot)
        {
            Append(
                state,
                "info",
                "bootstrap",
                string.IsNullOrWhiteSpace(launchCommand)
                    ? $"{PrettifyProfile(profile)} App Server Controller session started in MidTerm."
                    : $"{PrettifyProfile(profile)} worker started in MidTerm.",
                string.IsNullOrWhiteSpace(launchCommand)
                    ? BuildBootstrapDetail(slashCommands, guidanceInjected)
                    : $"Launch command `{launchCommand.Trim()}` queued. {BuildBootstrapDetail(slashCommands, guidanceInjected)}");
        }
    }

    public void NotePrompt(string sessionId, string profile, SessionPromptRequest request)
    {
        var state = GetState(sessionId);
        var excerpt = SummarizePrompt(request.Text);
        var mode = string.IsNullOrWhiteSpace(request.Mode) ? "auto" : request.Mode.Trim();
        lock (state.SyncRoot)
        {
            Append(
                state,
                request.InterruptFirst ? "attention" : "info",
                "prompt",
                string.IsNullOrWhiteSpace(excerpt)
                    ? $"{PrettifyProfile(profile)} prompt sent."
                    : $"{PrettifyProfile(profile)} prompt sent: {excerpt}",
                $"Mode `{mode}`. Submit keys: {DescribeKeys(request.SubmitKeys)}.");
        }
    }

    public void NoteKeyInput(string sessionId, SessionKeyInputRequest request)
    {
        if (request.Keys.Count == 0)
        {
            return;
        }

        var interestingKeys = request.Keys
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Take(3)
            .ToArray();
        if (interestingKeys.Length == 0)
        {
            return;
        }

        var tone = interestingKeys.Any(static key => key.Equals("C-c", StringComparison.OrdinalIgnoreCase))
            ? "attention"
            : "info";
        var summary = interestingKeys.Length == 1
            ? $"Special key sent: {interestingKeys[0]}."
            : $"Special keys sent: {string.Join(", ", interestingKeys)}.";

        var state = GetState(sessionId);
        lock (state.SyncRoot)
        {
            Append(state, tone, "keys", summary, request.Literal ? "Keys were sent literally." : null);
        }
    }

    public void NoteForeground(string sessionId, ForegroundChangePayload payload)
    {
        var state = GetState(sessionId);
        var signature = $"{payload.Name}|{payload.CommandLine}|{payload.Cwd}";
        lock (state.SyncRoot)
        {
            if (string.Equals(state.LastForegroundSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            state.LastForegroundSignature = signature;
            var app = string.IsNullOrWhiteSpace(payload.Name) ? "unknown process" : payload.Name.Trim();
            var detail = string.IsNullOrWhiteSpace(payload.CommandLine)
                ? null
                : payload.CommandLine.Trim();
            Append(state, "terminal", "foreground", $"Foreground changed to {app}.", detail);
        }
    }

    public IReadOnlyList<AgentSessionVibeActivity> RefreshAndGet(
        string sessionId,
        string profile,
        AgentSessionVibeLane lane,
        SessionSupervisorInfoDto supervisor,
        SessionActivityResponse activity,
        string tailText)
    {
        var state = GetState(sessionId);
        lock (state.SyncRoot)
        {
            if (!string.Equals(state.LastProfile, profile, StringComparison.Ordinal))
            {
                state.LastProfile = profile;
                Append(
                    state,
                    "info",
                    "provider",
                    $"{PrettifyProfile(profile)} lane attached to the Agent view.",
                    lane.Detail);
            }

            if (!string.Equals(state.LastState, supervisor.State, StringComparison.Ordinal))
            {
                state.LastState = supervisor.State;
                Append(
                    state,
                    ToneForState(supervisor.State),
                    "state",
                    $"{PrettifyProfile(profile)} is now {PrettifySupervisorState(supervisor.State).ToLowerInvariant()}.",
                    BuildStateDetail(supervisor.State));
            }

            if (state.LastNeedsAttention != supervisor.NeedsAttention ||
                !string.Equals(state.LastAttentionReason, supervisor.AttentionReason, StringComparison.Ordinal))
            {
                state.LastNeedsAttention = supervisor.NeedsAttention;
                state.LastAttentionReason = supervisor.AttentionReason;
                if (supervisor.NeedsAttention)
                {
                    Append(
                        state,
                        "attention",
                        "attention",
                        "This session likely needs human attention.",
                        PrettifyAttentionReason(supervisor.AttentionReason));
                }
                else
                {
                    Append(
                        state,
                        "positive",
                        "attention",
                        "Attention state cleared.",
                        "MidTerm no longer thinks this session is blocked or missing its agent runtime.");
                }
            }

            if (activity.TotalBellCount > state.LastBellCount)
            {
                var delta = activity.TotalBellCount - state.LastBellCount;
                state.LastBellCount = activity.TotalBellCount;
                Append(
                    state,
                    delta > 1 ? "attention" : "warning",
                    "bells",
                    delta == 1
                        ? "1 terminal bell was captured."
                        : $"{delta.ToString(CultureInfo.InvariantCulture)} new terminal bells were captured.",
                    activity.LastBellAt is null
                        ? null
                        : string.Create(
                            CultureInfo.InvariantCulture,
                            $"Most recent bell at {activity.LastBellAt.Value.ToLocalTime():HH:mm:ss}."));
            }

            var hasTailPreview = !string.IsNullOrWhiteSpace(tailText);
            if (hasTailPreview && !state.LastHadTailPreview)
            {
                state.LastHadTailPreview = true;
                Append(
                    state,
                    "terminal",
                    "preview",
                    "Recent terminal output is available as a fallback preview.",
                    "The terminal tab remains the ground truth and can be opened any time.");
            }
            else if (!hasTailPreview)
            {
                state.LastHadTailPreview = false;
            }

            return state.Activities
                .OrderByDescending(static item => item.CreatedAt)
                .ThenBy(static item => item.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public AgentSessionFeedResponse GetFeed(string sessionId, string source, IReadOnlyList<AgentSessionVibeActivity> activities, DateTimeOffset generatedAt)
    {
        return new AgentSessionFeedResponse
        {
            SessionId = sessionId,
            Source = string.IsNullOrWhiteSpace(source) ? "unknown" : source,
            GeneratedAt = generatedAt,
            Activities = activities.ToList()
        };
    }

    public void Forget(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
    }

    private SessionFeedState GetState(string sessionId)
    {
        return _sessions.GetOrAdd(sessionId, static _ => new SessionFeedState());
    }

    private static string BuildBootstrapDetail(IReadOnlyList<string> slashCommands, bool guidanceInjected)
    {
        var detail = guidanceInjected
            ? "MidTerm guidance was injected."
            : "No MidTerm guidance was injected.";

        if (slashCommands.Count == 0)
        {
            return detail;
        }

        return $"{detail} Slash commands queued: {string.Join(", ", slashCommands.Take(3))}.";
    }

    private static string BuildStateDetail(string state)
    {
        return state switch
        {
            SessionSupervisorService.BusyTurnState =>
                "Fresh output and heat suggest the agent is actively working.",
            SessionSupervisorService.IdlePromptState =>
                "The session appears ready for the next instruction.",
            SessionSupervisorService.BlockedState =>
                "Recent input or bell activity suggests the agent may be waiting for approval, confirmation, or operator help.",
            SessionSupervisorService.ShellState =>
                "The terminal is alive, but MidTerm does not currently detect an interactive AI runtime in the foreground.",
            SessionSupervisorService.DeadState =>
                "The PTY host reported that this session is no longer running.",
            _ =>
                "MidTerm does not yet have enough signal to classify this session confidently."
        };
    }

    private static string ToneForState(string state)
    {
        return state switch
        {
            SessionSupervisorService.BusyTurnState => "positive",
            SessionSupervisorService.IdlePromptState => "info",
            SessionSupervisorService.BlockedState => "attention",
            SessionSupervisorService.ShellState => "warning",
            SessionSupervisorService.DeadState => "attention",
            _ => "info"
        };
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
            "session-exited" => "Session exited.",
            "prompt-not-acknowledged" => "Prompt was sent but the agent did not acknowledge it.",
            "ai-cli-not-running" => "MidTerm expected an AI CLI here, but the foreground app looks like a plain shell.",
            null or "" => "MidTerm flagged the session for human attention.",
            _ => attentionReason
        };
    }

    private static string DescribeKeys(IReadOnlyList<string> keys)
    {
        return keys.Count == 0 ? "none" : string.Join(", ", keys);
    }

    private static string SummarizePrompt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var flattened = string.Join(" ", text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (flattened.Length <= 140)
        {
            return flattened;
        }

        return flattened[..137] + "...";
    }

    private static void Append(
        SessionFeedState state,
        string tone,
        string kind,
        string summary,
        string? detail)
    {
        state.NextSequence++;
        state.Activities.Add(new AgentSessionVibeActivity
        {
            Id = $"a{state.NextSequence.ToString(CultureInfo.InvariantCulture)}",
            Tone = tone,
            Kind = kind,
            Summary = summary,
            Detail = string.IsNullOrWhiteSpace(detail) ? null : detail,
            CreatedAt = DateTimeOffset.UtcNow
        });

        while (state.Activities.Count > MaxActivityCount)
        {
            state.Activities.RemoveAt(0);
        }
    }
}
