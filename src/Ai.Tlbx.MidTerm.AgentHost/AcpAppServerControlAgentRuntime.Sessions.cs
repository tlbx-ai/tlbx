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
    private List<string> _additionalDirectories = [];
    private string? _systemPromptMode;
    private string? _systemPromptText;
    private List<AppServerControlHttpMcpServer> _additionalMcpHttpServers = [];
    private bool _supportsSessionList;
    private bool _supportsSessionClose;
    private bool _supportsSessionDelete;
    private bool _supportsAdditionalDirectories;
    private bool _supportsSystemPromptAppend;
    private bool _supportsSystemPromptOverride;
    private bool _supportsMcpHttp;

    private void CaptureSessionCapabilities(JsonElement initializeResult)
    {
        if (_protocolGeneration == 2)
        {
            var sessionCaps = Traverse(initializeResult, "capabilities", "session");
            var hasSession = sessionCaps is { ValueKind: JsonValueKind.Object };
            _supportsSessionList = hasSession;
            _supportsSessionClose = hasSession;
            _supportsSessionDelete = Traverse(sessionCaps, "delete") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
            _supportsAdditionalDirectories = Traverse(sessionCaps, "additionalDirectories") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
            var sysPrompt = Traverse(sessionCaps, "systemPrompt");
            _supportsSystemPromptAppend = Traverse(sysPrompt, "append") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
            _supportsSystemPromptOverride = Traverse(sysPrompt, "override") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
            _supportsMcpHttp = Traverse(sessionCaps, "mcp", "http") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
        }
        else
        {
            var sessionCaps = Traverse(initializeResult, "agentCapabilities", "sessionCapabilities");
            _supportsSessionList = Traverse(sessionCaps, "list") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
            _supportsSessionClose = Traverse(sessionCaps, "close") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
            _supportsSessionDelete = Traverse(sessionCaps, "delete") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
            _supportsAdditionalDirectories = false;
            _supportsSystemPromptAppend = false;
            _supportsSystemPromptOverride = false;
            _supportsMcpHttp = Traverse(initializeResult, "agentCapabilities", "mcpCapabilities", "http") is { ValueKind: JsonValueKind.True };
        }
    }

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
            if (_additionalDirectories.Count > 0 && !_supportsAdditionalDirectories)
            {
                throw new InvalidOperationException($"{_agentName} does not advertise ACP session.additionalDirectories.");
            }

            if (!string.IsNullOrWhiteSpace(_systemPromptText))
            {
                var mode = string.IsNullOrWhiteSpace(_systemPromptMode) ? "append" : _systemPromptMode.Trim().ToLowerInvariant();
                if (mode == "append" && !_supportsSystemPromptAppend)
                    throw new InvalidOperationException($"{_agentName} does not advertise ACP session.systemPrompt.append.");
                if (mode == "override" && !_supportsSystemPromptOverride)
                    throw new InvalidOperationException($"{_agentName} does not advertise ACP session.systemPrompt.override.");
                if (mode is not ("append" or "override"))
                    throw new InvalidOperationException($"Unsupported ACP systemPrompt mode '{mode}'.");
            }

            if (_additionalMcpHttpServers.Count > 0 && !_supportsMcpHttp)
            {
                throw new InvalidOperationException($"{_agentName} does not advertise ACP HTTP MCP server support.");
            }

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
                if (_additionalDirectories.Count > 0)
                {
                    writer.WriteStartArray("additionalDirectories");
                    foreach (var dir in _additionalDirectories) writer.WriteStringValue(dir);
                    writer.WriteEndArray();
                }
                if (!string.IsNullOrWhiteSpace(_systemPromptText))
                {
                    var mode = string.IsNullOrWhiteSpace(_systemPromptMode) ? "append" : _systemPromptMode.Trim().ToLowerInvariant();
                    writer.WriteStartObject("systemPrompt");
                    writer.WriteString("mode", mode);
                    writer.WriteStartArray("content");
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", _systemPromptText);
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                AppServerControlMcpConfiguration.WriteAcp(writer, _mcpServer, _protocolGeneration, _additionalMcpHttpServers);
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

    private void HandleSessionInfoUpdate(JsonElement update, JsonElement root, string rawLine)
    {
        var title = GetString(update, "title");
        var updatedAt = GetString(update, "updatedAt");
        var summary = string.IsNullOrWhiteSpace(title)
            ? $"{_agentName} session metadata updated."
            : $"{_agentName} session title updated: {title}";
        var detail = string.IsNullOrWhiteSpace(updatedAt) ? title ?? rawLine : $"{title} ({updatedAt})";
        EmitRuntimeMessage("agent.state", summary, detail, "acp", "session_info_update", root, rawLine, timelineVisible: false);
    }

    private async Task<HostCommandOutcome> ListSessionsAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        if (!_supportsSessionList)
        {
            throw new InvalidOperationException($"{_agentName} does not advertise ACP session/list.");
        }

        var req = command.ListSessions ?? new AppServerControlSessionListRequest();
        var result = await SendRequestAsync("session/list", writer =>
        {
            writer.WriteStartObject();
            if (!string.IsNullOrWhiteSpace(req.Cwd)) writer.WriteString("cwd", req.Cwd.Trim());
            if (req.Cursor is not null) writer.WriteString("cursor", req.Cursor);
            if (!string.IsNullOrWhiteSpace(req.Archived)) writer.WriteString("archived", req.Archived.Trim());
            writer.WriteEndObject();
        }, AttachRequestTimeout, ct).ConfigureAwait(false);

        var response = new AppServerControlSessionListResponse
        {
            NextCursor = GetString(result, "nextCursor")
        };
        if (Traverse(result, "sessions") is { ValueKind: JsonValueKind.Array } sessions)
        {
            using var enumerator = sessions.EnumerateArray();
            while (enumerator.MoveNext())
            {
                var item = enumerator.Current;
                var sessionId = GetString(item, "sessionId");
                if (string.IsNullOrWhiteSpace(sessionId)) continue;
                var entry = new AppServerControlSessionListEntry
                {
                    SessionId = sessionId,
                    Cwd = GetString(item, "cwd") ?? string.Empty,
                    Title = GetString(item, "title"),
                    UpdatedAt = GetString(item, "updatedAt"),
                    Archived = Traverse(item, "archived") is { ValueKind: JsonValueKind.True }
                };
                if (Traverse(item, "additionalDirectories") is { ValueKind: JsonValueKind.Array } dirs)
                {
                    using var dirEnum = dirs.EnumerateArray();
                    while (dirEnum.MoveNext())
                    {
                        if (dirEnum.Current.ValueKind == JsonValueKind.String && dirEnum.Current.GetString() is { Length: > 0 } dir)
                            entry.AdditionalDirectories.Add(dir);
                    }
                }
                response.Sessions.Add(entry);
            }
        }

        return new HostCommandOutcome
        {
            Result = new AppServerControlHostCommandResultEnvelope
            {
                CommandId = command.CommandId,
                SessionId = command.SessionId,
                Status = "accepted",
                SessionList = response
            }
        };
    }

    private async Task<HostCommandOutcome> DeleteSessionAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        if (!_supportsSessionDelete)
        {
            throw new InvalidOperationException($"{_agentName} does not advertise ACP session/delete.");
        }

        var targetSessionId = command.SessionTarget?.ProviderSessionId?.Trim();
        if (string.IsNullOrWhiteSpace(targetSessionId))
        {
            throw new InvalidOperationException("providerSessionId is required for session/delete.");
        }

        await SendRequestAsync("session/delete", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", targetSessionId);
            writer.WriteEndObject();
        }, AttachRequestTimeout, ct).ConfigureAwait(false);

        return Accepted(command.CommandId, command.SessionId);
    }

    private async Task<HostCommandOutcome> CloseSessionCommandAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        var targetSessionId = string.IsNullOrWhiteSpace(command.SessionTarget?.ProviderSessionId)
            ? _providerThreadId!
            : command.SessionTarget!.ProviderSessionId.Trim();
        await TrySendSessionCloseAsync(targetSessionId, ct).ConfigureAwait(false);
        return Accepted(command.CommandId, command.SessionId);
    }

    private async Task TrySendSessionCloseAsync(string? providerSessionId, CancellationToken ct)
    {
        if (!_supportsSessionClose || string.IsNullOrWhiteSpace(providerSessionId) || _process is not { HasExited: false } || _input is null)
        {
            return;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await SendRequestAsync("session/close", writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("sessionId", providerSessionId);
                writer.WriteEndObject();
            }, TimeSpan.FromSeconds(2), timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // ACP spec treats -32002 (session not found/inactive) as successful close; ignore teardown transport errors.
        }
    }
}
