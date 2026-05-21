using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed class GrokAcpAppServerControlAgentRuntime : IAppServerControlAgentRuntime
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan AttachRequestTimeout = TimeSpan.FromSeconds(20);

    private readonly Action<AppServerControlProviderEvent> _emit;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _pendingPromptTurns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingPermissionRequest> _pendingPermissions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GrokToolState> _tools = new(StringComparer.Ordinal);
    private Process? _process;
    private StreamReader? _output;
    private StreamReader? _error;
    private StreamWriter? _input;
    private Task? _readerTask;
    private Task? _errorTask;
    private string? _sessionId;
    private string? _workingDirectory;
    private string? _binaryPath;
    private string? _userProfileDirectory;
    private string? _providerThreadId;
    private string? _activeTurnId;
    private string? _activeTurnModel;
    private string? _activeTurnEffort;
    private AppServerControlQuickSettingsSummary _quickSettings = new();
    private int _nextRequestId;
    private long _sequence;
    private bool _disposed;

    public GrokAcpAppServerControlAgentRuntime(Action<AppServerControlProviderEvent> emit)
    {
        _emit = emit;
    }

    public string Provider => "grok";

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        await DisposeProcessAsync().ConfigureAwait(false);
        _shutdown.Dispose();
        _writeGate.Dispose();
        _gate.Dispose();
    }

    public async Task<HostCommandOutcome> ExecuteAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return command.Type switch
            {
                "runtime.attach" => await AttachAsync(command, ct).ConfigureAwait(false),
                "turn.start" => await StartTurnAsync(command, ct).ConfigureAwait(false),
                "turn.interrupt" => await InterruptTurnAsync(command, ct).ConfigureAwait(false),
                "request.resolve" => await ResolvePermissionRequestAsync(command, ct).ConfigureAwait(false),
                "user-input.resolve" => throw new InvalidOperationException("Grok ACP user-input resolution is not supported yet."),
                "thread.goal.set" => Accepted(command.CommandId, command.SessionId),
                _ => throw new InvalidOperationException($"Unsupported Grok command '{command.Type}'.")
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HostCommandOutcome> AttachAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        var attach = command.AttachRuntime ?? throw new InvalidOperationException("runtime.attach payload is required.");
        if (!string.Equals(attach.Provider, Provider, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Grok runtime cannot attach provider '{attach.Provider}'.");
        }

        if (string.IsNullOrWhiteSpace(attach.WorkingDirectory) || !Directory.Exists(attach.WorkingDirectory))
        {
            throw new InvalidOperationException("Grok working directory is required.");
        }

        var binaryPath = string.IsNullOrWhiteSpace(attach.ExecutablePath)
            ? FindExecutableInPath("grok", attach.UserProfileDirectory)
            : attach.ExecutablePath;
        if (string.IsNullOrWhiteSpace(binaryPath) || !File.Exists(binaryPath))
        {
            throw new InvalidOperationException("Grok CLI was not found. Install Grok Build and make sure grok is on PATH.");
        }

        await DisposeProcessAsync().ConfigureAwait(false);

        _sessionId = command.SessionId;
        _workingDirectory = attach.WorkingDirectory;
        _binaryPath = binaryPath;
        _userProfileDirectory = attach.UserProfileDirectory;
        _providerThreadId = null;
        _quickSettings = CreateDefaultQuickSettings();

        await StartGrokProcessAsync(ct).ConfigureAwait(false);

        var initializeResult = await SendRequestAsync(
                "initialize",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("protocolVersion", 1);
                    writer.WritePropertyName("clientCapabilities");
                    writer.WriteStartObject();
                    writer.WritePropertyName("fs");
                    writer.WriteStartObject();
                    writer.WriteBoolean("readTextFile", false);
                    writer.WriteBoolean("writeTextFile", false);
                    writer.WriteEndObject();
                    writer.WriteBoolean("terminal", false);
                    writer.WriteEndObject();
                    writer.WritePropertyName("clientInfo");
                    writer.WriteStartObject();
                    writer.WriteString("name", "midterm");
                    writer.WriteString("title", "MidTerm");
                    writer.WriteString("version", "dev");
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                },
                AttachRequestTimeout,
                ct)
            .ConfigureAwait(false);

        var newSessionResult = await SendRequestAsync(
                "session/new",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("cwd", _workingDirectory);
                    writer.WritePropertyName("mcpServers");
                    writer.WriteStartArray();
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                },
                AttachRequestTimeout,
                ct)
            .ConfigureAwait(false);

        _providerThreadId = GetString(newSessionResult, "sessionId")
                            ?? "grok-session-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        _quickSettings = CreateQuickSettingsFromState(initializeResult, newSessionResult);

        return Accepted(
            command.CommandId,
            command.SessionId,
            events:
            [
                CreateEvent("session.started", null, null, null, "mtagenthost.grok-acp", "runtime.attach", attach, appServerControlEvent =>
                {
                    appServerControlEvent.SessionState = new AppServerControlProviderSessionStatePayload
                    {
                        State = "starting",
                        StateLabel = "Starting",
                        Reason = "Grok ACP runtime attached."
                    };
                }),
                CreateEvent("thread.started", null, null, null, "grok.acp", "session/new", newSessionResult, appServerControlEvent =>
                {
                    appServerControlEvent.ThreadState = new AppServerControlProviderThreadStatePayload
                    {
                        State = "active",
                        StateLabel = "Active",
                        ProviderThreadId = _providerThreadId
                    };
                }),
                CreateEvent("session.ready", null, null, null, "grok.acp", "session/new", newSessionResult, appServerControlEvent =>
                {
                    appServerControlEvent.SessionState = new AppServerControlProviderSessionStatePayload
                    {
                        State = "ready",
                        StateLabel = "Ready",
                        Reason = "Grok ACP runtime is ready for the next turn."
                    };
                }),
                CreateQuickSettingsUpdatedEvent(_quickSettings, "grok.acp", "session/new", newSessionResult)
            ]);
    }

    private async Task<HostCommandOutcome> StartTurnAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        if (!string.IsNullOrWhiteSpace(_activeTurnId))
        {
            throw new InvalidOperationException("Grok already has an active ACP turn.");
        }

        var request = command.StartTurn ?? throw new InvalidOperationException("turn.start payload is required.");
        var quickSettings = ResolveRequestedQuickSettings(request);
        var promptBlocks = BuildPromptBlocks(request, quickSettings.PlanMode);
        if (promptBlocks.Count == 0)
        {
            throw new InvalidOperationException("App Server Controller turn input must include text or attachments.");
        }

        _activeTurnId = "turn-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        _quickSettings = MergeRequestedQuickSettings(quickSettings);
        _activeTurnModel = _quickSettings.Model;
        _activeTurnEffort = _quickSettings.Effort;
        _tools.Clear();

        var requestId = NextRequestId();
        _pendingPromptTurns[requestId] = _activeTurnId;
        foreach (var appServerControlEvent in CreateTurnStartEvents(request))
        {
            _emit(appServerControlEvent);
        }

        try
        {
            await WriteJsonRpcRequestAsync(
                    requestId,
                    "session/prompt",
                    writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteString("sessionId", _providerThreadId);
                        writer.WritePropertyName("prompt");
                        WritePromptBlocks(writer, promptBlocks);
                        writer.WriteEndObject();
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch
        {
            _pendingPromptTurns.TryRemove(requestId, out _);
            CompleteTurn(
                _activeTurnId,
                "failed",
                "Failed",
                "send_failed",
                "Failed to send Grok ACP prompt.",
                string.Empty);
            ResetTurnState();
            throw;
        }

        return new HostCommandOutcome
        {
            Result = new AppServerControlHostCommandResultEnvelope
            {
                CommandId = command.CommandId,
                SessionId = command.SessionId,
                Status = "accepted",
                Accepted = new AppServerControlCommandAcceptedResponse
                {
                    SessionId = command.SessionId,
                    Status = "accepted",
                    TurnId = _activeTurnId
                },
                TurnStarted = new AppServerControlTurnStartResponse
                {
                    SessionId = command.SessionId,
                    Provider = Provider,
                    ThreadId = _providerThreadId ?? _sessionId ?? command.SessionId,
                    TurnId = _activeTurnId,
                    Status = "accepted",
                    QuickSettings = new AppServerControlQuickSettingsSummary
                    {
                        Model = _quickSettings.Model,
                        Effort = _quickSettings.Effort,
                        PlanMode = _quickSettings.PlanMode,
                        PermissionMode = _quickSettings.PermissionMode,
                        ModelOptions = AppServerControlQuickSettings.CloneOptions(_quickSettings.ModelOptions),
                        EffortOptions = AppServerControlQuickSettings.CloneOptions(_quickSettings.EffortOptions)
                    }
                }
            },
            Events = []
        };
    }

    private AppServerControlProviderEvent[] CreateTurnStartEvents(AppServerControlTurnRequest request)
    {
        return
        [
            CreateQuickSettingsUpdatedEvent(_quickSettings, "midterm.appServerControl", "turn.start", request),
            CreateEvent("session.state.changed", _activeTurnId, null, null, "grok.acp", "session/prompt", request, appServerControlEvent =>
            {
                appServerControlEvent.SessionState = new AppServerControlProviderSessionStatePayload
                {
                    State = "running",
                    StateLabel = "Running",
                    Reason = "Grok ACP turn started."
                };
            }),
            CreateEvent("turn.started", _activeTurnId, null, null, "grok.acp", "session/prompt", request, appServerControlEvent =>
            {
                appServerControlEvent.TurnStarted = new AppServerControlProviderTurnStartedPayload
                {
                    Model = _activeTurnModel,
                    Effort = _activeTurnEffort
                };
            })
        ];
    }

    private async Task<HostCommandOutcome> InterruptTurnAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        var turnId = string.IsNullOrWhiteSpace(command.InterruptTurn?.TurnId)
            ? _activeTurnId
            : command.InterruptTurn!.TurnId;
        if (string.IsNullOrWhiteSpace(turnId))
        {
            throw new InvalidOperationException("Grok does not have an active turn to interrupt.");
        }

        await WriteJsonRpcNotificationAsync(
                "session/cancel",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("sessionId", _providerThreadId);
                    writer.WriteEndObject();
                },
                ct)
            .ConfigureAwait(false);

        foreach (var pair in _pendingPermissions.Where(pair => string.Equals(pair.Value.TurnId, turnId, StringComparison.Ordinal)).ToList())
        {
            if (_pendingPermissions.TryRemove(pair.Key, out var pending))
            {
                await WritePermissionResponseAsync(pending.RpcId, null, cancelled: true, ct).ConfigureAwait(false);
            }
        }

        foreach (var pair in _pendingPromptTurns.Where(pair => string.Equals(pair.Value, turnId, StringComparison.Ordinal)).ToList())
        {
            _pendingPromptTurns.TryRemove(pair.Key, out _);
        }

        ResetTurnState();

        return Accepted(
            command.CommandId,
            command.SessionId,
            accepted: new AppServerControlCommandAcceptedResponse
            {
                SessionId = command.SessionId,
                Status = "accepted",
                TurnId = turnId
            },
            events:
            [
                CreateEvent("turn.aborted", turnId, null, null, "grok.acp", "session/cancel", command.InterruptTurn, appServerControlEvent =>
                {
                    appServerControlEvent.TurnCompleted = new AppServerControlProviderTurnCompletedPayload
                    {
                        State = "interrupted",
                        StateLabel = "Interrupted",
                        StopReason = "interrupt"
                    };
                }),
                CreateEvent("session.state.changed", turnId, null, null, "grok.acp", "session/cancel", command.InterruptTurn, appServerControlEvent =>
                {
                    appServerControlEvent.SessionState = new AppServerControlProviderSessionStatePayload
                    {
                        State = "ready",
                        StateLabel = "Ready",
                        Reason = "Grok turn interrupted."
                    };
                })
            ]);
    }

    private async Task<HostCommandOutcome> ResolvePermissionRequestAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        var request = command.ResolveRequest ?? throw new InvalidOperationException("request.resolve payload is required.");
        if (!_pendingPermissions.TryRemove(request.RequestId, out var pending))
        {
            throw new InvalidOperationException($"Grok ACP permission request was not found: {request.RequestId}");
        }

        var selectedOption = SelectPermissionOption(pending.Options, request.Decision);
        if (string.IsNullOrWhiteSpace(selectedOption))
        {
            await WritePermissionResponseAsync(pending.RpcId, null, cancelled: true, ct).ConfigureAwait(false);
        }
        else
        {
            await WritePermissionResponseAsync(pending.RpcId, selectedOption, cancelled: false, ct).ConfigureAwait(false);
        }

        return Accepted(
            command.CommandId,
            command.SessionId,
            accepted: new AppServerControlCommandAcceptedResponse
            {
                SessionId = command.SessionId,
                Status = "accepted",
                RequestId = request.RequestId
            },
            events:
            [
                CreateEvent("request.resolved", pending.TurnId, null, request.RequestId, "grok.acp", "session/request_permission", request, appServerControlEvent =>
                {
                    appServerControlEvent.RequestResolved = new AppServerControlProviderRequestResolvedPayload
                    {
                        RequestType = "command_execution_approval",
                        Decision = request.Decision
                    };
                })
            ]);
    }

    private async Task StartGrokProcessAsync(CancellationToken ct)
    {
        var startInfo = CreateProcessStartInfo(
            _binaryPath!,
            BuildArguments(_quickSettings.PermissionMode, _quickSettings.Model),
            _workingDirectory!);

        Process? process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        try
        {
            AppServerControlProviderRuntimeConfiguration.ApplyUserProfileEnvironment(process.StartInfo, _userProfileDirectory);
            AppServerControlProviderRuntimeConfiguration.ApplyEnvironmentVariables(process.StartInfo, Provider);
            if (!process.Start())
            {
                throw new InvalidOperationException("Grok process could not be started.");
            }

            var startedProcess = process;
#pragma warning disable IDISP003
            process = null;
            _process = startedProcess;
#pragma warning restore IDISP003
            _output = startedProcess.StandardOutput;
            _error = startedProcess.StandardError;
            _input = startedProcess.StandardInput;
            _readerTask = Task.Run(() => ReadLoopAsync(startedProcess, CancellationToken.None), CancellationToken.None);
            _errorTask = Task.Run(() => ReadErrorLoopAsync(CancellationToken.None), CancellationToken.None);
        }
        finally
        {
            process?.Dispose();
        }

        await Task.CompletedTask.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(Process process, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _output is not null)
            {
                var line = await _output.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    await HandleGrokLineAsync(line, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            EmitRuntimeMessage("runtime.error", "Grok ACP stream failed.", ex.Message);
        }
        finally
        {
            await FinalizeExitAsync(process).ConfigureAwait(false);
        }
    }

    private async Task ReadErrorLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _error is not null)
            {
                var line = await _error.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    EmitRuntimeMessage("runtime.warning", line.Trim(), line.Trim());
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private async Task HandleGrokLineAsync(string line, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.TryGetProperty("id", out var idElement) && !root.TryGetProperty("method", out _))
        {
            HandleJsonRpcResponse(idElement.ToString(), root, line);
            return;
        }

        var method = GetString(root, "method");
        switch (method)
        {
            case "session/update":
                HandleSessionUpdate(root, line);
                break;
            case "session/request_permission":
                await HandlePermissionRequestAsync(root, line, ct).ConfigureAwait(false);
                break;
            default:
                if (root.TryGetProperty("id", out var requestId))
                {
                    await WriteJsonRpcErrorAsync(requestId.Clone(), -32601, $"Unsupported ACP method '{method ?? "(null)"}'.", ct).ConfigureAwait(false);
                }
                else
                {
                    EmitRuntimeMessage("runtime.notice", "Grok ACP notification ignored.", method ?? line);
                }

                break;
        }
    }

    private void HandleJsonRpcResponse(string id, JsonElement root, string rawLine)
    {
        if (_pendingRequests.TryRemove(id, out var pending))
        {
            if (root.TryGetProperty("error", out var error))
            {
                pending.TrySetException(new InvalidOperationException(ReadErrorMessage(error)));
                return;
            }

            pending.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
            return;
        }

        if (_pendingPromptTurns.TryRemove(id, out var turnId))
        {
            if (root.TryGetProperty("error", out var error))
            {
                CompleteTurn(turnId, "failed", "Failed", "error", ReadErrorMessage(error), rawLine);
                return;
            }

            var stopReason = GetString(root, "result", "stopReason") ?? "end_turn";
            var state = string.Equals(stopReason, "cancelled", StringComparison.OrdinalIgnoreCase) ? "interrupted" : "completed";
            var stateLabel = state == "interrupted" ? "Interrupted" : "Completed";
            CompleteTurn(turnId, state, stateLabel, stopReason, null, rawLine);
        }
    }

    private void HandleSessionUpdate(JsonElement root, string rawLine)
    {
        var update = Traverse(root, "params", "update");
        if (update is not { ValueKind: JsonValueKind.Object } updateElement)
        {
            return;
        }

        var updateType = GetString(updateElement, "sessionUpdate");
        var turnId = _activeTurnId;
        if (string.IsNullOrWhiteSpace(turnId))
        {
            return;
        }

        switch (updateType)
        {
            case "agent_message_chunk":
                EmitContentDelta(turnId, "assistant_text", ExtractContentText(updateElement), "grok.acp", updateType, root, rawLine);
                break;
            case "agent_thought_chunk":
                EmitContentDelta(turnId, "reasoning_text", ExtractContentText(updateElement), "grok.acp", updateType, root, rawLine);
                break;
            case "plan":
                EmitPlan(turnId, updateElement, root, rawLine);
                break;
            case "tool_call":
                HandleToolCall(turnId, updateElement, root, rawLine);
                break;
            case "tool_call_update":
                HandleToolCallUpdate(turnId, updateElement, root, rawLine);
                break;
            case "config_option_update":
                HandleConfigOptionUpdate(updateElement, root, rawLine);
                break;
            default:
                if (!string.IsNullOrWhiteSpace(updateType))
                {
                    EmitRuntimeMessage("runtime.notice", $"Grok ACP update ignored: {updateType}", rawLine);
                }

                break;
        }
    }

    private async Task HandlePermissionRequestAsync(JsonElement root, string rawLine, CancellationToken ct)
    {
        if (!root.TryGetProperty("id", out var idElement))
        {
            return;
        }

        var turnId = _activeTurnId;
        if (string.IsNullOrWhiteSpace(turnId))
        {
            await WritePermissionResponseAsync(idElement.Clone(), null, cancelled: true, ct).ConfigureAwait(false);
            return;
        }

        var requestId = idElement.ToString();
        var toolCall = Traverse(root, "params", "toolCall");
        var options = ReadPermissionOptions(root);
        var selectedOption = string.Equals(_quickSettings.PermissionMode, AppServerControlQuickSettings.PermissionModeAuto, StringComparison.Ordinal)
            ? SelectPermissionOption(options, "accept")
            : null;
        if (!string.IsNullOrWhiteSpace(selectedOption))
        {
            await WritePermissionResponseAsync(idElement.Clone(), selectedOption, cancelled: false, ct).ConfigureAwait(false);
            return;
        }

        var title = toolCall is { ValueKind: JsonValueKind.Object } toolElement
            ? GetString(toolElement, "title") ?? GetString(toolElement, "toolCallId") ?? "Grok tool call"
            : "Grok tool call";
        var detail = toolCall is { ValueKind: JsonValueKind.Object } detailElement
            ? detailElement.GetRawText()
            : rawLine;
        _pendingPermissions[requestId] = new PendingPermissionRequest(idElement.Clone(), turnId, options);
        _emit(CreateEvent("request.opened", turnId, null, requestId, "grok.acp", "session/request_permission", root, appServerControlEvent =>
        {
            appServerControlEvent.RequestOpened = new AppServerControlProviderRequestOpenedPayload
            {
                RequestType = "command_execution_approval",
                RequestTypeLabel = "Approval",
                Detail = title + Environment.NewLine + detail
            };
        }, rawLine));
    }

    private void HandleToolCall(string turnId, JsonElement update, JsonElement root, string rawLine)
    {
        var toolCallId = GetString(update, "toolCallId") ?? "grok-tool-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var title = GetString(update, "title") ?? toolCallId;
        var kind = GetString(update, "kind");
        var status = NormalizeToolStatus(GetString(update, "status"));
        var detail = BuildToolDetail(update);
        _tools[toolCallId] = new GrokToolState
        {
            ItemId = toolCallId,
            ItemType = NormalizeToolItemType(kind, title),
            Title = title,
            Detail = new StringBuilder(detail)
        };

        _emit(CreateEvent(status == "completed" ? "item.completed" : "item.started", turnId, toolCallId, null, "grok.acp", "tool_call", root, appServerControlEvent =>
        {
            appServerControlEvent.Item = new AppServerControlProviderItemPayload
            {
                ItemType = _tools[toolCallId].ItemType,
                Status = status,
                Title = title,
                Detail = detail
            };
        }, rawLine));
    }

    private void HandleToolCallUpdate(string turnId, JsonElement update, JsonElement root, string rawLine)
    {
        var toolCallId = GetString(update, "toolCallId") ?? "grok-tool-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        if (!_tools.TryGetValue(toolCallId, out var state))
        {
            state = new GrokToolState
            {
                ItemId = toolCallId,
                ItemType = NormalizeToolItemType(GetString(update, "kind"), GetString(update, "title")),
                Title = GetString(update, "title") ?? toolCallId
            };
            _tools[toolCallId] = state;
        }

        var detail = BuildToolDetail(update);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            if (state.Detail.Length > 0)
            {
                state.Detail.AppendLine();
            }

            state.Detail.Append(detail);
        }

        var status = NormalizeToolStatus(GetString(update, "status"));
        var eventType = status is "completed" or "failed" or "cancelled" ? "item.completed" : "item.started";
        _emit(CreateEvent(eventType, turnId, state.ItemId, null, "grok.acp", "tool_call_update", root, appServerControlEvent =>
        {
            appServerControlEvent.Item = new AppServerControlProviderItemPayload
            {
                ItemType = state.ItemType,
                Status = status,
                Title = state.Title,
                Detail = state.Detail.ToString()
            };
        }, rawLine));

        var diff = ExtractDiff(update);
        if (!string.IsNullOrWhiteSpace(diff))
        {
            _emit(CreateEvent("diff.updated", turnId, state.ItemId, null, "grok.acp", "tool_call_update", root, appServerControlEvent =>
            {
                appServerControlEvent.DiffUpdated = new AppServerControlProviderDiffUpdatedPayload
                {
                    UnifiedDiff = diff
                };
            }, rawLine));
        }
    }

    private void HandleConfigOptionUpdate(JsonElement update, JsonElement root, string rawLine)
    {
        var updated = ApplyConfigOptions(_quickSettings, Traverse(update, "configOptions"));
        if (updated is null)
        {
            return;
        }

        _quickSettings = updated;
        _emit(CreateQuickSettingsUpdatedEvent(_quickSettings, "grok.acp", "config_option_update", root, rawLine));
    }

    private void EmitContentDelta(
        string turnId,
        string streamKind,
        string delta,
        string source,
        string method,
        JsonElement root,
        string rawLine)
    {
        if (string.IsNullOrWhiteSpace(delta))
        {
            return;
        }

        _emit(CreateEvent("content.delta", turnId, null, null, source, method, root, appServerControlEvent =>
        {
            appServerControlEvent.ContentDelta = new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = streamKind,
                Delta = delta
            };
        }, rawLine));
    }

    private void EmitPlan(string turnId, JsonElement update, JsonElement root, string rawLine)
    {
        var plan = BuildPlanMarkdown(update);
        if (string.IsNullOrWhiteSpace(plan))
        {
            return;
        }

        _emit(CreateEvent("plan.completed", turnId, null, null, "grok.acp", "plan", root, appServerControlEvent =>
        {
            appServerControlEvent.PlanCompleted = new AppServerControlProviderPlanCompletedPayload
            {
                PlanMarkdown = plan
            };
        }, rawLine));
    }

    private void CompleteTurn(string turnId, string state, string stateLabel, string? stopReason, string? errorMessage, string rawLine)
    {
        _emit(CreateEvent("turn.completed", turnId, null, null, "grok.acp", "session/prompt", null, appServerControlEvent =>
        {
            appServerControlEvent.TurnCompleted = new AppServerControlProviderTurnCompletedPayload
            {
                State = state,
                StateLabel = stateLabel,
                StopReason = stopReason,
                ErrorMessage = errorMessage
            };
        }, rawLine));
        _emit(CreateEvent("session.state.changed", turnId, null, null, "grok.acp", "session/prompt", null, appServerControlEvent =>
        {
            appServerControlEvent.SessionState = new AppServerControlProviderSessionStatePayload
            {
                State = "ready",
                StateLabel = "Ready",
                Reason = state == "failed" ? "Grok turn failed." : "Grok turn completed."
            };
        }, rawLine));

        if (string.Equals(_activeTurnId, turnId, StringComparison.Ordinal))
        {
            ResetTurnState();
        }
    }

    private async Task<JsonElement> SendRequestAsync(string method, Action<Utf8JsonWriter> writeParameters, TimeSpan timeout, CancellationToken ct)
    {
        var requestId = NextRequestId();
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[requestId] = pending;
        try
        {
            await WriteJsonRpcRequestAsync(requestId, method, writeParameters, ct).ConfigureAwait(false);
            return await pending.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    private async Task WriteJsonRpcRequestAsync(string id, string method, Action<Utf8JsonWriter> writeParameters, CancellationToken ct)
    {
        await WriteJsonLineAsync(
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("jsonrpc", "2.0");
                    writer.WriteString("id", id);
                    writer.WriteString("method", method);
                    writer.WritePropertyName("params");
                    writeParameters(writer);
                    writer.WriteEndObject();
                },
                ct)
            .ConfigureAwait(false);
    }

    private async Task WriteJsonRpcNotificationAsync(string method, Action<Utf8JsonWriter> writeParameters, CancellationToken ct)
    {
        await WriteJsonLineAsync(
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("jsonrpc", "2.0");
                    writer.WriteString("method", method);
                    writer.WritePropertyName("params");
                    writeParameters(writer);
                    writer.WriteEndObject();
                },
                ct)
            .ConfigureAwait(false);
    }

    private async Task WritePermissionResponseAsync(JsonElement id, string? optionId, bool cancelled, CancellationToken ct)
    {
        var outcome = cancelled || string.IsNullOrWhiteSpace(optionId)
            ? "cancelled"
            : "selected";
        await WriteJsonLineAsync(
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("jsonrpc", "2.0");
                    writer.WritePropertyName("id");
                    id.WriteTo(writer);
                    writer.WritePropertyName("result");
                    writer.WriteStartObject();
                    writer.WritePropertyName("outcome");
                    writer.WriteStartObject();
                    writer.WriteString("outcome", outcome);
                    if (!cancelled && !string.IsNullOrWhiteSpace(optionId))
                    {
                        writer.WriteString("optionId", optionId);
                    }

                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                },
                ct)
            .ConfigureAwait(false);
    }

    private async Task WriteJsonRpcErrorAsync(JsonElement id, int code, string message, CancellationToken ct)
    {
        await WriteJsonLineAsync(
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("jsonrpc", "2.0");
                    writer.WritePropertyName("id");
                    id.WriteTo(writer);
                    writer.WritePropertyName("error");
                    writer.WriteStartObject();
                    writer.WriteNumber("code", code);
                    writer.WriteString("message", message);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                },
                ct)
            .ConfigureAwait(false);
    }

    private async Task WriteJsonLineAsync(Action<Utf8JsonWriter> writePayload, CancellationToken ct)
    {
        var input = _input ?? throw new InvalidOperationException("Grok process input stream is unavailable.");
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writePayload(writer);
            }

            await input.WriteLineAsync(Utf8NoBom.GetString(buffer.ToArray()).AsMemory(), ct).ConfigureAwait(false);
            await input.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task DisposeProcessAsync()
    {
        foreach (var pending in _pendingRequests.Values)
        {
            pending.TrySetException(new InvalidOperationException("Grok ACP runtime is shutting down."));
        }

        _pendingRequests.Clear();
        _pendingPromptTurns.Clear();
        _pendingPermissions.Clear();

        try
        {
            if (_process is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
        }

        try { _input?.Dispose(); } catch { }
        try { _output?.Dispose(); } catch { }
        try { _error?.Dispose(); } catch { }
        try { _process?.Dispose(); } catch { }
        _process = null;
        _input = null;
        _output = null;
        _error = null;

        if (_readerTask is not null)
        {
            await Task.WhenAny(_readerTask, Task.Delay(250, CancellationToken.None)).ConfigureAwait(false);
        }

        if (_errorTask is not null)
        {
            await Task.WhenAny(_errorTask, Task.Delay(250, CancellationToken.None)).ConfigureAwait(false);
        }

        _readerTask = null;
        _errorTask = null;
        ResetTurnState();
    }

    private async Task FinalizeExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }

        if (!_shutdown.IsCancellationRequested && !string.IsNullOrWhiteSpace(_activeTurnId) && process.ExitCode != 0)
        {
            var turnId = _activeTurnId;
            _emit(CreateEvent("turn.completed", turnId, null, null, "grok.acp", "process.exit", new { process.ExitCode }, appServerControlEvent =>
            {
                appServerControlEvent.TurnCompleted = new AppServerControlProviderTurnCompletedPayload
                {
                    State = "failed",
                    StateLabel = "Failed",
                    StopReason = "process_exit",
                    ErrorMessage = $"Grok exited with code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}."
                };
            }));
            EmitRuntimeMessage("runtime.error", "Grok ACP process exited unexpectedly.", $"Exit code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}.");
            ResetTurnState();
        }
    }

    private void EnsureAttached()
    {
        if (string.IsNullOrWhiteSpace(_sessionId) ||
            string.IsNullOrWhiteSpace(_workingDirectory) ||
            string.IsNullOrWhiteSpace(_binaryPath) ||
            string.IsNullOrWhiteSpace(_providerThreadId) ||
            _process is null ||
            _process.HasExited)
        {
            throw new InvalidOperationException("Grok ACP runtime is not attached.");
        }
    }

    private string NextRequestId()
    {
        return "midterm-" + Interlocked.Increment(ref _nextRequestId).ToString(CultureInfo.InvariantCulture);
    }

    private AppServerControlQuickSettingsSummary CreateDefaultQuickSettings()
    {
        var defaultPermissionMode = AppServerControlProviderRuntimeConfiguration.GetGrokAlwaysApproveDefault()
            ? AppServerControlQuickSettings.PermissionModeAuto
            : AppServerControlQuickSettings.PermissionModeManual;
        return AppServerControlQuickSettings.CreateSummary(
            AppServerControlProviderRuntimeConfiguration.GetGrokDefaultModel(),
            null,
            AppServerControlQuickSettings.PlanModeOff,
            defaultPermissionMode,
            defaultPermissionMode);
    }

    private AppServerControlQuickSettingsSummary ResolveRequestedQuickSettings(AppServerControlTurnRequest request)
    {
        return AppServerControlQuickSettings.CreateSummary(
            request.Model ?? _quickSettings.Model,
            request.Effort ?? _quickSettings.Effort,
            request.PlanMode ?? _quickSettings.PlanMode,
            request.PermissionMode ?? _quickSettings.PermissionMode,
            _quickSettings.PermissionMode);
    }

    private AppServerControlQuickSettingsSummary MergeRequestedQuickSettings(AppServerControlQuickSettingsSummary requested)
    {
        return new AppServerControlQuickSettingsSummary
        {
            Model = _quickSettings.Model ?? requested.Model,
            Effort = requested.Effort,
            PlanMode = requested.PlanMode,
            PermissionMode = requested.PermissionMode,
            ModelOptions = AppServerControlQuickSettings.CloneOptions(_quickSettings.ModelOptions),
            EffortOptions = AppServerControlQuickSettings.CloneOptions(_quickSettings.EffortOptions)
        };
    }

    private AppServerControlQuickSettingsSummary CreateQuickSettingsFromState(JsonElement initializeResult, JsonElement sessionResult)
    {
        var modelState = Traverse(sessionResult, "models") ?? Traverse(initializeResult, "_meta", "modelState");
        var model = GetString(modelState, "currentModelId")
                    ?? _quickSettings.Model
                    ?? AppServerControlProviderRuntimeConfiguration.GetGrokDefaultModel();
        var summary = AppServerControlQuickSettings.CreateSummary(
            model,
            null,
            AppServerControlQuickSettings.PlanModeOff,
            _quickSettings.PermissionMode,
            _quickSettings.PermissionMode);
        summary.ModelOptions = ReadModelOptions(modelState, model);
        summary.EffortOptions =
        [
            new AppServerControlQuickSettingsOption { Value = "low", Label = "Low" },
            new AppServerControlQuickSettingsOption { Value = "medium", Label = "Medium" },
            new AppServerControlQuickSettingsOption { Value = "high", Label = "High" },
            new AppServerControlQuickSettingsOption { Value = "xhigh", Label = "Extra high" },
            new AppServerControlQuickSettingsOption { Value = "max", Label = "Max" }
        ];
        return summary;
    }

    private static AppServerControlQuickSettingsSummary? ApplyConfigOptions(AppServerControlQuickSettingsSummary current, JsonElement? configOptions)
    {
        if (configOptions is not { ValueKind: JsonValueKind.Array } options)
        {
            return null;
        }

        var next = new AppServerControlQuickSettingsSummary
        {
            Model = current.Model,
            Effort = current.Effort,
            PlanMode = current.PlanMode,
            PermissionMode = current.PermissionMode,
            ModelOptions = AppServerControlQuickSettings.CloneOptions(current.ModelOptions),
            EffortOptions = AppServerControlQuickSettings.CloneOptions(current.EffortOptions)
        };

        using var optionEnumerator = options.EnumerateArray();
        while (optionEnumerator.MoveNext())
        {
            var option = optionEnumerator.Current;
            var category = GetString(option, "category");
            var id = GetString(option, "id");
            var currentValue = GetString(option, "currentValue");
            if (string.Equals(category, "model", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(id, "model", StringComparison.OrdinalIgnoreCase))
            {
                next.Model = currentValue;
                next.ModelOptions = ReadConfigValueOptions(option);
            }
            else if (string.Equals(category, "thought_level", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(id, "effort", StringComparison.OrdinalIgnoreCase))
            {
                next.Effort = currentValue;
                next.EffortOptions = ReadConfigValueOptions(option);
            }
        }

        return next;
    }

    private static List<AppServerControlQuickSettingsOption> ReadModelOptions(JsonElement? modelState, string? currentModel)
    {
        var availableModels = Traverse(modelState, "availableModels");
        if (availableModels is not { ValueKind: JsonValueKind.Array } models)
        {
            return string.IsNullOrWhiteSpace(currentModel)
                ? []
                : [new AppServerControlQuickSettingsOption { Value = currentModel, Label = currentModel, IsDefault = true }];
        }

        var result = new List<AppServerControlQuickSettingsOption>();
        using var modelEnumerator = models.EnumerateArray();
        while (modelEnumerator.MoveNext())
        {
            var model = modelEnumerator.Current;
            var value = GetString(model, "modelId") ?? GetString(model, "id");
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            result.Add(new AppServerControlQuickSettingsOption
            {
                Value = value,
                Label = GetString(model, "name") ?? value,
                IsDefault = string.Equals(value, currentModel, StringComparison.Ordinal)
            });
        }

        return result;
    }

    private static List<AppServerControlQuickSettingsOption> ReadConfigValueOptions(JsonElement option)
    {
        if (!option.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<AppServerControlQuickSettingsOption>();
        using var itemEnumerator = options.EnumerateArray();
        while (itemEnumerator.MoveNext())
        {
            var item = itemEnumerator.Current;
            var value = GetString(item, "value");
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            result.Add(new AppServerControlQuickSettingsOption
            {
                Value = value,
                Label = GetString(item, "name") ?? value,
                Description = GetString(item, "description"),
                IsDefault = string.Equals(value, GetString(option, "currentValue"), StringComparison.Ordinal)
            });
        }

        return result;
    }

    private static List<GrokPromptBlock> BuildPromptBlocks(AppServerControlTurnRequest request, string? planMode)
    {
        var blocks = new List<GrokPromptBlock>();
        var prompt = AppServerControlQuickSettings.ApplyPlanModePrompt(request.Text, planMode);
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            blocks.Add(new GrokPromptBlock("text", prompt));
        }

        if (request.Attachments.Count == 0)
        {
            return blocks;
        }

        var builder = new StringBuilder();
        builder.AppendLine(request.Attachments.Count == 1 ? "Attached resource:" : $"Attached resources ({request.Attachments.Count.ToString(CultureInfo.InvariantCulture)}):");
        foreach (var attachment in request.Attachments)
        {
            if (string.IsNullOrWhiteSpace(attachment.Path))
            {
                continue;
            }

            if (!File.Exists(attachment.Path))
            {
                throw new InvalidOperationException($"App Server Controller attachment does not exist: {attachment.Path}");
            }

            builder.Append("- ");
            builder.Append(string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase) ? "[image] " : "[file] ");
            builder.Append(attachment.Path);
            if (!string.IsNullOrWhiteSpace(attachment.MimeType))
            {
                builder.Append(" (");
                builder.Append(attachment.MimeType);
                builder.Append(')');
            }

            builder.AppendLine();
        }

        blocks.Add(new GrokPromptBlock("text", builder.ToString().Trim()));
        return blocks;
    }

    private static void WritePromptBlocks(Utf8JsonWriter writer, IReadOnlyList<GrokPromptBlock> promptBlocks)
    {
        writer.WriteStartArray();
        foreach (var block in promptBlocks)
        {
            writer.WriteStartObject();
            writer.WriteString("type", block.Type);
            writer.WriteString("text", block.Text);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static List<PermissionOption> ReadPermissionOptions(JsonElement root)
    {
        var options = Traverse(root, "params", "options");
        if (options is not { ValueKind: JsonValueKind.Array } array)
        {
            return [];
        }

        var result = new List<PermissionOption>();
        using var optionEnumerator = array.EnumerateArray();
        while (optionEnumerator.MoveNext())
        {
            var option = optionEnumerator.Current;
            var optionId = GetString(option, "optionId");
            if (string.IsNullOrWhiteSpace(optionId))
            {
                continue;
            }

            result.Add(new PermissionOption(optionId, GetString(option, "name"), GetString(option, "kind")));
        }

        return result;
    }

    private static string? SelectPermissionOption(IReadOnlyList<PermissionOption> options, string? decision)
    {
        var wantsAccept = string.Equals(decision, "accept", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(decision, "approve", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(decision, "allow", StringComparison.OrdinalIgnoreCase);
        var preferredKind = wantsAccept ? "allow" : "reject";
        var selected = options.FirstOrDefault(option => option.Kind?.StartsWith(preferredKind, StringComparison.OrdinalIgnoreCase) == true);
        if (!string.IsNullOrWhiteSpace(selected.OptionId))
        {
            return selected.OptionId;
        }

        selected = options.FirstOrDefault(option => option.Name?.Contains(wantsAccept ? "allow" : "reject", StringComparison.OrdinalIgnoreCase) == true);
        return string.IsNullOrWhiteSpace(selected.OptionId) ? null : selected.OptionId;
    }

    private AppServerControlProviderEvent CreateQuickSettingsUpdatedEvent(
        AppServerControlQuickSettingsSummary quickSettings,
        string source,
        string? method,
        object? payload,
        string? rawLine = null)
    {
        return CreateEvent("quick-settings.updated", null, null, null, source, method, payload, appServerControlEvent =>
        {
            appServerControlEvent.QuickSettingsUpdated = AppServerControlQuickSettings.ToPayload(quickSettings);
        }, rawLine);
    }

    private AppServerControlProviderEvent CreateEvent(
        string type,
        string? turnId,
        string? itemId,
        string? requestId,
        string source,
        string? method,
        object? payload,
        Action<AppServerControlProviderEvent> configure,
        string? rawLine = null)
    {
        var appServerControlEvent = new AppServerControlProviderEvent
        {
            Sequence = Interlocked.Increment(ref _sequence),
            EventId = $"evt-grok-{Guid.NewGuid():N}",
            SessionId = _sessionId ?? string.Empty,
            Provider = Provider,
            ThreadId = _providerThreadId ?? _sessionId ?? string.Empty,
            TurnId = turnId,
            ItemId = itemId,
            RequestId = requestId,
            CreatedAt = DateTimeOffset.UtcNow,
            Type = type,
            Raw = new AppServerControlProviderEventRaw
            {
                Source = source,
                Method = method,
                PayloadJson = rawLine ?? SerializePayload(payload)
            }
        };
        configure(appServerControlEvent);
        return appServerControlEvent;
    }

    private static HostCommandOutcome Accepted(
        string commandId,
        string sessionId,
        AppServerControlCommandAcceptedResponse? accepted = null,
        IReadOnlyList<AppServerControlProviderEvent>? events = null)
    {
        return new HostCommandOutcome
        {
            Result = new AppServerControlHostCommandResultEnvelope
            {
                CommandId = commandId,
                SessionId = sessionId,
                Status = "accepted",
                Accepted = accepted
            },
            Events = events ?? []
        };
    }

    private void EmitRuntimeMessage(string type, string message, string? detail)
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
        {
            return;
        }

        _emit(CreateEvent(type, _activeTurnId, null, null, "grok.acp", type, new { message, detail }, appServerControlEvent =>
        {
            appServerControlEvent.RuntimeMessage = new AppServerControlProviderRuntimeMessagePayload
            {
                Message = message,
                Detail = detail
            };
        }));
    }

    private static string BuildArguments(string permissionMode, string? model)
    {
        var args = new List<string>
        {
            "agent"
        };

        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("-m");
            args.Add(model.Trim());
        }

        if (string.Equals(permissionMode, AppServerControlQuickSettings.PermissionModeAuto, StringComparison.Ordinal))
        {
            args.Add("--always-approve");
        }

        args.Add("stdio");

        return string.Join(" ", args.Select(QuoteArgument));
    }

    private static ProcessStartInfo CreateProcessStartInfo(string binaryPath, string arguments, string workingDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            var extension = Path.GetExtension(binaryPath);
            if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                var comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                return new ProcessStartInfo
                {
                    FileName = comspec,
                    Arguments = $"/d /c \"\"{binaryPath}\" {arguments}\"",
                    WorkingDirectory = workingDirectory,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Utf8NoBom,
                    StandardErrorEncoding = Utf8NoBom,
                    StandardInputEncoding = Utf8NoBom
                };
            }
        }

        return new ProcessStartInfo
        {
            FileName = binaryPath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            StandardInputEncoding = Utf8NoBom
        };
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        return value.Any(static ch => char.IsWhiteSpace(ch) || ch is '"' or '\\')
            ? "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : value;
    }

    private static string? FindExecutableInPath(string commandName, string? userProfileDirectory)
    {
        if (Path.IsPathRooted(commandName) && File.Exists(commandName))
        {
            return commandName;
        }

        var candidateNames = OperatingSystem.IsWindows() ? GetWindowsExecutableNames(commandName) : [commandName];
        foreach (var directory in EnumerateSearchDirectories(userProfileDirectory))
        {
            foreach (var candidateName in candidateNames)
            {
                var fullPath = Path.Combine(directory, candidateName);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSearchDirectories(string? userProfileDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(userProfileDirectory))
        {
            foreach (var directory in new[]
                     {
                         Path.Combine(userProfileDirectory, ".grok", "bin"),
                         Path.Combine(userProfileDirectory, ".local", "bin"),
                         Path.Combine(userProfileDirectory, "AppData", "Roaming", "npm"),
                         Path.Combine(userProfileDirectory, "AppData", "Local", "Programs", "nodejs")
                     })
            {
                if (Directory.Exists(directory) && seen.Add(directory))
                {
                    yield return directory;
                }
            }
        }

        var currentProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(currentProfile))
        {
            var grokBin = Path.Combine(currentProfile, ".grok", "bin");
            if (Directory.Exists(grokBin) && seen.Add(grokBin))
            {
                yield return grokBin;
            }
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathVar))
        {
            foreach (var rawDirectory in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var directory = rawDirectory.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(directory) && seen.Add(directory))
                {
                    yield return directory;
                }
            }
        }
    }

    private static string[] GetWindowsExecutableNames(string commandName)
    {
        if (!string.IsNullOrWhiteSpace(Path.GetExtension(commandName)))
        {
            return [commandName];
        }

        var pathext = Environment.GetEnvironmentVariable("PATHEXT");
        var extensions = string.IsNullOrWhiteSpace(pathext)
            ? [".exe", ".cmd", ".bat"]
            : pathext.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return extensions.Select(ext => commandName + ext.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ExtractContentText(JsonElement update)
    {
        var content = Traverse(update, "content");
        return ExtractText(content);
    }

    private static string ExtractText(JsonElement? element)
    {
        if (element is null)
        {
            return string.Empty;
        }

        var value = element.Value;
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString() ?? string.Empty;
            case JsonValueKind.Object when string.Equals(GetString(value, "type"), "text", StringComparison.OrdinalIgnoreCase):
                return GetString(value, "text") ?? string.Empty;
            case JsonValueKind.Object when value.TryGetProperty("content", out var content):
                return ExtractText(content);
            case JsonValueKind.Array:
                var parts = new List<string>();
                using (var itemEnumerator = value.EnumerateArray())
                {
                    while (itemEnumerator.MoveNext())
                    {
                        var text = ExtractText(itemEnumerator.Current);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            parts.Add(text);
                        }
                    }
                }

                return string.Join(Environment.NewLine, parts);
            default:
                return string.Empty;
        }
    }

    private static string BuildToolDetail(JsonElement update)
    {
        var parts = new List<string>();
        var title = GetString(update, "title");
        if (!string.IsNullOrWhiteSpace(title))
        {
            parts.Add(title);
        }

        var rawInput = Traverse(update, "rawInput");
        if (rawInput is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null })
        {
            parts.Add(rawInput.Value.GetRawText());
        }

        var contentText = ExtractText(Traverse(update, "content"));
        if (!string.IsNullOrWhiteSpace(contentText))
        {
            parts.Add(contentText);
        }

        return string.Join(Environment.NewLine, parts.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string? ExtractDiff(JsonElement update)
    {
        var content = Traverse(update, "content");
        if (content is not { ValueKind: JsonValueKind.Array } array)
        {
            return null;
        }

        var builder = new StringBuilder();
        using var itemEnumerator = array.EnumerateArray();
        while (itemEnumerator.MoveNext())
        {
            var item = itemEnumerator.Current;
            if (!string.Equals(GetString(item, "type"), "diff", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = GetString(item, "path") ?? "file";
            var oldText = GetString(item, "oldText") ?? string.Empty;
            var newText = GetString(item, "newText") ?? string.Empty;
            builder.AppendLine($"--- a/{path}");
            builder.AppendLine($"+++ b/{path}");
            builder.AppendLine("@@");
            foreach (var line in oldText.Split('\n'))
            {
                builder.Append('-');
                builder.AppendLine(line.TrimEnd('\r'));
            }

            foreach (var line in newText.Split('\n'))
            {
                builder.Append('+');
                builder.AppendLine(line.TrimEnd('\r'));
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string BuildPlanMarkdown(JsonElement update)
    {
        if (!update.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var lines = new List<string>();
        using var entryEnumerator = entries.EnumerateArray();
        while (entryEnumerator.MoveNext())
        {
            var entry = entryEnumerator.Current;
            var content = GetString(entry, "content");
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var status = GetString(entry, "status");
            lines.Add(string.IsNullOrWhiteSpace(status) ? $"- {content}" : $"- [{status}] {content}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string NormalizeToolStatus(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "completed" => "completed",
            "failed" => "failed",
            "cancelled" => "cancelled",
            "canceled" => "cancelled",
            "in_progress" => "in_progress",
            "running" => "in_progress",
            _ => "in_progress"
        };
    }

    private static string NormalizeToolItemType(string? kind, string? title)
    {
        if (string.Equals(kind, "execute", StringComparison.OrdinalIgnoreCase) ||
            title?.Contains("command", StringComparison.OrdinalIgnoreCase) == true ||
            title?.Contains("bash", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "command_execution";
        }

        if (string.Equals(kind, "edit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "delete", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "move", StringComparison.OrdinalIgnoreCase))
        {
            return "file_change";
        }

        return "dynamic_tool_call";
    }

    private static string ReadErrorMessage(JsonElement error)
    {
        return GetString(error, "message") ?? error.GetRawText();
    }

    private static string? SerializePayload(object? payload)
    {
        return payload switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.Undefined } => null,
            JsonElement element => element.GetRawText(),
            string text => text,
            _ => null
        };
    }

    private void ResetTurnState()
    {
        _activeTurnId = null;
        _activeTurnModel = null;
        _activeTurnEffort = null;
        _tools.Clear();
    }

    private static string? GetString(JsonElement? element, params string[] path)
    {
        if (element is null)
        {
            return null;
        }

        return GetString(element.Value, path);
    }

    private static string? GetString(JsonElement element, params string[] path)
    {
        var current = Traverse(element, path);
        return current is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    }

    private static JsonElement? Traverse(JsonElement? element, params string[] path)
    {
        if (element is null)
        {
            return null;
        }

        return Traverse(element.Value, path);
    }

    private static JsonElement? Traverse(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current;
    }

    private readonly record struct GrokPromptBlock(string Type, string Text);

    private readonly record struct PermissionOption(string OptionId, string? Name, string? Kind);

    private readonly record struct PendingPermissionRequest(JsonElement RpcId, string TurnId, IReadOnlyList<PermissionOption> Options);

    private sealed class GrokToolState
    {
        public string ItemId { get; init; } = string.Empty;
        public string ItemType { get; init; } = "dynamic_tool_call";
        public string Title { get; init; } = string.Empty;
        public StringBuilder Detail { get; init; } = new();
    }
}
