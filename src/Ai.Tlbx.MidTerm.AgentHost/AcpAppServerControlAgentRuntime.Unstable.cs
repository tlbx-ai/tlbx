using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class AcpAppServerControlAgentRuntime
{
    private bool _supportsSessionFork;
    private bool _supportsSessionArchive;
    private bool _supportsSessionStatus;
    private bool _supportsSessionOptions;
    private bool _supportsSessionTitle;
    private bool _supportsAuthStatus;
    private bool _supportsProviders;
    private bool _supportsMcpAcp;
    private readonly List<string> _promptSuggestions = [];
    private string? _latestCursorId;
    private JsonElement? _latestAuthStatus;

    private void ResetUnstableState()
    {
        _supportsSessionFork = false;
        _supportsSessionArchive = false;
        _supportsSessionStatus = false;
        _supportsSessionOptions = false;
        _supportsSessionTitle = false;
        _supportsAuthStatus = false;
        _supportsProviders = false;
        _supportsMcpAcp = false;
        _promptSuggestions.Clear();
        _latestCursorId = null;
        _latestAuthStatus = null;
    }

    private void CaptureUnstableCapabilities(JsonElement initializeResult)
    {
        if (_protocolGeneration == 2)
        {
            var sessionCaps = Traverse(initializeResult, "capabilities", "session");
            _supportsSessionFork = Traverse(sessionCaps, "fork") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
            _supportsSessionArchive = Traverse(sessionCaps, "archive") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
            _supportsSessionStatus = Traverse(sessionCaps, "status") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
            _supportsSessionOptions = Traverse(sessionCaps, "options") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
            _supportsSessionTitle = Traverse(sessionCaps, "title") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
            _supportsAuthStatus = Traverse(initializeResult, "capabilities", "auth", "status") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
            _supportsProviders = Traverse(initializeResult, "capabilities", "providers") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
            _supportsMcpAcp = Traverse(sessionCaps, "mcp", "acp") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
        }
        else
        {
            var sessionCaps = Traverse(initializeResult, "agentCapabilities", "sessionCapabilities");
            _supportsSessionFork = Traverse(sessionCaps, "fork") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
            _supportsSessionArchive = Traverse(sessionCaps, "archive") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
            _supportsSessionStatus = Traverse(sessionCaps, "status") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
            _supportsSessionOptions = Traverse(sessionCaps, "options") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
            _supportsSessionTitle = Traverse(sessionCaps, "title") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
            _supportsAuthStatus = Traverse(initializeResult, "agentCapabilities", "auth", "status") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
            _supportsProviders = Traverse(initializeResult, "agentCapabilities", "providers") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
            _supportsMcpAcp = Traverse(initializeResult, "agentCapabilities", "mcpCapabilities", "acp") is { ValueKind: JsonValueKind.Object or JsonValueKind.True };
        }
    }

    private void HandlePromptSuggestionsUpdate(JsonElement update, JsonElement root, string rawLine)
    {
        _promptSuggestions.Clear();
        if (Traverse(update, "suggestions") is { ValueKind: JsonValueKind.Array } array)
        {
            using var enumerator = array.EnumerateArray();
            while (enumerator.MoveNext())
            {
                var item = enumerator.Current;
                var text = item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : GetString(item, "label") ?? GetString(item, "text") ?? GetString(item, "prompt");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _promptSuggestions.Add(text.Trim());
                }
            }
        }

        EmitRuntimeMessage(
            "agent.state",
            _promptSuggestions.Count == 0
                ? $"{_agentName} cleared prompt suggestions."
                : $"{_agentName} prompt suggestions: {FormatNamePreview(_promptSuggestions)}.",
            rawLine,
            "acp",
            "prompt_suggestions_update",
            root,
            rawLine,
            timelineVisible: false);
    }

    private void HandleCursorUpdate(JsonElement update, JsonElement root, string rawLine)
    {
        var cursorId = GetString(update, "cursorId");
        if (string.IsNullOrWhiteSpace(cursorId))
        {
            return;
        }

        _latestCursorId = cursorId;
        EmitRuntimeMessage(
            "runtime.notice",
            $"{_agentName} updated replay/fork cursor to '{cursorId}'.",
            rawLine,
            "acp",
            "cursor_update",
            root,
            rawLine,
            timelineVisible: false);
    }

    private void HandleAuthStatusUpdate(JsonElement root, string rawLine)
    {
        var parameters = Traverse(root, "params");
        if (parameters is not { ValueKind: JsonValueKind.Object } payload)
        {
            return;
        }

        _latestAuthStatus = payload.Clone();
        var state = GetString(payload, "state") ?? "unknown";
        EmitRuntimeMessage(
            "agent.state",
            $"{_agentName} authentication status: {state}.",
            rawLine,
            "acp",
            "auth/status_update",
            root,
            rawLine,
            timelineVisible: false);
    }

    private async Task HandleInboundMcpMessageAsync(JsonElement root, string rawLine, CancellationToken ct)
    {
        var parameters = Traverse(root, "params");
        var serverId = GetString(parameters, "serverId") ?? "unknown";
        var mcpMethod = GetString(parameters, "method") ?? "unknown";
        var sessionId = GetString(parameters, "sessionId");
        var toolCallId = GetString(parameters, "toolCallId");

        EmitRuntimeMessage(
            "runtime.notice",
            $"{_agentName} MCP-over-ACP '{mcpMethod}' for server '{serverId}'" +
            (string.IsNullOrWhiteSpace(toolCallId) ? string.Empty : $" (tool {toolCallId})") +
            (string.IsNullOrWhiteSpace(sessionId) ? string.Empty : $" [session {sessionId}]") + ".",
            rawLine,
            "acp",
            "mcp/message",
            root,
            rawLine,
            timelineVisible: false);

        if (root.TryGetProperty("id", out var rpcId))
        {
            // Acknowledge cleanly with JSON-RPC MCP-level unsupported method unless a local MCP bridge handles it.
            await WriteJsonLineAsync(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WritePropertyName("id");
                rpcId.WriteTo(writer);
                writer.WriteStartObject("result");
                writer.WriteStartObject("error");
                writer.WriteNumber("code", -32601);
                writer.WriteString("message", $"No client-side ACP MCP server is registered for serverId '{serverId}'.");
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }, ct).ConfigureAwait(false);
        }
    }

    private async Task<HostCommandOutcome> ForkSessionAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        if (!_supportsSessionFork)
        {
            throw new InvalidOperationException($"{_agentName} does not advertise ACP session/fork.");
        }

        var req = command.ForkSession ?? new AppServerControlSessionForkRequest();
        var sourceSessionId = string.IsNullOrWhiteSpace(req.ProviderSessionId) ? _providerThreadId! : req.ProviderSessionId.Trim();
        var cwd = string.IsNullOrWhiteSpace(req.Cwd) ? _workingDirectory! : req.Cwd.Trim();
        var cursorId = string.IsNullOrWhiteSpace(req.CursorId) ? _latestCursorId : req.CursorId.Trim();

        var result = await SendRequestAsync("session/fork", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", sourceSessionId);
            if (!string.IsNullOrWhiteSpace(cursorId)) writer.WriteString("cursorId", cursorId);
            writer.WriteString("cwd", cwd);
            if (req.AdditionalDirectories.Count > 0)
            {
                writer.WriteStartArray("additionalDirectories");
                foreach (var dir in req.AdditionalDirectories) writer.WriteStringValue(dir);
                writer.WriteEndArray();
            }
            if (!string.IsNullOrWhiteSpace(req.SystemPromptText))
            {
                writer.WriteStartObject("systemPrompt");
                writer.WriteString("mode", string.IsNullOrWhiteSpace(req.SystemPromptMode) ? "append" : req.SystemPromptMode.Trim().ToLowerInvariant());
                writer.WriteStartArray("content");
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", req.SystemPromptText);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            if (req.ConfigOptions.Count > 0)
            {
                writer.WriteStartObject("configOptions");
                foreach (var (key, val) in req.ConfigOptions)
                {
                    writer.WritePropertyName(key);
                    val.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            AppServerControlMcpConfiguration.WriteAcp(writer, _mcpServer, _protocolGeneration, _additionalMcpHttpServers);
            writer.WriteEndObject();
        }, AttachRequestTimeout, ct).ConfigureAwait(false);

        var forkedId = GetString(result, "sessionId")
            ?? throw new InvalidOperationException($"{_agentName} session/fork response omitted sessionId.");
        var configOptions = new List<JsonElement>();
        if (Traverse(result, "configOptions") is { ValueKind: JsonValueKind.Array } cfgArray)
        {
            using var cfgEnum = cfgArray.EnumerateArray();
            while (cfgEnum.MoveNext()) configOptions.Add(cfgEnum.Current.Clone());
        }

        return new HostCommandOutcome
        {
            Result = new AppServerControlHostCommandResultEnvelope
            {
                CommandId = command.CommandId,
                SessionId = command.SessionId,
                Status = "accepted",
                ForkedSession = new AppServerControlSessionForkResponse
                {
                    SessionId = forkedId,
                    ConfigOptions = configOptions,
                    AvailableCommands = ReadAvailableCommands(result)
                }
            }
        };
    }

    private async Task<HostCommandOutcome> SetSessionTitleAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        if (!_supportsSessionTitle)
        {
            throw new InvalidOperationException($"{_agentName} does not advertise ACP session/set_title.");
        }

        var req = command.SessionTitle ?? throw new InvalidOperationException("Session title payload is required.");
        var targetId = string.IsNullOrWhiteSpace(req.ProviderSessionId) ? _providerThreadId! : req.ProviderSessionId.Trim();
        await SendRequestAsync("session/set_title", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", targetId);
            writer.WriteString("title", req.Title);
            writer.WriteEndObject();
        }, AttachRequestTimeout, ct).ConfigureAwait(false);

        return Accepted(command.CommandId, command.SessionId);
    }

    private async Task<HostCommandOutcome> ArchiveSessionAsync(AppServerControlHostCommandEnvelope command, bool unarchive, CancellationToken ct)
    {
        EnsureAttached();
        if (!_supportsSessionArchive)
        {
            throw new InvalidOperationException($"{_agentName} does not advertise ACP session.archive.");
        }

        var targetId = string.IsNullOrWhiteSpace(command.SessionTarget?.ProviderSessionId)
            ? _providerThreadId!
            : command.SessionTarget!.ProviderSessionId.Trim();
        var method = unarchive ? "session/unarchive" : "session/archive";
        await SendRequestAsync(method, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", targetId);
            writer.WriteEndObject();
        }, AttachRequestTimeout, ct).ConfigureAwait(false);

        return Accepted(command.CommandId, command.SessionId);
    }

    private async Task<HostCommandOutcome> GetSessionStatusAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        if (!_supportsSessionStatus)
        {
            throw new InvalidOperationException($"{_agentName} does not advertise ACP session/status.");
        }

        var targetId = string.IsNullOrWhiteSpace(command.SessionTarget?.ProviderSessionId)
            ? _providerThreadId!
            : command.SessionTarget!.ProviderSessionId.Trim();
        var result = await SendRequestAsync("session/status", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", targetId);
            writer.WriteEndObject();
        }, AttachRequestTimeout, ct).ConfigureAwait(false);

        return AcceptedRaw(command, result);
    }

    private async Task<HostCommandOutcome> GetSessionOptionsAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        if (!_supportsSessionOptions)
        {
            throw new InvalidOperationException($"{_agentName} does not advertise ACP session/options.");
        }

        var cwd = string.IsNullOrWhiteSpace(command.ListSessions?.Cwd) ? _workingDirectory! : command.ListSessions!.Cwd!.Trim();
        var result = await SendRequestAsync("session/options", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("cwd", cwd);
            if (!string.IsNullOrWhiteSpace(_providerThreadId)) writer.WriteString("sessionId", _providerThreadId);
            writer.WriteEndObject();
        }, AttachRequestTimeout, ct).ConfigureAwait(false);

        return AcceptedRaw(command, result);
    }

    private async Task<HostCommandOutcome> GetAuthStatusAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        if (!_supportsAuthStatus)
        {
            throw new InvalidOperationException($"{_agentName} does not advertise ACP auth/status.");
        }

        var result = await SendRequestAsync("auth/status", writer =>
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }, AttachRequestTimeout, ct).ConfigureAwait(false);
        _latestAuthStatus = result.Clone();
        return AcceptedRaw(command, result);
    }

    private async Task<HostCommandOutcome> ListProvidersAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        if (!_supportsProviders)
        {
            throw new InvalidOperationException($"{_agentName} does not advertise ACP providers capabilities.");
        }

        var result = await SendRequestAsync("providers/list", writer =>
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }, AttachRequestTimeout, ct).ConfigureAwait(false);
        return AcceptedRaw(command, result);
    }

    private async Task<HostCommandOutcome> SetProviderAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        if (!_supportsProviders)
        {
            throw new InvalidOperationException($"{_agentName} does not advertise ACP providers capabilities.");
        }

        var cfg = command.ProviderConfig ?? throw new InvalidOperationException("ProviderConfig payload is required.");
        if (string.IsNullOrWhiteSpace(cfg.ProviderId) || string.IsNullOrWhiteSpace(cfg.ApiType) || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            throw new InvalidOperationException("providerId, apiType, and baseUrl are required for providers/set.");
        }

        var result = await SendRequestAsync("providers/set", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("providerId", cfg.ProviderId.Trim());
            writer.WriteString("apiType", cfg.ApiType.Trim());
            writer.WriteString("baseUrl", cfg.BaseUrl.Trim());
            if (cfg.Headers.Count > 0)
            {
                writer.WriteStartObject("headers");
                foreach (var (k, v) in cfg.Headers) writer.WriteString(k, v);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }, AttachRequestTimeout, ct).ConfigureAwait(false);
        return AcceptedRaw(command, result);
    }

    private async Task<HostCommandOutcome> DisableProviderAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        if (!_supportsProviders)
        {
            throw new InvalidOperationException($"{_agentName} does not advertise ACP providers capabilities.");
        }

        var providerId = command.ProviderConfig?.ProviderId?.Trim();
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new InvalidOperationException("providerId is required for providers/disable.");
        }

        var result = await SendRequestAsync("providers/disable", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("providerId", providerId);
            writer.WriteEndObject();
        }, AttachRequestTimeout, ct).ConfigureAwait(false);
        return AcceptedRaw(command, result);
    }

    private async Task<HostCommandOutcome> SendMcpMessageAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        if (!_supportsMcpAcp)
        {
            throw new InvalidOperationException($"{_agentName} does not advertise ACP mcp/message support.");
        }

        var msg = command.McpMessage ?? throw new InvalidOperationException("McpMessage payload is required.");
        if (string.IsNullOrWhiteSpace(msg.ServerId) || string.IsNullOrWhiteSpace(msg.RequestId) || string.IsNullOrWhiteSpace(msg.Method))
        {
            throw new InvalidOperationException("serverId, requestId, and method are required for mcp/message.");
        }

        var result = await SendRequestAsync("mcp/message", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("serverId", msg.ServerId.Trim());
            writer.WriteString("requestId", msg.RequestId.Trim());
            writer.WriteString("method", msg.Method.Trim());
            if (msg.Params is { ValueKind: not JsonValueKind.Undefined } p)
            {
                writer.WritePropertyName("params");
                p.WriteTo(writer);
            }
            if (!string.IsNullOrWhiteSpace(msg.ProviderSessionId)) writer.WriteString("sessionId", msg.ProviderSessionId.Trim());
            if (!string.IsNullOrWhiteSpace(msg.ToolCallId)) writer.WriteString("toolCallId", msg.ToolCallId.Trim());
            writer.WriteEndObject();
        }, AttachRequestTimeout, ct).ConfigureAwait(false);
        return AcceptedRaw(command, result);
    }

    private static HostCommandOutcome AcceptedRaw(AppServerControlHostCommandEnvelope command, JsonElement payload) => new()
    {
        Result = new AppServerControlHostCommandResultEnvelope
        {
            CommandId = command.CommandId,
            SessionId = command.SessionId,
            Status = "accepted",
            RawPayload = payload.ValueKind == JsonValueKind.Undefined ? JsonElement.Parse("{}") : payload.Clone()
        }
    };
}
