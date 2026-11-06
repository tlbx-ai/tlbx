using System.Text;
using System.Globalization;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class AcpAppServerControlAgentRuntime
{
    private readonly object _replayGate = new();
    private List<(JsonElement Root, string Raw)>? _loadedUpdates;
    private List<AppServerControlProviderEvent>? _replayEvents;
    private bool _applyingHistoryReplay;
    private readonly List<string> _loadedRequests = [];

    private string ResolveSessionOpenMethod(JsonElement initializeResult, string? resumeThreadId)
    {
        if (_protocolGeneration == 2)
        {
            if (Traverse(initializeResult, "capabilities", "session") is { ValueKind: JsonValueKind.Object })
                return string.IsNullOrWhiteSpace(resumeThreadId) ? "session/new" : "session/resume";
            throw new InvalidOperationException($"{_agentName} does not advertise the ACP session surface.");
        }

        if (string.IsNullOrWhiteSpace(resumeThreadId)) return "session/new";

        if (Traverse(initializeResult, "agentCapabilities", "loadSession") is { ValueKind: JsonValueKind.True })
        {
            return "session/load";
        }

        if (Traverse(initializeResult, "agentCapabilities", "sessionCapabilities", "resume") is
            { ValueKind: JsonValueKind.Object })
        {
            return "session/resume";
        }

        throw new InvalidOperationException($"{_agentName} does not advertise ACP session/load or session/resume support; no new conversation was created.");
    }

    private async Task<JsonElement> OpenSessionAsync(string method, string? resumeThreadId, CancellationToken ct)
    {
        lock (_replayGate)
        {
            _loadedUpdates = [];
            _loadedRequests.Clear();
        }

        try
        {
            return await SendRequestAsync(method, writer =>
            {
                writer.WriteStartObject();
                if (method != "session/new")
                {
                    writer.WriteString("sessionId", resumeThreadId);
                    if (_protocolGeneration == 2)
                    {
                        writer.WritePropertyName("replayFrom");
                        writer.WriteStartObject();
                        writer.WriteString("type", "start");
                        writer.WriteEndObject();
                    }
                }
                writer.WriteString("cwd", _workingDirectory);
                AppServerControlMcpConfiguration.WriteAcp(writer, _mcpServer, _protocolGeneration);
                writer.WriteEndObject();
            }, AttachRequestTimeout, ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_replayGate)
            {
                _loadedUpdates = null;
            }
            await DisposeProcessAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void AppendLoadedHistory(List<AppServerControlProviderEvent> events)
    {
        lock (_replayGate)
        {
            var updates = _loadedUpdates ?? [];
            _loadedUpdates = null;
            _replayEvents = events;
            _applyingHistoryReplay = true;
            string? replayTurnId = null;
            var userText = new StringBuilder();
            var userChunks = false;
            var turnIndex = 0;
            try
            {
                foreach (var (root, raw) in updates)
                {
                    var update = Traverse(root, "params", "update");
                    var type = update is { } value ? GetString(value, "sessionUpdate") : null;
                    if (type is "user_message" or "agent_message" or "agent_thought" or
                        "user_message_chunk" or "agent_message_chunk" or "agent_thought_chunk" or
                        "tool_call" or "tool_call_update" or "plan")
                    {
                        if (replayTurnId is null || (type is "user_message_chunk" or "user_message" && !userChunks))
                        {
                            if (replayTurnId is not null)
                            {
                                AppendReplayTurnCompleted(replayTurnId);
                            }
                            replayTurnId = $"acp-replay:{_providerThreadId}:{(++turnIndex).ToString(CultureInfo.InvariantCulture)}";
                            userText.Clear();
                            _tools.Clear();
                            events.Add(CreateEvent("turn.started", replayTurnId, null, null, "acp", "session/load", root, e =>
                            {
                                e.TurnStarted = new AppServerControlProviderTurnStartedPayload();
                            }, raw));
                        }

                        userChunks = type is "user_message_chunk" or "user_message";
                        if (userChunks && _protocolGeneration == 1)
                        {
                            userText.Append(ExtractContentText(update!.Value));
                            events.Add(CreateEvent("item.completed", replayTurnId, $"{replayTurnId}:user", null,
                                "acp", type, root, e => e.Item = new AppServerControlProviderItemPayload
                                {
                                    ItemType = "user_message",
                                    Status = "completed",
                                    Title = "User message",
                                    Detail = userText.ToString()
                                }, raw));
                            continue;
                        }
                    }

                    ApplySessionUpdate(root, raw, replayTurnId);
                }

                var resumedState = _loadedStateSnapshot is { } snapshot ? GetString(snapshot, "state") : null;
                if (replayTurnId is not null && resumedState is not ("running" or "requires_action"))
                {
                    AppendReplayTurnCompleted(replayTurnId);
                }
                if (_loadedStateSnapshot is { } stateSnapshot)
                {
                    if (resumedState is "running" or "requires_action" && replayTurnId is not null)
                    {
                        _activeTurnId = replayTurnId;
                        _activeTurnCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    HandleV2State(stateSnapshot, stateSnapshot, stateSnapshot.GetRawText());
                    _loadedStateSnapshot = null;
                }
            }
            finally
            {
                _applyingHistoryReplay = false;
                if (_protocolGeneration == 1) _tools.Clear();
            }
        }
    }

    private async Task AppendLoadedRequestsAsync(List<AppServerControlProviderEvent> events, CancellationToken ct)
    {
        string[] requests;
        lock (_replayGate)
        {
            requests = _loadedRequests.ToArray();
            _loadedRequests.Clear();
            _replayEvents = events;
        }
        foreach (var request in requests) await HandleAcpLineAsync(request, ct).ConfigureAwait(false);
    }

    private void AppendReplayTurnCompleted(string turnId)
    {
        _replayEvents!.Add(CreateEvent("turn.completed", turnId, null, null, "acp", "session/load", null, e =>
        {
            e.TurnCompleted = new AppServerControlProviderTurnCompletedPayload
            {
                State = "completed",
                StateLabel = "Completed",
                StopReason = "history_replay"
            };
        }));
    }
}
